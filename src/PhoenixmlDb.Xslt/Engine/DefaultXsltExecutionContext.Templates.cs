using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xdm.Serialization;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
// XPath 4.0 ordered map: insertion-order iteration as a structural guarantee.
// xslt keeps its existing default key-equality (pass EqualityComparer<object>.Default
// at each construction site) — this change is about iteration order only.
using OrderedXdmMap = PhoenixmlDb.XQuery.Execution.OrderedXdmMap;

namespace PhoenixmlDb.Xslt.Engine;

internal sealed partial class DefaultXsltExecutionContext
{

    public override async ValueTask ApplyTemplatesAsync(
        XQueryExpression? select,
        QName? mode,
        List<XsltSort> sorts,
        List<XsltWithParam> withParams)
    {
        CheckResourceLimits();
        if (_recursionDepth >= MaxRecursionDepth)
            return;

        _recursionDepth++;
        try
        {
            await ApplyTemplatesCoreAsync(select, mode, sorts, withParams).ConfigureAwait(false);
        }
        finally
        {
            _recursionDepth--;
        }
    }


    private async ValueTask ApplyTemplatesCoreAsync(
        XQueryExpression? select,
        QName? mode,
        List<XsltSort> sorts,
        List<XsltWithParam> withParams)
    {
        // Streaming interception: when a streaming processor is active and
        // apply-templates is called on the document's children (select is null, or a
        // striding child-axis select like ./account naming top-level elements), delegate
        // to the streaming processor. The processor's forward pass offers each top-level
        // element to template matching, so a striding select restricting to a named child
        // dispatches exactly the templates that match that name (si-result-document-301/
        // 303/304, where the matched account template body writes an xsl:result-document).
        if (_activeStreamingProcessor != null && _activeStreamingReader != null
            && (select == null || IsDocumentLevelStridingSelect(select)))
        {
            var ci = ContextItem;
            if (ci is XdmDocument)
            {
                var proc = _activeStreamingProcessor;
                var rdr = _activeStreamingReader;
                var ct = _activeStreamingCancellationToken;
                // Clear the active processor so it's not re-triggered during
                // the streaming pass (the processor calls MatchAndExecuteStreamingNodeAsync
                // which may invoke templates that call apply-templates again).
                _activeStreamingProcessor = null;
                _activeStreamingReader = null;
                // Phase 2a (#143): propagate THIS apply-templates' explicit mode into the
                // processor's forward pass so a wrapped/conditional apply-templates carrying
                // mode="t" (deep-copy) etc. dispatches under that mode's template-matching and
                // built-in on-no-match rules — not the processor's construction-time mode
                // (the enclosing template's mode). Restored after the pass.
                var savedDispatchMode = proc.DispatchMode;
                if (mode != null)
                    proc.DispatchMode = mode;
                // Forward this apply-templates' with-params into the processor-driven
                // descent (si-apply-templates-008/009). The built-in shallow-copy rule
                // forwards all params unchanged at every level, so a single ambient set
                // covers the whole streamed pass.
                var savedForwardedParams = _streamingForwardedParams;
                _streamingForwardedParams = withParams;
                try
                {
                    await proc.ProcessAsync(rdr, ct).ConfigureAwait(false);
                }
                finally
                {
                    _streamingForwardedParams = savedForwardedParams;
                    proc.DispatchMode = savedDispatchMode;
                }
                return;
            }
        }

        // Streaming interception: a striding DOWNWARD-path apply-templates whose context
        // is the streamed document node (e.g. select="root/item"). The single-step case
        // (select="item") is routed through the processor above; a multi-step descent
        // cannot be, because the processor's forward pass only offers TOP-LEVEL elements to
        // matching — it does not descend to a grandchild set. Drive the reader directly:
        // navigate the intermediate steps (read start-tags, descend), and per element
        // selected by the FINAL step, run the matched template body via
        // MatchAndExecuteStreamingNodeAsync. This closes streamed apply-templates for the
        // simple (non-recursive-body) striding-descent case and, transitively, the
        // apply-imports/next-match cases whose bodies were never reached. Only reached when
        // the body executes directly (document-level match="/"), where the processor is
        // active but _isStreamingExecution is not yet set.
        if (_activeStreamingProcessor != null && _activeStreamingReader != null
            && ContextItem is XdmDocument
            && TryGetStridingDescentSteps(select) is { } descentSteps)
        {
            var proc = _activeStreamingProcessor;
            var rdr = _activeStreamingReader;
            var ct = _activeStreamingCancellationToken;
            // Clear the active processor for the duration of the descent so a nested
            // apply-templates in a matched body doesn't re-trigger the doc-level pass.
            _activeStreamingProcessor = null;
            var savedStreamingExec = _isStreamingExecution;
            _isStreamingExecution = true;
            var savedForwardedParams = _streamingForwardedParams;
            _streamingForwardedParams = withParams;
            try
            {
                await DriveStridingDescentLevelAsync(
                    rdr, descentSteps, 0, mode, ct).ConfigureAwait(false);
            }
            finally
            {
                _streamingForwardedParams = savedForwardedParams;
                _isStreamingExecution = savedStreamingExec;
                _activeStreamingProcessor = proc;
            }
            return;
        }

        // Streaming interception inside a template body: when we're already inside
        // the streaming processor's pass and apply-templates is called on a
        // consuming select (children of the current node), drive the reader directly
        // to consume each child event, fire templates inline, and stop at the parent's
        // EndElement. This makes the body of a user template see the correct ordering
        // (`<pre/> apply-templates <post/>` puts `<post/>` AFTER processed children,
        // not before). Without this, apply-templates returned immediately, `<post/>`
        // emitted, then the processor's outer loop read the children as siblings.
        if (_isStreamingExecution && _activeStreamingReader != null
            && IsConsumingChildSelect(select))
        {
            await ApplyTemplatesStreamingAsync(mode, withParams).ConfigureAwait(false);
            return;
        }

        // Get nodes to process
        IEnumerable<object> nodes;
        if (select != null)
        {
            var result = await EvaluateAsync(select).ConfigureAwait(false);
            nodes = result switch
            {
                null => [],
                // An XDM array (List<object?>) or map (IDictionary) is a single item:
                // apply-templates processes it as one item, it does NOT iterate the
                // array's members or the map's entries. Everything else that is
                // IEnumerable<object> (notably object?[], the engine's sequence
                // representation) is iterated. Without these guards a JSON array fed as
                // the context item was spread into its members and the template fired
                // once per member (Martin Honnen 2026-06-14).
                List<object?> arr => [arr],
                System.Collections.IDictionary dict => [dict],
                IEnumerable<object> seq => seq,
                object obj => [obj]
            };
        }
        else
        {
            // Default: children of context node — context item must be a node (XTTE0510)
            var ci = ContextItem;
            if (ci != null && ci is not XdmNode && ci is not ResultTreeFragment
                && !ReferenceEquals(ci, PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus))
                throw Error("XTTE0510: Context item for xsl:apply-templates must be a node");
            nodes = GetChildren(ci);
        }

        // Apply sorts if specified
        if (sorts.Count > 0)
        {
            nodes = await SortNodesAsync(nodes, sorts).ConfigureAwait(false);
        }

        var nodeList = nodes.ToList();

        // Expand any ResultTreeFragments to their XDM document nodes
        var expandedNodes = new List<object>();
        foreach (var item in nodeList)
        {
            if (item is ResultTreeFragment rtf)
            {
                // Parse RTF to XDM document node for template matching
                var docNode = ParseResultTreeFragment(rtf);
                if (docNode != null)
                    expandedNodes.Add(docNode);
            }
            else if (item is Xdm.TextNodeItem tni && _nodeStore != null)
            {
                // TextNodeItem is an internal MARKER — a bare string that lets the sequence
                // accumulator tell a text node apart from an atomic string (XSLT 3.0 §5.7.2).
                // It carries no identity, parent or store, so it is not a node as far as pattern
                // matching is concerned: match="text()" and even match="node()" both miss it, and
                // a mode with on-no-match="fail" then raises XTDE0555 for a node the stylesheet
                // plainly handles.
                //
                // Materialize it into a real text node here, at the same boundary where a
                // ResultTreeFragment is already expanded for exactly the same reason. The marker
                // is an optimization internal to sequence construction; it must not survive into
                // template matching, where the rest of the engine reasonably assumes a node.
                //
                // XSpec hits this whenever a scenario's result contains text nodes: its
                // local:report-node mode declares on-no-match="fail", so the whole suite dies.
                var textId = _nodeStore.NextId();
                var text = new XdmText
                {
                    Id = textId,
                    Document = DocumentId.None,
                    Parent = NodeId.None,
                    Value = tni.Value,
                };
                _nodeStore.Register(text);
                expandedNodes.Add(text);
            }
            else
            {
                expandedNodes.Add(item);
            }
        }

        // Pre-evaluate with-param values in the CALLING context (before the per-node loop changes context)
        var preEvaluatedParams = new Dictionary<QName, object?>();
        foreach (var param in withParams)
        {
            preEvaluatedParams[param.Name] = await EvaluateWithParamAsync(param).ConfigureAwait(false);
        }

        var position = 0;
        var lastTemplateResultWasAtomic = false; // Track for space-separating adjacent atomic values
        foreach (var node in expandedNodes)
        {
            CheckResourceLimits();
            position++;
            PushContextItem(node, position, expandedNodes.Count);
            PushCurrentItem(node); // For XSLT current() function
            PushScope();

            // XTDE3480: Clear merge-group context — not available in applied templates
            ClearMergeGroupContext();

            try
            {
                // Propagate tunnel parameters: first inherit from parent scopes,
                // then override with explicitly provided tunnel params.
                // NOTE: We only store in TunnelParameters here, NOT as variables.
                // Variables are bound later based on each template param's tunnel flag.
                InheritTunnelParameters();
                foreach (var param in withParams.Where(p => p.Tunnel))
                {
                    var value = preEvaluatedParams[param.Name];
                    _scopes.Peek().TunnelParameters[param.Name] = value;
                }

                // Find matching template
                XsltTemplate? template;
                using (var mc = AcquireMatchContext())
                    template = _templateIndex.FindMatchingTemplate(node, mode, mc.Value);

                // XTDE0540: Check on-multiple-match="fail"
                if (template != null)
                {
                    var modeKey = mode ?? new QName(NamespaceId.None, "");
                    if (_stylesheet.Modes.TryGetValue(modeKey, out var modeDecl2) &&
                        modeDecl2.OnMultipleMatch == OnMultipleMatchBehavior.Fail)
                    {
                        // Check if there's another matching template at the same priority.
                        // Skip union siblings: matching two branches of a union pattern
                        // doesn't count as multiple match (spec bug 30402).
                        XsltTemplate? next;
                        using (var mc = AcquireMatchContext())
                            next = _templateIndex.FindMatchingTemplate(node, mode, mc.Value, template);
                        while (next != null
                            && template.UnionGroupId != null
                            && next.UnionGroupId == template.UnionGroupId
                            && TemplateIndex.SameConflictRank(next, template))
                        {
                            using var mc2 = AcquireMatchContext();
                            next = _templateIndex.FindMatchingTemplate(node, mode, mc2.Value, next);
                        }
                        if (next != null && TemplateIndex.SameConflictRank(next, template))
                        {
                            // Name the node, the mode, and BOTH rules. "Multiple template rules
                            // match the node" states only that a conflict exists — which the
                            // author can already tell from the error code. Which node, and which
                            // two rules, is the entire diagnosis.
                            static string Describe(XsltTemplate t) =>
                                (t.Name != null ? $"name='{t.Name.Value.LocalName}'"
                                                : $"match=\"{DescribePattern(t.Match)}\"")
                                + $" (priority {TemplateIndex.EffectivePriority(t).ToString(System.Globalization.CultureInfo.InvariantCulture)}"
                                + $", precedence {TemplateIndex.EffectivePrecedence(t)})";
                            throw Error(
                                $"XTDE0540: Multiple template rules match {DescribeNodeForDiagnostics(node)}"
                                + $" in mode {(modeKey.LocalName.Length > 0 ? "'" + modeKey.PrefixedName + "'" : "#unnamed")}"
                                + $" with on-multiple-match='fail' — {Describe(template)} and {Describe(next)}"
                                + " have the same priority");
                        }
                    }
                }

                if (template != null)
                {
                    // Trace: template match
                    if (_options?.TraceListener != null)
                    {
                        var nodeDesc = DescribeTraceNode(node);
                        var templateDesc = template.Name.HasValue
                            ? $"name=\"{template.Name.Value.LocalName}\""
                            : $"match=\"{template.Match}\"";
                        var modeDesc = mode.HasValue ? $" mode=\"{mode.Value.LocalName}\"" : "";
                        var priorityDesc = template.Priority.HasValue ? $" priority={template.Priority.Value}" : "";
                        _options.TraceListener(_templateDepth, "match", $"{nodeDesc} → {templateDesc}{modeDesc}{priorityDesc}");
                    }

                    // Enforce xsl:context-item constraints
                    var makeContextAbsent = EnforceContextItemConstraint(template);

                    // Bind non-tunnel parameters (only to non-tunnel template params)
                    foreach (var param in withParams.Where(p => !p.Tunnel))
                    {
                        var templateParam = template.Parameters.FirstOrDefault(tp =>
                            tp.Name.Equals(param.Name) && !tp.Tunnel);

                        if (templateParam != null)
                        {
                            var value = preEvaluatedParams[param.Name];
                            if (templateParam.As != null)
                            {
                                value = CoerceToType(value, templateParam.As);
                                ValidateValueMatchesType(value, templateParam.As, "XTTE0590",
                                    $"Parameter ${param.Name.LocalName}");
                            }
                            SetVariable(param.Name, value);
                        }
                    }

                    // Bind template parameters with defaults
                    foreach (var param in template.Parameters)
                    {
                        if (!_scopes.Peek().Variables.ContainsKey(param.Name))
                        {
                            // For tunnel params not yet bound, check the tunnel param stack
                            if (param.Tunnel && TryGetTunnelParam(param.Name, out var tunnelValue))
                            {
                                if (param.As != null)
                                {
                                    tunnelValue = CoerceToType(tunnelValue, param.As);
                                    ValidateValueMatchesType(tunnelValue, param.As, "XTTE0590",
                                        $"Parameter ${param.Name.LocalName}");
                                }
                                SetVariable(param.Name, tunnelValue);
                            }
                            else if (param.Required)
                            {
                                throw Error($"XTDE0700: Required parameter ${param.Name.LocalName} not supplied");
                            }
                            else if (param.Select != null)
                            {
                                var value = await EvaluateAsync(param.Select).ConfigureAwait(false);
                                if (param.As != null)
                                {
                                    value = CoerceToType(value, param.As);
                                    ValidateValueMatchesType(value, param.As, "XTTE0600",
                                        $"Parameter ${param.Name.LocalName} default value");
                                }
                                SetVariable(param.Name, value);
                            }
                            else if (param.Content != null)
                            {
                                // Route through the accumulator-isolating helper so xsl:sequence
                                // inside the default body preserves typed items (nodes, maps, …)
                                // instead of being serialized to text and re-wrapped as untyped
                                // atomic. Same shape as the with-param fix for #19.
                                var value = await EvaluateBodyContentToValueAsync(param.Content).ConfigureAwait(false);
                                if (param.As != null)
                                {
                                    value = CoerceToType(value, param.As);
                                    ValidateValueMatchesType(value, param.As, "XTTE0600",
                                        $"Parameter ${param.Name.LocalName} default value");
                                }
                                SetVariable(param.Name, value);
                            }
                            else
                            {
                                // No select, no content: default is empty sequence.
                                // Per XSLT 3.0 spec, if 'as' requires a non-empty value
                                // and the type is a strict atomic type where empty sequence is not valid,
                                // the parameter is effectively required — raise XTDE0700.
                                if (param.As != null && param.As.Occurrence is Occurrence.ExactlyOne or Occurrence.OneOrMore
                                    && IsStrictAtomicType(param.As.ItemType))
                                    throw Error($"XTDE0700: Required parameter ${param.Name.LocalName} not supplied (type {param.As.ItemType} requires a value)");
                                else if (param.As != null && param.As.Occurrence is Occurrence.ZeroOrOne or Occurrence.ZeroOrMore)
                                    SetVariable(param.Name, null);
                                else
                                    SetVariable(param.Name, "");
                            }
                        }
                    }

                    // Track current template for next-match
                    var savedTemplate = _currentTemplate;
                    var savedMode = _currentMode;
                    _currentTemplate = template;
                    _currentMode = mode;

                    // If use="absent" or optional type mismatch, push absent focus
                    if (template.ContextItemUse == ContextItemUse.Absent || makeContextAbsent)
                        PushContextItem(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus, 0, 0);

                    // Push template-level version, collation, and static base URI for scoping
                    if (template.Version != null)
                        _effectiveVersionStack.Push(template.Version);
                    if (template.DefaultCollation != null)
                        _defaultCollationStack.Push(template.DefaultCollation);
                    if (template.BaseUri != null)
                        _staticBaseUriStack.Push(XsltTransformEngine.UriString(template.BaseUri)!);
                    _templateDepth++;
                    try
                    {

                        // Execute template body, capturing output for type checking if 'as' is declared
                        if (template.As != null)
                        {
                            var savedAccum = _sequenceAccumulator;
                            var savedCapture = _currentAsBodyCapture;
                            var savedLen = _output.Length;
                            var bodyAccum = new List<object?>();
                            _sequenceAccumulator = bodyAccum;
                            _currentAsBodyCapture = new AsBodyCapture { Accumulator = bodyAccum, OutputBaseLen = savedLen, AttrDepthAtStart = _collectedAttributesStack.Count };
                            var rdClaimedPrimaryBefore = _primaryOutputClaimedByResultDocument;
                            await template.Body.ExecuteAsync(this).ConfigureAwait(false);
                            var bodyOutput = TakeAsBodyOutput(savedLen, rdClaimedPrimaryBefore);
                            var bodyCapture = _currentAsBodyCapture;
                            _currentAsBodyCapture = savedCapture;

                            // Reassemble in document order using recorded offsets — without
                            // this, a template body that writes an LRE before an xsl:sequence
                            // emits items in the wrong order at the parent constructor.
                            var resultItems = AssembleAsBodyResultItems(bodyOutput, bodyCapture.Accumulator, bodyCapture.Positions, bodyCapture.ConsumedTo);

                            // Track whether results are only from body serialization
                            // (no sequence accumulator items). When true, emit bodyOutput
                            // directly to preserve exact namespace declarations (e.g. xmlns=""
                            // from inherit-namespaces="no") and disable-output-escaping text
                            // that would be lost by XDM re-serialization round-trip.
                            bool canEmitBodyDirectly = bodyCapture.Accumulator.Count == 0
                                && !string.IsNullOrEmpty(bodyOutput);

                            _sequenceAccumulator = savedAccum;

                            // XTTE0505: Validate template return value against 'as' type
                            ValidateTemplateReturnType(template, resultItems);

                            // If an outer sequence accumulator is active (e.g., raw delivery
                            // from fn:transform), propagate items there instead of serializing
                            if (_sequenceAccumulator != null && resultItems.Count > 0)
                            {
                                foreach (var item in resultItems)
                                    AppendToSeqAccumulator(item);
                                var lastItem = resultItems[^1];
                                lastTemplateResultWasAtomic = lastItem is not (XdmNode or ResultTreeFragment);
                            }
                            // Re-serialize validated items to output
                            // Adjacent atomic values from consecutive template invocations
                            // are space-separated per XSLT spec section 5.7.2
                            else if (resultItems.Count > 0)
                            {
                                var firstItem = resultItems[0];
                                bool firstIsAtomic = firstItem is not (XdmNode or ResultTreeFragment);
                                if (firstIsAtomic && lastTemplateResultWasAtomic && _textContentDepth == 0)
                                {
                                    _sink.RawText(" ");
                                    // Reset so SerializeSequenceItems doesn't add a duplicate separator
                                    _lastResultWasAtomic = false;
                                }
                                if (canEmitBodyDirectly)
                                {
                                    // Append bodyOutput directly to preserve DOE text,
                                    // xmlns="" undeclarations, and other serialization details
                                    // that would be lost by XDM round-trip re-serialization.
                                    _sink.RawText(bodyOutput);
                                    _lastResultWasAtomic = false;
                                }
                                else
                                {
                                    SerializeSequenceItems(resultItems);
                                }
                                var lastItem = resultItems[^1];
                                lastTemplateResultWasAtomic = lastItem is not (XdmNode or ResultTreeFragment);
                            }
                        }
                        else
                        {
                            await template.Body.ExecuteAsync(this).ConfigureAwait(false);
                            lastTemplateResultWasAtomic = false;
                        }

                    }
                    finally
                    {
                        _templateDepth--;
                        if (template.BaseUri != null)
                            _staticBaseUriStack.Pop();
                        if (template.DefaultCollation != null)
                            _defaultCollationStack.Pop();
                        if (template.Version != null)
                            _effectiveVersionStack.Pop();
                    }

                    // Pop absent context if pushed
                    if (template.ContextItemUse == ContextItemUse.Absent || makeContextAbsent)
                        PopContextItem();

                    _currentTemplate = savedTemplate;
                    _currentMode = savedMode;
                }
                else
                {
                    // Built-in template rules
                    if (_options?.TraceListener != null)
                        _options.TraceListener(_templateDepth, "built-in", DescribeTraceNode(node));
                    await ApplyBuiltInTemplateAsync(node, mode, withParams).ConfigureAwait(false);
                }
            }
            finally
            {
                PopScope();
                PopCurrentItem();
                PopContextItem();
            }
        }
    }


