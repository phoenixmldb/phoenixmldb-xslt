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
            if (TryGetSimpleChildStep(src.Select) == null) return false;
        }
        return true;
    }

    /// <summary>
    /// Returns the single child-axis step inside <paramref name="select"/> when it is a
    /// streaming-friendly path; otherwise null. Recognised shapes: a single
    /// <see cref="PathExpression"/> whose only step has <see cref="Axis.Child"/>, a node
    /// test and no predicates.
    /// </summary>
    internal static NodeTest? TryGetSimpleChildStep(XQueryExpression select)
    {
        if (select is not PathExpression path) return null;
        if (path.InitialExpression != null) return null;
        if (path.IsAbsolute) return null;
        if (path.Steps.Count != 1) return null;
        var step = path.Steps[0];
        if (step.Axis != Axis.Child) return null;
        if (step.Predicates.Count > 0) return null;
        return step.NodeTest;
    }
}
