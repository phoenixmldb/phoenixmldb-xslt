using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Accumulator rule.
/// </summary>
public sealed class XsltAccumulatorRule
{
    public required XsltPattern Match { get; init; }
    public AccumulatorPhase Phase { get; init; } = AccumulatorPhase.Start;
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
}
