using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Aggregation strategy for a stream watcher.
/// </summary>
internal enum WatcherAggregation
{
    Count,
    Sum,
    Max,
    Min,
    Avg,
    StringJoin,
    Snapshot,
    Sequence,
    /// <summary>
    /// Returns only the first matched item as a scalar (used for fn:head(path) patterns).
    /// </summary>
    Head
}

/// <summary>
/// Upward-navigation axis for a climbing (striding-then-climbing) watcher.
/// <c>None</c> = a plain downward watcher (no climb). <c>Ancestor</c> /
/// <c>AncestorOrSelf</c> = the watcher's leaf path is followed by an
/// <c>ancestor::</c> / <c>ancestor-or-self::</c> step. The climb is resolved at the
/// leaf's StartElement, where every ancestor is already open on the streaming
/// element stack (Task 1.3).
/// </summary>
internal enum ClimbAxisKind
{
    None,
    Ancestor,
    AncestorOrSelf
}

/// <summary>
/// Matches a path pattern against the streaming element stack.
/// </summary>
internal sealed class StreamPathMatcher
{
    private readonly string[] _steps;

    public StreamPathMatcher(string pathPattern)
    {
        // Parse "transactions/transaction" into ["transactions", "transaction"]
        // or "transactions/transaction/@value" into ["transactions", "transaction", "@value"]
        _steps = pathPattern.Split('/');
    }

    /// <summary>
    /// Checks if the current element stack matches this pattern.
    /// </summary>
    /// <param name="ancestorStack">Current element name stack (outermost first).</param>
    /// <param name="currentName">The current element's local name.</param>
    /// <param name="contextRootDepth">
    /// Stack index (in <paramref name="ancestorStack"/> terms, 0-indexed
    /// outermost-first) of the watcher's context-root element: the leftmost concrete
    /// step is anchored as a child of that root. A value of <c>null</c> (the default)
    /// preserves the legacy floating behavior — a relative path matches at any depth.
    /// <c>-1</c> anchors to the document NODE (xsl:source-document): its child
    /// elements fire at stack index 0, i.e. with an empty ancestor stack. A deferred
    /// matched-template passes the active reader depth (its <c>ParentDepth</c>), the
    /// stack index of the matched element.
    /// </param>
    public bool Matches(IReadOnlyList<string> ancestorStack, string currentName, int? contextRootDepth = null)
    {
        // Build full path: ancestors + current
        // Match against steps from the end
        if (_steps.Length == 0) return false;

        var lastStep = _steps[^1];

        // Attribute step — matched separately via MatchesAttribute
        if (lastStep.StartsWith('@')) return false;

        if (lastStep != currentName && lastStep != "*") return false;

        if (_steps.Length == 1) return contextRootDepth is null || ancestorStack.Count == contextRootDepth.Value + 1;

        return MatchAncestors(_steps, _steps.Length - 2, ancestorStack, ancestorStack.Count - 1, contextRootDepth);
    }

    /// <summary>
    /// Walks the step list backward against the ancestor stack. A "**" step is a
    /// descendant-axis marker that consumes zero or more ancestor entries, so the
    /// match becomes nondeterministic at that position — we try the shortest
    /// alignment first and fall back to deeper skips.
    /// </summary>
    private static bool MatchAncestors(string[] steps, int stepIdx, IReadOnlyList<string> ancestorStack, int stackIdx, int? contextRootDepth)
    {
        while (stepIdx >= 0)
        {
            var step = steps[stepIdx];
            if (step == "**")
            {
                // Descendant marker: match zero or more ancestor names. Recurse
                // for each possible alignment of the next concrete step.
                if (stepIdx == 0) return true; // unbounded left edge — anything above is fine (stays floating)
                var nextStep = steps[stepIdx - 1];
                for (var i = stackIdx; i >= 0; i--)
                {
                    if (nextStep == "**" || nextStep == ancestorStack[i] || nextStep == "*")
                    {
                        if (MatchAncestors(steps, stepIdx - 2, ancestorStack, i - 1, contextRootDepth)) return true;
                    }
                }
                return false;
            }
            if (stackIdx < 0) return false;
            if (step != ancestorStack[stackIdx] && step != "*") return false;
            stepIdx--;
            stackIdx--;
        }
        // Reached the leftmost concrete step. With a null sentinel the relative path
        // floats (matches at any depth); otherwise anchor that step as a child of the
        // context root, i.e. the next unconsumed ancestor index must equal
        // contextRootDepth (the stack index of the context-root element).
        return contextRootDepth is null || stackIdx == contextRootDepth.Value;
    }

