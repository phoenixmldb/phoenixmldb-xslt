using System.Text;
using System.Xml;
using PhoenixmlDb.Core;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>An indenter of an xml or xhtml result, one node at a time (see <see cref="XmlIndenter"/>).</summary>
internal interface IXmlIndenter
{
    /// <summary>Writes the node <paramref name="reader"/> is on; <paramref name="value"/> is its value when <see cref="XmlIndenter.HasValue"/>.</summary>
    void Node(XmlReader reader, string? value);

    /// <summary>What the layout writes after the last node.</summary>
    void End();
}

/// <summary>
/// <c>phx:indent-style="saxon-he-10"</c> (xslt#499): the xml and xhtml indentation laid out as
/// Saxon-HE 10.9 lays it out, to the byte, observed from its output (the cases of SaxonLayoutTests):
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Three spaces per level.</item>
/// <item>The namespace declarations of an element come first, sorted by prefix, then its
/// attributes in their order.</item>
/// <item>When an element has two or more of them and their cost is more than 80, the first stays
/// on the line of the start tag and each other goes on a line of its own, under the first. The
/// cost of a namespace declaration is the length of its text (` xmlns:p="uri"`); of an attribute,
/// the length of its name and of its value (not escaped), plus 8. The element's name and the
/// indentation do not count.</item>
/// <item>A comment or processing instruction just after a start tag stays on that line, also
/// several in a row; elsewhere it goes on a line of its own.</item>
/// <item>In mixed content nothing is indented, but an end tag after the end of an element child
/// goes on a line of its own.</item>
/// <item>A double quote in an attribute value is <c>&amp;#34;</c>.</item>
/// <item>A newline at the end of the result.</item>
/// </list>
/// Start and end tags are written by this class; text, comments, processing instructions, CDATA
/// sections and the document type declaration by the XmlWriter, as the default layout writes them.
/// </remarks>
internal sealed class SaxonXmlIndenter : IXmlIndenter
{
    private const int LineLength = 80;
    private const int IndentWidth = 3;

    // What was written last, of a start tag, an end tag and text: a comment or processing
    // instruction does not change it. InlineEnd: the end of an inline element (xhtml method only).
    private enum Last { Nothing, StartTag, EndTag, InlineEnd, Text }

