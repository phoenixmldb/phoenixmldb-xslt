using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:processing-instruction instruction.
/// </summary>
public sealed class XsltProcessingInstruction : XsltInstruction
{
    public required XsltAttributeValueTemplate Name { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitProcessingInstruction(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreatePIAsync(this).ConfigureAwait(false);
    }
}
