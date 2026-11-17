using System.Runtime.CompilerServices;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// The template rules of a streamable mode that the engine runs although they are not
/// guaranteed-streamable (XSLT 3.0 §19): it buffers the matched element's subtree, or evaluates
/// the transformation on a tree, and the result is right. XSLT 3.0 §19.10 allows that only where
/// the user has asked for it; a processor is otherwise to report XTSE3430. Under
/// <see cref="XsltTransformOptions.StrictStreamability"/> the engine reports it (xslt#295, #298).
/// </summary>
/// <remarks>
/// <para>The answer comes from <see cref="StreamabilityClassifier"/> in its by-specification
/// mode: the posture and sweep rules of §19.8, with the operand usages of the built-in functions
/// as the specification lists them. A body is accepted when it is grounded, and motionless or
/// consuming. Forced on for the W3C suite, the check refuses no stylesheet that a test expects
/// to run.</para>
/// <para>It accepts a little more than the specification guarantees, where the W3C suite expects
/// a result and the engine gives the right one (§19.10 allows a processor to stream more): a
/// union of two striding operands is striding; one branch of xsl:fork may return streamed nodes
/// when no other branch consumes; current-group() of a grounded population is grounded wherever
/// it is called; a motionless operand of a comma that is not grounded (@a in (@a, foo)) is not
/// counted with the consuming ones; a path that starts with current() has the posture of the
/// context item also inside a predicate. It does not check that the body of a stylesheet function keeps to its declared
/// streamability category, nor that xsl:apply-templates names a streamable mode; other checks
/// of the stylesheet report those.</para>
/// <para>A rule that reads outside the node it matched (xslt#298) is found by the same rules: a
/// step down from a parent, an ancestor or the root is roaming and free-ranging.
/// <see cref="StreamedScopeEscapeDetector"/> is not used for the error: it is over-inclusive by
/// design, which suits a fallback to the tree.</para>
/// </remarks>
internal static class StrictStreamability
{
    private static readonly ConditionalWeakTable<XsltStylesheet, StrongBox<XsltException?>> _cache = new();
    private static readonly ConditionalWeakTable<XsltStylesheet, StrongBox<XsltException?>> _certainCache = new();

    /// <summary>
    /// The XTSE3430 error for the first such rule, or null when none is found. With
    /// <paramref name="certainOnly"/>, only a rule that the modelled rules of §19.8 find not
    /// guaranteed-streamable, with no construct in the verdict that the classifier does not
    /// model: that is the check the engine makes by default.
    /// </summary>
    public static XsltException? Finding(XsltStylesheet stylesheet, bool certainOnly = false)
        => (certainOnly ? _certainCache : _cache)
            .GetValue(stylesheet, s => new StrongBox<XsltException?>(Compute(s, certainOnly))).Value;

    private static readonly ConditionalWeakTable<XsltSourceDocument, StrongBox<string?>> _bodies = new();
    private static readonly ConditionalWeakTable<XsltSourceDocument, StrongBox<string?>> _certainBodies = new();

    /// <summary>
    /// The XTSE3430 message for the body of an xsl:source-document with streamable="yes" that is
    /// not guaranteed-streamable, or null. The caller reports it when the instruction runs.
    /// <paramref name="certainOnly"/> as for the template rules.
    /// </summary>
    public static string? Finding(XsltStylesheet stylesheet, XsltSourceDocument instruction, bool certainOnly = false)
        => instruction is { Streamable: true, Content: { } body }
            ? (certainOnly ? _certainBodies : _bodies).GetValue(instruction, _ => new StrongBox<string?>(
                // The context item of the body is the document node (§18.1).
                Reason(body, stylesheet, certainOnly, childType: 'A',
                    outerGroupIsGrounded: EnclosingGroupIsGrounded(stylesheet, instruction)) is { } reason
                    ? "XTSE3430: The body of xsl:source-document is not guaranteed streamable: " + reason + "." + Remedy
                    : null)).Value
            : null;

    /// <summary>The way to run such a construct anyway, which every message of this check names.</summary>
    internal const string Remedy =
        " To run it with a buffer instead of streaming it, set StreamingFallback (xslt command line: --streaming-fallback).";