    private async ValueTask ApplyBuiltInTemplateAsync(
        object node,
        QName? mode,
        List<XsltWithParam> withParams)
    {
        // XTTE3100: typed="yes" mode disallows untyped nodes in built-in templates
        if (node is XdmElement or XdmDocument && IsTypedMode(mode))
            throw Error("XTTE3100: Built-in template rule invoked for an untyped node in a mode with typed='yes'");

        var behavior = GetOnNoMatchBehavior(mode);

        switch (behavior)
        {
            case OnNoMatchBehavior.TextOnlyCopy:
                await ApplyBuiltInTextOnlyCopyAsync(node, mode, withParams).ConfigureAwait(false);
                break;
            case OnNoMatchBehavior.ShallowCopy:
                await ApplyBuiltInShallowCopyAsync(node, mode, withParams).ConfigureAwait(false);
                break;
            case OnNoMatchBehavior.DeepCopy:
                await ApplyBuiltInDeepCopyAsync(node, mode, withParams).ConfigureAwait(false);
                break;
            case OnNoMatchBehavior.DeepSkip:
                // Skip the node and all descendants entirely.
                // Exception: document nodes still recurse into children (otherwise nothing would ever match).
                // In streaming mode, child recursion is handled by the streaming loop.
                if (node is XdmDocument && !_isStreamingExecution)
                    await ApplyTemplatesAsync(null, mode, [], withParams).ConfigureAwait(false);
                break;
            case OnNoMatchBehavior.ShallowSkip:
                // Skip the node but apply templates to @* | node() (per XSLT spec)
                if (node is XdmDocument or XdmElement)
                {
                    // Apply templates to attributes (element nodes only)
                    if (node is XdmElement elemSkip && _nodeStore != null)
                    {
                        foreach (var attr in _nodeStore.GetAttributes(elemSkip))
                        {
                            XsltTemplate? template;
                            using (var mc = AcquireMatchContext())
                                template = _templateIndex.FindMatchingTemplate(attr, mode, mc.Value);
                            if (template != null)
                            {
                                await ExecuteMatchedTemplateAsync(template, attr, mode, withParams)
                                    .ConfigureAwait(false);
                            }
                            // If no match, shallow-skip does nothing for attributes
                        }
                    }
                    // In streaming mode, child recursion is handled by the streaming loop.
                    if (!_isStreamingExecution)
                        await ApplyTemplatesAsync(null, mode, [], withParams).ConfigureAwait(false);
                }
                break;
            case OnNoMatchBehavior.Fail:
                // XsltException, not InvalidOperationException: this is a spec-defined dynamic
                // error, so it needs the ErrorCode and Location every other XSLT error carries,
                // and it must be catchable as one — AccumulatorDeferredError.IsDeferrable and
                // the CLI's XSLT handler both key off the type.
                // Name the node and the mode. The bare message said only that SOMETHING did not
                // match SOMEWHERE, which is the one fact that does not help: a stylesheet has
                // many modes and a document many nodes, and on-no-match="fail" exists precisely
                // to be diagnosed.
                throw Error(
                    "XTDE0555: No matching template found for "
                    + DescribeNodeForDiagnostics(node)
                    + " in mode "
                    + (mode is { } m && !string.IsNullOrEmpty(m.LocalName)
                        ? "'" + m.PrefixedName + "'"
                        : "#unnamed")
                    + " with on-no-match='fail'");
        }
    }