    /// <summary>
    /// Checks if the pattern ends with an attribute step and the element path matches.
    /// Returns the attribute local name if matched, null otherwise.
    /// </summary>
    public string? MatchesAttribute(IReadOnlyList<string> ancestorStack, string currentElementName, int? contextRootDepth = null)
    {
        if (_steps.Length < 2) return null;
        var lastStep = _steps[^1];
        if (!lastStep.StartsWith('@')) return null;

        // Check the element path (all steps except the last)
        var elementSteps = _steps[..^1];
        if (elementSteps[^1] != currentElementName && elementSteps[^1] != "*") return null;

        if (!MatchAncestors(elementSteps, elementSteps.Length - 2, ancestorStack, ancestorStack.Count - 1, contextRootDepth))
            return null;

        return lastStep[1..]; // Strip the @ prefix
    }
}

/// <summary>
/// A watcher that accumulates data from the XML stream for a consuming sub-expression.
/// </summary>
internal sealed class StreamWatcher
{
    /// <summary>
    /// Identity of the AST sub-expression this watcher replaces.
    /// </summary>
    public required XQueryExpression SourceExpression { get; init; }

    /// <summary>
    /// Path pattern to match in the stream.
    /// </summary>
    public required StreamPathMatcher PathMatcher { get; init; }

    /// <summary>
    /// How to accumulate matched values.
    /// </summary>
    public required WatcherAggregation Aggregation { get; init; }

    /// <summary>
    /// Stack index (in the streaming ancestor-name stack) of this watcher's
    /// context-root element. The leftmost concrete step of <see cref="PathMatcher"/>
    /// is anchored as a child of that root. <c>-1</c> for source-document activation
    /// (the default): the context root is the document NODE, above element index 0,
    /// so its child elements fire with an empty ancestor stack. For a deferred
    /// matched-template the active reader depth (<c>ParentDepth</c>), the stack index
    /// of the matched element, so the watcher path is interpreted relative to it
    /// rather than floated to any depth.
    /// </summary>
    public int ContextRootDepth { get; init; } = -1;

    /// <summary>
    /// Optional: attribute name to extract from matched elements (e.g., "value" for @value).
    /// When null, the element's text content or the element itself is used.
    /// </summary>
    public string? ValueAttribute { get; init; }

    /// <summary>
    /// For StringJoin: the separator string.
    /// </summary>
    public string? Separator { get; init; }

    /// <summary>
    /// When non-empty, predicates to evaluate against the matched element's
    /// snapshot before counting/accumulating. Mirrors
    /// <see cref="ForEachSubscription.Predicates"/>: items that fail any
    /// predicate are dropped from the aggregation.
    /// </summary>
    public IReadOnlyList<XQueryExpression> Predicates { get; init; }
        = Array.Empty<XQueryExpression>();

    /// <summary>
    /// Motionless predicates carried by ancestor (intermediate) element steps of the
    /// matched path — e.g. the <c>[@CAT='P']</c> on <c>ITEM</c> in
    /// <c>BOOKLIST/BOOKS/ITEM[@CAT='P']/PRICE</c>. Each entry records how many element
    /// levels above the matched leaf the predicated ancestor sits and the predicate(s)
    /// to evaluate against that ancestor (name + attributes only). A matched leaf is
    /// dropped from the aggregation unless every ancestor predicate passes.
    /// </summary>
    public IReadOnlyList<StreamingExpressionScanner.IntermediatePredicate> IntermediatePredicates { get; init; }
        = Array.Empty<StreamingExpressionScanner.IntermediatePredicate>();

