using System.Text;
using System.Xml;
using PhoenixmlDb.Core;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// The xml and xhtml indentation (<c>indent="yes"</c>), one node at a time: each node read from
/// the result is written again with the whitespace that goes before it. Nothing is indented in
/// mixed content, in an element named by <c>suppress-indentation</c>, or after whitespace outside
/// the document element. <see cref="XsltTransformEngine.ApplyXmlIndentation"/> drives it over a
/// whole result; <see cref="StreamedResultFinalizer"/> over a result written while the
/// transformation streams (xslt#575), so both write the same text.
/// </summary>
internal sealed class XmlIndenter : IXmlIndenter
{
    private readonly XmlWriter _xw;
    // What _xw writes to: the empty-element form is corrected in it.
    private readonly StringBuilder _buffer;
    private readonly HashSet<(NamespaceId, string)>? _suppressed;
    private readonly bool _xhtml;
    private int _depth;
    private bool _mixed;
    // Whitespace outside the document element stops the indentation of what follows it
    // there, but not inside the document element.
    private bool _topMixed;
    private bool _hasContent;
    // Whether anything is written since the start or since the last text.
    private bool _afterMarkup;
    private readonly Stack<(bool Mixed, bool HasContent)> _open = new();
    // A newline and the indentation of each depth, made once.
    private readonly List<string> _indents = ["\n"];

    public XmlIndenter(XmlWriter xw, StringBuilder buffer, HashSet<QName>? suppressIndentation, bool xhtml)
    {
        _xw = xw;
        _buffer = buffer;
        _xhtml = xhtml;
        if (suppressIndentation is { Count: > 0 })
        {
            _suppressed = [];
            foreach (var name in suppressIndentation)
                _suppressed.Add((name.Namespace, name.LocalName));
        }
    }

    /// <summary>
    /// The settings XDocument.Parse reads with. A streamed result is read as it comes, a fragment
    /// at a time and asynchronously: a second top-level element can no longer leave the whole
    /// result unindented, as it does a whole one, because what came before it is written.
    /// </summary>
    internal static XmlReaderSettings ReaderSettings(bool streamed)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, XmlResolver = null };
        if (streamed)
        {
            settings.ConformanceLevel = ConformanceLevel.Fragment;
            settings.Async = true;
        }
        return settings;
    }

    internal static XmlWriterSettings WriterSettings(bool streamed)
    {
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true, // We handle the declaration separately
            NewLineHandling = NewLineHandling.Replace,
            // NewLineChars defaults to Environment.NewLine, so indent="yes" wrote CRLF on
            // Windows and LF elsewhere: the same transform gave different bytes per OS. The
            // serializer's newline is LF everywhere, as the rest of the output already is.
            NewLineChars = "\n",
        };
        if (streamed)
            settings.ConformanceLevel = ConformanceLevel.Fragment;
        return settings;
    }

    /// <summary>Whether <see cref="Node"/> needs the node's value (read by the driver, which may read it asynchronously).</summary>
    internal static bool HasValue(XmlNodeType nodeType) => nodeType is XmlNodeType.Text or XmlNodeType.Whitespace
        or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA or XmlNodeType.Comment
        or XmlNodeType.ProcessingInstruction or XmlNodeType.DocumentType;

    // An end tag is indented by its own element's content; anything else by where it is.
    private void Indent(bool endTag = false)
    {
        if (!(_depth == 0 && !endTag ? _topMixed : _mixed) && _afterMarkup)
        {
            while (_indents.Count <= _depth)
                _indents.Add("\n" + new string(' ', _indents.Count * 2));
            _xw.WriteRaw(_indents[_depth]);
        }
    }

    private void Text()
    {
        _mixed = true;
        _hasContent = true;
        _afterMarkup = false;
    }

    /// <summary>The default layout writes nothing after the last node.</summary>
    public void End()
    {
    }

    /// <summary>Writes the node <paramref name="reader"/> is on; <paramref name="value"/> is its value when <see cref="HasValue"/>.</summary>
    public void Node(XmlReader reader, string? value)
    {
        switch (reader.NodeType)
        {
            case XmlNodeType.Element:
                Indent();
                _hasContent = true;
                var empty = reader.IsEmptyElement;
                var isSuppressed = _suppressed is not null
                    && _suppressed.Contains((StylesheetParser.ResolveNamespaceUri(reader.NamespaceURI), reader.LocalName));
                _xw.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
                _xw.WriteAttributes(reader, defattr: true);
                reader.MoveToElement();
                _afterMarkup = true;
                if (empty)
                {
                    _xw.WriteEndElement();
                    // XmlWriter always writes "<e />". The XML method writes "<e/>", as the
                    // unindented output does: indent="yes" changed the empty tags too.
                    if (!_xhtml)
                    {
                        _xw.Flush();
                        var n = _buffer.Length;
                        if (n >= 3 && _buffer[n - 3] == ' ' && _buffer[n - 2] == '/' && _buffer[n - 1] == '>')
                            _buffer.Remove(n - 3, 1);
                    }
                    break;
                }
                _open.Push((_mixed, _hasContent));
                _depth++;
                _hasContent = false;
                if (isSuppressed)
                    _mixed = true;
                break;
            case XmlNodeType.EndElement:
                _depth--;
                if (_hasContent)
                    Indent(endTag: true);
                _xw.WriteFullEndElement();
                (_mixed, _hasContent) = _open.Pop();
                _afterMarkup = true;
                break;
            case XmlNodeType.Text:
                _xw.WriteString(value);
                Text();
                break;
            case XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                _xw.WriteWhitespace(value);
                if (_depth == 0)
                {
                    _topMixed = true;
                    _afterMarkup = false;
                }
                else
                    Text();
                break;
            case XmlNodeType.CDATA:
                _xw.WriteCData(value);
                Text();
                break;
            case XmlNodeType.Comment:
                Indent();
                _xw.WriteComment(value);
                _hasContent = true;
                _afterMarkup = true;
                break;
            case XmlNodeType.ProcessingInstruction:
                Indent();
                _xw.WriteProcessingInstruction(reader.Name, value);
                _hasContent = true;
                _afterMarkup = true;
                break;
            case XmlNodeType.DocumentType:
                Indent();
                _xw.WriteDocType(reader.Name, reader.GetAttribute("PUBLIC"), reader.GetAttribute("SYSTEM"), value);
                _afterMarkup = true;
                break;
        }
    }
}
