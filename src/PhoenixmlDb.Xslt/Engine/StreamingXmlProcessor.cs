using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Processes an XML document via XmlReader in a single forward pass,
/// dispatching to XSLT templates along the way. This is the core of
/// XSLT 3.0 streaming execution: the source document is never fully
/// materialized in memory.
/// </summary>
internal sealed class StreamingXmlProcessor
{
    private readonly XsltStylesheet _stylesheet;
    private readonly TemplateIndex _templateIndex;
    private readonly DefaultXsltExecutionContext _context;
    private readonly XdmInMemoryStore _nodeStore;
    private QName? _mode;

    /// <summary>
    /// Overrides the streaming dispatch mode for the duration of a single
    /// <see cref="ProcessAsync"/> pass. Set by the wrapped/consuming
    /// <c>xsl:apply-templates</c> handoff (Phase 2a of #143) so that an
    /// apply-templates carrying an explicit <c>mode</c> attribute (e.g.
    /// <c>mode="t"</c> with <c>on-no-match="deep-copy"</c>) drives the live reader
    /// with THAT mode's template-matching and built-in rule behaviour — rather than
    /// the processor's construction-time mode (the enclosing template's mode).
    /// </summary>
    internal QName? DispatchMode
    {
        get => _mode;
        set => _mode = value;
    }
    private ulong _nextNodeId = 1_000_000; // High start to avoid collision with tree nodes

    // Accumulator state for streaming computation
    private readonly IReadOnlyList<XsltAccumulator> _accumulators;
    private object?[]? _accCurrentValues;
    private Dictionary<NodeId, (object? before, object? after)>[]? _accNodeValueMaps;

    // Stream watchers for consuming sub-expression evaluation
    private readonly IReadOnlyList<StreamWatcher>? _watchers;

    // For-each subscriptions: bodies dispatched per matching element on the stream
    private readonly IReadOnlyList<ForEachSubscription>? _subscriptions;

    // When true, this processor was invoked solely to dispatch for-each subscriptions
    // (no xsl:apply-templates in the source-document body). In that mode, non-matching
    // elements and stray text/whitespace nodes must NOT trigger default template
    // matching — the user only asked to iterate the subscribed paths.
    private readonly bool _subscriptionDispatchOnly;

    // Ancestor element names for watcher path matching (outermost first)
    private readonly List<string> _ancestorNames = [];

    // Ancestor element attributes, parallel to _ancestorNames (outermost first).
    // Maintained at the same push/pop sites so a matched leaf can evaluate a
    // motionless intermediate predicate (e.g. ITEM[@CAT='P']) against the
    // attributes of the predicated ancestor, which are fully known at its
    // start-tag. Each entry may be null when the ancestor has no attributes.
    private readonly List<IReadOnlyDictionary<string, string>?> _ancestorAttributes = [];

    // Forward sibling positions of each open ancestor, parallel to _ancestorNames
    // (outermost first). Each entry holds two 1-based positions captured at the
    // ancestor's start-tag: position among ALL element siblings under its parent
    // (for a wildcard `*` step), and position among siblings sharing its local name
    // (for a name test or `*:NCName` namespace wildcard). These let a matched leaf
    // evaluate a FORWARD-COUNTABLE positional intermediate predicate (e.g.
    // ITEM[position() lt 4]) by supplying the ancestor's position as the XPath
    // context position. Maintained at the same push/pop sites as _ancestorNames.
    private readonly List<(int ElementPos, int NamePos)> _ancestorPositions = [];

    // Scratch buffer for clearing per-name child counters at a closing element's depth
    // (avoids allocating a removal list per EndElement). Reused across EndElement events.
    private readonly List<(int Depth, string Name)> _nameCountResetScratch = [];

    // Running 1-based dispatch counter per for-each subscription. xsl:for-each
    // numbers position() over the items it actually iterates (the matched
    // elements/attributes/text-nodes that pass the subscription's predicates),
    // NOT over all stream events. Incremented once per dispatched body execution
    // so position()/last()-free positional logic inside the body is correct.
    private Dictionary<ForEachSubscription, int>? _subscriptionDispatchCount;

    // Running 1-based path-match counter per for-each subscription, incremented
    // every time the subscription's path matches an element — BEFORE its filter
    // predicate is evaluated. This is the context position() seen by a positional
    // predicate on the for-each select (e.g. transaction[position() lt 5]),
    // which counts position within the selected node sequence, distinct from the
    // body's position() (which counts only items that passed the predicate).
    private Dictionary<ForEachSubscription, int>? _subscriptionMatchCount;

    public StreamingXmlProcessor(
        XsltStylesheet stylesheet,
        TemplateIndex templateIndex,
        DefaultXsltExecutionContext context,
        XdmInMemoryStore nodeStore,
        QName? mode,
        IReadOnlyList<XsltAccumulator>? accumulators = null,
        IReadOnlyList<StreamWatcher>? watchers = null,
        IReadOnlyList<ForEachSubscription>? subscriptions = null,
        bool subscriptionDispatchOnly = false)
    {
        _stylesheet = stylesheet;
        _templateIndex = templateIndex;
        _context = context;
        _nodeStore = nodeStore;
        _mode = mode;
        _accumulators = accumulators ?? Array.Empty<XsltAccumulator>();
        _watchers = watchers;
        _subscriptions = subscriptions;
        _subscriptionDispatchOnly = subscriptionDispatchOnly;
    }

    /// <summary>The for-each subscriptions this processor dispatches, if any.</summary>
    public IReadOnlyList<ForEachSubscription>? Subscriptions => _subscriptions;

