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

    /// <summary>
    /// When true, the first of <see cref="Predicates"/> is not one the stylesheet wrote: it is
    /// the test of the type of the node (<c>. instance of element(*, T)</c>) that a kind test
    /// with a type, <c>schema-element(N)</c> or <c>schema-attribute(N)</c> asks for. It does
    /// not count as a predicate for the default priority of the pattern.
    /// </summary>
    public bool HasTypeTest { get; init; }

    /// <summary>
    /// The default priority XSLT 3.0 §6.5 gives the kind test of this step when the pattern is
    /// that one step, or null when the node test alone decides it: 0 for element(*, T), 0.25
    /// for element(E, T), attribute(A, T), schema-element(E) and schema-attribute(A), and for
    /// document-node() around one of them that one's.
    /// </summary>
    public double? KindTestPriority { get; init; }

    /// <summary>
    /// True when any predicate on this step could observe <c>position()</c> or <c>last()</c>.
    /// </summary>
    /// <remarks>
    /// Establishing that context scans every sibling matching <see cref="NodeTest"/>, which is
    /// what made a predicated match pattern O(n²) (#95). The answer is static, so it is computed
    /// once on first use and cached here rather than per candidate node.
    /// </remarks>
    internal bool NeedsPositionContext =>
        _needsPositionContext ??= PredicatePositionAnalysis.NeedsPositionContext(Predicates);

    private bool? _needsPositionContext;
}
