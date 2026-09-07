using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Union pattern (e.g., "para | title").
/// </summary>
public sealed class UnionPattern : XsltPattern
{
    public required IReadOnlyList<XsltPattern> Patterns { get; init; }

    /// <summary>
    /// For union patterns, default priority is computed per-alternative when used
    /// as a template match. However for sorting purposes, use the max of all alternatives.
    /// </summary>
    public override double DefaultPriority =>
        Patterns.Count > 0 ? Patterns.Max(p => p.DefaultPriority) : -0.5;

    public override bool Matches(object node, XsltContext context)
    {
        foreach (var pattern in Patterns)
        {
            if (pattern.Matches(node, context))
                return true;
        }
        return false;
    }

    public override bool MatchesNodeTest(object node)
    {
        foreach (var pattern in Patterns)
        {
            if (pattern.MatchesNodeTest(node))
                return true;
        }
        return false;
    }
}
