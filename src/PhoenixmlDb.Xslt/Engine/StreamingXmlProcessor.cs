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
    private readonly XsltTransformEngine.InMemoryNodeStore _nodeStore;
    private readonly QName? _mode;
    private ulong _nextNodeId = 1_000_000; // High start to avoid collision with tree nodes

    // Accumulator state for streaming computation
    private readonly IReadOnlyList<XsltAccumulator> _accumulators;
    private object?[]? _accCurrentValues;
    private Dictionary<NodeId, (object? before, object? after)>[]? _accNodeValueMaps;

    public StreamingXmlProcessor(
        XsltStylesheet stylesheet,
        TemplateIndex templateIndex,
        DefaultXsltExecutionContext context,
        XsltTransformEngine.InMemoryNodeStore nodeStore,
        QName? mode,
        IReadOnlyList<XsltAccumulator>? accumulators = null)
    {
        _stylesheet = stylesheet;
        _templateIndex = templateIndex;
        _context = context;
        _nodeStore = nodeStore;
        _mode = mode;
        _accumulators = accumulators ?? Array.Empty<XsltAccumulator>();
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

        // Initialize accumulator state
        await InitializeAccumulatorsAsync().ConfigureAwait(false);

        // Set streaming execution flag so built-in templates skip child recursion
        var previousStreamingFlag = _context._isStreamingExecution;
        _context._isStreamingExecution = true;
        try
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();

                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                    {
                        var nodeId = new NodeId(_nextNodeId++);
                        var isEmptyElement = reader.IsEmptyElement;

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
                        var elementDepth = reader.Depth;
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

                        await _context.MatchAndExecuteStreamingNodeAsync(xdmElem, _mode, current.Position)
                            .ConfigureAwait(false);

                        if (!isEmptyElement)
                        {
                            ancestorStack.Push(current);
                        }
                        else
                        {
                            // Self-closing: fire end-phase rules, close any deferred tag, then clean up
                            await FireAccumulatorRulesAsync(xdmElem, nodeId, AccumulatorPhase.End).ConfigureAwait(false);
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
                            // Reset sibling counter for children of this closing element.
                            // Children are at depth = closingContext.Depth + 1.
                            siblingCountByDepth.Remove(closingContext.Depth + 1);
                            // Fire end-phase accumulator rules
                            var closingNode = _nodeStore.GetNode(closingContext.NodeId);
                            if (closingNode != null)
                                await FireAccumulatorRulesAsync(closingNode, closingContext.NodeId, AccumulatorPhase.End).ConfigureAwait(false);

                            // Write the deferred closing tag for elements opened by shallow-copy.
                            // The built-in shallow-copy template writes only the start tag and
                            // pushes the qname; we close it here so children are properly nested.
                            if (_context._streamingOpenElements.Count > 0)
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
                    {
                        // Text nodes in streaming: create temporary text node, match templates
                        var textNodeId = new NodeId(_nextNodeId++);
                        var textNode = new XdmText
                        {
                            Id = textNodeId,
                            Document = new DocumentId(0),
                            Value = reader.Value
                        };
                        _nodeStore.Register(textNode);

                        // Fire accumulator rules for text nodes
                        await FireAccumulatorRulesAsync(textNode, textNodeId, AccumulatorPhase.Start).ConfigureAwait(false);

                        // Match text node templates (e.g., text() match patterns)
                        await _context.MatchAndExecuteStreamingNodeAsync(textNode, _mode, 1)
                            .ConfigureAwait(false);

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
                        await _context.MatchAndExecuteStreamingNodeAsync(comment, _mode, 1)
                            .ConfigureAwait(false);
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
                        await _context.MatchAndExecuteStreamingNodeAsync(pi, _mode, 1)
                            .ConfigureAwait(false);
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

    private void CleanupStreamingNode(StreamingNodeContext ctx)
    {
        // Remove temporary XDM nodes to free memory
        foreach (var attr in ctx.Attributes)
            _nodeStore.Remove(attr.NodeId);
        _nodeStore.Remove(ctx.NodeId);
    }
}