    // The xhtml method lays out an element by its local name, in any namespace and in any case.
    // Inline elements are never put on a new line, nor is what follows their end tag (a block
    // start tag); inside formatted elements only comments and processing instructions are.
    private static readonly HashSet<string> InlineElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "acronym", "applet", "area", "audio", "b", "basefont", "bdi", "bdo", "big", "br",
        "button", "canvas", "cite", "code", "data", "datalist", "del", "dfn", "em", "embed", "font", "i",
        "iframe", "img", "input", "ins", "kbd", "label", "map", "mark", "math", "meter", "noscript",
        "object", "output", "picture", "progress", "q", "ruby", "s", "samp", "script", "select", "small",
        "span", "strike", "strong", "sub", "sup", "svg", "template", "textarea", "time", "tt", "u", "var",
        "video", "wbr",
    };

    private static readonly HashSet<string> FormattedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "pre", "script", "style", "textarea", "title", "xmp",
    };

    private readonly XmlWriter _xw;
    private readonly StringBuilder _buffer;
    private readonly HashSet<(NamespaceId, string)>? _suppressed;
    private readonly HashSet<(NamespaceId, string)>? _cdataSection;
    private readonly bool _xhtml;
    private int _depth;
    private Last _last = Last.Nothing;
    // Whether anything is written at the top level (outside the document element) yet.
    private bool _topWritten;
    // Inside an element whose tags are not indented: suppress-indentation, xml:space="preserve".
    private bool _noIndent;
    // Inside xml:space="preserve": nothing is indented, comments and processing instructions too.
    private bool _preserve;
    private readonly Stack<(string Name, bool NoIndent, bool Preserve, bool Inline)> _open = new();
    // xhtml method: a comment or processing instruction came last. The next one, and an end tag,
    // stay on its line.
    private bool _afterComment;
    // Whitespace-only text not yet written: it is dropped when what follows goes on a new line.
    private readonly StringBuilder _pendingSpace = new();
    private readonly List<string> _indents = ["\n"];

    public SaxonXmlIndenter(XmlWriter xw, StringBuilder buffer, HashSet<QName>? suppressIndentation, bool xhtml,
        HashSet<QName>? cdataSectionElements)
    {
        _xw = xw;
        _buffer = buffer;
        _xhtml = xhtml;
        _suppressed = Names(suppressIndentation);
        _cdataSection = Names(cdataSectionElements);
    }

    private static HashSet<(NamespaceId, string)>? Names(HashSet<QName>? names)
    {
        if (names is not { Count: > 0 })
            return null;
        var set = new HashSet<(NamespaceId, string)>();
        foreach (var name in names)
            set.Add((name.Namespace, name.LocalName));
        return set;
    }

    private string IndentOf(int depth)
    {
        while (_indents.Count <= depth)
            _indents.Add("\n" + new string(' ', _indents.Count * IndentWidth));
        return _indents[depth];
    }

    private void NewLine() => _xw.WriteRaw(IndentOf(_depth));

    // Before the next thing is written: a new line in place of the whitespace held back, or the
    // whitespace as it is.
    private void Before(bool newLine)
    {
        if (newLine)
            NewLine();
        else if (_pendingSpace.Length > 0)
            _xw.WriteRaw(_pendingSpace.ToString());
        _pendingSpace.Clear();
    }

    public void Node(XmlReader reader, string? value)
    {
        switch (reader.NodeType)
        {
            case XmlNodeType.Element:
                StartTag(reader);
                break;
            case XmlNodeType.EndElement:
                EndTag();
                break;
            case XmlNodeType.Text:
                WriteText(IndentText(EscapeText(value ?? "")));
                break;
            case XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                // With the xhtml method, and in xml:space="preserve", it is text; elsewhere it waits
                // for what follows, and does not change what came last.
                if (_xhtml || _preserve)
                    WriteText(IndentText(EscapeText(value ?? "")));
                else
                    _pendingSpace.Append(EscapeText(value ?? ""));
                break;
            case XmlNodeType.CDATA:
                WriteText("<![CDATA[" + (value ?? "").Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal) + "]]>");
                break;
            case XmlNodeType.Comment:
                Before(MarkupIndented());
                _xw.WriteRaw("<!--" + value + "-->");
                _topWritten |= _depth == 0;
                _afterComment = true;
                break;
            case XmlNodeType.ProcessingInstruction:
                Before(MarkupIndented());
                _xw.WriteRaw(string.IsNullOrEmpty(value) ? "<?" + reader.Name + "?>" : "<?" + reader.Name + " " + value + "?>");
                _topWritten |= _depth == 0;
                _afterComment = true;
                break;
            case XmlNodeType.DocumentType:
                Before(_topWritten);
                var doctype = new StringBuilder("<!DOCTYPE ").Append(reader.Name);
                var publicId = reader.GetAttribute("PUBLIC");
                var systemId = reader.GetAttribute("SYSTEM");
                if (publicId != null)
                    doctype.Append(" PUBLIC \"").Append(publicId).Append("\" \"").Append(systemId).Append('"');
                else if (systemId != null)
                    doctype.Append(" SYSTEM \"").Append(systemId).Append('"');
                if (!string.IsNullOrEmpty(value))
                    doctype.Append(" [").Append(value).Append(']');
                _xw.WriteRaw(doctype.Append('>').ToString());
                _topWritten = true;
                break;
        }
    }

    private void WriteText(string markup)
    {
        Before(newLine: false);
        _xw.WriteRaw(markup);
        _last = Last.Text;
        _afterComment = false;
    }

    // The xhtml method indents the line after each newline in text, except in formatted and
    // suppress-indentation elements; comments, processing instructions and CDATA sections are
    // written as they are.
    private string IndentText(string text)
        => _xhtml && !_noIndent && text.Contains('\n', StringComparison.Ordinal)
            ? text.Replace("\n", IndentOf(_depth), StringComparison.Ordinal)
            : text;

    private static string EscapeText(string value)
    {
        if (value.AsSpan().IndexOfAny("&<>\r") < 0)
            return value;
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\r': sb.Append("&#xD;"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    // The xml method ends with a newline; the xhtml method does not.
    public void End()
    {
        Before(newLine: false);
        if (!_xhtml)
            _xw.WriteRaw("\n");
    }

    // A comment or processing instruction goes on a line of its own after an end tag, and at the
    // top level after anything; just after a start tag or text it stays on that line. The xml
    // method: not in suppress-indentation or xml:space="preserve". The xhtml method: also there
    // and in formatted elements, but not in an inline element, and not just after another one.
    private bool MarkupIndented()
    {
        if (_depth == 0)
            return _topWritten;
        if (_xhtml)
            return !_open.Peek().Inline && _last is (Last.EndTag or Last.InlineEnd) && !_afterComment;
        return !_preserve && !_noIndent && _last == Last.EndTag;
    }

    private void StartTag(XmlReader reader)
    {
        // A start tag goes on a line of its own after a start or end tag, and at the top level
        // after anything; after text it stays on the line. The xhtml method: an inline element's
        // never; any other's also after text, and not after an inline element's end tag.
        var inline = _xhtml && InlineElements.Contains(reader.LocalName);
        bool newLine;
        if (_noIndent)
            newLine = false;
        else if (_depth == 0)
            newLine = _topWritten;
        else if (_xhtml)
            newLine = !inline && _last != Last.InlineEnd;
        else
            newLine = _last is Last.StartTag or Last.EndTag;
        Before(newLine);
        var name = reader.Name;
        var empty = reader.IsEmptyElement;
        var namespaces = new List<(string Prefix, string Uri)>();
        var attributes = new List<(string Name, string Value)>();
        var preserve = false;
        if (reader.MoveToFirstAttribute())
        {
            do
            {
                if (reader.Name == "xmlns")
                    namespaces.Add(("", reader.Value));
                else if (reader.Prefix == "xmlns")
                    namespaces.Add((reader.LocalName, reader.Value));
                else
                {
                    attributes.Add((reader.Name, reader.Value));
                    // The xhtml method indents in xml:space="preserve" too.
                    if (!_xhtml && reader.Name == "xml:space" && reader.Value == "preserve")
                        preserve = true;
                }
            }
            while (reader.MoveToNextAttribute());
            reader.MoveToElement();
        }
        namespaces.Sort((a, b) => string.CompareOrdinal(a.Prefix, b.Prefix));

        var cost = 0;
        foreach (var (prefix, uri) in namespaces)
            cost += (prefix.Length == 0 ? 9 : 10 + prefix.Length) + uri.Length;
        foreach (var (attrName, attrValue) in attributes)
            cost += attrName.Length + attrValue.Length + 8;
        var expanded = (StylesheetParser.ResolveNamespaceUri(reader.NamespaceURI), reader.LocalName);
        var isSuppressed = (_suppressed is not null && _suppressed.Contains(expanded))
            || (_xhtml && FormattedElements.Contains(reader.LocalName));
        var count = namespaces.Count + attributes.Count;
        // Not in or on an element that is not indented (suppress-indentation, xml:space="preserve"),
        // and never with the xhtml method.
        var wrap = count >= 2 && cost > LineLength && !_noIndent && !isSuppressed && !preserve && !_xhtml;
        // Under the first item as if the start tag began at its depth's indentation, also when it
        // follows text on the line.
        var column = wrap ? _depth * IndentWidth + 1 + name.Length + 1 : 0;

        var tag = new StringBuilder().Append('<').Append(name);
        var first = true;
        foreach (var (prefix, uri) in namespaces)
        {
            Separator(tag, ref first, wrap, column);
            tag.Append(prefix.Length == 0 ? "xmlns" : "xmlns:" + prefix).Append("=\"");
            AppendEscaped(tag, uri);
            tag.Append('"');
        }
        foreach (var (attrName, attrValue) in attributes)
        {
            Separator(tag, ref first, wrap, column);
            tag.Append(attrName).Append("=\"");
            AppendEscaped(tag, attrValue);
            tag.Append('"');
        }

        _topWritten |= _depth == 0;
        _afterComment = false;
        if (empty)
        {
            tag.Append(_xhtml ? " />" : "/>");
            _xw.WriteRaw(tag.ToString());
            _last = inline ? Last.InlineEnd : Last.EndTag;
            return;
        }
        tag.Append('>');
        _xw.WriteRaw(tag.ToString());
        _open.Push((name, _noIndent, _preserve, inline));
        _depth++;
        _noIndent |= isSuppressed || preserve;
        _preserve |= preserve;
        _last = Last.StartTag;
    }

    private static void Separator(StringBuilder tag, ref bool first, bool wrap, int column)
    {
        if (first || !wrap)
            tag.Append(' ');
        else
            tag.Append('\n').Append(' ', column);
        first = false;
    }

    private void EndTag()
    {
        _depth--;
        var (name, noIndent, preserve, inline) = _open.Pop();
        // An end tag goes on a line of its own after an end tag. The xhtml method: an inline
        // element's never, and not just after a comment or processing instruction.
        Before(!_noIndent && !inline && _last == Last.EndTag && !(_xhtml && _afterComment));
        _xw.WriteRaw("</" + name + ">");
        _noIndent = noIndent;
        _preserve = preserve;
        _last = inline ? Last.InlineEnd : Last.EndTag;
        _afterComment = false;
    }

    private static void AppendEscaped(StringBuilder sb, string value)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&#34;"); break;
                case '\t': sb.Append("&#x9;"); break;
                case '\n': sb.Append("&#xA;"); break;
                case '\r': sb.Append("&#xD;"); break;
                default: sb.Append(c); break;
            }
        }
    }
}
