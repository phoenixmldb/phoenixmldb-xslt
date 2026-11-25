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
    /// <summary>The first read outside the match that was found: the rule, the expression and where it is.</summary>
    internal sealed record Escape(XsltTemplate Template, string Construct, SourceLocation? Location);

    private static readonly ConditionalWeakTable<XsltStylesheet, StrongBox<Escape?>> _cache = new();

    public static bool AnyStreamableRuleEscapesItsMatch(XsltStylesheet stylesheet) => FirstEscape(stylesheet) is not null;

    /// <summary>The first rule of a streamable mode that reads outside its match, or null.</summary>
    public static Escape? FirstEscape(XsltStylesheet stylesheet)
        => _cache.GetValue(stylesheet, s => new StrongBox<Escape?>(Compute(s))).Value;

    private static Escape? Compute(XsltStylesheet stylesheet)
    {
        foreach (var template in StreamableModeRules(stylesheet))
        {
            var contextRoots = MatchesOnlyDocument(template.Match)
                ? ContextRootPaths(template.Body)
                : NoPaths;
            var search = new Search(contextRoots);
            if (Escapes(template.Body, search, template.Body.Location))
                return new Escape(template, search.Construct ?? "an expression", search.Location ?? template.Body.Location);
        }
        return null;
    }

    /// <summary>The state of the walk over one rule, and what it found.</summary>
    private sealed class Search(HashSet<XQueryExpression> contextRoots)
    {
        public HashSet<XQueryExpression> ContextRoots { get; } = contextRoots;
        public HashSet<object> Seen { get; } = new(ReferenceEqualityComparer.Instance);
        public string? Construct { get; set; }
        public SourceLocation? Location { get; set; }

        /// <summary>Records the expression found, at the instruction that holds it, and returns true.</summary>
        public bool Found(XQueryExpression expr, SourceLocation? where)
        {
            Construct = expr.ToString();
            Location = where;
            return true;
        }
    }

    private static readonly HashSet<XQueryExpression> NoPaths = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The calls of root() and root(.) in a body that are evaluated with the body's own focus
    /// (see <see cref="ContextRootPaths"/>): where that focus is a document node, root() is it.
    /// </summary>
    internal static IReadOnlySet<XQueryExpression> FocusRootCalls(XsltSequenceConstructor? body)
    {
        var calls = new HashSet<XQueryExpression>(ReferenceEqualityComparer.Instance);
        foreach (var expr in ContextRootPaths(body))
            if (expr is FunctionCallExpression)
                calls.Add(expr);
        return calls;
    }

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
    private static HashSet<XQueryExpression> ContextRootPaths(XsltSequenceConstructor? body)
    {
        var found = new HashSet<XQueryExpression>(ReferenceEqualityComparer.Instance);
        AddFromBody(body, found);
        return found;
    }

    private static void AddFromBody(XsltSequenceConstructor? body, HashSet<XQueryExpression> found)
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

    private static void AddFromAvt(XsltAttributeValueTemplate? avt, HashSet<XQueryExpression> found)
    {
        if (avt is null)
            return;
        foreach (var part in avt.Parts)
            if (part is AvtExpression ae)
                AddFromExpression(ae.Expression, found);
    }

    private static void AddFromExpression(XQueryExpression? expr, HashSet<XQueryExpression> found)
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
            // root() and root(.) evaluated with the rule's focus (for the classifier, xslt#655).
            case FunctionCallExpression call when IsRootCall(call):
                found.Add(call);
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

    /// <summary>
    /// Walks an instruction tree: its XPath expressions, AVT parts and nested instructions.
    /// <paramref name="where"/> is the location of the innermost instruction, for an expression
    /// that has none of its own.
    /// </summary>
    private static bool Escapes(object? node, Search search, SourceLocation? where)
    {
        switch (node)
        {
            case null:
                return false;
            case XQueryExpression expr:
                return ExpressionEscapes(expr, search, where);
            // The value of an attribute value template is the string value of what it selects.
            case AvtExpression avt:
                return IsClimbing(avt.Expression) ? search.Found(avt.Expression, where)
                    : ExpressionEscapes(avt.Expression, search, where);
            case string or QName or ValueType:
                return false;
        }
        if (!search.Seen.Add(node))
            return false;
        // The attributes of a literal result element are a dictionary: its entries are value types,
        // which the case above skipped, so an expression in an attribute was not examined at all:
        // <b v="{last()}"/> streamed and gave 1 for every node (xslt#582).
        if (node is IDictionary dictionary)
        {
            foreach (var value in dictionary.Values)
                if (Escapes(value, search, where))
                    return true;
            return false;
        }
        if (node is IEnumerable items)
        {
            foreach (var item in items)
                if (Escapes(item, search, where))
                    return true;
            return false;
        }
        if (node is not (XsltInstruction or XsltAttributeValueTemplate or XsltWithParam or XsltSort
            or XsltWhen or XsltParam))
            return false;
        if (node is XsltInstruction { Location: { Line: > 0 } location })
            where = location;
        // The body of xsl:source-document reads another document, with that document as its focus:
        // "/" there is its root. Only the href is evaluated with the rule's focus. The body is checked
        // when the instruction runs (StrictStreamability). Taken as a read outside the match, it sent
        // the principal input to the tree, and under the strict check was phx:XTSE9430 (W3C
        // si-group-051, -052).
        if (node is XsltSourceDocument sourceDocument)
            return Escapes(sourceDocument.Href, search, where);
        // These take the string value of what they select, or copy it.
        if (node switch
            {
                XsltValueOf v => v.Select,
                XsltCopyOf c => c.Select,
                XsltAttribute a => a.Select,
                XsltComment c => c.Select,
                XsltProcessingInstruction pi => pi.Select,
                _ => null,
            } is { } absorbed && IsClimbing(absorbed))
            return search.Found(absorbed, where);
        foreach (var property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
                continue;
            if (Escapes(property.GetValue(node), search, where))
                return true;
        }
        return false;
    }

    private static bool ExpressionEscapes(XQueryExpression expr, Search search, SourceLocation? where)
    {
        var walker = new EscapeWalker(search.ContextRoots);
        walker.Walk(expr);
        return walker.Escapes && search.Found(walker.Offender ?? expr, where);
    }

    /// <summary>
    /// Whether an expression selects an ancestor of the context node, as a climb: <c>..</c>,
    /// <c>ancestor::x</c>, <c>root()</c>, with no step down or to an attribute after it. Its value
    /// (string(..), data(ancestor::x), .. = 'a') is the content of that ancestor: what the
    /// streamed input has passed or not yet reached. XSLT 3.0 §19.8.1 makes a climbing operand
    /// with usage absorption free-ranging. Streamed, string(..) gave "" (xslt#582).
    /// </summary>
    internal static bool IsClimbing(XQueryExpression? expr) => expr switch
    {
        StepExpression step => step.Axis is Axis.Parent or Axis.Ancestor or Axis.AncestorOrSelf
            && step.NodeTest is not KindTest { Kind: XdmNodeKind.Attribute or XdmNodeKind.Namespace or XdmNodeKind.Text
                or XdmNodeKind.Comment or XdmNodeKind.ProcessingInstruction },
        PathExpression { IsAbsolute: false, Steps.Count: > 0 } path
            when path.InitialExpression is null or ContextItemExpression || IsRootCall(path.InitialExpression) =>
            path.Steps.All(s => s is StepExpression { Axis: Axis.Parent or Axis.Ancestor or Axis.AncestorOrSelf or Axis.Self })
            && (IsClimbing(path.Steps[^1]) || (path.Steps[^1] is StepExpression { Axis: Axis.Self } && (IsRootCall(path.InitialExpression)
                || path.Steps.Any(s => s is StepExpression { Axis: Axis.Parent or Axis.Ancestor or Axis.AncestorOrSelf })))),
        PathExpression { IsAbsolute: false, Steps.Count: 0, InitialExpression: { } initial } => IsClimbing(initial),
        FunctionCallExpression => IsRootCall(expr),
        FilterExpression filter => IsClimbing(filter.Primary),
        _ => false,
    };

    private static bool IsRootCall(XQueryExpression? expr)
        => expr is FunctionCallExpression { Name.LocalName: "root" } call && call.Arguments is [] or [ContextItemExpression];

    // Functions that only inspect a node given as their argument (§19.8.9: operand usage "I"): of a
    // node already open above the matched one, they read nothing that has passed.
    private static readonly HashSet<string> InspectingFunctions = new(StringComparer.Ordinal)
    {
        "base-uri", "document-uri", "exists", "empty", "boolean", "not", "name", "local-name",
        "namespace-uri", "node-name", "generate-id", "has-children", "nilled",
    };

    private sealed class EscapeWalker(HashSet<XQueryExpression> contextRoots) : XQueryExpressionWalker
    {
        public bool Escapes { get; private set; }

        /// <summary>The first expression that reads outside the match.</summary>
        public XQueryExpression? Offender { get; private set; }

        private void Escape(XQueryExpression expr)
        {
            if (!Escapes)
                Offender = expr;
            Escapes = true;
        }

        // The root() calls whose result is only inspected, or climbed from: they stream.
        private readonly HashSet<XQueryExpression> _inspectedRoots = new(ReferenceEqualityComparer.Instance);

        private static bool IsRootOfContext(XQueryExpression? expr)
            => expr is FunctionCallExpression { Name.LocalName: "root" } call
                && call.Arguments is [] or [ContextItemExpression];

        public override object? VisitPathExpression(PathExpression expr)
        {
            if (expr.IsAbsolute && !contextRoots.Contains(expr))
                Escape(expr);
            // Climbing, then descending again: ../* reads the parent's other children. root() is a
            // climb to the document node (XSLT 3.0 §19.8.9.18: head(ancestor-or-self::node())), so
            // root()/x and root()//x read outside the match as /x does.
            var climbed = false;
            if (IsRootOfContext(expr.InitialExpression))
            {
                _inspectedRoots.Add(expr.InitialExpression!);
                climbed = true;
            }
            foreach (var step in expr.Steps)
            {
                if (step is not StepExpression st)
                    continue;
                if (st.Axis is Axis.Parent or Axis.Ancestor or Axis.AncestorOrSelf)
                    climbed = true;
                else if (climbed && st.Axis is not (Axis.Self or Axis.Attribute or Axis.Namespace))
                    Escape(expr);
            }
            return base.VisitPathExpression(expr);
        }

        public override object? VisitStepExpression(StepExpression expr)
        {
            if (expr.Axis is Axis.PrecedingSibling or Axis.FollowingSibling or Axis.Preceding or Axis.Following)
                Escape(expr);
            return base.VisitStepExpression(expr);
        }

        public override object? VisitBinaryExpression(BinaryExpression expr)
        {
            // A node comparison inspects its operands (§19.8.8: is, <<, >>): root() is root(..).
            if (expr.Operator is BinaryOperator.Is or BinaryOperator.Precedes or BinaryOperator.Follows)
            {
                if (IsRootOfContext(expr.Left))
                    _inspectedRoots.Add(expr.Left);
                if (IsRootOfContext(expr.Right))
                    _inspectedRoots.Add(expr.Right);
            }
            // The other operators that take values atomize their operands: .. = 'a' reads the
            // content of the parent. and, or and the set operators do not.
            else if (expr.Operator is not (BinaryOperator.And or BinaryOperator.Or or BinaryOperator.Union
                or BinaryOperator.Intersect or BinaryOperator.Except))
            {
                if (IsClimbing(expr.Left))
                    Escape(expr.Left);
                else if (IsClimbing(expr.Right))
                    Escape(expr.Right);
            }
            return base.VisitBinaryExpression(expr);
        }

        public override object? VisitInstanceOfExpression(InstanceOfExpression expr)
        {
            // The type of a node is known when it opens (root() instance of document-node()).
            if (IsRootOfContext(expr.Expression))
                _inspectedRoots.Add(expr.Expression);
            return base.VisitInstanceOfExpression(expr);
        }

        public override object? VisitUnaryExpression(UnaryExpression expr)
        {
            if (IsClimbing(expr.Operand))
                Escape(expr.Operand);
            return base.VisitUnaryExpression(expr);
        }

        public override object? VisitCastExpression(CastExpression expr)
        {
            if (IsClimbing(expr.Expression))
                Escape(expr.Expression);
            return base.VisitCastExpression(expr);
        }

        public override object? VisitStringConcatExpression(StringConcatExpression expr)
        {
            foreach (var operand in expr.Operands)
                if (IsClimbing(operand))
                    Escape(operand);
            return base.VisitStringConcatExpression(expr);
        }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (expr.Name.LocalName is "last")
                Escape(expr);
            else if (expr.Name.LocalName is "root")
            {
                // root() streams where its node is only inspected (base-uri(root()), exists(root())),
                // as name(..) does: the node is open. Anywhere else (its string value, a step down
                // from it) it reads what has passed or is still to come.
                if (!_inspectedRoots.Contains(expr) || expr.Arguments is not ([] or [ContextItemExpression]))
                    Escape(expr);
            }
            else if (InspectingFunctions.Contains(expr.Name.LocalName))
            {
                foreach (var argument in expr.Arguments)
                    if (IsRootOfContext(argument))
                        _inspectedRoots.Add(argument);
            }
            // An argument that the function absorbs (§19.8.9: string(..), data(ancestor::x)).
            if (AbsorbedArguments(expr) is { } usages)
            {
                for (var i = 0; i < expr.Arguments.Count && i < usages.Length; i++)
                    if (usages[i] == 'A' && IsClimbing(expr.Arguments[i]))
                    {
                        Escape(expr.Arguments[i]);
                        break;
                    }
            }
            return base.VisitFunctionCallExpression(expr);
        }

        /// <summary>The operand usages of a built-in function of fn:, as XSLT 3.0 §19.8.9 lists them, or null.</summary>
        private static string? AbsorbedArguments(FunctionCallExpression call)
        {
            if (!FunctionNames.IsFn(call.Name))
                return null;
            var count = call.Arguments.Count;
            if (call.Name.LocalName == "concat")
                return new string('A', count);
            return Streamability.BuiltInFunctionUsages.ByNameAndArity.GetValueOrDefault(
                "fn:" + call.Name.LocalName + "#" + count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
