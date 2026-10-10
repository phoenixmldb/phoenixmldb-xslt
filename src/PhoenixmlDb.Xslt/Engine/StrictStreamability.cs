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
/// it is called. It does not check that the body of a stylesheet function keeps to its declared
/// streamability category, nor that xsl:apply-templates names a streamable mode; other checks
/// of the stylesheet report those.</para>
/// <para>A rule that reads outside the node it matched (<see cref="StreamedScopeEscapeDetector"/>,
/// xslt#298) is a separate question: that detector is over-inclusive by design, which suits a
/// fallback to the tree and not an error.</para>
/// </remarks>
internal static class StrictStreamability
{
    private static readonly ConditionalWeakTable<XsltStylesheet, StrongBox<XsltException?>> _cache = new();

    /// <summary>The XTSE3430 error for the first such rule, or null when none is found.</summary>
    public static XsltException? Finding(XsltStylesheet stylesheet)
        => _cache.GetValue(stylesheet, s => new StrongBox<XsltException?>(Compute(s))).Value;

    private static readonly ConditionalWeakTable<XsltSourceDocument, StrongBox<string?>> _bodies = new();

    /// <summary>
    /// The XTSE3430 message for the body of an xsl:source-document with streamable="yes" that is
    /// not guaranteed-streamable, or null. The caller reports it when the instruction runs.
    /// </summary>
    public static string? Finding(XsltStylesheet stylesheet, XsltSourceDocument instruction)
        => instruction is { Streamable: true, Content: { } body }
            ? _bodies.GetValue(instruction, _ => new StrongBox<string?>(
                Reason(body, stylesheet) is { } reason
                    ? "XTSE3430: The body of xsl:source-document is not guaranteed streamable: " + reason
                    : null)).Value
            : null;

    private static XsltException? Compute(XsltStylesheet stylesheet)
    {
        foreach (var template in StreamedScopeEscapeDetector.StreamableModeRules(stylesheet))
        {
            const string Subject = "XTSE3430: The body of this template in a streamable mode is not guaranteed streamable: ";
            // The two commonest causes have a message of their own.
            if (StreamabilityChecker.HasSeveralConsumingOperands(template.Body))
                return new XsltException(
                    Subject + "more than one instruction reads the children of the matched node, and the streamed input passes once",
                    template.Body.Location);
            if (RoamingAxisDetector.First(template.Body) is { } roaming)
                return new XsltException(
                    Subject + $"the {AxisName(roaming.Axis)} axis from the matched node reads what the streamed input has passed or not yet reached",
                    roaming.Location ?? template.Body.Location);
            if (Reason(template.Body, stylesheet) is { } reason)
                return new XsltException(Subject + reason, template.Body.Location);
        }
        return null;
    }

    /// <summary>Why a body with a streamed context node is not guaranteed-streamable, or null when it is.</summary>
    private static string? Reason(XsltSequenceConstructor body, XsltStylesheet stylesheet)
    {
        var result = StreamabilityClassifier.Classify(body, new StreamingContext(
            Posture.Striding, InStreamedScope: true, BySpec: true, Functions: stylesheet.Functions, Stylesheet: stylesheet));
        if (result.Sweep == Sweep.FreeRanging || result.Posture == Posture.Roaming)
            return "it needs parts of the streamed input that have passed, or more than one pass (XSLT 3.0 section 19.8: roaming and free-ranging)";
        if (result.Posture != Posture.Grounded)
            return "it returns nodes of the streamed input; copy them (xsl:copy-of, copy-of(), snapshot()) or return their values";
        return null;
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
