using System.Collections;
using System.Reflection;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Finds a roaming axis taken from the node a streamed template rule matched:
/// <c>preceding-sibling::*</c>, <c>following::x</c>, <c>*/preceding::y</c>. The posture of such
/// a step is roaming (XSLT 3.0 §19.8.8.8), so the rule is not guaranteed-streamable and a
/// streaming processor reports XTSE3430 (xslt#337).
/// </summary>
/// <remarks>
/// It is made to refuse nothing that is streamable, so it reports only where the focus is known
/// to be the streamed node: in the template body outside any instruction that sets a new focus,
/// in a relative path that begins at the context item and consists of axis steps, and in the
/// predicates of those steps. It does not report a roaming axis from a node that may be
/// grounded: <c>copy-of(.)/preceding-sibling::*</c>, <c>$v/following::x</c>, anything inside
/// <c>xsl:for-each</c> (whose select may be a copy), the right of <c>!</c>, a filter on a
/// primary expression, or an inline function. Those run on a buffered copy as before.
/// </remarks>
internal static class RoamingAxisDetector
{
    /// <summary>The first roaming axis taken from the matched node in <paramref name="body"/>, or null.</summary>
    public static Finding? First(XsltSequenceConstructor? body)
    {
        var search = new Search();
        search.Instruction(body, new HashSet<object>(ReferenceEqualityComparer.Instance));
        return search.Found;
    }

    internal readonly record struct Finding(Axis Axis, SourceLocation? Location);

    private sealed class Search
    {
        public Finding? Found { get; private set; }

        /// <summary>Walks instructions whose expressions are evaluated with the template's own focus.</summary>
        public void Instruction(object? node, HashSet<object> seen)
        {
            if (Found != null)
                return;
            switch (node)
            {
                case null or string or PhoenixmlDb.Core.QName or ValueType:
                    return;
                case XQueryExpression expression:
                    Expression(expression);
                    return;
                case AvtExpression avt:
                    Expression(avt.Expression);
                    return;
                // A sort key is evaluated with each selected item as the focus.
                case XsltSort:
                    return;
            }
            if (!seen.Add(node))
                return;
            if (node is IEnumerable items)
            {
                foreach (var item in items)
                    Instruction(item, seen);
                return;
            }
            if (node is not (XsltInstruction or XsltAttributeValueTemplate or XsltWithParam or XsltWhen or XsltParam))
                return;
            // These set a new focus for what is inside them, and what they select may be
            // grounded. Only the expression that selects is evaluated with the template's focus.
            var selectOnly = node is XsltForEach or XsltForEachGroup or XsltIterate or XsltAnalyzeString
                or XsltPerformSort or XsltForEachMember or XsltMerge or XsltSourceDocument;
            foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.GetIndexParameters().Length > 0)
                    continue;
                if (selectOnly && property.Name != "Select")
                    continue;
                Instruction(property.GetValue(node), seen);
            }
        }

        private void Expression(XQueryExpression expression)
        {
            var walker = new Walker();
            walker.Walk(expression);
            Found ??= walker.Found is { } axis ? new Finding(axis, expression.Location) : null;
        }
    }

    /// <summary>Walks an expression, knowing at each point whether the focus is the streamed node.</summary>
    private sealed class Walker : XQueryExpressionWalker
    {
        private bool _streamedFocus = true;

        public Axis? Found { get; private set; }

        private static bool Roams(Axis axis) =>
            axis is Axis.Preceding or Axis.PrecedingSibling or Axis.Following or Axis.FollowingSibling;

        private void WithFocus(bool streamed, Action walk)
        {
            var saved = _streamedFocus;
            _streamedFocus = streamed;
            try { walk(); }
            finally { _streamedFocus = saved; }
        }

        public override object? VisitPathExpression(PathExpression expr)
        {
            // From the context item by axis steps: every step is taken from a streamed node.
            var fromContext = !expr.IsAbsolute && expr.InitialExpression is null or ContextItemExpression;
            if (expr.InitialExpression != null)
                Walk(expr.InitialExpression);
            WithFocus(_streamedFocus && fromContext, () =>
            {
                foreach (var step in expr.Steps)
                    Walk(step);
            });
            return null;
        }

        public override object? VisitStepExpression(StepExpression expr)
        {
            if (_streamedFocus && Roams(expr.Axis))
                Found ??= expr.Axis;
            // A predicate of a step from a streamed node has a streamed node as its focus.
            foreach (var predicate in expr.Predicates)
                Walk(predicate);
            return null;
        }

        public override object? VisitFilterExpression(FilterExpression expr)
        {
            Walk(expr.Primary);
            WithFocus(false, () =>
            {
                foreach (var predicate in expr.Predicates)
                    Walk(predicate);
            });
            return null;
        }

        public override object? VisitSimpleMapExpression(SimpleMapExpression expr)
        {
            Walk(expr.Left);
            WithFocus(false, () => Walk(expr.Right));
            return null;
        }

        public override object? VisitInlineFunctionExpression(InlineFunctionExpression expr)
        {
            WithFocus(false, () => base.VisitInlineFunctionExpression(expr));
            return null;
        }
    }
}