    private async ValueTask ApplyBuiltInTextOnlyCopyAsync(object node, QName? mode, List<XsltWithParam> withParams)
    {
        switch (node)
        {
            case XdmDocument:
            case XdmElement:
                // #143 Task 1.3 GUARD: a structure-bearing node (element/document) must never be
                // collapsed to the built-in text-only copy when the owning construct is
                // guaranteed-streamable — that is the si-iterate-013 regression (xsl:copy >
                // xsl:iterate > copy-of falling through here, its element structure lost to
                // concatenated descendant text). The correct route is StreamingPlanner.Plan's
                // stream/buffer dispatch (wired in Task 1.2). This arm being reached for such a
                // construct means the dispatch was bypassed.
                if (OwningConstructIsGuaranteedStreamable())
                {
                    System.Diagnostics.Debug.Assert(false,
                        "#143 Task 1.3 invariant: a guaranteed-streamable construct (" +
                        DescribeOwningConstruct() + ") reached the built-in text-only-copy sink " +
                        "for a " + (node is XdmDocument ? "document" : "element") + " node. It must " +
                        "stream or buffer via StreamingPlanner.Plan, never collapse structure to text.");
                    // Release safety net: materialise the matched subtree and run the owning body
                    // non-streaming (correct output — never the text-only collapse). This mirrors
                    // BufferMatchedSubtree, the plan a guaranteed-streamable construct maps to.
                    await ApplyGuaranteedStreamableBufferFallbackAsync(node, mode, withParams)
                        .ConfigureAwait(false);
                    break;
                }
                // In streaming mode, child recursion is handled by the streaming loop —
                // the built-in template only needs to handle the current node.
                if (!_isStreamingExecution)
                    await ApplyTemplatesAsync(null, mode, [], withParams).ConfigureAwait(false);
                break;
            case XdmText text:
                WriteText(text.Value, false);
                break;
            case XdmAttribute attr:
                WriteText(attr.Value, false);
                break;
            case XdmNamespace:
                // XSLT 3.0 §6.7: Namespace nodes produce no text output
                break;
            default:
                // XSLT 3.0 §6.7: Function items (maps, arrays, functions) — no action
                if (node is IDictionary<object, object?> or List<object?> or XQueryFunction)
                    break;
                // Atomic values: output string value (XSLT 3.0 §6.7)
                if (node is not XdmNode)
                    WriteText(StringValueOf(node), false);
                break;
        }
    }


