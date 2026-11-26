using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// XSLT 3.0 §19.8.9.18: where the context item is a document node, <c>root()</c> and
/// <c>root(.)</c> are that node. In a template rule whose pattern matches only document nodes, and
/// that has no name (a named rule can be called with another context item), the calls of
/// <c>root()</c> and <c>root(.)</c> evaluated with the rule's own focus
/// (<see cref="StreamedScopeEscapeDetector.FocusRootCalls"/>) are replaced with the context item:
/// <c>count(root()/books)</c> is <c>count(./books)</c>. The value is the same, because the
/// context item is the document node, and the streaming checks and the streamed run see a path
/// from the matched node. Before, such a rule was accepted by the check of §19.8 (xslt#655) but
/// read the input into memory (BUGS.md #166).
/// </summary>
/// <remarks>
/// Only the places that keep the rule's focus are visited, the same ones
/// <see cref="StreamedScopeEscapeDetector.FocusRootCalls"/> collects from: a grouping key, a sort
/// key, a predicate and the body of a loop have another focus, and keep their calls. An
/// instruction or expression with no such call is kept as it is; one with a call is copied with
/// its other properties.
/// </remarks>
internal static class RootOfDocumentRewrite
{
    /// <summary>The body of a rule that matches only document nodes and has no name, rewritten.</summary>
    internal static XsltSequenceConstructor Apply(XsltSequenceConstructor body)
    {
        var calls = StreamedScopeEscapeDetector.FocusRootCalls(body);
        return calls.Count == 0 ? body : new Rewriter(calls).Body(body)!;
    }

    private sealed class Rewriter(IReadOnlySet<XQueryExpression> calls) : XQueryExpressionRewriter
    {
        public override XQueryExpression VisitFunctionCallExpression(FunctionCallExpression expr)
            => calls.Contains(expr) ? ContextItemExpression.Instance : base.VisitFunctionCallExpression(expr);

        private XQueryExpression? Expr(XQueryExpression? expr) => expr is null ? null : Rewrite(expr);

        internal XsltSequenceConstructor? Body(XsltSequenceConstructor? body)
        {
            if (body is null)
                return null;
            List<XsltInstruction>? changed = null;
            for (var i = 0; i < body.Instructions.Count; i++)
            {
                var instruction = body.Instructions[i];
                var rewritten = Instruction(instruction);
                if (changed is null && !ReferenceEquals(rewritten, instruction))
                    changed = [.. body.Instructions.Take(i)];
                changed?.Add(rewritten);
            }
            return changed is null ? body : body.WithInstructions(changed);
        }

        private XsltAttributeValueTemplate? Avt(XsltAttributeValueTemplate? avt)
        {
            if (avt is null)
                return null;
            List<AvtPart>? changed = null;
            for (var i = 0; i < avt.Parts.Count; i++)
            {
                var part = avt.Parts[i];
                AvtPart rewritten = part is AvtExpression ae && Rewrite(ae.Expression) is var e && !ReferenceEquals(e, ae.Expression)
                    ? ae.WithExpression(e)
                    : part;
                if (changed is null && !ReferenceEquals(rewritten, part))
                    changed = [.. avt.Parts.Take(i)];
                changed?.Add(rewritten);
            }
            return changed is null ? avt : avt.WithParts(changed);
        }

        private static bool Same(object? a, object? b) => ReferenceEquals(a, b);

        private XsltInstruction Instruction(XsltInstruction instruction)
        {
            switch (instruction)
            {
                case XsltValueOf v:
                {
                    var (select, content, separator) = (Expr(v.Select), Body(v.Content), Avt(v.Separator));
                    return Same(select, v.Select) && Same(content, v.Content) && Same(separator, v.Separator)
                        ? v : v.WithOperands(select, content, separator);
                }
                case XsltSequence sq:
                {
                    var (select, content) = (Expr(sq.Select), Body(sq.Content));
                    return Same(select, sq.Select) && Same(content, sq.Content) ? sq : sq.WithOperands(select, content);
                }
                case XsltCopyOf co:
                {
                    var select = Rewrite(co.Select);
                    return Same(select, co.Select) ? co : co.WithSelect(select);
                }
                case XsltTextValueTemplate tvt:
                {
                    var template = Avt(tvt.Template)!;
                    return Same(template, tvt.Template) ? tvt : tvt.WithTemplate(template);
                }
                case XsltLiteralResultElement lre:
                {
                    Dictionary<QName, XsltAttributeValueTemplate>? attributes = null;
                    foreach (var (name, avt) in lre.Attributes)
                    {
                        var rewritten = Avt(avt)!;
                        if (!Same(rewritten, avt))
                            (attributes ??= new Dictionary<QName, XsltAttributeValueTemplate>(lre.Attributes))[name] = rewritten;
                    }
                    var content = Body(lre.Content)!;
                    return attributes is null && Same(content, lre.Content) ? lre : lre.WithOperands(attributes ?? lre.Attributes, content);
                }
                case XsltIf i:
                {
                    var (test, then) = (Rewrite(i.Test), Body(i.Then)!);
                    return Same(test, i.Test) && Same(then, i.Then) ? i : i.WithOperands(test, then);
                }
                case XsltChoose c:
                {
                    List<XsltWhen>? when = null;
                    for (var k = 0; k < c.When.Count; k++)
                    {
                        var w = c.When[k];
                        var (test, body) = (Rewrite(w.Test), Body(w.Body)!);
                        var rewritten = Same(test, w.Test) && Same(body, w.Body) ? w : w.WithOperands(test, body);
                        if (when is null && !Same(rewritten, w))
                            when = [.. c.When.Take(k)];
                        when?.Add(rewritten);
                    }
                    var otherwise = Body(c.Otherwise);
                    return when is null && Same(otherwise, c.Otherwise) ? c : c.WithOperands(when ?? c.When, otherwise);
                }
                case XsltVariableInstruction var:
                {
                    var (select, content) = (Expr(var.Select), Body(var.Content));
                    return Same(select, var.Select) && Same(content, var.Content) ? var : var.WithOperands(select, content);
                }
                // The select only: a sort key, a grouping key and the body have each selected item
                // as focus.
                case XsltApplyTemplates at:
                {
                    var select = Expr(at.Select);
                    return Same(select, at.Select) ? at : at.WithSelect(select);
                }
                case XsltForEach fe:
                {
                    var select = Rewrite(fe.Select);
                    return Same(select, fe.Select) ? fe : fe.WithSelect(select);
                }
                case XsltForEachGroup feg:
                {
                    var select = Rewrite(feg.Select);
                    return Same(select, feg.Select) ? feg : feg.WithSelect(select);
                }
                case XsltIterate it:
                {
                    var select = Rewrite(it.Select);
                    return Same(select, it.Select) ? it : it.WithSelect(select);
                }
                default:
                    return instruction;
            }
        }
    }
}
