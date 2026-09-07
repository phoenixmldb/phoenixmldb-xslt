using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:sequence instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltSequence : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitSequence(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.SequenceAsync(this).ConfigureAwait(false);
    }
}
