using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// XSLT 3.0 "intersect" pattern (e.g., "$a intersect $b").
/// Matches node N if there exists a context node F such that N is selected by both F/Left and F/Right.
/// </summary>
public sealed class IntersectPattern : XsltPattern
{
    public required XsltPattern Left { get; init; }
    public required XsltPattern Right { get; init; }

    public override double DefaultPriority => 0.5;

    public override bool Matches(object node, XsltContext context)
    {
        // Context-scoped matching: find a common ancestor context F such that the node is
        // selected by both F/Left and F/Right. Both sides must share the SAME anchor F — this
        // is the whole point of an intersect pattern (match-278).
        if (node is XdmNode xdmNode && context.NodeResolver != null
            && xdmNode.Parent is { } startPid && startPid != NodeId.None)
        {
            var current = xdmNode;
            while (current.Parent is { } pid && pid != NodeId.None)
            {
                var parent = context.NodeResolver(pid);
                if (parent == null) break;

                if (ExceptPattern.MatchesFromContext(Left, node, parent, context)
                    && ExceptPattern.MatchesFromContext(Right, node, parent, context))
                    return true;

                current = parent;
            }
            // The node has ancestors, so the context-scoped search above is authoritative:
            // no common anchor means no match. Do NOT fall through to unanchored matching,
            // which would (incorrectly) accept a node satisfying each side from DIFFERENT
            // anchors.
            return false;
        }

        // Fallback for parentless nodes (no anchor to scope to): simple matching.
        return Left.Matches(node, context) && Right.Matches(node, context);
    }

    public override bool MatchesNodeTest(object node) => Left.MatchesNodeTest(node) && Right.MatchesNodeTest(node);
}
