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
        foreach (var template in StreamableModeRules(stylesheet))
        {
            var contextRoots = MatchesOnlyDocument(template.Match)
                ? ContextRootPaths(template.Body)
                : NoPaths;
            if (Escapes(template.Body, new HashSet<object>(ReferenceEqualityComparer.Instance), contextRoots))
                return true;
        }
        return false;
    }

    private static readonly HashSet<PathExpression> NoPaths = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Whether a match pattern matches document nodes only: <c>/</c>, <c>document-node()</c>,
    /// <c>document-node(element(x))</c>, with or without predicates.
    /// </summary>
    internal static bool MatchesOnlyDocument(XsltPattern? pattern)
        => pattern is PathPattern { Steps: [{ Axis: Axis.Self, NodeTest: KindTest { Kind: XdmNodeKind.Document },
            DescendantSeparator: false, IsRootFunction: false }] };

    /// <summary>
    /// The absolute paths of a rule for the document node that are evaluated with that document
    /// node as the context item. A leading "/" is <c>root(.)</c>, and where the context item is a
    /// document node it is that node itself (XSLT 3.0 §19.8.8.8, §19.8.9.18): <c>/root/item</c> and
    /// <c>//item</c> read the matched node's own subtree, as <c>root/item</c> and <c>.//item</c> do.
    /// Only places known to keep the rule's focus are visited. Anything else (a step, a predicate,
    /// the right of "!", the body of xsl:for-each, an inline function) has another focus, where "/"
    /// is the root of another node, so a path there still counts as leaving the match.
    /// </summary>
    private static HashSet<PathExpression> ContextRootPaths(XsltSequenceConstructor? body)
    {
        var found = new HashSet<PathExpression>(ReferenceEqualityComparer.Instance);
        AddFromBody(body, found);
        return found;
    }

    private static void AddFromBody(XsltSequenceConstructor? body, HashSet<PathExpression> found)
    {
        if (body is null)
            return;
        foreach (var instruction in body.Instructions)
        {
            switch (instruction)
            {
                case XsltValueOf v:
                    AddFromExpression(v.Select, found);
                    AddFromBody(v.Content, found);
                    AddFromAvt(v.Separator, found);
                    break;
                case XsltSequence sq:
                    AddFromExpression(sq.Select, found);
                    AddFromBody(sq.Content, found);
                    break;
                case XsltCopyOf co:
                    AddFromExpression(co.Select, found);
                    break;
                case XsltTextValueTemplate tvt:
                    AddFromAvt(tvt.Template, found);
                    break;
                case XsltLiteralResultElement lre:
                    foreach (var avt in lre.Attributes.Values)
                        AddFromAvt(avt, found);
                    AddFromBody(lre.Content, found);
                    break;
                case XsltIf i:
                    AddFromExpression(i.Test, found);
                    AddFromBody(i.Then, found);
                    break;
                case XsltChoose c:
                    foreach (var when in c.When)
                    {
                        AddFromExpression(when.Test, found);
                        AddFromBody(when.Body, found);
                    }
                    AddFromBody(c.Otherwise, found);
                    break;
                case XsltVariableInstruction var:
                    AddFromExpression(var.Select, found);
                    AddFromBody(var.Content, found);
                    break;
                // The select only: a sort key, a grouping key and the body are evaluated with
                // each selected item as focus.
                case XsltApplyTemplates at:
                    AddFromExpression(at.Select, found);
                    break;
                case XsltForEach fe:
                    AddFromExpression(fe.Select, found);
                    break;
                case XsltForEachGroup feg:
                    AddFromExpression(feg.Select, found);
                    break;
                case XsltIterate it:
                    AddFromExpression(it.Select, found);
                    break;
            }
        }
    }

    private static void AddFromAvt(XsltAttributeValueTemplate? avt, HashSet<PathExpression> found)
    {
        if (avt is null)
            return;
        foreach (var part in avt.Parts)
            if (part is AvtExpression ae)
                AddFromExpression(ae.Expression, found);
    }

    private static void AddFromExpression(XQueryExpression? expr, HashSet<PathExpression> found)
    {
        switch (expr)
        {
            case PathExpression { IsAbsolute: true, InitialExpression: null, Steps.Count: > 0 } path:
                // The steps after the "/" have each step's result as focus, and are not visited.
                // "/" alone selects the matched node itself and is left to the tree.
                found.Add(path);
                break;
            case PathExpression { IsAbsolute: false, InitialExpression: { } initial }:
                AddFromExpression(initial, found);
                break;
            case FunctionCallExpression fc:
                foreach (var arg in fc.Arguments)
                    AddFromExpression(arg, found);
                break;
            case BinaryExpression b:
                AddFromExpression(b.Left, found);
                AddFromExpression(b.Right, found);
                break;
            case UnaryExpression u:
                AddFromExpression(u.Operand, found);
                break;
            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    AddFromExpression(item, found);
                break;
            case StringConcatExpression sc:
                foreach (var operand in sc.Operands)
                    AddFromExpression(operand, found);
                break;
            case SimpleMapExpression sm:
                // The right operand has each item of the left as focus.
                AddFromExpression(sm.Left, found);
                break;
            case IfExpression ie:
                AddFromExpression(ie.Condition, found);
                AddFromExpression(ie.Then, found);
                AddFromExpression(ie.Else, found);
                break;
            case CastExpression ce:
                AddFromExpression(ce.Expression, found);
                break;
            case TreatExpression te:
                AddFromExpression(te.Expression, found);
                break;
            case InstanceOfExpression io:
                AddFromExpression(io.Expression, found);
                break;
        }
    }

    /// <summary>The template rules that take part in a mode declared streamable.</summary>
    internal static IEnumerable<XsltTemplate> StreamableModeRules(XsltStylesheet stylesheet)
    {
        var streamableModes = new HashSet<QName>();
        foreach (var (name, mode) in stylesheet.Modes)
            if (mode.Streamable)
                streamableModes.Add(name);
        if (streamableModes.Count == 0)
            yield break;

        var unnamed = new QName(NamespaceId.None, "");
        foreach (var template in stylesheet.Templates)
        {
            if (template.Match == null)
                continue;
            var inStreamableMode = template.Modes.Count == 0
                ? streamableModes.Contains(unnamed)
                : template.Modes.Any(m => m.Equals(TemplateIndex.AllModeSentinel)
                    || streamableModes.Contains(m.Equals(TemplateIndex.DefaultModeSentinel) ? unnamed : m));
            if (inStreamableMode)
                yield return template;
        }
    }

    /// <summary>Walks an instruction tree: its XPath expressions, AVT parts and nested instructions.</summary>
    private static bool Escapes(object? node, HashSet<object> seen, HashSet<PathExpression> contextRoots)
    {
        switch (node)
        {
            case null:
                return false;
            case XQueryExpression expr:
                return ExpressionEscapes(expr, contextRoots);
            case AvtExpression avt:
                return ExpressionEscapes(avt.Expression, contextRoots);
            case string or QName or ValueType:
                return false;
        }
        if (!seen.Add(node))
            return false;
        if (node is IEnumerable items)
        {
            foreach (var item in items)
                if (Escapes(item, seen, contextRoots))
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
            if (Escapes(property.GetValue(node), seen, contextRoots))
                return true;
        }
        return false;
    }

    private static bool ExpressionEscapes(XQueryExpression expr, HashSet<PathExpression> contextRoots)
    {
        var walker = new EscapeWalker(contextRoots);
        walker.Walk(expr);
        return walker.Escapes;
    }

    private sealed class EscapeWalker(HashSet<PathExpression> contextRoots) : XQueryExpressionWalker
    {
        public bool Escapes { get; private set; }

        public override object? VisitPathExpression(PathExpression expr)
        {
            if (expr.IsAbsolute && !contextRoots.Contains(expr))
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
