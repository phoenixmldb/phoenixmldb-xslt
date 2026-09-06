using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:value-of instruction.
/// </summary>
public sealed class XsltValueOf : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public XsltAttributeValueTemplate? Separator { get; init; }
    public bool DisableOutputEscaping { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitValueOf(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.ValueOfAsync(this).ConfigureAwait(false);
    }
}
