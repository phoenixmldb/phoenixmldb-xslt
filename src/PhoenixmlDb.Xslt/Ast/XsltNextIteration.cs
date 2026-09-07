using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:next-iteration instruction (XSLT 3.0).
/// </summary>
public sealed class XsltNextIteration : XsltInstruction
{
    public List<XsltWithParam> WithParams { get; init; } = new();

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitNextIteration(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        context.NextIteration(this);
        return ValueTask.CompletedTask;
    }
}