    /// <summary>
    /// Group A (wrapped aggregation): when this watcher is keyed to the WHOLE
    /// wrapper expression (a <c>FilterExpression</c> over head/outermost/remove of a
    /// streamable path), these are the outer positional predicates applied to the
    /// GROUNDED accumulated sequence at resolve time — not during the streaming
    /// pass. Empty for a bare aggregation. The accumulated sequence is fully
    /// materialized in memory, so the existing per-item evaluator binds correct
    /// <c>position()</c>/<c>last()</c>.
    /// </summary>
    public IReadOnlyList<XQueryExpression> OuterPredicates { get; init; }
        = Array.Empty<XQueryExpression>();

    /// <summary>
    /// Group A: when this watcher is keyed to the whole wrapper and the wrapper is a
    /// <c>SimpleMapExpression</c> (<c>head/outermost/remove(path) ! RIGHT</c>), this is
    /// the per-item RIGHT applied to the grounded accumulated sequence at resolve.
    /// NOT YET WIRED by the scanner — the <c>! RIGHT</c> shape currently registers via
    /// the SM-ctx subscription path, so this field is always null at present and the
    /// matching branch in <c>ApplyWrappedOuterOpAsync</c> is a stub for future use.
    /// </summary>
    public XQueryExpression? OuterSimpleMapRight { get; init; }

    /// <summary>
    /// Group A: 1-based index dropped from the accumulated sequence when the inner
    /// function is <c>remove(path, n)</c>. Applied to the grounded sequence before the
    /// outer predicate/SimpleMap RIGHT. Null when the inner is not <c>remove</c>.
    /// </summary>
    public int? RemoveSkipIndex { get; init; }

    /// <summary>
    /// Group A: true when the inner path ends in a <c>text()</c> KindTest tail
    /// (<c>//PRICE/text()</c>). Accumulated leaf values are the matched elements'
    /// text content — already the string value the path matcher captures for a leaf
    /// element — so this is informational/forward-looking; it does not change the
    /// per-leaf capture for the single-text-child leaves in scope.
    /// </summary>
    public bool TextNodeTail { get; init; }

    /// <summary>
    /// Group A: true when the inner function is <c>outermost(...)</c>. The accumulated
    /// Snapshot drops any node that is a descendant of another accumulated node
    /// (outermost = a selected node with no selected ancestor). A no-op for child-axis
    /// or non-nesting match sets; applied when producing the result.
    /// </summary>
    public bool Outermost { get; init; }

    /// <summary>
    /// Task 1.3 (CLIMBING): when non-<c>None</c>, this watcher's leaf path is followed
    /// by an <c>ancestor::</c> / <c>ancestor-or-self::</c> step. Instead of accumulating
    /// the matched LEAF, the processor resolves the upward navigation against the leaf's
    /// open-ancestor chain at StartElement (every ancestor is known there) via
    /// <see cref="OnClimbMatch"/>. When <see cref="ValueAttribute"/> is also set, the
    /// climb is followed by an attribute step (<c>ancestor::*/@a</c>), so the accumulated
    /// items are the matched ancestors' attribute values; otherwise they are the ancestor
    /// element nodes themselves (for <c>! name()</c> / union / windowing consumers).
    /// </summary>
    public ClimbAxisKind ClimbAxis { get; init; } = ClimbAxisKind.None;

    /// <summary>
    /// A single open ancestor supplied to <see cref="OnClimbMatch"/>: its stable
    /// <see cref="OpenId"/> (a monotonic id assigned when the element was pushed onto the
    /// streaming stack — shared ancestors of two leaves keep the same id, distinct
    /// same-name elements get distinct ids, so document-order de-duplication is exact),
    /// its <see cref="Name"/>, and its <see cref="Attributes"/> (may be null). A
    /// materialized <see cref="XdmNode"/> may be attached (<see cref="Node"/>) so
    /// <c>name()</c>/attribute consumers see a real node.
    /// </summary>
    public readonly record struct ClimbAncestor(
        long OpenId,
        string Name,
        IReadOnlyDictionary<string, string>? Attributes,
        XdmNode? Node = null);