    /// <summary>
    /// Processes the XML document from the reader in a single streaming pass.
    /// Accumulator values are computed inline during the forward pass so that
    /// accumulator-before() / accumulator-after() return correct values.
    /// </summary>
    public async ValueTask ProcessAsync(XmlReader reader, CancellationToken ct = default)
    {
        var ancestorStack = new Stack<StreamingNodeContext>();

        // Track sibling element position per depth level.
        // Key = depth, Value = count of child elements seen so far at that depth
        // under the current parent. Reset when the parent's EndElement is encountered.
        var siblingCountByDepth = new Dictionary<int, int>();

        // Track per-name sibling position: key = (depth, local name), value = count of
        // child elements of that name seen so far at that depth under the current parent.
        // Parallels siblingCountByDepth (which counts ALL element children) and is reset
        // on the parent's EndElement. Drives same-name position() for forward-countable
        // positional intermediate-step predicates (e.g. ITEM[position() lt 4]).
        var nameCountByDepth = new Dictionary<(int Depth, string Name), int>();

        // When a user template with an empty body matches an element (suppressing it),
        // we must skip all child events until the matching EndElement. Track the depth
        // at which suppression started; -1 means no suppression active.
        int suppressionDepth = -1;

        // Initialize accumulator state
        await InitializeAccumulatorsAsync().ConfigureAwait(false);

        // Set streaming execution flag so built-in templates skip child recursion
        var previousStreamingFlag = _context._isStreamingExecution;
        _context._isStreamingExecution = true;
        // Expose the active reader on the context so streaming-aware operators
        // (notably xsl:for-each-group) can drive it directly during template
        // execution. Cleared on exit so non-streaming runs don't see a stale handle.
        var previousStreamingReader = _context._activeStreamingReader;
        _context._activeStreamingReader = reader;
        var previousStreamingCt = _context._activeStreamingCancellationToken;
        _context._activeStreamingCancellationToken = ct;
        try
        {
            while (true)
            {
                // Drain any output produced in the previous iteration to the external sink (no-op
                // unless _streamingOutputSink is set). Safe here because no ScopedOutputBuffer can
                // be open at the top of the loop — all prior instruction `using` scopes have unwound.
                await _context.DrainStreamingOutputAsync(ct).ConfigureAwait(false);
                // Streaming-aware operators (e.g. xsl:for-each-group) may consume an
                // event from the reader that we still need to process. They set
                // _streamingDeferReadOnNextIteration so we skip our own ReadAsync and
                // process whatever the reader is currently positioned on.
                if (_context._streamingDeferReadOnNextIteration)
                {
                    _context._streamingDeferReadOnNextIteration = false;
                }
                else if (!await reader.ReadAsync().ConfigureAwait(false))
                {
                    break;
                }
                ct.ThrowIfCancellationRequested();

                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        var nodeId = new NodeId(_nextNodeId++);
                        var isEmptyElement = reader.IsEmptyElement;
                        var elementDepth = reader.Depth;

                        // If we're inside a suppressed subtree, skip template execution
                        // but still fire accumulators (they must track the full document).
                        if (suppressionDepth >= 0 && elementDepth > suppressionDepth)
                        {
                            // Build minimal node for accumulator rules. Lazy-alloc the attrs / nsDecls
                            // collections; most elements have one but not both.
                            List<StreamingNodeContext>? skipAttrs = null;
                            Dictionary<string, string>? skipNsDecls = null;
                            if (reader.HasAttributes)
                            {
                                for (int i = 0; i < reader.AttributeCount; i++)
                                {
                                    reader.MoveToAttribute(i);
                                    if (reader.Prefix == "xmlns" || (reader.Prefix.Length == 0 && reader.LocalName == "xmlns"))
                                        (skipNsDecls ??= new Dictionary<string, string>(StringComparer.Ordinal))[reader.Prefix == "xmlns" ? reader.LocalName : ""] = reader.Value;
                                    else
                                    {
                                        var skipAttrId = new NodeId(_nextNodeId++);
                                        var skipAttrCtx = AcquireNodeContext();
                                        skipAttrCtx.NodeKind = XdmNodeKind.Attribute;
                                        skipAttrCtx.LocalName = reader.LocalName;
                                        skipAttrCtx.NamespaceUri = reader.NamespaceURI;
                                        skipAttrCtx.Prefix = reader.Prefix;
                                        skipAttrCtx.StringValue = reader.Value;
                                        skipAttrCtx.NodeId = skipAttrId;
                                        skipAttrCtx.Depth = elementDepth + 1;
                                        (skipAttrs ??= AcquireNodeCtxList()).Add(skipAttrCtx);
                                    }
                                }
                                reader.MoveToElement();
                            }
                            var skipCtx = AcquireNodeContext();
                            skipCtx.NodeKind = XdmNodeKind.Element;
                            skipCtx.LocalName = reader.LocalName;
                            skipCtx.NamespaceUri = reader.NamespaceURI;
                            skipCtx.Prefix = reader.Prefix;
                            skipCtx.NodeId = nodeId;
                            skipCtx.Attributes = skipAttrs;
                            skipCtx.NamespaceDeclarations = skipNsDecls;
                            skipCtx.Depth = elementDepth;
                            var skipXdm = MaterializeElement(skipCtx);
                            await FireAccumulatorRulesAsync(skipXdm, nodeId, AccumulatorPhase.Start).ConfigureAwait(false);
                            // Fire stream watchers in the suppressed branch too — when a
                            // parent template was deferred (consuming aggregates pending
                            // execution at parent EndElement), child elements feed the
                            // watchers but don't fire their own templates. Without this
                            // the count(*) / sum(*) etc. would never accumulate.
                            if (_watchers != null || _context._activeStreamWatchers != null)
                            {
                                Dictionary<string, string>? skipAttrDict = null;
                                if (skipAttrs is { Count: > 0 })
                                {
                                    skipAttrDict = new Dictionary<string, string>(skipAttrs.Count, StringComparer.Ordinal);
                                    foreach (var attr in skipAttrs)
                                        skipAttrDict[attr.LocalName] = attr.StringValue ?? "";
                                }
                                FireWatchers(skipCtx.LocalName, skipAttrDict, null);
                            }
                            if (!isEmptyElement)
                            {
                                ancestorStack.Push(skipCtx);
                                _ancestorNames.Add(skipCtx.LocalName);
                                _ancestorAttributes.Add(BuildAttrDict(skipCtx.Attributes));
                                // Keep _ancestorPositions parallel. Suppressed subtrees do
                                // not drive positional intermediate predicates (those run on
                                // the source-document aggregation path, not under a suppressed
                                // template), so a 0/0 sentinel suffices here.
                                _ancestorPositions.Add((0, 0));
                            }
                            else
                            {
                                await FireAccumulatorRulesAsync(skipXdm, nodeId, AccumulatorPhase.End).ConfigureAwait(false);
                                if (_watchers != null || _context._activeStreamWatchers != null)
                                    await FireWatchersEndElement(skipCtx.LocalName).ConfigureAwait(false);
                                CleanupStreamingNode(skipCtx);
                            }
                            break;
                        }

                        // Collect attributes and namespace declarations. Lazy-alloc both
                        // collections — many streamed elements have one but not the other,
                        // and the per-element empty-dict / empty-list churn was ~17% of
                        // post-delegate-cache allocations.
                        List<StreamingNodeContext>? attrs = null;
                        Dictionary<string, string>? nsDecls = null;
                        if (reader.HasAttributes)
                        {
                            for (int i = 0; i < reader.AttributeCount; i++)
                            {
                                reader.MoveToAttribute(i);
                                if (reader.Prefix == "xmlns" || (reader.Prefix.Length == 0 && reader.LocalName == "xmlns"))
                                {
                                    var prefix = reader.Prefix == "xmlns" ? reader.LocalName : "";
                                    (nsDecls ??= new Dictionary<string, string>(StringComparer.Ordinal))[prefix] = reader.Value;
                                }
                                else
                                {
                                    var attrCtx = AcquireNodeContext();
                                    attrCtx.NodeKind = XdmNodeKind.Attribute;
                                    attrCtx.LocalName = reader.LocalName;
                                    attrCtx.NamespaceUri = reader.NamespaceURI;
                                    attrCtx.Prefix = reader.Prefix;
                                    attrCtx.StringValue = reader.Value;
                                    attrCtx.NodeId = new NodeId(_nextNodeId++);
                                    attrCtx.Depth = reader.Depth + 1;
                                    (attrs ??= AcquireNodeCtxList()).Add(attrCtx);
                                }
                            }
                            reader.MoveToElement();
                        }

                        // Track sibling position: increment counter for this depth
                        if (!siblingCountByDepth.TryGetValue(elementDepth, out var siblingCount))
                            siblingCount = 0;
                        siblingCount++;
                        siblingCountByDepth[elementDepth] = siblingCount;

                        // Track per-name sibling position (for same-name position() in
                        // forward-countable positional intermediate predicates).
                        var nameKey = (elementDepth, reader.LocalName);
                        if (!nameCountByDepth.TryGetValue(nameKey, out var nameCount))
                            nameCount = 0;
                        nameCount++;
                        nameCountByDepth[nameKey] = nameCount;
                        int currentNamePos = nameCount;

                        var current = AcquireNodeContext();
                        current.NodeKind = XdmNodeKind.Element;
                        current.LocalName = reader.LocalName;
                        current.NamespaceUri = reader.NamespaceURI;
                        current.Prefix = reader.Prefix;
                        current.NodeId = nodeId;
                        current.Attributes = attrs;
                        current.NamespaceDeclarations = nsDecls;
                        current.Parent = ancestorStack.Count > 0 ? ancestorStack.Peek() : null;
                        current.Depth = elementDepth;
                        current.Position = siblingCount;

                        // Match and execute template
                        var xdmElem = MaterializeElement(current);

                        // Fire start-phase accumulator rules before template execution
                        await FireAccumulatorRulesAsync(xdmElem, nodeId, AccumulatorPhase.Start).ConfigureAwait(false);

                        // Fire stream watchers for element match
                        if (_watchers != null)
                        {
                            // Build attribute dictionary for watcher matching
                            Dictionary<string, string>? attrDict = null;
                            if (attrs is { Count: > 0 })
                            {
                                attrDict = new Dictionary<string, string>(attrs.Count, StringComparer.Ordinal);
                                foreach (var attr in attrs)
                                    attrDict[attr.LocalName] = attr.StringValue ?? "";
                            }
                            FireWatchers(current.LocalName, attrDict, null);
                        }

                        // Group B — non-consuming inspection subscriptions. An
                        // inspection for-each over outermost(//X) / //X with an
                        // inspection-only body dispatches the body per match against an
                        // ancestor-synthesized snapshot WITHOUT materializing/skipping
                        // the subtree (mirrors FireWatchers' observe-at-StartElement
                        // model), so the forward pass continues into descendants where
                        // deeper //X matches still fire. Runs BEFORE the consuming
                        // materialize-and-skip block (which would otherwise swallow the
                        // subtree) and never breaks.
                        if (_subscriptions != null && _subscriptions.Count > 0)
                        {
                            await DispatchInspectionSubscriptionsAsync(current.LocalName, attrs)
                                .ConfigureAwait(false);
                        }

                        // Dispatch for-each subscriptions whose path matches this element.
                        // Multiple subscriptions may match the same element; materialize the
                        // subtree once and execute each matching body against the snapshot.
                        // Materialization advances the reader past the matching EndElement
                        // (same semantics as the buffered-subtree fallback below), so we
                        // also fire End-phase accumulators/watchers and short-circuit.
                        if (_subscriptions != null && _subscriptions.Count > 0)
                        {
                            List<ForEachSubscription>? matched = null;
                            foreach (var sub in _subscriptions)
                            {
                                if (sub.IsInspectionOnly) continue; // handled non-consuming above
                                if (sub.PathMatcher.Matches(_ancestorNames, current.LocalName))
                                {
                                    (matched ??= new List<ForEachSubscription>()).Add(sub);
                                }
                            }
                            if (matched != null)
                            {
                                // Materialize the current element subtree from the live reader.
                                // Reader is left at the matching EndElement (or still on the
                                // empty element) — same contract as ExecuteWithBufferedSubtreeAsync.
                                var snapshot = StreamingSubtreeMaterializer.Materialize(reader, _nodeStore, new DocumentId(0));
                                if (snapshot != null)
                                {
                                    // Synthesize an ancestor chain on the snapshot root so the
                                    // for-each body's upward navigation (parent axis `..`,
                                    // ancestor::*) resolves against the elements open on the
                                    // stream when this leaf matched. Without it name(..) is
                                    // empty (si-for-each-008/009). Ancestors carry their names +
                                    // attributes (known at their start-tags) but no children
                                    // (motionless — descendants are not yet streamed).
                                    LinkSnapshotAncestors(snapshot);

                                    // Body executes against a buffered snapshot, not the live
                                    // reader — temporarily clear the streaming flag so descendant
                                    // navigation uses the normal in-memory path.
                                    var prevStreaming = _context._isStreamingExecution;
                                    _context._isStreamingExecution = false;
                                    try
                                    {
                                        foreach (var sub in matched)
                                        {
                                            // Intermediate (non-leaf) child-axis predicate filter —
                                            // forward-countable-positional (employee[1]) or motionless
                                            // (department[@name='sales']) — evaluated against the matched
                                            // leaf's ancestor (the leaf itself is NOT yet on
                                            // _ancestorNames, so offset 1 == immediate parent, matching
                                            // the watcher convention). A leaf whose ancestor fails the
                                            // predicate is NOT part of the selected sequence, so this runs
                                            // BEFORE NextSubscriptionMatchPosition — rejected leaves must
                                            // not consume a position()/subsequence window index.
                                            if (sub.IntermediatePredicates.Count > 0)
                                            {
                                                var ancSnaps = BuildSubscriptionIntermediateAncestors(sub.IntermediatePredicates);
                                                if (ancSnaps is { Count: > 0 }
                                                    && !await EvaluateIntermediateAncestorPredicatesAsync(ancSnaps).ConfigureAwait(false))
                                                {
                                                    continue;
                                                }
                                            }

                                            // Path-level match position (counts every element the
                                            // subscription path matches, before predicate filtering)
                                            // — the context position() for a positional filter
                                            // predicate on the for-each select, and the index used
                                            // to apply a subsequence() slice window.
                                            int matchPosition = NextSubscriptionMatchPosition(sub);

                                            // Apply a subsequence(path, start [, length]) slice: skip
                                            // matches outside [start, start+length). Below the start
                                            // index and past the end are both skipped (motionless —
                                            // the body never sees them, so position() inside the body
                                            // numbers only the windowed items).
                                            if (sub.SubsequenceStart > 1 || sub.SubsequenceLength != null)
                                            {
                                                if (matchPosition < sub.SubsequenceStart) continue;
                                                if (sub.SubsequenceLength is { } len
                                                    && matchPosition >= sub.SubsequenceStart + len) continue;
                                            }

                                            // Apply remove(path, n): skip the single match whose
                                            // 1-based path-match position equals the remove index.
                                            // Composes with the subsequence window above.
                                            if (sub.RemoveIndex is { } ri && matchPosition == ri) continue;

                                            // Evaluate predicates (if any) against the snapshot.
                                            // Skip dispatch if any predicate is false.
                                            if (sub.Predicates.Count > 0)
                                            {
                                                bool allPass = true;
                                                _context.PushContextItem(snapshot, matchPosition, 1);
                                                _context.PushCurrentItem(snapshot);
                                                try
                                                {
                                                    foreach (var pred in sub.Predicates)
                                                    {
                                                        // A bare numeric predicate [N] is positional shorthand for
                                                        // position() = N. EvaluateBooleanAsync would return the
                                                        // literal's effective boolean (always true for non-zero),
                                                        // so compare the match position directly. Mirrors the
                                                        // motionless-ancestor positional handling below. No IsPositional
                                                        // guard is needed (the motionless path has one): ForEachSubscription
                                                        // carries no such flag, and per XPath a bare numeric literal in a
                                                        // step predicate is always positional, so the literal-type test suffices.
                                                        bool ok = pred is IntegerLiteral or DecimalLiteral or DoubleLiteral
                                                            ? NumericLiteralEqualsPosition(pred, matchPosition)
                                                            : await _context.EvaluateBooleanAsync(pred).ConfigureAwait(false);
                                                        if (!ok)
                                                        {
                                                            allPass = false;
                                                            break;
                                                        }
                                                    }
                                                }
                                                finally
                                                {
                                                    _context.PopCurrentItem();
                                                    _context.PopContextItem();
                                                }
                                                if (!allPass) continue;
                                            }

                                            if (sub.AttributeName != null)
                                            {
                                                // Find the named attribute on the snapshot. Skip if missing.
                                                PhoenixmlDb.Xdm.Nodes.XdmAttribute? matchedAttr = null;
                                                foreach (var attrId in snapshot.Attributes)
                                                {
                                                    if (_nodeStore.GetNode(attrId) is PhoenixmlDb.Xdm.Nodes.XdmAttribute xa
                                                        && xa.LocalName == sub.AttributeName)
                                                    {
                                                        matchedAttr = xa;
                                                        break;
                                                    }
                                                }
                                                if (matchedAttr == null) continue;
                                                _context.PushContextItem(matchedAttr, NextSubscriptionPosition(sub), 1);
                                                _context.PushCurrentItem(matchedAttr);
                                                try
                                                {
                                                    if (sub.PerItemSelect != null)
                                                        await _context.EmitSimpleMapContextResultAsync(sub.PerItemSelect).ConfigureAwait(false);
                                                    else
                                                        await sub.Body!.ExecuteAsync(_context).ConfigureAwait(false);
                                                }
                                                finally
                                                {
                                                    _context.PopCurrentItem();
                                                    _context.PopContextItem();
                                                }
                                                continue;
                                            }

                                            if (sub.TextNodeTail)
                                            {
                                                // Iterate text-node children of the materialized
                                                // element. Each text node becomes the context item;
                                                // the body's parent-axis navigation (`..` or
                                                // parent::) resolves to the materialized element via
                                                // the text node's Parent reference (set during
                                                // materialization).
                                                foreach (var textChild in EnumerateTextChildren(snapshot))
                                                {
                                                    _context.PushContextItem(textChild, NextSubscriptionPosition(sub), 1);
                                                    _context.PushCurrentItem(textChild);
                                                    try
                                                    {
                                                        if (sub.PerItemSelect != null)
                                                            await _context.EmitSimpleMapContextResultAsync(sub.PerItemSelect).ConfigureAwait(false);
                                                        else
                                                            await sub.Body!.ExecuteAsync(_context).ConfigureAwait(false);
                                                    }
                                                    finally
                                                    {
                                                        _context.PopCurrentItem();
                                                        _context.PopContextItem();
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                _context.PushContextItem(snapshot, NextSubscriptionPosition(sub), 1);
                                                _context.PushCurrentItem(snapshot);
                                                try
                                                {
                                                    if (sub.PerItemSelect != null)
                                                        await _context.EmitSimpleMapContextResultAsync(sub.PerItemSelect).ConfigureAwait(false);
                                                    else
                                                        await sub.Body!.ExecuteAsync(_context).ConfigureAwait(false);
                                                }
                                                finally
                                                {
                                                    _context.PopCurrentItem();
                                                    _context.PopContextItem();
                                                }
                                            }
                                        }
                                    }
                                    finally
                                    {
                                        _context._isStreamingExecution = prevStreaming;
                                    }

                                    // Fire end-phase accumulators/watchers manually since the
                                    // EndElement event was consumed by Materialize.
                                    await FireAccumulatorRulesAsync(xdmElem, nodeId, AccumulatorPhase.End).ConfigureAwait(false);
                                    if (_watchers != null
                                        || _context._activeStreamWatchers != null
                                        || _pendingTextWatcherMatches.Count > 0)
                                    {
                                        await FireWatchersEndElement(current.LocalName).ConfigureAwait(false);
                                    }
                                    CleanupStreamingNode(current);
                                    break;
                                }
                            }
                        }

                        // In subscription-dispatch-only mode the source-document body has
                        // no xsl:apply-templates — non-subscribed elements must not trigger
                        // the default template machinery (which would emit text children).
                        bool wasSuppressed;
                        if (_subscriptionDispatchOnly)
                        {
                            wasSuppressed = false;
                        }
                        else
                        {
                            wasSuppressed = await _context.MatchAndExecuteStreamingNodeAsync(xdmElem, _mode, current.Position)
                                .ConfigureAwait(false);
                        }

                        // Subtree-buffer fallback: MatchAndExecute consumed the entire
                        // element subtree from the reader (snapshot()/copy-of() needed an
                        // in-memory tree). Skip ancestor push — no matching EndElement event
                        // will arrive — and fire End-phase accumulators / watchers manually,
                        // mirroring the self-closing-element path.
                        if (_context._streamingSubtreeBufferConsumed)
                        {
                            _context._streamingSubtreeBufferConsumed = false;
                            await FireAccumulatorRulesAsync(xdmElem, nodeId, AccumulatorPhase.End).ConfigureAwait(false);
                            if (_watchers != null
                                || _context._activeStreamWatchers != null
                                || _pendingTextWatcherMatches.Count > 0)
                            {
                                await FireWatchersEndElement(current.LocalName).ConfigureAwait(false);
                            }
                            if (_context._streamingOpenElements.Count > 0)
                            {
                                var qname = _context._streamingOpenElements.Pop();
                                _context.WriteStreamingEndTag(qname);
                            }
                            CleanupStreamingNode(current);
                            break;
                        }

                        if (!isEmptyElement)
                        {
                            ancestorStack.Push(current);
                            _ancestorNames.Add(current.LocalName);
                            _ancestorAttributes.Add(BuildAttrDict(current.Attributes));
                            _ancestorPositions.Add((siblingCount, currentNamePos));

                            // If the template had an empty body (suppression), skip all children
                            if (wasSuppressed && suppressionDepth < 0)
                                suppressionDepth = elementDepth;
                        }
                        else
                        {
                            // Self-closing: fire end-phase rules, close any deferred tag, then clean up
                            await FireAccumulatorRulesAsync(xdmElem, nodeId, AccumulatorPhase.End).ConfigureAwait(false);

                            // Fire watchers for end of self-closing element (constructor-time
                            // + dynamic + pending-text-content matches).
                            if (_watchers != null
                                || _context._activeStreamWatchers != null
                                || _pendingTextWatcherMatches.Count > 0)
                            {
                                await FireWatchersEndElement(current.LocalName).ConfigureAwait(false);
                            }

                            if (_context._streamingOpenElements.Count > 0)
                            {
                                var qname = _context._streamingOpenElements.Pop();
                                _context.WriteStreamingEndTag(qname);
                            }
                            CleanupStreamingNode(current);
                        }
                        break;
                    }

                    case XmlNodeType.EndElement:
                    {
                        if (ancestorStack.Count > 0)
                        {
                            var closingContext = ancestorStack.Pop();

                            // Pop ancestor name for watcher path matching
                            if (_ancestorNames.Count > 0)
                                _ancestorNames.RemoveAt(_ancestorNames.Count - 1);
                            if (_ancestorAttributes.Count > 0)
                                _ancestorAttributes.RemoveAt(_ancestorAttributes.Count - 1);
                            if (_ancestorPositions.Count > 0)
                                _ancestorPositions.RemoveAt(_ancestorPositions.Count - 1);

                            // Deferred-template execution: if a template firing for this
                            // element was deferred (consuming aggregates accumulated via
                            // watchers), execute the body NOW with watcher results
                            // available for substitution. Done BEFORE
                            // CleanupStreamingNode so the element is still registered.
                            if (_context._streamingDeferredExecutions.Count > 0)
                            {
                                var topDeferred = _context._streamingDeferredExecutions.Peek();
                                if (topDeferred.ParentDepth == closingContext.Depth)
                                {
                                    _context._streamingDeferredExecutions.Pop();
                                    await _context.ExecuteDeferredAsync(topDeferred).ConfigureAwait(false);
                                }
                            }

                            // Check if we're closing a suppressed element
                            var wasSuppressedElement = suppressionDepth >= 0 && closingContext.Depth == suppressionDepth;
                            if (wasSuppressedElement)
                                suppressionDepth = -1;

                            // Reset sibling counter for children of this closing element.
                            // Children are at depth = closingContext.Depth + 1.
                            siblingCountByDepth.Remove(closingContext.Depth + 1);
                            // Reset per-name child counters at that same depth so the next
                            // parent restarts position() from 1. Entries are sparse (one per
                            // distinct child name seen under this parent).
                            if (nameCountByDepth.Count > 0)
                            {
                                int childDepth = closingContext.Depth + 1;
                                _nameCountResetScratch.Clear();
                                foreach (var key in nameCountByDepth.Keys)
                                    if (key.Depth == childDepth)
                                        _nameCountResetScratch.Add(key);
                                foreach (var key in _nameCountResetScratch)
                                    nameCountByDepth.Remove(key);
                            }
                            // Fire end-phase accumulator rules (always, even for suppressed elements)
                            var closingNode = _nodeStore.GetNode(closingContext.NodeId);
                            if (closingNode != null)
                                await FireAccumulatorRulesAsync(closingNode, closingContext.NodeId, AccumulatorPhase.End).ConfigureAwait(false);

                            // Fire watchers for end element (subtree tracking AND
                            // pending-text-content match completion). Must run for
                            // dynamic watchers + pending matches too, not just the
                            // constructor-time _watchers.
                            if (_watchers != null
                                || _context._activeStreamWatchers != null
                                || _pendingTextWatcherMatches.Count > 0)
                            {
                                await FireWatchersEndElement(closingContext.LocalName).ConfigureAwait(false);
                            }

                            // Write the deferred closing tag for elements opened by shallow-copy.
                            // Skip this for suppressed elements — they never wrote an open tag.
                            if (!wasSuppressedElement && _context._streamingOpenElements.Count > 0)
                            {
                                var qname = _context._streamingOpenElements.Pop();
                                _context.WriteStreamingEndTag(qname);
                            }

                            // Clean up temporary XDM nodes to free memory
                            CleanupStreamingNode(closingContext);
                        }
                        break;
                    }

                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                    case XmlNodeType.SignificantWhitespace:
                    // Whitespace-only text between sibling elements is still a text
                    // node and must flow through unless an xsl:strip-space rule removes
                    // it. Without this case the streaming identity transform silently
                    // drops indentation (Martin Honnen 2026-05-18).
                    case XmlNodeType.Whitespace:
                    {
                        var textValue = reader.Value;

                        // Text nodes in streaming: create temporary text node, match templates.
                        // We pool XdmText instances here to avoid per-event GC churn on
                        // long-running streamed transforms (10M-item identity allocated 97 GiB
                        // before pooling). The instance is returned to the pool at the
                        // matching _nodeStore.Remove below. Safe because:
                        //   - Watchers consume textValue (string), not the XdmText reference.
                        //   - StreamingSubtreeMaterializer constructs its own XdmText copies.
                        //   - Template bodies push/pop context-item stacks within this loop body;
                        //     the node is unreferenced before we reach Remove.
                        var textNodeId = new NodeId(_nextNodeId++);
                        var textNode = AcquirePooledText(textNodeId, textValue);
                        _nodeStore.Register(textNode);

                        // Fire stream watchers for text content (constructor-time +
                        // dynamic _activeStreamWatchers + accumulate into any pending
                        // text-content matches so sum/string-join over text-content
                        // elements get their values).
                        if (_watchers != null
                            || _context._activeStreamWatchers != null
                            || _pendingTextWatcherMatches.Count > 0)
                        {
                            FireWatchersText(textValue);
                        }

                        // Fire accumulator rules for text nodes (always, even if suppressed)
                        await FireAccumulatorRulesAsync(textNode, textNodeId, AccumulatorPhase.Start).ConfigureAwait(false);

                        // Only match templates if not inside a suppressed subtree.
                        // Skip entirely in subscription-dispatch-only mode (no apply-templates
                        // exists; default text rule would otherwise leak whitespace).
                        if (suppressionDepth < 0 && !_subscriptionDispatchOnly)
                        {
                            await _context.MatchAndExecuteStreamingNodeAsync(textNode, _mode, 1)
                                .ConfigureAwait(false);
                        }

                        // Fire end-phase accumulator rules for text nodes
                        await FireAccumulatorRulesAsync(textNode, textNodeId, AccumulatorPhase.End).ConfigureAwait(false);

                        // Clean up
                        _nodeStore.Remove(textNodeId);
                        ReleasePooledText(textNode);
                        break;
                    }

                    case XmlNodeType.Comment:
                    {
                        var commentId = new NodeId(_nextNodeId++);
                        var comment = new XdmComment
                        {
                            Id = commentId,
                            Document = new DocumentId(0),
                            Value = reader.Value
                        };
                        _nodeStore.Register(comment);

                        await FireAccumulatorRulesAsync(comment, commentId, AccumulatorPhase.Start).ConfigureAwait(false);
                        if (suppressionDepth < 0)
                        {
                            await _context.MatchAndExecuteStreamingNodeAsync(comment, _mode, 1)
                                .ConfigureAwait(false);
                        }
                        await FireAccumulatorRulesAsync(comment, commentId, AccumulatorPhase.End).ConfigureAwait(false);

                        _nodeStore.Remove(commentId);
                        break;
                    }

                    case XmlNodeType.ProcessingInstruction:
                    {
                        var piId = new NodeId(_nextNodeId++);
                        var pi = new XdmProcessingInstruction
                        {
                            Id = piId,
                            Document = new DocumentId(0),
                            Target = reader.LocalName,
                            Value = reader.Value
                        };
                        _nodeStore.Register(pi);

                        await FireAccumulatorRulesAsync(pi, piId, AccumulatorPhase.Start).ConfigureAwait(false);
                        if (suppressionDepth < 0)
                        {
                            await _context.MatchAndExecuteStreamingNodeAsync(pi, _mode, 1)
                                .ConfigureAwait(false);
                        }
                        await FireAccumulatorRulesAsync(pi, piId, AccumulatorPhase.End).ConfigureAwait(false);

                        _nodeStore.Remove(piId);
                        break;
                    }
                }
            }
        }
        finally
        {
            _context._isStreamingExecution = previousStreamingFlag;
            _context._activeStreamingReader = previousStreamingReader;
            _context._activeStreamingCancellationToken = previousStreamingCt;
        }
    }

