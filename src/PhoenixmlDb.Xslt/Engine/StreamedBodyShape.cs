using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// The shapes of body that the streaming executor runs on the live reader (xslt#340, xslt#343).
/// A body of any other shape runs on a buffered copy: of the matched element for a template
/// rule, of the whole input for a body whose context is the document node.
/// </summary>
/// <remarks>
/// <para>
/// The executor reads the streamed input for an instruction that walks it, for a loop or a
/// mapping the scanner registers, and for an aggregate a watcher computes during the pass.
/// It reads it for nothing else. An expression that reads the input with none of these to
/// carry it out is evaluated against a node with no children, and gives a wrong result with
/// no error. So a body streams only when one such mechanism does all its reading and the
/// rest of the body, by the rules of the specification, reads nothing below the context node.
/// </para>
/// <para>
/// The specification allows much more to stream than this. The list is what is shown to give
/// the result the tree gives: <c>StreamedShapeParityTests</c> runs every walking instruction
/// in every position the list allows, at every place a body can run. A shape is added here
/// together with its cases there.
/// </para>
/// </remarks>
internal static class StreamedBodyShape
{
    private static readonly ConditionalWeakTable<XsltSequenceConstructor, StrongBox<bool>> _provenForElement = new();

    /// <summary>
    /// True when the body of a template rule is of a shape the executor is proven to run
    /// right on the live reader, with the matched element not yet read: at most one instruction
    /// walks the children, in a form and a position the parity tests cover, and nothing else in
    /// the body reads below the matched element. Any other body is run on a buffered copy of
    /// the element, which gives the result the tree gives.
    /// </summary>
    /// <remarks>
    /// The executor reads the children for an instruction that walks them, and for nothing
    /// else. A read with nothing to carry it out sees an element with no children and gives a
    /// wrong result with no error (xslt#340, xslt#343). The specification allows far more than
    /// this list; the list is what has been shown to work, and it grows with the tests that
    /// show more (<c>StreamedShapeParityTests</c>).
    /// </remarks>
    public static bool IsProvenForElement(XsltSequenceConstructor body, XsltStylesheet stylesheet)
        => _provenForElement.GetValue(body, b => new StrongBox<bool>(ProvenForElement(b, stylesheet))).Value;

    private static bool ProvenForElement(XsltSequenceConstructor body, XsltStylesheet stylesheet)
    {
        var walkers = new List<XsltInstruction>();
        CollectWalkers(body, walkers);
        if (walkers.Count > 1)
            return false;
        var context = new Streamability.StreamingContext(
            Streamability.Posture.Striding, InStreamedScope: true, BySpec: true,
            Functions: stylesheet.Functions, Stylesheet: stylesheet,
            Handled: new HashSet<object>(walkers, ReferenceEqualityComparer.Instance));
        foreach (var walker in walkers)
        {
            if (!OperandsReadNothing(walker, context))
                return false;
        }
        return Streamability.StreamabilityClassifier.Classify(body, context).Sweep == Streamability.Sweep.Motionless;
    }

    private static readonly ConditionalWeakTable<XsltSequenceConstructor, StrongBox<bool>> _provenForDocument = new();

    /// <summary>
    /// True when a body whose context is the streamed document node (a rule for the document
    /// node, or the content of xsl:source-document) is of a shape the executor is proven to
    /// run right on the live reader. Any other body is run on the whole input, read into
    /// memory.
    /// </summary>
    /// <remarks>
    /// Three things read the input for such a body: one xsl:apply-templates, one streamed loop
    /// or mapping that the scanner registers, or the watchers that compute its aggregates
    /// during the pass. Whatever else the body reads, it reads from a document node with no
    /// children, and gives a wrong result with no error (xslt#343).
    /// </remarks>
    public static bool IsProvenForDocument(XsltSequenceConstructor body, XsltStylesheet stylesheet)
        => _provenForDocument.GetValue(body, b => new StrongBox<bool>(ProvenForDocument(b, stylesheet))).Value;

    private static bool ProvenForDocument(XsltSequenceConstructor body, XsltStylesheet stylesheet)
    {
        var scan = new StreamingExpressionScanner().ScanWithSubscriptions(body);
        var watchers = scan.Watchers ?? [];
        var subscriptions = scan.Subscriptions ?? [];
        var applies = new List<XsltInstruction>();
        CollectDocumentApplyTemplates(body, applies);

        // One mechanism at a time, as the dispatch runs them.
        if (applies.Count > 1 || subscriptions.Count > 1)
            return false;
        var mechanisms = (applies.Count > 0 ? 1 : 0) + (subscriptions.Count > 0 ? 1 : 0) + (watchers.Count > 0 ? 1 : 0);
        if (mechanisms > 1)
            return false;

        var handled = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var watcher in watchers)
            handled.Add(watcher.SourceExpression);
        foreach (var subscription in subscriptions)
        {
            object? source = subscription.SourceInstruction is { } loop ? loop : subscription.SourceExpression;
            if (source is null)
                return false;
            handled.Add(source);
        }
        handled.UnionWith(applies);