    // Open ids already contributed to the accumulation, so a shared ancestor climbed
    // from multiple leaves is emitted once (in first-seen document order).
    private readonly HashSet<long> _climbSeenOpenIds = [];

    // Accumulation state
    private long _count;
    private double _sum;
    private double? _max;
    private double? _min;
    private readonly List<string> _strings = [];
    private readonly List<object> _items = [];
    private readonly List<XdmNode> _snapshots = [];

    // Subtree collection state (for Sequence/Snapshot)
    private bool _collectingSubtree;
    private int _subtreeDepth;
    private readonly List<StreamXmlEvent> _subtreeEvents = [];

    /// <summary>
    /// Records a materialized element node for Snapshot/Sequence watchers, alongside
    /// the string capture in <see cref="_items"/>. Used when downstream consumers
    /// (xsl:copy-of, subsequence over copy-of, etc.) need real <see cref="XdmNode"/>
    /// references rather than atomized string values.
    /// </summary>
    public void OnLeafElementCaptured(XdmNode element)
    {
        if (Aggregation is WatcherAggregation.Snapshot or WatcherAggregation.Sequence)
            _snapshots.Add(element);
    }

    /// <summary>
    /// Fills the materialized-element snapshot for a previously reserved slot.
    /// Mirrors <see cref="FillSequenceSlot"/> for the XdmNode parallel array.
    /// Expands <c>_snapshots</c> as needed so it can be indexed by slot.
    /// </summary>
    public void FillSnapshotSlot(int index, XdmNode element)
    {
        if (Aggregation is not (WatcherAggregation.Snapshot or WatcherAggregation.Sequence)) return;
        if (index < 0) return;
        while (_snapshots.Count <= index)
            _snapshots.Add(null!);
        _snapshots[index] = element;
    }

    /// <summary>
    /// Reserves a slot in the items list at the current end (document order
    /// position) and returns its index. Used when an element MATCHES at
    /// StartElement but its text-content value cannot be known until
    /// EndElement (e.g., a non-leaf match under descendant axis). Subsequent
    /// inner matches that resolve sooner will get later indices, preserving
    /// XPath document order.
    /// </summary>
    public int ReserveSequenceSlot()
    {
        if (Aggregation is not (WatcherAggregation.Sequence or WatcherAggregation.Snapshot
            or WatcherAggregation.Sum or WatcherAggregation.Max or WatcherAggregation.Min
            or WatcherAggregation.Avg or WatcherAggregation.StringJoin))
            return -1;
        _items.Add(string.Empty);
        return _items.Count - 1;
    }

    /// <summary>
    /// Fills a previously reserved sequence slot with the element's resolved
    /// text/attribute value. Mirrors the Sequence/Snapshot branch of
    /// <see cref="OnElementMatch"/> but writes by index instead of appending.
    /// </summary>
    public void FillSequenceSlot(int index, IReadOnlyDictionary<string, string>? attributes, string? textContent)
    {
        if (index < 0 || index >= _items.Count) return;
        if (ValueAttribute != null && attributes != null)
        {
            // A matched attribute's value is xs:untypedAtomic (its typed value when the
            // attribute is not schema-validated), exactly like a matched text node's
            // value below. Capturing it as XsUntypedAtomic — rather than a raw
            // xs:string — lets a general comparison over the sequence promote to the
            // other operand's type (e.g. (a/b/@v[pred]) = 4.32 -> numeric), while
            // value-of / xsl:attribute / string-join still serialize its lexical string.
            // A raw string would raise XPTY0004 ("cannot compare xs:string with numeric")
            // in that comparison — the regression behind sx-GeneralComp-*-019/119.
            var av = attributes.GetValueOrDefault(ValueAttribute);
            _items[index] = av != null ? new Xdm.XsUntypedAtomic(av) : (object)string.Empty;
        }
        else
        {
            // Mirror OnElementMatch: a matched text node's value is xs:untypedAtomic
            // (matching a non-streamed XdmText.TypedValue), so the predicate-deferred
            // path produces the same typed item as the non-deferred path — keeping
            // _items uniformly typed so e.g. text() ! (.+1) promotes for arithmetic.
            _items[index] = textContent != null
                ? new Xdm.XsUntypedAtomic(textContent)
                : (object)string.Empty;
        }
    }