    /// <summary>
    /// Initializes accumulator current-values and per-node value maps.
    /// Called once at the start of streaming to set up initial values.
    /// </summary>
    private async ValueTask InitializeAccumulatorsAsync()
    {
        if (_accumulators.Count == 0)
            return;

        _accCurrentValues = new object?[_accumulators.Count];
        _accNodeValueMaps = new Dictionary<NodeId, (object? before, object? after)>[_accumulators.Count];

        // Ensure the context's accumulator storage is initialized
        _context._accumulatorValues ??= new();

        for (var i = 0; i < _accumulators.Count; i++)
        {
            // Get or create the per-accumulator node value map
            if (!_context._accumulatorValues.TryGetValue(_accumulators[i].Name, out var existingMap))
            {
                existingMap = new Dictionary<NodeId, (object? before, object? after)>();
                _context._accumulatorValues[_accumulators[i].Name] = existingMap;
            }
            _accNodeValueMaps[i] = existingMap;

            try
            {
                var initVal = await _context.EvaluateAsync(_accumulators[i].InitialValue).ConfigureAwait(false);
                if (_accumulators[i].As != null)
                    initVal = DefaultXsltExecutionContext.CoerceAccumulatorValue(initVal, _accumulators[i].As!, _accumulators[i].Name);
                _accCurrentValues[i] = initVal;
            }
#pragma warning disable CA1031 // Dynamic errors in accumulators are intentionally deferred per XSLT 3.0
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _accCurrentValues[i] = new AccumulatorDeferredError(ex);
            }
        }
    }

    /// <summary>
    /// Fires accumulator rules (start or end phase) for the given node during
    /// the streaming forward pass. Stores before/after values so that
    /// accumulator-before() and accumulator-after() work correctly.
    /// </summary>
    private async ValueTask FireAccumulatorRulesAsync(object node, NodeId nodeId, AccumulatorPhase phase)
    {
        if (_accCurrentValues == null || _accNodeValueMaps == null || _accumulators.Count == 0)
            return;

        // Lazy-cache the 5 delegates: method-group conversions allocate fresh delegates
        // per call, and this method fires once per node during streaming. Captured
        // `_nodeStore` and `_context` are stable for the lifetime of the processor.
        _accMatchNodeResolver ??= id => _nodeStore.GetNode(id);
        _accMatchPredicateEvaluator ??= _context.EvaluatePatternPredicate;
        _accMatchPositionComputer ??= _context.ComputeNodePosition;
        _accMatchKeyPatternEvaluator ??= _context.EvaluateKeyPattern;
        _accMatchIdPatternEvaluator ??= _context.EvaluateIdPattern;

        var matchContext = new XsltContext
        {
            CurrentNode = node,
            Position = 1,
            Last = 1,
            NodeResolver = _accMatchNodeResolver,
            PredicateEvaluator = _accMatchPredicateEvaluator,
            PositionComputer = _accMatchPositionComputer,
            KeyPatternEvaluator = _accMatchKeyPatternEvaluator,
            IdPatternEvaluator = _accMatchIdPatternEvaluator,
        };

        for (var i = 0; i < _accumulators.Count; i++)
        {
            if (_accCurrentValues[i] is AccumulatorDeferredError)
            {
                // Propagate error state to node value map
                if (phase == AccumulatorPhase.Start)
                    _accNodeValueMaps[i][nodeId] = (before: _accCurrentValues[i], after: _accCurrentValues[i]);
                continue;
            }

            var acc = _accumulators[i];
            foreach (var rule in acc.Rules)
            {
                if (rule.Phase != phase)
                    continue;
                if (!rule.Match.Matches(node, matchContext))
                    continue;

                try
                {
                    _accCurrentValues[i] = await _context.EvaluateAccumulatorRuleAsync(
                        node, rule, _accCurrentValues[i], acc).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Dynamic errors in accumulators are intentionally deferred
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _accCurrentValues[i] = new AccumulatorDeferredError(ex);
                }
                break; // Only first matching rule fires
            }

            // Store values: start phase sets before-value, end phase updates after-value
            if (phase == AccumulatorPhase.Start)
            {
                _accNodeValueMaps[i][nodeId] = (before: _accCurrentValues[i], after: _accCurrentValues[i]);
            }
            else
            {
                var existing = _accNodeValueMaps[i].GetValueOrDefault(nodeId, (before: _accCurrentValues[i], after: _accCurrentValues[i]));
                _accNodeValueMaps[i][nodeId] = (existing.before, after: _accCurrentValues[i]);
            }
        }
    }

    /// <summary>
    /// Fires watchers for element start events.
    /// </summary>
    /// <summary>
    /// One pending element-match — created at StartElement when a watcher needs
    /// the element's text content (ValueAttribute == null). Text events between
    /// the start and matching EndElement append to <see cref="TextBuffer"/>;
    /// on EndElement, the watcher is fired once with the accumulated text and
    /// the entry removed.
    /// </summary>
    private sealed class PendingTextWatcherMatch
    {
        public required StreamWatcher Watcher;
        public required int Depth;
        public required string ElementName;
        public Dictionary<string, string>? Attributes;
        public StringBuilder TextBuffer = new();
        // Set when a child element StartElement appears inside the match window.
        // Materialization of a leaf XdmElement only fires when this stays false —
        // nested-element snapshots can't be flattened into a leaf and must fall
        // back to the existing string-capture path so we don't synthesize a bogus
        // outer leaf that contains the concatenated descendant text.
        public bool HasChildElement;
        // For Sequence/Snapshot aggregations, the slot was reserved at
        // StartElement to preserve XPath document order across nested matches
        // (descendant axis with same-name elements). -1 means no reservation
        // (other aggregations append at EndElement).
        public int ReservedSlot = -1;
        // Subtree builder stack for Snapshot/Sequence watchers — root is the
        // matched element, each push is a nested child element. Filled during
        // FireWatchers/FireWatchersEndElement/FireWatchersText. On the matching
        // EndElement the root is popped and materialized to a real XdmElement
        // tree so copy-of(descendant::x) yields actual nested nodes rather
        // than collapsing to a leaf with concatenated descendant text.
        public Stack<SubtreeBuilderFrame>? SubtreeStack;

        // Snapshotted ancestor elements (name + attributes) for motionless
        // intermediate-step predicates (e.g. ITEM[@CAT='P'] above a matched PRICE).
        // Captured at StartElement when the ancestors' attributes are fully known;
        // each predicate group is evaluated against its ancestor at EndElement, and
        // the matched leaf is dropped from the aggregation if any group fails.
        public List<IntermediateAncestorSnapshot>? IntermediateAncestors;
    }

    /// <summary>
    /// A captured ancestor element (name + attributes) paired with the motionless
    /// predicate(s) that must hold against it for a matched leaf to contribute to
    /// a streaming aggregation. See <see cref="PendingTextWatcherMatch.IntermediateAncestors"/>.
    /// </summary>
    private sealed class IntermediateAncestorSnapshot
    {
        public required string ElementName;
        public required IReadOnlyDictionary<string, string>? Attributes;
        public required IReadOnlyList<XQueryExpression> Predicates;

        // True when Predicates are FORWARD-COUNTABLE positional filters evaluated
        // against the ancestor's forward sibling position (see ContextPosition) rather
        // than its name/attributes. last()-dependent predicates are never captured here.
        public bool IsPositional;
        // The ancestor's 1-based forward sibling position, captured at its start-tag —
        // position among same-name siblings for a name/namespace-wildcard step, or among
        // ALL element siblings for a `*` step. Supplied as the XPath context position so
        // position()-based predicates evaluate correctly. Unused when IsPositional is false.
        public int ContextPosition;
    }

    private sealed class SubtreeBuilderFrame
    {
        public required string LocalName;
        public Dictionary<string, string>? Attributes;
        public List<XdmNode> Children = [];
        public StringBuilder PendingText = new();

        public void FlushPendingText(Func<string, XdmText> textFactory)
        {
            if (PendingText.Length == 0) return;
            Children.Add(textFactory(PendingText.ToString()));
            PendingText.Clear();
        }
    }

    private readonly List<PendingTextWatcherMatch> _pendingTextWatcherMatches = [];

    /// <summary>
    /// Snapshots an element's attribute contexts into a plain local-name → value
    /// dictionary for ancestor-attribute tracking. Returns null when there are no
    /// attributes (callers treat null as empty). Keys use ordinal comparison to
    /// mirror the watcher-matching attribute dictionaries.
    /// </summary>
    /// <summary>
    /// Returns the next 1-based position for <paramref name="sub"/>'s dispatch
    /// sequence, incrementing the running counter. xsl:for-each position() must
    /// count the items the for-each actually iterates, not all stream events.
    /// </summary>
    private int NextSubscriptionPosition(ForEachSubscription sub)
    {
        _subscriptionDispatchCount ??= new Dictionary<ForEachSubscription, int>(ReferenceEqualityComparer.Instance);
        _subscriptionDispatchCount.TryGetValue(sub, out var n);
        n++;
        _subscriptionDispatchCount[sub] = n;
        return n;
    }

    /// <summary>
    /// Returns the next 1-based path-match position for <paramref name="sub"/>
    /// (incremented every time its path matches, before predicate filtering).
    /// Used as the context position() when evaluating a positional filter
    /// predicate on the for-each select.
    /// </summary>
    private int NextSubscriptionMatchPosition(ForEachSubscription sub)
    {
        _subscriptionMatchCount ??= new Dictionary<ForEachSubscription, int>(ReferenceEqualityComparer.Instance);
        _subscriptionMatchCount.TryGetValue(sub, out var n);
        n++;
        _subscriptionMatchCount[sub] = n;
        return n;
    }

    /// <summary>
    /// Group B non-consuming dispatch. For each inspection subscription whose path
    /// matches the element just opened (<paramref name="localName"/> with the live
    /// ancestor stack), dispatch the inspection-only body against a lightweight
    /// ancestor-synthesized snapshot WITHOUT materializing or skipping the subtree —
    /// so the forward pass continues into descendants where deeper <c>//X</c> matches
    /// still fire. <c>outermost(...)</c> dedup is decided immediately from the live
    /// ancestor stack: a match is skipped when any ancestor also matches the pattern.
    /// <para>
    /// Called at StartElement BEFORE the current element is pushed onto
    /// <see cref="_ancestorNames"/>, so the snapshot's synthesized ancestors are
    /// exactly this element's ancestors and the outermost check inspects only
    /// strictly-containing elements.
    /// </para>
    /// </summary>
    private async ValueTask DispatchInspectionSubscriptionsAsync(
        string localName, List<StreamingNodeContext>? attrs)
    {
        if (_subscriptions == null) return;

        List<ForEachSubscription>? matched = null;
        foreach (var sub in _subscriptions)
        {
            if (!sub.IsInspectionOnly) continue;
            if (!sub.PathMatcher.Matches(_ancestorNames, localName)) continue;

            // Outermost dedup: skip if a strictly-containing ancestor also matches
            // the pattern (decided from the live ancestor stack — no buffering).
            if (sub.Outermost && AncestorMatchesPattern(sub.PathMatcher))
                continue;

            (matched ??= new List<ForEachSubscription>()).Add(sub);
        }
        if (matched == null) return;

        // Build the matched element's inspection snapshot once: local-name +
        // attributes, empty children (descendants are NOT consumed), with its
        // ancestor chain synthesized from the live stack so upward navigation
        // (parent axis, ancestor::, ancestor-or-self::) resolves.
        //
        // Synthesize the ancestor chain FIRST so its nodes (and their attributes)
        // receive LOWER NodeIds than the matched element and its attributes — the
        // engine sorts union (`|`) results by ascending NodeId to realize document
        // order, so an ancestor's @OWNER/@CAT must precede the self element's @UNIT
        // (sx-union-137). LinkSnapshotAncestors alone (called after the leaf) would
        // invert that ordering.
        var attrDict = BuildAttrDict(attrs);
        var parentId = SynthesizeAncestorChain();
        var snapshot = MaterializeLeafElement(localName, attrDict, string.Empty);
        snapshot.Parent = parentId;

        // The body executes against a buffered snapshot, not the live reader —
        // clear the streaming flag so any in-memory navigation uses the normal path.
        var prevStreaming = _context._isStreamingExecution;
        _context._isStreamingExecution = false;
        try
        {
            foreach (var sub in matched)
            {
                _context.PushContextItem(snapshot, NextSubscriptionPosition(sub), 1);
                _context.PushCurrentItem(snapshot);
                try
                {
                    if (sub.PerItemSelect != null)
                        await _context.EmitSimpleMapContextResultAsync(sub.PerItemSelect).ConfigureAwait(false);
                    else
                        await sub.Body!.ExecuteAsync(_context).ConfigureAwait(false);
                }
                finally
                {
                    _context.PopCurrentItem();
                    _context.PopContextItem();
                }
            }
        }
        finally
        {
            _context._isStreamingExecution = prevStreaming;
        }
    }

    /// <summary>
    /// True when some element currently on <see cref="_ancestorNames"/> (a strict
    /// ancestor of the element just opened) satisfies <paramref name="matcher"/>.
    /// Used for <c>outermost(...)</c> dedup: a match nested inside another match is
    /// not outermost and is skipped. Each ancestor depth <c>i</c> is tested with the
    /// names above it as its own ancestor stack and its own name as the current name.
    /// </summary>
    private bool AncestorMatchesPattern(StreamPathMatcher matcher, int contextRootDepth = -1)
    {
        for (int i = 0; i < _ancestorNames.Count; i++)
        {
            var prefix = _ancestorNames.GetRange(0, i);
            if (matcher.Matches(prefix, _ancestorNames[i], contextRootDepth))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Sets <paramref name="snapshot"/>'s Parent to a synthesized ancestor chain
    /// reconstructed from the currently-open stream elements (<see cref="_ancestorNames"/>
    /// and <see cref="_ancestorAttributes"/>, outermost first). Each synthesized
    /// ancestor carries its name and start-tag attributes but no children — upward
    /// navigation (parent axis, ancestor::*) in the for-each body is motionless, so
    /// it never needs the ancestors' not-yet-streamed descendant content. No-op when
    /// there are no open ancestors. Ancestors are created with no-namespace names
    /// (the streaming processor tracks local names only); this is sufficient for the
    /// no-namespace upward-navigation cases in scope.
    /// </summary>
    private void LinkSnapshotAncestors(XdmElement snapshot)
    {
        snapshot.Parent = SynthesizeAncestorChain();
    }

    /// <summary>
    /// Synthesizes the ancestor chain from the currently-open stream elements
    /// (<see cref="_ancestorNames"/>/<see cref="_ancestorAttributes"/>, outermost
    /// first) and returns the innermost ancestor's <see cref="NodeId"/> (the Parent
    /// to assign to a matched-element snapshot), or <c>null</c> when there are no
    /// open ancestors. Outermost ancestors are registered first so they receive the
    /// lowest NodeIds — which the engine uses as the document-order key for union
    /// (<c>|</c>) and other set operations.
    /// </summary>
    private NodeId? SynthesizeAncestorChain()
    {
        if (_ancestorNames.Count == 0) return null;

        var documentId = new DocumentId(0);
        NodeId? parentId = null;
        // Build outermost → innermost so each ancestor's Parent points at the one above.
        for (int i = 0; i < _ancestorNames.Count; i++)
        {
            var ancestorId = _nodeStore.NextId();
            var attrs = _ancestorAttributes[i];
            var attrIds = new List<NodeId>();
            if (attrs is { Count: > 0 })
            {
                foreach (var (k, v) in attrs)
                {
                    var attrId = _nodeStore.NextId();
                    _nodeStore.Register(new XdmAttribute
                    {
                        Id = attrId,
                        Document = documentId,
                        Namespace = _nodeStore.InternNamespace(string.Empty),
                        LocalName = k,
                        Prefix = null,
                        Value = v,
                        Parent = ancestorId
                    });
                    attrIds.Add(attrId);
                }
            }
            var ancestor = new XdmElement
            {
                Id = ancestorId,
                Document = documentId,
                Namespace = _nodeStore.InternNamespace(string.Empty),
                LocalName = _ancestorNames[i],
                Prefix = null,
                Attributes = attrIds.Count == 0 ? XdmElement.EmptyAttributes : attrIds,
                Children = XdmElement.EmptyChildren,
                NamespaceDeclarations = XdmElement.EmptyNamespaceDeclarations,
                Parent = parentId
            };
            _nodeStore.Register(ancestor);
            parentId = ancestorId;
        }
        return parentId;
    }

    private static Dictionary<string, string>? BuildAttrDict(List<StreamingNodeContext>? attrs)
    {
        if (attrs is not { Count: > 0 }) return null;
        var dict = new Dictionary<string, string>(attrs.Count, StringComparer.Ordinal);
        foreach (var attr in attrs)
            dict[attr.LocalName] = attr.StringValue ?? "";
        return dict;
    }

    private void FireWatchers(string elementName, IReadOnlyDictionary<string, string>? attributes, string? textContent)
    {
        // Fire both constructor-time watchers (xsl:source-document path) and dynamic
        // watchers registered per-template-firing (streamable-mode deferred-body path).
        // The xsl:source-document path stores the same list in both slots; reference
        // equality detects that and avoids double-counting.
        FireWatchersFromList(_watchers, elementName, attributes, textContent);
        if (!ReferenceEquals(_context._activeStreamWatchers, _watchers))
            FireWatchersFromList(_context._activeStreamWatchers, elementName, attributes, textContent);
    }

    private void FireWatchersFromList(IReadOnlyList<StreamWatcher>? watchers, string elementName, IReadOnlyDictionary<string, string>? attributes, string? textContent)
    {
        if (watchers == null) return;

        // Mark every still-open pending match as containing a child element so
        // the leaf-materialization path skips it. This runs BEFORE the current
        // element is appended to _ancestorNames or its own pending entry is
        // created, so every existing entry is by definition an ancestor of the
        // element we're firing for.
        if (_pendingTextWatcherMatches.Count > 0)
        {
            foreach (var pending in _pendingTextWatcherMatches)
            {
                pending.HasChildElement = true;
                // Push a child frame onto any active subtree builder so the
                // nested element is captured in the materialized tree.
                if (pending.SubtreeStack is { Count: > 0 } stack)
                {
                    var top = stack.Peek();
                    top.FlushPendingText(MakeStreamingText);
                    stack.Push(new SubtreeBuilderFrame
                    {
                        LocalName = elementName,
                        Attributes = attributes != null ? new Dictionary<string, string>((IDictionary<string, string>)attributes) : null
                    });
                }
            }
        }

        foreach (var watcher in watchers)
        {
            // Check for subtree collection in progress
            if (watcher.IsCollectingSubtree)
            {
                var evt = new StreamXmlEvent(XmlNodeType.Element, elementName, null, null, attributes);
                watcher.OnSubtreeEvent(evt);
                continue;
            }

            // Check element path match (anchored to the watcher's context-root depth)
            if (watcher.PathMatcher.Matches(_ancestorNames, elementName, watcher.ContextRootDepth))
            {
                // When predicates are present we MUST defer to EndElement so the
                // matched element can be materialized for predicate evaluation
                // — otherwise Count/Sum/etc. would commit before we know whether
                // the predicate accepts the item.
                // Snapshot ancestor elements named by motionless intermediate-step
                // predicates (e.g. ITEM[@CAT='P'] above PRICE). The ancestors'
                // attributes are fully known now (StartElement); evaluation is
                // deferred to EndElement alongside the final-step predicates.
                // At this point the current (matched) element has NOT yet been
                // pushed onto _ancestorNames, so those lists hold exactly the
                // matched element's ancestors; offset 1 == immediate parent.
                List<IntermediateAncestorSnapshot>? intermediateAncestors = null;
                if (watcher.IntermediatePredicates.Count > 0)
                {
                    foreach (var ip in watcher.IntermediatePredicates)
                    {
                        int idx = _ancestorNames.Count - ip.AncestorOffset;
                        if (idx < 0 || idx >= _ancestorNames.Count) continue;
                        // For a forward-countable positional predicate, capture the
                        // ancestor's running sibling position: among same-name siblings
                        // for a name/namespace-wildcard step, or among ALL element
                        // siblings for a `*` step. This is the XPath context position
                        // supplied to position()-based predicate evaluation at EndElement.
                        int contextPos = 0;
                        if (ip.IsPositional && idx < _ancestorPositions.Count)
                        {
                            var pos = _ancestorPositions[idx];
                            contextPos = ip.IsWildcardStep ? pos.ElementPos : pos.NamePos;
                        }
                        (intermediateAncestors ??= []).Add(new IntermediateAncestorSnapshot
                        {
                            ElementName = _ancestorNames[idx],
                            Attributes = idx < _ancestorAttributes.Count ? _ancestorAttributes[idx] : null,
                            Predicates = ip.Predicates,
                            IsPositional = ip.IsPositional,
                            ContextPosition = contextPos
                        });
                    }
                }

                bool defer = watcher.ValueAttribute == null
                    && (WatcherNeedsTextContent(watcher) || watcher.Predicates.Count > 0
                        || watcher.IntermediatePredicates.Count > 0);
                if (defer)
                {
                    // Defer the OnElementMatch until EndElement so accumulated text
                    // content from intervening Text events is available. Without
                    // this, sum(n) / string-join(n, ',') over text-content elements
                    // never receive the text and aggregate to null/empty.
                    // For Sequence/Snapshot, reserve a slot now so outer matches
                    // (descendant axis with nested same-name) sit before their
                    // inner matches in document order.
                    var reservedSlot = -1;
                    Stack<SubtreeBuilderFrame>? subtreeStack = null;
                    if (watcher.Aggregation is WatcherAggregation.Sequence or WatcherAggregation.Snapshot)
                    {
                        reservedSlot = watcher.ReserveSequenceSlot();
                    }
                    // Build a subtree stack whenever we will need to materialize
                    // the matched element — either because the consumer wants
                    // the snapshot (Sequence/Snapshot) OR because we have to
                    // evaluate motionless predicates that may navigate into
                    // descendants. Without this stack, Count/Sum/etc. with a
                    // predicate would see materialized == null at EndElement
                    // and silently fall through unfiltered.
                    bool needSubtree = watcher.Aggregation is WatcherAggregation.Sequence or WatcherAggregation.Snapshot
                        || watcher.Predicates.Count > 0;
                    if (needSubtree)
                    {
                        subtreeStack = new Stack<SubtreeBuilderFrame>();
                        subtreeStack.Push(new SubtreeBuilderFrame
                        {
                            LocalName = elementName,
                            Attributes = attributes != null ? new Dictionary<string, string>((IDictionary<string, string>)attributes) : null
                        });
                    }
                    _pendingTextWatcherMatches.Add(new PendingTextWatcherMatch
                    {
                        Watcher = watcher,
                        Depth = _ancestorNames.Count,
                        ElementName = elementName,
                        Attributes = attributes != null ? new Dictionary<string, string>((IDictionary<string, string>)attributes) : null,
                        ReservedSlot = reservedSlot,
                        SubtreeStack = subtreeStack,
                        IntermediateAncestors = intermediateAncestors
                    });
                }
                else
                {
                    watcher.OnElementMatch(elementName, attributes, textContent);
                }
            }

            // Check attribute match — attribute values are known at StartElement.
            // Intermediate-step predicates on the attribute path (e.g.
            // transaction[@x]/@value) are not supported here: this immediate
            // accumulation has no async predicate-evaluation hook, so rather than
            // accumulate unfiltered values we skip the match and let the case fall
            // back to its baseline. No current conformance case exercises this shape;
            // the scanner only emits IntermediatePredicates for paths it captured.
            if (watcher.IntermediatePredicates.Count == 0)
            {
                var attrName = watcher.PathMatcher.MatchesAttribute(_ancestorNames, elementName, watcher.ContextRootDepth);
                if (attrName != null && attributes != null)
                {
                    var attrValue = attributes.GetValueOrDefault(attrName);
                    if (attrValue != null)
                    {
                        watcher.OnElementMatch(elementName, attributes, attrValue);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Returns true when the watcher's aggregation reads element text content
    /// (and therefore needs deferral until EndElement). Count alone doesn't need
    /// text — only the value-consuming aggregations do.
    /// </summary>
    private static bool WatcherNeedsTextContent(StreamWatcher watcher) => watcher.Aggregation switch
    {
        WatcherAggregation.Sum or WatcherAggregation.Max or WatcherAggregation.Min
            or WatcherAggregation.Avg or WatcherAggregation.StringJoin
            or WatcherAggregation.Sequence or WatcherAggregation.Snapshot
            or WatcherAggregation.Head => true,
        _ => false
    };

    /// <summary>
    /// True when the numeric literal <paramref name="lit"/> equals <paramref name="position"/>
    /// — used to evaluate a bare positional predicate <c>[N]</c> on an intermediate step as
    /// <c>position() = N</c>. A non-integral bound (e.g. <c>[2.5]</c>) never selects a position.
    /// </summary>
    private static bool NumericLiteralEqualsPosition(XQueryExpression lit, int position) => lit switch
    {
        IntegerLiteral i => i.LongValue is { } l && l == position,
        DecimalLiteral d => d.Value == position,
        DoubleLiteral db => db.Value == position,
        _ => false
    };

    /// <summary>
    /// Evaluates a set of intermediate (ancestor) element-step predicate snapshots —
    /// forward-countable-positional (against the captured forward sibling position) or
    /// motionless (against the ancestor's name + attributes) — and returns true only when
    /// EVERY snapshot's predicates pass. Each ancestor is materialized as a lightweight
    /// leaf element (no children) purely so the in-memory predicate-evaluation machinery
    /// can run against a real XdmNode. Shared by the aggregation-watcher EndElement path
    /// (<see cref="FireWatchersEndElement"/>) and the for-each subscription dispatch.
    /// </summary>
    private async ValueTask<bool> EvaluateIntermediateAncestorPredicatesAsync(
        IReadOnlyList<IntermediateAncestorSnapshot> ancestors)
    {
        foreach (var anc in ancestors)
        {
            var ancElem = MaterializeLeafElement(anc.ElementName, anc.Attributes, string.Empty);
            var prevStreaming = _context._isStreamingExecution;
            _context._isStreamingExecution = false;
            // For a forward-countable positional predicate, supply the ancestor's captured
            // forward sibling position as the XPath context position so position()-based
            // comparisons (e.g. position() lt 4) decide correctly. Motionless (non-positional)
            // predicates keep the 1/1 context.
            int ctxPos = anc.IsPositional ? anc.ContextPosition : 1;
            _context.PushContextItem(ancElem, ctxPos, ctxPos);
            _context.PushCurrentItem(ancElem);
            bool pass = true;
            try
            {
                foreach (var pred in anc.Predicates)
                {
                    bool ok;
                    if (anc.IsPositional && pred is IntegerLiteral or DecimalLiteral or DoubleLiteral)
                    {
                        // A bare numeric predicate [N] is a positional shorthand for
                        // position() = N. EvaluateBooleanAsync would instead return the
                        // literal's effective boolean value (always true for non-zero),
                        // so compare the position directly here.
                        ok = NumericLiteralEqualsPosition(pred, anc.ContextPosition);
                    }
                    else
                    {
                        ok = await _context.EvaluateBooleanAsync(pred).ConfigureAwait(false);
                    }
                    if (!ok) { pass = false; break; }
                }
            }
            finally
            {
                _context.PopCurrentItem();
                _context.PopContextItem();
                _context._isStreamingExecution = prevStreaming;
            }
            if (!pass) return false;
        }
        return true;
    }

    /// <summary>
    /// Builds <see cref="IntermediateAncestorSnapshot"/> records for a for-each
    /// subscription's intermediate predicates from the CURRENT ancestor state
    /// (<see cref="_ancestorNames"/>/<see cref="_ancestorAttributes"/>/<see cref="_ancestorPositions"/>),
    /// captured at the matched leaf's StartElement — exactly the convention the watcher
    /// path uses (offset 1 == immediate parent; the matched leaf has NOT yet been pushed).
    /// Returns null when there is nothing to evaluate.
    /// </summary>
    private List<IntermediateAncestorSnapshot>? BuildSubscriptionIntermediateAncestors(
        IReadOnlyList<StreamingExpressionScanner.IntermediatePredicate> intermediatePredicates)
    {
        if (intermediatePredicates.Count == 0) return null;
        List<IntermediateAncestorSnapshot>? snapshots = null;
        foreach (var ip in intermediatePredicates)
        {
            int idx = _ancestorNames.Count - ip.AncestorOffset;
            if (idx < 0 || idx >= _ancestorNames.Count) continue;
            int contextPos = 0;
            if (ip.IsPositional && idx < _ancestorPositions.Count)
            {
                var pos = _ancestorPositions[idx];
                contextPos = ip.IsWildcardStep ? pos.ElementPos : pos.NamePos;
            }
            (snapshots ??= []).Add(new IntermediateAncestorSnapshot
            {
                ElementName = _ancestorNames[idx],
                Attributes = idx < _ancestorAttributes.Count ? _ancestorAttributes[idx] : null,
                Predicates = ip.Predicates,
                IsPositional = ip.IsPositional,
                ContextPosition = contextPos
            });
        }
        return snapshots;
    }

    /// <summary>
    /// Fires watchers for end element events (subtree tracking).
    /// </summary>
    private async ValueTask FireWatchersEndElement(string localName)
    {
        // Fire any pending text-content watcher matches whose element ends here.
        // entry.Depth was recorded at StartElement BEFORE the matching element was
        // pushed onto _ancestorNames. By the time we reach EndElement, the matching
        // element has been pushed and then popped, so _ancestorNames.Count is back
        // to that same starting depth. Walk backwards to allow safe in-place removal.
        var pendingDepth = _ancestorNames.Count;

        // First pass: for any pending match that has a non-root frame on its
        // subtree stack, the closing element is a nested child — flush its
        // pending text, materialize a real XdmElement, pop, and append the
        // materialized child to the new top frame. This must run for ALL open
        // pending matches (including ones whose root has not yet closed).
        foreach (var entry in _pendingTextWatcherMatches)
        {
            if (entry.SubtreeStack is not { Count: > 1 } stack) continue;
            var top = stack.Peek();
            // Frame's LocalName should match the closing element when our
            // tracking is in sync — defensive check guards against mismatches.
            if (top.LocalName != localName) continue;
            top.FlushPendingText(MakeStreamingText);
            stack.Pop();
            var materialized = MaterializeSubtreeFrame(top);
            var parent = stack.Peek();
            parent.FlushPendingText(MakeStreamingText);
            parent.Children.Add(materialized);
        }

        for (var i = _pendingTextWatcherMatches.Count - 1; i >= 0; i--)
        {
            var entry = _pendingTextWatcherMatches[i];
            if (entry.Depth != pendingDepth || entry.ElementName != localName) continue;

            // Materialize FIRST when predicates are present (or when the
            // Sequence/Snapshot path needs it anyway) so predicates can be
            // evaluated against a real XdmNode. The materialized element is
            // then reused both for predicate evaluation and (for
            // Sequence/Snapshot) slot population — we never materialize twice.
            XdmNode? materialized = null;
            bool needMaterialize = entry.Watcher.Predicates.Count > 0
                || (entry.Watcher.ValueAttribute == null
                    && entry.Watcher.Aggregation is WatcherAggregation.Snapshot or WatcherAggregation.Sequence);
            if (needMaterialize)
            {
                if (!entry.HasChildElement)
                {
                    materialized = MaterializeLeafElement(entry.ElementName, entry.Attributes, entry.TextBuffer.ToString());
                }
                else if (entry.SubtreeStack is { Count: 1 } rootStack)
                {
                    var rootFrame = rootStack.Pop();
                    rootFrame.FlushPendingText(MakeStreamingText);
                    materialized = MaterializeSubtreeFrame(rootFrame);
                }
            }

            // Evaluate motionless predicates against the materialized snapshot.
            // Mirrors ForEachSubscription's evaluation block in the streaming
            // dispatch path. If any predicate is false, the matched element is
            // dropped from the aggregation entirely.
            bool predicatesPass = true;
            if (entry.Watcher.Predicates.Count > 0 && materialized != null)
            {
                var prevStreaming = _context._isStreamingExecution;
                _context._isStreamingExecution = false;
                _context.PushContextItem(materialized, 1, 1);
                _context.PushCurrentItem(materialized);
                try
                {
                    foreach (var pred in entry.Watcher.Predicates)
                    {
                        if (!await _context.EvaluateBooleanAsync(pred).ConfigureAwait(false))
                        {
                            predicatesPass = false;
                            break;
                        }
                    }
                }
                finally
                {
                    _context.PopCurrentItem();
                    _context.PopContextItem();
                    _context._isStreamingExecution = prevStreaming;
                }
            }

            // Evaluate motionless intermediate (ancestor) predicates against the
            // snapshotted ancestor element (name + attributes only). e.g. the
            // [@CAT='P'] on ITEM above a matched PRICE. Each ancestor was captured
            // at StartElement when its attributes were fully known; we materialize a
            // lightweight leaf element (no children) for it here purely so the
            // existing predicate-evaluation machinery can run against a real XdmNode.
            if (predicatesPass && entry.IntermediateAncestors is { Count: > 0 } ancestors)
            {
                predicatesPass = await EvaluateIntermediateAncestorPredicatesAsync(ancestors)
                    .ConfigureAwait(false);
            }

            if (!predicatesPass)
            {
                // Discard the reserved slot if any, and skip slot population /
                // numeric / string-join accumulation. _pendingTextWatcherMatches
                // still needs to be removed at loop end.
                if (entry.ReservedSlot >= 0)
                    entry.Watcher.DiscardSequenceSlot(entry.ReservedSlot);
                _pendingTextWatcherMatches.RemoveAt(i);
                continue;
            }

            if (entry.ReservedSlot >= 0)
            {
                entry.Watcher.FillSequenceSlot(entry.ReservedSlot, entry.Attributes, entry.TextBuffer.ToString());
            }
            else
            {
                entry.Watcher.OnElementMatch(entry.ElementName, entry.Attributes, entry.TextBuffer.ToString());
            }
            // Snapshot/Sequence: fill the materialized-element slot we just
            // produced for predicate evaluation (or, when there were no
            // predicates, that we materialized purely for downstream consumers).
            if (entry.Watcher.ValueAttribute == null
                && entry.Watcher.Aggregation is WatcherAggregation.Snapshot or WatcherAggregation.Sequence
                && materialized != null)
            {
                if (entry.ReservedSlot >= 0)
                    entry.Watcher.FillSnapshotSlot(entry.ReservedSlot, materialized);
                else
                    entry.Watcher.OnLeafElementCaptured(materialized);
            }
            _pendingTextWatcherMatches.RemoveAt(i);
        }

        FireEndElementOnList(_watchers, localName);
        if (!ReferenceEquals(_context._activeStreamWatchers, _watchers))
            FireEndElementOnList(_context._activeStreamWatchers, localName);
    }

    private static void FireEndElementOnList(IReadOnlyList<StreamWatcher>? watchers, string localName)
    {
        if (watchers == null) return;
        foreach (var watcher in watchers)
        {
            if (watcher.IsCollectingSubtree)
            {
                var evt = new StreamXmlEvent(XmlNodeType.EndElement, localName, null, null, null);
                watcher.OnSubtreeEvent(evt);
            }
        }
    }

    /// <summary>
    /// Fires watchers for text events (subtree tracking and text content delivery).
    /// </summary>
    private void FireWatchersText(string textValue)
    {
        // Append text to any pending text-content watcher matches whose element
        // is currently open (depth matches the current ancestor depth + 1, since
        // text events appear at child-of-element level).
        if (_pendingTextWatcherMatches.Count > 0)
        {
            var textDepth = _ancestorNames.Count;
            foreach (var entry in _pendingTextWatcherMatches)
            {
                // Accumulate text from all descendants of the matched element
                // (XPath string-value of an element concatenates all descendant text).
                if (entry.Depth <= textDepth)
                {
                    entry.TextBuffer.Append(textValue);
                    // Mirror the text into the subtree builder so materialized
                    // copy-of(descendant::x) preserves real text-node children.
                    if (entry.SubtreeStack is { Count: > 0 } stack)
                    {
                        stack.Peek().PendingText.Append(textValue);
                    }
                }
            }
        }
        FireTextOnList(_watchers, textValue);
        if (!ReferenceEquals(_context._activeStreamWatchers, _watchers))
            FireTextOnList(_context._activeStreamWatchers, textValue);
    }

    private static void FireTextOnList(IReadOnlyList<StreamWatcher>? watchers, string textValue)
    {
        if (watchers == null) return;
        foreach (var watcher in watchers)
        {
            if (watcher.IsCollectingSubtree)
            {
                var evt = new StreamXmlEvent(XmlNodeType.Text, "", null, textValue, null);
                watcher.OnSubtreeEvent(evt);
            }
        }
    }

    private void CleanupStreamingNode(StreamingNodeContext ctx)
    {
        // Release in this order: remove XDM nodes from the store first (so any
        // late lookups by NodeId return null before we recycle the underlying
        // instances), then return XdmAttributes / lists / XdmElement to their pools,
        // then return inner attribute contexts and the outer element context.
        if (ctx.Attributes is { } ctxAttrs)
        {
            foreach (var attrCtx in ctxAttrs)
            {
                _nodeStore.Remove(attrCtx.NodeId);
                ReleaseNodeContext(attrCtx);
            }
            ReleaseNodeCtxList(ctxAttrs);
            ctx.Attributes = null;
        }
        _nodeStore.Remove(ctx.NodeId);

        // Pool the materialized XdmElement and its backing NodeId/XdmAttribute lists.
        // Capture references before releasing the element (which clears its Attributes).
        if (ctx.MaterializedElement is { } elem)
        {
            // Pool the attrIds list if it's the dynamic one we drew from the pool.
            if (elem.Attributes is List<NodeId> idList && idList.Count > 0)
                ReleaseNodeIdList(idList);
            ReleasePooledElement(elem);
            ctx.MaterializedElement = null;
        }
        if (ctx.MaterializedAttributes is { } materialized)
        {
            foreach (var attr in materialized)
                ReleasePooledAttribute(attr);
            ReleaseXdmAttrList(materialized);
        }
        ReleaseNodeContext(ctx);
    }

    /// <summary>
    /// Builds a minimal leaf <see cref="XdmElement"/> with a single text child and
    /// optional attributes, registered in the node store. Used by Snapshot/Sequence
    /// watchers that match leaf elements (e.g. <c>copy-of(/BOOKLIST/BOOKS/ITEM/PRICE)</c>)
    /// so downstream xsl:copy-of consumers receive a real element node.
    /// </summary>
    private XdmElement MaterializeLeafElement(string localName, IReadOnlyDictionary<string, string>? attributes, string textContent)
    {
        var documentId = new DocumentId(0);
        var elemId = _nodeStore.NextId();
        var attrIds = new List<NodeId>();
        if (attributes != null)
        {
            foreach (var (k, v) in attributes)
            {
                var attrId = _nodeStore.NextId();
                var attr = new XdmAttribute
                {
                    Id = attrId,
                    Document = documentId,
                    Namespace = _nodeStore.InternNamespace(string.Empty),
                    LocalName = k,
                    Prefix = null,
                    Value = v,
                    Parent = elemId
                };
                _nodeStore.Register(attr);
                attrIds.Add(attrId);
            }
        }

        var childIds = new List<NodeId>();
        if (!string.IsNullOrEmpty(textContent))
        {
            var textId = _nodeStore.NextId();
            var text = new XdmText
            {
                Id = textId,
                Document = documentId,
                Value = textContent,
                Parent = elemId
            };
            _nodeStore.Register(text);
            childIds.Add(textId);
        }

        var elem = new XdmElement
        {
            Id = elemId,
            Document = documentId,
            Namespace = _nodeStore.InternNamespace(string.Empty),
            LocalName = localName,
            Prefix = null,
            Attributes = attrIds.Count == 0 ? XdmElement.EmptyAttributes : attrIds,
            Children = childIds.Count == 0 ? XdmElement.EmptyChildren : childIds,
            NamespaceDeclarations = XdmElement.EmptyNamespaceDeclarations
        };
        elem._stringValue = textContent ?? string.Empty;
        _nodeStore.Register(elem);
        return elem;
    }

    private XdmText MakeStreamingText(string value)
    {
        var id = _nodeStore.NextId();
        var t = new XdmText
        {
            Id = id,
            Document = new DocumentId(0),
            Value = value
        };
        _nodeStore.Register(t);
        return t;
    }

    /// <summary>
    /// Materializes a <see cref="SubtreeBuilderFrame"/> (and its already-built
    /// children) into a real <see cref="XdmElement"/> registered in the node
    /// store. Used by Snapshot/Sequence watchers when copy-of(descendant::x)
    /// must yield element nodes with nested structure (sf-copy-of-027:
    /// outer &lt;n&gt; contains inner &lt;n&gt; children).
    /// </summary>
    private XdmElement MaterializeSubtreeFrame(SubtreeBuilderFrame frame)
    {
        var documentId = new DocumentId(0);
        var elemId = _nodeStore.NextId();

        var attrIds = new List<NodeId>();
        if (frame.Attributes != null)
        {
            foreach (var (k, v) in frame.Attributes)
            {
                var attrId = _nodeStore.NextId();
                var attr = new XdmAttribute
                {
                    Id = attrId,
                    Document = documentId,
                    Namespace = _nodeStore.InternNamespace(string.Empty),
                    LocalName = k,
                    Prefix = null,
                    Value = v,
                    Parent = elemId
                };
                _nodeStore.Register(attr);
                attrIds.Add(attrId);
            }
        }

        var childIds = new List<NodeId>();
        var stringValue = new StringBuilder();
        foreach (var child in frame.Children)
        {
            // Re-parent child node — the builder created nodes without a parent,
            // so finalize their Parent reference now that the element id is known.
            if (child is XdmElement childElem)
            {
                childElem.Parent = elemId;
                stringValue.Append(childElem.StringValue);
            }
            else if (child is XdmText childText)
            {
                childText.Parent = elemId;
                stringValue.Append(childText.Value);
            }
            childIds.Add(child.Id);
        }

        var elem = new XdmElement
        {
            Id = elemId,
            Document = documentId,
            Namespace = _nodeStore.InternNamespace(string.Empty),
            LocalName = frame.LocalName,
            Prefix = null,
            Attributes = attrIds.Count == 0 ? XdmElement.EmptyAttributes : attrIds,
            Children = childIds.Count == 0 ? XdmElement.EmptyChildren : childIds,
            NamespaceDeclarations = XdmElement.EmptyNamespaceDeclarations
        };
        elem._stringValue = stringValue.ToString();
        _nodeStore.Register(elem);
        return elem;
    }

    /// <summary>
    /// Enumerates the <see cref="XdmText"/> children of a materialized element in
    /// document order. Used by TextNodeTail subscriptions so the body iterates
    /// actual text nodes (with Parent intact) rather than the parent element.
    /// </summary>
    private IEnumerable<XdmText> EnumerateTextChildren(XdmElement element)
    {
        foreach (var childId in element.Children)
        {
            if (_nodeStore.GetNode(childId) is XdmText t)
                yield return t;
        }
    }

    // ---------------------------------------------------------------------
    // XdmText pool — reuses instances across the per-event Register/Remove
    // cycle to cut GC churn during long-running streamed transforms.
    // Instance-scoped; bounded; mutated via UnsafeAccessor since XdmText's
    // properties are init-only on the public surface.
    // ---------------------------------------------------------------------

    private const int MaxPooledTextNodes = 64;
    private readonly Stack<XdmText> _textPool = new(MaxPooledTextNodes);
    private static readonly DocumentId StreamingDocumentId = new(0);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Id")]
    private static extern void SetXdmNodeId(XdmNode node, NodeId value);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Value")]
    private static extern void SetXdmTextValue(XdmText node, string value);

    private XdmText AcquirePooledText(NodeId id, string value)
    {
        if (_textPool.TryPop(out var pooled))
        {
            SetXdmNodeId(pooled, id);
            SetXdmTextValue(pooled, value);
            // Parent is `set` on XdmNode (not init) — reset in case a prior caller stamped it.
            pooled.Parent = null;
            return pooled;
        }
        return new XdmText
        {
            Id = id,
            Document = StreamingDocumentId,
            Value = value
        };
    }

    private void ReleasePooledText(XdmText node)
    {
        if (_textPool.Count >= MaxPooledTextNodes) return;
        // Clear potentially-large string reference so the pooled slot doesn't
        // pin the previous text content alive while idle.
        SetXdmTextValue(node, string.Empty);
        node.Parent = null;
        _textPool.Push(node);
    }

    // ---------------------------------------------------------------------
    // XdmAttribute pool — same pattern as XdmText. Per-element materialization
    // burns one attribute per source attribute; pooling cuts that churn during
    // long streamed transforms.
    // ---------------------------------------------------------------------

    // Cached match-context delegates for FireAccumulatorRulesAsync. See comment there.
    private Func<NodeId, XdmNode?>? _accMatchNodeResolver;
    private Func<object, XQueryExpression, int, int, object?, bool>? _accMatchPredicateEvaluator;
    private Func<object, NodeTest, object?, (int, int)>? _accMatchPositionComputer;
    private Func<string, XQueryExpression, object, bool>? _accMatchKeyPatternEvaluator;
    private Func<XQueryExpression, object, bool>? _accMatchIdPatternEvaluator;

    private const int MaxPooledAttributes = 128;
    private readonly Stack<XdmAttribute> _attributePool = new(MaxPooledAttributes);

    // StreamingNodeContext pool. Per-event allocation churn (~16% of post-XsltContext-pool
    // streaming bench) was dominated by the per-element context object plus one per-attribute
    // inner context. Pool both; AcquireNodeContext resets fields, ReleaseNodeContext clears
    // refs and pushes back to the bounded stack.
    private const int MaxPooledNodeContexts = 256;
    private readonly Stack<StreamingNodeContext> _nodeContextPool = new(MaxPooledNodeContexts);

    private StreamingNodeContext AcquireNodeContext()
    {
        return _nodeContextPool.TryPop(out var pooled) ? pooled : new StreamingNodeContext();
    }

    private void ReleaseNodeContext(StreamingNodeContext ctx)
    {
        if (_nodeContextPool.Count >= MaxPooledNodeContexts) return;
        // Clear refs so the pooled slot doesn't pin garbage and an accidental reuse-before-reset
        // fails loudly instead of silently inheriting stale state.
        ctx.LocalName = "";
        ctx.NamespaceUri = "";
        ctx.Prefix = "";
        ctx.StringValue = null;
        ctx.Attributes = null;
        ctx.NamespaceDeclarations = null;
        ctx.Parent = null;
        ctx.MaterializedAttributes = null;
        ctx.Position = 1;
        ctx.NodeKind = default;
        ctx.NodeId = default;
        ctx.Depth = 0;
        _nodeContextPool.Push(ctx);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Namespace")]
    private static extern void SetXdmAttrNamespace(XdmAttribute node, NamespaceId value);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_LocalName")]
    private static extern void SetXdmAttrLocalName(XdmAttribute node, string value);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Prefix")]
    private static extern void SetXdmAttrPrefix(XdmAttribute node, string? value);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Value")]
    private static extern void SetXdmAttrValue(XdmAttribute node, string value);

    private XdmAttribute AcquirePooledAttribute(NodeId id, NamespaceId ns, string localName, string? prefix, string value)
    {
        if (_attributePool.TryPop(out var pooled))
        {
            SetXdmNodeId(pooled, id);
            SetXdmAttrNamespace(pooled, ns);
            SetXdmAttrLocalName(pooled, localName);
            SetXdmAttrPrefix(pooled, prefix);
            SetXdmAttrValue(pooled, value);
            pooled.Parent = null;
            return pooled;
        }
        return new XdmAttribute
        {
            Id = id,
            Document = StreamingDocumentId,
            Namespace = ns,
            LocalName = localName,
            Prefix = prefix,
            Value = value,
        };
    }

    private void ReleasePooledAttribute(XdmAttribute attr)
    {
        if (_attributePool.Count >= MaxPooledAttributes) return;
        // Drop large string references so a pooled slot doesn't pin user data.
        SetXdmAttrLocalName(attr, string.Empty);
        SetXdmAttrPrefix(attr, null);
        SetXdmAttrValue(attr, string.Empty);
        attr.Parent = null;
        _attributePool.Push(attr);
    }

    // ---------------------------------------------------------------------
    // XdmElement pool. Each MaterializeElement allocated a fresh element; profiling
    // showed XdmElement at ~17% of post-Scope-pool streaming alloc. Safe under
    // streaming mode (XSLT 3.0 §19) — templates cannot retain non-grounded node refs
    // across element boundaries, so reuse via the pool can't be observed externally.
    // The associated NodeId / XdmAttribute / NamespaceBinding lists are also stashed
    // and Clear()'d on release so their backing arrays are reused.
    // ---------------------------------------------------------------------

    private const int MaxPooledElements = 64;
    private readonly Stack<XdmElement> _elementPool = new(MaxPooledElements);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Namespace")]
    private static extern void SetXdmElemNamespace(XdmElement node, NamespaceId value);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_LocalName")]
    private static extern void SetXdmElemLocalName(XdmElement node, string value);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Prefix")]
    private static extern void SetXdmElemPrefix(XdmElement node, string? value);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Attributes")]
    private static extern void SetXdmElemAttributes(XdmElement node, IReadOnlyList<NodeId> value);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Children")]
    private static extern void SetXdmElemChildren(XdmElement node, IReadOnlyList<NodeId> value);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_NamespaceDeclarations")]
    private static extern void SetXdmElemNamespaceDeclarations(XdmElement node, IReadOnlyList<NamespaceBinding> value);

    private XdmElement AcquirePooledElement(
        NodeId id, NamespaceId ns, string localName, string? prefix,
        IReadOnlyList<NodeId> attributes, IReadOnlyList<NamespaceBinding> nsDecls)
    {
        if (_elementPool.TryPop(out var pooled))
        {
            SetXdmNodeId(pooled, id);
            SetXdmElemNamespace(pooled, ns);
            SetXdmElemLocalName(pooled, localName);
            SetXdmElemPrefix(pooled, prefix);
            SetXdmElemAttributes(pooled, attributes);
            SetXdmElemChildren(pooled, XdmElement.EmptyChildren);
            SetXdmElemNamespaceDeclarations(pooled, nsDecls);
            pooled.Parent = null;
            pooled._stringValue = null;
            return pooled;
        }
        return new XdmElement
        {
            Id = id,
            Document = StreamingDocumentId,
            Namespace = ns,
            LocalName = localName,
            Prefix = prefix,
            Attributes = attributes,
            Children = XdmElement.EmptyChildren,
            NamespaceDeclarations = nsDecls,
        };
    }

    private void ReleasePooledElement(XdmElement elem)
    {
        if (_elementPool.Count >= MaxPooledElements) return;
        // Drop list refs so the pool slot doesn't pin reusable list backings while idle,
        // and clear string refs.
        SetXdmElemLocalName(elem, string.Empty);
        SetXdmElemPrefix(elem, null);
        SetXdmElemAttributes(elem, XdmElement.EmptyAttributes);
        SetXdmElemNamespaceDeclarations(elem, XdmElement.EmptyNamespaceDeclarations);
        elem.Parent = null;
        elem._stringValue = null;
        _elementPool.Push(elem);
    }

    // Pool of List<NodeId> and List<XdmAttribute> reused inside MaterializeElement.
    // Lists are cleared on release; their backing array is retained so the next
    // element with the same/smaller attribute count avoids growing the buffer.
    private const int MaxPooledLists = 32;
    private readonly Stack<List<NodeId>> _nodeIdListPool = new(MaxPooledLists);
    private readonly Stack<List<XdmAttribute>> _xdmAttrListPool = new(MaxPooledLists);

    private List<NodeId> AcquireNodeIdList() =>
        _nodeIdListPool.TryPop(out var pooled) ? pooled : new List<NodeId>();

    private void ReleaseNodeIdList(List<NodeId> list)
    {
        if (_nodeIdListPool.Count >= MaxPooledLists) return;
        list.Clear();
        _nodeIdListPool.Push(list);
    }

    private List<XdmAttribute> AcquireXdmAttrList() =>
        _xdmAttrListPool.TryPop(out var pooled) ? pooled : new List<XdmAttribute>();

    private void ReleaseXdmAttrList(List<XdmAttribute> list)
    {
        if (_xdmAttrListPool.Count >= MaxPooledLists) return;
        list.Clear();
        _xdmAttrListPool.Push(list);
    }

    private readonly Stack<List<StreamingNodeContext>> _nodeCtxListPool = new(MaxPooledLists);

    private List<StreamingNodeContext> AcquireNodeCtxList() =>
        _nodeCtxListPool.TryPop(out var pooled) ? pooled : new List<StreamingNodeContext>();

    private void ReleaseNodeCtxList(List<StreamingNodeContext> list)
    {
        if (_nodeCtxListPool.Count >= MaxPooledLists) return;
        list.Clear();
        _nodeCtxListPool.Push(list);
    }

    /// <summary>
    /// Materializes a <see cref="StreamingNodeContext"/> into a registered <see cref="XdmElement"/>,
    /// drawing per-attribute <see cref="XdmAttribute"/> instances from the processor's pool.
    /// Stashes the materialized attribute refs on the context so <see cref="CleanupStreamingNode"/>
    /// can return them when the element closes.
    /// </summary>
    private XdmElement MaterializeElement(StreamingNodeContext ctx)
    {
        var nsId = _nodeStore.InternNamespace(ctx.NamespaceUri);
        List<NodeId>? attrIds = null;
        List<XdmAttribute>? materializedAttrs = null;

        if (ctx.Attributes is { } ctxAttrs)
        {
            foreach (var attr in ctxAttrs)
            {
                var attrNsId = _nodeStore.InternNamespace(attr.NamespaceUri);
                var xdmAttr = AcquirePooledAttribute(
                    attr.NodeId, attrNsId, attr.LocalName, attr.Prefix, attr.StringValue ?? string.Empty);
                _nodeStore.Register(xdmAttr);
                (attrIds ??= AcquireNodeIdList()).Add(attr.NodeId);
                (materializedAttrs ??= AcquireXdmAttrList()).Add(xdmAttr);
            }
        }
        ctx.MaterializedAttributes = materializedAttrs;

        // Namespace declarations are rare in streaming workloads; keep them as a
        // fresh List for now (no observed alloc pressure post-pool work).
        List<NamespaceBinding>? nsBindings = null;
        if (ctx.NamespaceDeclarations is { } ctxNs)
        {
            foreach (var (prefix, uri) in ctxNs)
            {
                (nsBindings ??= new List<NamespaceBinding>())
                    .Add(new NamespaceBinding(prefix, _nodeStore.InternNamespace(uri)));
            }
        }

        var elem = AcquirePooledElement(
            ctx.NodeId, nsId, ctx.LocalName, ctx.Prefix,
            (IReadOnlyList<NodeId>?)attrIds ?? XdmElement.EmptyAttributes,
            (IReadOnlyList<NamespaceBinding>?)nsBindings ?? XdmElement.EmptyNamespaceDeclarations);
        ctx.MaterializedElement = elem;
        _nodeStore.Register(elem);
        return elem;
    }
}
