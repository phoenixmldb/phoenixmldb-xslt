using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Whether any template rule of a streamable mode reads outside the element it matched: a sibling,
/// preceding or following step, a step back down after climbing (<c>../*</c>), an absolute path,
/// <c>last()</c> or <c>root()</c>. The streaming pass can buffer a matched element's subtree, but
/// not its surroundings, so such a rule cannot be answered there. Climbing alone (<c>..</c>,
/// <c>ancestor::x/@id</c>) is streamable (XSLT 3.0 §19.8.8) and is left to the streaming pass, which
/// also raises the dynamic errors that belong to it (W3C accumulator-061: XTDE3350).
/// </summary>
/// <remarks>
/// Such a body is not streamable (XSLT 3.0 §19), and a streaming processor should reject it with
/// XTSE3430. The engine's streamability analysis is not complete enough to do that safely (BUGS #16),
/// so these bodies were streamed and answered wrongly with no error: count(preceding-sibling::*) was
/// 0 for every item, last() was 1 and count(../*) was 0 (xslt#298). The caller evaluates such a
/// transformation against a tree instead: the right answer, at the memory cost of the tree.
/// This is deliberately syntactic and over-inclusive; falling back to the tree is always correct.
/// </remarks>
internal static class StreamedScopeEscapeDetector
{
    private static readonly ConditionalWeakTable<XsltStylesheet, StrongBox<bool>> _cache = new();

    public static bool AnyStreamableRuleEscapesItsMatch(XsltStylesheet stylesheet)
        => _cache.GetValue(stylesheet, s => new StrongBox<bool>(Compute(s))).Value;

    private static bool Compute(XsltStylesheet stylesheet)
    {
        var streamableModes = new HashSet<QName>();
        foreach (var (name, mode) in stylesheet.Modes)
            if (mode.Streamable)
                streamableModes.Add(name);
        if (streamableModes.Count == 0)
            return false;

        var unnamed = new QName(NamespaceId.None, "");
        foreach (var template in stylesheet.Templates)
        {
            if (template.Match == null)
                continue;
            var inStreamableMode = template.Modes.Count == 0
                ? streamableModes.Contains(unnamed)
                : template.Modes.Any(m => m.Equals(TemplateIndex.AllModeSentinel)
                    || streamableModes.Contains(m.Equals(TemplateIndex.DefaultModeSentinel) ? unnamed : m));
            if (inStreamableMode && Escapes(template.Body, new HashSet<object>(ReferenceEqualityComparer.Instance)))
                return true;
        }
        return false;
    }

    /// <summary>Walks an instruction tree: its XPath expressions, AVT parts and nested instructions.</summary>
    private static bool Escapes(object? node, HashSet<object> seen)
    {
        switch (node)
        {
            case null:
                return false;
            case XQueryExpression expr:
                return ExpressionEscapes(expr);
            case AvtExpression avt:
                return ExpressionEscapes(avt.Expression);
            case string or QName or ValueType:
                return false;
        }
        if (!seen.Add(node))
            return false;
        if (node is IEnumerable items)
        {
            foreach (var item in items)
                if (Escapes(item, seen))
                    return true;
            return false;
        }
        if (node is not (XsltInstruction or XsltAttributeValueTemplate or XsltWithParam or XsltSort
            or XsltWhen or XsltParam))
            return false;
        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;
            if (Escapes(property.GetValue(node), seen))
                return true;
        }
        return false;
    }

    private static bool ExpressionEscapes(XQueryExpression expr)
    {
        var walker = new EscapeWalker();
        walker.Walk(expr);
        return walker.Escapes;
    }

    private sealed class EscapeWalker : XQueryExpressionWalker
    {
        public bool Escapes { get; private set; }

        public override object? VisitPathExpression(PathExpression expr)
        {
            if (expr.IsAbsolute)
                Escapes = true;
            // Climbing, then descending again: ../* reads the parent's other children.
            var climbed = false;
            foreach (var step in expr.Steps)
            {
                if (step is not StepExpression st)
                    continue;
                if (st.Axis is Axis.Parent or Axis.Ancestor or Axis.AncestorOrSelf)
                    climbed = true;
                else if (climbed && st.Axis is not (Axis.Self or Axis.Attribute or Axis.Namespace))
                    Escapes = true;
            }
            return base.VisitPathExpression(expr);
        }

        public override object? VisitStepExpression(StepExpression expr)
        {
            if (expr.Axis is Axis.PrecedingSibling or Axis.FollowingSibling or Axis.Preceding or Axis.Following)
                Escapes = true;
            return base.VisitStepExpression(expr);
        }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (expr.Name.LocalName is "last" or "root")
                Escapes = true;
            return base.VisitFunctionCallExpression(expr);
        }
    }
}
