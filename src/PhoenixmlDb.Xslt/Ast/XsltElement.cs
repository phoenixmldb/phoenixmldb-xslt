using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:element instruction.
/// </summary>
public sealed class XsltElement : XsltInstruction
{
    public required XsltAttributeValueTemplate Name { get; init; }
    public XsltAttributeValueTemplate? Namespace { get; init; }
    public List<QName> UseAttributeSets { get; init; } = new();
    public bool? InheritNamespaces { get; init; }
    public ValidationMode? Validation { get; init; }
    public QName? Type { get; init; }
    public required XsltSequenceConstructor Content { get; init; }

    /// <summary>
    /// Effective base URI from xml:base on the xsl:element instruction.
    /// When set, affects static-base-uri() for XPath expressions in this scope.
    /// </summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// In-scope namespace bindings from the stylesheet element (prefix → URI).
    /// Used to resolve prefixed element names when no namespace attribute is specified.
    /// </summary>
    public Dictionary<string, string> InScopeNamespaces { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitElement(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateElementAsync(this).ConfigureAwait(false);
    }
}