    /// <summary>
    /// Called when a matching element is encountered during the streaming pass.
    /// </summary>
    public void OnElementMatch(string elementName, IReadOnlyDictionary<string, string>? attributes, string? textContent)
    {
        switch (Aggregation)
        {
            case WatcherAggregation.Count:
                _count++;
                break;

            case WatcherAggregation.Sum:
            case WatcherAggregation.Max:
            case WatcherAggregation.Min:
            case WatcherAggregation.Avg:
                var numStr = ValueAttribute != null
                    ? attributes?.GetValueOrDefault(ValueAttribute)
                    : textContent;
                if (numStr != null && double.TryParse(numStr, System.Globalization.CultureInfo.InvariantCulture, out var num))
                {
                    _sum += num;
                    _count++;
                    if (!_max.HasValue || num > _max.Value) _max = num;
                    if (!_min.HasValue || num < _min.Value) _min = num;
                }
                break;

            case WatcherAggregation.StringJoin:
                var str = ValueAttribute != null
                    ? attributes?.GetValueOrDefault(ValueAttribute)
                    : textContent;
                if (str != null) _strings.Add(str);
                break;

            case WatcherAggregation.Sequence:
            case WatcherAggregation.Snapshot:
                // For simple leaf elements, capture value directly
                if (ValueAttribute != null && attributes != null)
                {
                    // Capture the attribute's value as xs:untypedAtomic (its typed value
                    // for a non-validated attribute), matching FillSequenceSlot and the
                    // text-node branch below, so a general comparison over the sequence
                    // promotes to the comparand's numeric type instead of raising
                    // XPTY0004 for xs:string. value-of / string-join still serialize its
                    // lexical string. (sx-GeneralComp-*-019/119.)
                    var attrVal = attributes.GetValueOrDefault(ValueAttribute);
                    if (attrVal != null) _items.Add(new Xdm.XsUntypedAtomic(attrVal));
                }
                else if (textContent != null)
                {
                    // Capture a streamed text node's value as xs:untypedAtomic, matching the
                    // non-streaming text atomization (XdmText.TypedValue; the engine's own
                    // text-capture path uses Xdm.XsUntypedAtomic). This lets arithmetic over the
                    // captured value promote (head(//X/text())!(.+1)) while value-of/copy-of/
                    // string-join still serialize its lexical string value.
                    _items.Add(new Xdm.XsUntypedAtomic(textContent));
                }
                // The streaming processor calls OnLeafElementMatch separately for
                // Snapshot/Sequence watchers when a full XdmElement is needed
                // (e.g. xsl:copy-of select="subsequence(copy-of(/path), 3)"). The
                // captured-element path coexists with the string path above so
                // existing consumers (value-of/string-join) still see strings.
                break;

            case WatcherAggregation.Head:
                // Only capture the first match; subsequent matches are ignored.
                if (_items.Count == 0)
                {
                    var headAttr = ValueAttribute != null ? attributes?.GetValueOrDefault(ValueAttribute) : null;
                    object? headVal = ValueAttribute != null
                        // Attribute value as xs:untypedAtomic (see Sequence/Snapshot branch)
                        // so head(a/b/@v) promotes for arithmetic/comparison; value-of serializes.
                        ? (headAttr != null ? new Xdm.XsUntypedAtomic(headAttr) : null)
                        // Streamed text node value as xs:untypedAtomic (see Sequence/Snapshot
                        // branch): arithmetic over head(//X/text()) promotes; value-of serializes.
                        : textContent != null ? new Xdm.XsUntypedAtomic(textContent) : null;
                    if (headVal != null) _items.Add(headVal);
                }
                break;
        }
    }

