using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Context for XSLT pattern matching.
/// </summary>
public class XsltContext
{
    public object? CurrentNode { get; set; }
    public int Position { get; set; }
    public int Last { get; set; }

    /// <summary>
    /// Resolves a NodeId to its XdmNode. Required for multi-step pattern matching.
    /// </summary>
    public Func<NodeId, XdmNode?>? NodeResolver { get; set; }

    /// <summary>
    /// Evaluates predicates during pattern matching.
    /// Parameters: (stepNode, expression, position, size, matchedNode) → bool
    /// - stepNode: The node being checked at the current step (may be an ancestor for multi-step patterns)
    /// - expression: The predicate expression to evaluate
    /// - position, size: For position() and last() functions
    /// - matchedNode: The node being matched by the entire pattern (for current() function)
    /// </summary>
    public Func<object, XQueryExpression, int, int, object?, bool>? PredicateEvaluator { get; set; }

    /// <summary>
    /// Computes the position of a node among its siblings matching a given node test.
    /// Returns (position, size) where position is 1-based and size is the total count.
    /// Used for evaluating positional predicates like [2] or [position() mod 2 = 1].
    /// Third parameter is optional descendant axis ancestor (for descendant axis patterns).
    /// </summary>
    public Func<object, NodeTest, object?, (int position, int size)>? PositionComputer { get; set; }

    /// <summary>
    /// Evaluates a chain of predicates against a node with true XPath filter semantics.
    /// Given the node being tested, the step's predicate list, its node test, and the
    /// optional descendant-axis ancestor, this builds the candidate sequence the node
    /// belongs to (in document order) and applies each predicate to the sequence that
    /// survived the earlier predicates — so <c>position()</c>/<c>last()</c> inside a later
    /// predicate are relative to the re-indexed survivors. Returns true iff the node is in
    /// the final filtered sequence. Used for patterns with two or more predicates such as
    /// <c>x[(position() mod 2)=1][position() &gt; 3]</c>; single-predicate steps keep the
    /// cheaper <see cref="PositionComputer"/> path. Null when no sequence-aware evaluator is
    /// wired (e.g. the streaming accumulator match context), in which case the caller falls
    /// back to per-node predicate evaluation.
    /// </summary>
    public Func<object, IReadOnlyList<XQueryExpression>, NodeTest, object?, bool>? SequencePredicateEvaluator { get; set; }

    /// <summary>
    /// When set, position computation should be relative to descendants of this ancestor
    /// rather than siblings of the parent. Used for descendant axis patterns like
    /// doc/descendant::*[position() mod 2 = 0].
    /// </summary>
    public object? DescendantPositionAncestor { get; set; }

    /// <summary>
    /// The node being matched by the entire pattern. Used for current() in multi-step patterns.
    /// Per XSLT 3.0 spec section 5.5.4: "current() refers to the node that is being matched by the pattern."
    /// This is set by PathPattern.Matches and should be used by the predicate evaluator for current().
    /// </summary>
    public object? MatchedNode { get; set; }

    /// <summary>
    /// Evaluates key() patterns during pattern matching.
    /// Parameters: (keyName, valueExpression, node) → bool
    /// Returns true if the given node is in the result of key(keyName, valueExpression).
    /// </summary>
    public Func<string, XQueryExpression, object, bool>? KeyPatternEvaluator { get; set; }

    /// <summary>
    /// Evaluates id() patterns during pattern matching.
    /// Parameters: (valueExpression, node) → bool
    /// Returns true if the given node is in the result of id(valueExpression).
    /// </summary>
    public Func<XQueryExpression, object, bool>? IdPatternEvaluator { get; set; }

    /// <summary>
    /// Evaluates variable reference patterns during pattern matching.
    /// Parameters: (variableName) → variable value (sequence or single item)
    /// Returns the current value of the named variable, or null if not found.
    /// </summary>
    public Func<QName, object?>? VariablePatternEvaluator { get; set; }

    /// <summary>
    /// Evaluates doc() function patterns during pattern matching.
    /// Parameters: (uri) → document node at that URI, or null if not available.
    /// </summary>
    public Func<string, XdmNode?>? DocPatternEvaluator { get; set; }

    /// <summary>
    /// Returns all nodes (in document order) of the tree containing the given node, used to
    /// evaluate outer positional predicates on parenthesized patterns like
    /// <c>(doc/descendant::foo)[2]</c>. Null when no node store is available.
    /// </summary>
    public Func<object, IReadOnlyList<object>>? TreeNodesInDocumentOrder { get; set; }
}
