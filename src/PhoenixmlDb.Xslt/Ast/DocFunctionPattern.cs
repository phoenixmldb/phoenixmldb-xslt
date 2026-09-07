using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// XSLT 3.0 doc() function pattern (e.g., "doc('file.xml')", "doc('file.xml')//foo").
/// Matches nodes that belong to the document at the specified URI.
/// </summary>
public sealed class DocFunctionPattern : XsltPattern
{
    /// <summary>The URI argument to doc().</summary>
#pragma warning disable CA1056 // URI stored as resolved string for pattern matching
    public required string DocumentUri { get; init; }
#pragma warning restore CA1056

    /// <summary>
    /// Optional continuation pattern for descendant/child steps after doc().
    /// For example, in doc('file.xml')//foo, the continuation matches "foo".
    /// </summary>
    public XsltPattern? Continuation { get; init; }

    /// <summary>Whether the separator to the continuation is '//' (descendant) vs '/' (child).</summary>
    public bool DescendantSeparator { get; init; }

    public override double DefaultPriority => 0.5;

    public override bool Matches(object node, XsltContext context)
    {
        if (context.DocPatternEvaluator == null)
            return false;

        var docNode = context.DocPatternEvaluator(DocumentUri);
        if (docNode == null)
            return false;

        if (Continuation == null)
        {
            // Bare doc('uri') — match the document node itself
            if (node is XdmNode n)
                return ReferenceEquals(node, docNode) || (n.Id == docNode.Id && n.Id != NodeId.None);
            return false;
        }

        // doc('uri')/path or doc('uri')//path:
        // Node must match continuation AND be in the document tree of doc('uri')
        if (!Continuation.MatchesNodeTest(node))
            return false;

        if (node is not XdmNode xdmNode || context.NodeResolver == null)
            return false;

        // Walk up to the root and verify it's the doc() document
        var current = xdmNode;
        while (current.Parent is { } pid && pid != NodeId.None)
        {
            var parent = context.NodeResolver(pid);
            if (parent == null) break;
            current = parent;
        }

        // The root must be the doc() document node
        if (current is not XdmDocument)
            return false;
        if (!ReferenceEquals(current, docNode) && (current.Id != docNode.Id || current.Id == NodeId.None))
            return false;

        // Now check the continuation path
        return Continuation.Matches(node, context);
    }

    public override bool MatchesNodeTest(object node)
    {
        if (Continuation != null)
            return Continuation.MatchesNodeTest(node);
        return node is XdmDocument; // bare doc() matches document nodes
    }
}