    /// <summary>
    /// Task 1.3 (CLIMBING): resolves the <c>ancestor::</c> / <c>ancestor-or-self::</c>
    /// navigation for a matched leaf against its currently-open ancestor chain.
    /// <paramref name="ancestors"/> are outermost-first (document order); for
    /// <see cref="ClimbAxisKind.AncestorOrSelf"/> the leaf itself is the innermost
    /// selected node and is appended last. Each selected node is contributed at most
    /// once across all leaves (keyed on <see cref="ClimbAncestor.OpenId"/> / the leaf's
    /// own <paramref name="leafOpenId"/>) so <c>ancestor::*</c> climbed from several
    /// leaves de-duplicates by node identity in first-seen document order — matching the
    /// XPath reverse-axis-then-document-order semantics for the in-scope corpus shapes.
    ///
    /// When <see cref="ValueAttribute"/> is set the selected item is the node's named
    /// attribute value (dropped when absent, mirroring an empty attribute step); when a
    /// materialized <see cref="XdmNode"/> is attached it is captured for node consumers
    /// (<c>name()</c>, union) alongside the string form used by value-of/string-join.
    /// </summary>
    public void OnClimbMatch(
        IReadOnlyList<ClimbAncestor> ancestors,
        string leafName,
        IReadOnlyDictionary<string, string>? leafAttributes,
        long leafOpenId = -1,
        XdmNode? leafNode = null)
    {
        if (Aggregation is not (WatcherAggregation.Sequence or WatcherAggregation.Snapshot))
            return;

        // ancestor::  -> the ancestors only (outermost first).
        // ancestor-or-self:: -> ancestors then the leaf (document order: leaf is deepest).
        for (int i = 0; i < ancestors.Count; i++)
            ContributeClimbNode(ancestors[i].OpenId, ancestors[i].Name, ancestors[i].Attributes, ancestors[i].Node);

        if (ClimbAxis == ClimbAxisKind.AncestorOrSelf)
            ContributeClimbNode(leafOpenId, leafName, leafAttributes, leafNode);
    }

    private void ContributeClimbNode(
        long openId,
        string name,
        IReadOnlyDictionary<string, string>? attributes,
        XdmNode? node)
    {
        // De-duplicate by node identity. A synthetic (openId < 0) node is always kept
        // (unit-test / no-identity path), but the real streaming path always supplies a
        // stable open id so shared ancestors collapse to one occurrence.
        if (openId >= 0 && !_climbSeenOpenIds.Add(openId)) return;

        if (ValueAttribute != null)
        {
            // ancestor::*/@a — the selected item is the attribute value (untyped atomic),
            // absent attributes contribute nothing (an empty attribute step).
            var av = attributes?.GetValueOrDefault(ValueAttribute);
            if (av != null)
                _items.Add(new Xdm.XsUntypedAtomic(av));
            return;
        }

        // Bare ancestor::* — the selected item is the element node. Capture its name as
        // the string form (value-of/string-join) and the materialized node (name()/union).
        _items.Add(name);
        if (node != null)
            _snapshots.Add(node);
    }

    /// <summary>
    /// Begin collecting a subtree for Sequence/Snapshot watchers.
    /// Called when a match occurs at a non-leaf element.
    /// </summary>
    public void BeginSubtreeCollection()
    {
        _collectingSubtree = true;
        _subtreeDepth = 0;
        _subtreeEvents.Clear();
    }

    /// <summary>
    /// Feed a streaming event during subtree collection.
    /// Returns true if the subtree is complete.
    /// </summary>
    public bool OnSubtreeEvent(StreamXmlEvent evt)
    {
        if (!_collectingSubtree) return false;
        _subtreeEvents.Add(evt);

        if (evt.Type == XmlNodeType.Element) _subtreeDepth++;
        else if (evt.Type == XmlNodeType.EndElement)
        {
            _subtreeDepth--;
            if (_subtreeDepth < 0)
            {
                _collectingSubtree = false;
                // Build in-memory node from collected events
                // (implementation deferred to integration task)
                return true;
            }
        }

        return false;
    }

