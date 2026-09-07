using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:result-document instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltResultDocument : XsltInstruction
{
    public XsltAttributeValueTemplate? Href { get; init; }
    public XsltAttributeValueTemplate? Format { get; init; }
    /// <summary>
    /// The format name resolved at compile time (namespace-aware QName).
    /// Set when the format attribute is a static value (not a dynamic AVT).
    /// </summary>
    public QName? ResolvedFormatName { get; init; }
    public ValidationMode? Validation { get; init; }
    public QName? Type { get; init; }
    public XsltAttributeValueTemplate? Method { get; init; }
    public XsltAttributeValueTemplate? OmitXmlDeclaration { get; init; }
    public XsltAttributeValueTemplate? Encoding { get; init; }
    /// <summary>The <c>standalone</c> serialization attribute (yes/no/omit; may be an AVT).</summary>
    public XsltAttributeValueTemplate? Standalone { get; init; }
    /// <summary>The <c>output-version</c> serialization attribute (may be an AVT).</summary>
    public XsltAttributeValueTemplate? OutputVersion { get; init; }
    public XsltAttributeValueTemplate? Indent { get; init; }
    /// <summary>The <c>html-version</c> serialization attribute (may be an AVT). For the html/xhtml
    /// output methods a value &gt;= 5.0 selects HTML5 serialization, which emits <c>&lt;!DOCTYPE html&gt;</c>
    /// (result-document-0242 / 0244).</summary>
    public XsltAttributeValueTemplate? HtmlVersion { get; init; }
    public XsltAttributeValueTemplate? DoctypePublic { get; init; }
    public XsltAttributeValueTemplate? DoctypeSystem { get; init; }
    /// <summary>The <c>media-type</c> serialization attribute (may be an AVT); drives the
    /// HTML/XHTML Content-Type meta and the serialized media type.</summary>
    public XsltAttributeValueTemplate? MediaType { get; init; }
    /// <summary>The <c>include-content-type</c> serialization attribute (yes/no; may be an AVT).</summary>
    public XsltAttributeValueTemplate? IncludeContentType { get; init; }
    /// <summary>The <c>byte-order-mark</c> serialization attribute (yes/no; may be an AVT).
    /// When truthy, a U+FEFF is prepended to the serialized result document.</summary>
    public XsltAttributeValueTemplate? ByteOrderMark { get; init; }
    /// <summary>The <c>escape-uri-attributes</c> serialization attribute (yes/no; may be an AVT);
    /// controls percent-encoding of non-ASCII characters in URI-valued HTML/XHTML attributes.</summary>
    public XsltAttributeValueTemplate? EscapeUriAttributes { get; init; }
    /// <summary>The <c>cdata-section-elements</c> serialization attribute: a whitespace-separated
    /// list of element QNames whose text content is serialized inside <c>&lt;![CDATA[…]]&gt;</c>.
    /// Stored as an AVT because it is an attribute value template on xsl:result-document
    /// (result-document-0401). Evaluated at runtime, split on whitespace and resolved to QNames
    /// against <see cref="NamespaceBindings"/>; the effective set is the union with the matched
    /// xsl:output declaration's cdata-section-elements (result-document-0240).</summary>
    public XsltAttributeValueTemplate? CdataSectionElements { get; init; }
    public bool? BuildTree { get; init; }
    public XsltAttributeValueTemplate? ItemSeparator { get; init; }
    public XsltAttributeValueTemplate? AllowDuplicateNames { get; init; }
    public List<QName> UseCharacterMaps { get; init; } = [];
    /// <summary>
    /// The <c>parameter-document</c> serialization attribute (an AVT; XSLT 3.0 §27.1). References an
    /// external <c>output:serialization-parameters</c> document whose parameters (notably
    /// <c>method</c>) and inline character maps supplement this result-document's serialization.
    /// Resolved and loaded at runtime because the URI may reference variables
    /// (insn/result-document/result-document-1406 uses <c>"{$o}-params.xml"</c>). Values set directly
    /// on xsl:result-document take precedence over the parameter document per §27.1.
    /// </summary>
    public XsltAttributeValueTemplate? ParameterDocument { get; init; }
    /// <summary>
    /// Namespace bindings from the source element, for resolving prefixed format names at runtime.
    /// </summary>
    public IReadOnlyDictionary<string, string>? NamespaceBindings { get; init; }
    public required XsltSequenceConstructor Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitResultDocument(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.ResultDocumentAsync(this).ConfigureAwait(false);
    }
}
