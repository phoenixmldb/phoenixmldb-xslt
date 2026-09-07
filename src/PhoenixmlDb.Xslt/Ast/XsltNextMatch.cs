using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:next-match instruction (XSLT 2.0+).
/// </summary>
public sealed class XsltNextMatch : XsltInstruction
{
    public List<XsltWithParam> WithParams { get; init; } = new();
    public XsltSequenceConstructor? Fallback { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitNextMatch(this);

    public override async ValueTask ExecuteAsync(XsltExecutionContext context)
    {
        await context.NextMatchAsync(WithParams, Fallback).ConfigureAwait(false);
    }
}
