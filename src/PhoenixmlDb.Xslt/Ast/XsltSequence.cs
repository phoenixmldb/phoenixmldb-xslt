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

    /// <summary>
    /// Set on the xsl:sequence a stylesheet function's body evaluates last (see
    /// StylesheetParser.MarkFunctionTailCalls): the function's tail-call key. A call in tail
    /// position of its select to a stylesheet function of the same key runs as the caller's next
    /// iteration instead of nested (xquery#296).
    /// </summary>
    public string? TailCallKey { get; set; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitSequence(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.SequenceAsync(this).ConfigureAwait(false);
    }
}