    private static XsltException? Compute(XsltStylesheet stylesheet, bool certainOnly)
    {
        foreach (var template in StreamedScopeEscapeDetector.StreamableModeRules(stylesheet))
        {
            const string Subject = "XTSE3430: The body of this template in a streamable mode is not guaranteed streamable: ";
            var reason = Reason(template.Body, stylesheet, certainOnly, ContextChildType(template.Match), MatchesNoDocument(template.Match));
            // The opt-in check also reports what its two detectors find; the default check
            // reports only what the classifier is certain of, and uses them for the message.
            if (certainOnly && reason is null)
                continue;
            // The two commonest causes have a message of their own.
            if (StreamabilityChecker.HasSeveralConsumingOperands(template.Body))
                return new XsltException(
                    Subject + "more than one instruction reads the children of the matched node, and the streamed input passes once." + Remedy,
                    template.Body.Location);
            if (RoamingAxisDetector.First(template.Body) is { } roaming)
                return new XsltException(
                    Subject + $"the {AxisName(roaming.Axis)} axis from the matched node reads what the streamed input has passed or not yet reached." + Remedy,
                    roaming.Location ?? template.Body.Location);
            if (reason is not null)
                return new XsltException(Subject + reason + "." + Remedy, template.Body.Location);
        }
        return null;
    }

    /// <summary>
    /// The static type of the context item of a template rule, from its match pattern
    /// (§19.1): 'I' where every node it can match has no children (text, comment, processing
    /// instruction, attribute, namespace), 'A' where it can match an element or a document
    /// node, 'a' where the classifier cannot tell.
    /// </summary>
    internal static char ContextChildType(XsltPattern? pattern)
    {
        switch (pattern)
        {
            case UnionPattern union:
            {
                var type = 'I';
                foreach (var alternative in union.Patterns)
                {
                    var one = ContextChildType(alternative);
                    if (one == 'A')
                        return 'A';
                    if (one == 'a')
                        type = 'a';
                }
                return type;
            }
            case IntersectPattern intersect:
            {
                var left = ContextChildType(intersect.Left);
                var right = ContextChildType(intersect.Right);
                return left == 'I' || right == 'I' ? 'I' : left == 'A' && right == 'A' ? 'A' : 'a';
            }
            case ExceptPattern except:
                return ContextChildType(except.Left) == 'I' ? 'I' : 'a';
            case PathPattern { Steps.Count: 0 }:
                return 'A';
            case PathPattern { Steps: var steps }:
            {
                var last = steps[^1];
                if (last.IsRootFunction)
                    return 'A';
                if (last.Axis is PhoenixmlDb.XQuery.Ast.Axis.Attribute or PhoenixmlDb.XQuery.Ast.Axis.Namespace)
                    return 'I';
                return last.NodeTest switch
                {
                    PhoenixmlDb.XQuery.Ast.KindTest { Kind: PhoenixmlDb.Core.XdmNodeKind.Text or PhoenixmlDb.Core.XdmNodeKind.Comment
                        or PhoenixmlDb.Core.XdmNodeKind.ProcessingInstruction or PhoenixmlDb.Core.XdmNodeKind.Attribute
                        or PhoenixmlDb.Core.XdmNodeKind.Namespace } => 'I',
                    PhoenixmlDb.XQuery.Ast.NameTest => 'A',
                    PhoenixmlDb.XQuery.Ast.KindTest { Kind: PhoenixmlDb.Core.XdmNodeKind.Element or PhoenixmlDb.Core.XdmNodeKind.Document
                        or PhoenixmlDb.Core.XdmNodeKind.None } => 'A',
                    _ => 'a',
                };
            }
            default:
                return 'a';
        }
    }

    /// <summary>
    /// Whether a match pattern is certain to match no document node: its last step is a name
    /// test on the child or the attribute axis. Any other pattern may match one, and a leading
    /// "/" in the body is then taken to be the context node (§19.8.8.8).
    /// </summary>
    private static bool MatchesNoDocument(XsltPattern? pattern)
        => pattern is PathPattern { Steps: { Count: > 0 } steps }
            && steps[^1] is { IsRootFunction: false, Axis: PhoenixmlDb.XQuery.Ast.Axis.Child or PhoenixmlDb.XQuery.Ast.Axis.Attribute, NodeTest: PhoenixmlDb.XQuery.Ast.NameTest };