        var context = new Streamability.StreamingContext(
            Streamability.Posture.Striding, InStreamedScope: true, BySpec: true,
            Functions: stylesheet.Functions, Stylesheet: stylesheet, Handled: handled);
        foreach (var apply in applies)
        {
            if (!OperandsReadNothing(apply, context))
                return false;
        }
        return Streamability.StreamabilityClassifier.Classify(body, context).Sweep == Streamability.Sweep.Motionless;
    }

    private static void CollectDocumentApplyTemplates(XsltInstruction? node, List<XsltInstruction> applies)
    {
        switch (node)
        {
            case XsltApplyTemplates apply when apply.Sorts.Count == 0
                && (apply.Select is null
                    || DefaultXsltExecutionContext.IsChildNodeSelect(apply.Select)
                    || DefaultXsltExecutionContext.IsSelfContextSelect(apply.Select)
                    || DefaultXsltExecutionContext.IsDocumentLevelStridingSelect(apply.Select)
                    || DefaultXsltExecutionContext.TryGetStridingDescentSteps(apply.Select) is not null):
            // The whole document, sent on from the reader as it is.
            case XsltCopyOf copyOf when DefaultXsltExecutionContext.IsConsumingChildSelect(copyOf.Select)
                || DefaultXsltExecutionContext.IsSelfContextSelect(copyOf.Select):
                applies.Add(node);
                break;
            default:
                foreach (var content in WrappedContent(node))
                    CollectDocumentApplyTemplates(content, applies);
                break;
        }
    }

    /// <summary>
    /// The content of an instruction that only writes around what is in it, or chooses whether
    /// to run it: where a walking instruction may stand and still be the one that reads the
    /// input for the body.
    /// </summary>
    private static IEnumerable<XsltInstruction> WrappedContent(XsltInstruction? node)
    {
        switch (node)
        {
            case XsltSequenceConstructor constructor:
                foreach (var instruction in constructor.Instructions)
                    yield return instruction;
                break;
            case XsltLiteralResultElement element:
                yield return element.Content;
                break;
            case XsltCopy { Select: null, Content: { } content }:
                yield return content;
                break;
            case XsltElement element:
                yield return element.Content;
                break;
            case XsltResultDocument resultDocument:
                yield return resultDocument.Content;
                break;
            case XsltWherePopulated wherePopulated:
                yield return wherePopulated.Content;
                break;
            case XsltVariableInstruction { Content: { } content }:
                yield return content;
                break;
            case XsltTry { Body: { } tried }:
                yield return tried;
                break;
            case XsltIf conditional:
                yield return conditional.Then;
                break;
            case XsltChoose choose:
                foreach (var when in choose.When)
                    yield return when.Body;
                if (choose.Otherwise is { } otherwise)
                    yield return otherwise;
                break;
        }
    }

    /// <summary>
    /// The instructions of the body that walk the children of the matched element, found
    /// through the instructions that only write around them. A walking instruction anywhere
    /// else (in a loop, in a variable, in a function argument) is not collected, so the
    /// classifier counts what it reads against the body.
    /// </summary>
    private static void CollectWalkers(XsltInstruction? node, List<XsltInstruction> walkers)
    {
        switch (node)
        {
            // A single child step only. A path of child steps (chapter/title) has a route of
            // its own in the executor, and the parity cases for it fail: it is buffered.
            case XsltApplyTemplates apply when apply.Sorts.Count == 0
                && DefaultXsltExecutionContext.IsConsumingChildSelect(apply.Select):
            case XsltForEach forEach when forEach.Sorts.Count == 0
                && DefaultXsltExecutionContext.IsConsumingChildSelect(forEach.Select):
            case XsltIterate iterate when DefaultXsltExecutionContext.IsConsumingChildSelect(iterate.Select):
            case XsltForEachGroup group when group.Sorts.Count == 0
                && StreamingSubtreeBufferDetector.StreamedGroupingModelsSelect(group.Select)
                && (group.GroupAdjacent is not null || group.GroupStartingWith is not null || group.GroupEndingWith is not null):
                walkers.Add(node);
                break;
            default:
                foreach (var content in WrappedContent(node))
                    CollectWalkers(content, walkers);
                break;
        }
    }

    /// <summary>
    /// What a walking instruction evaluates with the matched element as the context, before it
    /// walks: the parameters it passes. They are not read for it by the walk.
    /// </summary>
    private static bool OperandsReadNothing(XsltInstruction walker, Streamability.StreamingContext context)
    {
        IEnumerable<(XQueryExpression? Select, XsltSequenceConstructor? Content)> operands = walker switch
        {
            XsltApplyTemplates apply => apply.WithParams.Select(p => (p.Select, p.Content)),
            XsltIterate iterate => iterate.Params.Select(p => (p.Select, p.Content)),
            _ => [],
        };
        foreach (var (select, content) in operands)
        {
            if (select is not null
                && Streamability.StreamabilityClassifier.Classify(select, context).Sweep != Streamability.Sweep.Motionless)
                return false;
            if (content is not null
                && Streamability.StreamabilityClassifier.Classify(content, context).Sweep != Streamability.Sweep.Motionless)
                return false;
        }
        return true;
    }
}
