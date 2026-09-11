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

    private static (bool Resolved, object? Value) TryResolveFromWatchers(
        PhoenixmlDb.XQuery.Ast.XQueryExpression expr,
        IReadOnlyList<StreamWatcher> watchers)
    {
        // Direct match: the entire expression is a watched sub-expression
        foreach (var watcher in watchers)
        {
            if (ReferenceEquals(expr, watcher.SourceExpression))
                return (true, watcher.GetResult());
        }

        // Map constructor: resolve each entry's value from watchers and build the map
        if (expr is PhoenixmlDb.XQuery.Ast.MapConstructor mapCtor)
        {
            var hasWatcherEntries = false;
            foreach (var entry in mapCtor.Entries)
            {
                foreach (var watcher in watchers)
                {
                    if (ReferenceEquals(entry.Value, watcher.SourceExpression))
                    {
                        hasWatcherEntries = true;
                        break;
                    }
                }
                if (hasWatcherEntries) break;
            }

            if (hasWatcherEntries)
            {
                var map = new OrderedXdmMap(EqualityComparer<object>.Default);
                foreach (var entry in mapCtor.Entries)
                {
                    object? key = entry.Key switch
                    {
                        PhoenixmlDb.XQuery.Ast.StringLiteral sl => sl.Value,
                        PhoenixmlDb.XQuery.Ast.IntegerLiteral il => il.Value,
                        _ => entry.Key.ToString()
                    };

                    object? value = null;
                    foreach (var watcher in watchers)
                    {
                        if (ReferenceEquals(entry.Value, watcher.SourceExpression))
                        {
                            value = watcher.GetResult();
                            break;
                        }
                    }

                    if (key != null)
                        map[key] = value;
                }
                return (true, map);
            }
        }

        return (false, null);
    }


    /// <summary>
    /// Attempts to resolve a single expression to a scalar object using only
    /// watcher results and literal values — no streaming context needed.
    /// Returns (true, value) if the expression can be fully resolved this way.
    /// </summary>
    private (bool Resolved, object? Value) TryResolveExprFromWatchers(
        PhoenixmlDb.XQuery.Ast.XQueryExpression expr,
        IReadOnlyList<StreamWatcher> watchers)
    {
        // Direct watcher match
        var watcherMatch = TryResolveFromWatchers(expr, watchers);
        if (watcherMatch.Resolved) return watcherMatch;

        // Literal values are always resolvable
        switch (expr)
        {
            case PhoenixmlDb.XQuery.Ast.StringLiteral sl: return (true, sl.Value);
            case PhoenixmlDb.XQuery.Ast.IntegerLiteral il: return (true, il.Value);
            case PhoenixmlDb.XQuery.Ast.DoubleLiteral dl: return (true, dl.Value);
            case PhoenixmlDb.XQuery.Ast.DecimalLiteral decL: return (true, decL.Value);
            case PhoenixmlDb.XQuery.Ast.BooleanLiteral bl: return (true, bl.Value);
            case PhoenixmlDb.XQuery.Ast.EmptySequence: return (true, Array.Empty<object?>());
            // Variable references: look up via XSLT scope (globals + lexical scopes).
            // Needed so subsequence(copy-of(/path), $start, $length) resolves the
            // grounded integer parameters when the inner watcher provides the seq.
            case PhoenixmlDb.XQuery.Ast.VariableReference vref:
                try
                {
                    var val = GetVariable(vref.Name);
                    return (true, val);
                }
                catch (XsltException)
                {
                    return (false, null);
                }
                catch (KeyNotFoundException)
                {
                    return (false, null);
                }
        }

        // Named function reference: e.g., f:test#1 or fn:lower-case#1.
        // Resolve directly via the function library so higher-order calls like
        // filter(copy-of(/path), f:test#1) and fold-right(seq, 0, f:add#2)
        // can pass the all-args-resolvable gate without re-entering the eval
        // pipeline (which would crawl the synthetic empty document).
        if (expr is PhoenixmlDb.XQuery.Ast.NamedFunctionRef nfr)
        {
            var func = _functionLibrary.Resolve(nfr.Name, nfr.Arity);
            if (func == null) return (false, null);
            // Match NamedFunctionRefOperator: variadic functions get wrapped so they
            // expose the requested arity to the dynamic caller.
            object item = func.IsVariadic
                ? new PhoenixmlDb.XQuery.Execution.VariadicFunctionRefItem(func, nfr.Arity)
                : func;
            return (true, item);
        }

        // Inline function expression: e.g., function($x) { $x + 1 }.
        // Materialise as an InlineFunctionItem with a minimal captured context so
        // the closure remains invokable by the HOF that receives it.
        if (expr is PhoenixmlDb.XQuery.Ast.InlineFunctionExpression ife)
        {
            // The captured context is owned by the InlineFunctionItem closure for its
            // lifetime — it cannot be disposed eagerly here.
#pragma warning disable CA2000
            var capturedContext = new PhoenixmlDb.XQuery.Execution.QueryExecutionContext(
                container: default,
                functions: _functionLibrary,
                nodeProvider: _nodeStore,
                documentResolver: null,
                schemaProvider: _schemaProvider,
                namespaceResolver: null);
#pragma warning restore CA2000
            capturedContext.DefaultCollation = DefaultCollation;
            capturedContext.StaticBaseUri = StaticBaseUri;
            var inlineItem = new PhoenixmlDb.XQuery.Execution.InlineFunctionItem(
                ife.Parameters, ife.Body, capturedContext, ife.ReturnType);
            return (true, inlineItem);
        }

        // Sequence of literals: e.g., ('a', 'b', 'c') — evaluable without context
        if (expr is PhoenixmlDb.XQuery.Ast.SequenceExpression seqExpr)
        {
            var allResolved = true;
            var items = new List<object?>();
            foreach (var item in seqExpr.Items)
            {
                var (itemResolved, itemVal) = TryResolveExprFromWatchers(item, watchers);
                if (!itemResolved) { allResolved = false; break; }
                items.Add(itemVal);
            }
            if (allResolved)
                return (true, items.Count == 1 ? items[0] : items.ToArray());
        }

        return (false, null);
    }


    /// <summary>
    /// Attempts to evaluate a function call by resolving all its arguments from
    /// stream watchers or literals, then invoking the function directly.
    /// Returns (true, value) if all arguments were resolvable; (false, null) otherwise.
    ///
    /// This handles consuming sub-expressions used as arguments to non-consuming
    /// functions — e.g., translate(head(//AUTHOR), ' ', '_') in an AVT attribute name.
    /// </summary>
    private async ValueTask<(bool Resolved, object? Value)> TryEvaluateFunctionWithWatcherArgs(
        PhoenixmlDb.XQuery.Ast.FunctionCallExpression fcall,
        IReadOnlyList<StreamWatcher> watchers)
    {
        // Resolve each argument; give up if any can't be resolved
        var args = new object?[fcall.Arguments.Count];
        for (var i = 0; i < fcall.Arguments.Count; i++)
        {
            var (resolved, val) = TryResolveExprFromWatchers(fcall.Arguments[i], watchers);
            if (!resolved) return (false, null);
            args[i] = val;
        }

        // Only handle standard library functions (fn: or unqualified namespace).
        // User-defined XSLT functions (any other namespace) require the full XSLT execution
        // context and cannot be invoked with the minimal execContext here.
        var ns = fcall.Name.Namespace;
        if (ns != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn && ns != NamespaceId.None)
            return (false, null);

        // Look up the function in the function library and invoke it
        var fn = _functionLibrary.Resolve(fcall.Name, fcall.Arguments.Count);
        if (fn == null) return (false, null);

        // Build a minimal execution context for the function invocation
        using var execContext = new PhoenixmlDb.XQuery.Execution.QueryExecutionContext(
            container: default,
            functions: _functionLibrary,
            nodeProvider: _nodeStore,
            documentResolver: null,
            schemaProvider: _schemaProvider,
            namespaceResolver: null);
        execContext.DefaultCollation = DefaultCollation;
        execContext.StaticBaseUri = StaticBaseUri;

        var result = await fn.InvokeAsync(args, execContext).ConfigureAwait(false);
        return (true, result);
    }


    /// <summary>
    /// When a general-comparison expression has one operand that is a
    /// SimpleMap watcher source (e.g. <c>/path!xs:NMTOKENS(.)!xs:decimal(.)</c>),
    /// re-apply the SimpleMap's tail (Right) against each item the watcher
    /// captured during the streaming pass, then run the general comparison
    /// against the other operand. This surfaces type errors (XPTY0004) that
    /// the optimizer/plan would otherwise mask by re-evaluating the streamable
    /// path against the synthetic empty document and short-circuiting to false.
    /// </summary>
    private async ValueTask<(bool Resolved, object? Value)> TryEvaluateGeneralCompWithSimpleMapWatcherAsync(
        BinaryExpression cmp,
        IReadOnlyList<StreamWatcher> watchers)
    {
        StreamWatcher? leftWatcher = null;
        StreamWatcher? rightWatcher = null;
        foreach (var w in watchers)
        {
            if (w.SourceExpression is not SimpleMapExpression) continue;
            if (ReferenceEquals(cmp.Left, w.SourceExpression)) leftWatcher = w;
            if (ReferenceEquals(cmp.Right, w.SourceExpression)) rightWatcher = w;
        }
        if (leftWatcher == null && rightWatcher == null) return (false, null);

        var leftItems = leftWatcher != null
            ? await ApplySimpleMapTailAsync((SimpleMapExpression)leftWatcher.SourceExpression, leftWatcher.GetResult()).ConfigureAwait(false)
            : await EvaluateToListAsync(cmp.Left).ConfigureAwait(false);
        var rightItems = rightWatcher != null
            ? await ApplySimpleMapTailAsync((SimpleMapExpression)rightWatcher.SourceExpression, rightWatcher.GetResult()).ConfigureAwait(false)
            : await EvaluateToListAsync(cmp.Right).ConfigureAwait(false);

        return (true, RunGeneralComparison(cmp.Operator, leftItems, rightItems));
    }


    /// <summary>
    /// Node-capture cheap-eval recognizer (mirror of the scanner's
    /// <c>IsNodeNavigatingAttributeTail</c>): true when <paramref name="tail"/> is a
    /// compile-time numeric op over a SINGLE context-relative attribute step plus numeric
    /// literals. Yields the attribute local name in <paramref name="attr"/>. Used to take
    /// the cheap per-node path in <see cref="ApplySimpleMapTailAsync"/>.
    /// </summary>
    private static bool TryGetAttributeArithmeticTail(XQueryExpression tail, out string? attr)
    {
        attr = null;
        return MatchAttrArith(tail, ref attr) && attr != null;
    }


    private static bool TryParseWatcherNumber(string s, out object? result)
    {
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            result = d;
            return true;
        }
        result = null;
        return false;
    }


    /// <summary>
    /// When operating in <see cref="InTopLevelItemSeparatorSequence"/> mode, writes the
    /// item-separator before the next top-level item (if one was already emitted) and records that
    /// an item has now been emitted. Returns true when in that mode so callers skip the legacy
    /// atomic-only separator logic; returns false otherwise (leaving legacy behaviour intact).
    /// </summary>
    private bool TryWriteTopLevelItemSeparator()
    {
        if (!InTopLevelItemSeparatorSequence)
            return false;
        if (_topLevelItemEmitted)
            WriteText(_itemSeparatorOverride!, false);
        _topLevelItemEmitted = true;
        return true;
    }


    /// <summary>
    /// Searches the scope stack for a tunnel parameter with the given name.
    /// Returns true and the value if found, false otherwise.
    /// </summary>
    private bool TryGetTunnelParam(QName name, out object? value)
    {
        foreach (var scope in _scopes)
        {
            if (scope.TunnelParametersOrNull is { } tunnels && tunnels.TryGetValue(name, out value))
                return true;
            // Stop at function boundaries — tunnel params don't propagate through functions
            if (scope.IsTunnelBarrier)
                break;
        }
        value = null;
        return false;
    }



    /// <summary>
    /// Tries to get a variable value without throwing if not found.
    /// Used by context-dependent functions like current-group() that should
    /// return empty sequence when called outside their expected context.
    /// </summary>
    public bool TryGetVariable(QName name, out object? value)
    {
        foreach (var scope in _scopes)
        {
            if (scope.VariablesOrNull is { } vars && vars.TryGetValue(name, out value))
            {
                if (value is LazyValue lazy)
                {
                    value = lazy.GetValueAsync().AsTask().GetAwaiter().GetResult();
                    vars[name] = value;
                }
                return true;
            }
        }

        if (GlobalVariables.TryGetValue(name, out value))
        {
            if (value is LazyValue lazyGlobal)
            {
                value = lazyGlobal.GetValueAsync().AsTask().GetAwaiter().GetResult();
                GlobalVariables[name] = value;
            }
            return true;
        }

        // Fallback: prefix-based matching for unresolved namespaces
        if (name.Prefix != null)
        {
            if (TryFindVariableByPrefixFallback(name, out value))
                return true;
        }

        value = null;
        return false;
    }


    /// <summary>
    /// When <paramref name="global"/> is an overriding <c>xsl:variable</c> that carries an
    /// <see cref="Ast.XsltVariable.OriginalVariable"/> (from an <c>xsl:override</c>), evaluates
    /// the overridden variable's value and pushes a scope binding <c>$xsl:original</c> to it so
    /// the overriding variable's <c>select</c>/content can reference it (XSLT 3.0 §3.5.6).
    /// Returns true when a scope was pushed — the caller must <see cref="PopScope"/> afterwards.
    /// </summary>
    internal async ValueTask<bool> TryPushXslOriginalVariableBindingAsync(
        GlobalDeclaration global, StringBuilder outputBuilder)
    {
        if (global.OriginalDeclaration is not Ast.XsltVariable ov || ov.OriginalVariable is not { } orig)
            return false;

        object? originalValue;
        if (orig.Select != null)
        {
            var v = await EvaluateAsync(orig.Select).ConfigureAwait(false);
            originalValue = CoerceSelectValueToDeclaredType(v, orig.As);
        }
        else if (orig.Content != null)
        {
            var savedLen = outputBuilder.Length;
            await orig.Content.ExecuteAsync(this).ConfigureAwait(false);
            originalValue = outputBuilder.ToString(savedLen, outputBuilder.Length - savedLen);
            outputBuilder.Length = savedLen;
        }
        else
        {
            originalValue = "";
        }

        PushScope();
        SetVariable(XslOriginalVariableName, originalValue);
        return true;
    }


    /// <summary>
    /// Pre-scans <paramref name="template"/>'s body for consuming aggregates that
    /// require deferred execution. Returns a deferred-execution entry when:
    /// <list type="bullet">
    ///   <item>The body has at least one aggregate over a consuming expression
    ///         (count(*), sum(*), string-join(*/text(),', '), etc.); AND</item>
    ///   <item>The body does NOT contain xsl:apply-templates (which would itself
    ///         consume children — incompatible with deferral).</item>
    /// </list>
    /// Returns null when deferral isn't applicable; the caller falls through to
    /// the immediate-execution path.
    /// </summary>
    private DeferredStreamingExecution? TryBuildDeferredExecution(
        Ast.XsltTemplate template, Xdm.Nodes.XdmElement element, QName? mode, int position)
    {
        if (_activeStreamingReader == null) return null;
        var scanner = new StreamingExpressionScanner();
        // Anchor the deferred watchers to the matched element's depth: their paths
        // are relative to the matched template's context node, not the document root.
        var watchers = scanner.Scan(template.Body, _activeStreamingReader.Depth);
        if (watchers.Count == 0) return null;
        if (BodyContainsApplyTemplates(template.Body)) return null;
        return new DeferredStreamingExecution
        {
            Template = template,
            Element = element,
            Mode = mode,
            Position = position,
            ParentDepth = _activeStreamingReader.Depth,
            Watchers = watchers
        };
    }


    /// <summary>
    /// SM-ctx (OP-bucket phase 1) streaming handoff for a consuming simple-map
    /// <c>LEFT ! RIGHT</c> in an <c>xsl:value-of</c>/<c>xsl:sequence</c> select. When the
    /// select is the SimpleMap whose RIGHT was registered as an inline-driven
    /// <see cref="ForEachSubscription.PerItemSelect"/> on the active streaming processor
    /// (and the live reader has not yet been consumed), hand off to the processor's
    /// forward pass: it drives the reader, matches the subscription's LEFT path, and for
    /// each matched item materializes it and evaluates RIGHT in place into the
    /// currently-open output. Mirrors the wrapped-for-each handoff in
    /// <see cref="ForEachAsync"/>. Returns true when the handoff fired (caller returns).
    /// </summary>
    /// <param name="select">The consuming instruction's select expression.</param>
    /// <param name="atomizeText">
    /// B3 (atomization/separator under streaming): when the consuming instruction is
    /// <c>xsl:value-of</c>/<c>xsl:attribute</c>/<c>data()</c> (a text/simple-content
    /// consumer), pass the resolved separator here (default <c>" "</c> for value-of,
    /// <c>""</c> for attribute content without an explicit separator) so the per-match
    /// RIGHT emission atomizes element items to their string value and joins them with
    /// the separator, instead of serializing the raw element markup. Pass <c>null</c>
    /// for node-preserving consumers (<c>xsl:copy-of</c>/<c>xsl:sequence</c>).
    /// </param>
    private async ValueTask<bool> TryHandoffSimpleMapContextStreamingAsync(
        XQueryExpression? select, string? atomizeText = null)
    {
        if (_activeStreamingProcessor == null || _activeStreamingReader == null)
            return false;
        if (select is not SimpleMapExpression sm)
            return false;
        var subs = _activeStreamingProcessor.Subscriptions;
        if (subs == null)
            return false;

        ForEachSubscription? matchSub = null;
        foreach (var s in subs)
        {
            if (s.InlineDriven && s.PerItemSelect != null
                && ReferenceEquals(s.PerItemSelect, sm.Right))
            {
                matchSub = s;
                break;
            }
        }
        if (matchSub == null)
            return false;

        var proc = _activeStreamingProcessor;
        var rdr = _activeStreamingReader;
        var ct = _activeStreamingCancellationToken;
        // Clear the handles so the per-match RIGHT evaluation (which runs against the
        // buffered materialized snapshot, not the live reader) doesn't re-enter here.
        _activeStreamingProcessor = null;
        _activeStreamingReader = null;
        // B3: for a text/simple-content consumer, drive the forward pass in
        // atomizing-text mode so element results emit their string value joined by
        // the resolved separator (SerializeResult/SerializeSequenceItems honor
        // _textContentDepth and _itemSeparatorOverride).
        var savedTextDepth = _textContentDepth;
        var savedSeparator = _itemSeparatorOverride;
        if (atomizeText != null)
        {
            _textContentDepth++;
            _itemSeparatorOverride = atomizeText;
        }
        try
        {
            await proc.ProcessAsync(rdr, ct).ConfigureAwait(false);
        }
        finally
        {
            _activeStreamingProcessor = proc;
            _activeStreamingReader = rdr;
            if (atomizeText != null)
            {
                _textContentDepth = savedTextDepth;
                _itemSeparatorOverride = savedSeparator;
            }
        }
        return true;
    }


    /// <summary>
    /// ForExpr streaming handoff (sx-ForExpr): when <paramref name="select"/> is a
    /// <c>for $x in CONSUMING-PATH return EXPR</c> registered as an inline-driven
    /// subscription on the active streaming processor, drive the forward pass in place so
    /// the surrounding LRE is emitted around the concatenated per-item results. Mirrors
    /// <see cref="TryHandoffSimpleMapContextStreamingAsync"/>. Returns true when fired.
    /// </summary>
    private async ValueTask<bool> TryHandoffForExpressionStreamingAsync(
        XQueryExpression? select, string? atomizeText = null)
    {
        if (_activeStreamingProcessor == null || _activeStreamingReader == null)
            return false;
        if (select is not PhoenixmlDb.XQuery.Ast.FlworExpression flwor)
            return false;
        var subs = _activeStreamingProcessor.Subscriptions;
        if (subs == null)
            return false;

        ForEachSubscription? matchSub = null;
        foreach (var s in subs)
        {
            if (s.InlineDriven && s.RangeVariable != null
                && ReferenceEquals(s.PerItemSelect, flwor.ReturnExpression))
            {
                matchSub = s;
                break;
            }
        }
        if (matchSub == null)
            return false;

        var proc = _activeStreamingProcessor;
        var rdr = _activeStreamingReader;
        var ct = _activeStreamingCancellationToken;
        // Clear the handles so the per-match return evaluation (against the buffered
        // snapshot, not the live reader) doesn't re-enter here.
        _activeStreamingProcessor = null;
        _activeStreamingReader = null;
        // B3: atomizing-text mode for value-of/attribute/data() consumers (see
        // TryHandoffSimpleMapContextStreamingAsync).
        var savedTextDepth = _textContentDepth;
        var savedSeparator = _itemSeparatorOverride;
        if (atomizeText != null)
        {
            _textContentDepth++;
            _itemSeparatorOverride = atomizeText;
        }
        try
        {
            await proc.ProcessAsync(rdr, ct).ConfigureAwait(false);
        }
        finally
        {
            _activeStreamingProcessor = proc;
            _activeStreamingReader = rdr;
            if (atomizeText != null)
            {
                _textContentDepth = savedTextDepth;
                _itemSeparatorOverride = savedSeparator;
            }
        }
        return true;
    }


    public override async ValueTask ForEachAsync(
        XQueryExpression select,
        List<XsltSort> sorts,
        XsltSequenceConstructor body)
    {
        // Wrapped (inline-driven) streamable for-each handoff: when this for-each is
        // the source instruction of an inline-driven subscription registered on the
        // active streaming processor, and the live reader has not yet been consumed
        // (the body is running linearly so the enclosing construction has already been
        // emitted), hand off to the processor's forward pass. It drives the reader,
        // matches the subscription's path, and dispatches THIS for-each body per match
        // into the currently-open output. On return, linear execution continues and
        // emits the construction's close tag — so the wrapper survives. Mirrors the
        // apply-templates streaming intercept below.
        if (_activeStreamingProcessor != null && _activeStreamingReader != null
            && sorts.Count == 0)
        {
            var subs = _activeStreamingProcessor.Subscriptions;
            if (subs != null)
            {
                ForEachSubscription? matchSub = null;
                foreach (var s in subs)
                {
                    if (s.InlineDriven && s.SourceInstruction != null
                        && ReferenceEquals(s.SourceInstruction.Body, body))
                    {
                        matchSub = s;
                        break;
                    }
                }
                if (matchSub != null)
                {
                    var proc = _activeStreamingProcessor;
                    var rdr = _activeStreamingReader;
                    var ct = _activeStreamingCancellationToken;
                    // Clear the handles so the body dispatched per-match doesn't
                    // re-enter this handoff (the for-each body runs against buffered
                    // snapshots, not the live reader).
                    _activeStreamingProcessor = null;
                    _activeStreamingReader = null;
                    try
                    {
                        // Mixed-sequence prefix/suffix: grounded operands appearing
                        // before/after the streamable path in the for-each select
                        // (e.g. `data(path), 101, 102` — si-result-document-002).
                        // The bare top-of-body forward-pass path drains these in
                        // SourceDocumentAsync; the WRAPPED (inline-driven) handoff must
                        // do the same in place so the per-item body dispatch surrounds
                        // the streaming pass in document order (and, when wrapped in an
                        // xsl:result-document, lands in the redirected secondary buffer).
                        if (matchSub.PrefixItems.Count > 0)
                            await ExecuteForEachSubscriptionItemsAsync(matchSub, matchSub.PrefixItems).ConfigureAwait(false);
                        await proc.ProcessAsync(rdr, ct).ConfigureAwait(false);
                        if (matchSub.SuffixItems.Count > 0)
                            await ExecuteForEachSubscriptionItemsAsync(matchSub, matchSub.SuffixItems).ConfigureAwait(false);
                    }
                    finally
                    {
                        _activeStreamingProcessor = proc;
                        _activeStreamingReader = rdr;
                    }
                    return;
                }
            }
        }

        // Streaming: when inside a streamable template with a consuming child-axis
        // select, drive the reader directly instead of pre-evaluating select.
        // Mirrors the apply-templates streaming intercept above. Sorts are not
        // applicable in this path (a sort would force materialization, defeating
        // streaming) — fall back to the buffered impl if sorts are present.
        if (_isStreamingExecution && _activeStreamingReader != null
            && sorts.Count == 0 && IsConsumingChildSelect(select))
        {
            await ForEachStreamingAsync(body).ConfigureAwait(false);
            return;
        }

        // Fast path for range expressions (1 to N) without sorts — avoid boxing N integers
        if (select is PhoenixmlDb.XQuery.Ast.RangeExpression rangeExpr && sorts.Count == 0)
        {
            var startVal = await EvaluateAsync(rangeExpr.Start).ConfigureAwait(false);
            var endVal = await EvaluateAsync(rangeExpr.End).ConfigureAwait(false);
            if (startVal != null && endVal != null)
            {
                var s = Convert.ToInt64(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(startVal), CultureInfo.InvariantCulture);
                var e = Convert.ToInt64(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(endVal), CultureInfo.InvariantCulture);
                var count = (int)Math.Max(0, e - s + 1);
                // Check if range body is trivially empty (e.g., xsl:if with always-false test)
                // In that case, skip the entire loop — just set position context for the end
                if (!RangeBodyHasEffect(body, count))
                {
                    // No observable effect — skip entire loop
                }
                else
                {
                    // Use a single shared scope for the entire range loop to avoid N dictionary allocations.
                    PushScope();
                    var savedTemplate = _currentTemplate;
                    _currentTemplate = null;
                    // Per XSLT 3.0 §13.4.1, xsl:for-each does NOT change the current mode.
                    // Only the current template rule is absent inside xsl:for-each (XTDE0560).
                    // `mode="#current"` on a nested xsl:apply-templates must still see the
                    // enclosing template's mode.
                    try
                    {
                        for (var i = s; i <= e; i++)
                        {
                            if ((i - s) % 4096 == 0) CheckResourceLimits();
                            var position = (int)(i - s + 1);
                            PushContextItem(i, position, count);
                            PushCurrentItem(i);
                            try
                            {
                                await body.ExecuteAsync(this).ConfigureAwait(false);
                            }
                            finally
                            {
                                PopCurrentItem();
                                PopContextItem();
                            }
                        }
                    }
                    finally
                    {
                        _currentTemplate = savedTemplate;
                        PopScope();
                    }
                }
                return;
            }
        }

        var result = await EvaluateAsync(select).ConfigureAwait(false);
        // An XDM array/map selected here is a single item to iterate once, not flattened.
        IEnumerable<object> items = SelectResultItems(result);

        if (sorts.Count > 0)
        {
            items = await SortNodesAsync(items, sorts).ConfigureAwait(false);
        }

        var itemList = items.ToList();
        var position2 = 0;

        foreach (var item in itemList)
        {
            CheckResourceLimits();
            position2++;
            // Convert ResultTreeFragments to XDM documents so accumulators and XPath navigation work
            var effectiveItem = item is ResultTreeFragment rtf && _nodeStore != null
                ? (object?)ParseResultTreeFragment(rtf) ?? item
                : item;
            PushContextItem(effectiveItem, position2, itemList.Count);
            PushCurrentItem(effectiveItem); // For XSLT current() function
            PushScope();

            // XTDE0560: the current template rule is absent inside xsl:for-each — but
            // per XSLT 3.0 §13.4.1, the *current mode* is unchanged. Don't null it: a
            // nested xsl:apply-templates with mode="#current" must still see the
            // enclosing template's mode.
            var savedTemplate = _currentTemplate;
            _currentTemplate = null;

            try
            {
                await body.ExecuteAsync(this).ConfigureAwait(false);
            }
            finally
            {
                _currentTemplate = savedTemplate;
                PopScope();
                PopCurrentItem();
                PopContextItem();
            }
        }
    }


    public override async ValueTask SwitchAsync(Ast.XsltSwitch instruction)
    {
        // xsl:switch is NOT xsl:choose with a subject. Each xsl:when/@test supplies a sequence of
        // CANDIDATE VALUES, and the branch is taken when the switch operand equals any of them —
        // the "=" general comparison, which is why a when can list alternatives:
        //     <xsl:when test="('jpg','JPG','jpeg','JPEG')" select="'bitmap'"/>
        //
        // Taking the effective boolean value of that instead raised FORG0006 ("not defined for a
        // sequence of two or more items starting with a non-node value") on any multi-value when.
        // Worse, a SINGLE-value when never errored and silently always matched: the EBV of a
        // non-empty string is true, so the first branch won whatever the operand was.
        var selectItems = AtomizeForComparison(
            await EvaluateToListAsync(instruction.Select).ConfigureAwait(false));

        // The operand stays the context item for the test expressions, as before.
        PushContextItem(selectItems.Count == 1 ? selectItems[0] : selectItems.ToArray(),
            1, 1);
        try
        {
            foreach (var when in instruction.When)
            {
                var testItems = AtomizeForComparison(
                    await EvaluateToListAsync(when.Test).ConfigureAwait(false));
                if (RunGeneralComparison(BinaryOperator.GeneralEqual, selectItems, testItems))
                {
                    await when.Body.ExecuteAsync(this).ConfigureAwait(false);
                    return;
                }
            }
            if (instruction.Otherwise != null)
                await instruction.Otherwise.ExecuteAsync(this).ConfigureAwait(false);
        }
        finally
        {
            PopContextItem();
        }
    }


    public override async ValueTask ForEachMemberAsync(Ast.XsltForEachMember instruction)
    {
        var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);

        // Get array members
        IList<object?> members;
        if (result is List<object?> list)
            members = list;
        else if (result is object?[] arr)
            members = arr;
        else if (result != null)
            members = [result];
        else
            return;

        var position = 0;
        foreach (var member in members)
        {
            position++;
            PushContextItem(member, position, members.Count);
            try
            {
                await instruction.Body.ExecuteAsync(this).ConfigureAwait(false);
            }
            finally
            {
                PopContextItem();
            }
        }
    }


    public override async ValueTask ForEachGroupAsync(XsltForEachGroup instruction)
    {
        // Resolve namespace IDs in group-starting-with / group-ending-with patterns
        // (similar to xsl:number count/from patterns, these need runtime resolution)
        ResolvePatternNamespacesLocal(instruction.GroupStartingWith);
        ResolvePatternNamespacesLocal(instruction.GroupEndingWith);

        // Streaming for-each-group: when inside a streamable template with the active
        // XmlReader available, drive the reader directly instead of pre-evaluating select.
        //
        // Currently dispatches group-starting-with, group-ending-with and group-adjacent.
        // group-by is excluded: see the note in ForEachGroupStreamingAsync's group-by branch.
        if (_isStreamingExecution && _activeStreamingReader != null
            && (instruction.GroupStartingWith != null
                || instruction.GroupEndingWith != null
                || instruction.GroupAdjacent != null))
        {
            await ForEachGroupStreamingAsync(instruction).ConfigureAwait(false);
            return;
        }

        var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
        // An XDM array/map is a single item to group, not flattened into members/entries.
        var items = SelectResultItems(result).ToList();

        List<(object Key, List<object> Items)>? groupList = null;

        if (instruction.GroupBy != null)
        {
            // Group by key value — items with the same key are grouped together. The grouping
            // semantics live in GroupByAccumulator, shared with the streaming path.
            string? groupByCollation = instruction.Collation != null
                ? await EvaluateAvtAsync(instruction.Collation).ConfigureAwait(false)
                : DefaultCollation;
            ValidateCollation(groupByCollation, "XTDE1110");
            StringComparer groupByComparer = GetCollationComparer(groupByCollation);
            var accumulator = new GroupByAccumulator(instruction.Composite, groupByComparer);

            var pos = 0;
            foreach (var item in items)
            {
                pos++;
                PushContextItem(item, pos, items.Count);
                try
                {
                    var key = await EvaluateAsync(instruction.GroupBy).ConfigureAwait(false);
                    accumulator.Add(item, key, GroupingKeyString, IsNumeric,
                                    (a, b) => ValuesEqual(a, b), CompositeKeysEqual, groupByComparer);
                }
                finally
                {
                    PopContextItem();
                }
            }

            groupList = accumulator.ToGroupList();
        }
        else if (instruction.GroupAdjacent != null)
        {
            // Group adjacent — consecutive items with the same key form a group
            groupList = new List<(object Key, List<object> Items)>();
            string? currentKeyStr = null;

            // Determine collation-aware comparison for group-adjacent
            string? groupCollation = instruction.Collation != null
                ? await EvaluateAvtAsync(instruction.Collation).ConfigureAwait(false)
                : DefaultCollation;
            ValidateCollation(groupCollation, "XTDE1110");
            StringComparer adjacentComparer = GetCollationComparer(groupCollation);

            var pos = 0;
            foreach (var item in items)
            {
                pos++;
                PushContextItem(item, pos, items.Count);
                try
                {
                    var key = await EvaluateAsync(instruction.GroupAdjacent).ConfigureAwait(false);
                    // XTTE1100: group-adjacent expression must return a single atomic value (unless composite="yes")
                    if (!instruction.Composite)
                    {
                        if (key == null || (key is object[] keyArr && keyArr.Length == 0)
                            || (key is IEnumerable<object?> keySeq && !keySeq.Any()))
                            throw Error("XTTE1100: The group-adjacent expression must return a single atomic value; it returned an empty sequence");
                        if (key is object[] multiArr && multiArr.Length > 1)
                            throw Error("XTTE1100: The group-adjacent expression must return a single atomic value; it returned a sequence of " + multiArr.Length + " items");
                    }
                    var keyStr = GroupingKeyString(key);
                    if (groupList.Count == 0 || adjacentComparer.Compare(keyStr, currentKeyStr!) != 0)
                    {
                        groupList.Add((key ?? "", new List<object> { item }));
                        currentKeyStr = keyStr;
                    }
                    else
                    {
                        groupList[^1].Items.Add(item);
                    }
                }
                finally
                {
                    PopContextItem();
                }
            }
        }
        else if (instruction.GroupStartingWith != null)
        {
            // Group starting with — new group starts when item matches the pattern
            groupList = new List<(object Key, List<object> Items)>();

            foreach (var item in items)
            {
                var matches = MatchesPattern(item, instruction.GroupStartingWith);
                if (matches || groupList.Count == 0)
                {
                    groupList.Add((item, new List<object> { item }));
                }
                else
                {
                    groupList[^1].Items.Add(item);
                }
            }
        }
        else if (instruction.GroupEndingWith != null)
        {
            // Group ending with — group ends when item matches the pattern
            groupList = new List<(object Key, List<object> Items)>();
            groupList.Add((items.Count > 0 ? items[0] : "", new List<object>()));

            foreach (var item in items)
            {
                groupList[^1].Items.Add(item);
                var matches = MatchesPattern(item, instruction.GroupEndingWith);
                if (matches)
                {
                    // Current group ended, start new one for next items
                    groupList.Add((item, new List<object>()));
                }
            }

            // Remove trailing empty group
            if (groupList.Count > 0 && groupList[^1].Items.Count == 0)
                groupList.RemoveAt(groupList.Count - 1);
        }

        if (groupList != null)
        {
            // Apply sorting if specified
            if (instruction.Sorts.Count > 0)
            {
                groupList = await SortGroupsAsync(groupList, instruction.Sorts).ConfigureAwait(false);
            }

            var position = 0;
            foreach (var (key, group) in groupList)
            {
                CheckResourceLimits();
                position++;
                PushContextItem(group[0], position, groupList.Count);
                PushCurrentItem(group[0]);
                PushScope();
                SetVariable(new QName(NamespaceId.None, "current-group"), group);
                // current-grouping-key is only available for group-by and group-adjacent,
                // not for group-starting-with or group-ending-with (XTDE1071)
                SetVariable(new QName(NamespaceId.None, "current-grouping-key"),
                    instruction.GroupBy != null || instruction.GroupAdjacent != null ? key : null);

                // XTDE0560: the current template rule is absent inside xsl:for-each-group —
                // but the current mode is unchanged (XSLT 3.0 §15). Don't null it: a nested
                // xsl:apply-templates with mode="#current" must still see the enclosing
                // template's mode.
                var savedTemplate = _currentTemplate;
                _currentTemplate = null;

                try
                {
                    await instruction.Body.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    _currentTemplate = savedTemplate;
                    PopScope();
                    PopCurrentItem();
                    PopContextItem();
                }
            }
        }
    }


    /// <summary>
    /// Recognizes a striding DOWNWARD-path select — a multi-step child-axis name-test
    /// path rooted at the document node (e.g. <c>root/item</c>, <c>./a/b/c</c>) — and
    /// returns its step name-tests in document order. Returns <c>null</c> for anything
    /// else (predicates, non-child axes, kind tests, descendant hops, initial variable).
    /// The single-step case is handled by <see cref="IsDocumentLevelStridingSelect"/> and
    /// the processor forward pass; this drives the reader for the multi-step descent that
    /// the forward pass cannot reach. Conservative by construction.
    /// </summary>
    private static List<PhoenixmlDb.XQuery.Ast.NameTest>? TryGetStridingDescentSteps(
        XQueryExpression? select)
    {
        if (select is not PhoenixmlDb.XQuery.Ast.PathExpression path)
            return null;
        if (path.InitialExpression != null
            && path.InitialExpression is not PhoenixmlDb.XQuery.Ast.ContextItemExpression)
            return null;
        // Require at least two steps — one-step striding selects route through the
        // processor forward pass, not this reader-driven descent.
        if (path.Steps.Count < 2) return null;
        var names = new List<PhoenixmlDb.XQuery.Ast.NameTest>(path.Steps.Count);
        foreach (var step in path.Steps)
        {
            if (step.Axis != PhoenixmlDb.XQuery.Ast.Axis.Child) return null;
            if (step.Predicates.Count > 0) return null;
            if (step.NodeTest is not PhoenixmlDb.XQuery.Ast.NameTest nt) return null;
            names.Add(nt);
        }
        return names;
    }


    /// <summary>
    /// Streaming implementation of xsl:for-each over the children of the current node.
    /// Drives <see cref="_activeStreamingReader"/> directly: for each child element,
    /// builds a full subtree, binds it as the focus, and runs the body. Sorts are
    /// not supported in this path (the caller falls back to the buffered impl).
    /// </summary>
    private async ValueTask ForEachStreamingAsync(XsltSequenceConstructor body)
    {
        var reader = _activeStreamingReader!;
        var ct = _activeStreamingCancellationToken;
        var parentDepth = reader.Depth;
        var savedTemplate = _currentTemplate;
        _currentTemplate = null; // current template rule is absent inside xsl:for-each
        var position = 0;
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
                if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;

                var elem = await ReadStreamingElementForDispatchAsync(reader, ct).ConfigureAwait(false);
                position++;
                PushContextItem(elem, position, 0);
                PushCurrentItem(elem);
                PushScope();
                // Body iterates a buffered XdmElement — suspend streaming-execution
                // so shallow-copy / xsl:copy inside the body use normal close-tag emission.
                var savedStreaming = _isStreamingExecution;
                _isStreamingExecution = false;
                try
                {
                    await body.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    _isStreamingExecution = savedStreaming;
                    PopScope();
                    PopCurrentItem();
                    PopContextItem();
                }
            }
        }
        finally
        {
            _currentTemplate = savedTemplate;
        }
    }


    /// <summary>
    /// Streaming implementation of xsl:iterate. Drives the reader, runs the body
    /// per child element, handles xsl:next-iteration state updates and xsl:break.
    /// Initial-params are bound once before the loop; xsl:next-iteration's
    /// xsl:with-param bindings overwrite them on each iteration. xsl:on-completion
    /// runs after the last item (or on early break — same as non-streaming impl).
    /// </summary>
    private async ValueTask IterateStreamingAsync(XsltIterate instruction)
    {
        var reader = _activeStreamingReader!;
        var ct = _activeStreamingCancellationToken;
        var parentDepth = reader.Depth;

        // Bind initial iteration parameters once
        PushScope();
        foreach (var param in instruction.Params)
        {
            object? value;
            if (param.Select != null)
                value = await EvaluateAsync(param.Select).ConfigureAwait(false);
            else if (param.Content != null)
                value = await EvaluateBodyContentToValueAsync(param.Content).ConfigureAwait(false);
            else
                value = null;
            SetVariable(param.Name, value);
        }

        var brokeOut = false;
        var position = 0;
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
                if (reader.NodeType != System.Xml.XmlNodeType.Element) continue;

                var elem = await ReadStreamingElementForDispatchAsync(reader, ct).ConfigureAwait(false);
                position++;
                PushContextItem(elem, position, 0);
                PushCurrentItem(elem);
                var savedStreaming = _isStreamingExecution;
                _isStreamingExecution = false; // body iterates a buffered element
                try
                {
                    await instruction.Body.ExecuteAsync(this).ConfigureAwait(false);
                }
                catch (BreakException)
                {
                    brokeOut = true;
                    break;
                }
                catch (NextIterationException next)
                {
                    // Evaluate all next-iteration with-param values BEFORE writing them,
                    // so params don't see each other's new values during evaluation.
                    var newValues = new List<(QName Name, object? Value)>();
                    foreach (var p in next.WithParams)
                    {
                        object? value;
                        if (p.Select != null)
                            value = await EvaluateAsync(p.Select).ConfigureAwait(false);
                        else if (p.Content != null)
                            value = await EvaluateBodyContentToValueAsync(p.Content).ConfigureAwait(false);
                        else
                            value = null;
                        newValues.Add((p.Name, value));
                    }
                    foreach (var (name, value) in newValues)
                        SetVariable(name, value);
                }
                finally
                {
                    _isStreamingExecution = savedStreaming;
                    PopCurrentItem();
                    PopContextItem();
                }
            }

            // xsl:break stops iteration early with the reader parked at the broken
            // child's EndElement. Following siblings up to the parent's EndElement
            // were NOT consumed by this iterate and would otherwise leak out through
            // the processor's built-in template rule (si-iterate-013: trailing
            // <item>/<c>/<d> text after the break, and the parent's own </root> close).
            // Skip forward to the parent's EndElement and hand it to the processor via
            // deferred-read so it closes any enclosing construction (e.g. the wrapping
            // xsl:copy's <root> tag) — exactly as the natural end-of-children path does.
            if (brokeOut)
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    ct.ThrowIfCancellationRequested();
                    if (reader.NodeType == System.Xml.XmlNodeType.EndElement
                        && reader.Depth == parentDepth)
                        break;
                }
                _streamingDeferReadOnNextIteration = true;
            }

            // xsl:on-completion fires after the loop unless an outer xsl:break
            // explicitly suppressed it (BreakException reaches the outer loop).
            // Streaming mirrors the non-streaming policy: on-completion fires when
            // either we exhausted the input naturally OR an inner break exited early.
            if (instruction.OnCompletion != null)
            {
                _ = brokeOut; // brokeOut tracked for parity; on-completion fires either way
                await instruction.OnCompletion.ExecuteAsync(this).ConfigureAwait(false);
            }
        }
        finally
        {
            PopScope();
        }
    }


    /// <summary>
    /// Streaming implementation of xsl:for-each-group. Drives the active XmlReader
    /// directly, building one full XdmElement subtree per child element event,
    /// applying the grouping logic (group-starting-with / group-ending-with /
    /// group-adjacent), and firing the body per group with current-group() bound
    /// to the buffered items.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Group accumulation buffers the items of the CURRENT group only — group-by
    /// (which needs all items materialized for cross-group aggregation) falls back
    /// to the non-streaming path; this method handles only the three single-pass
    /// patterns. The body executes with <see cref="_isStreamingExecution"/>
    /// temporarily cleared so apply-templates over current-group() uses normal
    /// (non-streaming) semantics on the buffered XdmNodes — without that toggle,
    /// the shallow-copy fallback would push to <see cref="_streamingOpenElements"/>
    /// and corrupt close-tag bookkeeping.
    /// </para>
    /// <para>
    /// End-of-children detection: when an EndElement event at the parent's depth
    /// fires, this method has consumed it but the StreamingXmlProcessor still needs
    /// to do its own EndElement bookkeeping (pop ancestor stack, close deferred
    /// tag). We set <see cref="_streamingDeferReadOnNextIteration"/> so the
    /// processor skips its next ReadAsync and processes the position we already
    /// reached.
    /// </para>
    /// </remarks>
    private async ValueTask ForEachGroupStreamingAsync(XsltForEachGroup instruction)
    {
        var reader = _activeStreamingReader!;
        var ct = _activeStreamingCancellationToken;
        // Reader is currently positioned just past the parent's StartElement.
        // The parent's depth is reader.Depth (children appear at parent.Depth + 1
        // when we read them). We stop when we see an EndElement at parent.Depth
        // (the parent's own EndElement).
        var parentDepth = reader.Depth;

        var currentGroup = new List<object>();
        object? currentKey = null;
        var firstItem = true;
        // 1-based position of the current item within the whole SELECTED sequence
        // (not within its group) — the context position() for the grouping-key
        // expression, e.g. 032's group-adjacent="(position()-1) idiv $block-size".
        var selectedPosition = 0;

        // The select expression names which child elements are grouped. For a
        // striding leaf like select="transaction" (or "record/copy-of()", whose
        // striding step is `record`), only elements with that local name are
        // members — a mixed-content parent (007's <account-number> sibling of the
        // <transaction>s) would otherwise pollute the first group and evaluate the
        // grouping key against the wrong element. select="*" (or any wildcard leaf)
        // yields null → every child element is a member.
        var selectLeafName = StreamingForEachGroupSelectLeafName(instruction.Select);

        // group-by accumulates across the WHOLE population before any group can be emitted, so
        // it uses the shared accumulator rather than the running currentGroup the adjacent modes
        // fill. Collation is resolved once, up front, exactly as the buffered path does.
        //
        // NOT REACHED. Enabling it needs two upstream changes, and the second is unsolved:
        //
        //   1. StreamingSubtreeBufferDetector forces a materialized subtree for group-by, on the
        //      stated grounds that group-by has no streaming dispatch. Retiring that rule does
        //      clear the buffer decision (verified: detector goes False, output unchanged), and
        //      the Streamability planner was always content to stream these bodies.
        //
        //   2. Even then this branch does not run: at the for-each-group, _isStreamingExecution
        //      is false and _activeStreamingReader is null, though the template-dispatch site saw
        //      reader != null moments earlier. The streaming context is torn down before the
        //      template BODY executes. That affects every grouping mode, not just group-by, so
        //      the streamed dispatch appears unreachable for these shapes generally — worth
        //      confirming against StreamingForEachGroupTest, whose assertions pass either way and
        //      so do not prove which path served them.
        //
        // Measure before pursuing: peak RSS on a 7.7 MB input was ~284 MB via the buffered path,
        // dominated by the source tree rather than by grouping, so the ceiling on this win is
        // smaller than it looks.
        GroupByAccumulator? groupByAccumulator = null;
        StringComparer? groupByComparer = null;
        if (instruction.GroupBy != null)
        {
            var groupByCollation = instruction.Collation != null
                ? await EvaluateAvtAsync(instruction.Collation).ConfigureAwait(false)
                : DefaultCollation;
            ValidateCollation(groupByCollation, "XTDE1110");
            groupByComparer = GetCollationComparer(groupByCollation);
            groupByAccumulator = new GroupByAccumulator(instruction.Composite, groupByComparer);
        }

        // Helper: build a full XdmElement subtree starting from the current
        // StartElement event, delegating to the shared streaming materializer so
        // the accumulated group members are REAL nodes: text descendants captured
        // and _stringValue precomputed (so value-of/aggregate/copy-of/snapshot over
        // current-group() all resolve). The materializer leaves the reader ON the
        // matching EndElement (or on the empty element) — the outer child loop's
        // next ReadAsync advances past it to the next sibling.
        Xdm.Nodes.XdmElement ReadElementSubtree()
        {
            ct.ThrowIfCancellationRequested();
            var elem = StreamingSubtreeMaterializer.Materialize(reader, _nodeStore!, DocumentId.None)!;
            // Detach the materialized root: as a selected group member it stands on
            // its own, and later snapshot(current-group())/.. synthesizes the parent.
            elem.Parent = NodeId.None;
            return elem;
        }

        // Helper: flush the current group through the body
        async ValueTask FlushAsync()
        {
            if (currentGroup.Count == 0) return;
            var savedStreaming = _isStreamingExecution;
            _isStreamingExecution = false; // body iterates buffered items, not the stream
            PushContextItem(currentGroup[0], 1, 1);
            PushCurrentItem(currentGroup[0]);
            PushScope();
            SetVariable(new QName(NamespaceId.None, "current-group"), currentGroup);
            // current-grouping-key is bound only for group-by/group-adjacent
            SetVariable(new QName(NamespaceId.None, "current-grouping-key"),
                instruction.GroupAdjacent != null || instruction.GroupBy != null ? currentKey : null);
            var savedTemplate = _currentTemplate;
            _currentTemplate = null; // current template rule is absent inside for-each-group
            try
            {
                await instruction.Body.ExecuteAsync(this).ConfigureAwait(false);
            }
            finally
            {
                _currentTemplate = savedTemplate;
                PopScope();
                PopCurrentItem();
                PopContextItem();
                _isStreamingExecution = savedStreaming;
            }
        }

        // Drive the reader through the parent's children
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType == System.Xml.XmlNodeType.EndElement && reader.Depth == parentDepth)
            {
                // Reached the parent's EndElement. We've consumed it but the processor
                // still needs to do its own EndElement bookkeeping — signal it.
                _streamingDeferReadOnNextIteration = true;
                break;
            }

            if (reader.NodeType != System.Xml.XmlNodeType.Element)
                continue; // text/whitespace/comment between children are not selected by *

            // Non-selected sibling element (wrong local name): consume its subtree so
            // the reader advances, but do NOT treat it as a group member. Its presence
            // does not break an adjacent run (the spec's selected sequence simply omits
            // it), matching the buffered path where select filters before grouping.
            if (selectLeafName != null && reader.LocalName != selectLeafName)
            {
                ReadElementSubtree();
                continue;
            }

            // Build full subtree for this child element
            var child = ReadElementSubtree();

            // Apply grouping decision
            if (groupByAccumulator != null)
            {
                // Evaluate the key against this member, then hand it to the shared accumulator.
                // Nothing is emitted yet: a later member may carry an earlier group's key.
                selectedPosition++;
                PushContextItem(child, selectedPosition, selectedPosition);
                try
                {
                    var savedStreamingKey = _isStreamingExecution;
                    _isStreamingExecution = false; // key runs over the materialized member
                    object? key;
                    try
                    {
                        key = await EvaluateAsync(instruction.GroupBy!).ConfigureAwait(false);
                    }
                    finally
                    {
                        _isStreamingExecution = savedStreamingKey;
                    }
                    groupByAccumulator.Add(child, key, GroupingKeyString, IsNumeric,
                        (a, b) => ValuesEqual(a, b), CompositeKeysEqual, groupByComparer!);
                }
                finally
                {
                    PopContextItem();
                }
                continue;
            }

            if (instruction.GroupStartingWith != null)
            {
                var matches = MatchesPattern(child, instruction.GroupStartingWith);
                if (matches || firstItem)
                {
                    await FlushAsync().ConfigureAwait(false);
                    currentGroup = new List<object>();
                }
                currentGroup.Add(child);
                firstItem = false;
            }
            else if (instruction.GroupEndingWith != null)
            {
                currentGroup.Add(child);
                if (MatchesPattern(child, instruction.GroupEndingWith))
                {
                    await FlushAsync().ConfigureAwait(false);
                    currentGroup = new List<object>();
                }
                firstItem = false;
            }
            else // GroupAdjacent
            {
                // Evaluate the key expression with the child as context item, using
                // its position in the whole selected sequence (position() support).
                selectedPosition++;
                PushContextItem(child, selectedPosition, 0);
                object? key;
                try
                {
                    key = await EvaluateAsync(instruction.GroupAdjacent!).ConfigureAwait(false);
                }
                finally
                {
                    PopContextItem();
                }
                if (firstItem)
                {
                    currentKey = key;
                    firstItem = false;
                }
                else if (!GroupAdjacentKeysEqual(currentKey, key))
                {
                    await FlushAsync().ConfigureAwait(false);
                    currentGroup = new List<object>();
                    currentKey = key;
                }
                currentGroup.Add(child);
            }
        }

        // Final flush. For the adjacent modes this emits the run still in hand; for group-by the
        // population is only now complete, so every group is emitted here in first-appearance
        // order — the same order the buffered path produces.
        if (groupByAccumulator != null)
        {
            foreach (var (key, members) in groupByAccumulator.ToGroupList())
            {
                currentGroup = members;
                currentKey = key;
                await FlushAsync().ConfigureAwait(false);
            }
            return;
        }
        await FlushAsync().ConfigureAwait(false);
    }


    public override async ValueTask IterateAsync(XsltIterate instruction)
    {
        // Streaming: when inside a streamable template with a consuming child-axis
        // select, drive the reader directly. State passing via xsl:next-iteration
        // mirrors the non-streaming impl (NextIterationException carries the new
        // param bindings).
        if (_isStreamingExecution && _activeStreamingReader != null
            && IsConsumingChildSelect(instruction.Select))
        {
            await IterateStreamingAsync(instruction).ConfigureAwait(false);
            return;
        }

        var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);

        // Use lazy enumeration when possible to avoid materializing large sequences.
        // xsl:iterate with xsl:break can exit early, so full materialization is wasteful.
        // Only materialize to a list when last() might be needed (we use -1 for unknown).
        IEnumerable<object> items;
        int itemCount;
        if (result == null)
        {
            items = [];
            itemCount = 0;
        }
        else if (result is List<object?> or System.Collections.IDictionary)
        {
            // An XDM array/map is a single item to iterate, not flattened.
            items = [result];
            itemCount = 1;
        }
        else if (result is object?[] arr)
        {
            items = arr.Where(x => x != null).Cast<object>();
            itemCount = arr.Length;
        }
        else if (result is IEnumerable<object> seq)
        {
            // Keep lazy — don't ToList() for large sequences
            items = seq;
            itemCount = -1; // unknown, use -1 for last()
        }
        else
        {
            items = [result];
            itemCount = 1;
        }

        // Initialize iteration parameters
        PushScope();
        foreach (var param in instruction.Params)
        {
            object? value;
            if (param.Select != null)
            {
                value = await EvaluateAsync(param.Select).ConfigureAwait(false);
            }
            else if (param.Content != null)
            {
                value = await EvaluateBodyContentToValueAsync(param.Content).ConfigureAwait(false);
            }
            else
            {
                value = null;
            }
            SetVariable(param.Name, value);
        }

        try
        {
            var position = 0;
            var brokeOut = false;
            foreach (var item in items)
            {
                CheckResourceLimits();
                position++;
                PushContextItem(item, position, itemCount > 0 ? itemCount : position);
                PushCurrentItem(item); // For XSLT current() function

                try
                {
                    await instruction.Body.ExecuteAsync(this).ConfigureAwait(false);
                }
                catch (BreakException)
                {
                    brokeOut = true;
                    break;
                }
                catch (NextIterationException next)
                {
                    // Evaluate ALL with-param values first using old parameter values,
                    // then set them all at once. This ensures params don't see each other's
                    // new values during evaluation (XSLT spec requirement).
                    var newValues = new List<(QName Name, object? Value)>();
                    foreach (var param in next.WithParams)
                    {
                        object? value;
                        if (param.Select != null)
                        {
                            value = await EvaluateAsync(param.Select).ConfigureAwait(false);
                        }
                        else if (param.Content != null)
                        {
                            // Use accumulator-isolated body eval so xsl:sequence emitting typed
                            // items (doc-nodes, elements, etc.) is preserved instead of being
                            // serialized to text and rebound as XsUntypedAtomic. Found in Docbook
                            // TNG where xsl:next-iteration with-param="document" rebinds $document
                            // each iteration via `<xsl:sequence select="$next-result?output"/>`.
                            value = await EvaluateBodyContentToValueAsync(param.Content).ConfigureAwait(false);
                        }
                        else
                        {
                            value = null;
                        }
                        // Apply type coercion based on the matching xsl:param's as type
                        var matchingParam = instruction.Params.FirstOrDefault(p => p.Name == param.Name);
                        if (matchingParam?.As != null && value != null)
                        {
                            value = CoerceToType(value, matchingParam.As);
                        }
                        newValues.Add((param.Name, value));
                    }
                    // Now apply all new values
                    foreach (var (name, val) in newValues)
                    {
                        SetVariable(name, val);
                    }
                }
                finally
                {
                    PopCurrentItem();
                    PopContextItem();
                }
            }

            // On completion - only runs if iteration completed normally (not via xsl:break)
            // Context item/position/size are undefined in on-completion (XPDY0002)
            if (!brokeOut && instruction.OnCompletion != null)
            {
                PushContextItem(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus, 0, 0);
                try
                {
                    await instruction.OnCompletion.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    PopContextItem();
                }
            }
        }
        finally
        {
            PopScope();
        }
    }


    public override async ValueTask TryAsync(XsltTry instruction)
    {
        // Checkpoint the output by LENGTH, not by copying the whole buffer. Nested element
        // constructors (LRE, xsl:element) always append their content PAST this point and
        // truncate their own suffix on completion (see EvaluateBodyContentToValueAsync's
        // `_output.Length = savedLen` pattern); an exception mid-assembly leaves partial
        // content appended beyond savedOutputLen but never shrinks the buffer below it.
        // So on the rollback path, truncating back to savedOutputLen discards exactly the
        // try body's partial output while preserving the outer content — and is O(1)
        // rather than O(current-output-length). The former `_output.ToString()` snapshot
        // was O(N) per try; inside a streaming for-each that accumulates all matches into
        // _output, that made an N-item pass O(N^2) (each try copied the whole running
        // buffer). See sf-boolean-107 / sf-not-107.
        var savedOutputLen = _output.Length;
        var savedAttrStackDepth = _collectedAttributesStack.Count;
        // Safety net for scopes an INTERRUPTED nested construct pushed and never popped
        // (a for-each inside the try that threw mid-iteration). The try body's own scope is
        // handled by the push/pop around ExecuteAsync below; this catches the rest, the same
        // shape as savedAttrStackDepth above.
        var savedScopeDepth = _scopes.Count;
        // A handler must not read a location left behind by an earlier, already-handled failure.
        var savedExpressionErrorLocation = _lastExpressionErrorLocation;
        _lastExpressionErrorLocation = null;
        try
        {
            if (instruction.SelectExpression != null)
            {
                // Evaluate the select expression and handle the result.
                // Must respect _sequenceAccumulator (like xsl:sequence) so typed values
                // (e.g. boolean false) are preserved, not serialized to string "false".
                var result = await EvaluateAsync(instruction.SelectExpression).ConfigureAwait(false);
                if (result != null && _sequenceAccumulator != null)
                {
                    if (result is object?[] tryArr)
                    {
                        foreach (var item in tryArr)
                            AppendToSeqAccumulator(item);
                    }
                    else if (result is System.Collections.IEnumerable tryEnum && result is not string && result is not XdmNode && result is not IDictionary<object, object?>)
                    {
                        foreach (var item in tryEnum)
                            AppendToSeqAccumulator(item);
                    }
                    else
                    {
                        AppendToSeqAccumulator(result);
                    }
                }
                else
                {
                    OutputValue(result);
                }
            }
            else if (instruction.Body != null)
            {
                // The body gets its own scope. Without one its xsl:variable declarations
                // land in the CALLER's scope, so an inner $pi overwrites an outer $pi that
                // xsl:catch (and anything after </xsl:try>) must still see. Scopes are
                // parent-linked, so the body still reads outer bindings; it just cannot
                // clobber them. Popped in finally so the handler below — which runs while
                // the exception is still unwinding — evaluates in the outer scope.
                PushScope();
                try
                {
                    await instruction.Body.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    PopScope();
                }
            }
        }
        // A deferred GLOBAL variable error is declined here: it notionally occurred before the
        // transformation began, so xsl:try must not catch it however late the lazy rethrow lands.
        catch (Exception ex) when (!DeferredGlobalError.IsMarked(ex)
            && ex is XsltException or XQuery.Execution.XQueryRuntimeException or XQuery.Functions.XQueryException or InvalidOperationException or FormatException or OverflowException or ArgumentException or NullReferenceException or ArithmeticException)
        {
            // Discard output produced during failed try block (rollback-output behavior).
            // Restore from snapshot because _output may have been replaced by nested
            // element constructors whose assembly code was skipped by the exception.
            if (instruction.Rollback)
            {
                // O(1) truncation: discard everything the (failed) try body appended,
                // leaving the outer content intact.
                if (_output.Length > savedOutputLen)
                    _output.Length = savedOutputLen;
                // Pop any _collectedAttributesStack entries pushed by interrupted element constructors
                while (_collectedAttributesStack.Count > savedAttrStackDepth)
                    _collectedAttributesStack.Pop();
                // Drop scopes the interrupted body opened, so xsl:catch evaluates against the
                // scope that was current before xsl:try — not against the body's declarations.
                while (_scopes.Count > savedScopeDepth)
                    PopScope();
            }
            else if (_output.Length > savedOutputLen)
            {
                // XTDE3530: When rollback-output="no" and output has been committed,
                // recovery is not possible — the output state cannot be rolled back.
                throw Error("XTDE3530: Recovery from a dynamic error is not possible because rollback-output='no' and output has already been written");
            }

            var errorCode = ex is XQuery.Execution.XQueryRuntimeException xqEx ? xqEx.ErrorCode
                : ex is XQuery.Functions.XQueryException xqFnEx ? xqFnEx.ErrorCode
                : ExtractErrorCode(ex.Message) ?? "XSLT0000";
            // Determine error namespace: standard error codes (FO/XP/XT/XQ/SE prefix + 4 digits)
            // are in the standard error namespace; user-defined codes are in no namespace
            var isStandardError = errorCode.Length >= 8
                && char.IsUpper(errorCode[0]) && char.IsUpper(errorCode[1])
                && char.IsLetterOrDigit(errorCode[2]) && char.IsLetterOrDigit(errorCode[3])
                && errorCode[4..].All(char.IsDigit);

            // Find matching catch
            foreach (var @catch in instruction.Catches)
            {
                // Check if error matches — per XSLT spec, namespace must match
                var matches = @catch.Errors.Count == 0; // Empty = catch all
                if (!matches && @catch.Errors.Any(e =>
                {
                    if (e.LocalName == "*" && (e.Namespace == NamespaceId.None || e.Prefix == null))
                        return true; // * or Q{ns}* wildcard
                    if (e.LocalName != errorCode)
                        return false;
                    // Wildcard prefix *:code matches any namespace
                    if (e.Prefix == "*")
                        return true;
                    // Namespace check: standard errors are in the error namespace;
                    // user-defined errors (from fn:error with custom QName) are in no namespace
                    if (isStandardError)
                    {
                        // Standard error — catch must have err: prefix or be in the error namespace
                        return e.Prefix == "err" || e.Namespace != NamespaceId.None;
                    }
                    // User-defined error — catch must also have no namespace (no prefix)
                    return e.Namespace == NamespaceId.None && e.Prefix == null;
                }))
                {
                    matches = true;
                }

                if (matches)
                {
                    PushScope();
                    // Set $err:code, $err:description, $err:value, $err:module, $err:line-number, $err:column-number
                    // Register with both NamespaceId (for prefix-resolved references like $err:code)
                    // and ExpandedNamespace (for EQName references like $Q{uri}code).
                    // QName is a record struct — Dictionary equality uses all fields — so we register
                    // under both forms for correct lookup.
                    const string errUri = "http://www.w3.org/2005/xqt-errors";
                    var errNs = StylesheetParser.ResolveNamespaceUri(errUri);
                    // $err:description is fn:error()'s second argument verbatim (XSLT 3.0 §13.3),
                    // NOT our diagnostic rendering of it. ex.Message is the decorated form —
                    // EvaluateAsync prefixes "[module:line] " and appends the expression snippet
                    // so a failing XPath can be located. That decoration is for humans reading a
                    // stack trace; a stylesheet comparing $err:description against the string it
                    // passed to fn:error() must see the string it passed.
                    var errDescription = ExtractErrorDescription(ex);
                    SetVariable(new QName(errNs, "description", "err"), errDescription);
                    SetVariable(new QName(NamespaceId.None, "description", "") { ExpandedNamespace = errUri }, errDescription);
                    // A standard error code is a QName in the XQT errors namespace, so it carries
                    // the URI as well as the interned id — without it
                    // fn:namespace-uri-from-QName($err:code) is empty and the code can never equal
                    // QName('http://www.w3.org/2005/xqt-errors', 'XTTE0570'), which is how a
                    // stylesheet (and XSpec) asserts on an error code.
                    //
                    // Attaching the URI used to change how the value PRINTED — the XQuery
                    // stringifier fell through to QName.ToString() and rendered "Q{uri}local"
                    // instead of the lexical "err:XTTE0570". That is fixed at its source in
                    // PhoenixmlDb.XQuery (fn:string on an xs:QName now yields the lexical form per
                    // XPath 3.1 §19.2), so both properties hold at once. Requires the companion
                    // XQuery release; with an older pin the rendering regresses.
                    var errCodeValue = isStandardError
                        ? (object)new QName(errNs, errorCode, "err") { ExpandedNamespace = errUri }
                        : (object)new QName(NamespaceId.None, errorCode);
                    SetVariable(new QName(errNs, "code", "err"), errCodeValue);
                    SetVariable(new QName(NamespaceId.None, "code", "") { ExpandedNamespace = errUri }, errCodeValue);
                    // $err:value is fn:error()'s third argument (the error object). It rides on
                    // the exception as ErrorValue. Walk InnerException: the diagnostic decorator
                    // in EvaluateAsync re-throws a fresh XQueryException to attach the source
                    // module and expression snippet, and a wrapper that forgets to copy
                    // ErrorValue would otherwise silently turn the error object into (). The
                    // walk makes the binding independent of how many times we have re-wrapped.
                    var errValue = ExtractErrorValue(ex);
                    SetVariable(new QName(errNs, "value", "err"), errValue);
                    SetVariable(new QName(NamespaceId.None, "value", "") { ExpandedNamespace = errUri }, errValue);
                    // XsltException carries its own location; an error raised by fn:error()
                    // arrives as an XQueryException and has none, so fall back to the location
                    // the evaluator recorded for the expression that raised it.
                    var errLocation = (ex as XsltException)?.Location ?? _lastExpressionErrorLocation;
                    // Prefer the module the expression was written in — for an error raised
                    // inside an imported stylesheet that is the imported module, not the
                    // principal one (XSLT 3.0 §13.3).
                    var errModule = errLocation is { Module.Length: > 0 } locatedModule
                        ? locatedModule.Module!
                        : errLocation != null ? XsltTransformEngine.UriString(_stylesheet.BaseUri) ?? "" : "";
                    var errLine = errLocation?.Line ?? 0;
                    var errColumn = errLocation?.Column ?? 0;
                    SetVariable(new QName(errNs, "module", "err"), errModule.Length > 0 ? errModule : null);
                    SetVariable(new QName(NamespaceId.None, "module", "") { ExpandedNamespace = errUri }, errModule.Length > 0 ? errModule : null);
                    SetVariable(new QName(errNs, "line-number", "err"), errLine > 0 ? (object)errLine : null);
                    SetVariable(new QName(NamespaceId.None, "line-number", "") { ExpandedNamespace = errUri }, errLine > 0 ? (object)errLine : null);
                    SetVariable(new QName(errNs, "column-number", "err"), errColumn > 0 ? (object)errColumn : null);
                    SetVariable(new QName(NamespaceId.None, "column-number", "") { ExpandedNamespace = errUri }, errColumn > 0 ? (object)errColumn : null);

                    try
                    {
                        if (@catch.SelectExpression != null)
                        {
                            var result = await EvaluateAsync(@catch.SelectExpression).ConfigureAwait(false);
                            if (result != null && _sequenceAccumulator != null)
                            {
                                if (result is object?[] catchArr)
                                {
                                    foreach (var item in catchArr)
                                        AppendToSeqAccumulator(item);
                                }
                                else if (result is System.Collections.IEnumerable catchEnum && result is not string && result is not XdmNode && result is not IDictionary<object, object?>)
                                {
                                    foreach (var item in catchEnum)
                                        AppendToSeqAccumulator(item);
                                }
                                else
                                {
                                    AppendToSeqAccumulator(result);
                                }
                            }
                            else
                            {
                                OutputValue(result);
                            }
                        }
                        else if (@catch.Body != null)
                        {
                            await @catch.Body.ExecuteAsync(this).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        PopScope();
                    }

                    return;
                }
            }

            throw; // Re-throw if no catch matches
        }
        finally
        {
            // Restore the enclosing try's value, so a nested handler cannot leave its own
            // error's location visible to an outer handler whose failure carried none.
            _lastExpressionErrorLocation = savedExpressionErrorLocation;
        }
    }


    /// <summary>
    /// Fast-path evaluation for simple comparisons like position() = N, last() gt 1, etc.
    /// Returns null if the expression can't be evaluated via fast path.
    /// </summary>
    private object? TryEvaluateSimpleComparison(BinaryExpression be)
    {
        var leftVal = TryEvaluateSimpleOperand(be.Left);
        var rightVal = TryEvaluateSimpleOperand(be.Right);
        if (leftVal is null || rightVal is null) return null;

        var l = (long)leftVal;
        var r = (long)rightVal;
        return be.Operator switch
        {
            BinaryOperator.Equal or BinaryOperator.GeneralEqual => l == r,
            BinaryOperator.NotEqual or BinaryOperator.GeneralNotEqual => l != r,
            BinaryOperator.LessThan or BinaryOperator.GeneralLessThan => l < r,
            BinaryOperator.LessOrEqual or BinaryOperator.GeneralLessOrEqual => l <= r,
            BinaryOperator.GreaterThan or BinaryOperator.GeneralGreaterThan => l > r,
            BinaryOperator.GreaterOrEqual or BinaryOperator.GeneralGreaterOrEqual => l >= r,
            _ => null
        };
    }


    private long? TryEvaluateSimpleOperand(XQueryExpression expr) => expr switch
    {
        IntegerLiteral il when il.Value is long lv => lv,
        FunctionCallExpression { Arguments.Count: 0 } fce
            when fce.Name.LocalName == "position" && (fce.Name.Namespace == NamespaceId.None || fce.Name.Namespace == NamespaceId.Fn)
            => Position,
        FunctionCallExpression { Arguments.Count: 0 } fce2
            when fce2.Name.LocalName == "last" && (fce2.Name.Namespace == NamespaceId.None || fce2.Name.Namespace == NamespaceId.Fn)
            => Last,
        _ => null
    };


    /// <summary>
    /// Streaming whole-subtree copy-of forward for the document-level fresh-reader case
    /// (<c>xsl:copy-of select="child::node()"</c> inside a streamable
    /// <c>xsl:source-document</c> body). The active streaming reader is still positioned
    /// before the document element (<see cref="System.Xml.ReadState.Initial"/>); walk it
    /// forward, materialize each top-level element subtree into the node store via
    /// <see cref="StreamingSubtreeMaterializer"/> (which leaves the reader on the subtree's
    /// EndElement), and serialize it into <c>_output</c> with copy-of semantics — reusing
    /// the same <see cref="SerializeNode"/> path a materialized element result would take.
    /// Top-level comments / processing-instructions are forwarded verbatim (child::node()
    /// selects them too); whitespace-only text between top-level nodes is dropped, matching
    /// the streamed-input strip-space default the subtree materializer applies.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the forward ran (the reader was fresh and this call consumed it);
    /// <c>false</c> when the reader was already positioned, so the caller must fall through
    /// to the normal evaluate-and-serialize path.
    /// </returns>
    private async ValueTask<bool> TryStreamingCopyOfDocumentChildrenAsync(bool copyNamespaces)
    {
        var reader = _activeStreamingReader!;
        // Only the document-level, unpositioned-reader case is handled here. Once the
        // reader has advanced (e.g. a striding for-each is mid-stream), the mapping from
        // child::node() to reader events is no longer "the whole remaining input", so we
        // decline and let the normal path run.
        if (reader.ReadState != System.Xml.ReadState.Initial)
            return false;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var elem = StreamingSubtreeMaterializer.Materialize(reader, _nodeStore!, new DocumentId(0));
                    if (elem != null)
                        SerializeNode(elem, copyNamespaces, faithfulNamespaces: false);
                    break;
                }
                case XmlNodeType.Comment:
                    // Note: raw reader value is not passed through EscapeCommentValue at this
                    // call site (matches pre-existing behavior) — use RawText to keep
                    // byte-identical output.
                    _sink.RawText("<!--" + reader.Value + "-->");
                    break;
                case XmlNodeType.ProcessingInstruction:
                    // Note: raw reader value is not passed through EscapePIValue at this call
                    // site (matches pre-existing behavior) — use RawText, not
                    // _sink.ProcessingInstruction, to keep byte-identical output.
                    _sink.RawText("<?" + reader.Name + (string.IsNullOrEmpty(reader.Value) ? "" : " " + reader.Value) + "?>");
                    break;
                // Document-level whitespace text is stripped (streamed-input strip-space
                // default); other node kinds cannot appear as document children.
            }
        }
        return true;
    }


    /// <summary>
    /// Extracts the element nodes from an xsl:copy-of result. Returns false unless the
    /// result is exclusively element node(s) (single element or a sequence of elements).
    /// Mixed or non-element results fall back to the normal serialization path so this
    /// base-URI-preserving branch never changes behavior for other node/atomic kinds.
    /// </summary>
    private static bool TryGetCopyOfElements(object? result, out List<XdmElement>? elements)
    {
        elements = null;
        if (result is XdmElement single)
        {
            elements = new List<XdmElement> { single };
            return true;
        }
        IEnumerable<object?>? items = result switch
        {
            object?[] arr => arr,
            IEnumerable<object?> seq when result is not string => seq,
            _ => null
        };
        if (items == null)
            return false;
        var collected = new List<XdmElement>();
        foreach (var item in items)
        {
            if (item is XdmElement e)
                collected.Add(e);
            else
                return false; // not a pure element sequence — defer to normal path
        }
        if (collected.Count == 0)
            return false;
        elements = collected;
        return true;
    }


    /// <summary>
    /// SP-C targeted: extracts an ordered node sequence from an xsl:copy-of result, admitting a
    /// MIXED sequence of element / text / comment / processing-instruction nodes (copy-1220's
    /// <c>wrapper/child::node()</c>). Returns false if any item is not one of those kinds
    /// (attributes, document nodes, atomics need other handling) so those results fall through to
    /// the unchanged serialization path. Used only by the untyped-RTF-flip divergent routing.
    /// </summary>
    private static bool TryGetCopyOfNodes(object? result, out List<XdmNode>? nodes)
    {
        nodes = null;
        static bool IsRoutable(object? o) =>
            o is XdmElement or XdmText or XdmComment or XdmProcessingInstruction;
        if (IsRoutable(result))
        {
            nodes = new List<XdmNode> { (XdmNode)result! };
            return true;
        }
        IEnumerable<object?>? items = result switch
        {
            object?[] arr => arr,
            IEnumerable<object?> seq when result is not string => seq,
            _ => null
        };
        if (items == null)
            return false;
        var collected = new List<XdmNode>();
        foreach (var item in items)
        {
            if (!IsRoutable(item))
                return false;
            collected.Add((XdmNode)item!);
        }
        if (collected.Count == 0)
            return false;
        nodes = collected;
        return true;
    }


    /// <summary>
    /// SP-C copy-0612 family: like <see cref="TryGetCopyOfNodes"/>, but first UNWRAPS a single
    /// document node (or an untyped-RTF whose node build is available) into its element / text /
    /// comment / PI children — <c>xsl:copy-of select="$doc"</c> copies the document's children, not
    /// a document node. Used only by the inherit-namespaces="no" divergent copy-of routing, so the
    /// grafted document content (copy-0612's <c>copy-of select="$inner"</c>) routes the same way an
    /// element selection (copy-0620's <c>$inner//*:r</c>) does. Returns false for anything else so
    /// those results fall through to the unchanged paths.
    /// </summary>
    private bool TryGetCopyOfNodesForDivergent(object? result, out List<XdmNode>? nodes)
    {
        nodes = null;
        XdmDocument? doc = result switch
        {
            XdmDocument d => d,
            ResultTreeFragment rtf => ParseResultTreeFragment(rtf),
            _ => null,
        };
        if (doc != null)
        {
            if (_nodeStore == null)
                return false;
            var collected = new List<XdmNode>();
            foreach (var child in _nodeStore.GetChildren(doc))
            {
                if (child is XdmElement or XdmText or XdmComment or XdmProcessingInstruction)
                    collected.Add(child);
                else
                    return false;
            }
            if (collected.Count == 0)
                return false;
            nodes = collected;
            return true;
        }
        return TryGetCopyOfNodes(result, out nodes);
    }


    /// <summary>
    /// XSLT 3.0 §11.9.1 write-side: serialize each source element, reparse it into an
    /// orphaned <see cref="XdmElement"/>, stamp <see cref="XdmNode.CopySourceBaseUri"/>
    /// from the source's computed base URI, and append to the active sequence accumulator.
    /// Returns true only if every element was successfully materialized with a non-empty
    /// source base URI; otherwise returns false (leaving the accumulator untouched) so
    /// the caller runs the unchanged text-serialization path. Mirrors the xsl:copy
    /// set-site in <see cref="CopyAsync"/>.
    /// </summary>
    private ValueTask<bool> TryAccumulateCopyOfElementsWithBaseUriAsync(List<XdmElement> sourceElems, bool copyNamespaces)
    {
        // First pass: compute each source's base URI. Bail (return false) if any is
        // null/empty so we never partially accumulate then fall through to text.
        var baseUris = new string?[sourceElems.Count];
        for (int i = 0; i < sourceElems.Count; i++)
        {
            var bu = ComputeSourceBaseUri(sourceElems[i]);
            if (string.IsNullOrEmpty(bu))
                return new ValueTask<bool>(false);
            baseUris[i] = bu;
        }

        var staged = new List<XdmElement>(sourceElems.Count);
        for (int i = 0; i < sourceElems.Count; i++)
        {
            // Deep-clone the source element directly into the node store (node-model copy),
            // preserving ALL in-scope namespaces — including those an ancestor already declared,
            // which the old serialize→reparse path incorrectly dropped ("in-scope namespaces of a
            // copied node are correct", copy-3702 / copy-1221). Bail to text on any non-element.
            if (_nodeStore is null)
                return new ValueTask<bool>(false);
            var cloneId = CloneSubtreeDeep(sourceElems[i], null, copyNamespaces);
            if (_nodeStore.GetNode(cloneId) is not XdmElement copiedRoot)
                return new ValueTask<bool>(false);
            // Never overwrite a non-null value (e.g. a nested copy already stamped).
            copiedRoot.CopySourceBaseUri ??= baseUris[i];
            staged.Add(copiedRoot);
        }

        foreach (var copiedRoot in staged)
            AppendToSeqAccumulator(copiedRoot);
        return new ValueTask<bool>(true);
    }


    /// <summary>
    /// EMIT side of temp-tree base-URI preservation. When <c>_tempTreeSerializeDepth &gt; 0</c>
    /// (i.e. serializing into a buffer that will be reparsed) and <paramref name="elem"/>'s
    /// computed source base URI is non-empty and differs from the enclosing serialization
    /// context (<paramref name="context"/>), appends the sentinel namespace declaration and
    /// <c>_pxbase_:base</c> attribute to the just-written start tag and updates
    /// <paramref name="context"/> to that base URI so descendants of this subtree inherit it
    /// and do not re-emit. Does nothing during final-output serialization, so the sentinel
    /// is structurally impossible to leak into the transform result. The reparse step
    /// (<see cref="ReadXdmElementFromReader"/> / <see cref="XsltTransformEngine.ConvertXmlNode"/>)
    /// always strips it.
    /// </summary>
    private void TryEmitBaseSentinel(XdmElement elem, ref string? context)
        => TryEmitBaseSentinel(elem, ref context, force: false);


    // SP-C copy-0612 family: with force=true, emit even when _tempTreeSerializeDepth is 0. The
    // untyped-RTF flip seam does NOT raise the temp-tree depth (a blanket raise perturbs base-uri
    // resolution across the body — regressing fn/base-uri), but when the flip installs the
    // constructor an xsl:copy of a SOURCE element takes the TcOpenElement path instead of the
    // sequence-accumulator base-URI path, so its serialized CONTENT (the reparse fallback used
    // when the flip is byte-parity-vetoed) would drop the base sentinel and lose base-uri() on the
    // copy. Forcing the sentinel HERE — only at the xsl:copy site, only under the flip — restores
    // the content-reparse base URI without the global depth-raise side effects.
    private void TryEmitBaseSentinel(XdmElement elem, ref string? context, bool force)
    {
        if ((_tempTreeSerializeDepth <= 0 && !force) || _nodeStore == null)
            return;
        // Prefer an already-preserved source base URI (set by an earlier copy that hasn't
        // yet crossed a text boundary); otherwise compute it from the source tree position.
        var srcBase = elem.CopySourceBaseUri ?? ComputeSourceBaseUri(elem);
        if (string.IsNullOrEmpty(srcBase) || string.Equals(srcBase, context, StringComparison.Ordinal))
            return;
        _sink.Namespace(XsltTransformEngine.BaseSentinelPrefix, XsltTransformEngine.BaseSentinelNs);
        _sink.Attribute(
            $"{XsltTransformEngine.BaseSentinelPrefix}:{XsltTransformEngine.BaseSentinelLocalName}",
            srcBase);
        context = srcBase;
    }


    public override async ValueTask SequenceAsync(XsltSequence instruction)
    {
        // SM-ctx streaming handoff: a consuming simple-map LEFT ! RIGHT select whose
        // RIGHT was registered as an inline-driven subscription streams in place.
        if (await TryHandoffSimpleMapContextStreamingAsync(instruction.Select).ConfigureAwait(false))
            return;

        if (instruction.Select != null)
        {
            var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
            if (result != null)
            {
                // If we're collecting sequence items (variable body with as="type *"),
                // add individual items rather than serializing to output
                if (_sequenceAccumulator != null)
                {
                    if (result is object?[] arr)
                    {
                        foreach (var item in arr)
                            AppendToSeqAccumulator(item);
                    }
                    else if (result is System.Collections.IEnumerable enumerable && result is not string && result is not XdmNode && result is not IDictionary<object, object?> && result is not List<object?>)
                    {
                        foreach (var item in enumerable)
                            AppendToSeqAccumulator(item);
                    }
                    else
                    {
                        AppendToSeqAccumulator(result);
                    }
                }
                else
                {
                    // xsl:sequence produces atomic values — strings from XPath expressions
                    // are xs:string atomic values that need space separation per XSLT 3.0 spec 5.7.2.
                    if (result is string str)
                    {
                        // In where-populated scopes (Phase 2 of on-non-empty), empty strings
                        // are insignificant (XSLT 3.0 §11.4). Skip emitting separators for them
                        // so zero-length items don't produce whitespace in the final output.
                        if (str.Length == 0 && _wherePopulatedDepth > 0)
                        {
                            // Don't emit separator or content, but preserve atomic state
                        }
                        else
                        {
                            // Top-level item-separator mode separates every adjacent item uniformly
                            // (including after a node); otherwise fall back to the legacy
                            // "separate adjacent atomic values" rule.
                            if (InTopLevelItemSeparatorSequence)
                            {
                                var wroteTopSep = _topLevelItemEmitted;
                                TryWriteTopLevelItemSeparator();
                                if (wroteTopSep && _contentTrackingStack.Count > 0)
                                    _separatorCharsWritten += _itemSeparatorOverride!.Length;
                            }
                            else if (_lastResultWasAtomic && _attributeContentDepth == 0)
                            {
                                var sep = _itemSeparatorOverride ?? " ";
                                WriteText(sep, false);
                                // Track separator chars so EndContentTracking can distinguish
                                // separator-only growth from significant content growth.
                                if (_contentTrackingStack.Count > 0)
                                    _separatorCharsWritten += sep.Length;
                            }
                            WriteText(str, false);
                            _lastResultWasAtomic = true;
                        }
                    }
                    else
                    {
                        SerializeResult(result!);
                    }
                }
            }
        }
        else if (instruction.Content != null)
        {
            await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
        }
    }


    /// <summary>
    /// Attempts to format a number using Unicode number sequences (circled digits, parenthesized digits, etc.)
    /// Returns null if the token is not a Unicode number sequence or the number is out of range.
    /// </summary>
    private static string? TryFormatUnicodeNumber(long number, string token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        // Handle SMP characters (surrogate pairs)
        int codePoint;
        if (char.IsHighSurrogate(token[0]) && token.Length > 1 && char.IsLowSurrogate(token[1]))
        {
            codePoint = char.ConvertToUtf32(token[0], token[1]);
        }
        else
        {
            codePoint = token[0];
        }

        return TryFormatUnicodeNumberByCodePoint(number, codePoint);
    }


    private static string? TryFormatUnicodeNumberByCodePoint(long number, int codePoint)
    {
        // Circled digits: ① (U+2460) through ⑳ (U+2473) for 1-20
        // Circled zero: ⓪ (U+24EA) - separate from main sequence
        // Circled 21-35: ㉑ (U+3251) through ㉟ (U+325F)
        // Circled 36-50: ㊱ (U+32B1) through ㊿ (U+32BF)
        if (codePoint >= 0x2460 && codePoint <= 0x2473)
        {
            if (number == 0)
                return "\u24EA"; // ⓪
            if (number >= 1 && number <= 20)
                return ((char)(0x2460 + number - 1)).ToString();
            if (number >= 21 && number <= 35)
                return ((char)(0x3251 + number - 21)).ToString();
            if (number >= 36 && number <= 50)
                return ((char)(0x32B1 + number - 36)).ToString();
            return number.ToString(CultureInfo.InvariantCulture); // Fallback for out of range
        }

        // Circled zero by itself (⓪ U+24EA)
        if (codePoint == 0x24EA)
        {
            if (number == 0)
                return "\u24EA";
            if (number >= 1 && number <= 20)
                return ((char)(0x2460 + number - 1)).ToString();
            if (number >= 21 && number <= 35)
                return ((char)(0x3251 + number - 21)).ToString();
            if (number >= 36 && number <= 50)
                return ((char)(0x32B1 + number - 36)).ToString();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Parenthesized digits: ⑴ (U+2474) through ⒇ (U+2487) for 1-20
        if (codePoint >= 0x2474 && codePoint <= 0x2487)
        {
            if (number == 0)
                return "0"; // No parenthesized zero exists
            if (number >= 1 && number <= 20)
                return ((char)(0x2474 + number - 1)).ToString();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Full stop digits: ⒈ (U+2488) through ⒛ (U+249B) for 1-20
        // Full stop zero: 🄀 (U+1F100) in SMP
        if (codePoint >= 0x2488 && codePoint <= 0x249B)
        {
            if (number == 0)
                return char.ConvertFromUtf32(0x1F100); // 🄀
            if (number >= 1 && number <= 20)
                return ((char)(0x2488 + number - 1)).ToString();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Full stop zero (🄀 U+1F100) as format token
        if (codePoint == 0x1F100)
        {
            if (number == 0)
                return char.ConvertFromUtf32(0x1F100); // 🄀
            if (number >= 1 && number <= 20)
                return ((char)(0x2488 + number - 1)).ToString();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Dingbat negative circled digits: ❶ (U+2776) to ❿ (U+277F) for 1-10
        // Dingbat negative circled 11-20: ⓫ (U+24EB) to ⓴ (U+24F4)
        // Dingbat negative circled zero: ⓿ (U+24FF)
        if (codePoint >= 0x2776 && codePoint <= 0x277F)
        {
            if (number == 0)
                return "\u24FF"; // ⓿
            if (number >= 1 && number <= 10)
                return ((char)(0x2776 + number - 1)).ToString();
            if (number >= 11 && number <= 20)
                return ((char)(0x24EB + number - 11)).ToString();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Negative circled zero (⓿ U+24FF) as format token
        if (codePoint == 0x24FF)
        {
            if (number == 0)
                return "\u24FF"; // ⓿
            if (number >= 1 && number <= 10)
                return ((char)(0x2776 + number - 1)).ToString();
            if (number >= 11 && number <= 20)
                return ((char)(0x24EB + number - 11)).ToString();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Dingbat negative circled 11-20: ⓫ (U+24EB) to ⓴ (U+24F4)
        if (codePoint >= 0x24EB && codePoint <= 0x24F4)
        {
            if (number == 0)
                return "\u24FF"; // ⓿
            if (number >= 1 && number <= 10)
                return ((char)(0x2776 + number - 1)).ToString();
            if (number >= 11 && number <= 20)
                return ((char)(0x24EB + number - 11)).ToString();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Double circled digits: ⓵ (U+24F5) to ⓾ (U+24FE) for 1-10
        if (codePoint >= 0x24F5 && codePoint <= 0x24FE)
        {
            if (number >= 1 && number <= 10)
                return ((char)(0x24F5 + number - 1)).ToString();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Dingbat circled sans-serif digits: ➀ (U+2780) to ➉ (U+2789) for 1-10
        // Dingbat circled sans-serif zero: 🄋 (U+1F10B) in SMP
        if (codePoint >= 0x2780 && codePoint <= 0x2789)
        {
            if (number == 0)
                return char.ConvertFromUtf32(0x1F10B); // 🄋
            if (number >= 1 && number <= 10)
                return ((char)(0x2780 + number - 1)).ToString();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Dingbat circled sans-serif zero (🄋 U+1F10B) as format token
        if (codePoint == 0x1F10B)
        {
            if (number == 0)
                return char.ConvertFromUtf32(0x1F10B); // 🄋
            if (number >= 1 && number <= 10)
                return ((char)(0x2780 + number - 1)).ToString();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Dingbat negative circled sans-serif digits: ➊ (U+278A) to ➓ (U+2793) for 1-10
        // Dingbat negative circled sans-serif zero: 🄌 (U+1F10C) in SMP
        if (codePoint >= 0x278A && codePoint <= 0x2793)
        {
            if (number == 0)
                return char.ConvertFromUtf32(0x1F10C); // 🄌
            if (number >= 1 && number <= 10)
                return ((char)(0x278A + number - 1)).ToString();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Dingbat negative circled sans-serif zero (🄌 U+1F10C) as format token
        if (codePoint == 0x1F10C)
        {
            if (number == 0)
                return char.ConvertFromUtf32(0x1F10C); // 🄌
            if (number >= 1 && number <= 10)
                return ((char)(0x278A + number - 1)).ToString();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // DIGIT COMMA sequence: 🄁 (U+1F101) to 🄊 (U+1F10A) for 0-9
        if (codePoint >= 0x1F101 && codePoint <= 0x1F10A)
        {
            if (number >= 0 && number <= 9)
                return char.ConvertFromUtf32(0x1F101 + (int)number);
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Parenthesized Ideograph One to Ten: ㈠ (U+3220) to ㈩ (U+3229) for 1-10
        if (codePoint >= 0x3220 && codePoint <= 0x3229)
        {
            if (number >= 1 && number <= 10)
                return ((char)(0x3220 + number - 1)).ToString();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Circled Ideograph One to Ten: ㊀ (U+3280) to ㊉ (U+3289) for 1-10
        if (codePoint >= 0x3280 && codePoint <= 0x3289)
        {
            if (number >= 1 && number <= 10)
                return ((char)(0x3280 + number - 1)).ToString();
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Aegean Number One: 𐄇 (U+10107) — range 1-10 (Ten = U+10110)
        if (codePoint >= 0x10107 && codePoint <= 0x10110)
        {
            if (number >= 1 && number <= 10)
                return char.ConvertFromUtf32(0x10107 + (int)number - 1);
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Coptic Epact Digit One: 𐋡 (U+102E1) — range 1-10 (Ten = U+102EA)
        if (codePoint >= 0x102E1 && codePoint <= 0x102EA)
        {
            if (number >= 1 && number <= 10)
                return char.ConvertFromUtf32(0x102E1 + (int)number - 1);
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Rumi Digit One: 𐹠 (U+10E60) — range 1-10 (Ten = U+10E69)
        if (codePoint >= 0x10E60 && codePoint <= 0x10E69)
        {
            if (number >= 1 && number <= 10)
                return char.ConvertFromUtf32(0x10E60 + (int)number - 1);
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Brahmi Number One: 𑁒 (U+11052) — range 1-10 (Ten = U+1105B)
        if (codePoint >= 0x11052 && codePoint <= 0x1105B)
        {
            if (number >= 1 && number <= 10)
                return char.ConvertFromUtf32(0x11052 + (int)number - 1);
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Sinhala Archaic Digit One: 𑇡 (U+111E1) — range 1-10 (Ten = U+111EA)
        if (codePoint >= 0x111E1 && codePoint <= 0x111EA)
        {
            if (number >= 1 && number <= 10)
                return char.ConvertFromUtf32(0x111E1 + (int)number - 1);
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Counting Rod Unit Digit One: 𝍠 (U+1D360) — range 1-9
        if (codePoint >= 0x1D360 && codePoint <= 0x1D368)
        {
            if (number >= 1 && number <= 9)
                return char.ConvertFromUtf32(0x1D360 + (int)number - 1);
            return number.ToString(CultureInfo.InvariantCulture);
        }

        // Mende Kikakui Digit One: 𞣇 (U+1E8C7) — range 1-9
        if (codePoint >= 0x1E8C7 && codePoint <= 0x1E8CF)
        {
            if (number >= 1 && number <= 9)
                return char.ConvertFromUtf32(0x1E8C7 + (int)number - 1);
            return number.ToString(CultureInfo.InvariantCulture);
        }

        return null; // Not a recognized Unicode number sequence
    }


    public override void Break(XsltBreak instruction)
    {
        // Execute break's content/select before throwing — the BreakException signals
        // IterateAsync to skip on-completion. The select value contributes to the
        // iterate's result, mirroring xsl:sequence semantics: route through the
        // sequence accumulator when active so node identity / typed values survive
        // the iteration boundary (e.g. an iterate inside `as="element()?"` that
        // breaks with `select="."` must return the element, not its string value).
        // Found while transpiling Docbook xform-locale.xsl: fp:lookup-localization-template
        // returned the matched l:template element via xsl:break, but Break wrote its
        // string-value to the output buffer, so the function ended up returning a
        // whitespace string instead of the element.
        if (instruction.Select != null)
        {
            var result = EvaluateAsync(instruction.Select).AsTask().GetAwaiter().GetResult();
            if (result != null)
            {
                if (_sequenceAccumulator != null)
                {
                    if (result is object?[] arr)
                    {
                        foreach (var item in arr)
                            AppendToSeqAccumulator(item);
                    }
                    else if (result is System.Collections.IEnumerable enumerable
                        && result is not string && result is not XdmNode
                        && result is not IDictionary<object, object?>)
                    {
                        foreach (var item in enumerable)
                            AppendToSeqAccumulator(item);
                    }
                    else
                    {
                        AppendToSeqAccumulator(result);
                    }
                }
                else
                {
                    SerializeResult(result);
                }
            }
        }
        else if (instruction.Content != null)
        {
            instruction.Content.ExecuteAsync(this).AsTask().GetAwaiter().GetResult();
        }
        throw new BreakException();
    }


    public override async ValueTask ForkAsync(XsltFork instruction)
    {
        // Streaming-aware sequential execution: prongs execute one at a time.
        // True parallel fork would require thread-safe output handling per prong;
        // keeping sequential for safety while streaming is about input processing.
        foreach (var feg in instruction.ForEachGroups)
        {
            await ForEachGroupAsync(feg).ConfigureAwait(false);
        }

        foreach (var seq in instruction.Sequences)
        {
            await seq.ExecuteAsync(this).ConfigureAwait(false);
        }

        foreach (var rd in instruction.ResultDocuments)
        {
            await ResultDocumentAsync(rd).ConfigureAwait(false);
        }
    }


    private static bool TryParseDouble(string s, out double r) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out r);


    /// <summary>
    /// Attempts to coerce a string value to the specified atomic type (function conversion rules).
    /// Returns the coerced value, or null if conversion is not possible.
    /// </summary>
    private static object? TryCoerceStringToType(string value, ItemType targetType)
    {
        try
        {
            return targetType switch
            {
                ItemType.String => value,
                ItemType.UntypedAtomic => new Xdm.XsUntypedAtomic(value),
                ItemType.Integer => long.TryParse(value.Trim(), out var l) ? l : null,
                ItemType.Decimal => decimal.TryParse(value.Trim(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null,
                ItemType.Double => double.TryParse(value.Trim(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var dbl)
                    ? dbl
                    : value.Trim() is "INF" ? double.PositiveInfinity
                    : value.Trim() is "-INF" ? double.NegativeInfinity
                    : value.Trim() is "NaN" ? double.NaN
                    : null,
                ItemType.Float => float.TryParse(value.Trim(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var f)
                    ? f
                    : value.Trim() is "INF" ? float.PositiveInfinity
                    : value.Trim() is "-INF" ? float.NegativeInfinity
                    : value.Trim() is "NaN" ? float.NaN
                    : null,
                ItemType.Boolean => value.Trim() switch
                {
                    "true" or "1" => (object)true,
                    "false" or "0" => (object)false,
                    _ => null
                },
                ItemType.Duration => (object?)Xdm.XsDuration.Parse(value.Trim()),
                ItemType.YearMonthDuration => (object?)new Xdm.YearMonthDuration(
                    System.Xml.XmlConvert.ToTimeSpan(value.Trim()).Days / 30), // approximation
                ItemType.DayTimeDuration => (object?)System.Xml.XmlConvert.ToTimeSpan(value.Trim()),
                ItemType.Date => (object?)Xdm.XsDate.Parse(value.Trim()),
                ItemType.DateTime => (object?)Xdm.XsDateTime.Parse(value.Trim()),
                ItemType.Time => (object?)Xdm.XsTime.Parse(value.Trim()),
                ItemType.AnyUri => (object?)new Xdm.XsAnyUri(value.Trim()),
                ItemType.AnyAtomicType => value,
                _ => value // unknown atomic types: pass through as string
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _ = ex; // suppress CA1031: conversion failure is expected
            return null; // conversion failed
        }
    }


    /// <summary>
    /// Casts <paramref name="value"/> to an atomic type via the canonical XQuery caster,
    /// returning the original value if the cast is not applicable (mirrors the lenient
    /// numeric arms of <see cref="CoerceToType"/>).
    /// </summary>
    private static object? TryCastToAtomicLenient(object? value, ItemType itemType)
    {
        if (value == null)
            return null;
        try
        {
            return PhoenixmlDb.XQuery.Execution.TypeCastHelper.CastValue(value, itemType) ?? value;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            _ = ex; // conversion failure — leave value for the downstream type validator
            return value;
        }
    }


    /// <summary>Public alias used by the engine's global-variable initialization path.</summary>
    internal static object? TryCoerceStringToTypePublic(string value, ItemType targetType) =>
        TryCoerceStringToType(value, targetType);

}
