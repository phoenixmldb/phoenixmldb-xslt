using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Static classifier that decides whether an <see cref="XsltMerge"/> instruction qualifies
/// for the streaming K-way merge fast path. The streaming runtime (see
/// <see cref="DefaultXsltExecutionContext.MergeAsyncStreaming"/>) is intentionally narrow —
/// it only handles the shape that lets us drive each input with an <see cref="XmlReader"/>
/// without materialising the full document. When any source falls outside this shape, the
/// engine falls back to the non-streaming path that fully materialises each input.
/// </summary>
internal static class XsltMergeStreaming
{
    /// <summary>
    /// Returns true when every merge source is marked <c>streamable="yes"</c>, uses
    /// <c>for-each-source</c> (i.e. a URI sequence), and the <c>select</c> expression is
    /// a single child-axis step with a simple name/kind test and no predicates. These are
    /// the only shapes the streaming runtime currently understands.
    /// </summary>
    internal static bool CanStreamMerge(XsltMerge instruction)
    {
        if (instruction.Sources.Count == 0) return false;
        foreach (var src in instruction.Sources)
        {
            if (!src.Streamable) return false;
            if (src.ForEachSource == null) return false;
            if (src.ForEachItem != null) return false;
            if (src.SortBeforeMerge) return false;
            if (src.UseAccumulators.Count > 0) return false;
            // A validated document is read into a typed tree, as xsl:source-document's is.
            if (src.Validation is Ast.ValidationMode.Strict or Ast.ValidationMode.Lax || src.ValidationType is not null) return false;
            if (TryGetChildSteps(src.Select) == null) return false;
        }
        return true;
    }

    /// <summary>
    /// The node tests of <paramref name="select"/> when it is a relative path of child-axis
    /// steps with no predicates (<c>log/day/record</c>, <c>item</c>); otherwise null. The path
    /// starts at the document node, so its first step tests the document element.
    /// </summary>
    /// <remarks>
    /// Only a single step was accepted, and the reader then tested it against the CHILDREN of
    /// the document element: select="item" over &lt;a&gt;&lt;item/&gt;&lt;/a&gt; merged the
    /// items, where the path selects nothing, while "a/item", which does select them, was not
    /// streamed at all.
    /// </remarks>
    internal static IReadOnlyList<NodeTest>? TryGetChildSteps(XQueryExpression select)
    {
        if (select is not PathExpression path) return null;
        if (path.InitialExpression != null) return null;
        if (path.IsAbsolute) return null;
        if (path.Steps.Count == 0) return null;
        var tests = new List<NodeTest>(path.Steps.Count);
        foreach (var step in path.Steps)
        {
            if (step.Axis != Axis.Child) return null;
            if (step.Predicates.Count > 0) return null;
            tests.Add(step.NodeTest);
        }
        return tests;
    }
}
