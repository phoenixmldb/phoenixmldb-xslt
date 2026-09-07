using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// XSLT pattern for id() function calls in match patterns.
/// Matches nodes that are in the result of id(value).
/// Supports patterns like: id('v'), id($var), id('v')//child
/// </summary>
public sealed class IdPattern : XsltPattern
{
    /// <summary>The value expression (argument to id()).</summary>
    public required XQueryExpression ValueExpression { get; init; }

    /// <summary>
    /// Optional continuation pattern for descendant/child steps after id().
    /// For example, in id('v')//p, the continuation is the path pattern for "p".
    /// </summary>
    public XsltPattern? Continuation { get; init; }

    /// <summary>Whether the separator to the continuation is '//' (descendant) vs '/' (child).</summary>
    public bool DescendantSeparator { get; init; }

    public override double DefaultPriority => 0.5;

    public override bool Matches(object node, XsltContext context)
    {
        if (context.IdPatternEvaluator == null)
            return false;

        if (Continuation == null)
        {
            // Simple id pattern: node must be in id() result
            return context.IdPatternEvaluator(ValueExpression, node);
        }

        // id('v')//child or id('v')/child pattern:
        // 1. Node must match the continuation pattern's node test
        if (!Continuation.MatchesNodeTest(node))
            return false;

        // 2. Walk up ancestors to find one that's in id() result
        if (node is not XdmNode xdmNode || context.NodeResolver == null)
            return false;

        if (DescendantSeparator)
        {
            // '//' separator: any ancestor can be in id() result
            var ancestor = xdmNode.Parent is { } pid && pid != NodeId.None ? context.NodeResolver(pid) : null;
            while (ancestor != null)
            {
                if (context.IdPatternEvaluator(ValueExpression, ancestor))
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
            // '/' separator: the id-identified element is N ancestors up,
            // where N = number of steps in the continuation pattern.
            // For id('x')/a/b/c matching node c: a=parent, b=grandparent, x=great-grandparent.
            int depth = Continuation is PathPattern pp ? pp.Steps.Count : 1;
            XdmNode? ancestor = xdmNode;
            for (int d = 0; d < depth && ancestor != null; d++)
            {
                ancestor = ancestor.Parent is { } pid && pid != NodeId.None ? context.NodeResolver(pid) as XdmNode : null;
            }
            if (ancestor != null && context.IdPatternEvaluator(ValueExpression, ancestor))
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
        return true; // id pattern without continuation could match any node
    }
}