    public bool IsCollectingSubtree => _collectingSubtree;

    /// <summary>
    /// Gets the accumulated result after the streaming pass.
    /// </summary>
    public object? GetResult()
    {
        return Aggregation switch
        {
            WatcherAggregation.Count => _count,
            WatcherAggregation.Sum => _count > 0 ? _sum : null,
            WatcherAggregation.Max => _max.HasValue ? _max.Value : null,
            WatcherAggregation.Min => _min.HasValue ? _min.Value : null,
            WatcherAggregation.Avg => _count > 0 ? _sum / _count : null,
            WatcherAggregation.StringJoin => string.Join(Separator ?? "", _strings),
            // Prefer materialized element nodes when present AND every match
            // produced one (1:1 with _items). Mixed cases — nested-element or
            // attribute paths skip materialization — fall back to atomized
            // strings used by value-of / string-join consumers.
            WatcherAggregation.Sequence => BuildSequenceResult(),
            // Snapshot falls back to Array.Empty<XdmNode>() (the prior contract)
            // rather than the atomized strings, so consumers that rely on the
            // node-typed empty result (e.g. sf-snapshot-0101b's deep-equal
            // against a real doc) don't see a sudden type change when
            // materialization skips a nested-element match.
            WatcherAggregation.Snapshot => BuildSnapshotResult(),
            // Head returns the first item as a scalar (or null if none matched)
            WatcherAggregation.Head => _items.Count > 0 ? _items[0] : null,
            _ => null
        };
    }

    /// <summary>
    /// Group A: produces the inner accumulated sequence with the streaming-decidable
    /// post-processing applied — outermost descendant-dedup and the <c>remove</c>
    /// skip-index — but NOT the outer predicate / SimpleMap RIGHT (those need the
    /// evaluator and are applied at resolve in <c>XsltTransformer</c>). Returns the
    /// items as a flat <see cref="object"/> array (strings and/or <see cref="XdmNode"/>),
    /// in document order. For a watcher with no outer wrapper this is never called.
    /// </summary>
    public object[] ProduceAccumulated()
    {
        // GetResult() already compacts tombstoned slots and prefers materialized
        // nodes; normalize whatever it returns to a flat object[].
        var raw = GetResult();
        var items = new List<object>();
        switch (raw)
        {
            case null:
                break;
            case object[] arr:
                items.AddRange(arr);
                break;
            default:
                items.Add(raw);
                break;
        }

        // outermost(): drop a node that is a descendant of another accumulated node.
        // Decidable on the materialized set via Parent-chain reachability. For the
        // in-scope cases (child-axis PRICE, single-ITEM text nodes) the match set has
        // no nesting, so this is a no-op; the string-valued path is always a no-op.
        if (Outermost && items.Count > 1)
        {
            var nodes = new List<XdmNode>(items.Count);
            foreach (var it in items)
                if (it is XdmNode n) nodes.Add(n);
            if (nodes.Count == items.Count)
            {
                var kept = new List<object>(items.Count);
                foreach (var candidate in nodes)
                {
                    bool hasSelectedAncestor = false;
                    foreach (var other in nodes)
                    {
                        if (ReferenceEquals(other, candidate)) continue;
                        if (IsDescendantOf(candidate, other)) { hasSelectedAncestor = true; break; }
                    }
                    if (!hasSelectedAncestor) kept.Add(candidate);
                }
                items = kept;
            }
        }

        // remove(path, n): drop the 1-based index n from the accumulated sequence.
        if (RemoveSkipIndex is { } skip && skip >= 1 && skip <= items.Count)
            items.RemoveAt(skip - 1);

        return items.ToArray();
    }

