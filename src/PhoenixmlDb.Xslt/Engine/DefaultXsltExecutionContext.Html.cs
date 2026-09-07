using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xdm.Serialization;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
// XPath 4.0 ordered map: insertion-order iteration as a structural guarantee.
// xslt keeps its existing default key-equality (pass EqualityComparer<object>.Default
// at each construction site) — this change is about iteration order only.
using OrderedXdmMap = PhoenixmlDb.XQuery.Execution.OrderedXdmMap;

namespace PhoenixmlDb.Xslt.Engine;

internal sealed partial class DefaultXsltExecutionContext
{

    internal static string PostProcessHtmlOutput(string output, OutputMethod method = OutputMethod.Html)
    {
        // Convert <br/> → <br> (HTML) or <br /> (XHTML) for void elements,
        // <div/> → <div></div> for non-void.
        var isXhtml = method == OutputMethod.Xhtml;
        var sb = new StringBuilder(output.Length);
        var i = 0;
        while (i < output.Length)
        {
            if (output[i] == '<' && i + 1 < output.Length && output[i + 1] != '/' && output[i + 1] != '!' && output[i + 1] != '?')
            {
                // Opening tag - find tag name and check for self-closing
                var tagStart = i;
                i++; // skip '<'
                var nameStart = i;
                while (i < output.Length && output[i] != ' ' && output[i] != '>' && output[i] != '/' && output[i] != '\t' && output[i] != '\n' && output[i] != '\r')
                    i++;
                var tagName = output[nameStart..i];

                // Find end of tag
                while (i < output.Length && output[i] != '>')
                    i++;

                if (i > 0 && output[i - 1] == '/')
                {
                    // Self-closing tag: <tag ... />
                    if (HtmlVoidElements.Contains(tagName))
                    {
                        // Void element. HTML: <br/> → <br>. XHTML: <br/> → <br /> (well-formed
                        // empty element with a space before the self-closing slash).
                        var upToSlash = output.AsSpan(tagStart, i - 1 - tagStart); // everything up to /
                        if (isXhtml)
                        {
                            sb.Append(upToSlash.TrimEnd());
                            sb.Append(" />");
                        }
                        else
                        {
                            sb.Append(upToSlash);
                            sb.Append('>');
                        }
                    }
                    else
                    {
                        // Non-void: expand <tag/> → <tag></tag>
                        sb.Append(output.AsSpan(tagStart, i - 1 - tagStart)); // everything up to /
                        sb.Append("></");
                        sb.Append(tagName);
                        sb.Append('>');
                    }
                    if (i < output.Length)
                        i++; // skip >
                }
                else
                {
                    // Normal opening tag
                    sb.Append(output.AsSpan(tagStart, i + 1 - tagStart));
                    if (i < output.Length)
                        i++;
                }
            }
            else
            {
                sb.Append(output[i]);
                i++;
            }
        }
        return sb.ToString();
    }


    /// <summary>
    /// Reverses XML escaping inside the content of HTML raw-text (CDATA) elements — <c>script</c>
    /// and <c>style</c> — for the HTML output method. Per the XSLT/XQuery Serialization 3.0 HTML
    /// output method, the content of these elements is emitted verbatim: <c>&lt;</c>, <c>&gt;</c>
    /// and <c>&amp;</c> are NOT escaped (output-0154/0159). The rest of the tree keeps normal XML
    /// escaping. Element-name matching is case-insensitive; the start tag must be a real element
    /// tag (not a comment/PI/declaration). Content ends at the matching case-insensitive
    /// <c>&lt;/script&gt;</c> / <c>&lt;/style&gt;</c> end tag. Only the XML metacharacter entities
    /// (<c>&amp;lt;</c>, <c>&amp;gt;</c>, <c>&amp;amp;</c>, <c>&amp;quot;</c>, <c>&amp;apos;</c>)
    /// are reversed — numeric character references are left untouched.
    /// </summary>
    internal static string UnescapeHtmlRawTextElements(string output)
    {
        // Fast path: nothing to do when neither raw-text element is present.
        if (output.IndexOf("script", StringComparison.OrdinalIgnoreCase) < 0
            && output.IndexOf("style", StringComparison.OrdinalIgnoreCase) < 0)
            return output;

        StringBuilder? sb = null;
        var i = 0;
        var appendedFrom = 0;
        while (i < output.Length)
        {
            // Look for a start tag "<script" or "<style" whose name is delimited by a tag
            // boundary character (so "<scripting" or "<styles" do not match).
            if (output[i] != '<' || i + 1 >= output.Length
                || output[i + 1] == '/' || output[i + 1] == '!' || output[i + 1] == '?')
            {
                i++;
                continue;
            }
            var name = MatchRawTextElementName(output, i + 1);
            if (name == null)
            {
                i++;
                continue;
            }
            // Find end of the start tag.
            var tagEnd = output.IndexOf('>', i + 1);
            if (tagEnd < 0)
                break;
            // A self-closing start tag (<script .../>) has no text content to unescape.
            if (output[tagEnd - 1] == '/')
            {
                i = tagEnd + 1;
                continue;
            }
            var contentStart = tagEnd + 1;
            // Locate the matching end tag "</name" (case-insensitive).
            var endTag = "</" + name;
            var endIdx = output.IndexOf(endTag, contentStart, StringComparison.OrdinalIgnoreCase);
            if (endIdx < 0)
            {
                i = contentStart;
                continue;
            }
            // Emit everything up to the content verbatim, then the unescaped content.
            sb ??= new StringBuilder(output.Length);
            sb.Append(output, appendedFrom, contentStart - appendedFrom);
            sb.Append(UnescapeXmlMetacharacters(output.AsSpan(contentStart, endIdx - contentStart)));
            appendedFrom = endIdx;
            i = endIdx;
        }
        if (sb == null)
            return output;
        sb.Append(output, appendedFrom, output.Length - appendedFrom);
        return sb.ToString();
    }


