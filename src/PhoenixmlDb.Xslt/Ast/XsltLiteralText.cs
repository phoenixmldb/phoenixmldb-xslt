using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Literal text node.
/// </summary>
public sealed class XsltLiteralText : XsltInstruction
{
    public required string Value { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitLiteralText(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        context.WriteText(Value, false);
        return ValueTask.CompletedTask;
    }
}