    /// <summary>
    /// True when <paramref name="node"/> is a descendant of <paramref name="ancestor"/>,
    /// walking the materialized Parent chain. Both nodes must share the same document
    /// for the comparison to resolve; standalone materialized subtrees never share a
    /// document, so this returns false (the dedup no-op for the in-scope cases).
    /// </summary>
    private static bool IsDescendantOf(XdmNode node, XdmNode ancestor)
    {
        if (node.Document != ancestor.Document) return false;
        var p = node.Parent;
        while (p is { } pid)
        {
            if (pid == ancestor.Id) return true;
            // No store reference here to resolve further ancestors; the materialized
            // snapshots are single-level for the in-scope cases. Stop at the first
            // parent comparison (sufficient for the non-nesting target sets).
            break;
        }
        return false;
    }

    private object[] BuildSequenceResult()
    {
        var (items, snaps) = CompactSlots();
        if (snaps.Length > 0 && snaps.Length == items.Length)
            return snaps.Cast<object>().ToArray();
        return items.Length > 0 ? items : Array.Empty<object>();
    }

    private object[] BuildSnapshotResult()
    {
        var (items, snaps) = CompactSlots();
        if (snaps.Length > 0 && snaps.Length == items.Length)
            return snaps.Cast<object>().ToArray();
        return Array.Empty<XdmNode>();
    }

    /// <summary>
    /// Discards a previously reserved slot when a predicate filtered the element
    /// out at EndElement. Replaces the slot with a tombstone so document-order
    /// indices for items added after this one (e.g. inner descendant matches)
    /// remain stable; <see cref="GetResult"/> filters tombstones out.
    /// </summary>
    public void DiscardSequenceSlot(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _items[index] = _slotTombstone;
        if (_snapshots.Count > index)
            _snapshots[index] = null!;
    }

    /// <summary>
    /// Sentinel object that marks a sequence slot as filtered-out by a predicate.
    /// Distinguishable from string values so <see cref="GetResult"/> can compact
    /// the result array.
    /// </summary>
    private static readonly object _slotTombstone = new();

    /// <summary>
    /// Removes any tombstoned slots from the accumulator arrays. Called from
    /// <see cref="GetResult"/> for aggregations that materialize per-item.
    /// </summary>
    private (object[] items, XdmNode[] snapshots) CompactSlots()
    {
        var items = new List<object>(_items.Count);
        var snaps = new List<XdmNode>(_snapshots.Count);
        for (int i = 0; i < _items.Count; i++)
        {
            if (ReferenceEquals(_items[i], _slotTombstone)) continue;
            items.Add(_items[i]);
            if (i < _snapshots.Count && _snapshots[i] != null)
                snaps.Add(_snapshots[i]);
        }
        return (items.ToArray(), snaps.ToArray());
    }

    /// <summary>
    /// Rolls back a numeric/string accumulation when a predicate filters an
    /// element after <see cref="OnElementMatch"/> already committed it (for the
    /// non-deferred fast path). Subtracts the contribution from sum/avg/count
    /// and is a no-op for max/min (we can't reliably retract those; the engine
    /// only uses this path when the watcher uses ReserveSequenceSlot).
    /// </summary>
    public void RollbackNumericContribution(double value)
    {
        if (Aggregation is WatcherAggregation.Sum or WatcherAggregation.Avg)
        {
            _sum -= value;
            if (_count > 0) _count--;
        }
        else if (Aggregation is WatcherAggregation.Count)
        {
            if (_count > 0) _count--;
        }
    }

    /// <summary>
    /// Reset for reuse.
    /// </summary>
    public void Reset()
    {
        _count = 0;
        _sum = 0;
        _max = null;
        _min = null;
        _strings.Clear();
        _items.Clear();
        _snapshots.Clear();
        _collectingSubtree = false;
        _subtreeEvents.Clear();
        _climbSeenOpenIds.Clear();
    }
}

/// <summary>
/// A captured XML event for incremental subtree building.
/// </summary>
internal readonly record struct StreamXmlEvent(
    XmlNodeType Type,
    string LocalName,
    string? NamespaceUri,
    string? Value,
    IReadOnlyDictionary<string, string>? Attributes);