    private async ValueTask ApplyBuiltInShallowCopyAsync(object node, QName? mode, List<XsltWithParam> withParams)
    {
        // SP-B slice 4: the built-in shallow-copy of a source node emits the copied node directly
        // to the sink (bypassing the routed emitters), so an active constructor's live tree can't
        // capture it — mark the body incomplete so the differential skips it (element-content
        // level only). The document case dispatches to apply-templates and emits nothing itself.
        if (_textContentDepth == 0 && node is not XdmDocument)
            MarkTcIncompleteIfActive();
        switch (node)
        {
            case XdmDocument builtinShallowDoc:
                // Built-in shallow-copy of a document node is
                // `<xsl:copy><xsl:apply-templates mode="#current"/></xsl:copy>` (XSLT 3.0 §6.7.4).
                // When capturing into a sequence, materialise a real document node whose children
                // are the apply-templates result and whose base URI is the SOURCE document's base
                // URI, so base-uri() of the copy reports the source URI (the serialize dispatch
                // produced no doc node at all → empty). (fn/base-uri 053: shallow-copy-doc2.)
                // Direct-output / streaming keep the existing child-recursion dispatch, as does
                // any untyped-RTF body (flip-active OR blocked): only a typed as-body sequence
                // accumulator (InTypedAsBodyAccumulator) delivers the copy as a node item.
                if (!_isStreamingExecution && InTypedAsBodyAccumulator && _nodeStore != null)
                    await BuildBuiltInCopyDocNodeAsync(builtinShallowDoc, mode, withParams).ConfigureAwait(false);
                else if (!_isStreamingExecution)
                    await ApplyTemplatesAsync(null, mode, [], withParams).ConfigureAwait(false);
                break;
            case XdmElement elem:
            {
                // Shallow copy: copy element, apply-templates to @* | node() (per XSLT spec)
                var nsUri = _nodeStore?.GetNamespaceUri(elem.Namespace) ?? "";
                var prefix = elem.Prefix ?? "";
                var localName = elem.LocalName;
                var qname = !string.IsNullOrEmpty(prefix) ? $"{prefix}:{localName}" : localName;

                _sink.StartElementOpen(qname);

                // Copy namespace declarations
                var wroteDefaultNsDecl = false;
                foreach (var nsDecl in elem.NamespaceDeclarations)
                {
                    var nsDeclUri = _nodeStore?.GetNamespaceUri(nsDecl.Namespace) ?? "";
                    var nsDeclPrefix = nsDecl.Prefix ?? "";
                    if (string.IsNullOrEmpty(nsDeclPrefix))
                        wroteDefaultNsDecl = true;
                    _sink.Namespace(nsDeclPrefix, nsDeclUri);
                }

                // Same defect as the xsl:copy path, in the built-in shallow-copy twin: an
                // unprefixed element in NO namespace needs an explicit xmlns="" or the start tag
                // we write re-parses into whatever default namespace is in scope, silently
                // changing the element's name. The loop above only replays declarations the node
                // model recorded, and an xmlns="" undeclaration is not among them.
                // The condition is on the SOURCE parent, not the output scope: this path writes
                // start tags straight to _output without pushing an output namespace scope, so
                // GetInScopeDefaultNamespace() is null here and gating on it emitted nothing.
                // A no-namespace element whose parent IS in a namespace is exactly the shape
                // that needed xmlns="" in the source and needs it again in the copy.
                if (!wroteDefaultNsDecl
                    && string.IsNullOrEmpty(prefix)
                    && string.IsNullOrEmpty(nsUri)
                    && elem.Parent is { } parentId
                    && _nodeStore?.GetNode(parentId) is XdmElement parentElem
                    && !string.IsNullOrEmpty(_nodeStore.GetNamespaceUri(parentElem.Namespace)))
                {
                    _sink.Namespace("", "");
                }

                // EMIT (temp-tree base-URI preservation): built-in shallow-copy writes the
                // copied SOURCE element's start tag directly to _output (it does not go through
                // SerializeNode). When this output is destined for a temp-tree reparse
                // (_tempTreeSerializeDepth > 0, e.g. an xsl:variable as="document-node()" body),
                // stamp the source element's base URI as a sentinel so the reparse recovers it
                // onto CopySourceBaseUri. Mirrors SerializeNode's element case. Context is
                // restored after the child recursion / close tag below so siblings re-evaluate.
                var savedShallowCopyBaseContext = _serializeBaseContext;
                TryEmitBaseSentinel(elem, ref _serializeBaseContext);

                // Apply templates to attributes: attributes with no matching user template
                // are copied directly (built-in shallow-copy for attributes); attributes with
                // a matching user template have their template output as child content.
                List<(XsltTemplate template, XdmAttribute attr)>? templateMatches = null;
                if (_nodeStore != null)
                {
                    foreach (var attr in _nodeStore.EnumerateAttributes(elem))
                    {
                        XsltTemplate? template;
                        using (var mc = AcquireMatchContext())
                            template = _templateIndex.FindMatchingTemplate(attr, mode, mc.Value);
                        if (template != null)
                        {
                            (templateMatches ??= new()).Add((template, attr));
                        }
                        else
                        {
                            // Built-in: copy attribute directly
                            var attrPrefix = attr.Prefix ?? "";
                            var attrName = !string.IsNullOrEmpty(attrPrefix)
                                ? $"{attrPrefix}:{attr.LocalName}"
                                : attr.LocalName;
                            _sink.Attribute(attrName, attr.Value);
                        }
                    }
                }

                // Execute user templates for matched attributes using attribute collection
                // so xsl:attribute instructions produce proper attributes, not text content.
                // Non-attribute output from templates (e.g. elements) becomes child content.
                var templateChildContent = string.Empty;
                if (templateMatches is { Count: > 0 })
                {
                    _collectedAttributesStack.Push(new StringBuilder());
                    var scope = new XsltTransformEngine.ScopedOutputBuffer(_output);
                    // Reset _outputLogicalStart to the scope's start so the XTDE0410 check
                    // ("attribute after children") uses the correct baseline. Without this, the
                    // check compares _output.Length against the default value of 0, which causes
                    // a false positive when there is already content in _output from ancestor
                    // elements (e.g. shallow-copy of parent elements in streaming mode).
                    var savedLogicalStartForAttrTemplate = _outputLogicalStart;
                    _outputLogicalStart = scope.SavedLength;
                    // Suspend any enclosing sequence accumulator for the duration, exactly as
                    // the child application below already does. A matched attribute template
                    // with a declared type (as="attribute()") captures its own result and then
                    // propagates it to whatever accumulator is active; with an enclosing
                    // node()*-typed function or variable body that is the OUTER accumulator, so
                    // the attribute escaped the element being copied and surfaced as a loose
                    // attribute node in the caller's sequence — later fatal as XTDE0420 when
                    // that sequence is dropped into an xsl:document.
                    //
                    // XSpec gather-specs.xsl has exactly this shape: mode x:gather-specs is
                    // on-no-match="shallow-copy" and carries a typed template for
                    // @as|@function|@mode|@name|@port|@template, so every x:variable/x:param
                    // shed its attributes into x:resolve-import's result.
                    var savedSeqAccumAttrs = _sequenceAccumulator;
                    _sequenceAccumulator = null;
                    try
                    {
                        foreach (var (matchedTemplate, matchedAttr) in templateMatches)
                        {
                            await ExecuteMatchedTemplateAsync(matchedTemplate, matchedAttr, mode, withParams)
                                .ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _sequenceAccumulator = savedSeqAccumAttrs;
                    }
                    _outputLogicalStart = savedLogicalStartForAttrTemplate;
                    var collectedAttrs = _collectedAttributesStack.Pop();
                    templateChildContent = scope.GetWritten();
                    scope.Dispose();
                    _sink.RawText(collectedAttrs.ToString());
                }

                _sink.StartElementClose(false);

                // Insert any non-attribute content from matched attribute templates as children
                if (templateChildContent.Length > 0)
                    _sink.RawText(templateChildContent);

                // Apply templates to children — suspend accumulator and track element depth
                // so inner templates with 'as' serialize to _output (child content)
                // instead of redirecting to an outer sequence accumulator.
                // In streaming mode, child recursion is handled by the streaming loop:
                // we leave the element open (no closing tag) and push the qname so that
                // StreamingXmlProcessor can write the closing tag on EndElement.
                if (_isStreamingExecution)
                {
                    _streamingOpenElements.Push(qname);
                    // Descendants in the streaming loop inherit this subtree's base context;
                    // restore happens on the matching EndElement is out of scope here, so
                    // restore now — sentinel already emitted on the open tag above.
                    _serializeBaseContext = savedShallowCopyBaseContext;
                }
                else
                {
                    var savedSeqAccumSC = _sequenceAccumulator;
                    _sequenceAccumulator = null;
                    _serializingElementDepth++;
                    try
                    {
                        await ApplyTemplatesAsync(null, mode, [], withParams).ConfigureAwait(false);
                    }
                    finally
                    {
                        _serializingElementDepth--;
                        _sequenceAccumulator = savedSeqAccumSC;
                        // Restore base context so following siblings re-evaluate whether their
                        // own base differs (mirrors SerializeNode's element case).
                        _serializeBaseContext = savedShallowCopyBaseContext;
                    }

                    _sink.EndElement(qname);
                }
                break;
            }
            case XdmText text:
                WriteText(text.Value, false);
                break;
            case XdmAttribute attr2:
            {
                // Shallow-copy of attribute: write as attribute (into collected attrs if inside element)
                var attrTarget = _attributeCollecting ? _collectedAttributes! : _output;
                var attrName = !string.IsNullOrEmpty(attr2.Prefix)
                    ? $"{attr2.Prefix}:{attr2.LocalName}"
                    : attr2.LocalName;
                if (attrTarget == _output)
                {
                    _sink.Attribute(attrName, attr2.Value);
                }
                else
                {
                    attrTarget.Append(' ');
                    attrTarget.Append(attrName);
                    attrTarget.Append("=\"");
                    attrTarget.Append(EscapeAttributeValue(attr2.Value));
                    attrTarget.Append('"');
                }
                break;
            }
            case XdmComment comment:
                _sink.Comment(comment.Value);
                break;
            case XdmProcessingInstruction pi:
                // Note: deep-copy path intentionally does not apply EscapePIValue (matches
                // pre-existing behavior at this call site) — use RawText, not
                // _sink.ProcessingInstruction, to keep byte-identical output.
                _sink.RawText("<?" + pi.Target + (string.IsNullOrEmpty(pi.Value) ? "" : " " + pi.Value) + "?>");
                break;
            case XdmNamespace ns2:
            {
                // Shallow/deep copy: emit namespace declaration
                var nsTarget = _attributeCollecting ? _collectedAttributes! : _output;
                nsTarget.Append(" xmlns");
                if (!string.IsNullOrEmpty(ns2.Prefix))
                {
                    nsTarget.Append(':');
                    nsTarget.Append(ns2.Prefix);
                }
                nsTarget.Append("=\"");
                nsTarget.Append(EscapeAttributeValue(ns2.Uri));
                nsTarget.Append('"');
                break;
            }
            default:
                // XSLT 3.0 §6.7: Function items (maps, arrays, functions) — deep copy (pass through)
                if (node is IDictionary<object, object?> or List<object?> or XQueryFunction)
                {
                    if (_sequenceAccumulator != null)
                        AppendToSeqAccumulator(node);
                    break;
                }
                // Atomic values: output string value (XSLT 3.0 §6.7)
                if (node is not XdmNode)
                    WriteText(StringValueOf(node), false);
                break;
        }
    }


    private async ValueTask ApplyBuiltInDeepCopyAsync(object node, QName? mode, List<XsltWithParam> withParams)
    {
        // SP-B slice 4: the built-in deep-copy serializes an entire source subtree to the sink
        // without routing into the active constructor — mark the body incomplete so the
        // differential skips it (element-content level only). The document case only dispatches
        // to apply-templates (its children route through their own templates).
        if (_textContentDepth == 0 && node is not XdmDocument)
            MarkTcIncompleteIfActive();
        // Deep copy: serialize the entire node subtree
        switch (node)
        {
            case XdmDocument builtinDeepDoc:
                // Built-in deep-copy of a document node is `<xsl:copy-of select="."/>` (XSLT
                // 3.0 §6.7.4). When capturing into a sequence (as="node()*" etc.), materialise
                // a real document node carrying the SOURCE document's base URI so base-uri() of
                // the copy reports the source URI — not the fragmented RTFs the serialize path
                // produced. (fn/base-uri 053: deep-copy-doc2.) Direct-output / streaming keep the
                // existing child-recursion dispatch (a doc node serializes as its children), as
                // does any untyped-RTF body (flip-active OR blocked): only a typed as-body
                // sequence accumulator (InTypedAsBodyAccumulator) delivers the copy as a node item.
                if (!_isStreamingExecution && InTypedAsBodyAccumulator && _nodeStore != null)
                    await BuildBuiltInCopyDocNodeAsync(builtinDeepDoc, mode, withParams).ConfigureAwait(false);
                else if (!_isStreamingExecution)
                    await ApplyTemplatesAsync(null, mode, [], withParams).ConfigureAwait(false);
                break;
            case XdmElement elem:
                // Built-in deep-copy of an element is `<xsl:copy-of select="."/>`. When
                // capturing into a sequence, clone the subtree as a node-model copy and stamp
                // CopySourceBaseUri from the source's computed base URI (mirroring the xsl:copy-of
                // accumulator path) so base-uri() of the copy reports the source base. Serializing
                // to the sink here would fragment into ResultTreeFragments under the item()* text
                // collection and lose the base URI. Any untyped-RTF body (flip-active OR blocked)
                // keeps the serialize path; only a typed as-body sequence accumulator
                // (InTypedAsBodyAccumulator) takes the node-clone branch. (fn/base-uri 053:
                // deep-copy-elem2.)
                if (!_isStreamingExecution && InTypedAsBodyAccumulator && _nodeStore != null
                    && _textContentDepth == 0)
                {
                    var deepCloneId = CloneSubtreeDeep(elem, null, copyNamespaces: true);
                    if (_nodeStore.GetNode(deepCloneId) is XdmElement deepClone)
                    {
                        deepClone.CopySourceBaseUri ??= ComputeSourceBaseUri(elem);
                        AppendToSeqAccumulator(deepClone);
                        break;
                    }
                }
                SerializeElement(elem);
                break;
            case XdmText text:
                WriteText(text.Value, false);
                break;
            case XdmAttribute attr:
            {
                // Deep-copy of attribute: write as attribute (into collected attrs if inside element)
                var attrTarget = _attributeCollecting ? _collectedAttributes! : _output;
                var attrName = !string.IsNullOrEmpty(attr.Prefix)
                    ? $"{attr.Prefix}:{attr.LocalName}"
                    : attr.LocalName;
                if (attrTarget == _output)
                {
                    _sink.Attribute(attrName, attr.Value);
                }
                else
                {
                    attrTarget.Append(' ');
                    attrTarget.Append(attrName);
                    attrTarget.Append("=\"");
                    attrTarget.Append(EscapeAttributeValue(attr.Value));
                    attrTarget.Append('"');
                }
                break;
            }
            case XdmComment comment:
                _sink.Comment(comment.Value);
                break;
            case XdmProcessingInstruction pi:
                // Note: deep-copy path intentionally does not apply EscapePIValue (matches
                // pre-existing behavior at this call site) — use RawText, not
                // _sink.ProcessingInstruction, to keep byte-identical output.
                _sink.RawText("<?" + pi.Target + (string.IsNullOrEmpty(pi.Value) ? "" : " " + pi.Value) + "?>");
                break;
            case XdmNamespace ns3:
            {
                // Deep copy: emit namespace declaration
                var nsTarget3 = _attributeCollecting ? _collectedAttributes! : _output;
                nsTarget3.Append(" xmlns");
                if (!string.IsNullOrEmpty(ns3.Prefix))
                {
                    nsTarget3.Append(':');
                    nsTarget3.Append(ns3.Prefix);
                }
                nsTarget3.Append("=\"");
                nsTarget3.Append(EscapeAttributeValue(ns3.Uri));
                nsTarget3.Append('"');
                break;
            }
            default:
                // XSLT 3.0 §6.7: Function items (maps, arrays, functions) — deep copy (pass through)
                if (node is IDictionary<object, object?> or List<object?> or XQueryFunction)
                {
                    if (_sequenceAccumulator != null)
                        AppendToSeqAccumulator(node);
                    break;
                }
                // Atomic values: output string value (XSLT 3.0 §6.7)
                if (node is not XdmNode)
                    WriteText(StringValueOf(node), false);
                break;
        }
    }


    public override async ValueTask CallTemplateAsync(QName name, List<XsltWithParam> withParams)
    {
        // Trace: call-template
        if (_options?.TraceListener != null)
            _options.TraceListener(_templateDepth, "call-template", name.LocalName);

        CheckResourceLimits();
        if (_recursionDepth >= MaxRecursionDepth)
            return;

        XsltTemplate? template;

        // xsl:original resolution — call the overridden template from a package
        if (name.LocalName == "original" && name.Namespace == NamespaceId.Xslt)
        {
            // Look for the original template in the current template stack
            template = null;
            if (_currentTemplateStack.Count > 0)
            {
                var currentTemplate = _currentTemplateStack.Peek();
                template = currentTemplate.OriginalTemplate;
            }
            if (template == null)
                throw Error("XTDE3058: xsl:original invoked but no overridden template is available");
        }
        else if (!_stylesheet.NamedTemplates.TryGetValue(name, out template))
        {
            // The id-keyed lookup above is the fast path and is correct for a name the parser
            // interned. It misses for a QName built at runtime by fn:QName(), which carries a
            // hash-based NamespaceId that never equals the parser's id for the same URI - so
            // fn:transform with a namespaced initial-template could not find its entry point,
            // and reported the local name of a template it had just failed to qualify.
            // Resolving an interned id back to a URI needs the table that interned it. The node
            // store does not have it - these ids come from the stylesheet parser - but the
            // stylesheet keeps its own prefix-to-URI map, and the parsed QName kept its prefix.
            string? ResolveViaStylesheet(QName q)
                => _nodeStore?.GetNamespaceUri(q.Namespace)
                ?? (!string.IsNullOrEmpty(q.Prefix)
                    && _stylesheet.Namespaces.TryGetValue(q.Prefix, out var declared)
                        ? declared : null);

            foreach (var (candidate, decl) in _stylesheet.NamedTemplates)
            {
                if (QNameNamespaces.SameExpandedName(candidate, name, ResolveViaStylesheet))
                {
                    template = decl;
                    break;
                }
            }

            if (template == null)
            {
                // Name the namespace too. The old message quoted only the local part, which is
                // exactly the information that does not help when the namespace is the problem.
                var uri = QNameNamespaces.UriOf(name, ResolveViaStylesheet);
                throw Error(uri.Length > 0
                    ? $"Named template 'Q{{{uri}}}{name.LocalName}' not found"
                    : $"Named template '{name.LocalName}' not found");
            }
        }

        // Pre-evaluate ALL with-param values in the CALLING context before pushing scope.
        // This prevents parameter cross-contamination where a later param sees the value
        // of an earlier param that was already bound in the new scope.
        var preEvaluatedParams = new Dictionary<QName, object?>();
        foreach (var wp in withParams)
        {
            preEvaluatedParams[wp.Name] = await EvaluateWithParamAsync(wp).ConfigureAwait(false);
        }

        // Enforce xsl:context-item constraints
        var makeContextAbsent = EnforceContextItemConstraint(template);

        _recursionDepth++;
        _currentTemplateStack.Push(template);
        PushScope();

        try
        {
            // XTDE3480: Clear merge-group context — not available in called templates
            ClearMergeGroupContext();

            // If use="absent" or optional type mismatch, make context item absent
            if (template.ContextItemUse == ContextItemUse.Absent || makeContextAbsent)
            {
                PushContextItem(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus, 0, 0);
                SuppressGroupingFocus();
            }

            // Forward inherited tunnel parameters from parent scopes.
            // NOTE: Only store in TunnelParameters, NOT as variables.
            // Variables are bound later based on each template param's tunnel flag.
            InheritTunnelParameters();

            // Track explicitly provided tunnel params (using pre-evaluated values)
            foreach (var wp in withParams.Where(p => p.Tunnel))
            {
                var value = preEvaluatedParams[wp.Name];
                _scopes.Peek().TunnelParameters[wp.Name] = value;
            }

            // Bind parameters (using pre-evaluated values)
            foreach (var param in template.Parameters)
            {
                var withParam = withParams.FirstOrDefault(p => p.Name.Equals(param.Name) && !p.Tunnel);

                if (withParam != null && !param.Tunnel)
                {
                    // Non-tunnel with-param binds only to non-tunnel template param
                    var value = preEvaluatedParams[withParam.Name];
                    if (param.As != null)
                    {
                        value = CoerceToType(value, param.As);
                        ValidateValueMatchesType(value, param.As, "XTTE0590",
                            $"Parameter ${param.Name.LocalName}");
                    }
                    SetVariable(param.Name, value);
                }
                else if (param.Tunnel && TryGetTunnelParam(param.Name, out var tunnelValue))
                {
                    if (param.As != null)
                    {
                        tunnelValue = CoerceToType(tunnelValue, param.As);
                        ValidateValueMatchesType(tunnelValue, param.As, "XTTE0590",
                            $"Parameter ${param.Name.LocalName}");
                    }
                    SetVariable(param.Name, tunnelValue);
                }
                else if (param.Required)
                {
                    throw Error($"Required parameter ${param.Name.LocalName} not supplied");
                }
                else if (param.Select != null)
                {
                    var value = await EvaluateAsync(param.Select).ConfigureAwait(false);
                    if (param.As != null)
                    {
                        value = CoerceToType(value, param.As);
                        ValidateValueMatchesType(value, param.As, "XTTE0600",
                            $"Parameter ${param.Name.LocalName} default value");
                    }
                    SetVariable(param.Name, value);
                }
                else if (param.Content != null)
                {
                    // Accumulator-isolating evaluation — see comment at the helper definition.
                    var value = await EvaluateBodyContentToValueAsync(param.Content).ConfigureAwait(false);
                    if (param.As != null)
                    {
                        value = CoerceToType(value, param.As);
                        ValidateValueMatchesType(value, param.As, "XTTE0600",
                            $"Parameter ${param.Name.LocalName} default value");
                    }
                    SetVariable(param.Name, value);
                }
                else
                {
                    // No select, no content, no with-param: default is empty sequence.
                    if (param.As != null && param.As.Occurrence is Occurrence.ExactlyOne or Occurrence.OneOrMore
                        && IsStrictAtomicType(param.As.ItemType))
                        throw Error($"XTDE0700: Required parameter ${param.Name.LocalName} not supplied (type {param.As.ItemType} requires a value)");
                    else if (param.As != null && param.As.Occurrence is Occurrence.ZeroOrOne or Occurrence.ZeroOrMore)
                        SetVariable(param.Name, null);
                    else
                        SetVariable(param.Name, "");
                }
            }

            // XTSE0680: In XSLT 2.0+, passing a non-tunnel parameter that the template doesn't declare,
            // or that matches a tunnel param, is a static error.
            // Note: tunnel with-params matching non-tunnel params are NOT errors — they pass through.
            if (!IsBackwardsCompatible)
            {
                foreach (var wp in withParams.Where(p => !p.Tunnel && !p.FromRuntimeOptions))
                {
                    var matchingParam = template.Parameters.FirstOrDefault(p => p.Name.Equals(wp.Name));
                    if (matchingParam == null)
                        throw Error($"XTSE0680: Parameter '{wp.Name.LocalName}' is not declared in the called template '{name.LocalName}'");
                    if (matchingParam.Tunnel)
                        throw Error($"XTSE0680: Non-tunnel parameter '{wp.Name.LocalName}' in xsl:call-template does not match tunnel parameter in template '{name.LocalName}'");
                }
            }

            if (template.Version != null)
                _effectiveVersionStack.Push(template.Version);
            if (template.DefaultCollation != null)
                _defaultCollationStack.Push(template.DefaultCollation);
            if (template.BaseUri != null)
                _staticBaseUriStack.Push(XsltTransformEngine.UriString(template.BaseUri)!);
            try
            {
                // Execute template body, capturing output for type checking if 'as' is declared
                if (template.As != null)
                {
                    var savedAccum = _sequenceAccumulator;
                    var savedCapture = _currentAsBodyCapture;
                    var savedLen = _output.Length;
                    var bodyAccum = new List<object?>();
                    _sequenceAccumulator = bodyAccum;
                    _currentAsBodyCapture = new AsBodyCapture { Accumulator = bodyAccum, OutputBaseLen = savedLen, AttrDepthAtStart = _collectedAttributesStack.Count };
                    var rdClaimedPrimaryBefore = _primaryOutputClaimedByResultDocument;
                    await template.Body.ExecuteAsync(this).ConfigureAwait(false);
                    var bodyOutput = TakeAsBodyOutput(savedLen, rdClaimedPrimaryBefore);
                    var bodyCapture = _currentAsBodyCapture;
                    _currentAsBodyCapture = savedCapture;

                    // Reassemble in document order: parse `bodyOutput` into top-level XDM nodes,
                    // then weave accumulator items in at the offsets recorded when each was added.
                    var resultItems = AssembleAsBodyResultItems(bodyOutput, bodyCapture.Accumulator, bodyCapture.Positions, bodyCapture.ConsumedTo);
                    // Track whether results are only from body serialization
                    // (no sequence accumulator items). When true, emit bodyOutput
                    // directly to preserve exact namespace declarations (e.g. xmlns=""
                    // from inherit-namespaces="no") and disable-output-escaping text
                    // that would be lost by XDM re-serialization round-trip.
                    bool canEmitBodyDirectly2 = bodyCapture.Accumulator.Count == 0
                        && !string.IsNullOrEmpty(bodyOutput);

                    _sequenceAccumulator = savedAccum;

                    // XTTE0505: Validate template return value against 'as' type
                    ValidateTemplateReturnType(template, resultItems);

                    // Re-output validated items: if the outer context is collecting
                    // individual items (e.g. another template with 'as'), preserve them
                    // in the accumulator. Otherwise serialize to _output.
                    if (resultItems.Count > 0)
                    {
                        if (_sequenceAccumulator != null)
                        {
                            // When the outer scope is itself an `as=` body capture, each
                            // item we forward records its position via AppendToSeqAccumulator
                            // so the outer reassembly can interleave correctly.
                            foreach (var item in resultItems)
                                AppendToSeqAccumulator(item);
                        }
                        else if (canEmitBodyDirectly2)
                        {
                            // Append bodyOutput directly to preserve DOE text,
                            // xmlns="" undeclarations, and other serialization details
                            // that would be lost by XDM round-trip re-serialization.
                            _sink.RawText(bodyOutput);
                            _lastResultWasAtomic = false;
                        }
                        else
                        {
                            SerializeSequenceItems(resultItems);
                        }
                    }
                }
                else
                {
                    await template.Body.ExecuteAsync(this).ConfigureAwait(false);
                }
            }
            finally
            {
                if (template.BaseUri != null)
                    _staticBaseUriStack.Pop();
                if (template.DefaultCollation != null)
                    _defaultCollationStack.Pop();
                if (template.Version != null)
                    _effectiveVersionStack.Pop();
            }
        }
        finally
        {
            if (template.ContextItemUse == ContextItemUse.Absent || makeContextAbsent)
                PopContextItem();
            PopScope();
            _currentTemplateStack.Pop();
            _recursionDepth--;
        }
    }


    public override async ValueTask ApplyImportsAsync(List<XsltWithParam> withParams)
    {
        CheckResourceLimits();
        // XTDE0560: apply-imports requires a current template rule
        if (_currentTemplate == null)
            throw Error("XTDE0560: xsl:apply-imports can only be used when there is a current template rule");
        var node = ContextItem;
        // XTDE0560: apply-imports requires a context item (fails when context-item use="absent")
        if (node == null || ReferenceEquals(node, PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus))
            throw Error("XTDE0560: xsl:apply-imports requires a context item, but the context item is absent");

        // apply-imports searches only templates from imported stylesheets
        // (lower precedence than the module containing the current template).
        // This differs from next-match which searches all templates in priority order.
        var ownerIndex = _templateIndex.FindOwnerIndex(_currentTemplate);
        XsltTemplate? importedTemplate;
        using (var mc = AcquireMatchContext())
            importedTemplate = ownerIndex?.FindImportedTemplate(node, _currentMode, mc.Value);

        if (importedTemplate != null)
        {
            PushScope();
            try
            {
                // Propagate tunnel parameters from parent scopes
                InheritTunnelParameters();

                // Store explicit tunnel with-params
                foreach (var param in withParams.Where(p => p.Tunnel))
                {
                    var value = await EvaluateWithParamAsync(param).ConfigureAwait(false);
                    _scopes.Peek().TunnelParameters[param.Name] = value;
                }

                // Bind non-tunnel with-params to non-tunnel template params
                foreach (var param in withParams.Where(p => !p.Tunnel))
                {
                    var tp = importedTemplate.Parameters.FirstOrDefault(t =>
                        t.Name.Equals(param.Name) && !t.Tunnel);
                    if (tp != null)
                    {
                        var value = await EvaluateWithParamAsync(param).ConfigureAwait(false);
                        SetVariable(param.Name, value);
                    }
                }

                foreach (var p in importedTemplate.Parameters)
                {
                    if (!_scopes.Peek().Variables.ContainsKey(p.Name))
                    {
                        if (p.Tunnel && TryGetTunnelParam(p.Name, out var tunnelValue))
                        {
                            SetVariable(p.Name, tunnelValue);
                        }
                        else if (p.Select != null)
                        {
                            var value = await EvaluateAsync(p.Select).ConfigureAwait(false);
                            SetVariable(p.Name, value);
                        }
                    }
                }

                var savedTemplate = _currentTemplate;
                _currentTemplate = importedTemplate;
                if (importedTemplate.Version != null)
                    _effectiveVersionStack.Push(importedTemplate.Version);
                if (importedTemplate.DefaultCollation != null)
                    _defaultCollationStack.Push(importedTemplate.DefaultCollation);
                try
                {
                    await importedTemplate.Body.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    if (importedTemplate.DefaultCollation != null)
                        _defaultCollationStack.Pop();
                    if (importedTemplate.Version != null)
                        _effectiveVersionStack.Pop();
                }
                _currentTemplate = savedTemplate;
            }
            finally
            {
                PopScope();
            }
        }
        else
        {
            // No matching imported template - apply built-in template rule
            await ApplyBuiltInTemplateAsync(node, _currentMode, withParams).ConfigureAwait(false);
        }
    }


    public override async ValueTask NextMatchAsync(List<XsltWithParam> withParams, XsltSequenceConstructor? fallback)
    {
        CheckResourceLimits();
        // XTDE0560: next-match requires a current template rule
        if (_currentTemplate == null)
            throw Error("XTDE0560: xsl:next-match can only be used when there is a current template rule");
        var node = ContextItem;
        // XTDE0560: next-match requires a context item (fails when context-item use="absent")
        if (node == null || ReferenceEquals(node, PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus))
        {
            throw Error("XTDE0560: xsl:next-match requires a context item, but the context item is absent");
        }

        XsltTemplate? nextTemplate;
        using (var mc = AcquireMatchContext())
            nextTemplate = _templateIndex.FindMatchingTemplate(node, _currentMode, mc.Value, _currentTemplate);

        if (nextTemplate != null)
        {
            // Enforce xsl:context-item constraints
            var makeContextAbsent = EnforceContextItemConstraint(nextTemplate);

            PushScope();
            try
            {
                // If use="absent" or optional type mismatch, push absent focus
                if (nextTemplate.ContextItemUse == ContextItemUse.Absent || makeContextAbsent)
                {
                    PushContextItem(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus, 0, 0);
                    SuppressGroupingFocus();
                }

                // Propagate tunnel parameters from parent scopes
                InheritTunnelParameters();

                // Store explicit tunnel with-params
                foreach (var param in withParams.Where(p => p.Tunnel))
                {
                    var value = await EvaluateWithParamAsync(param).ConfigureAwait(false);
                    _scopes.Peek().TunnelParameters[param.Name] = value;
                }

                // Bind non-tunnel with-params to non-tunnel template params
                foreach (var param in withParams.Where(p => !p.Tunnel))
                {
                    var tp = nextTemplate.Parameters.FirstOrDefault(t =>
                        t.Name.Equals(param.Name) && !t.Tunnel);
                    if (tp != null)
                    {
                        var value = await EvaluateWithParamAsync(param).ConfigureAwait(false);
                        SetVariable(param.Name, value);
                    }
                }

                // Bind template parameters with defaults
                foreach (var param in nextTemplate.Parameters)
                {
                    if (!_scopes.Peek().Variables.ContainsKey(param.Name))
                    {
                        if (param.Tunnel && TryGetTunnelParam(param.Name, out var tunnelValue))
                        {
                            SetVariable(param.Name, tunnelValue);
                        }
                        else if (param.Select != null)
                        {
                            var value = await EvaluateAsync(param.Select).ConfigureAwait(false);
                            SetVariable(param.Name, value);
                        }
                    }
                }

                var savedTemplate = _currentTemplate;
                _currentTemplate = nextTemplate;
                if (nextTemplate.Version != null)
                    _effectiveVersionStack.Push(nextTemplate.Version);
                if (nextTemplate.DefaultCollation != null)
                    _defaultCollationStack.Push(nextTemplate.DefaultCollation);
                try
                {
                    await nextTemplate.Body.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    if (nextTemplate.DefaultCollation != null)
                        _defaultCollationStack.Pop();
                    if (nextTemplate.Version != null)
                        _effectiveVersionStack.Pop();
                }
                _currentTemplate = savedTemplate;
            }
            finally
            {
                if (nextTemplate.ContextItemUse == ContextItemUse.Absent || makeContextAbsent)
                    PopContextItem();
                PopScope();
            }
        }
        else
        {
            // No matching template found - apply built-in template rule
            // Note: xsl:fallback is NOT executed since we support xsl:next-match
            await ApplyBuiltInTemplateAsync(node, _currentMode, withParams).ConfigureAwait(false);
        }
    }


    /// <summary>
    /// Streaming implementation of xsl:apply-templates over the children of the
    /// current context node. Drives <see cref="_activeStreamingReader"/> directly,
    /// firing templates inline as each child element arrives, until the parent's
    /// EndElement event signals end-of-children. Sets
    /// <see cref="_streamingDeferReadOnNextIteration"/> so the streaming processor's
    /// outer loop can process the EndElement itself (close any deferred parent tag).
    /// </summary>
    private async ValueTask ApplyTemplatesStreamingAsync(QName? mode, List<XsltWithParam> withParams)
    {
        var reader = _activeStreamingReader!;
        var ct = _activeStreamingCancellationToken;
        var parentDepth = reader.Depth;
        var position = 0;

        // Forward these with-params into the streamed dispatch. Each node this loop
        // dispatches (and any built-in shallow-copy recursion underneath it) binds
        // this ambient set (si-apply-templates-008/009). Restored on exit.
        var savedForwardedParams = _streamingForwardedParams;
        _streamingForwardedParams = withParams;
        try
        {
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType == System.Xml.XmlNodeType.EndElement && reader.Depth == parentDepth)
            {
                _streamingDeferReadOnNextIteration = true;
                break;
            }

            switch (reader.NodeType)
            {
                case System.Xml.XmlNodeType.Element:
                {
                    var elem = await ReadStreamingElementForDispatchAsync(reader, ct).ConfigureAwait(false);
                    position++;
                    // Fire matching template (or default rule) inline, same path the main
                    // streaming processor uses. Pop any deferred element close on the way
                    // out — element template body's shallow-copy / xsl:copy may have pushed.
                    await MatchAndExecuteStreamingNodeAsync(elem, mode, position).ConfigureAwait(false);
                    // After the template runs, the deferred-close stack may contain the
                    // element's open tag (if shallow-copy / xsl:copy was used). Close it
                    // now, since we already consumed the element's full subtree.
                    if (_streamingOpenElements.Count > 0)
                    {
                        var qn = _streamingOpenElements.Pop();
                        WriteStreamingEndTag(qn);
                    }
                    break;
                }
                case System.Xml.XmlNodeType.Text:
                case System.Xml.XmlNodeType.CDATA:
                case System.Xml.XmlNodeType.SignificantWhitespace:
                {
                    // Apply templates to text node — default rule copies text to output
                    var textNode = new Xdm.Nodes.XdmText
                    {
                        Id = new NodeId(_nextStreamGroupNodeId++),
                        Document = DocumentId.None,
                        Value = reader.Value
                    };
                    _nodeStore!.Register(textNode);
                    position++;
                    await MatchAndExecuteStreamingNodeAsync(textNode, mode, position).ConfigureAwait(false);
                    break;
                }
                // Comments and PIs skipped — they're not matched by the default rule
                // for templating in this engine; out of scope for first cut.
            }
        }
        }
        finally
        {
            _streamingForwardedParams = savedForwardedParams;
        }
    }

}
