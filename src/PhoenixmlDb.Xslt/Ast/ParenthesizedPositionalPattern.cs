using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// A parenthesized pattern with an outer positional/filter predicate, e.g.
/// <c>(doc/descendant::foo)[2]</c>. Unlike <c>doc/descendant::foo[2]</c> (where the
/// predicate binds to the last step per starting node), the outer predicate here filters
/// the ENTIRE sequence of nodes matching the inner pattern, in document order across the
/// whole tree. See W3C match-076.
/// </summary>
public sealed class ParenthesizedPositionalPattern : XsltPattern
{
    public required XsltPattern Inner { get; init; }
    public required IReadOnlyList<XQueryExpression> Predicates { get; init; }

    // A pattern with a predicate has default priority 0.5 (XSLT 3.0 §6.4).
    public override double DefaultPriority => 0.5;

    public override bool MatchesNodeTest(object node) => Inner.MatchesNodeTest(node);

    public override bool Matches(object node, XsltContext context)
    {
        // The node must first match the inner pattern.
        if (!Inner.Matches(node, context))
            return false;
        if (Predicates.Count == 0)
            return true;
        if (context.PredicateEvaluator == null || context.TreeNodesInDocumentOrder == null)
            return true; // no way to build the sequence — accept the inner match

        // Build the document-order sequence of all nodes in the tree that match the inner
        // pattern, and locate the target within it.
        int position = 0, size = 0;
        foreach (var candidate in context.TreeNodesInDocumentOrder(node))
        {
            if (!Inner.Matches(candidate, context))
                continue;
            size++;
            if (position == 0 && ReferenceEquals(candidate, node))
                position = size;
            else if (position == 0 && candidate is XdmNode cn && node is XdmNode tn && cn.Id == tn.Id && cn.Id != NodeId.None)
                position = size;
        }
        if (position == 0)
            return false;

        context.MatchedNode = node;
        foreach (var predicate in Predicates)
        {
            if (!context.PredicateEvaluator(node, predicate, position, size, node))
                return false;
        }
        return true;
    }
}
