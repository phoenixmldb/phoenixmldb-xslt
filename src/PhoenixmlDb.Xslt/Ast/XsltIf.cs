using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:if instruction.
/// </summary>
public sealed class XsltIf : XsltInstruction
{
    public required XQueryExpression Test { get; init; }
    public required XsltSequenceConstructor Then { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitIf(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        if (await context.EvaluateBooleanAsync(Test).ConfigureAwait(false))
        {
            await Then.ExecuteAsync(context).ConfigureAwait(false);
        }
    }
}
