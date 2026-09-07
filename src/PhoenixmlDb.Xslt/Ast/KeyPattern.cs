using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// XSLT pattern for key() function calls in match patterns.
/// Matches nodes that are in the result of key(keyName, value).
/// Supports patterns like: key('k', 'v'), key('k', $var), key('k', 'v')//child
/// </summary>
public sealed class KeyPattern : XsltPattern
{
    /// <summary>The key name (first argument to key()).</summary>
    public required string KeyName { get; init; }

    /// <summary>The value expression (second argument to key()).</summary>
    public required XQueryExpression ValueExpression { get; init; }

    /// <summary>
    /// Optional continuation pattern for descendant/child steps after key().
    /// For example, in key('k','v')//p, the continuation is the path pattern for "p".
    /// </summary>
    public XsltPattern? Continuation { get; init; }

    /// <summary>Whether the separator to the continuation is '//' (descendant) vs '/' (child).</summary>
    public bool DescendantSeparator { get; init; }

    public override double DefaultPriority => 0.5;

    public override bool Matches(object node, XsltContext context)
    {
        if (context.KeyPatternEvaluator == null)
            return false;

        if (Continuation == null)
        {
            // Simple key pattern: node must be in key() result
            return context.KeyPatternEvaluator(KeyName, ValueExpression, node);
        }

        // key('k','v')//child or key('k','v')/child pattern:
        // 1. Node must match the continuation pattern's node test
        if (!Continuation.MatchesNodeTest(node))
            return false;

        // 2. Walk up ancestors to find one that's in key() result
        if (node is not XdmNode xdmNode || context.NodeResolver == null)
            return false;

        if (DescendantSeparator)
        {
            // '//' separator: any ancestor can be in key() result
            var ancestor = xdmNode.Parent is { } pid && pid != NodeId.None ? context.NodeResolver(pid) : null;
            while (ancestor != null)
            {
                if (context.KeyPatternEvaluator(KeyName, ValueExpression, ancestor))
                {
                    // Also check continuation predicates
                    if (Continuation.Matches(node, context))
                        return true;
                }
                ancestor = ancestor is XdmNode anc && anc.Parent is { } ancPid && ancPid != NodeId.None
                    ? context.NodeResolver(ancPid) : null;
            }
        }
        else
        {
            // '/' separator: parent must be in key() result
            var parent = xdmNode.Parent is { } ppid && ppid != NodeId.None ? context.NodeResolver(ppid) : null;
            if (parent != null && context.KeyPatternEvaluator(KeyName, ValueExpression, parent))
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
        return true; // key pattern without continuation could match any node
    }
}
