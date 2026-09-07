using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:message instruction.
/// </summary>
public sealed class XsltMessage : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public bool Terminate { get; init; }
    public XsltAttributeValueTemplate? TerminateAvt { get; init; }
    public string? ErrorCode { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitMessage(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.MessageAsync(this).ConfigureAwait(false);
    }
}
