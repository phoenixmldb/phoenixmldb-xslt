using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:iterate instruction (XSLT 3.0).
/// </summary>
public sealed class XsltIterate : XsltInstruction
{
    public required XQueryExpression Select { get; init; }
    public List<XsltParam> Params { get; init; } = new();
    public XsltSequenceConstructor? OnCompletion { get; init; }
    public required XsltSequenceConstructor Body { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitIterate(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.IterateAsync(this).ConfigureAwait(false);
    }
}
