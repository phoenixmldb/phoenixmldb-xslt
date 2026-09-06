using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Represents xsl:source-document instruction (XSLT 3.0 streaming).
/// </summary>
public sealed class XsltSourceDocument : XsltInstruction
{
    /// <summary>
    /// Source document URI.
    /// </summary>
    public required XsltAttributeValueTemplate Href { get; init; }

    /// <summary>
    /// Whether to stream the document.
    /// </summary>
    public bool Streamable { get; init; }

    /// <summary>
    /// Validation mode.
    /// </summary>
    public ValidationMode Validation { get; init; } = ValidationMode.Strip;

    /// <summary>
    /// Content to execute with the source document.
    /// </summary>
    public XsltSequenceConstructor? Content { get; init; }

    /// <summary>
    /// Effective base URI for resolving the href attribute (may differ from stylesheet base URI due to xml:base).
    /// </summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// List of accumulator names to apply when processing the source document.
    /// </summary>
    public List<QName> UseAccumulators { get; init; } = new();

    /// <summary>
    /// Deferred XTSE3430 streamability error message. When set, the instruction
    /// throws at runtime instead of parse time, allowing shared stylesheets
    /// with multiple templates to compile even if some templates are non-streamable.
    /// </summary>
    public string? StreamabilityError { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitSourceDocument(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
        => context.SourceDocumentAsync(this);
}
