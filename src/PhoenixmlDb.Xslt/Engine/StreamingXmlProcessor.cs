using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Ast;

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
    private readonly QName? _mode;
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
                            // Build minimal node for accumulator rules
                            var skipAttrs = new List<StreamingNodeContext>();
                            var skipNsDecls = new Dictionary<string, string>();
                            if (reader.HasAttributes)
                            {
                                for (int i = 0; i < reader.AttributeCount; i++)
                                {
                                    reader.MoveToAttribute(i);
                                    if (reader.Prefix == "xmlns" || (reader.Prefix.Length == 0 && reader.LocalName == "xmlns"))
                                        skipNsDecls[reader.Prefix == "xmlns" ? reader.LocalName : ""] = reader.Value;
                                    else
                                    {
                                        var skipAttrId = new NodeId(_nextNodeId++);
                                        skipAttrs.Add(new StreamingNodeContext
                                        {
                                            NodeKind = XdmNodeKind.Attribute,
                                            LocalName = reader.LocalName,
                                            NamespaceUri = reader.NamespaceURI,
                                            Prefix = reader.Prefix,
                                            StringValue = reader.Value,
                                            NodeId = skipAttrId,
                                            Depth = elementDepth + 1
                                        });
                                    }
                                }
                                reader.MoveToElement();
                            }
                            var skipCtx = new StreamingNodeContext
                            {
                                NodeKind = XdmNodeKind.Element,
                                LocalName = reader.LocalName,
                                NamespaceUri = reader.NamespaceURI,
                                Prefix = reader.Prefix,
                                NodeId = nodeId,
                                Attributes = skipAttrs,
                                NamespaceDeclarations = skipNsDecls,
                                Depth = elementDepth
                            };
                            var skipXdm = skipCtx.ToXdmElement(_nodeStore);
                            await FireAccumulatorRulesAsync(skipXdm, nodeId, AccumulatorPhase.Start).ConfigureAwait(false);
                            // Fire stream watchers in the suppressed branch too — when a
                            // parent template was deferred (consuming aggregates pending
                            // execution at parent EndElement), child elements feed the
                            // watchers but don't fire their own templates. Without this
                            // the count(*) / sum(*) etc. would never accumulate.
                            if (_watchers != null || _context._activeStreamWatchers != null)
                            {
                                Dictionary<string, string>? skipAttrDict = null;
                                if (skipAttrs.Count > 0)
                                {
                                    skipAttrDict = new Dictionary<string, string>(skipAttrs.Count);
                                    foreach (var attr in skipAttrs)
                                        skipAttrDict[attr.LocalName] = attr.StringValue ?? "";
                                }
                                FireWatchers(skipCtx.LocalName, skipAttrDict, null);
                            }
                            if (!isEmptyElement)
                            {
                                ancestorStack.Push(skipCtx);
                                _ancestorNames.Add(skipCtx.LocalName);
                            }
                            else
                            {
                                await FireAccumulatorRulesAsync(skipXdm, nodeId, AccumulatorPhase.End).ConfigureAwait(false);
                                if (_watchers != null || _context._activeStreamWatchers != null)
                                    FireWatchersEndElement(skipCtx.LocalName);
                                CleanupStreamingNode(skipCtx);
                            }
                            break;
                        }

                        // Collect attributes and namespace declarations
                        var attrs = new List<StreamingNodeContext>();
                        var nsDecls = new Dictionary<string, string>();
                        if (reader.HasAttributes)
                        {
                            for (int i = 0; i < reader.AttributeCount; i++)
                            {
                                reader.MoveToAttribute(i);
                                if (reader.Prefix == "xmlns" || (reader.Prefix.Length == 0 && reader.LocalName == "xmlns"))
                                {
                                    var prefix = reader.Prefix == "xmlns" ? reader.LocalName : "";
                                    nsDecls[prefix] = reader.Value;
                                }
                                else
                                {
                                    attrs.Add(new StreamingNodeContext
                                    {
                                        NodeKind = XdmNodeKind.Attribute,
                                        LocalName = reader.LocalName,
                                        NamespaceUri = reader.NamespaceURI,
                                        Prefix = reader.Prefix,
                                        StringValue = reader.Value,
                                        NodeId = new NodeId(_nextNodeId++),
                                        Depth = reader.Depth + 1
                                    });
                                }
                            }
                            reader.MoveToElement();
                        }

                        // Track sibling position: increment counter for this depth
                        if (!siblingCountByDepth.TryGetValue(elementDepth, out var siblingCount))
                            siblingCount = 0;
                        siblingCount++;
                        siblingCountByDepth[elementDepth] = siblingCount;

                        var current = new StreamingNodeContext
                        {
                            NodeKind = XdmNodeKind.Element,
                            LocalName = reader.LocalName,
                            NamespaceUri = reader.NamespaceURI,
                            Prefix = reader.Prefix,
                            NodeId = nodeId,
                            Attributes = attrs,
                            NamespaceDeclarations = nsDecls,
                            Parent = ancestorStack.Count > 0 ? ancestorStack.Peek() : null,
                            Depth = elementDepth,
                            Position = siblingCount
                        };

                        // Match and execute template
                        var xdmElem = current.ToXdmElement(_nodeStore);

                        // Fire start-phase accumulator rules before template execution
                        await FireAccumulatorRulesAsync(xdmElem, nodeId, AccumulatorPhase.Start).ConfigureAwait(false);

                        // Fire stream watchers for element match
                        if (_watchers != null)
                        {
                            // Build attribute dictionary for watcher matching
                            Dictionary<string, string>? attrDict = null;
                            if (attrs.Count > 0)
                            {
                                attrDict = new Dictionary<string, string>(attrs.Count);
                                foreach (var attr in attrs)
                                    attrDict[attr.LocalName] = attr.StringValue ?? "";
                            }
                            FireWatchers(current.LocalName, attrDict, null);
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
                                    // Body executes against a buffered snapshot, not the live
                                    // reader — temporarily clear the streaming flag so descendant
                                    // navigation uses the normal in-memory path.
                                    var prevStreaming = _context._isStreamingExecution;
                                    _context._isStreamingExecution = false;
                                    try
                                    {
                                        foreach (var sub in matched)
                                        {
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
                                                    _context.PushContextItem(textChild, 1, 1);
                                                    _context.PushCurrentItem(textChild);
                                                    try
                                                    {
                                                        await sub.Body.ExecuteAsync(_context).ConfigureAwait(false);
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
                                                _context.PushContextItem(snapshot, 1, 1);
                                                _context.PushCurrentItem(snapshot);
                                                try
                                                {
                                                    await sub.Body.ExecuteAsync(_context).ConfigureAwait(false);
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
                                        FireWatchersEndElement(current.LocalName);
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
                                FireWatchersEndElement(current.LocalName);
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
                                FireWatchersEndElement(current.LocalName);
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
                                FireWatchersEndElement(closingContext.LocalName);
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

                        // Text nodes in streaming: create temporary text node, match templates
                        var textNodeId = new NodeId(_nextNodeId++);
                        var textNode = new XdmText
                        {
                            Id = textNodeId,
                            Document = new DocumentId(0),
                            Value = textValue
                        };
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

        var matchContext = new XsltContext
        {
            CurrentNode = node,
            Position = 1,
            Last = 1,
            NodeResolver = id => _nodeStore.GetNode(id),
            PredicateEvaluator = _context.EvaluatePatternPredicate,
            PositionComputer = _context.ComputeNodePosition,
            KeyPatternEvaluator = _context.EvaluateKeyPattern,
            IdPatternEvaluator = _context.EvaluateIdPattern
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
    }

    private readonly List<PendingTextWatcherMatch> _pendingTextWatcherMatches = [];

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

        foreach (var watcher in watchers)
        {
            // Check for subtree collection in progress
            if (watcher.IsCollectingSubtree)
            {
                var evt = new StreamXmlEvent(XmlNodeType.Element, elementName, null, null, attributes);
                watcher.OnSubtreeEvent(evt);
                continue;
            }

            // Check element path match
            if (watcher.PathMatcher.Matches(_ancestorNames, elementName))
            {
                if (watcher.ValueAttribute == null && WatcherNeedsTextContent(watcher))
                {
                    // Defer the OnElementMatch until EndElement so accumulated text
                    // content from intervening Text events is available. Without
                    // this, sum(n) / string-join(n, ',') over text-content elements
                    // never receive the text and aggregate to null/empty.
                    _pendingTextWatcherMatches.Add(new PendingTextWatcherMatch
                    {
                        Watcher = watcher,
                        Depth = _ancestorNames.Count,
                        ElementName = elementName,
                        Attributes = attributes != null ? new Dictionary<string, string>((IDictionary<string, string>)attributes) : null
                    });
                }
                else
                {
                    watcher.OnElementMatch(elementName, attributes, textContent);
                }
            }

            // Check attribute match — attribute values are known at StartElement
            var attrName = watcher.PathMatcher.MatchesAttribute(_ancestorNames, elementName);
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
    /// Fires watchers for end element events (subtree tracking).
    /// </summary>
    private void FireWatchersEndElement(string localName)
    {
        // Fire any pending text-content watcher matches whose element ends here.
        // entry.Depth was recorded at StartElement BEFORE the matching element was
        // pushed onto _ancestorNames. By the time we reach EndElement, the matching
        // element has been pushed and then popped, so _ancestorNames.Count is back
        // to that same starting depth. Walk backwards to allow safe in-place removal.
        var pendingDepth = _ancestorNames.Count;
        for (var i = _pendingTextWatcherMatches.Count - 1; i >= 0; i--)
        {
            var entry = _pendingTextWatcherMatches[i];
            if (entry.Depth != pendingDepth || entry.ElementName != localName) continue;
            entry.Watcher.OnElementMatch(entry.ElementName, entry.Attributes, entry.TextBuffer.ToString());
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
                    entry.TextBuffer.Append(textValue);
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
        // Remove temporary XDM nodes to free memory
        foreach (var attr in ctx.Attributes)
            _nodeStore.Remove(attr.NodeId);
        _nodeStore.Remove(ctx.NodeId);
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
}