    /// <summary>
    /// Reverses the five XML metacharacter entity references (<c>&amp;lt;</c>, <c>&amp;gt;</c>,
    /// <c>&amp;amp;</c>, <c>&amp;quot;</c>, <c>&amp;apos;</c>) to their literal characters.
    /// Numeric and other character references are left unchanged.
    /// </summary>
    private static string UnescapeXmlMetacharacters(ReadOnlySpan<char> content)
    {
        if (content.IndexOf('&') < 0)
            return content.ToString();
        var sb = new StringBuilder(content.Length);
        var i = 0;
        while (i < content.Length)
        {
            if (content[i] == '&')
            {
                var rest = content[i..];
                if (rest.StartsWith("&lt;")) { sb.Append('<'); i += 4; continue; }
                if (rest.StartsWith("&gt;")) { sb.Append('>'); i += 4; continue; }
                if (rest.StartsWith("&amp;")) { sb.Append('&'); i += 5; continue; }
                if (rest.StartsWith("&quot;")) { sb.Append('"'); i += 6; continue; }
                if (rest.StartsWith("&apos;")) { sb.Append('\''); i += 6; continue; }
            }
            sb.Append(content[i]);
            i++;
        }
        return sb.ToString();
    }



    /// <summary>
    /// Strips all XML markup from output, returning only text content.
    /// Used for xsl:output method="text".
    /// </summary>
    internal static string StripXmlMarkup(string output)
    {
        var sb = new StringBuilder(output.Length);
        var i = 0;
        while (i < output.Length)
        {
            if (output[i] == '<')
            {
                // Skip entire tag (including CDATA, comments, PIs)
                var end = output.IndexOf('>', i);
                if (end >= 0)
                {
                    i = end + 1;
                }
                else
                {
                    i++;
                }
            }
            else if (output[i] == '&')
            {
                // Decode entity references
                var semi = output.IndexOf(';', i);
                if (semi >= 0 && semi - i < 10)
                {
                    var entity = output.AsSpan(i + 1, semi - i - 1);
                    if (entity.SequenceEqual("amp"))
                        sb.Append('&');
                    else if (entity.SequenceEqual("lt"))
                        sb.Append('<');
                    else if (entity.SequenceEqual("gt"))
                        sb.Append('>');
                    else if (entity.SequenceEqual("quot"))
                        sb.Append('"');
                    else if (entity.SequenceEqual("apos"))
                        sb.Append('\'');
                    else if (entity.Length > 1 && entity[0] == '#')
                    {
                        // Numeric character reference
                        var numStr = entity[1] == 'x'
                            ? entity[2..].ToString()
                            : entity[1..].ToString();
                        var style = entity[1] == 'x' ? NumberStyles.HexNumber : NumberStyles.Integer;
                        if (int.TryParse(numStr, style, CultureInfo.InvariantCulture, out var cp))
                            sb.Append(char.ConvertFromUtf32(cp));
                    }
                    else
                    {
                        // Unknown entity, pass through
                        sb.Append(output, i, semi - i + 1);
                    }
                    i = semi + 1;
                }
                else
                {
                    sb.Append(output[i]);
                    i++;
                }
            }
            else
            {
                sb.Append(output[i]);
                i++;
            }
        }
        return sb.ToString();
    }

}
