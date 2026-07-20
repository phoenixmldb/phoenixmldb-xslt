using System.Text;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// <see cref="IOutputSink"/> implementation that reproduces exactly the markup the
/// engine's inline <c>_output</c> appends have always produced. Wraps the same
/// <see cref="StringBuilder"/> instance the transformer already writes to, so — once
/// call sites are converted in later SP-A tasks — the serialized string stays
/// byte-for-byte identical to today's output. Escaping is delegated to
/// <see cref="DefaultXsltExecutionContext"/>'s existing static escapers to avoid any
/// divergence between the direct-append path and the sink path.
/// </summary>
internal sealed class StringOutputSink(StringBuilder output) : IOutputSink
{
    private readonly StringBuilder _output = output;

    public void StartElementOpen(string qName)
    {
        _output.Append('<');
        _output.Append(qName);
    }

    public void Namespace(string prefix, string uri)
    {
        _output.Append(" xmlns");
        if (!string.IsNullOrEmpty(prefix))
        {
            _output.Append(':');
            _output.Append(prefix);
        }
        _output.Append("=\"");
        _output.Append(DefaultXsltExecutionContext.EscapeAttributeValue(uri));
        _output.Append('"');
    }

    public void Attribute(string qName, string value)
    {
        _output.Append(' ');
        _output.Append(qName);
        _output.Append("=\"");
        _output.Append(DefaultXsltExecutionContext.EscapeAttributeValue(value));
        _output.Append('"');
    }

    public void StartElementClose(bool selfClose)
    {
        _output.Append(selfClose ? "/>" : ">");
    }

    public void Text(string value)
    {
        _output.Append(DefaultXsltExecutionContext.EscapeText(value));
    }

    public void RawText(string markup)
    {
        _output.Append(markup);
    }

    public void Comment(string value)
    {
        _output.Append("<!--");
        _output.Append(DefaultXsltExecutionContext.EscapeCommentValue(value));
        _output.Append("-->");
    }

    public void ProcessingInstruction(string target, string data)
    {
        _output.Append("<?");
        _output.Append(target);
        if (!string.IsNullOrEmpty(data))
        {
            _output.Append(' ');
            _output.Append(DefaultXsltExecutionContext.EscapePIValue(data));
        }
        _output.Append("?>");
    }

    public void EndElement(string qName)
    {
        _output.Append("</");
        _output.Append(qName);
        _output.Append('>');
    }
}
