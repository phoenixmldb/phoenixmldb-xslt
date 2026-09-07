using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:comment instruction.
/// </summary>
public sealed class XsltComment : XsltInstruction
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitComment(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.CreateCommentAsync(this).ConfigureAwait(false);
    }
}
