using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:break instruction (XSLT 3.0).
/// </summary>
public sealed class XsltBreak : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitBreak(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        context.Break(this);
        return ValueTask.CompletedTask;
    }
}
