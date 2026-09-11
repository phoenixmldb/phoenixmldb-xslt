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

    /// <summary>
    /// Evaluates each per-item tail step of a watched SimpleMap against the
    /// watcher's captured items, in the same left-to-right order that
    /// <c>a!b!c</c> would: apply <c>b</c> to each captured item (flattening),
    /// then apply <c>c</c> to each result, etc.
    /// </summary>
    private async ValueTask<List<object?>> ApplySimpleMapTailAsync(SimpleMapExpression sm, object? sourceItems)
    {
        // CHEAP node-capture path (sx-GeneralComp-*-016/116/020/120): when the source
        // items are the watcher's captured childless elements and the single tail step is
        // a compile-time numeric op over ONE context-relative attribute (@value*2,
        // abs(@value), -@value, …), read the attribute value off each captured node and
        // apply the op inline. No PushContextItem / full-plan EvaluateAsync per node — that
        // is what timed out on 100k rows in the reverted prototype.
        if (sm.Left is not SimpleMapExpression
            && TryGetAttributeArithmeticTail(sm.Right, out var attrName))
        {
            var srcList = NormalizeToList(sourceItems);
            if (srcList.Count > 0 && srcList.TrueForAll(static it => it is Xdm.Nodes.XdmElement))
            {
                var cheap = new List<object?>(srcList.Count);
                foreach (var it in srcList)
                {
                    var el = (Xdm.Nodes.XdmElement)it!;
                    var raw = GetElementAttributeValue(el, attrName!);
                    if (raw == null) continue; // absent attribute contributes nothing
                    if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var av))
                        continue;
                    cheap.Add(EvaluateAttributeArithmeticTail(sm.Right, av));
                }
                return cheap;
            }
        }

        // Walk down the Left chain to collect tail-step Rights in order.
        // SimpleMap(SimpleMap(path, R1), R2) ⇒ [R1, R2].
        var tailSteps = new List<XQueryExpression>();
        XQueryExpression current = sm;
        while (current is SimpleMapExpression mapNode)
        {
            tailSteps.Add(mapNode.Right);
            current = mapNode.Left;
        }
        tailSteps.Reverse();

        var working = NormalizeToList(sourceItems);
        foreach (var step in tailSteps)
        {
            var next = new List<object?>();
            for (var i = 0; i < working.Count; i++)
            {
                PushContextItem(working[i], i + 1, working.Count);
                try
                {
                    var stepResult = await EvaluateAsync(step).ConfigureAwait(false);
                    AppendFlattened(next, stepResult);
                }
                finally
                {
                    PopContextItem();
                }
            }
            working = next;
        }
        return working;
    }


    private static bool MatchAttrArith(XQueryExpression tail, ref string? attr)
    {
        switch (tail)
        {
            case IntegerLiteral:
            case DoubleLiteral:
            case DecimalLiteral:
                return true;
            case PathExpression pe:
                if (pe.IsAbsolute || pe.InitialExpression != null || pe.Steps.Count != 1) return false;
                var step = pe.Steps[0];
                if (step.Axis != Axis.Attribute || step.Predicates.Count > 0) return false;
                if (step.NodeTest is not NameTest nt || nt.IsLocalNameWildcard) return false;
                if (attr != null && attr != nt.LocalName) return false;
                attr = nt.LocalName;
                return true;
            case UnaryExpression ue when ue.Operator is UnaryOperator.Plus or UnaryOperator.Minus:
                return MatchAttrArith(ue.Operand, ref attr);
            case BinaryExpression be when IsCheapNumericArithmetic(be.Operator):
                return MatchAttrArith(be.Left, ref attr) && MatchAttrArith(be.Right, ref attr);
            case FunctionCallExpression fc when IsCheapNumericFunction(fc):
                foreach (var a in fc.Arguments)
                    if (!MatchAttrArith(a, ref attr)) return false;
                return true;
            default:
                return false;
        }
    }


    /// <summary>
    /// Evaluates a recognized attribute-arithmetic tail against a single attribute value
    /// (already parsed to <paramref name="attrValue"/>). The tree is numeric literals,
    /// arithmetic, unary +/-, and numeric built-ins over one <c>@attr</c> — all evaluated
    /// as <see cref="double"/>. This is the compile-time-known op the cheap path applies
    /// per captured node.
    /// </summary>
    private static double EvaluateAttributeArithmeticTail(XQueryExpression tail, double attrValue) => tail switch
    {
        IntegerLiteral i => (double)(i.LongValue ?? 0),
        DoubleLiteral d => d.Value,
        DecimalLiteral dc => (double)dc.Value,
        PathExpression => attrValue, // the single @attr step
        UnaryExpression { Operator: UnaryOperator.Plus } up => EvaluateAttributeArithmeticTail(up.Operand, attrValue),
        UnaryExpression { Operator: UnaryOperator.Minus } um => -EvaluateAttributeArithmeticTail(um.Operand, attrValue),
        BinaryExpression be => ApplyCheapArith(be.Operator,
            EvaluateAttributeArithmeticTail(be.Left, attrValue),
            EvaluateAttributeArithmeticTail(be.Right, attrValue)),
        FunctionCallExpression fc => ApplyCheapNumericFn(fc,
            fc.Arguments.Count > 0 ? EvaluateAttributeArithmeticTail(fc.Arguments[0], attrValue) : attrValue),
        _ => attrValue
    };


    private static double ApplyCheapArith(BinaryOperator op, double a, double b) => op switch
    {
        BinaryOperator.Add => a + b,
        BinaryOperator.Subtract => a - b,
        BinaryOperator.Multiply => a * b,
        BinaryOperator.Divide => a / b,
        BinaryOperator.IntegerDivide => Math.Truncate(a / b),
        BinaryOperator.Modulo => a % b,
        _ => a
    };


    private static double ApplyCheapNumericFn(FunctionCallExpression fc, double v) => fc.Name.LocalName switch
    {
        "abs" => Math.Abs(v),
        "ceiling" => Math.Ceiling(v),
        "floor" => Math.Floor(v),
        "round" => Math.Round(v, MidpointRounding.AwayFromZero),
        _ => v // number/decimal/double/integer/float — identity on the already-numeric value
    };


    /// <summary>
    /// Group A: applies a wrapped-aggregation watcher's stored outer op to its
    /// grounded accumulated sequence. The sequence is fully materialized in memory
    /// (inner head/outermost/remove result + outermost-dedup + remove-skip), so the
    /// outer SimpleMap RIGHT or positional predicates run via the existing per-item
    /// evaluator with correct <c>position()</c>/<c>last()</c>. Returns an
    /// <c>object[]</c> (the grounded result) for <c>value-of</c>/<c>copy-of</c> to emit.
    /// </summary>
    private async ValueTask<object?> ApplyWrappedOuterOpAsync(StreamWatcher watcher)
    {
        var working = new List<object?>();
        foreach (var it in watcher.ProduceAccumulated())
            working.Add(it);

        // SimpleMap RIGHT: apply per item, flattening (mirrors ApplySimpleMapTailAsync).
        if (watcher.OuterSimpleMapRight is { } right)
        {
            var next = new List<object?>();
            for (var i = 0; i < working.Count; i++)
            {
                PushContextItem(working[i], i + 1, working.Count);
                try
                {
                    var stepResult = await EvaluateAsync(right).ConfigureAwait(false);
                    AppendFlattened(next, stepResult);
                }
                finally { PopContextItem(); }
            }
            return next.ToArray();
        }

        // Positional predicates: apply each left-to-right against the grounded
        // sequence, re-counting between predicates so position()/last() are correct.
        foreach (var pred in watcher.OuterPredicates)
        {
            var kept = new List<object?>();
            var count = working.Count;
            for (var i = 0; i < working.Count; i++)
            {
                PushContextItem(working[i], i + 1, count);
                try
                {
                    // A bare numeric literal [N] means position() = N; the generic
                    // EvaluateBooleanAsync would return the literal's effective boolean
                    // (always true for non-zero), so handle it positionally here.
                    bool ok = IsNumericLiteral(pred)
                        ? NumericLiteralEqualsPosition(pred, i + 1)
                        : await EvaluateBooleanAsync(pred).ConfigureAwait(false);
                    if (ok) kept.Add(working[i]);
                }
                finally { PopContextItem(); }
            }
            working = kept;
        }

        return working.ToArray();
    }


    private async ValueTask<List<object?>> EvaluateToListAsync(XQueryExpression expr)
    {
        var v = await EvaluateAsync(expr).ConfigureAwait(false);
        var list = new List<object?>();
        AppendFlattened(list, v);
        return list;
    }


    /// <summary>
    /// Evaluates the body content of an xsl:param / xsl:with-param (when no `select`
    /// attribute is present) into a typed value, preserving xsl:sequence-emitted items.
    ///
    /// The naive path (write to output buffer, then atomize the text) destroys typed
    /// items: an xsl:sequence emitting a document-node would serialize to text, then
    /// the text would be wrapped as XsUntypedAtomic, then bound to the parameter.
    /// Downstream code expecting `as="document-node()"` to preserve the doc-node would
    /// see XsUntypedAtomic and fail XPTY0020 on axis steps. Found in Docbook TNG
    /// fp:run-transforms's xsl:next-iteration with-param="document"; same shape as the
    /// xsl:variable accumulator-isolation fix earlier.
    ///
    /// Strategy: install a fresh sequence accumulator around the body. xsl:sequence
    /// inside the body appends items to it; literal text falls through to the output
    /// buffer. After execution, prefer the accumulator items when present (they retain
    /// type), otherwise fall back to the output text wrapped as untyped atomic.
    /// </summary>
    internal async ValueTask<object?> EvaluateBodyContentToValueAsync(XsltSequenceConstructor body)
    {
        var savedAccumulator = _sequenceAccumulator;
        _sequenceAccumulator = new List<object?>();
        var savedLen = _output.Length;
        _temporaryOutputDepth++;
        // Shared typed-body context (see EnterTypedBody). This evaluates a body to a SEQUENCE
        // value — never a temporary tree — so the caller's declared type is not available here
        // and the neutral form applies: attribute and namespace nodes are legal members of the
        // result, and a body that really does produce an attribute for an
        // `as="document-node()"` parameter is caught by the type check on the resulting value.
        var typedBody = EnterTypedBody(null);
        List<object?> captured;
        try
        {
            await body.ExecuteAsync(this).ConfigureAwait(false);
        }
        finally
        {
            captured = _sequenceAccumulator;
            _sequenceAccumulator = savedAccumulator;
            ExitTypedBody(typedBody);
            _temporaryOutputDepth--;
        }
        var content = _output.ToString(savedLen, _output.Length - savedLen);
        _output.Length = savedLen;
        if (captured.Count > 0)
        {
            // Typed items captured — return them (single-item unwrapped, multi as array).
            return captured.Count == 1 ? captured[0] : captured.ToArray();
        }
        // Fall back to text content as untyped atomic / RTF.
        return content.Contains('<', StringComparison.Ordinal)
            ? (object)new ResultTreeFragment(content)
            : new Xdm.XsUntypedAtomic(StripXmlMarkup(content));
    }


    /// <summary>
    /// #143 Task 1.3 — Release safety net for a guaranteed-streamable construct that reached a
    /// silent sink. Rather than the structure-losing text-only copy (or an empty emission), this
    /// preserves the node's full structure by deep-copying its buffered subtree — the correct,
    /// never-collapsed output. Under an active streaming reader the subtree is materialised from
    /// the live reader (identical contract to the snapshot()/copy-of()/deep-copy streaming paths);
    /// otherwise the node is already a fully-populated tree and is serialized directly. This is a
    /// backstop only: Task 1.2's StreamingPlanner.Plan dispatch routes such constructs before they
    /// ever reach here, so in a non-regressed engine this method is unreachable.
    /// </summary>
    private ValueTask ApplyGuaranteedStreamableBufferFallbackAsync(
        object node, QName? mode, List<XsltWithParam> withParams)
    {
        if (_activeStreamingReader != null && node is XdmElement streamedElem)
        {
            var bufferedRoot = StreamingSubtreeMaterializer.Materialize(
                _activeStreamingReader, _nodeStore!, new DocumentId(0)) ?? streamedElem;
            SerializeElement(bufferedRoot);
            _streamingSubtreeBufferConsumed = true;
            return ValueTask.CompletedTask;
        }
        if (node is XdmElement fullElem)
        {
            SerializeElement(fullElem);
            return ValueTask.CompletedTask;
        }
        // Document node with no live reader: recurse into children non-streaming, preserving
        // structure (the built-in document rule's structure-preserving behaviour, not text-only).
        return ApplyTemplatesAsync(null, mode, [], withParams);
    }


    /// <summary>
    /// Matches and executes a template for a streaming node.
    /// Returns true if the matched template suppressed the node (empty body = no output)
    /// OR if execution was deferred — in either case the streaming processor should
    /// treat children as suppressed (no template execution; watchers and accumulators
    /// still fire).
    /// </summary>
    internal async ValueTask<bool> MatchAndExecuteStreamingNodeAsync(XdmNode node, QName? mode, int position)
    {
        // last=0 signals "unknown in streaming mode". The StreamabilityChecker rejects
        // last() in streamable templates, so this value should never be accessed.
        PushContextItem(node, position, 0);
        PushCurrentItem(node);
        PushScope();
        var pushedScope = true;
        try
        {
            XsltTemplate? template;
            using (var mc = AcquireMatchContext())
                template = _templateIndex.FindMatchingTemplate(node, mode, mc.Value);
            if (template != null)
            {
                // Route the buffering decision through the single posture-derived
                // StreamingPlanner.Plan (#143 Task 1.2). A matched streaming template runs with
                // the matched element as a live-streamed striding context node, so classify the
                // body in that context. A Buffer plan (BufferMatchedSubtree — snapshot/copy-of of
                // the matched subtree or a re-traversing body; OR BufferWholeInput — a body that
                // navigates the matched subtree but is not guaranteed-streamable, e.g. the
                // 013-sibling shapes: nested apply-templates composite select, LRE navigating AVT,
                // on-empty/on-non-empty/where-populated navigating content, group-by for-each-group)
                // means the body cannot be driven off the live reader and must run against the
                // materialised matched subtree. Both plans map to the same in-template remedy —
                // materialise the matched element's subtree and run the body in memory — because
                // for a matched template the matched subtree IS the whole streamed scope. This is
                // the unification: the SAME plan the document level uses, applied in-template, so a
                // navigating body BUFFERS (correct output) instead of falling through to the
                // text-only sink and silently losing structure. StreamInline / NotStreaming stay on
                // the live streaming path below.
                //
                // EXECUTOR BACKSTOP: §19.8-guaranteed-streamable is a superset of what the
                // in-template executor can actually drive off the live reader. Where Plan reports
                // StreamInline for a shape the matched-template pass cannot yet stream, the proven
                // legacy RequiresSubtreeBuffer detector still forces the subtree buffer — keeping
                // the full-pass oracle clean. (TODO(#143 later phase): teach the executor those
                // shapes and drop this backstop.) The absorbing-stylesheet-function check is an
                // orthogonal signal Plan does not model (it needs the function table).
                var streamingBodyCtx = new Streamability.StreamingContext(
                    Streamability.Posture.Striding, InStreamedScope: true);
                var streamingBodyPlan = StreamingPlanner.Plan(template.Body, streamingBodyCtx);
                if (_activeStreamingReader != null
                    && node is Xdm.Nodes.XdmElement bufElem
                    && (streamingBodyPlan is StreamingPlan.BufferMatchedSubtree
                            or StreamingPlan.BufferWholeInput
                        || StreamingSubtreeBufferDetector.RequiresSubtreeBuffer(template.Body)
                        || StreamingSubtreeBufferDetector.RequiresSubtreeBufferForAbsorbingFunctions(
                            template.Body, _stylesheet.Functions)))
                {
                    await ExecuteWithBufferedSubtreeAsync(template, bufElem, mode, position).ConfigureAwait(false);
                    PopScope(); pushedScope = false;
                    PopCurrentItem();
                    PopContextItem();
                    _streamingSubtreeBufferConsumed = true;
                    return true;
                }

                // Pre-scan template body for consuming aggregates (count(*), sum(*), …).
                // If found AND the body doesn't itself call apply-templates (which would
                // consume children directly), DEFER body execution to parent EndElement
                // so watchers can accumulate the consuming-expression results first.
                // Without this deferral, the body evaluates count(*) before any children
                // have been read and gets 0.
                if (_activeStreamingReader != null
                    && node is Xdm.Nodes.XdmElement deferredElem
                    && TryBuildDeferredExecution(template, deferredElem, mode, position) is { } deferredEntry)
                {
                    _streamingDeferredExecutions.Push(deferredEntry);
                    deferredEntry.PriorActiveWatchers = _activeStreamWatchers;
                    _activeStreamWatchers = deferredEntry.Watchers;
                    // Pop the temporary scope/context — the real ones will be re-pushed
                    // when the deferred body executes at parent EndElement.
                    PopScope(); pushedScope = false;
                    PopCurrentItem();
                    PopContextItem();
                    return true; // signal suppression so processor skips child template execution
                }

                // An empty template body in streaming mode means "suppress this element"
                // — skip the element and all its children. Return true so the streaming
                // processor can skip child events.
                var isSuppression = template.Body.Instructions.Count == 0
                    && node is Xdm.Nodes.XdmElement;

                var savedTemplate = _currentTemplate;
                var savedMode = _currentMode;
                _currentTemplate = template;
                _currentMode = mode;

                // Bind template parameters. Forwarded with-params (from the apply-templates
                // that entered this streamed pass) take precedence over defaults; both tunnel
                // and non-tunnel are honoured, mirroring the non-streaming apply-templates
                // binding (si-apply-templates-008/009). The current scope was pushed above.
                var forwardedParams = _streamingForwardedParams;

                // Propagate tunnel parameters from parent scopes into this scope.
                InheritTunnelParameters();
                // Register forwarded tunnel params in this scope's tunnel table.
                foreach (var param in forwardedParams.Where(p => p.Tunnel))
                {
                    var value = await EvaluateWithParamAsync(param).ConfigureAwait(false);
                    _scopes.Peek().TunnelParameters[param.Name] = value;
                }
                // Bind forwarded non-tunnel params to matching template params.
                foreach (var param in forwardedParams.Where(p => !p.Tunnel))
                {
                    var templateParam = template.Parameters.FirstOrDefault(tp =>
                        tp.Name.Equals(param.Name) && !tp.Tunnel);
                    if (templateParam != null)
                    {
                        var value = await EvaluateWithParamAsync(param).ConfigureAwait(false);
                        if (templateParam.As != null)
                        {
                            value = CoerceToType(value, templateParam.As);
                            ValidateValueMatchesType(value, templateParam.As, "XTTE0590",
                                $"Parameter ${param.Name.LocalName}");
                        }
                        SetVariable(param.Name, value);
                    }
                }
                // Bind any remaining template params: tunnel-supplied value, then default.
                foreach (var param in template.Parameters)
                {
                    if (_scopes.Peek().Variables.ContainsKey(param.Name))
                        continue;
                    if (param.Tunnel && TryGetTunnelParam(param.Name, out var tunnelValue))
                    {
                        if (param.As != null)
                            tunnelValue = CoerceToType(tunnelValue, param.As);
                        SetVariable(param.Name, tunnelValue);
                    }
                    else if (param.Select != null)
                    {
                        var defaultVal = await EvaluateAsync(param.Select).ConfigureAwait(false);
                        if (param.As != null)
                            defaultVal = CoerceToType(defaultVal, param.As);
                        SetVariable(param.Name, defaultVal);
                    }
                    else
                    {
                        SetVariable(param.Name, "");
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
                    await template.Body.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    if (template.BaseUri != null)
                        _staticBaseUriStack.Pop();
                    if (template.DefaultCollation != null)
                        _defaultCollationStack.Pop();
                    if (template.Version != null)
                        _effectiveVersionStack.Pop();
                    _currentTemplate = savedTemplate;
                    _currentMode = savedMode;
                }

                return isSuppression;
            }
            else
            {
                // Phase 2a (#143): streaming built-in DEEP-COPY of an unmatched element.
                // The streamed element is shallow (its children have not yet been read),
                // so serializing it directly would emit just an empty tag while the loop
                // reads the descendants as flat siblings. Instead buffer the whole subtree
                // from the live reader (same materialize + deferred-EndElement contract the
                // snapshot()/copy-of() path uses) and serialize the buffered tree. Setting
                // _streamingSubtreeBufferConsumed tells the processor's loop that the
                // matching EndElement was already consumed, so it skips the ancestor push.
                if (_activeStreamingReader != null
                    && node is Xdm.Nodes.XdmElement deepCopyElem
                    && GetOnNoMatchBehavior(mode) == OnNoMatchBehavior.DeepCopy)
                {
                    var bufferedRoot = StreamingSubtreeMaterializer.Materialize(
                        _activeStreamingReader, _nodeStore!, new DocumentId(0)) ?? deepCopyElem;
                    SerializeElement(bufferedRoot);
                    _streamingSubtreeBufferConsumed = true;
                    return false;
                }
                // Apply built-in template rules. Forward the ambient with-params so the
                // built-in shallow-copy rule passes them to matched attribute/child templates
                // (si-apply-templates-008: w/@id rule receives prefix/suffix). Per spec the
                // built-in rules forward all caller params unchanged.
                await ApplyBuiltInTemplateAsync(node, mode, _streamingForwardedParams).ConfigureAwait(false);
            }
        }
        finally
        {
            if (pushedScope)
            {
                PopScope();
                PopCurrentItem();
                PopContextItem();
            }
        }
        return false;
    }


    private bool MatchesPattern(object item, XsltPattern pattern)
    {
        // XSLT 3.0 allows patterns to match atomic values (e.g., ".[. instance of xs:string]")
        // Let the pattern decide if it can match the item type
        using var mc = AcquireMatchContext();
        return pattern.Matches(item, mc.Value);
    }


    public override async ValueTask<bool> EvaluateBooleanAsync(XQueryExpression expr)
    {
        var result = await EvaluateAsync(expr).ConfigureAwait(false);
        return EffectiveBooleanValue(result);
    }


    public override async ValueTask<object?> EvaluateAsync(XQueryExpression expr)
    {
        // Fast path for simple expressions
        switch (expr)
        {
            case StringLiteral sl:
                return sl.Value;
            case IntegerLiteral il:
                return il.Value;
            case DoubleLiteral dl:
                return dl.Value;
            case BooleanLiteral bl:
                return bl.Value;
            case VariableReference vr:
                return GetVariable(vr.Name);
            case ContextItemExpression:
                var ci = ContextItem;
                if (ReferenceEquals(ci, PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus))
                    // Say what was being evaluated and where. "Context item is absent" restates
                    // the error code: an author already knows what XPDY0002 means, and needs to
                    // know WHICH expression asked for a focus that is not there. The context
                    // item is absent by DESIGN in several places — a global variable, a named
                    // template called without one, xsl:context-item use="absent" — so the
                    // instruction is the part that locates the mistake.
                    throw Error("XPDY0002: Context item is absent"
                        + " — evaluating '.' (the context item)"
                        + (_currentInstructionLocation != null
                            ? $" at {_currentInstructionLocation}"
                            // Say the location is unknown, not that there is no enclosing
                            // instruction. Those are different claims and only the first is
                            // supported by _currentInstructionLocation being null.
                            : " (source location not recorded for this expression)"));
                return ci;
            // Fast path: position() and last() — avoid full XQuery pipeline in loops
            case FunctionCallExpression { Arguments.Count: 0 } fce
                when fce.Name.LocalName == "position" && (fce.Name.Namespace == NamespaceId.None || fce.Name.Namespace == NamespaceId.Fn):
                if (ReferenceEquals(ContextItem, PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus))
                    throw Error("XPDY0002: Context position is absent");
                return (long)Position;
            case FunctionCallExpression { Arguments.Count: 0 } fce2
                when fce2.Name.LocalName == "last" && (fce2.Name.Namespace == NamespaceId.None || fce2.Name.Namespace == NamespaceId.Fn):
                if (ReferenceEquals(ContextItem, PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus))
                    throw Error("XPDY0002: Context size is absent");
                return (long)Last;
            // Fast path: simple comparisons with position()/last() and integer literals
            case BinaryExpression { Operator: var op } be
                when (op >= BinaryOperator.Equal && op <= BinaryOperator.GreaterOrEqual
                   || op >= BinaryOperator.GeneralEqual && op <= BinaryOperator.GeneralGreaterOrEqual)
                && TryEvaluateSimpleComparison(be) is { } result:
                return result;
        }

        // Check for stream watcher result substitution.
        // This handles both direct matches (entire expr is a watcher) and
        // nested matches (expr contains watcher sub-expressions, e.g., map entries).
        if (_activeStreamWatchers != null)
        {
            // Group A — wrapped head/outermost/remove aggregation. When the whole
            // select is a watched wrapper carrying an outer op (positional
            // predicates / SimpleMap RIGHT), apply that op to the grounded
            // accumulated sequence (inner result + outermost-dedup + remove-skip)
            // via the existing per-item evaluator, and return the post-processed
            // grounded sequence for emission.
            var directWatcher = FindDirectWatcher(expr, _activeStreamWatchers);
            if (directWatcher != null
                && (directWatcher.OuterPredicates.Count > 0 || directWatcher.OuterSimpleMapRight != null))
            {
                return await ApplyWrappedOuterOpAsync(directWatcher).ConfigureAwait(false);
            }

            // B2 — fn:sum(seq, $zero) over an EMPTY streamed sequence returns the $zero
            // default, not an empty value-of. The default rides on the watcher as an
            // expression and is evaluated here against the live scope only when nothing
            // matched (sf-sum-011/041/042). Only the two-arg form is handled; a single-arg
            // empty sum keeps its existing (null → empty) contract untouched. Non-empty
            // sums fall through to the normal GetResult() path below unchanged.
            if (directWatcher != null && directWatcher.SumIsEmpty
                && directWatcher.SumDefaultExpression != null)
            {
                return await EvaluateAsync(directWatcher.SumDefaultExpression).ConfigureAwait(false);
            }

            // B2 (per-item window over a grounded atomic tail) — when the whole select is
            // an atomic-tail SimpleMap watcher (`path ! RIGHT`, RIGHT a per-item atomic
            // expression on the context item), re-apply RIGHT to each captured leaf value.
            // The watcher only captured the raw path leaves; the RIGHT — e.g.
            // `head(tokenize(., ' '))` / `subsequence(tokenize(.), $s)` /
            // `insert-before(tokenize(.), 2, $ins)` — must run per item so its positional
            // window is honored rather than dropped (sf-head/tail/remove/subsequence/
            // insert-before -003/-103). This mirrors the binary-operand SimpleMap tail
            // application already used for general comparisons; here it feeds value-of.
            if (directWatcher != null
                && directWatcher.SourceExpression is SimpleMapExpression directSm
                && directWatcher.Aggregation == WatcherAggregation.Sequence
                && directWatcher.OuterPredicates.Count == 0
                && directWatcher.OuterSimpleMapRight == null)
            {
                var tailed = await ApplySimpleMapTailAsync(directSm, directWatcher.GetResult()).ConfigureAwait(false);
                return tailed.ToArray();
            }

            var watcherResult = TryResolveFromWatchers(expr, _activeStreamWatchers);
            if (watcherResult.Resolved)
                return watcherResult.Value;

            // For function calls where all arguments are resolvable from watchers or literals,
            // evaluate them directly to avoid losing watcher values inside the XQuery plan.
            // This handles patterns like translate(head(//AUTHOR), ' ', '_') in AVT contexts.
            if (expr is FunctionCallExpression fcall && fcall.Arguments.Count > 0)
            {
                var fnResult = await TryEvaluateFunctionWithWatcherArgs(fcall, _activeStreamWatchers).ConfigureAwait(false);
                if (fnResult.Resolved)
                    return fnResult.Value;
            }

            // General comparison whose operand is a watched SimpleMap source —
            // e.g. (/*/*/ITEM/DIMENSIONS!xs:NMTOKENS(.)!xs:decimal(.)) = $s.
            // The plan-level path inside the SimpleMap re-evaluates against
            // the synthetic empty doc and short-circuits to false. Instead,
            // substitute the watcher's captured items into the SimpleMap by
            // re-running its tail (Right) per item with the item as context,
            // then run a general comparison against the other operand. This
            // surfaces XPTY0004 from the tail casts rather than masking it.
            if (expr is BinaryExpression cmp
                && IsGeneralComparison(cmp.Operator))
            {
                var compResult = await TryEvaluateGeneralCompWithSimpleMapWatcherAsync(cmp, _activeStreamWatchers).ConfigureAwait(false);
                if (compResult.Resolved)
                    return compResult.Value;
            }

            // Last-resort substitution: if the expression contains a watched
            // sub-expression strictly *inside* it (e.g. snapshot(/chapter) deep
            // within innermost(snapshot(/chapter)//section)/@id), rewrite the
            // watched sub-expression to a synthetic $__streaming_watcher_N
            // variable reference and re-evaluate. The XQuery engine then runs
            // naturally against the materialised subtree without re-touching
            // the now-closed stream.
            var rewritten = RewriteWithWatcherVariables(expr, _activeStreamWatchers);
            if (!ReferenceEquals(rewritten, expr))
            {
                return await EvaluateAsync(rewritten).ConfigureAwait(false);
            }
        }

        // Resolve NamespaceUri → ResolvedNamespace on NameTests using node store
        if (_nodeStore != null)
            ResolveExpressionNamespaceIds(expr);

        // Full XQuery evaluation via optimizer + executor (with plan caching)
        if (!_planCache.TryGetValue(expr, out var plan))
        {
            var optimizer = new PhoenixmlDb.XQuery.Optimizer.QueryOptimizer();
            var optContext = new PhoenixmlDb.XQuery.Optimizer.OptimizationContext { Container = default, BackwardsCompatible = IsBackwardsCompatible, FunctionLibrary = _functionLibrary };
            plan = optimizer.Optimize(expr, optContext);
            _planCache[expr] = plan;
        }

        // Pass the node store directly as the node provider — this preserves INodeBuilder
        // so XQuery constructors can create proper XDM elements instead of serialized strings
        PhoenixmlDb.XQuery.INodeProvider? nodeProvider = _nodeStore;

        using var execContext = new PhoenixmlDb.XQuery.Execution.QueryExecutionContext(
            container: default,
            functions: _functionLibrary,
            nodeProvider: nodeProvider,
            documentResolver: (PhoenixmlDb.XQuery.IDocumentResolver?)_policyResolver ?? _documentResolver,
            schemaProvider: _schemaProvider,
            namespaceResolver: _nodeStore != null ? _nodeStore.GetNamespaceUri : null);
        // Provide in-scope namespace prefix bindings so XSLT functions (system-property, etc.)
        // can resolve prefixed QName string arguments at runtime
        // When inside xsl:evaluate with namespace-context, use those bindings instead
        execContext.PrefixNamespaceBindings = _evaluateNamespaceBindings != null
            ? (IReadOnlyDictionary<string, string>)_evaluateNamespaceBindings
            : XPathNamespaceBindings;
        execContext.BackwardsCompatible = IsBackwardsCompatible;
        execContext.InsideXslEvaluate = _insideXslEvaluateDepth > 0;
        execContext.DefaultCollation = DefaultCollation;
        execContext.StaticBaseUri = StaticBaseUri;

        // Set up variable fallback for lazy global initialization.
        // When the XQuery engine encounters a variable not yet bound, this callback
        // triggers the XSLT GetVariable which can lazily initialize pending globals.
        execContext.VariableFallback = varName =>
        {
            try
            {
                var value = GetVariable(varName);
                return (true, ConvertRtfForXQuery(value));
            }
            catch (XsltException ex) when (ex.ErrorCode != "XTDE0640"
                && !ex.IsDeferredGlobalError
                && !ex.Message.Contains("private to its package", StringComparison.Ordinal))
            {
                return (false, null);
            }
        };

        // Bind XSLT variables into XQuery context
        // Bind outer scopes first, then inner scopes, so inner scope values override (proper shadowing)
        // Convert ResultTreeFragments to XDM documents so XPath can navigate into them
        var privateGlobals = _stylesheet.PackagePrivateGlobals;
        var currentPkgForGlobals = privateGlobals.Count > 0 ? CurrentComponentPackage() : null;
        foreach (var (name, value) in GlobalVariables)
        {
            // A global a used package keeps private must not be bound into an expression
            // evaluated by a DIFFERENT package: leaving it unbound routes the reference through
            // VariableFallback → GetVariable, which raises XPST0008 (use-package-006/007). The
            // owning package's own components (matching CurrentComponentPackage) still see it.
            if (privateGlobals.Count > 0
                && privateGlobals.TryGetValue(name, out var owner)
                && !ReferenceEquals(currentPkgForGlobals, owner))
                continue;
            // A global bound to a LazyValue is deferred: either an abstract-variable proxy
            // (XTDE3052) or one whose eager initializer failed and must re-raise at the point of
            // reference. ConvertRtfForXQuery passes LazyValue through untouched, so binding it
            // here hands XQuery the wrapper as an *item* ("context item is not a node: got item
            // of type LazyValue"). Leaving it unbound routes the reference through
            // VariableFallback → GetVariable, which forces it and reports the real error.
            if (value is LazyValue)
                continue;
            execContext.BindVariable(name, ConvertRtfForXQuery(value));
        }
        // Reverse iteration: bind outer scopes before inner scopes
        foreach (var scope in _scopes.Reverse())
        {
            foreach (var (name, value) in scope.Variables)
            {
                execContext.BindVariable(name, ConvertRtfForXQuery(value));
            }
        }

        // Bind stream watcher results as synthetic variables for map constructor entries etc.
        if (_activeStreamWatchers != null)
        {
            for (var wi = 0; wi < _activeStreamWatchers.Count; wi++)
            {
                var watcherVar = new QName(NamespaceId.None, $"__streaming_watcher_{wi}");
                var w = _activeStreamWatchers[wi];
                // Group A wrapped-aggregation watcher: bind the GROUNDED BASE sequence
                // (inner result + outermost-dedup + remove-skip) so a re-wrapped outer
                // predicate — `$__streaming_watcher_N[pred…]`, injected by
                // RewriteWithWatcherVariables for operands like `outermost(//PRICE)[1] + $two`
                // — filters over the correct base. Bare (non-wrapped) watchers keep the
                // GetResult() contract used by map-entry / general-comparison consumers.
                var bound = (w.OuterPredicates.Count > 0 || w.OuterSimpleMapRight != null)
                    ? (object?)w.ProduceAccumulated()
                    : w.GetResult();
                execContext.BindVariable(watcherVar, bound);
            }
        }

        // Set context item
        if (ContextItem != null)
        {
            execContext.PushContextItem(ContextItem, Position, Last);
        }

        // Execute and collect results.
        // Catch XQueryException missing source-module info and re-throw with the offending
        // XPath expression text + AST type prefixed. Without this, errors like
        // "XPTY0020: [line 2, col 24] An axis step was used..." give no clue WHICH
        // expression in WHICH XSLT module raised them. The expression text alone is
        // enough to bisect Docbook-TNG-scale stylesheets where many xpaths look alike.
        var results = new List<object?>();
        try
        {
            await foreach (var item in plan.Root.ExecuteAsync(execContext).ConfigureAwait(false))
            {
                results.Add(item);
            }
        }
        // XQueryRuntimeException is a SIBLING of XQueryException, not a subclass — both derive
        // straight from Exception. The catch below therefore never sees it, so an error raised
        // through that type (FODC0002 from doc(), most of the runtime codes) recorded no
        // location and left $err:module and $err:line-number empty in xsl:catch. try-018 asserts
        // both and fails on exactly that. Record the location and rethrow unchanged: the
        // diagnostic rewrapping below is deliberately NOT applied here, because these messages
        // already carry their own position and rewrapping them would change text that other
        // tests match on.
        catch (PhoenixmlDb.XQuery.Execution.XQueryRuntimeException)
        {
            if (expr.Location is not null)
                _lastExpressionErrorLocation = expr.Location;
            throw;
        }
        catch (PhoenixmlDb.XQuery.Functions.XQueryException xqe) when (string.IsNullOrEmpty(xqe.Module))
        {
            var snippet = expr.ToString() ?? "(unknown)";
            if (snippet.Length > 200) snippet = snippet[..200] + "…";
            // If the XSLT compiler stamped this expression's Location with the source
            // module URI (StylesheetParser.AttachXsltSourceLocation), surface that as
            // a [module:line] prefix. The original [line N, col M] from xqe.Message stays —
            // it pinpoints the position WITHIN the inline XPath string. Together they
            // identify "which XSLT element + which character in its XPath."
            var sourcePrefix = expr.Location is { Module: { Length: > 0 } mod } loc
                ? $"[{mod}:{loc.Line}] "
                : "";
            // Same location, kept structurally for $err:module / $err:line-number. Recorded
            // whenever a location exists at all, not only when it carries a module URI: a
            // stylesheet compiled from a string has no module to report, but its line numbers
            // are still meaningful and $err:line-number must not go empty because of it.
            if (expr.Location is not null)
                _lastExpressionErrorLocation = expr.Location;
            throw new PhoenixmlDb.XQuery.Functions.XQueryException(
                xqe.ErrorCode,
                $"{sourcePrefix}{xqe.Message}\n  ↳ in expression ({expr.GetType().Name}): {snippet}",
                xqe);
        }

        return results.Count switch
        {
            0 => null,
            1 => results[0],
            _ => results.ToArray()
        };
    }


    private async ValueTask ApplyAttributeSetsAsync(List<QName> attrSetNames, StringBuilder target)
    {
        foreach (var attrSetName in attrSetNames)
        {
            // XTDE0640: detect circular attribute set references using field-level tracker
            // so cycles through LRE elements (which create new call frames) are caught
            if (!_activeAttributeSets.Add(attrSetName))
            {
                throw Error($"XTDE0640: Circular reference in attribute set '{attrSetName}'");
            }

            var pushedCurrentAttrSet = false;
            try
            {
                // use-attribute-sets="xsl:original" resolves to the overridden attribute-set
                // of the overriding xsl:attribute-set currently being expanded (XSLT 3.0 §3.5.6).
                XsltAttributeSet? attrSet;
                var isXslOriginal = attrSetName.Namespace == NamespaceId.Xslt
                    && attrSetName.LocalName == "original";
                if (isXslOriginal)
                {
                    attrSet = _currentAttributeSetStack.Count > 0
                        ? _currentAttributeSetStack.Peek().OriginalAttributeSet
                        : null;
                    if (attrSet == null)
                        throw Error("XTSE0710: use-attribute-sets=\"xsl:original\" has no overridden attribute set");
                }
                else
                {
                    // Package-local resolution first: when the attribute-set currently being
                    // expanded belongs to a used package, its nested use-attribute-sets
                    // references resolve within that package's own scope — including the
                    // package's private/abstract sets — not the merged registry
                    // (override-as-005: base's as-public must reach base's private as-private,
                    // and the using package's own like-named set must stay separate).
                    var ownerPkg = _currentAttributeSetStack.Count > 0
                        ? _currentAttributeSetStack.Peek().PackageStylesheet
                        : null;
                    if (ownerPkg != null && ownerPkg.AttributeSets.TryGetValue(attrSetName, out var pkgSet))
                    {
                        if (pkgSet.Visibility == Ast.Visibility.Abstract || pkgSet.IsAbstract)
                            throw Error($"XTDE3052: Abstract attribute-set '{attrSetName}' has no concrete implementation");
                        attrSet = pkgSet;
                    }
                    else
                    {
                        _stylesheet.AttributeSets.TryGetValue(attrSetName, out attrSet);
                    }
                    // An abstract attribute-set from a used package that was never overridden
                    // cannot be instantiated (XTDE3052).
                    if (attrSet == null && _stylesheet.AbstractAttributeSetNames.Contains(attrSetName))
                        throw Error($"XTDE3052: Abstract attribute-set '{attrSetName}' has no concrete implementation");
                }

                if (attrSet != null)
                {
                    _currentAttributeSetStack.Push(attrSet);
                    pushedCurrentAttrSet = true;
                    // Temporarily disable attribute collection mode so that the xsl:attribute
                    // instructions write to _output (which we capture) instead of _collectedAttributes
                    // Save and clear the stack, then restore after
                    var savedStack = _collectedAttributesStack.ToArray();
                    _collectedAttributesStack.Clear();

                    // Attribute sets are always applied to elements, not document nodes.
                    // Save and reset _documentNodeDepth so XTDE0420 checks don't fire.
                    var savedDocDepth = _documentNodeDepth;
                    _documentNodeDepth = 0;

                    // Per XSLT spec: only top-level variables and parameters are visible
                    // within attribute set declarations. Save/replace scope stack.
                    var savedScopes = _scopes.ToArray();
                    _scopes.Clear();
                    _scopes.Push(new Scope()); // Fresh scope with access to GlobalVariables only

                    var savedForAttr = _output.ToString();
                    _output.Clear();

                    if (attrSet.Parts is { Count: > 0 })
                    {
                        // Multiple definitions: evaluate each part's use-attribute-sets
                        // then its local attributes, in document order (XSLT 3.0 §10.2.2)
                        foreach (var part in attrSet.Parts)
                        {
                            // Flush any accumulated output before recursive call
                            target.Append(_output);
                            _output.Clear();
                            // Push xml:base onto static base URI stack for this part
                            if (part.BaseUri != null)
                                _staticBaseUriStack.Push(XsltTransformEngine.UriString(part.BaseUri)!);
                            try
                            {
                                if (part.UseAttributeSets.Count > 0)
                                    await ApplyAttributeSetsAsync(part.UseAttributeSets, target).ConfigureAwait(false);
                                foreach (var attr in part.Attributes)
                                    await attr.ExecuteAsync(this).ConfigureAwait(false);
                            }
                            finally
                            {
                                if (part.BaseUri != null)
                                    _staticBaseUriStack.Pop();
                            }
                        }
                    }
                    else
                    {
                        // Single definition: expand inherited sets first, then local attributes
                        if (attrSet.BaseUri != null)
                            _staticBaseUriStack.Push(XsltTransformEngine.UriString(attrSet.BaseUri)!);
                        try
                        {
                            if (attrSet.UseAttributeSets.Count > 0)
                                await ApplyAttributeSetsAsync(attrSet.UseAttributeSets, target).ConfigureAwait(false);
                            foreach (var attr in attrSet.Attributes)
                                await attr.ExecuteAsync(this).ConfigureAwait(false);
                        }
                        finally
                        {
                            if (attrSet.BaseUri != null)
                                _staticBaseUriStack.Pop();
                        }
                    }

                    target.Append(_output);
                    _output.Clear();
                    _output.Append(savedForAttr);

                    // Restore scope stack
                    _scopes.Clear();
                    foreach (var scope in savedScopes.Reverse())
                        _scopes.Push(scope);

                    // Restore attribute collection stack and document node depth
                    _documentNodeDepth = savedDocDepth;
                    foreach (var sb in savedStack.Reverse())
                        _collectedAttributesStack.Push(sb);
                }
            }
            finally
            {
                if (pushedCurrentAttrSet)
                    _currentAttributeSetStack.Pop();
                _activeAttributeSets.Remove(attrSetName);
            }
        }
    }


    /// <summary>
    /// Creates a synthetic XsltOutput with the given method, inheriting other properties
    /// (omit-xml-declaration, encoding, etc.) from the stylesheet's default output declaration.
    /// Used when xsl:result-document has an explicit method attribute but no format attribute.
    /// </summary>
    /// <summary>
    /// Returns <paramref name="baseDecl"/> with the doctype-public/doctype-system parameters
    /// overridden by the values specified on an xsl:result-document. Used when a result-document
    /// targeting the principal output supplies its own doctype parameters (which may be a
    /// zero-length string to override an inherited non-empty value back to "none"; erratum E31,
    /// XSLT test output-0313). Returns <paramref name="baseDecl"/> unchanged if neither override
    /// is present.
    /// </summary>
    private static Ast.XsltOutput? ApplyResultDocumentDoctype(
        Ast.XsltOutput? baseDecl, string? doctypePublic, string? doctypeSystem)
    {
        if (baseDecl == null || (doctypePublic == null && doctypeSystem == null))
            return baseDecl;
        return new Ast.XsltOutput
        {
            Name = baseDecl.Name,
            ImportPrecedence = baseDecl.ImportPrecedence,
            Method = baseDecl.Method,
            Version = baseDecl.Version,
            Encoding = baseDecl.Encoding,
            OmitXmlDeclaration = baseDecl.OmitXmlDeclaration,
            Standalone = baseDecl.Standalone,
            DoctypePublic = doctypePublic ?? baseDecl.DoctypePublic,
            DoctypeSystem = doctypeSystem ?? baseDecl.DoctypeSystem,
            CdataSectionElements = baseDecl.CdataSectionElements,
            Indent = baseDecl.Indent,
            MediaType = baseDecl.MediaType,
            IncludeContentType = baseDecl.IncludeContentType,
            EscapeUriAttributes = baseDecl.EscapeUriAttributes,
            UndeclarePrefixes = baseDecl.UndeclarePrefixes,
            NormalizationForm = baseDecl.NormalizationForm,
            ItemSeparator = baseDecl.ItemSeparator,
            HtmlVersion = baseDecl.HtmlVersion,
            BuildTree = baseDecl.BuildTree,
            AllowDuplicateNames = baseDecl.AllowDuplicateNames,
            UseCharacterMaps = baseDecl.UseCharacterMaps,
            PackageStylesheet = baseDecl.PackageStylesheet,
            SuppressIndentation = baseDecl.SuppressIndentation,
            ByteOrderMark = baseDecl.ByteOrderMark,
            JsonNodeOutputMethod = baseDecl.JsonNodeOutputMethod,
        };
    }


    /// <summary>
    /// Evaluates the <c>cdata-section-elements</c> AVT on an xsl:result-document (result-document-0401)
    /// into a set of QNames, resolving each whitespace-separated token against the result-document
    /// element's in-scope namespace bindings (including the default namespace, per the
    /// cdata-section-elements resolution rules). Returns <c>null</c> if the attribute is absent or empty.
    /// </summary>
    private async ValueTask<HashSet<QName>?> EvaluateCdataSectionElementsAsync(Ast.XsltResultDocument instruction)
    {
        if (instruction.CdataSectionElements == null)
            return null;
        var raw = await EvaluateAvtAsync(instruction.CdataSectionElements).ConfigureAwait(false);
        HashSet<QName>? set = null;
        foreach (var token in raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            (set ??= []).Add(ResolveCdataSectionQName(token, instruction.NamespaceBindings));
        }
        return set;
    }


    internal async ValueTask<object?> EvaluateAccumulatorRuleAsync(
        object node, XsltAccumulatorRule rule, object? currentValue, XsltAccumulator accumulator)
    {
        PushScope();
        PushContextItem(node, 1, 1);
        PushCurrentItem(node);
        SetVariable(new QName(NamespaceId.None, "value"), currentValue);
        // XTDE1480: Accumulator rule evaluation is in temporary output state
        _temporaryOutputDepth++;
        try
        {
            if (rule.Select != null)
                currentValue = await EvaluateAsync(rule.Select).ConfigureAwait(false);
            else if (rule.Content != null)
            {
                BeginSequenceCollection();
                await rule.Content.ExecuteAsync(this).ConfigureAwait(false);
                var items = EndSequenceCollection();
                currentValue = items.Count == 1 ? items[0] : items;
            }
        }
        finally
        {
            _temporaryOutputDepth--;
            PopCurrentItem();
            PopContextItem();
            PopScope();
        }
        if (accumulator.As != null)
            currentValue = CoerceAccumulatorValue(currentValue, accumulator.As, accumulator.Name);
        return currentValue;
    }


    private static bool MatchesAtomicType(object? value, ItemType type) => type switch
    {
        ItemType.String => value is string,
        ItemType.Boolean => value is bool,
        // xs:integer is UNBOUNDED in XSD, so a value too wide for long is carried as BigInteger —
        // xs:integer() and the overflow-safe arithmetic both produce one. The XQuery engine's
        // MatchesItemType already accepts it for ItemType.Integer; this separate matcher did not,
        // so an accumulator declared as="xs:integer" whose rule used xs:integer() failed its type
        // check on EVERY node. The throw becomes a deferred error, which freezes the accumulator,
        // so it silently reported its initial value forever rather than erroring.
        ItemType.Integer => value is int or long or System.Numerics.BigInteger,
        ItemType.Double => value is double,
        ItemType.Decimal => value is decimal,
        ItemType.Float => value is float,
        ItemType.AnyAtomicType => true, // any value is acceptable
        // Nodes atomize to untypedAtomic in non-schema-validated mode,
        // so both string values and node values are acceptable for untypedAtomic
        ItemType.UntypedAtomic => value is string or Xdm.XsUntypedAtomic or XdmNode or XdmDocument,
        _ => true // Unknown types: don't reject
    };


    /// <summary>
    /// Matches a pattern against a node, computing the correct position context
    /// for predicate evaluation. Position is computed among siblings matching the node test.
    /// </summary>
    private bool MatchesWithSiblingPosition(XsltPattern pattern, XdmNode node)
    {
        if (node.Parent is not { } parentId || parentId == NodeId.None)
        {
            // Root node - no siblings, position is 1
            return pattern.Matches(node, CreateMatchContext(1, 1));
        }

        var parent = _nodeStore!.GetNode(parentId);
        if (parent == null)
            return pattern.Matches(node, CreateMatchContext(1, 1));

        var children = _nodeStore.GetChildren(parent).ToList();
        int totalMatchingNodeTest = children.Count(c => pattern.MatchesNodeTest(c));
        int position = 0;

        foreach (var sibling in children)
        {
            if (pattern.MatchesNodeTest(sibling))
                position++;
            if (sibling.Id == node.Id)
                break;
        }

        return pattern.Matches(node, CreateMatchContext(position, totalMatchingNodeTest));
    }


#pragma warning disable CA1859 // Callback type requires XdmNode? not XdmDocument?
    private XdmNode? EvaluateDocPattern(string uri)
#pragma warning restore CA1859
    {
        try
        {
            return _policyResolver?.ResolveDocument(uri) ?? _documentResolver.ResolveDocument(uri);
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031
    }


    private object? EvaluateVariablePattern(QName variableName)
    {
        try
        {
            var value = GetVariable(variableName);
            // RTF variables need to be parsed to XDM for node identity matching
            if (value is ResultTreeFragment rtf)
                return ParseResultTreeFragment(rtf);
            return value;
        }
        catch (XsltException ex) when (ex.ErrorCode == "XTDE0640")
        {
            throw; // Non-recoverable: circular reference must propagate
        }
        catch (XsltException)
        {
            return null;
        }
    }


    /// <summary>
    /// Evaluates a chain of two or more pattern predicates against <paramref name="node"/>
    /// with true XPath filter semantics. Builds the candidate sequence the node belongs to
    /// (siblings matching the node test, or all descendants of the descendant-axis ancestor,
    /// in document order), then applies each predicate to the survivors of the previous
    /// predicate so that <c>position()</c>/<c>last()</c> re-index against the surviving
    /// sequence. Returns true iff <paramref name="node"/> is in the final filtered sequence.
    /// Backs <see cref="XsltContext.SequencePredicateEvaluator"/>.
    /// </summary>
    internal bool EvaluateSequencePredicates(object node, IReadOnlyList<XQueryExpression> predicates, NodeTest nodeTest, object? descendantAncestor)
    {
        if (node is not XdmNode target || _nodeStore == null)
        {
            // No node store to build a sequence from: fall back to single-position AND
            // semantics (each predicate evaluated against the node's own position).
            var (pos, sz) = ComputeNodePosition(node, nodeTest, descendantAncestor);
            foreach (var pred in predicates)
            {
                if (!EvaluatePatternPredicate(node, pred, pos, sz, node))
                    return false;
            }
            return true;
        }

        var current = BuildCandidateSequence(target, nodeTest, descendantAncestor);

        foreach (var predicate in predicates)
        {
            int size = current.Count;
            var next = new List<XdmNode>(size);
            for (int i = 0; i < size; i++)
            {
                // current() in a pattern predicate refers to the node being matched (the
                // original target), not the candidate under test — mirror the single-predicate path.
                if (EvaluatePatternPredicate(current[i], predicate, i + 1, size, target))
                    next.Add(current[i]);
            }
            current = next;

            // Once the target drops out of the survivors it can never return.
            if (!ContainsNodeId(current, target.Id))
                return false;
        }

        return ContainsNodeId(current, target.Id);
    }


    /// <summary>
    /// Tests if a node matches a given node test.
    /// </summary>
    private static bool MatchesNodeTest(XdmNode node, NodeTest nodeTest)
    {
        return (nodeTest, node) switch
        {
            (NameTest nt, XdmElement elem) => nt.Matches(XdmNodeKind.Element, elem.Namespace, elem.LocalName),
            (NameTest nt, XdmAttribute attr) => nt.Matches(XdmNodeKind.Attribute, attr.Namespace, attr.LocalName),
            (KindTest { Kind: XdmNodeKind.Element }, XdmElement) => true,
            (KindTest { Kind: XdmNodeKind.Text }, XdmText) => true,
            (KindTest { Kind: XdmNodeKind.Comment }, XdmComment) => true,
            (KindTest { Kind: XdmNodeKind.ProcessingInstruction }, XdmProcessingInstruction) => true,
            (KindTest { Kind: XdmNodeKind.Document }, XdmDocument) => true,
            (KindTest { Kind: XdmNodeKind.Attribute }, XdmAttribute) => true,
            (KindTest { Kind: XdmNodeKind.None }, _) => node is not XdmDocument, // node() matches all except document
            _ => false
        };
    }


    internal bool EvaluatePatternPredicate(object stepNode, XQueryExpression predicate, int position, int size, object? matchedNode)
    {
        // Push the step node as context item for predicate evaluation (for "." references)
        // Position and size are used for position() and last() functions
        PushContextItem(stepNode, position, size);

        // Per XSLT spec section 5.5.4: "The special rule for current() in patterns is that
        // current() refers to the node that is being matched by the pattern."
        // For multi-step patterns, this is the original node being matched (not the ancestor being checked).
        // If matchedNode is null (shouldn't happen), fall back to stepNode.
        PushCurrentItem(matchedNode ?? stepNode);
        try
        {
            var result = EvaluateAsync(predicate).AsTask().GetAwaiter().GetResult();

            // XPath numeric predicates: if the predicate evaluates to a number,
            // the predicate is true if and only if that number equals the context position
            if (result is int i)
                return i == position;
            if (result is long l)
                return l == position;
            if (result is double d)
                return Math.Abs(d - position) < 0.0001;
            if (result is decimal m)
                return m == position;

            return EffectiveBooleanValue(result);
        }
#pragma warning disable CA1031 // Predicate evaluation failure should not crash pattern matching
        catch (Exception ex) when (
            ex is not PhoenixmlDb.XQuery.Execution.XQueryRuntimeException { ErrorCode: "XPST0017" }
            && (ex is not XsltException xsltEx || xsltEx.ErrorCode != "XTDE0640"))
        {
            // XTDE0640: If predicate evaluation failed because a variable is not yet bound
            // and that variable is currently being evaluated (circular reference), propagate as XTDE0640
            if (ex is PhoenixmlDb.XQuery.Execution.XQueryRuntimeException { ErrorCode: "XPST0008" }
                && _globalsBeingEvaluated.Count > 0)
            {
                throw Error($"XTDE0640: Circular reference detected while evaluating global variable/parameter (predicate references a global that is currently being initialized)");
            }
            return false;
        }
#pragma warning restore CA1031
        finally
        {
            PopCurrentItem();
            PopContextItem();
        }
    }


    /// <summary>
    /// Evaluates a key() pattern during pattern matching.
    /// Checks if the given node is in the result set of key(keyName, valueExpr).
    /// </summary>
    internal bool EvaluateKeyPattern(string keyName, PhoenixmlDb.XQuery.Ast.XQueryExpression valueExpr, object node)
    {
        try
        {
            // Evaluate the value expression
            var value = EvaluateAsync(valueExpr).AsTask().GetAwaiter().GetResult();

            // Get the key function and invoke it
            var keyFunc = new XsltKeyFunction(this);
            var args = new List<object?> { keyName, value };
            var result = keyFunc.InvokeAsync(args, null!).AsTask().GetAwaiter().GetResult();

            // Check if node is in the result set
            if (result is object[] arr)
            {
                foreach (var item in arr)
                {
                    if (item is XdmNode resultNode && node is XdmNode targetNode &&
                        resultNode.Id == targetNode.Id)
                        return true;
                }
            }
            else if (result is IEnumerable<object> seq && result is not string)
            {
                foreach (var item in seq)
                {
                    if (item is XdmNode resultNode && node is XdmNode targetNode &&
                        resultNode.Id == targetNode.Id)
                        return true;
                }
            }
            return false;
        }
#pragma warning disable CA1031
        catch (XsltException ex) when (ex.ErrorCode == "XTDE0640")
        {
            throw; // Non-recoverable: circular key reference must propagate
        }
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }


    internal bool EvaluateIdPattern(PhoenixmlDb.XQuery.Ast.XQueryExpression valueExpr, object node)
    {
        try
        {
            // Evaluate the value expression
            var value = EvaluateAsync(valueExpr).AsTask().GetAwaiter().GetResult();

            // Find the document for the node being matched — id() searches within that document
            XdmDocument? doc = null;
            if (node is XdmDocument d)
                doc = d;
            else if (node is XdmNode n)
                doc = FindDocumentForNode(n);
            if (doc == null || _nodeStore == null)
                return false;

            var results = XsltIdFunction.FindElementsById(value, doc, _nodeStore);

            // Check if node is in the result set
            foreach (var item in results)
            {
                if (item is XdmNode resultNode && node is XdmNode targetNode &&
                    resultNode.Id == targetNode.Id)
                    return true;
            }
            return false;
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
    }


    public override async ValueTask AnalyzeStringAsync(XsltAnalyzeString instruction)
    {
        var selectResult = await EvaluateAsync(instruction.Select).ConfigureAwait(false);

        // XPTY0004: select must evaluate to a single xs:string (or promotable: xs:anyURI, xs:untypedAtomic)
        // After atomization, numeric/boolean/date types are not promotable to xs:string.
        // Nodes are accepted (they atomize to xs:untypedAtomic which is promotable).
        if (selectResult is IList<object?> seq)
        {
            if (seq.Count > 1)
                throw Error("XPTY0004: The select expression of xsl:analyze-string must return a single xs:string value, got a sequence of " + seq.Count + " items");
            selectResult = seq.Count == 1 ? seq[0] : null;
        }
        if (selectResult is int or long or double or float or decimal or bool
            or DateTimeOffset or DateOnly or TimeOnly or TimeSpan or byte or short)
            throw Error("XPTY0004: The select expression of xsl:analyze-string must return a single xs:string value, got " + selectResult!.GetType().Name);

        var input = StringValueOf(selectResult);
        var pattern = await EvaluateAvtAsync(instruction.Regex).ConfigureAwait(false);
        var flags = instruction.Flags != null
            ? await EvaluateAvtAsync(instruction.Flags).ConfigureAwait(false)
            : "";

        // XTDE1145: Validate flags before processing
        foreach (var ch in flags)
        {
            if (ch is not ('s' or 'm' or 'i' or 'x' or 'q'))
                throw Error($"XTDE1145: Invalid flag '{ch}' in xsl:analyze-string flags attribute. Valid flags are: s, m, i, x, q");
        }

        var regexOptions = System.Text.RegularExpressions.RegexOptions.None;
        if (flags.Contains('i', StringComparison.Ordinal))
            regexOptions |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        if (flags.Contains('m', StringComparison.Ordinal))
            regexOptions |= System.Text.RegularExpressions.RegexOptions.Multiline;
        if (flags.Contains('s', StringComparison.Ordinal))
            regexOptions |= System.Text.RegularExpressions.RegexOptions.Singleline;
        if (flags.Contains('x', StringComparison.Ordinal))
            regexOptions |= System.Text.RegularExpressions.RegexOptions.IgnorePatternWhitespace;

        // 'q' flag: treat pattern as literal string (no regex metacharacters)
        if (flags.Contains('q', StringComparison.Ordinal))
            pattern = System.Text.RegularExpressions.Regex.Escape(pattern);
        else
        {
            PhoenixmlDb.XQuery.Functions.XQueryRegexHelper.ValidateXsdRegex(pattern);
            pattern = PhoenixmlDb.XQuery.Functions.XQueryRegexHelper.ConvertXPathPatternToNet(pattern);
        }
        pattern = PhoenixmlDb.XQuery.Functions.XQueryRegexHelper.ConvertXsdEscapesToNet(pattern);
        if (!flags.Contains('m', StringComparison.Ordinal))
            pattern = PhoenixmlDb.XQuery.Functions.XQueryRegexHelper.FixDollarAnchor(pattern);
        // Pass the 's' flag through: this helper rewrites '.' into an explicit character
        // class, and its non-single-line form is [^\r\n]. Rewriting unconditionally bakes
        // "dot does not match a newline" into the PATTERN, where RegexOptions.Singleline —
        // set above — can no longer affect it. The five XQuery callers pass the flag; these
        // two XSLT ones did not (analyze-string-008/034/065).
        pattern = PhoenixmlDb.XQuery.Functions.XQueryRegexHelper.FixDotForSurrogatePairs(pattern,
            flags.Contains('s', StringComparison.Ordinal));

        System.Text.RegularExpressions.Regex regex;
        try
        {
            regex = new System.Text.RegularExpressions.Regex(pattern, regexOptions);
        }
        catch (ArgumentException ex)
        {
            // The .NET parser's rejection (e.g. regex="[A-Z", an unterminated class) escaped
            // with no error code (error-1140a).
            throw Error($"XTDE1140: The regex attribute of xsl:analyze-string is not a valid regular expression: {ex.Message}");
        }

        // First pass: collect all matches to compute total substring count for position()/last()
        var matches = new List<System.Text.RegularExpressions.Match>();
        for (var m = regex.Match(input); m.Success; m = m.NextMatch())
            matches.Add(m);

        // Count total substrings: non-matching gaps + matching + possible trailing non-matching
        int totalSubstrings = 0;
        {
            int idx = 0;
            foreach (var m in matches)
            {
                if (m.Index > idx) totalSubstrings++; // non-matching before
                totalSubstrings++; // matching
                idx = m.Index + m.Length;
            }
            if (idx < input.Length) totalSubstrings++; // trailing non-matching
        }

        var lastIndex = 0;
        var position = 0;

        foreach (var match in matches)
        {
            // Non-matching substring before this match
            if (match.Index > lastIndex && instruction.NonMatchingSubstring != null)
            {
                position++;
                var nonMatching = input[lastIndex..match.Index];
                PushContextItem(nonMatching, position, totalSubstrings);
                PushCurrentItem(nonMatching);
                try
                {
                    await instruction.NonMatchingSubstring.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    PopCurrentItem();
                    PopContextItem();
                }
            }
            else if (match.Index > lastIndex)
            {
                position++; // count the non-matching substring even if no handler
            }

            // Matching substring
            position++;
            if (instruction.MatchingSubstring != null)
            {
                PushContextItem(match.Value, position, totalSubstrings);
                PushCurrentItem(match.Value);
                PushScope();
                // Set regex-group() function context — store the Match for the regex-group() function
                SetVariable(new QName(NamespaceId.None, "regex-groups"), match);
                for (var i = 0; i < match.Groups.Count; i++)
                {
                    SetVariable(new QName(NamespaceId.None, $"regex-group-{i}"), match.Groups[i].Value);
                }

                try
                {
                    await instruction.MatchingSubstring.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    PopScope();
                    PopCurrentItem();
                    PopContextItem();
                }
            }

            lastIndex = match.Index + match.Length;
        }

        // Trailing non-matching substring
        if (lastIndex < input.Length && instruction.NonMatchingSubstring != null)
        {
            position++;
            var nonMatching = input[lastIndex..];
            PushContextItem(nonMatching, position, totalSubstrings);
            PushCurrentItem(nonMatching);
            try
            {
                await instruction.NonMatchingSubstring.ExecuteAsync(this).ConfigureAwait(false);
            }
            finally
            {
                PopCurrentItem();
                PopContextItem();
            }
        }
    }


    public override async ValueTask EvaluateInstructionAsync(XsltEvaluate instruction)
    {
        // xsl:evaluate: dynamically evaluate an XPath expression string
        // The xpath attribute is an XPath expression that produces the string to evaluate
        var xpathResult = await EvaluateAsync(instruction.Xpath).ConfigureAwait(false);
        var xpathStr = StringValueOf(xpathResult);

        // Parse the XPath expression
        XQueryExpression parsedExpr;
        try
        {
            parsedExpr = new PhoenixmlDb.XQuery.Parser.XQueryParserFacade { AllowNamespaceAxis = true }.Parse(xpathStr);
        }
        catch (Exception ex)
        {
            // A static error while analysing the xpath string is XTDE3160 (XSLT 3.0 §19.4);
            // XTDE3150 is a different rule (error-3160a).
            throw new XsltException($"XTDE3160: Dynamic XPath expression is not valid: {ex.Message}", instruction.Location);
        }

        // Determine namespace bindings for the dynamic expression.
        // Per XSLT 3.0 §19.4: namespace-context provides an element whose in-scope namespaces
        // are used for resolving prefixes. Otherwise, the in-scope namespaces of the
        // xsl:evaluate element are used.
        Dictionary<string, string> nsBindings;
        string? xpathDefaultNs = instruction.XpathDefaultNamespace;
        if (instruction.NamespaceContext != null)
        {
            var nsCtxResult = await EvaluateAsync(instruction.NamespaceContext).ConfigureAwait(false);
            // XTTE3170: namespace-context must be a single node
            if (nsCtxResult is object?[] nsArr)
            {
                if (nsArr.Length != 1)
                    throw new XsltException("XTTE3170: The namespace-context attribute of xsl:evaluate must evaluate to a single node", instruction.Location);
                nsCtxResult = nsArr[0];
            }
            if (nsCtxResult is not (Xdm.Nodes.XdmElement or Xdm.Nodes.XdmNode or System.Xml.XmlNode or System.Xml.Linq.XNode))
                throw new XsltException("XTTE3170: The namespace-context attribute of xsl:evaluate must evaluate to a single node", instruction.Location);
            nsBindings = ExtractNamespaceBindings(nsCtxResult);
            // Default namespace from the namespace-context element overrides xpath-default-namespace
            if (nsBindings.TryGetValue("", out var defaultNs) && defaultNs.Length > 0)
                xpathDefaultNs = defaultNs;
            else
                xpathDefaultNs = null; // No default namespace in context element
        }
        else
        {
            nsBindings = instruction.DefaultNamespaceBindings;
        }

        // Resolve namespace prefixes in the parsed expression using the determined bindings
        ResolveExpressionNamespacesRuntime(parsedExpr, nsBindings, xpathDefaultNs);

        // Evaluate context-item if specified
        object? contextItem = null;
        if (instruction.ContextItem != null)
        {
            contextItem = await EvaluateAsync(instruction.ContextItem).ConfigureAwait(false);
            // The context item is one item or none. A longer sequence was pushed whole, and the
            // dynamic expression's first axis step then failed on "an item of type Object[]"
            // instead of reporting the type error (error-3210a).
            if (contextItem is object?[] contextItems)
            {
                if (contextItems.Length > 1)
                    throw new XsltException("XTTE3210: The context-item attribute of xsl:evaluate must evaluate to a single item", instruction.Location);
                contextItem = contextItems.Length == 1 ? contextItems[0] : null;
            }
        }

        // Evaluate base-uri AVT if specified
        string? baseUri = null;
        if (instruction.BaseUri != null)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var part in instruction.BaseUri.Parts)
                sb.Append(await part.EvaluateAsync(this).ConfigureAwait(false));
            baseUri = sb.ToString();
        }

        // Bind with-param values as variables in the evaluation scope
        PushScope();
        try
        {
            // Handle xsl:with-param child elements first (lower precedence)
            foreach (var param in instruction.WithParams)
            {
                var value = await EvaluateAsync(param.Select!).ConfigureAwait(false);
                SetVariable(param.Name, value);
            }
            // Handle with-params attribute (map expression) — higher precedence, overwrites children
            if (instruction.WithParamsExpr != null)
            {
                var mapResult = await EvaluateAsync(instruction.WithParamsExpr).ConfigureAwait(false);
                if (mapResult is IDictionary<object, object?> paramMap)
                {
                    foreach (var (key, val) in paramMap)
                    {
                        if (key is PhoenixmlDb.Core.QName qn)
                            SetVariable(qn, val);
                        else
                            throw new XsltException("XTTE3165: The keys in the with-params map must be of type xs:QName", instruction.Location);
                    }
                }
            }

            // Push context item for the dynamic expression.
            // Per XSLT 3.0 §19.4: if context-item is not specified OR evaluates to
            // empty sequence, context is absent (XPDY0002 if accessed).
            if (contextItem != null)
                PushContextItem(contextItem, 1, 1);
            else
                PushContextItem(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus, 0, 0);

            // Set default collation for the evaluation: explicit attribute > inherited
            var effectiveCollation = instruction.EvaluateDefaultCollation ?? instruction.DefaultCollation;
            if (effectiveCollation != null)
                _defaultCollationStack.Push(effectiveCollation);

            // Push base-uri for static-base-uri() and base-uri() within the evaluation
            if (baseUri != null)
                _staticBaseUriStack.Push(baseUri);

            // Override PrefixNamespaceBindings for the evaluation so functions like
            // system-property() and QName resolution use the correct namespace context
            var savedNsBindings = _evaluateNamespaceBindings;
            _evaluateNamespaceBindings = nsBindings;

            _insideXslEvaluateDepth++;
            try
            {
                var result = await EvaluateAsync(parsedExpr).ConfigureAwait(false);

                // Apply type coercion via 'as' attribute (XSLT 3.0 §19.4)
                // Like xsl:variable 'as': atomize nodes, coerce types, check cardinality
                if (instruction.As != null)
                    result = CoerceEvaluateResult(result, instruction.As, instruction.Location);

                if (result != null)
                {
                    // If sequence accumulator is active (e.g., inside variable with as="function(*)"),
                    // add the result directly to avoid XTDE0450 for function/map items
                    if (_sequenceAccumulator != null)
                    {
                        if (result is object?[] arr)
                            foreach (var item in arr) AppendToSeqAccumulator(item);
                        else
                            AppendToSeqAccumulator(result);
                    }
                    else
                    {
                        // Serialize the result: nodes are deep-copied (like xsl:copy-of),
                        // atomic values are output as text
                        OutputEvaluateResult(result);
                    }
                }
            }
            finally
            {
                _insideXslEvaluateDepth--;
                _evaluateNamespaceBindings = savedNsBindings;
                if (baseUri != null)
                    _staticBaseUriStack.Pop();
                if (effectiveCollation != null)
                    _defaultCollationStack.Pop();
                PopContextItem();
            }
        }
        finally
        {
            PopScope();
        }
    }


    private async ValueTask<string> EvaluateAvtAsync(XsltAttributeValueTemplate avt)
    {
        // Fast paths for the common case: most AVTs are either a single literal
        // (e.g. <foo bar="baz"/>) or a single expression (<foo bar="{$x}"/>).
        // Skipping the StringBuilder removes allocation pressure on large workloads:
        // ProjectToReport hits 24K AVT evals at ~18-20µs each, where most are 1-part.
        var parts = avt.Parts;
        if (parts.Count == 0) return string.Empty;
        if (parts.Count == 1)
            return await parts[0].EvaluateAsync(this).ConfigureAwait(false);

        var sb = new StringBuilder();
        foreach (var part in parts)
            sb.Append(await part.EvaluateAsync(this).ConfigureAwait(false));
        return sb.ToString();
    }


    /// <summary>
    /// Evaluates a sequence constructor and returns its result value.
    /// Used for sort key evaluation where the sort key is defined by content.
    /// Captures both typed values (via xsl:sequence / _sequenceAccumulator) and text output.
    /// </summary>
    internal async ValueTask<object?> EvaluateSequenceConstructorAsync(XsltSequenceConstructor content)
    {
        var savedAccumulator = _sequenceAccumulator;
        _sequenceAccumulator = new List<object?>();
        var savedLen = _output.Length;

        await content.ExecuteAsync(this).ConfigureAwait(false);

        var textOutput = _output.ToString(savedLen, _output.Length - savedLen);
        _output.Length = savedLen;
        var accumulatedItems = _sequenceAccumulator;
        _sequenceAccumulator = savedAccumulator;

        // Prefer typed sequence items over text output
        if (accumulatedItems.Count == 1)
            return accumulatedItems[0];
        if (accumulatedItems.Count > 1)
            return accumulatedItems.ToArray();

        return textOutput;
    }


    /// <summary>
    /// Evaluates a with-param value, handling both select and content body.
    /// </summary>
    private async ValueTask<object?> EvaluateWithParamAsync(XsltWithParam param)
    {
        // A value handed in directly needs no evaluation, and must not be converted on the way
        // through — see XsltWithParam.RuntimeValue. It may however have arrived from another
        // engine wrapped for cross-store transport, in which case it is re-parsed into THIS
        // store first: template-params and tunnel-params reach their declared-type check here,
        // and an unwrapped CrossStoreNodeRef fails it as
        // "requires type Element but got CrossStoreNodeRef".
        if (param.HasRuntimeValue)
            return XsltTransformEngine.UnwrapCrossStoreValue(param.RuntimeValue, _nodeStore);
        if (param.Select != null)
        {
            return await EvaluateAsync(param.Select).ConfigureAwait(false);
        }
        if (param.Content != null)
        {
            // If the param has a sequence type (as="type *" or as="type +"),
            // use the sequence accumulator to collect individual items
            var isSequenceType = param.As != null &&
                (param.As.Occurrence == Occurrence.ZeroOrMore || param.As.Occurrence == Occurrence.OneOrMore);

            if (isSequenceType)
            {
                var savedAccumulator = _sequenceAccumulator;
                _sequenceAccumulator = new List<object?>();

                var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
                // Shared typed-body context (see EnterTypedBody). DocBook xslTNG reaches this
                // seam via <xsl:with-param name="extra-attributes" as="attribute()*"> bodies.
                var typedBody = EnterTypedBody(param.As);
                _temporaryOutputDepth++;
                try
                { await param.Content.ExecuteAsync(this).ConfigureAwait(false); }
                finally
                {
                    _temporaryOutputDepth--;
                    ExitTypedBody(typedBody);
                }
                var textContent = savedScope.GetWritten();
                savedScope.Dispose();

                var sequenceItems = new List<object?>();
                foreach (var item in _sequenceAccumulator)
                    if (item != null)
                        sequenceItems.Add(item);

                if (!string.IsNullOrEmpty(textContent) && _nodeStore != null && textContent.Contains('<', StringComparison.Ordinal))
                {
                    try
                    {
                        // Stream-parse rather than allocating a full XmlDocument.
                        var settings = new System.Xml.XmlReaderSettings
                        {
                            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                            IgnoreWhitespace = false,
                            IgnoreComments = false,
                            IgnoreProcessingInstructions = false,
                        };
                        using var stringReader = new System.IO.StringReader($"<_seq_root_>{textContent}</_seq_root_>");
                        using var reader = System.Xml.XmlReader.Create(stringReader, settings);
                        var parsedChildren = new List<object?>();
                        ReadAsBodyChunkChildren(reader, parsedChildren);
                        foreach (var child in parsedChildren)
                        {
                            if (child is XdmNode cn)
                                cn.Parent = null;
                            sequenceItems.Add(child);
                        }
                    }
                    catch (System.Xml.XmlException)
                    {
                        sequenceItems.Add(new ResultTreeFragment(textContent));
                    }
                }
                else if (!string.IsNullOrEmpty(textContent))
                {
                    sequenceItems.Add(new Xdm.XsUntypedAtomic(StripXmlMarkup(textContent)));
                }

                _sequenceAccumulator = savedAccumulator;
                // Apply as="ATOMIC*" coercion per item (atomize + cast). The sequence branch
                // previously returned the raw untypedAtomic/string items, so e.g.
                // as="xs:anyURI+" / as="xs:float*" bodies failed `instance of xs:TYPE`
                // (attr/as-1001, as-1101).
                if (param.As != null && IsCastableAtomicType(param.As.ItemType))
                {
                    for (var i = 0; i < sequenceItems.Count; i++)
                        if (sequenceItems[i] != null)
                            sequenceItems[i] = CoerceToType(sequenceItems[i], param.As);
                }
                return sequenceItems.Count > 0 ? sequenceItems.ToArray() : Array.Empty<object?>();
            }

            var savedScope2 = new XsltTransformEngine.ScopedOutputBuffer(_output);
            var savedLogicalStart2 = _outputLogicalStart;
            _outputLogicalStart = _output.Length;
            // Save/restore sequence accumulator to prevent xsl:map/xsl:array
            // inside with-param content from leaking into the outer accumulator
            var savedAccum2 = _sequenceAccumulator;
            _sequenceAccumulator = new List<object?>();
            _temporaryOutputDepth++;
            try
            { await param.Content.ExecuteAsync(this).ConfigureAwait(false); }
            finally { _temporaryOutputDepth--; }
            var contentAccum = _sequenceAccumulator;
            _sequenceAccumulator = savedAccum2;
            var result = savedScope2.GetWritten();
            savedScope2.Dispose();
            _outputLogicalStart = savedLogicalStart2;
            // If the content produced typed items (maps, arrays, functions, nodes)
            // via sequence accumulator, use those directly instead of serialized output
            if (contentAccum.Count > 0 && string.IsNullOrEmpty(result))
            {
                object? paramValue = contentAccum.Count == 1 ? contentAccum[0] : contentAccum.ToArray();
                if (param.As != null)
                    paramValue = CoerceToType(paramValue, param.As);
                return paramValue;
            }
            // Wrap as RTF if content contains XML markup so copy-of preserves it
            object? paramValue2 = result.Contains('<', StringComparison.Ordinal) ? new ResultTreeFragment(result) : (object)new Xdm.XsUntypedAtomic(StripXmlMarkup(result));
            // Apply as="" type coercion on the with-param value
            if (param.As != null)
                paramValue2 = CoerceToType(paramValue2, param.As);
            return paramValue2;
        }
        // If 'as' specifies an optional/empty-allowed type, return empty sequence (null);
        // otherwise default to empty string (matches XSLT 1.0/2.0 behavior for untyped params).
        if (param.As != null && param.As.Occurrence is Occurrence.ZeroOrOne or Occurrence.ZeroOrMore)
            return null;
        return "";
    }


    /// <summary>
    /// Applies XSLT 3.0 §5.4.1 function conversion rules:
    /// 1. Atomize node values  2. Cast untypedAtomic to target type  3. Numeric promotion
    /// </summary>
    private static object? ApplyFunctionConversionRules(object? value, XdmSequenceType targetType)
    {
        if (value == null) return null;

        // For sequences, apply rules to each item
        if (value is object?[] arr)
        {
            var result = new object?[arr.Length];
            for (int i = 0; i < arr.Length; i++)
                result[i] = ApplyFunctionConversionRulesSingle(arr[i], targetType);
            return result;
        }

        return ApplyFunctionConversionRulesSingle(value, targetType);
    }


    private static object? ApplyFunctionConversionRulesSingle(object? value, XdmSequenceType targetType)
    {
        if (value == null) return null;

        // Only atomize nodes when the target type is an atomic type (not node/item/function types)
        if (!IsNodeType(targetType.ItemType) && targetType.ItemType is not ItemType.Item
            and not ItemType.Map and not ItemType.Array and not ItemType.Function)
        {
            // Step 1: Atomize nodes → produces xs:untypedAtomic (for non-schema-aware processors)
            if (value is Xdm.Nodes.XdmNode or System.Xml.XmlNode or System.Xml.Linq.XNode)
                value = PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AtomizeTyped(value);

            if (value == null) return null;

            // Step 2: Cast xs:untypedAtomic to target type (ONLY untypedAtomic gets implicit casting)
            if (value is Xdm.XsUntypedAtomic)
                return CoerceToType(value, targetType);

            // Step 3: Numeric promotion (xs:integer → xs:double, xs:float → xs:double, etc.)
            if (targetType.ItemType == ItemType.Double && value is int or long or float or decimal)
                return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            if (targetType.ItemType == ItemType.Float && value is int or long or decimal)
                return Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
            if (targetType.ItemType == ItemType.Decimal && value is int or long)
                return Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);

            // No implicit casting for other type mismatches (e.g., xs:string → xs:integer)
            return value;
        }

        return value;
    }


    /// <summary>
    /// Checks whether a target type is a strict atomic type where type mismatches
    /// should always be reported as errors (not silently accepted).
    /// </summary>
    /// <summary>
    /// Checks if an element matches a declared element name, handling both simple names
    /// and EQName syntax (Q{uri}local or uri}local after Q{ is stripped).
    /// </summary>
    /// <summary>
    /// Per XSLT 3.0 §2.5.5, an element(QName) test matches when both the element's local name
    /// and (when constrained) its namespace URI agree with the declared name. The earlier
    /// implementation only checked local name, so e.g. element(Q{xhtml}html) accepted an
    /// element named "html" in any namespace — including the null namespace produced by the
    /// xsl:copy copy-namespaces="no" bug fixed in 1.3.5.
    /// </summary>
    private bool MatchesElementName(XdmElement el, string? declaredLocalName, string? declaredNamespace)
    {
        if (declaredLocalName != null)
        {
            // EQName: strip Q{uri}local form to bare local part for comparison
            var localPart = declaredLocalName;
            var closeBrace = declaredLocalName.LastIndexOf('}');
            if (closeBrace >= 0 && closeBrace < declaredLocalName.Length - 1)
                localPart = declaredLocalName[(closeBrace + 1)..];
            if (el.LocalName != localPart)
                return false;
        }
        if (declaredNamespace != null)
        {
            var elNs = _nodeStore?.GetNamespaceUri(el.Namespace) ?? "";
            if (elNs != declaredNamespace)
                return false;
        }
        return true;
    }


    /// <summary>
    /// If the text at <paramref name="pos"/> is the local name <c>script</c> or <c>style</c>
    /// (case-insensitive) followed by a tag-name boundary, returns the canonical lowercase name;
    /// otherwise <c>null</c>.
    /// </summary>
    private static string? MatchRawTextElementName(string s, int pos)
    {
        foreach (var name in new[] { "script", "style" })
        {
            if (pos + name.Length <= s.Length
                && s.AsSpan(pos, name.Length).Equals(name.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                var after = pos + name.Length;
                var boundary = after >= s.Length || s[after] is ' ' or '\t' or '\n' or '\r' or '>' or '/';
                if (boundary)
                    return name;
            }
        }
        return null;
    }


    /// <summary>
    /// Calls an XSLT user-defined function (xsl:function) and returns its result.
    /// </summary>
    internal async ValueTask<object?> CallXsltFunctionAsync(XsltFunction func, IReadOnlyList<object?> arguments)
    {
        // Trace: function call
        if (_options?.TraceListener != null)
            _options.TraceListener(_templateDepth, "call-function", $"{func.Name.LocalName}#{arguments.Count}");

        // Memoization: cache="yes" asks for it, and new-each-time="no" declares the function
        // deterministic — two calls with identical arguments must return IDENTICAL results, so
        // a node it constructs is the same node each time (XSLT 3.0 §10.3.2). Without this,
        // `f(1) | f(1)` held two nodes where the spec requires one (W3C function-1025/1026).
        FunctionMemoKey? cacheKey = null;
        if (func.Cache || func.NewEachTime == "no")
        {
            cacheKey = new FunctionMemoKey(func, arguments);
            if (_functionCache.TryGetValue(cacheKey, out var cachedResult))
                return cachedResult;
        }

        _recursionDepth++;
        if (_recursionDepth > MaxRecursionDepth)
            throw Error($"Maximum recursion depth ({MaxRecursionDepth}) exceeded in function '{func.Name.LocalName}'");
        // Probe the physical stack: recursive stylesheet functions overflow the native
        // stack (uncatchable SIGABRT) well before _recursionDepth hits the limit above,
        // because each call burns ~15 async frames. CheckResourceLimits converts that
        // into a catchable error.
        CheckResourceLimits();

        _currentXsltFunctionStack.Push(func);
        // When this function overrides a package component, bind xsl:original to the overridden
        // function for the duration of the body. Registering a per-call adapter (rather than
        // relying on the runtime function stack) means a function ITEM materialized from
        // xsl:original#N or a partial application xsl:original(?, …) captures the correct
        // original even when it is later invoked outside this frame (override-f-017/-018).
        XQueryFunction? savedXslOriginalAdapter = null;
        var swappedXslOriginalAdapter = false;
        if (func.OriginalFunction is { } overriddenFunc)
        {
            savedXslOriginalAdapter = _functionLibrary.Resolve(XslOriginalFunctionName, 0);
            _functionLibrary.Register(new XsltBoundOriginalFunctionAdapter(this, overriddenFunc));
            swappedXslOriginalAdapter = true;
        }
        // static-base-uri() inside the body resolves against the module the function is
        // WRITTEN in, not the principal stylesheet (XPath 3.1 §16.2.4 — it comes from the
        // expression's static context). Templates already push this; functions did not, so a
        // function in an imported module reported the importing stylesheet's URI.
        var pushedFunctionBaseUri = false;
        if (func.BaseUri != null)
        {
            _staticBaseUriStack.Push(XsltTransformEngine.UriString(func.BaseUri)!);
            pushedFunctionBaseUri = true;
        }
        PushScope();
        // Mark this scope as a tunnel barrier — per XSLT spec, tunnel parameters
        // are not propagated through stylesheet function calls
        _scopes.Peek().IsTunnelBarrier = true;
        // Per XSLT spec, context item is absent inside stylesheet functions (XPDY0002)
        PushContextItem(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus, 0, 0);
        // current() must also return absent focus in functions (XTDE1360)
        PushCurrentItem(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus);
        // Per XSLT 2.0 erratum E19: mode="#current" in functions reverts to unnamed mode
        var savedMode = _currentMode;
        _currentMode = null;
        try
        {
            // Shadow context-specific variables that shouldn't leak into function scope
            // Per XSLT spec, regex-group(), current-group(), etc. return empty inside functions
            SetVariable(new QName(NamespaceId.None, "regex-groups"), null);
            SetVariable(new QName(NamespaceId.None, "current-group"), null);
            SetVariable(new QName(NamespaceId.None, "current-grouping-key"), null);
            SetVariable(new QName(NamespaceId.None, "current-merge-group"), null);
            SetVariable(new QName(NamespaceId.None, "current-merge-key"), null);

            // Bind parameters with function conversion rules (XSLT 3.0 §5.4.1):
            // Atomize node values and coerce to target type when target is atomic
            // Then validate with XTTE0790 for strict atomic types
            for (var i = 0; i < func.Parameters.Count && i < arguments.Count; i++)
            {
                var param = func.Parameters[i];
                var value = arguments[i];
                if (param.As != null && IsStrictAtomicType(param.As.ItemType))
                {
                    // Apply function conversion rules: atomize nodes, coerce to target type
                    value = ApplyFunctionConversionRules(value, param.As);
                    ValidateValueMatchesType(value, param.As, "XTTE0790",
                        $"Value of parameter ${param.Name.LocalName} in function {func.Name.LocalName}");
                }
                else if (param.As != null && param.As.ItemType == ItemType.Function
                    && param.As.FunctionParameterTypes != null)
                {
                    // Validate arity and wrap in coercion wrappers (XSLT 3.0 §5.4.11)
                    ValidateFunctionTypeArgument(value, param.As, param.Name.LocalName, func.Name.LocalName);
                    value = WrapInCoercionWrapper(value, param.As);
                }
                SetVariable(param.Name, value);
            }

            // Use sequence accumulator to capture xsl:sequence return values as typed results
            var savedAccumulator = _sequenceAccumulator;
            _sequenceAccumulator = new List<object?>();

            // Save _lastResultWasAtomic — function body's internal text/sequence ops
            // must not leak into the caller's atomic spacing state
            // Also capture any text output (function body is temporary output state)
            var savedOutput = _output.Length;
            // Shared typed-body context (see EnterTypedBody): declares this body's base in
            // _outputLogicalStart, neutralizes the document depth, enables text-as-items for a
            // node()*/text()*/item()* return type, and isolates text/attribute-content depth and
            // the collected-attribute stack. Two of the five capture bugs found while unblocking
            // XSpec were this seam lacking a field the xsl:variable seam already had.
            // An xsl:function with no as= has the default return type item()* (XSLT 3.0 §10.3), so
            // the body still constructs a SEQUENCE and text it produces is a text node. Passing
            // func.As straight through left declaredType null for that case, text-as-items stayed
            // off, and the text fell into the output buffer to be handed back as a string — so
            // f:text($v) returned an atomic where the spec says a text node. An explicitly declared
            // as="item()*" already behaved correctly; only the omitted form did not, which is the
            // whole of the difference.
            //
            // Surfaced by W3C xslt30-test sf-boolean-119 / sf-not-119: boolean() and not() over
            // outermost(//PRICE) ! Q{f}text(string(.)) raise FORG0006 on a sequence of atomics
            // where a sequence of text nodes is required.
            var typedBody = EnterTypedBody(func.As ?? ItemStarSequenceType);
            // Namespace scopes are function-specific: the body's output is extracted as a
            // separate text fragment, so its LREs must emit self-contained declarations.
            var savedNsScopesFunc = new List<Dictionary<string, string>>(_outputNsScopes);
            _outputNsScopes.Clear();
            _collectedAttributesStack.Clear();
            _temporaryOutputDepth++;
            _functionBodyDepth++;
            var savedFunctionBodyAccumulator = _functionBodyAccumulator;
            _functionBodyAccumulator = _sequenceAccumulator;
            try
            { await func.Body.ExecuteAsync(this).ConfigureAwait(false); }
            finally
            {
                _functionBodyAccumulator = savedFunctionBodyAccumulator;
                _functionBodyDepth--;
                _temporaryOutputDepth--;
                // Restore namespace scopes
                _outputNsScopes.Clear();
                foreach (var scope in savedNsScopesFunc.AsEnumerable().Reverse())
                    _outputNsScopes.Push(scope);
                // Restore serialization state (document depth, element depth, text collection,
                // buffer base, collected attributes) — all owned by the shared typed-body scope.
                ExitTypedBody(typedBody);
            }
            var textOutput = _output.ToString(savedOutput, _output.Length - savedOutput);
            _output.Length = savedOutput;

            var accumulatedItems = _sequenceAccumulator;
            _sequenceAccumulator = savedAccumulator;

            // A declared node-ish return type wants TEXT NODES, so materialize the text the
            // body produced (collected above as TextNodeItem, or written as a bare string)
            // into XdmText. Without this the type check sees a TextNodeItem/String and raises
            // XTTE0780 for a body that legitimately returned text. Mirrors the wrapAsTextNode
            // step the typed xsl:variable and global-variable paths already perform, and is
            // deliberately limited to Text/Node the same way they are — an atomic return type
            // is handled by the CoerceToType calls below, and item() must keep whatever the
            // body actually produced.
            // Only when the accumulator is the SOLE channel. WriteTextItem deliberately writes
            // text to both the accumulator and _output inside a function body so the assembly
            // below can restore source order between text and elements; that assembly
            // recognises the duplicate by its TextNodeItem type, so materializing to XdmText
            // first made it emit the same text twice (xsl:text A + B came back as AB, A, B).
            if (_nodeStore != null && func.As != null
                && func.As.ItemType is ItemType.Text or ItemType.Node
                && string.IsNullOrEmpty(textOutput))
            {
                for (var i = 0; i < accumulatedItems.Count; i++)
                {
                    var value = accumulatedItems[i] switch
                    {
                        Xdm.TextNodeItem tniWrap => tniWrap.Value,
                        string strWrap => strWrap,
                        _ => null,
                    };
                    if (value != null)
                    {
                        accumulatedItems[i] = new Xdm.Nodes.XdmText
                        {
                            Id = _nodeStore.NextId(),
                            Document = DocumentId.None,
                            Parent = NodeId.None,
                            Value = value,
                        };
                    }
                }
            }

            // Determine function return value from accumulated items and/or text output
            object? funcResult;
            if (accumulatedItems.Count == 1 && string.IsNullOrEmpty(textOutput))
            {
                funcResult = accumulatedItems[0];
                // TextNodeItem from TVTs/xsl:text/xsl:value-of: coerce to atomic return type if needed.
                // Includes string/anyURI types — atomization of text nodes always produces a string.
                if (funcResult is Xdm.TextNodeItem tni && func.As != null && IsAtomicReturnType(func.As.ItemType))
                    funcResult = CoerceToType(tni.Value, func.As);
                // An ATOMIC declared return type atomizes the result first (function conversion
                // rules): a node's typed value is taken and then converted, so returning an
                // attribute or element where xs:decimal is declared is legal, not XTTE0780.
                // XSpec's x:xslt-version is exactly this — as="xs:decimal" over
                // `(ancestor-or-self::*[@xslt-version][1]/@xslt-version, 3.0)[1]`, which yields
                // an attribute node whenever the stylesheet declares the version.
                else if (funcResult is XdmNode && func.As != null && IsAtomicReturnType(func.As.ItemType))
                    funcResult = CoerceToType(funcResult, func.As);
                ValidateFunctionReturnType(funcResult, func);
            }
            else if (accumulatedItems.Count > 1 && string.IsNullOrEmpty(textOutput))
            {
                // Coerce TextNodeItems to atomic types if function has atomic return type
                if (func.As != null && IsAtomicReturnType(func.As.ItemType))
                {
                    for (var i = 0; i < accumulatedItems.Count; i++)
                    {
                        if (accumulatedItems[i] is Xdm.TextNodeItem tni2)
                            accumulatedItems[i] = CoerceToType(tni2.Value, func.As);
                    }
                }
                funcResult = accumulatedItems.ToArray();
            }
            else if (string.IsNullOrEmpty(textOutput) && accumulatedItems.Count == 0)
            {
                ValidateFunctionReturnType(null, func);
                funcResult = null;
            }
            else if (!string.IsNullOrEmpty(textOutput)
                && _nodeStore != null
                // Parse text output to XDM nodes when:
                //   (a) the text contains XML markup — a parented subtree to extract, or
                //   (b) the function's declared return is a node type — we need to produce
                //       text nodes, not concatenated strings. Found in Docbook chunk-cleanup
                //       f:chunk-title (as="node()*") whose apply-templates result was plain
                //       text; without (b) the function returned the string and the caller's
                //       `descendant-or-self::text()` axis step blew up with XPTY0020.
                && (textOutput.Contains('<', StringComparison.Ordinal)
                    || (func.As != null
                        && func.As.ItemType is ItemType.Element or ItemType.Node or ItemType.Document
                            or ItemType.Comment or ItemType.ProcessingInstruction or ItemType.Text))
                && (func.As == null
                    || func.As.ItemType is ItemType.Element or ItemType.Node or ItemType.Document
                        or ItemType.Comment or ItemType.ProcessingInstruction or ItemType.Text))
            {
                // Parse text output to XDM nodes. This handles:
                // - Functions with node return type (as="node()") — so instance-of checks work
                // - Functions with no as attribute (XSLT 3.0 §10.3: result used directly)
                funcResult = null;
                try
                {
                    // Stream-parse via XmlReader rather than allocating a full XmlDocument
                    // and re-converting to XDM. Children parented under the synthetic
                    // wrapper, then unparented before being returned as the function result.
                    var fnSettings = new System.Xml.XmlReaderSettings
                    {
                        DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                        IgnoreWhitespace = false,
                        IgnoreComments = false,
                        IgnoreProcessingInstructions = false,
                    };
                    using var fnStringReader = new System.IO.StringReader($"<_fn_root_>{textOutput}</_fn_root_>");
                    using var fnReader = System.Xml.XmlReader.Create(fnStringReader, fnSettings);
                    var fnChildren = new List<object?>();
                    ReadAsBodyChunkChildren(fnReader, fnChildren);
                    var parsedItems = new List<object?>();
                    foreach (var child in fnChildren)
                    {
                        if (child is XdmNode cn)
                            cn.Parent = null;
                        parsedItems.Add(child);
                    }
                    foreach (var item in accumulatedItems)
                    {
                        if (item is not Xdm.TextNodeItem)
                            parsedItems.Add(item);
                    }
                    funcResult = parsedItems.Count == 1 ? parsedItems[0] : parsedItems.Count > 1 ? parsedItems.ToArray() : null;
                }
                catch (System.Xml.XmlException)
                {
                    // Fall through to RTF wrapping
                }
                funcResult ??= textOutput.Contains('<', StringComparison.Ordinal) ? new ResultTreeFragment(textOutput) : StripXmlMarkup(textOutput);
            }
            else if (accumulatedItems.Count > 0 && !string.IsNullOrEmpty(textOutput))
            {
                // Combine accumulated items with text output — return the accumulated items
                // (text is typically from LREs inside the function)
                // Coerce TextNodeItems to atomic types if function has atomic return type
                if (func.As != null && IsAtomicReturnType(func.As.ItemType))
                {
                    for (var i = 0; i < accumulatedItems.Count; i++)
                    {
                        if (accumulatedItems[i] is Xdm.TextNodeItem tni)
                            accumulatedItems[i] = CoerceToType(tni.Value, func.As);
                    }
                }
                funcResult = accumulatedItems.Count == 1 ? accumulatedItems[0] : accumulatedItems.ToArray();
            }
            else if (string.IsNullOrEmpty(textOutput))
            {
                ValidateFunctionReturnType(null, func);
                funcResult = null;
            }
            else
            {
                // If text output contains XML markup, wrap as ResultTreeFragment so StringValueOf strips it
                funcResult = textOutput.Contains('<', StringComparison.Ordinal) ? new ResultTreeFragment(textOutput) : StripXmlMarkup(textOutput);
            }

            // If the function's return type is a typed function type, wrap returned
            // function items in coercion wrappers (XSLT 3.0 §5.4.11)
            if (funcResult != null && func.As != null
                && func.As.ItemType == ItemType.Function
                && func.As.FunctionParameterTypes != null)
            {
                funcResult = WrapInCoercionWrapper(funcResult, func.As);
            }

            // String → atomic coercion at function boundary (Saxon-compatible). When the
            // function body produces a string (typically via `xsl:number` or `xsl:value-of`)
            // and the declared return is a strict atomic type (xs:integer, xs:double, …),
            // attempt to cast it. Per XSLT 3.0 spec the body's text result is treated as
            // untypedAtomic, which casts to the target. Without this, the function-body
            // chain in Docbook fp:number (`as="xs:integer?"` whose body is
            // `<xsl:apply-templates select="$node" mode="mp:label-number"/>` and the
            // matching templates use `<xsl:number/>`) returns the formatted string and the
            // tightened validator now rejects it as XTTE0780.
            // A declared NODE return type has to materialise a temporary tree, the same way
            // assigning it to a variable already does. Only atomic types were coerced below, so
            // an RTF was handed back raw and a path step straight off the call failed:
            //
            //   <xsl:function name="f:wrap" as="document-node()">
            //     <xsl:variable name="w"><xsl:sequence select="$nodes"/></xsl:variable>
            //     <xsl:sequence select="$w"/>
            //   </xsl:function>
            //   f:wrap($e)/node()   ->  XPTY0020 "context item is not a node (got ResultTreeFragment)"
            //
            // Assigning to a variable first converted it, so this only bit a direct path step or
            // an RTF flowing into an as="item()*" variable. That untyped-variable body IS the
            // spec's implicit-document-node idiom, and XSpec's wrap:wrap-nodes is exactly it —
            // its wrapper document came back with no children, so every x:expect whose @test
            // navigates from the context item saw an empty sequence.
            if (func.As != null
                && func.As.ItemType is ItemType.Document or ItemType.Node or ItemType.Item)
            {
                if (funcResult is ResultTreeFragment rtfResult)
                {
                    funcResult = ParseResultTreeFragment(rtfResult) ?? funcResult;
                }
                else if (funcResult is object?[] rtfArr)
                {
                    for (var i = 0; i < rtfArr.Length; i++)
                        if (rtfArr[i] is ResultTreeFragment itemRtf)
                            rtfArr[i] = ParseResultTreeFragment(itemRtf) ?? rtfArr[i];
                }
            }

            if (func.As != null && IsCastableAtomicType(func.As.ItemType))
            {
                // Atomize + cast the return value to the declared atomic type. Covers a
                // string body (xsl:number/xsl:value-of), a temporary tree / node produced by
                // the sequence constructor (attr/as-0122: RTF → xs:dayTimeDuration, previously
                // XTTE0780), and numeric promotion (attr/as-0152: xs:integer → xs:double).
                if (funcResult is object?[] resultArr)
                {
                    for (var i = 0; i < resultArr.Length; i++)
                        if (resultArr[i] != null)
                            resultArr[i] = CoerceToType(resultArr[i], func.As);
                }
                else if (funcResult != null)
                {
                    funcResult = CoerceToType(funcResult, func.As);
                }
            }

            // XTTE0780 final gate: catch wrong-shape values that slipped past the per-branch
            // funcResult assembly. Several recent bugs (xsl:break atomizing an element to a
            // string, xsl:copy copy-namespaces="no" producing a wrong-namespace element,
            // function body whose text-only output would have returned a string for an
            // `as="node()"` declaration) were only detected downstream as XPTY0020 axis errors
            // because branches that synthesize funcResult from textOutput skipped validation.
            // Validating once here, after every branch converges, surfaces those mismatches
            // at the function boundary instead.
            ValidateFunctionReturnType(funcResult, func);

            if (cacheKey != null)
                _functionCache[cacheKey] = funcResult;
            return funcResult;
        }
        finally
        {
            _currentMode = savedMode;
            PopCurrentItem();
            PopContextItem();
            PopScope();
            if (pushedFunctionBaseUri)
                _staticBaseUriStack.Pop();
            if (swappedXslOriginalAdapter && savedXslOriginalAdapter != null)
                _functionLibrary.Register(savedXslOriginalAdapter);
            _currentXsltFunctionStack.Pop();
            _recursionDepth--;
        }
    }

}