    /// <summary>
    /// Why a body with a streamed context node is not guaranteed-streamable, or null when it is
    /// (or, with <paramref name="certainOnly"/>, when the classifier is not certain it is not).
    /// <paramref name="childType"/> is the static type of the context item, as
    /// <see cref="ContextChildType"/> gives it.
    /// </summary>
    /// <remarks>
    /// <c>outerGroupIsGrounded</c>: the body is that of an xsl:source-document inside an
    /// xsl:for-each-group whose population is grounded (copies), so current-group() there,
    /// outside any xsl:for-each-group in the body, is held in memory and does not read the
    /// streamed document (W3C si-group-051, which Saxon-EE passes; with a streamed population it
    /// is XTSE3430, si-group-052).
    /// </remarks>
    private static string? Reason(XsltSequenceConstructor body, XsltStylesheet stylesheet, bool certainOnly, char childType,
        bool contextIsNeverDocument = false, bool outerGroupIsGrounded = false)
    {
        var context = new StreamingContext(
            Posture.Striding, InStreamedScope: true, BySpec: true, Functions: stylesheet.Functions, Stylesheet: stylesheet,
            ContextHasNoChildren: childType == 'I', ContextIsNeverDocument: contextIsNeverDocument,
            InGroundedGroup: outerGroupIsGrounded)
        {
            ContextMayHaveChildren = childType == 'A',
            ForTheCheck = true,
        };
        var result = StreamabilityClassifier.ClassifyWithCertainty(() => StreamabilityClassifier.Classify(body, context), out var certain);
        if (certainOnly && !certain)
            return null;
        if (result.Sweep == Sweep.FreeRanging || result.Posture == Posture.Roaming)
            return "it needs parts of the streamed input that have passed, or more than one pass (XSLT 3.0 section 19.8: roaming and free-ranging)";
        if (result.Posture != Posture.Grounded)
            return "it returns nodes of the streamed input; copy them (xsl:copy-of, copy-of(), snapshot()) or return their values";
        return null;
    }

    private static readonly ConditionalWeakTable<XsltStylesheet, Dictionary<XsltSourceDocument, bool>> _groundedGroups = new();

    /// <summary>
    /// Whether the nearest xsl:for-each-group that contains this xsl:source-document selects
    /// grounded items, so that its current group is held in memory. False where there is none.
    /// </summary>
    private static bool EnclosingGroupIsGrounded(XsltStylesheet stylesheet, XsltSourceDocument instruction)
    {
        var map = _groundedGroups.GetValue(stylesheet, s =>
        {
            var found = new Dictionary<XsltSourceDocument, bool>(ReferenceEqualityComparer.Instance);
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var template in s.Templates)
                CollectGroups(template.Body, null, s, seen, found);
            foreach (var template in s.NamedTemplates.Values)
                CollectGroups(template.Body, null, s, seen, found);
            foreach (var function in s.Functions.Values)
                CollectGroups(function.Body, null, s, seen, found);
            return found;
        });
        return map.TryGetValue(instruction, out var grounded) && grounded;
    }

    private static void CollectGroups(object? node, XsltForEachGroup? group, XsltStylesheet stylesheet, HashSet<object> seen,
        Dictionary<XsltSourceDocument, bool> found)
    {
        if (node is null or string or ValueType or PhoenixmlDb.XQuery.Ast.XQueryExpression)
            return;
        if (!seen.Add(node))
            return;
        if (node is System.Collections.IEnumerable items)
        {
            foreach (var item in items)
                CollectGroups(item, group, stylesheet, seen, found);
            return;
        }
        if (node is not (XsltInstruction or XsltWhen))
            return;
        if (node is XsltSourceDocument document)
            found[document] = group is not null && StreamabilityClassifier.Classify(group.Select, new StreamingContext(
                Posture.Striding, InStreamedScope: true, BySpec: true, Functions: stylesheet.Functions, Stylesheet: stylesheet)).Posture == Posture.Grounded;
        if (node is XsltForEachGroup nested)
            group = nested;
        foreach (var property in node.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0)
                CollectGroups(property.GetValue(node), group, stylesheet, seen, found);
        }
    }

    /// <summary>
    /// Every xsl:source-document with streamable="yes" in a template or a stylesheet function,
    /// at any depth. Its body is a streamed scope of its own (§18.1).
    /// </summary>
    internal static List<XsltSourceDocument> StreamableSourceDocuments(XsltStylesheet stylesheet)
    {
        var found = new List<XsltSourceDocument>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var template in stylesheet.Templates)
            Collect(template.Body, seen, found);
        foreach (var function in stylesheet.Functions.Values)
            Collect(function.Body, seen, found);
        return found;
    }

    private static void Collect(object? node, HashSet<object> seen, List<XsltSourceDocument> found)
    {
        if (node is null or string or ValueType or PhoenixmlDb.XQuery.Ast.XQueryExpression)
            return;
        if (!seen.Add(node))
            return;
        if (node is System.Collections.IEnumerable items)
        {
            foreach (var item in items)
                Collect(item, seen, found);
            return;
        }
        if (node is not (XsltInstruction or XsltWhen))
            return;
        if (node is XsltSourceDocument { Streamable: true } document)
            found.Add(document);
        foreach (var property in node.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0)
                Collect(property.GetValue(node), seen, found);
        }
    }

    private static string AxisName(PhoenixmlDb.XQuery.Ast.Axis axis) => axis switch
    {
        PhoenixmlDb.XQuery.Ast.Axis.Preceding => "preceding",
        PhoenixmlDb.XQuery.Ast.Axis.PrecedingSibling => "preceding-sibling",
        PhoenixmlDb.XQuery.Ast.Axis.Following => "following",
        _ => "following-sibling",
    };
}
