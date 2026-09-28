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

/// <summary>
/// Shared URI resolution and file reading for unparsed-text functions.
/// </summary>
internal static class UnparsedTextHelper
{
    internal static string? ResolveFilePath(string href, Uri? baseUri)
    {
        // An EXISTING file or null — for an absolute URI too. Returning the path unchecked made
        // unparsed-text-available answer true for any absolute file URI.
        if (Uri.TryCreate(href, UriKind.Absolute, out var absUri))
            return absUri.IsFile && System.IO.File.Exists(absUri.LocalPath) ? absUri.LocalPath : null;

        if (baseUri != null)
        {
            // A relative URI resolves against the base and nothing else. It used to fall back to
            // the process's CURRENT DIRECTORY when not found there, which could quietly read an
            // unrelated file of the same name.
            var resolved = new Uri(baseUri, href);
            return resolved.IsFile && System.IO.File.Exists(resolved.LocalPath) ? resolved.LocalPath : null;
        }

        // No base URI at all (a stylesheet loaded from a string): the current directory is the
        // only reference point there is.
        return System.IO.File.Exists(href) ? href : null;
    }

    /// <summary>
    /// The base a relative URI resolves against: the static base URI of the MODULE making the
    /// call — what static-base-uri() reports — not the principal stylesheet's.
    /// </summary>
    /// <remarks>
    /// All six unparsed-text functions used the principal stylesheet's base, so
    /// unparsed-text('VERSION') in an included module read the VERSION file next to the MAIN
    /// stylesheet (xslt#195: XSpec 4.1 moved its version lookup into src/common/ beside VERSION,
    /// and printed "XSpec v"). static-base-uri() in the same module was already right.
    /// </remarks>
    internal static Uri? StaticBase(DefaultXsltExecutionContext context)
        => context.StaticBaseUri is { } s && Uri.TryCreate(s, UriKind.Absolute, out var u)
            ? u
            : context._stylesheet.BaseUri;

    /// <summary>
    /// <paramref name="href"/> made absolute against <see cref="StaticBase"/>, so the resource
    /// policy checks and a custom resolver see the resource actually being read rather than a bare
    /// relative name.
    /// </summary>
    internal static string Absolute(string href, DefaultXsltExecutionContext context)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs))
            return abs.AbsoluteUri;
        return StaticBase(context) is { } b ? new Uri(b, href).AbsoluteUri : href;
    }

    /// <summary>
    /// FOUT1170: the resource cannot be retrieved. The reading functions returned the empty
    /// sequence instead, so a missing file became "" and hid whatever made it go missing
    /// (xslt#195: it hid the wrong base URI above).
    /// </summary>
    internal static XsltException CannotRetrieve(string href, Exception? cause = null)
        => new($"FOUT1170: Cannot retrieve the resource '{href}'" + (cause is null ? "" : $": {cause.Message}"));

    /// <summary>
    /// Checks that text does not contain characters forbidden in XML (NUL U+0000).
    /// Throws FOUT1190 if invalid characters are found.
    /// </summary>
    /// <summary>
    /// fn:unparsed-text-lines: the text split at each "\r\n", "\r" or "\n", without the empty
    /// string a final line ending would otherwise leave — as a SEQUENCE of xs:string.
    /// </summary>
    /// <remarks>
    /// Both XSLT overloads returned a <c>List&lt;object&gt;</c>, which the engine carries as an
    /// XDM array: one item. So <c>count(unparsed-text-lines($f))</c> was 1 and xsl:for-each saw a
    /// single item whose string value was every line joined by spaces (W3C
    /// unparsed-text-lines-001/002/003/005). The split also ignored a bare "\r" line ending.
    /// </remarks>
    internal static object? SplitLines(string text)
    {
        var lines = new List<object?>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n')
                continue;
            lines.Add(text[start..i]);
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                i++;
            start = i + 1;
        }
        if (start < text.Length)
            lines.Add(text[start..]);
        return lines.Count switch
        {
            0 => null,
            1 => lines[0],
            _ => lines.ToArray(),
        };
    }


    internal static void ValidateTextContent(string text)
    {
        if (text.Contains('\0', StringComparison.Ordinal))
            throw new XsltException("FOUT1190: The text resource contains a character that is not permitted in XML (NUL U+0000)");
    }

    /// <summary>
    /// Reads a file with automatic encoding detection:
    /// 1. BOM detection (UTF-8, UTF-16 LE/BE, UTF-32)
    /// 2. XML declaration encoding sniffing (for files starting with &lt;?xml)
    /// 3. UTF-8 fallback
    /// </summary>
    internal static async Task<string> ReadWithEncodingDetectionAsync(string filePath)
    {
        var bytes = await System.IO.File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
        var encoding = DetectEncoding(bytes);
        var text = encoding.GetString(bytes);
        // Strip BOM character (U+FEFF) if present at start of decoded text
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];
        return text;
    }

    private static System.Text.Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length == 0)
            return System.Text.Encoding.UTF8;

        // Check BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return System.Text.Encoding.Unicode; // UTF-16 LE
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return System.Text.Encoding.BigEndianUnicode; // UTF-16 BE

        // Try to detect XML encoding declaration: <?xml ... encoding="..."?>
        // Only check ASCII-compatible bytes for the prolog
        if (bytes.Length >= 5 && bytes[0] == '<' && bytes[1] == '?' && bytes[2] == 'x' && bytes[3] == 'm' && bytes[4] == 'l')
        {
            // Find encoding="..." in the XML declaration (scan ASCII bytes)
            var prologEnd = System.Array.IndexOf(bytes, (byte)'>', 5);
            if (prologEnd > 0 && prologEnd < 200)
            {
                var prolog = System.Text.Encoding.ASCII.GetString(bytes, 0, prologEnd);
                var encIdx = prolog.IndexOf("encoding", StringComparison.OrdinalIgnoreCase);
                if (encIdx > 0)
                {
                    var eqIdx = prolog.IndexOf('=', encIdx + 8);
                    if (eqIdx > 0)
                    {
                        var quote = eqIdx + 1 < prolog.Length ? prolog[eqIdx + 1] : '\0';
                        if (quote is '"' or '\'')
                        {
                            var closeQuote = prolog.IndexOf(quote, eqIdx + 2);
                            if (closeQuote > 0)
                            {
                                var encName = prolog[(eqIdx + 2)..closeQuote].Trim();
                                try
                                {
                                    return System.Text.Encoding.GetEncoding(encName);
                                }
                                catch (ArgumentException)
                                {
                                    // Unknown encoding, fall through to UTF-8
                                }
                            }
                        }
                    }
                }
            }
        }

        return System.Text.Encoding.UTF8;
    }
}
