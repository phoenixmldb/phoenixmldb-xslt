using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// A step in a pattern.
/// </summary>
public sealed class PatternStep
{
    public required Axis Axis { get; init; }
    public required NodeTest NodeTest { get; init; }
    public List<XQueryExpression> Predicates { get; init; } = new();

    /// <summary>
    /// When true, this step was preceded by '//' in the pattern,
    /// meaning it can match any ancestor (not just the direct parent).
    /// </summary>
    public bool DescendantSeparator { get; init; }

    /// <summary>
    /// When true, this step is the <c>root()</c> function used as a pattern step.
    /// It matches any node that is the root of its tree (a node with no parent):
    /// document nodes and parentless (free-standing) elements/other nodes.
    /// See W3C match-233.
    /// </summary>
    public bool IsRootFunction { get; init; }
}
