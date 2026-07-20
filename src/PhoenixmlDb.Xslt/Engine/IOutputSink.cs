namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Emission abstraction for the XSLT serializer (SP-A). Every markup fragment the
/// transformer currently appends directly to its <c>_output</c> <see cref="System.Text.StringBuilder"/>
/// is modeled here as one method, shaped to match the actual append sequences in
/// <c>DefaultXsltExecutionContext.SerializeNode</c> / <c>CreateElementAsync</c> /
/// <c>WriteText</c>. <see cref="StringOutputSink"/> is the byte-identical string
/// implementation; a future node-building sink can implement the same contract to
/// construct XDM nodes directly instead of re-parsing serialized markup.
/// </summary>
internal interface IOutputSink
{
    /// <summary>Opens a start tag: appends "&lt;qName" (no trailing "&gt;" yet).</summary>
    void StartElementOpen(string qName);

    /// <summary>
    /// Appends a namespace declaration on the currently open start tag:
    /// " xmlns=\"uri\"" when <paramref name="prefix"/> is empty (including the
    /// undeclaration case uri == ""), otherwise " xmlns:prefix=\"uri\"". The URI is
    /// escaped via <see cref="DefaultXsltExecutionContext.EscapeAttributeValue"/>.
    /// </summary>
    void Namespace(string prefix, string uri);

    /// <summary>
    /// Appends an attribute on the currently open start tag: " qName=\"escaped-value\"".
    /// The value is escaped via <see cref="DefaultXsltExecutionContext.EscapeAttributeValue"/>.
    /// </summary>
    void Attribute(string qName, string value);

    /// <summary>Closes the currently open start tag: appends "&gt;" or "/&gt;" for an empty element.</summary>
    void StartElementClose(bool selfClose);

    /// <summary>Appends XML-escaped text content via <see cref="DefaultXsltExecutionContext.EscapeText"/>.</summary>
    void Text(string value);

    /// <summary>Appends markup verbatim, with no escaping (disable-output-escaping / raw fragments).</summary>
    void RawText(string markup);

    /// <summary>Appends a comment: "&lt;!--value--&gt;" (value pre-sanitized via EscapeCommentValue).</summary>
    void Comment(string value);

    /// <summary>
    /// Appends a processing instruction: "&lt;?target data?&gt;" when data is non-empty,
    /// otherwise "&lt;?target?&gt;" (data pre-sanitized via EscapePIValue).
    /// </summary>
    void ProcessingInstruction(string target, string data);

    /// <summary>Appends an end tag: "&lt;/qName&gt;".</summary>
    void EndElement(string qName);
}
