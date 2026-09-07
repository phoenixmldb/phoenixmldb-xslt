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
    public Dictionary<string, string> InScopeNamespaces { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitAttribute(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateAttributeAsync(this).ConfigureAwait(false);
    }
}
