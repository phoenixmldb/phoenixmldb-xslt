using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// XSLT 3.0 "except" pattern (e.g., "* except q").
/// Matches node N if there exists a context node F such that N is selected by F/Left but not by F/Right.
/// </summary>
public sealed class ExceptPattern : XsltPattern
{
    public required XsltPattern Left { get; init; }
    public required XsltPattern Right { get; init; }

    public override double DefaultPriority => 0.5;

    public override bool Matches(object node, XsltContext context)
    {
        // Context-scoped matching: find a common ancestor context where
        // the node matches Left but not Right
        if (node is XdmNode xdmNode && context.NodeResolver != null)
        {
            // Try each ancestor as the context node
            var current = xdmNode;
            while (current.Parent is { } pid && pid != NodeId.None)
            {
                var parent = context.NodeResolver(pid);
                if (parent == null) break;

                if (MatchesFromContext(Left, node, parent, context)
                    && !MatchesFromContext(Right, node, parent, context))
                    return true;

                current = parent;
            }
        }

        // Fallback: try simple matching (works for same-axis patterns and parentless nodes)
        return Left.Matches(node, context) && !Right.Matches(node, context);
    }

    public override bool MatchesNodeTest(object node) => Left.MatchesNodeTest(node);

    /// <summary>
    /// Checks if a node matches a pattern when evaluated from a specific context (ancestor) node.
    /// For a PathPattern with a single step, this checks: does the axis relationship hold
    /// between contextNode and node, AND does the node match the step's node test?
    /// </summary>
    internal static bool MatchesFromContext(XsltPattern pattern, object node, XdmNode contextNode, XsltContext context)
    {
        if (pattern is PathPattern path && path.Steps.Count > 0)
        {
            // Absolute patterns (starting with '/') are context-independent —
            // the document root anchor makes them match the same nodes regardless
            // of the ancestor context. Use simple global matching.
            if (path.Steps[0].Axis == Axis.Self && path.Steps[0].NodeTest is KindTest { Kind: XdmNodeKind.Document })
                return path.Matches(node, context);

            // A pattern whose first step is introduced by '//' (e.g. "//div[@id='a']//*") is
            // rooted at the document, so it selects the same nodes regardless of the ancestor
            // context F. Use global matching — it must NOT be anchored to a specific F (match-279).
            if (path.Steps[0].DescendantSeparator)
                return path.Matches(node, context);

            var lastStep = path.Steps[^1];

            // Check if node matches the final step's node test and its predicates.
            // Predicates must be honoured here so anchored intersect/except patterns like
            // "div[@id='a']//* intersect div[@id='b']//*" require both sides' constraints to
            // hold relative to the SAME context node, not merely somewhere in the tree (match-278).
            if (!PathPattern.MatchesStepPublic(lastStep, node))
                return false;
            if (!PathPattern.EvaluatePredicatesPublic(lastStep, node, context))
                return false;

            // For single-step patterns, check axis relationship between context and node
            if (path.Steps.Count == 1)
                return CheckAxisRelationship(lastStep.Axis, contextNode, node, context);

            // Multi-step: verify the path from context through intermediate steps to node
            // Walk from node up to context, checking each step
            if (node is not XdmNode xdmNode) return false;
            return MatchMultiStepFromContext(path, xdmNode, contextNode, context);
        }

        // For Except/Intersect patterns, recursively constrain both sides to the same context
        if (pattern is ExceptPattern ep)
            return MatchesFromContext(ep.Left, node, contextNode, context)
                && !MatchesFromContext(ep.Right, node, contextNode, context);
        if (pattern is IntersectPattern ip)
            return MatchesFromContext(ip.Left, node, contextNode, context)
                && MatchesFromContext(ip.Right, node, contextNode, context);

        // Variable reference and doc() patterns are context-independent
        if (pattern is VariableReferencePattern or DocFunctionPattern)
            return pattern.Matches(node, context);

        // For other pattern types (Union, etc.), use simple matching
        return pattern.Matches(node, context);
    }

    private static bool MatchMultiStepFromContext(PathPattern path, XdmNode node, XdmNode contextNode, XsltContext context)
    {
        if (context.NodeResolver == null) return false;

        // Walk from node upward, matching steps right to left
        var current = node;
        for (var i = path.Steps.Count - 2; i >= 0; i--)
        {
            var step = path.Steps[i];
            var nextStep = path.Steps[i + 1];
            var walkAncestors = nextStep.DescendantSeparator
                || nextStep.Axis is Axis.Descendant or Axis.DescendantOrSelf;

            if (walkAncestors)
            {
                // Walk up to find a matching ancestor
                var found = false;
                var ancestor = current;
                while (ancestor.Parent is { } pid && pid != NodeId.None)
                {
                    var parent = context.NodeResolver(pid);
                    if (parent == null) break;

                    if (PathPattern.MatchesStepPublic(step, parent)
                        && PathPattern.EvaluatePredicatesPublic(step, parent, context))
                    {
                        if (i == 0)
                        {
                            // First step must match the context node
                            if (parent == contextNode) { found = true; current = parent; break; }
                        }
                        else
                        {
                            found = true; current = parent; break;
                        }
                    }
                    ancestor = parent;
                }
                if (!found) return false;
            }
            else
            {
                // Must match direct parent
                var parentId = current.Parent;
                if (parentId is null || parentId.Value == NodeId.None) return false;
                var parent = context.NodeResolver(parentId.Value);
                if (parent == null) return false;

                if (!PathPattern.MatchesStepPublic(step, parent)) return false;
                if (!PathPattern.EvaluatePredicatesPublic(step, parent, context)) return false;

                if (i == 0 && parent != contextNode) return false;

                current = parent;
            }
        }

        return true;
    }

    private static bool CheckAxisRelationship(Axis axis, XdmNode contextNode, object node, XsltContext context)
    {
        if (context.NodeResolver == null) return false;

        switch (axis)
        {
            case Axis.Child:
            {
                // node must be a direct child of contextNode
                if (node is XdmAttribute attr)
                    return attr.Parent is { } pid && pid != NodeId.None
                        && context.NodeResolver(pid) == contextNode;
                if (node is not XdmNode xdmNode) return false;
                return xdmNode.Parent is { } parentId && parentId != NodeId.None
                    && context.NodeResolver(parentId) == contextNode;
            }
            case Axis.Descendant:
            case Axis.DescendantOrSelf:
            {
                if (node is not XdmNode xdmNode) return false;
                if (axis == Axis.DescendantOrSelf && xdmNode == contextNode) return true;
                var current = xdmNode;
                while (current.Parent is { } pid && pid != NodeId.None)
                {
                    var parent = context.NodeResolver(pid);
                    if (parent == contextNode) return true;
                    if (parent == null) break;
                    current = parent;
                }
                return false;
            }
            case Axis.Self:
                return node == contextNode;
            case Axis.Attribute:
            {
                if (node is not XdmAttribute attrNode) return false;
                return attrNode.Parent is { } pid && pid != NodeId.None
                    && context.NodeResolver(pid) == contextNode;
            }
            default:
                return false;
        }
    }
}
