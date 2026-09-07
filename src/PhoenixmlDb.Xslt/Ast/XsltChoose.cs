using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:choose instruction.
/// </summary>
public sealed class XsltChoose : XsltInstruction
{
    public required IReadOnlyList<XsltWhen> When { get; init; }
    public XsltSequenceConstructor? Otherwise { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitChoose(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        foreach (var when in When)
        {
            if (await context.EvaluateBooleanAsync(when.Test).ConfigureAwait(false))
            {
                await when.Body.ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        if (Otherwise != null)
        {
            await Otherwise.ExecuteAsync(context).ConfigureAwait(false);
        }
    }
}
