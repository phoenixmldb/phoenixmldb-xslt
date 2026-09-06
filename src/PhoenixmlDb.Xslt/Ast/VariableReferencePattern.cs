using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// XSLT 3.0 variable reference pattern (e.g., "$nodes", "$x//baz").
/// Matches nodes that are members of the variable's value sequence.
/// Supports optional path continuation: $var/path or $var//path.
/// </summary>
public sealed class VariableReferencePattern : XsltPattern
{
    /// <summary>The variable name (QName).</summary>
    public required QName VariableName { get; init; }

    /// <summary>
    /// Optional continuation pattern for descendant/child steps after the variable.
    /// For example, in $x//baz, the continuation is the path pattern for "baz".
    /// </summary>
    public XsltPattern? Continuation { get; init; }

    /// <summary>Whether the separator to the continuation is '//' (descendant) vs '/' (child).</summary>
    public bool DescendantSeparator { get; init; }

    /// <summary>
    /// Optional predicates applied directly to the variable reference, e.g. <c>$v[@att1='a']</c>
    /// or <c>$v[2]</c>. Position/size are relative to the variable's value sequence. See W3C match-074.
    /// </summary>
    public List<XQueryExpression> Predicates { get; init; } = new();

    public override double DefaultPriority => 0.5;

    public override bool Matches(object node, XsltContext context)
    {
        if (context.VariablePatternEvaluator == null)
            return false;

        var variableValue = context.VariablePatternEvaluator(VariableName);
        if (variableValue == null)
            return false;

        if (Continuation == null)
        {
            // Simple variable pattern: node must be a member of the variable's value.
            if (!IsMemberOf(node, variableValue))
                return false;
            if (Predicates.Count == 0)
                return true;
            // Predicates are filtered against the variable's value sequence: position()/last()
            // are relative to that sequence (in its stored order).
            var (position, size) = MemberPositionOf(node, variableValue);
            if (context.PredicateEvaluator == null)
                return true;
            context.MatchedNode = node;
            foreach (var predicate in Predicates)
            {
                if (!context.PredicateEvaluator(node, predicate, position, size, node))
                    return false;
            }
            return true;
        }

        // $var//child or $var/child pattern:
        // 1. Node must match the continuation pattern
        if (!Continuation.MatchesNodeTest(node))
            return false;

        // 2. Walk up ancestors to find one that's in the variable's value
        if (node is not XdmNode xdmNode || context.NodeResolver == null)
            return false;

        if (DescendantSeparator)
        {
            // '//' separator: any ancestor-or-self can be in variable value
            // Check self first (for $var//descendant-or-self::*)
            var ancestor = xdmNode.Parent is { } pid && pid != NodeId.None ? context.NodeResolver(pid) : null;
            while (ancestor != null)
            {
                if (IsMemberOf(ancestor, variableValue))
                {
                    if (Continuation.Matches(node, context))
                        return true;
                }
                ancestor = ancestor is XdmNode anc && anc.Parent is { } ancPid && ancPid != NodeId.None
                    ? context.NodeResolver(ancPid) : null;
            }
        }
        else
        {
            // '/' separator: parent must be in variable value
            var parent = xdmNode.Parent is { } ppid && ppid != NodeId.None ? context.NodeResolver(ppid) : null;
            if (parent != null && IsMemberOf(parent, variableValue))
            {
                if (Continuation.Matches(node, context))
                    return true;
            }
        }

        return false;
    }

    public override bool MatchesNodeTest(object node)
    {
        if (Continuation != null)
            return Continuation.MatchesNodeTest(node);
        return true; // variable pattern without continuation could match any node
    }

    /// <summary>
    /// Checks if a node is a member (by node identity) of a variable's value sequence.
    /// </summary>
    private static bool IsMemberOf(object node, object variableValue)
    {
        // Single node
        if (variableValue is XdmNode singleNode)
            return ReferenceEquals(node, singleNode) || (node is XdmNode n && n.Id == singleNode.Id && n.Id != NodeId.None);

        // Sequence of items (object?[])
        if (variableValue is object?[] seq)
        {
            foreach (var item in seq)
            {
                if (item == null) continue;
                if (ReferenceEquals(node, item)) return true;
                if (node is XdmNode nodeN && item is XdmNode itemN && nodeN.Id == itemN.Id && nodeN.Id != NodeId.None)
                    return true;
            }
            return false;
        }

        // IList (arrays, other collections)
        if (variableValue is System.Collections.IList list)
        {
            foreach (var item in list)
            {
                if (item == null) continue;
                if (ReferenceEquals(node, item)) return true;
                if (node is XdmNode nodeN && item is XdmNode itemN && nodeN.Id == itemN.Id && nodeN.Id != NodeId.None)
                    return true;
            }
            return false;
        }

        // Single non-node item — direct equality
        return ReferenceEquals(node, variableValue);
    }

    /// <summary>
    /// Returns the 1-based position of <paramref name="node"/> within the variable's value
    /// sequence and the total size of that sequence. Used for predicate context on
    /// <c>$v[predicate]</c> patterns.
    /// </summary>
    private static (int position, int size) MemberPositionOf(object node, object variableValue)
    {
        static bool Same(object node, object? item)
            => item != null && (ReferenceEquals(node, item)
                || (node is XdmNode n && item is XdmNode i && n.Id == i.Id && n.Id != NodeId.None));

        var items = variableValue switch
        {
            object?[] arr => (System.Collections.IEnumerable)arr,
            System.Collections.IList list => list,
            _ => new[] { variableValue }
        };

        int size = 0, position = 0;
        foreach (var item in items)
        {
            size++;
            if (position == 0 && Same(node, item))
                position = size;
        }
        return (position == 0 ? 1 : position, size == 0 ? 1 : size);
    }
}
