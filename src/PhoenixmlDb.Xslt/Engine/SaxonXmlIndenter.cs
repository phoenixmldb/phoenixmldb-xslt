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
/// Saxon-HE 10.9 lays it out, to the byte, as observed from its output.
/// </summary>
/// <remarks>
/// The rules, each with the generated cases that change when it alone is switched off, are in the
/// text of xslt#499; <c>SaxonLayoutTests</c> runs those cases (<c>scripts/saxon-layout-cases.py</c>).
/// In short: three spaces per level; namespace declarations first, sorted by prefix; <c>&amp;#34;</c>
/// for a double quote in an attribute; with the xml method, attributes wrapped by a cost rule and
/// newlines decided by what was written last (a start tag, an end tag or text), whitespace-only
/// text held; with the xhtml method, the layout of an element taken from its local name (inline,
/// formatted, void, block). This class writes every node itself: tags, text, comments, processing
/// instructions, CDATA sections and the document type declaration.
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

    // The xhtml method writes an empty element as <x /> only for these names (case as written) in
    // the XHTML namespace; any other empty element is <x></x>.
    private const string XhtmlNamespace = "http://www.w3.org/1999/xhtml";
    private static readonly HashSet<string> VoidElements = new(StringComparer.Ordinal)
    {
        "area", "base", "basefont", "br", "col", "embed", "frame", "hr", "img", "input", "isindex", "link",
        "meta", "param",
    };

    private readonly XmlWriter _xw;
    private readonly StringBuilder _buffer;
    private readonly HashSet<(NamespaceId, string)>? _suppressed;
    private readonly bool _xhtml;
    private int _depth;
    private Last _last = Last.Nothing;
    // Whether anything is written at the top level (outside the document element) yet.
    private bool _topWritten;
    // Inside an element whose tags are not indented: suppress-indentation, xml:space="preserve".
    private bool _noIndent;
    // xhtml method: a formatted element (pre, script, ...) started, and no formatted element has
    // ended since. A flag, not a level: the end of a formatted element inside another one ends it.
    private bool _formatted;
    private bool NoIndent => _noIndent || _formatted;
    // Inside xml:space="preserve": nothing is indented, comments and processing instructions too.
    private bool _preserve;
    private readonly Stack<(string Name, bool NoIndent, bool Preserve, bool Inline, bool Formatted)> _open = new();
    // xhtml method: a comment or processing instruction came last. The next one, and an end tag,
    // stay on its line.
    private bool _afterComment;
    // Whitespace-only text not yet written: it is dropped when what follows goes on a new line.
    private readonly StringBuilder _pendingSpace = new();
    private readonly List<string> _indents = ["\n"];

    public SaxonXmlIndenter(XmlWriter xw, StringBuilder buffer, HashSet<QName>? suppressIndentation, bool xhtml)
    {
        _xw = xw;
        _buffer = buffer;
        _xhtml = xhtml;
        _suppressed = Names(suppressIndentation);
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

    // The xhtml method indents the line after each newline in text, in place of the spaces (U+0020
    // only) that begin it, except in formatted and suppress-indentation elements; comments,
    // processing instructions and CDATA sections are written as they are.
    private string IndentText(string text)
    {
        if (!_xhtml || NoIndent || !text.Contains('\n', StringComparison.Ordinal))
            return text;
        var indent = IndentOf(_depth);
        var sb = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                sb.Append(text[i]);
                continue;
            }
            sb.Append(indent);
            while (i + 1 < text.Length && text[i + 1] == ' ')
                i++;
        }
        return sb.ToString();
    }

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
        return !_preserve && !NoIndent && _last == Last.EndTag;
    }

    private void StartTag(XmlReader reader)
    {
        // A start tag goes on a line of its own after a start or end tag, and at the top level
        // after anything; after text it stays on the line. The xhtml method: an inline element's
        // never; any other's also after text, and not after an inline element's end tag.
        var inline = _xhtml && InlineElements.Contains(reader.LocalName);
        bool newLine;
        if (NoIndent)
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
        var isSuppressed = _suppressed is not null && _suppressed.Contains(expanded);
        var isFormatted = _xhtml && FormattedElements.Contains(reader.LocalName);
        var count = namespaces.Count + attributes.Count;
        // Not in or on an element that is not indented (suppress-indentation, xml:space="preserve"),
        // and never with the xhtml method.
        var wrap = count >= 2 && cost > LineLength && !NoIndent && !isSuppressed && !preserve && !_xhtml;
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
            if (!_xhtml)
                tag.Append("/>");
            else if (reader.NamespaceURI == XhtmlNamespace && VoidElements.Contains(reader.LocalName))
                tag.Append(" />");
            else
                tag.Append("></").Append(name).Append('>');
            _xw.WriteRaw(tag.ToString());
            _last = inline ? Last.InlineEnd : Last.EndTag;
            return;
        }
        tag.Append('>');
        _xw.WriteRaw(tag.ToString());
        _open.Push((name, _noIndent, _preserve, inline, isFormatted));
        _depth++;
        _noIndent |= isSuppressed || preserve;
        _formatted |= isFormatted;
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
        var (name, noIndent, preserve, inline, formatted) = _open.Pop();
        // An end tag goes on a line of its own after an end tag. The xhtml method: an inline
        // element's never, and not just after a comment or processing instruction.
        Before(!NoIndent && !inline && _last == Last.EndTag && !(_xhtml && _afterComment));
        _xw.WriteRaw("</" + name + ">");
        _noIndent = noIndent;
        if (formatted)
            _formatted = false;
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
