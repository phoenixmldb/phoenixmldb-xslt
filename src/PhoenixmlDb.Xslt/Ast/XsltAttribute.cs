using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:attribute instruction.
/// </summary>
public sealed class XsltAttribute : XsltInstruction
{
    public required XsltAttributeValueTemplate Name { get; init; }
    public XsltAttributeValueTemplate? Namespace { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public XsltAttributeValueTemplate? Separator { get; init; }
    public ValidationMode? Validation { get; init; }
    public QName? Type { get; init; }

    /// <summary>The schema type the <c>type</c> attribute names, which the node is validated against; null when there is none.</summary>
    public XsltTypeName? ValidationType { get; init; }
    /// <summary>In-scope namespace bindings from the stylesheet element (prefix → URI).</summary>
    /// <remarks>
    /// Read-only: equal sets in one stylesheet module are one shared dictionary, so a write to
    /// one would change every instruction that shares it.
    /// </remarks>
    public IReadOnlyDictionary<string, string> InScopeNamespaces { get; init; } = Engine.NamespaceSets.Empty;

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitAttribute(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateAttributeAsync(this).ConfigureAwait(false);
    }
}
