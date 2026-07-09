using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Pre-scans the instruction tree inside xsl:source-document to identify
/// consuming sub-expressions and generate StreamWatchers for them.
/// </summary>
internal sealed class StreamingExpressionScanner
{
    private readonly List<StreamWatcher> _watchers = [];
    private readonly List<ForEachSubscription> _subscriptions = [];

    // Lexical construction-nesting depth at the current scan position. Incremented
    // while descending into output-constructing instructions (LRE, xsl:element,
    // xsl:copy, xsl:if, xsl:choose). A for-each scanned at depth > 0 is lexically
    // WRAPPED in construction — it must run inside linear body execution and hand off
    // to the live reader at its lexical position so the surrounding construction is
    // emitted around its output. A for-each at depth 0 is a BARE top-of-body for-each
    // and keeps the forward-pass subscription-dispatch path unchanged.
    private int _constructionDepth;

    // Stack index of the context-root element that emitted watchers anchor against.
    // -1 for xsl:source-document (the document NODE, above element index 0); the
    // active reader depth for a deferred matched-template. Stamped onto every
    // StreamWatcher this scan emits.
    private int _contextRootDepth = -1;

    /// <summary>
    /// Result of <see cref="ScanWithSubscriptions(XsltSequenceConstructor?)"/> — watchers for consuming
    /// aggregations plus per-item subscriptions for streamable xsl:for-each.
    /// </summary>
    public readonly record struct ScanResult(
        IReadOnlyList<StreamWatcher> Watchers,
        IReadOnlyList<ForEachSubscription> Subscriptions);

    /// <summary>
    /// Scans the content body of an xsl:source-document instruction.
    /// Returns the list of watchers needed for the streaming pass.
    /// </summary>
    public IReadOnlyList<StreamWatcher> Scan(XsltSequenceConstructor? body)
        => ScanWithSubscriptions(body).Watchers;

    /// <summary>
    /// Scans the content body of a streaming instruction, anchoring every emitted
    /// watcher's path against <paramref name="contextRootDepth"/> (the absolute
    /// stack index of the watcher's context-root element in the streaming ancestor
    /// stack). The parameterless overload defaults to -1 (xsl:source-document, the
    /// document node); a deferred matched-template passes the active reader depth.
    /// </summary>
    public IReadOnlyList<StreamWatcher> Scan(XsltSequenceConstructor? body, int contextRootDepth)
        => ScanWithSubscriptions(body, contextRootDepth).Watchers;

    /// <summary>
    /// Scans the content body of an xsl:source-document instruction.
    /// Returns both the watchers for consuming aggregates and the
    /// subscriptions for streamable xsl:for-each instructions.
    /// </summary>
    public ScanResult ScanWithSubscriptions(XsltSequenceConstructor? body)
        => ScanWithSubscriptions(body, contextRootDepth: -1);

    /// <summary>
    /// As <see cref="ScanWithSubscriptions(XsltSequenceConstructor?)"/> but stamps
    /// every emitted watcher with <paramref name="contextRootDepth"/> so its path
    /// anchors to the runtime context root rather than floating to any depth.
    /// </summary>
    public ScanResult ScanWithSubscriptions(XsltSequenceConstructor? body, int contextRootDepth)
    {
        _watchers.Clear();
        _subscriptions.Clear();
        _constructionDepth = 0;
        _contextRootDepth = contextRootDepth;
        if (body != null)
        {
            ScanInstructions(body);
        }
        return new ScanResult(_watchers.ToArray(), _subscriptions.ToArray());
    }

    private void ScanInstruction(XsltInstruction instruction)
    {
        switch (instruction)
        {
            case XsltVariableInstruction variable:
                if (variable.Select != null)
                    ScanExpression(variable.Select);
                if (variable.Content != null)
                    ScanInstructions(variable.Content);
                break;

            case XsltValueOf valueOf:
                if (valueOf.Select != null)
                    ScanExpression(valueOf.Select);
                break;

            case XsltSequence seq:
                if (seq.Select != null)
                    ScanExpression(seq.Select);
                if (seq.Content != null)
                    ScanInstructions(seq.Content);
                break;

            case XsltCopyOf copyOf:
                ScanExpression(copyOf.Select);
                break;

            // xsl:for-each inside an xsl:source-document streamable body: if the
            // select is a simple absolute child-axis path and there are no sorts,
            // we can register a ForEachSubscription so the streaming processor
            // dispatches the body per matched start element. Anything more
            // complex falls back to the buffered path.
            case XsltForEach forEach:
                TryRegisterForEachSubscription(forEach);
                break;

            case XsltIf ifInsn:
                ScanExpression(ifInsn.Test);
                if (ifInsn.Then != null)
                {
                    _constructionDepth++;
                    ScanInstructions(ifInsn.Then);
                    _constructionDepth--;
                }
                break;

            case XsltChoose choose:
                foreach (var branch in choose.When)
                {
                    ScanExpression(branch.Test);
                    _constructionDepth++;
                    ScanInstructions(branch.Body);
                    _constructionDepth--;
                }
                if (choose.Otherwise != null)
                {
                    _constructionDepth++;
                    ScanInstructions(choose.Otherwise);
                    _constructionDepth--;
                }
                break;

            case XsltSequenceConstructor ctor:
                ScanInstructions(ctor);
                break;

            // Literal result elements have Content that may contain consuming instructions
            case XsltLiteralResultElement lre:
                _constructionDepth++;
                ScanInstructions(lre.Content);
                _constructionDepth--;
                break;

            // xsl:result-document redirects its body to a secondary output but is
            // otherwise a construction wrapper: a streamable xsl:for-each lexically
            // inside it (e.g. <xsl:result-document><out><xsl:for-each select="//X"/>
            // </out></xsl:result-document>, si-result-document-005) must be scanned so
            // its subscription registers. Descend at construction depth > 0 so the
            // for-each is treated as WRAPPED (InlineDriven) — it runs inside linear
            // body execution and hands off to the live reader at its lexical position,
            // with the surrounding <out> and the result-document redirect preserved.
            case XsltResultDocument resultDoc:
                _constructionDepth++;
                ScanInstructions(resultDoc.Content);
                _constructionDepth--;
                break;

            // xsl:copy in streaming mode keeps its open tag deferred; recurse into
            // its content so consuming aggregates nested inside surface to the watcher
            // registry (Martin's pattern: <xsl:copy><xsl:fork><count/></xsl:fork></xsl:copy>).
            case XsltCopy copy:
                if (copy.Content != null)
                {
                    _constructionDepth++;
                    ScanInstructions(copy.Content);
                    _constructionDepth--;
                }
                break;

            // xsl:fork: each prong (sequence body, for-each-group, result-document)
            // may contain consuming aggregates. Scanner walks them all so the
            // surrounding template body's deferred execution picks them up via
            // the watcher list; after children stream, prong bodies execute with
            // accumulated values substituted.
            case XsltFork fork:
                foreach (var seq in fork.Sequences)
                    ScanInstructions(seq);
                foreach (var feg in fork.ForEachGroups)
                {
                    if (feg.Body != null) ScanInstructions(feg.Body);
                }
                foreach (var rd in fork.ResultDocuments)
                {
                    if (rd.Content != null) ScanInstructions(rd.Content);
                }
                break;

            // xsl:where-populated / xsl:on-empty / xsl:on-non-empty have
            // conditional execution semantics (the body only fires depending on
            // whether sibling content is empty/non-empty). The streaming-pass
            // dispatch for ForEachSubscription is unconditional, so registering
            // a subscription for a for-each inside these wrappers would break
            // the gate. Skip descending here entirely — the for-each (and any
            // consuming aggregates inside) fall back to the buffered execution
            // path that honours the wrapper's semantics.
            case XsltWherePopulated:
            case XsltOnEmpty:
            case XsltOnNonEmpty:
                break;

            // xsl:attribute: the name and namespace AVTs may contain consuming expressions
            // (e.g., name="{translate(head(//AUTHOR), ' ', '_')}")
            case XsltAttribute attr:
                ScanAvt(attr.Name);
                if (attr.Namespace != null) ScanAvt(attr.Namespace);
                if (attr.Select != null) ScanExpression(attr.Select);
                if (attr.Content != null) ScanInstructions(attr.Content);
                break;

            // xsl:element: same shape as xsl:attribute — the name and namespace AVTs
            // may contain consuming expressions (e.g., name="{head(//AUTHOR)}"). The
            // body (Content) executes after the streaming pass with the AVT
            // resolved via watcher value substitution at EvaluateAvtAsync time.
            case XsltElement elem:
                ScanAvt(elem.Name);
                if (elem.Namespace != null) ScanAvt(elem.Namespace);
                _constructionDepth++;
                ScanInstructions(elem.Content);
                _constructionDepth--;
                break;

            // xsl:try is transparent to streaming: its select expression (or body)
            // runs the consuming expression against the stream exactly as the same
            // expression would outside a try, with the catch still catching dynamic
            // errors at runtime. Scan the try's select/body so the consuming
            // sub-expression registers a watcher keyed on the same expression object
            // that xsl:try evaluates at runtime (resolved by reference-equality in
            // TryResolveFromWatchers). The catch clauses are NOT scanned — they only
            // execute on error, after the stream is closed, against grounded values.
            case XsltTry tryInsn:
                if (tryInsn.SelectExpression != null)
                    ScanExpression(tryInsn.SelectExpression);
                if (tryInsn.Body != null)
                    ScanInstructions(tryInsn.Body);
                break;

            // Skip xsl:apply-templates and xsl:iterate — handled by existing streaming
            case XsltApplyTemplates:
            case XsltIterate:
                break;

            default:
                // Scan any child content if the instruction is a sequence constructor
                if (instruction is XsltSequenceConstructor childCtor)
                    ScanInstructions(childCtor);
                break;
        }
    }

    private void ScanInstructions(XsltSequenceConstructor body)
    {
        foreach (var insn in body.Instructions)
            ScanInstruction(insn);
    }

    private void ScanAvt(XsltAttributeValueTemplate avt)
    {
        foreach (var part in avt.Parts)
        {
            if (part is AvtExpression avtExpr)
                ScanExpression(avtExpr.Expression);
        }
    }

    /// <summary>
    /// Scans an XPath expression for consuming sub-expressions.
    /// </summary>
    private void ScanExpression(XQueryExpression expr)
    {
        switch (expr)
        {
            // Recognized aggregation patterns: count(path), sum(path), max(path), etc.
            case FunctionCallExpression fc when IsAggregationFunction(fc):
                var aggType = GetAggregationType(fc);
                var pathInfo = ExtractPathFromArgument(fc.Arguments[0]);
                if (pathInfo != null)
                {
                    _watchers.Add(new StreamWatcher
                    {
                        SourceExpression = expr,
                        ContextRootDepth = _contextRootDepth,
                        PathMatcher = new StreamPathMatcher(pathInfo.Value.Path),
                        Aggregation = aggType,
                        ValueAttribute = pathInfo.Value.Attribute,
                        Predicates = pathInfo.Value.Predicates,
                        IntermediatePredicates = pathInfo.Value.IntermediatePredicates,
                        Separator = aggType == WatcherAggregation.StringJoin && fc.Arguments.Count > 1
                            ? ExtractStringLiteral(fc.Arguments[1])
                            : null,
                        // B2 — carry the fn:sum(seq, $zero) default so an empty stream
                        // yields $zero (sf-sum-011/041/042) instead of an empty value-of.
                        // The default rides along as an expression and is evaluated by the
                        // transformer against the live scope only when nothing matched, so a
                        // grounded literal (-1, 42) or a variable ($zero) both resolve. Only
                        // the two-arg sum form carries it; count/max/min/avg ignore arg[1].
                        SumDefaultExpression = aggType == WatcherAggregation.Sum && fc.Arguments.Count > 1
                            ? fc.Arguments[1]
                            : null
                    });
                    return; // Don't recurse into recognized aggregation
                }
                break;

            // fn:head(path) — capture only the first matched item as a scalar.
            // This handles consuming expressions like head(//AUTHOR) in AVT attribute
            // name/namespace positions where a single string value is required.
            case FunctionCallExpression fcHead when IsHeadFunction(fcHead):
                var headPathInfo = ExtractPathFromArgument(fcHead.Arguments[0]);
                if (headPathInfo != null)
                {
                    _watchers.Add(new StreamWatcher
                    {
                        SourceExpression = expr,
                        ContextRootDepth = _contextRootDepth,
                        PathMatcher = new StreamPathMatcher(headPathInfo.Value.Path),
                        Aggregation = WatcherAggregation.Head,
                        ValueAttribute = headPathInfo.Value.Attribute,
                        Predicates = headPathInfo.Value.Predicates,
                        IntermediatePredicates = headPathInfo.Value.IntermediatePredicates
                    });
                    return;
                }
                break;

            // Bare downward path — collect as Sequence
            case PathExpression path when IsDownwardPath(path):
                var seqPath = ExtractPathFromExpression(path);
                if (seqPath != null)
                {
                    _watchers.Add(new StreamWatcher
                    {
                        SourceExpression = expr,
                        ContextRootDepth = _contextRootDepth,
                        PathMatcher = new StreamPathMatcher(seqPath.Value.Path),
                        Aggregation = WatcherAggregation.Sequence,
                        ValueAttribute = seqPath.Value.Attribute,
                        Predicates = seqPath.Value.Predicates,
                        IntermediatePredicates = seqPath.Value.IntermediatePredicates
                    });
                    return;
                }
                break;

            // Striding-then-climbing path — a downward striding prefix that locates a
            // leaf, followed by ancestor::/ancestor-or-self:: (optionally then @attr).
            // The climb is resolved at the leaf's StartElement (all ancestors open), so
            // register a Sequence watcher keyed on the whole climbing PathExpression; the
            // compositional rewriter (Task 1.1/1.2) substitutes $__streaming_watcher_N for
            // it inside any enclosing head/tail/subsequence/if/name() wrapper. (Task 1.3)
            case PathExpression climbPath when TryExtractClimbingPath(climbPath) is { } climb:
                _watchers.Add(new StreamWatcher
                {
                    SourceExpression = expr,
                    ContextRootDepth = _contextRootDepth,
                    PathMatcher = new StreamPathMatcher(climb.LeafPath.Path),
                    Aggregation = WatcherAggregation.Sequence,
                    ValueAttribute = climb.ClimbAttribute,
                    Predicates = climb.LeafPath.Predicates,
                    IntermediatePredicates = climb.LeafPath.IntermediatePredicates,
                    ClimbAxis = climb.ClimbAxis
                });
                return;

            // snapshot() / copy-of() wrapping a path
            case FunctionCallExpression fc2 when IsSnapshotFunction(fc2):
                var snapPath = ExtractPathFromArgument(fc2.Arguments[0]);
                if (snapPath != null)
                {
                    _watchers.Add(new StreamWatcher
                    {
                        SourceExpression = expr,
                        ContextRootDepth = _contextRootDepth,
                        PathMatcher = new StreamPathMatcher(snapPath.Value.Path),
                        Aggregation = WatcherAggregation.Snapshot,
                        ValueAttribute = snapPath.Value.Attribute,
                        Predicates = snapPath.Value.Predicates,
                        IntermediatePredicates = snapPath.Value.IntermediatePredicates
                    });
                    return;
                }
                break;

            // outermost()/innermost() wrapping a downward path. These functions
            // preserve node identity and just filter — for streaming purposes we
            // register a Snapshot watcher keyed on the WHOLE filter call so the
            // body-eval rewriter swaps the call for $__streaming_watcher_N (a
            // sequence of materialized subtree nodes). A wrapping SimpleMap
            // RHS (e.g. `outermost(//PRICE) ! string(.)`) then runs per-item
            // against the grounded subtrees without re-touching the closed
            // stream. NOTE: outermost matches all path hits then dedupes;
            // since paths in the test corpus that use this pattern have no
            // nesting among the matches, the dedupe is a no-op. If callers
            // rely on the dedupe semantics, the watcher results would need
            // post-filtering — out of scope for current target tests.
            case FunctionCallExpression fcFilter when IsFilterFunction(fcFilter):
                // Only register a watcher when the inner argument is a bare
                // downward path keyed off the document root. If it has a
                // non-context InitialExpression (e.g. snapshot(/chapter)//section
                // inside innermost(...)), fall through to ScanChildExpressions so
                // the inner snapshot's path is recognized at its own level.
                if (fcFilter.Arguments[0] is PathExpression filterArgPath
                    && IsDownwardPath(filterArgPath))
                {
                    var filterPath = ExtractPathFromExpression(filterArgPath);
                    if (filterPath != null)
                    {
                        _watchers.Add(new StreamWatcher
                        {
                            SourceExpression = expr,
                            ContextRootDepth = _contextRootDepth,
                            PathMatcher = new StreamPathMatcher(filterPath.Value.Path),
                            Aggregation = WatcherAggregation.Snapshot,
                            ValueAttribute = filterPath.Value.Attribute,
                            Predicates = filterPath.Value.Predicates,
                            IntermediatePredicates = filterPath.Value.IntermediatePredicates
                        });
                        return;
                    }
                }
                break;

            // Group A — wrapped head/outermost/remove aggregation. A top-level
            // FilterExpression whose Primary is head/outermost/remove over a
            // streamable downward/descendant path, with outer positional
            // predicates. Register ONE watcher keyed to the WHOLE FilterExpression
            // (so TryResolveFromWatchers' reference match succeeds), accumulating
            // the inner function's result on the forward pass; the outer predicates
            // are applied to the grounded sequence at resolve. innermost is
            // recognized only to NOT register (it needs descendant lookahead).
            case FilterExpression filt
                when filt.Predicates.Count > 0
                    && TryExtractWrappedAggregation(filt.Primary) is { } wrapped:
                _watchers.Add(new StreamWatcher
                {
                    SourceExpression = expr,
                    ContextRootDepth = _contextRootDepth,
                    PathMatcher = new StreamPathMatcher(wrapped.Path.Path),
                    Aggregation = wrapped.Aggregation,
                    ValueAttribute = wrapped.Path.Attribute,
                    Predicates = wrapped.Path.Predicates,
                    IntermediatePredicates = wrapped.Path.IntermediatePredicates,
                    OuterPredicates = filt.Predicates,
                    RemoveSkipIndex = wrapped.RemoveSkipIndex,
                    Outermost = wrapped.Outermost,
                    TextNodeTail = wrapped.Path.TextNodeTail
                });
                return;

            // Map constructor — scan each entry value
            case MapConstructor map:
                foreach (var entry in map.Entries)
                {
                    ScanExpression(entry.Value);
                }
                return;

            // Binary expressions — scan both sides
            case BinaryExpression bin:
                ScanExpression(bin.Left);
                ScanExpression(bin.Right);
                return;

            // SimpleMap (path!cast!cast …) — when the Left is a downward path
            // AND the Right consists only of per-item atomic-cast / function
            // applications on the context item (no navigation back into the
            // streamed node), register a Sequence watcher keyed on the whole
            // SimpleMap. The watcher rewriter then swaps the SimpleMap for
            // $__streaming_watcher_N (a sequence of leaf string values), and
            // the plan re-applies the casts per atomic item. Handles patterns
            // like /*/*/ITEM/DIMENSIONS!xs:NMTOKENS(.)!xs:decimal(.) where the
            // tail must surface XPTY0004 on incompatible comparison rather
            // than short-circuit to empty.
            case SimpleMapExpression sm when TryExtractSimpleMapWatcherShape(sm) is { } shape:
                _watchers.Add(new StreamWatcher
                {
                    SourceExpression = expr,
                    ContextRootDepth = _contextRootDepth,
                    PathMatcher = new StreamPathMatcher(shape.Path),
                    Aggregation = WatcherAggregation.Sequence,
                    ValueAttribute = shape.Attribute,
                    Predicates = shape.Predicates,
                    IntermediatePredicates = shape.IntermediatePredicates
                });
                return;

            // Node-capturing SimpleMap (sx-GeneralComp-*-016/116/020/120): LEFT ! TAIL
            // where TAIL navigates the matched node's attribute inside a compile-time
            // numeric op (@value*2, abs(@value), -@value, …). The string/atomic capture
            // of IsPerItemAtomicTail can't satisfy the @attr tail, so register a
            // node-capturing Sequence watcher: the processor materializes a childless
            // element carrying the matched element's attributes, and the general-comparison
            // resolver reads @attr off it and applies the known numeric op cheaply per item
            // (no full per-node plan eval — stays fast on 100k rows).
            case SimpleMapExpression smAttr when TryExtractAttributeArithmeticTail(smAttr) is { } attrShape:
                _watchers.Add(new StreamWatcher
                {
                    SourceExpression = expr,
                    ContextRootDepth = _contextRootDepth,
                    PathMatcher = new StreamPathMatcher(attrShape.Path.Path),
                    Aggregation = WatcherAggregation.Sequence,
                    CaptureMatchedNode = true,
                    Predicates = attrShape.Path.Predicates,
                    IntermediatePredicates = attrShape.Path.IntermediatePredicates
                });
                return;

            // SM-ctx (OP-bucket phase 1): a consuming simple-map LEFT ! RIGHT whose
            // LEFT is a plain striding/downward path and whose RIGHT consumes the
            // streamed context node per item (navigates `..`, reads attributes,
            // uses position(), wraps the item in a conditional, …) — shapes the
            // atomic-tail watcher branch above rejects. Register a for-each-style
            // subscription off LEFT carrying RIGHT as PerItemSelect; the per-match
            // dispatch materializes each item, binds it as context, and evaluates
            // RIGHT in-memory. Falls through to ScanChildExpressions (Left-only
            // Sequence watcher) when LEFT is a bounded-window function (deferred).
            case SimpleMapExpression smCtx when TryRegisterSimpleMapContextSubscription(smCtx):
                return;

            // ForExpr streaming (sx-ForExpr): `for $x in CONSUMING-PATH return EXPR`
            // whose `in` operand is a striding child-axis path ending in a grounding
            // step (string()/data()/copy-of()/snapshot()). Register a subscription that
            // binds $x to the grounded value per matched item and evaluates EXPR.
            case FlworExpression flworFor when TryRegisterForExpressionSubscription(flworFor):
                return;
        }

        // Recurse into child expressions for unrecognized patterns
        ScanChildExpressions(expr);
    }

    private void ScanChildExpressions(XQueryExpression expr)
    {
        switch (expr)
        {
            case FunctionCallExpression fc:
                foreach (var arg in fc.Arguments)
                    ScanExpression(arg);
                break;
            case UnaryExpression unary:
                ScanExpression(unary.Operand);
                break;
            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    ScanExpression(item);
                break;
            // PathExpression with a non-step InitialExpression (e.g.,
            // snapshot(/chapter)//section) — recurse into the initial part so
            // an inner snapshot/copy-of of a streamable path can be picked up
            // by the snapshot watcher branch. The Steps themselves can't
            // independently drive a watcher here (their context is the
            // InitialExpression's value, not the document root), but the
            // body-eval AST rewriter substitutes the watched initial with a
            // synthetic variable reference so the Steps execute naturally
            // against the materialised subtree.
            case PathExpression pe:
                if (pe.InitialExpression != null && pe.InitialExpression is not ContextItemExpression)
                    ScanExpression(pe.InitialExpression);
                break;
            case FlworExpression flwor:
                // The for clause source may be consuming
                foreach (var clause in flwor.Clauses)
                {
                    if (clause is ForClause forClause)
                    {
                        foreach (var binding in forClause.Bindings)
                            ScanExpression(binding.Expression);
                    }
                }
                break;
            // SimpleMap that didn't match TryExtractSimpleMapWatcherShape:
            // descend into the Left side so a watched LHS (e.g.
            // outermost(//PRICE) inside `outermost(//PRICE) ! string(.)`)
            // is still recognized. The Right is per-item and runs against
            // the materialized items at body-eval time — no watcher needed.
            case SimpleMapExpression sme:
                ScanExpression(sme.Left);
                break;
            // Binary expressions handled here when ScanExpression's case
            // is bypassed (e.g. comparison inside SimpleMap RHS during
            // unrecognized-shape recursion).
            case BinaryExpression bin:
                ScanExpression(bin.Left);
                ScanExpression(bin.Right);
                break;

            // Task 1.1 — operator-node recursion. A streamable striding/child-axis
            // base path reached through one of these operators (rather than at a
            // recognized top-level shape) must still register a Sequence watcher so
            // the body rewriter (RewriteWithWatcherVariables) has a node to substitute
            // with $__streaming_watcher_N. Without descending here, a path inside
            // `if (exists(<path>)) …`, `(<path>, 31, 32) = …`, `some $x in <path> …`,
            // `<path> instance of …`, or a `cast`/`treat` was never watched and ran
            // against the closed synthetic document → empty sequence → wrong result.
            // Registration itself is gated by the existing PathExpression /
            // IsDownwardPath branch in ScanExpression, so only paths TryBuildPathMatcher
            // already supports register; unsupported shapes fall through unchanged
            // (the oracle + shadow are the soundness guard).
            case IfExpression iff:
                ScanExpression(iff.Condition);
                ScanExpression(iff.Then);
                if (iff.Else != null) ScanExpression(iff.Else);
                break;
            case QuantifiedExpression quant:
                foreach (var binding in quant.Bindings)
                    ScanExpression(binding.Expression);
                ScanExpression(quant.Satisfies);
                break;
            case InstanceOfExpression iof:
                ScanExpression(iof.Expression);
                break;
            case TreatExpression treat:
                ScanExpression(treat.Expression);
                break;
            case CastExpression cast:
                ScanExpression(cast.Expression);
                break;
            case CastableExpression castable:
                ScanExpression(castable.Expression);
                break;

            // Task 1.2 — FilterExpression composition point ((path)[pred]). A plain
            // parenthesized striding base path with an outer predicate — e.g.
            // `(/BOOKLIST/BOOKS/ITEM/PRICE)[1] + 2` (sx-arithmetic-001) — falls through
            // the ScanExpression FilterExpression branch (which only fires for wrapped
            // head/outermost/remove aggregations). Descend into the Primary so a
            // streamable striding base path registers a Sequence watcher; the body
            // rewriter (RewriteWithWatcherVariables) then substitutes the Primary with
            // $__streaming_watcher_N and the outer predicate applies to the grounded
            // materialized sequence in memory. The predicates are NOT scanned here —
            // they filter the grounded per-item context after materialization, not the
            // stream. Registration is still gated by IsDownwardPath/TryBuildPathMatcher
            // in the ScanExpression path branch, so a climbing/consuming Primary falls
            // through un-watched (Task 1.3 / buffer-fallback).
            case FilterExpression filterExpr:
                ScanExpression(filterExpr.Primary);
                break;
        }
    }

    private static bool IsAggregationFunction(FunctionCallExpression fc)
    {
        if (fc.Arguments.Count == 0) return false;
        return fc.Name.LocalName is "count" or "sum" or "max" or "min" or "avg" or "string-join"
            && (fc.Name.Namespace == NamespaceId.None || fc.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn);
    }

    private static WatcherAggregation GetAggregationType(FunctionCallExpression fc)
    {
        return fc.Name.LocalName switch
        {
            "count" => WatcherAggregation.Count,
            "sum" => WatcherAggregation.Sum,
            "max" => WatcherAggregation.Max,
            "min" => WatcherAggregation.Min,
            "avg" => WatcherAggregation.Avg,
            "string-join" => WatcherAggregation.StringJoin,
            _ => WatcherAggregation.Sequence
        };
    }

    private static bool IsSnapshotFunction(FunctionCallExpression fc)
    {
        return fc.Name.LocalName is "snapshot" or "copy-of"
            && fc.Arguments.Count >= 1;
    }

    /// <summary>
    /// True for a zero-argument <c>copy-of()</c> / <c>snapshot()</c> applied per
    /// item as the RHS of a SimpleMap — i.e. a trailing snapshot step such as the
    /// <c>copy-of()</c> in <c>records/record/copy-of()</c>. The function takes the
    /// context item as its implicit argument, so it carries zero explicit arguments
    /// (distinct from the wrapping <see cref="IsSnapshotFunction"/> form
    /// <c>copy-of(path)</c>).
    /// </summary>
    private static bool IsTrailingSnapshotStep(XQueryExpression expr)
    {
        return expr is FunctionCallExpression fc
            && fc.Arguments.Count == 0
            && (fc.Name.Namespace == NamespaceId.None || fc.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn)
            && fc.Name.LocalName is "snapshot" or "copy-of";
    }

    private static bool IsHeadFunction(FunctionCallExpression fc)
    {
        return fc.Name.LocalName == "head"
            && fc.Arguments.Count == 1
            && (fc.Name.Namespace == NamespaceId.None || fc.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn);
    }

    /// <summary>
    /// True for fn:outermost / fn:innermost — identity-preserving filter
    /// functions over a node sequence. When their sole argument is a downward
    /// path, the scanner can register a Snapshot watcher keyed on the whole
    /// call so the streaming pass materializes the matched subtrees.
    /// </summary>
    private static bool IsFilterFunction(FunctionCallExpression fc)
    {
        return fc.Arguments.Count == 1
            && (fc.Name.Namespace == NamespaceId.None || fc.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn)
            && fc.Name.LocalName is "outermost" or "innermost";
    }

    /// <summary>
    /// Group A: the inner consuming function of a wrapped aggregation, decomposed.
    /// </summary>
    internal readonly record struct WrappedAggregation(
        ExtractedPath Path,
        WatcherAggregation Aggregation,
        bool Outermost,
        int? RemoveSkipIndex);

    /// <summary>
    /// Group A: recognizes <c>head(path)</c> / <c>outermost(path)</c> /
    /// <c>remove(path, n)</c> over a streamable downward/descendant path as the inner
    /// of a wrapped aggregation. Returns the extracted path plus the watcher
    /// aggregation (Head for head; Snapshot for outermost/remove), the outermost
    /// flag, and the remove skip-index. <c>innermost</c> is rejected (it needs
    /// descendant lookahead) by returning null — so the wrapper falls through and
    /// stays in the streaming baseline.
    /// </summary>
    private static WrappedAggregation? TryExtractWrappedAggregation(XQueryExpression inner)
    {
        if (inner is not FunctionCallExpression fc) return null;
        if (fc.Name.Namespace != NamespaceId.None
            && fc.Name.Namespace != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn)
            return null;

        switch (fc.Name.LocalName)
        {
            case "head" when fc.Arguments.Count == 1:
            {
                var p = ExtractPathFromArgument(fc.Arguments[0]);
                if (p == null) return null;
                return new WrappedAggregation(p.Value, WatcherAggregation.Head, Outermost: false, RemoveSkipIndex: null);
            }
            case "outermost" when fc.Arguments.Count == 1:
            {
                var p = ExtractPathFromArgument(fc.Arguments[0]);
                if (p == null) return null;
                return new WrappedAggregation(p.Value, WatcherAggregation.Snapshot, Outermost: true, RemoveSkipIndex: null);
            }
            case "remove" when fc.Arguments.Count == 2:
            {
                if (!TryConstantInt(fc.Arguments[1], out var n) || n < 1) return null;
                var p = ExtractPathFromArgument(fc.Arguments[0]);
                if (p == null) return null;
                return new WrappedAggregation(p.Value, WatcherAggregation.Snapshot, Outermost: false, RemoveSkipIndex: n);
            }
            // innermost — recognized only to NOT register (returns null).
            default:
                return null;
        }
    }

    /// <summary>
    /// Recognizes a SimpleMap whose deep-left source is a downward PathExpression
    /// and whose every subsequent step is a per-item atomic operation on the
    /// context item. Handles chains like <c>path!xs:NMTOKENS(.)!xs:decimal(.)</c>
    /// where the parser builds left-associative SimpleMap nodes:
    /// SimpleMap(SimpleMap(path, NMTOKENS(.)), decimal(.)).
    /// </summary>
    private static ExtractedPath? TryExtractSimpleMapWatcherShape(SimpleMapExpression sm)
    {
        // Walk down the Left chain, accumulating tail steps. Every step we
        // encounter (the Right of each SimpleMap we descend through) must be
        // a per-item atomic operation on the context item.
        XQueryExpression current = sm;
        while (current is SimpleMapExpression mapNode)
        {
            if (!IsPerItemAtomicTail(mapNode.Right)) return null;
            current = mapNode.Left;
        }

        // The deep-left must be a downward path we can register a watcher on
        if (current is not PathExpression path || !IsDownwardPath(path))
            return null;

        return ExtractPathFromExpression(path);
    }

    /// <summary>
    /// Node-capturing SimpleMap recognizer (sx-GeneralComp-*-016/116/020/120): matches a
    /// <c>path ! TAIL</c> whose deep-left is a plain downward striding path and whose TAIL
    /// is a numeric expression over a SINGLE context-relative attribute step
    /// (<c>@attr</c>) plus numeric literals — e.g. <c>@value*2</c>, <c>abs(@value)</c>,
    /// <c>-@value</c>, <c>(@value+1) div 2</c>. Returns the extracted LEFT path (with the
    /// attribute NOT folded into <see cref="ExtractedPath.Attribute"/> — the leaf element,
    /// not the attribute, is what the watcher must match) plus the single attribute local
    /// name the tail reads, so the cheap per-item evaluator can read it off the captured
    /// childless element. Rejects any tail that navigates children/descendants/absolute
    /// paths, references more than one attribute name, or contains non-numeric operations.
    /// Distinct from <see cref="IsPerItemAtomicTail"/> (the context-item string path),
    /// which stays untouched.
    /// </summary>
    private static (ExtractedPath Path, string Attribute)? TryExtractAttributeArithmeticTail(SimpleMapExpression sm)
    {
        // Only a single tail step: SimpleMap(path, TAIL). A chained SM (a!b!c) mixing an
        // attribute-navigating tail with further steps is out of scope.
        if (sm.Left is SimpleMapExpression) return null;

        string? attr = null;
        if (!IsNodeNavigatingAttributeTail(sm.Right, ref attr) || attr == null)
            return null;

        if (sm.Left is not PathExpression path || !IsDownwardPath(path))
            return null;

        // The LEFT path must itself be a plain element path (no attribute leaf); the
        // attribute is read by the tail, not the striding matcher.
        var extracted = ExtractPathFromExpression(path);
        if (extracted is not { } ep || ep.Attribute != null) return null;

        return (ep, attr);
    }

    /// <summary>
    /// True when <paramref name="tail"/> is a numeric expression built only from numeric
    /// literals and exactly one context-relative single-step attribute reference
    /// (<c>@attr</c>), combined via arithmetic (<c>* + - div idiv mod</c>), unary
    /// plus/minus, or a numeric built-in function call (<c>abs</c>, <c>ceiling</c>,
    /// <c>floor</c>, <c>round</c>, <c>number</c>, <c>xs:decimal/xs:double/…</c>). The
    /// single attribute name is captured in <paramref name="attr"/>; two DIFFERENT
    /// attribute names, any child/descendant navigation, or any non-numeric operation
    /// makes it false. This is the compile-time-known shape the cheap evaluator applies
    /// per captured node.
    /// </summary>
    private static bool IsNodeNavigatingAttributeTail(XQueryExpression tail, ref string? attr)
    {
        switch (tail)
        {
            case IntegerLiteral:
            case DoubleLiteral:
            case DecimalLiteral:
                return true;
            case PathExpression pe:
            {
                // A single relative attribute step: @value. No initial expression, one
                // attribute-axis step, a concrete local name, no predicates.
                if (pe.IsAbsolute || pe.InitialExpression != null) return false;
                if (pe.Steps.Count != 1) return false;
                var step = pe.Steps[0];
                if (step.Axis != Axis.Attribute || step.Predicates.Count > 0) return false;
                if (step.NodeTest is not NameTest nt || nt.IsLocalNameWildcard) return false;
                if (attr != null && attr != nt.LocalName) return false; // >1 distinct attr
                attr = nt.LocalName;
                return true;
            }
            case UnaryExpression ue when ue.Operator is UnaryOperator.Plus or UnaryOperator.Minus:
                return IsNodeNavigatingAttributeTail(ue.Operand, ref attr);
            case BinaryExpression be when IsNumericArithmetic(be.Operator):
                return IsNodeNavigatingAttributeTail(be.Left, ref attr)
                    && IsNodeNavigatingAttributeTail(be.Right, ref attr);
            case FunctionCallExpression fc when IsNumericTailFunction(fc):
            {
                foreach (var a in fc.Arguments)
                    if (!IsNodeNavigatingAttributeTail(a, ref attr)) return false;
                return true;
            }
            default:
                return false;
        }
    }

    private static bool IsNumericArithmetic(BinaryOperator op) => op is
        BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply
        or BinaryOperator.Divide or BinaryOperator.IntegerDivide or BinaryOperator.Modulo;

    private static bool IsNumericTailFunction(FunctionCallExpression fc)
    {
        var ns = fc.Name.Namespace;
        if (ns != NamespaceId.None
            && ns != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn
            && ns != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Xs)
            return false;
        return fc.Name.LocalName is "abs" or "ceiling" or "floor" or "round" or "number"
            or "decimal" or "double" or "integer" or "float";
    }

    /// <summary>
    /// Returns true when <paramref name="tail"/> is safe to re-apply per atomic
    /// item from a SimpleMap source — i.e. it operates only on the context item
    /// (<c>.</c>) via atomic casts or built-in fn:/xs: function calls, possibly
    /// chained through further SimpleMap operators. Rejects anything that
    /// navigates (PathExpression, attribute access, etc.) because the captured
    /// items are strings, not nodes.
    /// </summary>
    private static bool IsPerItemAtomicTail(XQueryExpression tail)
    {
        switch (tail)
        {
            case ContextItemExpression:
                return true;
            case CastExpression cast:
                return IsPerItemAtomicTail(cast.Expression);
            case CastableExpression castable:
                return IsPerItemAtomicTail(castable.Expression);
            case TreatExpression treat:
                return IsPerItemAtomicTail(treat.Expression);
            case InstanceOfExpression inst:
                return IsPerItemAtomicTail(inst.Expression);
            case FunctionCallExpression fc:
                // Allow fn:/xs: constructor and library function calls whose
                // arguments are themselves per-item atomic. xs:NMTOKENS(.),
                // xs:decimal(.), fn:upper-case(.), etc.
                var ns = fc.Name.Namespace;
                if (ns != NamespaceId.None
                    && ns != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn
                    && ns != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Xs)
                    return false;
                foreach (var arg in fc.Arguments)
                {
                    if (!IsPerItemAtomicTail(arg)) return false;
                }
                return true;
            case SimpleMapExpression sm:
                return IsPerItemAtomicTail(sm.Left) && IsPerItemAtomicTail(sm.Right);
            case StringLiteral:
            case IntegerLiteral:
            case DoubleLiteral:
            case DecimalLiteral:
            case BooleanLiteral:
                return true;
            default:
                return false;
        }
    }

    private static bool IsDownwardPath(PathExpression path)
    {
        // A non-step InitialExpression (e.g., snapshot(/chapter)//section) means
        // the steps don't navigate from the streamable document root — the
        // scanner must NOT register a sequence matcher keyed off the Steps
        // alone (it would silently match unrelated elements in the stream).
        // ContextItemExpression is an exception: it's equivalent to no initial
        // (the body's implicit context is the document root). The recursion
        // through ScanChildExpressions handles the inner watch for other shapes.
        if (path.InitialExpression != null && path.InitialExpression is not ContextItemExpression)
            return false;
        // A path is "downward" if it uses only child/descendant/descendant-or-self/attribute axes
        foreach (var step in path.Steps)
        {
            if (step.Axis is not (Axis.Child or Axis.Descendant or Axis.DescendantOrSelf or Axis.Attribute))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Extracts a simple path string from an XPath expression.
    /// Returns null if the expression is too complex to decompose.
    /// </summary>
    internal readonly record struct ExtractedPath(
        string Path,
        string? Attribute,
        IReadOnlyList<XQueryExpression> Predicates,
        IReadOnlyList<IntermediatePredicate> IntermediatePredicates,
        bool TextNodeTail = false);

    /// <summary>
    /// A motionless predicate carried by an ancestor (intermediate) element step of
    /// a streaming aggregation path, e.g. the <c>[@CAT='P']</c> on <c>ITEM</c> in
    /// <c>BOOKLIST/BOOKS/ITEM[@CAT='P']/PRICE</c>. <see cref="AncestorOffset"/> is the
    /// number of element-step levels the predicated ancestor sits above the matched
    /// leaf element (1 = immediate parent). The predicate is evaluated against the
    /// ancestor element (name + attributes) when the leaf matches; only ancestors
    /// whose predicate passes contribute their descendant leaves to the aggregation.
    /// <para>
    /// When <see cref="IsPositional"/> is true the predicate(s) are FORWARD-COUNTABLE
    /// positional filters (e.g. <c>[position() lt 4]</c>, <c>[3]</c>) on the ancestor
    /// step. They are decidable from the ancestor's running occurrence count as the
    /// stream descends — the processor supplies the ancestor's forward position as the
    /// XPath context position when evaluating them. <see cref="IsWildcardStep"/> marks a
    /// <c>*</c> (any-name) step so the processor counts ALL element siblings; otherwise
    /// it counts siblings sharing the ancestor's local name (a name test or <c>*:NCName</c>
    /// namespace wildcard). Predicates depending on <c>last()</c> are NOT forward-countable
    /// and are never captured here (they stay deferred).
    /// </para>
    /// </summary>
    internal readonly record struct IntermediatePredicate(
        int AncestorOffset,
        IReadOnlyList<XQueryExpression> Predicates,
        bool IsPositional = false,
        bool IsWildcardStep = false);

    private static ExtractedPath? ExtractPathFromArgument(XQueryExpression expr)
    {
        return ExtractPathFromExpression(expr);
    }

    /// <summary>
    /// Task 1.3 — the recognized shape of a striding-then-climbing path:
    /// a downward striding <see cref="LeafPath"/> that locates a leaf, a single
    /// <see cref="ClimbAxis"/> step (<c>ancestor::</c> / <c>ancestor-or-self::</c>),
    /// and an optional trailing attribute (<see cref="ClimbAttribute"/>, the local
    /// name of an <c>@a</c> selected from each climbed node).
    /// </summary>
    private readonly record struct ClimbingPath(
        ExtractedPath LeafPath,
        ClimbAxisKind ClimbAxis,
        string? ClimbAttribute);

    /// <summary>
    /// Recognizes <c>downward-prefix / (ancestor|ancestor-or-self)::* [ / @attr ]</c>
    /// and, where <c>data(@attr)</c> is used in the corpus, its function-wrapped
    /// equivalent handled by the caller. Returns <c>null</c> for any other shape (a bare
    /// climb with no downward anchor, a named-ancestor test, a reverse axis other than
    /// ancestor(-or-self), or a non-attribute tail) so the case stays conservative.
    /// The downward prefix must itself be a recognizable streaming leaf path.
    /// </summary>
    private static ClimbingPath? TryExtractClimbingPath(PathExpression path)
    {
        // A non-step initial expression means the steps don't navigate from the
        // streamable document root — reject (mirrors IsDownwardPath).
        if (path.InitialExpression != null && path.InitialExpression is not ContextItemExpression)
            return null;

        var steps = path.Steps;
        if (steps.Count < 2) return null;

        // Locate the (single) climbing step. It must be ancestor/ancestor-or-self with a
        // wildcard name test; everything before it must be downward; after it at most one
        // attribute step (the @attr tail).
        int climbIdx = -1;
        for (int i = 0; i < steps.Count; i++)
        {
            var ax = steps[i].Axis;
            if (ax is Axis.Ancestor or Axis.AncestorOrSelf)
            {
                if (climbIdx >= 0) return null; // more than one climb — out of scope
                climbIdx = i;
            }
        }
        if (climbIdx <= 0) return null; // no climb, or climb with no downward prefix (bare)

        var climbStep = steps[climbIdx];
        if (climbStep.Predicates.Count > 0) return null; // predicate on the climb — out of scope
        // Only the any-name wildcard climb (ancestor::*) appears in the target corpus.
        if (climbStep.NodeTest is not NameTest { LocalName: "*" }) return null;

        // Prefix: steps[0..climbIdx) must all be downward.
        for (int i = 0; i < climbIdx; i++)
        {
            if (steps[i].Axis is not (Axis.Child or Axis.Descendant or Axis.DescendantOrSelf))
                return null;
        }

        // Tail after the climb: nothing, or a single attribute step (@a / @*).
        string? climbAttribute = null;
        if (climbIdx + 1 < steps.Count)
        {
            if (climbIdx + 2 != steps.Count) return null; // more than one tail step
            var tail = steps[climbIdx + 1];
            if (tail.Axis != Axis.Attribute) return null;
            if (tail.Predicates.Count > 0) return null;
            climbAttribute = tail.NodeTest is NameTest { LocalName: var ln } && ln != "*" ? ln : null;
            // @* (any attribute) is out of scope for this slice — a per-node attribute
            // fan-out needs multi-value expansion the single-ValueAttribute watcher can't
            // express. Reject so those cases stay conservative (deferred).
            if (climbAttribute == null) return null;
        }

        // Build the leaf-only downward path and reuse the existing extractor.
        var leafPath = new PathExpression
        {
            IsAbsolute = path.IsAbsolute,
            InitialExpression = path.InitialExpression,
            Steps = steps.Take(climbIdx).ToArray(),
        };
        var leaf = ExtractPathFromExpression(leafPath);
        if (leaf == null) return null;
        // The leaf extractor must not itself have produced an attribute tail (the prefix
        // is pure element steps) — the climb's @attr is carried separately.
        if (leaf.Value.Attribute != null) return null;

        var climbAxis = climbStep.Axis == Axis.AncestorOrSelf
            ? ClimbAxisKind.AncestorOrSelf
            : ClimbAxisKind.Ancestor;

        return new ClimbingPath(leaf.Value, climbAxis, climbAttribute);
    }

    private static ExtractedPath? ExtractPathFromExpression(XQueryExpression expr)
    {
        // Walk the path expression to build a simple "a/b/c" or "a/b/@attr" string.
        // This handles the common cases: child steps and attribute access.
        // Complex expressions (predicates, descendant-or-self) fall through to null.
        var parts = new List<string>();
        string? attribute = null;
        bool textNodeTail = false;
        IReadOnlyList<XQueryExpression> predicates = Array.Empty<XQueryExpression>();
        List<IntermediatePredicate>? intermediatePredicates = null;

        var current = expr;
        while (current != null)
        {
            switch (current)
            {
                case PathExpression pathExpr:
                    // PathExpression has steps — iterate. Predicates on the LAST
                    // element step (or the element step immediately before an
                    // attribute tail) are captured for final-step runtime filtering.
                    // Predicates on EARLIER (intermediate/ancestor) element steps are
                    // captured separately when they are MOTIONLESS — decidable from
                    // the ancestor element's own attributes/name at its start-tag
                    // (e.g. [@CAT='P']). Non-motionless intermediate predicates
                    // (positional, last(), or child navigation) cause the whole path
                    // to be rejected (return null → no watcher registered, the case
                    // falls back to its prior failing baseline) rather than silently
                    // accumulating unfiltered items.
                    bool pendingDescendant = false;
                    int lastElementStepIdx = -1;
                    for (int si = pathExpr.Steps.Count - 1; si >= 0; si--)
                    {
                        var endStep = pathExpr.Steps[si];
                        // Skip a trailing attribute step or a trailing text() KindTest
                        // (//PRICE/text()): the matched-element step is the one above.
                        if (endStep.Axis == Axis.Attribute) continue;
                        if (endStep.NodeTest is KindTest tk
                            && tk.Kind == XdmNodeKind.Text && tk.Name == null && tk.TypeName == null)
                            continue;
                        lastElementStepIdx = si;
                        break;
                    }
                    // Count element-axis steps so an intermediate predicate's ancestor
                    // offset (levels above the matched leaf) can be derived. Also detect
                    // any descendant-axis marker, which makes the ancestor offset
                    // ambiguous — intermediate predicates are then disallowed.
                    int totalElementSteps = 0;
                    bool pathHasDescendantAxis = false;
                    for (int si = 0; si < pathExpr.Steps.Count; si++)
                    {
                        var s = pathExpr.Steps[si];
                        if (s.Axis is Axis.Child or Axis.Descendant)
                            totalElementSteps++;
                        if (s.Axis is Axis.Descendant or Axis.DescendantOrSelf)
                            pathHasDescendantAxis = true;
                    }
                    int elementStepSeen = 0;
                    for (int si = 0; si < pathExpr.Steps.Count; si++)
                    {
                        var step = pathExpr.Steps[si];
                        if (step.Axis == Axis.Attribute)
                        {
                            if (step.NodeTest is NameTest attrNameTest)
                                attribute = attrNameTest.LocalName;
                            // A predicate on the attribute leaf step itself
                            // (e.g. @value[xs:decimal(.) gt 0]) is motionless —
                            // its only context is the attribute's own string value
                            // (the context item `.`). Capture it as the final-step
                            // filter so the watcher drops attribute values that fail
                            // it; without this the predicate was silently dropped and
                            // the aggregate ran over ALL attribute values
                            // (sf-sum-019 / sf-avg-019 / sf-min-019). Only when an
                            // element step hasn't already supplied a final predicate.
                            if (step.Predicates.Count > 0 && predicates.Count == 0)
                                predicates = step.Predicates;
                        }
                        else if (step.Axis == Axis.Child)
                        {
                            // text() KindTest tail (e.g. //PRICE/text()): the leaf
                            // match is still the parent element step; record the
                            // text-node tail so the watcher consumer atomizes text
                            // content. Don't add a path part (the matcher keys on
                            // elements). Only valid as the last step.
                            if (step.NodeTest is KindTest ktTail
                                && ktTail.Kind == XdmNodeKind.Text
                                && ktTail.Name == null
                                && ktTail.TypeName == null)
                            {
                                if (si == pathExpr.Steps.Count - 1)
                                {
                                    textNodeTail = true;
                                    // Predicates on the text() tail (e.g.
                                    // PAGES/text()[. < 1000][. > 0]) filter the
                                    // leaf's text value — the leaf's string value
                                    // equals the text node's value, so capture them
                                    // as the final-step predicates (preserving the
                                    // historical aggregation-filter behavior). Only do
                                    // so when the matched element step (e.g.
                                    // b[@type='X']/text()[…]) hasn't already supplied a
                                    // final-step predicate, so the element predicate is
                                    // not silently overwritten by the text-tail one.
                                    if (step.Predicates.Count > 0 && predicates.Count == 0)
                                        predicates = step.Predicates;
                                    continue;
                                }
                                return null; // text() not at tail — unsupported
                            }
                            if (step.NodeTest is NameTest nameTest)
                            {
                                if (pendingDescendant)
                                {
                                    parts.Add("**");
                                    pendingDescendant = false;
                                }
                                parts.Add(nameTest.LocalName);
                            }
                            elementStepSeen++;
                            if (si == lastElementStepIdx && step.Predicates.Count > 0)
                            {
                                predicates = step.Predicates;
                            }
                            else if (si != lastElementStepIdx && step.Predicates.Count > 0)
                            {
                                // Intermediate (ancestor) element step predicate.
                                // Capture it for runtime ancestor-filtering ONLY when it
                                // is motionless against the ancestor (attributes/name) and
                                // the path has no descendant axis (so the ancestor offset
                                // is unambiguous). Otherwise fall back to the historical
                                // behavior: silently drop the predicate and register a
                                // watcher over the unfiltered path. Positional / last() /
                                // child-navigating intermediate predicates are DEFERRED
                                // this way (they remain in the streaming baseline) rather
                                // than rejecting the whole path — which would otherwise
                                // suppress the watcher and regress cases that previously
                                // passed via the unfiltered aggregation.
                                //
                                // Two flavors are captured:
                                //   (a) MOTIONLESS predicates (e.g. [@CAT='P']) — decidable
                                //       from the ancestor's name + attributes alone.
                                //   (b) FORWARD-COUNTABLE POSITIONAL predicates (e.g.
                                //       [position() lt 4], [3]) — decidable from the
                                //       ancestor's running occurrence count under its parent,
                                //       which is known when its start-tag is seen. last()
                                //       (needs the total) keeps these deferred.
                                if (!pathHasDescendantAxis)
                                {
                                    int ancestorOffset = totalElementSteps - elementStepSeen;
                                    bool wildcardStep = step.NodeTest is NameTest nt && nt.IsLocalNameWildcard;
                                    if (ArePredicatesMotionlessAgainstAncestor(step.Predicates))
                                    {
                                        (intermediatePredicates ??= []).Add(
                                            new IntermediatePredicate(ancestorOffset, step.Predicates));
                                    }
                                    else if (ArePredicatesForwardCountablePositional(step.Predicates))
                                    {
                                        (intermediatePredicates ??= []).Add(
                                            new IntermediatePredicate(
                                                ancestorOffset, step.Predicates,
                                                IsPositional: true, IsWildcardStep: wildcardStep));
                                    }
                                }
                            }
                        }
                        else if (step.Axis == Axis.DescendantOrSelf)
                        {
                            // For // (descendant-or-self::node()/child::X),
                            // mark that the following child step is at any depth.
                            pendingDescendant = true;
                            continue;
                        }
                        else if (step.Axis == Axis.Descendant)
                        {
                            // descendant::X — match X at any depth below the
                            // current context. Encode the "any-depth" marker
                            // before the name step so the path matcher walks
                            // ancestors loosely for this segment.
                            if (step.NodeTest is NameTest descNameTest)
                            {
                                parts.Add("**");
                                parts.Add(descNameTest.LocalName);
                                pendingDescendant = false;
                            }
                            else
                            {
                                return null;
                            }
                            elementStepSeen++;
                            if (si == lastElementStepIdx && step.Predicates.Count > 0)
                            {
                                predicates = step.Predicates;
                            }
                            // Predicate on an intermediate descendant-axis step: the
                            // ancestor offset is ambiguous under //, so it is not
                            // captured (historical silent-drop behavior preserved).
                        }
                        else
                        {
                            return null; // Unsupported axis
                        }
                    }
                    current = null;
                    break;

                case ContextItemExpression:
                    current = null;
                    break;

                default:
                    return null; // Can't decompose
            }
        }

        if (parts.Count == 0) return null;
        return new ExtractedPath(
            string.Join("/", parts),
            attribute,
            predicates,
            (IReadOnlyList<IntermediatePredicate>?)intermediatePredicates ?? Array.Empty<IntermediatePredicate>(),
            textNodeTail);
    }

    /// <summary>
    /// True when every predicate in <paramref name="preds"/> is decidable from an
    /// ancestor element's own name and attributes at its start-tag — i.e. motionless
    /// against the ancestor. Permits attribute-axis navigation (<c>@x</c>), <c>self::</c>,
    /// comparisons, boolean connectives, literals and a small set of value functions.
    /// Rejects positional predicates, <c>last()</c>/<c>position()</c>, and any child or
    /// descendant navigation (which would require the ancestor's not-yet-streamed
    /// content). Conservative: anything unrecognized is rejected.
    /// </summary>
    private static bool ArePredicatesMotionlessAgainstAncestor(IReadOnlyList<XQueryExpression> preds)
    {
        foreach (var pred in preds)
        {
            // Bare positional literal [1], [2.0] — positional, not motionless.
            if (pred is IntegerLiteral or DecimalLiteral or DoubleLiteral) return false;
            if (!IsMotionlessAncestorExpression(pred)) return false;
        }
        return true;
    }

    /// <summary>
    /// True when every predicate in <paramref name="preds"/> is a FORWARD-COUNTABLE
    /// positional filter on an intermediate (ancestor) element step — decidable solely
    /// from the ancestor's running occurrence count under its parent as the stream
    /// descends. Accepts:
    /// <list type="bullet">
    /// <item>a bare numeric literal <c>[N]</c> (equivalent to <c>position() = N</c>);</item>
    /// <item>a comparison with <c>position()</c> on one side and a constant numeric
    /// expression on the other (<c>[position() lt 4]</c>, <c>[position() = 2]</c>,
    /// <c>[3 ge position()]</c>, value- or general-comparison operators).</item>
    /// </list>
    /// Rejects anything mentioning <c>last()</c> (needs the not-yet-known total),
    /// boolean connectives, multiple predicates that aren't all positional, or any other
    /// shape. Conservative: unrecognized → false (predicate stays deferred).
    /// </summary>
    private static bool ArePredicatesForwardCountablePositional(IReadOnlyList<XQueryExpression> preds)
    {
        if (preds.Count == 0) return false;
        foreach (var pred in preds)
            if (!IsForwardCountablePositionalPredicate(pred)) return false;
        return true;
    }

    private static bool IsForwardCountablePositionalPredicate(XQueryExpression pred)
    {
        // Bare numeric literal [N] — a positional shorthand for position() = N.
        if (pred is IntegerLiteral or DecimalLiteral or DoubleLiteral)
            return true;

        // A comparison with position() on one side and a constant numeric expression
        // on the other. The order is irrelevant for forward-countability. last() must
        // not appear anywhere.
        if (pred is BinaryExpression bin && IsComparisonOperator(bin.Operator))
        {
            bool leftPos = IsPositionCall(bin.Left);
            bool rightPos = IsPositionCall(bin.Right);
            if (leftPos == rightPos) return false; // need exactly one position() operand
            var other = leftPos ? bin.Right : bin.Left;
            return IsConstantNumericNoLast(other);
        }

        return false;
    }

    private static bool IsComparisonOperator(BinaryOperator op) => op is
        BinaryOperator.Equal or BinaryOperator.NotEqual
        or BinaryOperator.LessThan or BinaryOperator.LessOrEqual
        or BinaryOperator.GreaterThan or BinaryOperator.GreaterOrEqual
        or BinaryOperator.GeneralEqual or BinaryOperator.GeneralNotEqual
        or BinaryOperator.GeneralLessThan or BinaryOperator.GeneralLessOrEqual
        or BinaryOperator.GeneralGreaterThan or BinaryOperator.GeneralGreaterOrEqual;

    private static bool IsPositionCall(XQueryExpression expr) =>
        expr is FunctionCallExpression { Name.LocalName: "position", Arguments.Count: 0 };

    /// <summary>
    /// True for a numeric constant expression that does not reference position()/last()
    /// — i.e. a fixed bound the positional predicate is compared against. Permits
    /// literals, unary +/-, and arithmetic over such constants.
    /// </summary>
    private static bool IsConstantNumericNoLast(XQueryExpression expr)
    {
        switch (expr)
        {
            case IntegerLiteral or DecimalLiteral or DoubleLiteral:
                return true;
            case UnaryExpression u:
                return IsConstantNumericNoLast(u.Operand);
            case BinaryExpression b:
                return IsConstantNumericNoLast(b.Left) && IsConstantNumericNoLast(b.Right);
            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="expr"/> is a GROUNDED result: a value that does not
    /// navigate into the streamed input (no path, no bare context item, no consuming
    /// function). Used to recognize the branches of a grounded-branch conditional
    /// select <c>if(C) then G1 else G2</c>, where only the condition C consumes the
    /// stream. Whitelist-only (conservative false for anything unrecognized).
    /// </summary>
    private static bool IsGroundedResultExpression(XQueryExpression expr)
    {
        switch (expr)
        {
            case IntegerLiteral or DecimalLiteral or DoubleLiteral
                or StringLiteral or BooleanLiteral or EmptySequence:
                return true;
            // A variable reference is a constant per run (evaluated once against the
            // live scope), so it does not navigate the stream.
            case VariableReference:
                return true;
            case UnaryExpression unary:
                return IsGroundedResultExpression(unary.Operand);
            case BinaryExpression bin:
                return IsGroundedResultExpression(bin.Left)
                    && IsGroundedResultExpression(bin.Right);
            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    if (!IsGroundedResultExpression(item)) return false;
                return true;
            default:
                return false;
        }
    }

    private static bool IsMotionlessAncestorExpression(XQueryExpression expr)
    {
        switch (expr)
        {
            case IntegerLiteral or DecimalLiteral or DoubleLiteral
                or StringLiteral or BooleanLiteral:
                return true;

            // The ancestor element itself (the predicate's context item). Reading
            // its string value would require its child content — not motionless —
            // so a bare '.' is rejected; only attribute/self navigation is allowed.
            case ContextItemExpression:
                return false;

            case BinaryExpression bin:
                return IsMotionlessAncestorExpression(bin.Left)
                    && IsMotionlessAncestorExpression(bin.Right);

            case UnaryExpression unary:
                return IsMotionlessAncestorExpression(unary.Operand);

            // Parenthesized expressions parse as a SequenceExpression (the empty
            // sequence () has zero items, which is trivially motionless).
            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    if (!IsMotionlessAncestorExpression(item)) return false;
                return true;

            case CastExpression cast:
                return IsMotionlessAncestorExpression(cast.Expression);
            case CastableExpression castable:
                return IsMotionlessAncestorExpression(castable.Expression);
            case TreatExpression treat:
                return IsMotionlessAncestorExpression(treat.Expression);
            case InstanceOfExpression inst:
                return IsMotionlessAncestorExpression(inst.Expression);

            // A path is motionless against the ancestor only when it consists of a
            // single attribute-axis step (e.g. @CAT) or self::name with no further
            // navigation into children. Anything else (child/descendant) needs the
            // not-yet-streamed subtree.
            case PathExpression path:
                if (path.InitialExpression != null && path.InitialExpression is not ContextItemExpression)
                    return false;
                if (path.Steps.Count == 0) return false;
                foreach (var step in path.Steps)
                {
                    if (step.Predicates.Count > 0) return false;
                    if (step.Axis == Axis.Attribute) continue;
                    if (step.Axis == Axis.Self) continue;
                    return false; // child/descendant/etc. — not motionless
                }
                return true;

            // Only fn:/xs: functions whose arguments are themselves motionless.
            // Reject positional/context-dependent functions explicitly.
            case FunctionCallExpression fc:
                if (fc.Name.LocalName is "position" or "last") return false;
                var ns = fc.Name.Namespace;
                if (ns != NamespaceId.None
                    && ns != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn
                    && ns != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Xs)
                    return false;
                foreach (var arg in fc.Arguments)
                    if (!IsMotionlessAncestorExpression(arg)) return false;
                return true;

            default:
                return false;
        }
    }

    private static string? ExtractStringLiteral(XQueryExpression expr)
    {
        return expr is StringLiteral sl ? sl.Value : null;
    }

    /// <summary>
    /// Registers a <see cref="ForEachSubscription"/> for an <c>xsl:for-each</c>
    /// whose select expression is either a simple absolute child-axis path or
    /// a comma sequence of grounded operands surrounding a single streamable
    /// path. Otherwise leaves the for-each to the buffered execution path.
    /// </summary>
    private void TryRegisterForEachSubscription(XsltForEach forEach)
    {
        if (forEach.Sorts.Count > 0) return;

        // Grounded-branch conditional select — `xsl:for-each select="if(C) then G1
        // else G2"` where BOTH branches are grounded (motionless: literals / constants
        // / variable refs, no navigation into the streamed input). The whole select's
        // only dependence on the stream is the effective boolean value of the consuming
        // condition C. Rather than register a for-each SUBSCRIPTION (there is no per-match
        // striding path to iterate — the iteration is over a grounded 0/1), scan C so its
        // consuming path registers a WATCHER, then leave the for-each on the deferred body
        // path. When the deferred body runs, EvaluateAsync(select) sees _activeStreamWatchers
        // set, RewriteWithWatcherVariables substitutes the watched condition sub-expression,
        // the grounded `if` collapses to the selected branch, and the for-each iterates it.
        // (sx-if-215.) The condition scan must not leak a for-each subscription: a consuming
        // SimpleMap/for-expr condition would register one keyed to no iterable body, so any
        // subscription the scan adds is rolled back (those shapes stay deferred/buffered).
        if (forEach.Select is IfExpression groundedIf
            && groundedIf.Condition != null
            && IsGroundedResultExpression(groundedIf.Then)
            && (groundedIf.Else == null || IsGroundedResultExpression(groundedIf.Else)))
        {
            int watchersBefore = _watchers.Count;
            int subsBefore = _subscriptions.Count;
            ScanExpression(groundedIf.Condition);
            while (_subscriptions.Count > subsBefore)
                _subscriptions.RemoveAt(_subscriptions.Count - 1);
            // Only claim the for-each when the condition actually registered a watcher;
            // otherwise fall through so nothing changes for un-watchable conditions.
            if (_watchers.Count > watchersBefore)
                return;
        }

        // Group B — non-consuming inspection subscription. A streamable for-each over
        // outermost(//X) / innermost(//X) / a bare descendant path //X whose per-match
        // body is INSPECTION-ONLY (ancestor/parent/self/attribute + atomize + set-ops,
        // no descent) streams by dispatching the body per match against an
        // ancestor-synthesized snapshot WITHOUT materialize-and-skip, so the forward
        // pass continues into descendants. This is SEPARATE from the consuming
        // child-axis subscription below (TryBuildPathMatcher's child-axis-only gate
        // deliberately rejects descendant axis as unsound for materialize-and-skip).
        if (TryRegisterInspectionForEach(forEach)) return;

        // Peel any leading snapshot()/copy-of() wrapper(s) and a forward-window
        // function (subsequence/head/tail/remove), in either nesting order, until the
        // core is a plain streamable path. A snapshot(<path>)/copy-of(<path>) wrapper is
        // a grounded deep copy with navigable ancestors/attributes — exactly the
        // materialized element + synthesized ancestor chain the subscription dispatch
        // produces — so peeling it routes the for-each to the existing subscription
        // machinery (which binds the @attr/text() context item and climbs ancestors),
        // instead of falling through to the string-capturing Snapshot WATCHER (no node
        // identity, no ancestor chain). The window is forward-countable over the selected
        // sequence — decidable from the running path-match count as the stream descends —
        // so it is applied by the dispatcher. Both `subsequence(snapshot(p),s,l)` (window
        // outside) and `snapshot(remove(p,n))` (window inside) reduce to `p + window`.
        // NOTE: confined to the for-each decomposition; the snapshot WATCHER path
        // (ScanExpression, used by value-of/copy-of/aggregation over snapshot) is a
        // separate scanner entry point and is untouched.
        var select = forEach.Select;

        // Runtime branch-gate — `for-each select="if(C) then T else E"` where the
        // condition C is GROUNDED (motionless — a variable / constant) and exactly ONE
        // branch is a streamable path while the OTHER is the empty sequence (). The
        // striding path lives in a BRANCH of the conditional, so the ordinary decomposition
        // below cannot reach it; register the subscription for the streamable branch and
        // carry the condition as a runtime GATE (evaluated once against the live scope by
        // the processor). The gate is open — the subscription fires — only when C selects
        // the streamable branch; when C selects the () branch the subscription is skipped
        // entirely, which is exactly right (the other branch is empty). A static always-fire
        // would be UNSOUND: with C true, `then ()` is empty and the else-path must NOT fire.
        // (sx-if-015.) Both branches grounded is handled by the earlier grounded-branch
        // block; both non-empty / neither empty is out of scope (not this shape).
        XQueryExpression? gateCondition = null;
        bool gateFiresWhenConditionTrue = false;
        if (select is IfExpression branchIf
            && branchIf.Condition != null
            && IsGroundedResultExpression(branchIf.Condition)
            && branchIf.Else != null)
        {
            bool thenEmpty = IsEmptySequenceExpression(branchIf.Then);
            bool elseEmpty = IsEmptySequenceExpression(branchIf.Else);
            if (thenEmpty ^ elseEmpty)
            {
                gateCondition = branchIf.Condition;
                if (elseEmpty)
                {
                    // if(C) then <path> else () — fire when C is true.
                    select = UnwrapSingletonSequence(branchIf.Then);
                    gateFiresWhenConditionTrue = true;
                }
                else
                {
                    // if(C) then () else <path> — fire when C is false.
                    select = UnwrapSingletonSequence(branchIf.Else);
                    gateFiresWhenConditionTrue = false;
                }
            }
        }

        int subseqStart = 1;
        int? subseqLength = null;
        int? removeIndex = null;
        XQueryExpression? startExpr = null;
        XQueryExpression? lengthExpr = null;
        XQueryExpression? removeIndexExpr = null;
        bool windowPeeled = false;
        while (true)
        {
            // Leading snapshot(<arg>) wrapper → peel to <arg>. Only the 1-arg wrapping
            // form; a zero-arg trailing snapshot step is handled inside TryBuildPathMatcher
            // and is not a select wrapper. NOTE: snapshot ONLY, not copy-of — fn:snapshot
            // copies the node *and its ancestors* (so a climbing body sees them), which the
            // subscription dispatch reproduces via SynthesizeAncestorChain; fn:copy-of is a
            // parentless deep copy, so routing it through the ancestor-synthesizing path
            // would make a `..`/`ancestor::` body see ancestors it must not (spec-wrong).
            if (select is FunctionCallExpression snapFc
                && snapFc.Name.LocalName == "snapshot"
                && snapFc.Arguments.Count == 1
                && (snapFc.Name.Namespace == NamespaceId.None
                    || snapFc.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn))
            {
                select = snapFc.Arguments[0];
                continue;
            }

            // Leading one-or-more(<arg>) / exactly-one(<arg>) wrapper → peel to <arg>.
            // These fn: cardinality functions return their operand unchanged when it
            // satisfies the cardinality; under a streaming for-each over a striding path
            // (sf-one-or-more-015: one-or-more(/BOOKLIST/BOOKS/ITEM/PRICE)) they are a
            // focus-setting pass-through, so peeling routes the for-each to the existing
            // subscription machinery instead of leaving the path unreached (subs=0 →
            // one-or-more sees an empty synthetic sequence → runtime error). The
            // cardinality guard is a no-op on the non-empty match stream the subscription
            // dispatches; an empty input simply fires no body — the same observable
            // result the buffered path would reach after the (skipped) window handling.
            if (select is FunctionCallExpression cardFc
                && (cardFc.Name.LocalName == "one-or-more" || cardFc.Name.LocalName == "exactly-one")
                && cardFc.Arguments.Count == 1
                && (cardFc.Name.Namespace == NamespaceId.None
                    || cardFc.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn))
            {
                select = cardFc.Arguments[0];
                continue;
            }

            // Leading unordered(<arg>) / trace(<arg>[, label]) wrapper → peel to <arg>.
            // Both are pure identity pass-throughs over their value operand:
            // fn:unordered returns its input in an implementation-dependent order (leaving
            // it in document order is conformant), and fn:trace returns its first argument
            // unchanged (the optional label is a side-effecting diagnostic only). Under a
            // streaming for-each over a striding path — sf-unordered-015
            // (unordered(/BOOKLIST/BOOKS/ITEM/PRICE)), sf-trace-015
            // (trace(/BOOKLIST/BOOKS/ITEM/PRICE,'r-015')) — peeling routes the for-each to
            // the existing subscription machinery instead of leaving the wrapped path
            // unreached (the wrapper ran against the closed synthetic document → empty).
            if (select is FunctionCallExpression passFc
                && ((passFc.Name.LocalName == "unordered" && passFc.Arguments.Count == 1)
                    || (passFc.Name.LocalName == "trace" && passFc.Arguments.Count is 1 or 2))
                && (passFc.Name.Namespace == NamespaceId.None
                    || passFc.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn))
            {
                select = passFc.Arguments[0];
                continue;
            }

            // A single forward-window wrapper. A second window is out of scope (the
            // accumulated index would span heterogeneous slices) — fall back.
            if (TryPeelWindowFunction(select, out var inner, out var start, out var length, out var remIdx))
            {
                if (windowPeeled) return;
                // Reject a non-positive or non-integer start — out of scope; let the
                // for-each fall back to buffered execution.
                if (start < 1) return;
                select = inner;
                subseqStart = start;
                subseqLength = length;
                removeIndex = remIdx;
                windowPeeled = true;
                continue;
            }

            // B2 — a window whose positional bound is a GROUNDED non-literal (a variable
            // like $three / $two that does not navigate the input) is still
            // forward-decidable: the bound is a constant per run, evaluated once against
            // the live scope by the processor. Carry the bound expressions so the
            // dispatcher folds them into the effective window. A stream-navigating bound
            // is rejected (returns false) → the for-each stays on the buffered path.
            if (TryPeelWindowFunctionExpr(select, out var innerE, out var startE, out var lengthE, out var remIdxE))
            {
                if (windowPeeled) return;
                select = innerE;
                startExpr = startE;
                lengthExpr = lengthE;
                removeIndexExpr = remIdxE;
                windowPeeled = true;
                continue;
            }

            break;
        }

        if (!TryDecomposeForEachSelect(select, out var prefix, out var path, out var textNodeTail, out var suffix, out var attributeName, out var predicates, out var intermediatePredicates, out var atomize))
            return;

        // A window slice combined with a mixed-sequence prefix/suffix is out of scope
        // (the window index would span heterogeneous operands). Only register the
        // window when the select decomposed to a bare single path.
        if (subseqStart != 1 || subseqLength != null || removeIndex != null
            || startExpr != null || lengthExpr != null || removeIndexExpr != null)
        {
            if (prefix.Count > 0 || suffix.Count > 0) return;
        }

        _subscriptions.Add(new ForEachSubscription
        {
            SourceInstruction = forEach,
            PathMatcher = path,
            Body = forEach.Body,
            PrefixItems = prefix,
            SuffixItems = suffix,
            TextNodeTail = textNodeTail,
            AttributeName = attributeName,
            AtomizeContextItem = atomize,
            Predicates = predicates,
            IntermediatePredicates = intermediatePredicates,
            SubsequenceStart = subseqStart,
            SubsequenceLength = subseqLength,
            RemoveIndex = removeIndex,
            SubsequenceStartExpression = startExpr,
            SubsequenceLengthExpression = lengthExpr,
            RemoveIndexExpression = removeIndexExpr,
            InlineDriven = _constructionDepth > 0,
            GateCondition = gateCondition,
            GateFiresWhenConditionTrue = gateFiresWhenConditionTrue,
        });
    }

    /// <summary>
    /// True when <paramref name="expr"/> is the empty sequence <c>()</c> — either the
    /// dedicated <see cref="EmptySequence"/> node or a parenthesized sequence with zero
    /// items. Used to recognize the empty branch of a runtime branch-gated conditional
    /// for-each select.
    /// </summary>
    private static bool IsEmptySequenceExpression(XQueryExpression? expr)
        => expr is EmptySequence
            || (expr is SequenceExpression seq && seq.Items.Count == 0);

    /// <summary>
    /// Unwraps a parenthesized single-item sequence <c>(X)</c> to <c>X</c> so the
    /// streamable branch of a conditional select decomposes as a bare path. A
    /// multi-item sequence or non-sequence expression is returned unchanged.
    /// </summary>
    private static XQueryExpression UnwrapSingletonSequence(XQueryExpression expr)
        => expr is SequenceExpression { Items: { Count: 1 } items } ? items[0] : expr;

    /// <summary>
    /// Group B: registers a non-consuming inspection <see cref="ForEachSubscription"/>
    /// for a streamable <c>xsl:for-each</c> whose select is <c>outermost(//X)</c> /
    /// <c>innermost(//X)</c> / a bare descendant path <c>//X</c> AND whose body is
    /// provably inspection-only (ancestor/parent/self/attribute + atomize, no descent).
    /// Returns true (registered) only for <c>outermost</c> or a bare descendant path;
    /// <c>innermost</c> is recognized solely to REJECT it (it needs descendant
    /// lookahead — never registered, never dispatched unsoundly). Returns false for any
    /// other shape so the caller proceeds to the consuming child-axis path.
    /// </summary>
    private bool TryRegisterInspectionForEach(XsltForEach forEach)
    {
        var select = forEach.Select;

        bool outermost = false;
        XQueryExpression innerPath;
        switch (select)
        {
            case FunctionCallExpression fc when IsFilterFunction(fc):
                // outermost(<path>) / innermost(<path>). innermost is never registered.
                if (fc.Name.LocalName == "innermost") return false;
                outermost = true;
                innerPath = fc.Arguments[0];
                break;
            case PathExpression:
                innerPath = select;
                break;
            default:
                return false;
        }

        // The inner argument must be a downward path containing a descendant-axis
        // hop (// or descendant::). A purely child-axis path is handled by the
        // existing consuming subscription path — do not capture it here.
        if (innerPath is not PathExpression path || !IsDownwardPath(path))
            return false;
        if (!PathHasDescendantAxis(path)) return false;

        // The body's soundness class decides the dispatch mode:
        //   inspection-only  → non-consuming empty-snapshot dispatch (Group B), valid
        //                      for outermost AND bare //X (matches may nest).
        //   self-atomizing   → the body reads the bare context item `.` (needs the
        //                      matched leaf's text value), so the empty-children
        //                      snapshot would atomize to "". Only sound under
        //                      `outermost` (matches never nest), where materialize-and-skip
        //                      captures the leaf's text and cannot swallow a deeper match.
        // A body that is neither (bare //X with a `.`-atomizing body, or any downward
        // navigation we can't materialize soundly non-outermost) is left to buffered
        // execution.
        bool inspectionOnly = StreamabilityChecker.IsInspectionOnlyBody(forEach.Body);
        if (!inspectionOnly && !outermost) return false;

        // Encode the descendant path (// → ** marker) into a StreamPathMatcher.
        var extracted = ExtractPathFromExpression(path);
        if (extracted == null) return false;
        // An attribute tail or final-step predicates are out of scope for the
        // inspection dispatch (the matched item is the element snapshot itself).
        if (extracted.Value.Attribute != null) return false;
        if (extracted.Value.Predicates.Count > 0) return false;

        _subscriptions.Add(new ForEachSubscription
        {
            SourceInstruction = forEach,
            PathMatcher = new StreamPathMatcher(extracted.Value.Path),
            Body = forEach.Body,
            // Inspection-only bodies ride the non-consuming empty-snapshot path;
            // a not-inspection-only body under outermost rides the consuming
            // materialize-and-skip path (sound because outermost never nests).
            IsInspectionOnly = inspectionOnly,
            ConsumingOutermost = !inspectionOnly,
            Outermost = outermost,
            InlineDriven = _constructionDepth > 0,
        });
        return true;
    }

    private static bool PathHasDescendantAxis(PathExpression path)
    {
        foreach (var step in path.Steps)
            if (step.Axis is Axis.Descendant or Axis.DescendantOrSelf)
                return true;
        return false;
    }

    /// <summary>
    /// SM-ctx (OP-bucket phase 1): registers a for-each-style subscription for a
    /// consuming simple-map <c>LEFT ! RIGHT</c> whose LEFT is a plain striding
    /// child-axis path (the shape <see cref="TryBuildPathMatcher"/> accepts) and
    /// whose RIGHT consumes the streamed context node per item. The subscription
    /// carries RIGHT as <see cref="ForEachSubscription.PerItemSelect"/> and has no
    /// Body; the per-match dispatch materializes each matched item, binds it as the
    /// context item, and evaluates RIGHT in-memory.
    /// <para>
    /// Does NOT register (returns false, so the caller falls through to the Left-only
    /// Sequence watcher) when: RIGHT is a trailing snapshot step (the for-each /
    /// trailing-snapshot peel already handles it); LEFT is <c>outermost</c> or another
    /// absorbing/descendant-axis shape <see cref="TryBuildPathMatcher"/> rejects; or
    /// LEFT is grounded (does not navigate the input). The <c>NavigatesInput</c> guard
    /// keeps a grounded LEFT on its current path.
    /// </para>
    /// <para>
    /// A forward-window function LEFT (<c>head</c>/<c>tail</c>/<c>subsequence</c>/
    /// <c>remove</c>) over a plain striding path peels to its inner path plus a forward
    /// window (phase 3): the window fields ride along on the registered subscription so
    /// <c>head(p)!RIGHT</c> / <c>subsequence(p,s,l)!RIGHT</c> stream with the RIGHT
    /// applied per windowed item.
    /// </para>
    /// </summary>
    private bool TryRegisterSimpleMapContextSubscription(SimpleMapExpression sm)
    {
        // A trailing snapshot RIGHT (records/record/copy-of()) is handled by the
        // for-each / trailing-snapshot peel — not an SM-ctx per-item consuming shape.
        if (IsTrailingSnapshotStep(sm.Right)) return false;

        // Peel a forward-window function (subsequence/head/tail/remove) wrapping the
        // LEFT. The inner path must still decompose to a plain striding path; the
        // window rides along as dispatcher fields. outermost()/descendant-axis stay
        // unpeeled and are rejected below by TryBuildPathMatcher (deferred).
        var left = sm.Left;
        int subseqStart = 1;
        int? subseqLength = null;
        int? removeIndex = null;
        bool windowPeeled = false;
        if (TryPeelWindowFunction(left, out var inner, out var start, out var length, out var remIdx))
        {
            if (start < 1) return false;
            left = inner;
            subseqStart = start;
            subseqLength = length;
            removeIndex = remIdx;
            windowPeeled = true;

            // A peeled window over an ATOMIZED inner — fn:data(path) or a path whose
            // last step is an attribute — is NOT a plain striding ELEMENT path. The
            // per-item materialize-then-evaluate dispatch binds each windowed item as a
            // node context item; an atomized-attribute window mis-emits through that
            // path (it would regress the already-passing Sequence-watcher handling, e.g.
            // subsequence(data(.../@value), 5, 3) ! (position(), .)). Leave such shapes
            // unpeeled here so they stay on their working Sequence-watcher path.
            if (left is FunctionCallExpression dataWrap
                && dataWrap.Name.LocalName == "data"
                && dataWrap.Arguments.Count == 1
                && (dataWrap.Name.Namespace == NamespaceId.None
                    || dataWrap.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn))
                return false;
        }

        // LEFT must genuinely navigate the input. A grounded LEFT stays on its
        // current path (the grounded-operand guard).
        if (!StreamingSubtreeBufferDetector.NavigatesInput(left)) return false;

        // LEFT must decompose to a plain striding child-axis path the subscription
        // dispatcher can match (child-axis steps, last-step positional/attribute
        // predicate, attribute tail). outermost() / descendant-axis (//) LEFT is NOT a
        // matchable PathExpression, so TryBuildPathMatcher returns null and we leave it
        // unregistered (separate watcher effort).
        var matcher = TryBuildPathMatcher(left, out var textNodeTail, out var attributeName, out var predicates, out var intermediatePredicates, out _);
        if (matcher == null) return false;

        // A peeled window over an attribute-tailed path atomizes per item; the per-item
        // node-context dispatch mis-emits it (see the data() guard above). Leave it on
        // the Sequence-watcher path.
        if (windowPeeled && attributeName != null) return false;

        _subscriptions.Add(new ForEachSubscription
        {
            SourceInstruction = null,
            PathMatcher = matcher,
            Body = null,
            PerItemSelect = sm.Right,
            TextNodeTail = textNodeTail,
            AttributeName = attributeName,
            Predicates = predicates,
            IntermediatePredicates = intermediatePredicates,
            SubsequenceStart = subseqStart,
            SubsequenceLength = subseqLength,
            RemoveIndex = removeIndex,
            InlineDriven = _constructionDepth > 0,
        });
        return true;
    }

    /// <summary>
    /// ForExpr streaming (sx-ForExpr): registers a subscription for an XPath
    /// <c>for $x in CONSUMING-PATH return EXPR</c> whose <c>in</c> operand is a striding
    /// child-axis path ending in a per-item grounding step
    /// (<c>string()</c>/<c>data()</c>/<c>copy-of()</c>/<c>snapshot()</c>). The path minus
    /// its grounding step is matched against the stream; per matched element snapshot the
    /// grounding step is evaluated to produce the value bound to <c>$x</c>, then
    /// <c>EXPR</c> is evaluated and emitted.
    /// <para>
    /// Only the single-for-clause, single-binding shape (no positional/type/allowing-empty,
    /// no let/where/order-by/group-by) is accepted; anything else falls through to
    /// <see cref="ScanChildExpressions"/> (which scans the for-clause source) so it
    /// stays on the existing buffered path.
    /// </para>
    /// </summary>
    private bool TryRegisterForExpressionSubscription(FlworExpression flwor)
    {
        // Single `for $x in P` clause, single binding, no decorations; no other clauses.
        if (flwor.Clauses.Count != 1) return false;
        if (flwor.Clauses[0] is not ForClause forClause) return false;
        if (forClause.IsMember) return false;
        if (forClause.Bindings.Count != 1) return false;
        var binding = forClause.Bindings[0];
        if (binding.PositionalVariable != null) return false;
        if (binding.TypeDeclaration != null) return false;
        if (binding.AllowingEmpty) return false;

        // The `in` operand must be a striding path ending in a per-item grounding step
        // (string()/data()/copy-of()/snapshot()), built by the parser as a SimpleMap
        // whose Left is the path and whose Right is the zero-arg grounding function.
        if (binding.Expression is not SimpleMapExpression sm) return false;
        if (!IsForExprGroundingStep(sm.Right)) return false;

        // The Left path must decompose to a plain striding child-axis matcher.
        // Attribute-tail / text() tail paths are not part of the target ForExpr shape;
        // reject them (intermediate predicates ride along, mirroring SM-ctx).
        // A climbing `in` operand (DIMENSIONS/ancestor-or-self::*/@* — sx-for-005) is NOT
        // handled here: its streaming-snapshot identity semantics (a shared ancestor
        // attribute surfacing once across all matched items, not once per item) need a
        // larger mechanism than per-match snapshot evaluation, so it stays on the buffered
        // path. TryBuildPathMatcher returns null for it (non-child axis) → return false.
        var matcher = TryBuildPathMatcher(sm.Left, out var textNodeTail, out var attributeName, out var predicates, out var intermediatePredicates, out _);
        if (matcher == null) return false;
        if (textNodeTail || attributeName != null) return false;

        // LEFT must genuinely navigate the input (grounded LEFT stays on its own path).
        if (!StreamingSubtreeBufferDetector.NavigatesInput(sm.Left)) return false;

        _subscriptions.Add(new ForEachSubscription
        {
            SourceInstruction = null,
            PathMatcher = matcher,
            Body = null,
            PerItemSelect = flwor.ReturnExpression,
            RangeVariable = binding.Variable,
            RangeBindExpression = sm.Right,
            Predicates = predicates,
            IntermediatePredicates = intermediatePredicates,
            InlineDriven = _constructionDepth > 0,
        });
        return true;
    }

    /// <summary>
    /// True for a zero-argument per-item grounding step usable as the trailing step of a
    /// ForExpr <c>in</c> operand: <c>string()</c>, <c>data()</c>, <c>copy-of()</c>, or
    /// <c>snapshot()</c> in the fn namespace (or unprefixed). The function grounds the
    /// matched node into the value bound to the range variable.
    /// </summary>
    private static bool IsForExprGroundingStep(XQueryExpression expr)
    {
        return expr is FunctionCallExpression fc
            && fc.Arguments.Count == 0
            && (fc.Name.Namespace == NamespaceId.None || fc.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn)
            && fc.Name.LocalName is "string" or "data" or "copy-of" or "snapshot";
    }

    /// <summary>
    /// Recognizes the forward-decidable window functions over a streamed sequence and
    /// reduces each to a positional window the dispatcher applies during the forward
    /// pass (matched on the function's single sequence argument):
    /// <list type="bullet">
    ///   <item><c>subsequence(p, s)</c> / <c>subsequence(p, s, l)</c> → <c>(p, s, l, null)</c></item>
    ///   <item><c>head(p)</c> → <c>(p, 1, 1, null)</c></item>
    ///   <item><c>tail(p)</c> → <c>(p, 2, null, null)</c></item>
    ///   <item><c>remove(p, n)</c> → <c>(p, 1, null, n)</c> (skip the n-th match)</item>
    /// </list>
    /// Functions are recognized in the fn namespace or with no namespace prefix; the
    /// positional arguments must be constant integers (<see cref="TryConstantInt"/>).
    /// Returns false (caller falls back to buffered execution) for any other shape.
    /// </summary>
    private static bool TryPeelWindowFunction(
        XQueryExpression expr, out XQueryExpression inner, out int start, out int? length, out int? removeIndex)
    {
        inner = expr;
        start = 1;
        length = null;
        removeIndex = null;
        if (expr is not FunctionCallExpression fc) return false;
        if (fc.Name.Namespace != NamespaceId.None
            && fc.Name.Namespace != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn)
            return false;

        switch (fc.Name.LocalName)
        {
            case "subsequence":
                if (fc.Arguments.Count is not (2 or 3)) return false;
                if (!TryConstantInt(fc.Arguments[1], out start)) return false;
                if (fc.Arguments.Count == 3)
                {
                    if (!TryConstantInt(fc.Arguments[2], out var len)) return false;
                    length = len;
                }
                inner = fc.Arguments[0];
                return true;

            case "head":
                if (fc.Arguments.Count != 1) return false;
                start = 1;
                length = 1;
                inner = fc.Arguments[0];
                return true;

            case "tail":
                if (fc.Arguments.Count != 1) return false;
                start = 2;
                inner = fc.Arguments[0];
                return true;

            case "remove":
                if (fc.Arguments.Count != 2) return false;
                if (!TryConstantInt(fc.Arguments[1], out var n)) return false;
                if (n < 1) return false;
                start = 1;
                removeIndex = n;
                inner = fc.Arguments[0];
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// B2 — like <see cref="TryPeelWindowFunction"/> but for <c>subsequence</c>/<c>remove</c>
    /// whose positional bound(s) are GROUNDED non-literal expressions (a variable such as
    /// <c>$three</c>, or a grounded arithmetic on such) rather than compile-time integer
    /// literals. Such a bound is a constant per run — it does not navigate the input — so it
    /// is forward-decidable: the processor evaluates it ONCE against the live XSLT scope when
    /// the subscription activates. Returns the inner sequence plus the bound EXPRESSIONS
    /// (<paramref name="startExpr"/> etc.), leaving numeric folding to the dispatcher.
    /// <para>
    /// Rejects (returns false, → buffer fallback, never a silent drop) any bound that
    /// navigates the input (<see cref="StreamingSubtreeBufferDetector.NavigatesInput"/>) —
    /// a stream-derived window is not forward-decidable — and any shape
    /// <see cref="TryPeelWindowFunction"/> already handles as a literal. <c>head</c>/<c>tail</c>
    /// have no variable bound, so they are not handled here.
    /// </para>
    /// </summary>
    private static bool TryPeelWindowFunctionExpr(
        XQueryExpression expr,
        out XQueryExpression inner,
        out XQueryExpression? startExpr,
        out XQueryExpression? lengthExpr,
        out XQueryExpression? removeIndexExpr)
    {
        inner = expr;
        startExpr = null;
        lengthExpr = null;
        removeIndexExpr = null;
        if (expr is not FunctionCallExpression fc) return false;
        if (fc.Name.Namespace != NamespaceId.None
            && fc.Name.Namespace != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn)
            return false;

        static bool GroundedBound(XQueryExpression e)
            => !TryConstantInt(e, out _)               // literal ints are handled elsewhere
               && !StreamingSubtreeBufferDetector.NavigatesInput(e); // must not touch the stream

        switch (fc.Name.LocalName)
        {
            case "subsequence":
                if (fc.Arguments.Count is not (2 or 3)) return false;
                // At least the start must be a grounded non-literal for this path to apply.
                if (!GroundedBound(fc.Arguments[1])) return false;
                startExpr = fc.Arguments[1];
                if (fc.Arguments.Count == 3)
                {
                    // Length may be a literal int or a grounded expression; either way it
                    // must not navigate the input.
                    if (StreamingSubtreeBufferDetector.NavigatesInput(fc.Arguments[2])) return false;
                    lengthExpr = fc.Arguments[2];
                }
                inner = fc.Arguments[0];
                return true;

            case "remove":
                if (fc.Arguments.Count != 2) return false;
                if (!GroundedBound(fc.Arguments[1])) return false;
                removeIndexExpr = fc.Arguments[1];
                inner = fc.Arguments[0];
                return true;

            default:
                return false;
        }
    }

    private static bool TryConstantInt(XQueryExpression expr, out int value)
    {
        switch (expr)
        {
            case IntegerLiteral { LongValue: { } l }:
                value = (int)l;
                return true;
            case DecimalLiteral dl when dl.Value == decimal.Truncate(dl.Value):
                value = (int)dl.Value;
                return true;
            case DoubleLiteral dbl when dbl.Value == Math.Truncate(dbl.Value):
                value = (int)dbl.Value;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>
    /// Recognizes <paramref name="select"/> as either a single streamable absolute
    /// child-axis path (with optional text() KindTest at the last step) OR a comma
    /// sequence with exactly one streamable path operand surrounded by zero or more
    /// "grounded" operands (literals, variable refs).
    /// </summary>
    private static bool TryDecomposeForEachSelect(
        XQueryExpression select,
        out IReadOnlyList<XQueryExpression> prefix,
        out StreamPathMatcher path,
        out bool textNodeTail,
        out IReadOnlyList<XQueryExpression> suffix,
        out string? attributeName,
        out IReadOnlyList<XQueryExpression> predicates,
        out IReadOnlyList<IntermediatePredicate> intermediatePredicates,
        out bool atomize)
    {
        prefix = Array.Empty<XQueryExpression>();
        suffix = Array.Empty<XQueryExpression>();
        path = null!;
        textNodeTail = false;
        attributeName = null;
        predicates = Array.Empty<XQueryExpression>();
        intermediatePredicates = Array.Empty<IntermediatePredicate>();
        atomize = false;

        var singleMatcher = TryBuildPathMatcher(select, out textNodeTail, out attributeName, out predicates, out intermediatePredicates, out atomize);
        if (singleMatcher != null)
        {
            path = singleMatcher;
            return true;
        }
        textNodeTail = false;
        attributeName = null;
        predicates = Array.Empty<XQueryExpression>();
        intermediatePredicates = Array.Empty<IntermediatePredicate>();
        atomize = false;

        // AST uses SequenceExpression with Items property for comma sequences.
        if (select is not SequenceExpression seq) return false;
        var operands = seq.Items;
        if (operands == null || operands.Count < 2) return false;

        int streamableIndex = -1;
        for (int i = 0; i < operands.Count; i++)
        {
            var inner = TryBuildPathMatcher(operands[i], out var innerTail, out var innerAttr, out var innerPreds, out var innerInterPreds, out var innerAtomize);
            if (inner == null) continue;
            if (streamableIndex >= 0) return false; // more than one streamable operand
            streamableIndex = i;
            path = inner;
            textNodeTail = innerTail;
            attributeName = innerAttr;
            predicates = innerPreds;
            intermediatePredicates = innerInterPreds;
            atomize = innerAtomize;
        }
        if (streamableIndex < 0) return false;

        for (int i = 0; i < operands.Count; i++)
        {
            if (i == streamableIndex) continue;
            if (!IsGroundedForStreaming(operands[i])) return false;
        }

        var prefixArr = new XQueryExpression[streamableIndex];
        for (int i = 0; i < streamableIndex; i++) prefixArr[i] = operands[i];
        var suffixArr = new XQueryExpression[operands.Count - streamableIndex - 1];
        for (int i = streamableIndex + 1; i < operands.Count; i++) suffixArr[i - streamableIndex - 1] = operands[i];
        prefix = prefixArr;
        suffix = suffixArr;
        return true;
    }

    /// <summary>
    /// True when <paramref name="expr"/> can be evaluated outside the streaming pass
    /// without consuming the source stream. Conservatively accepts literals and
    /// variable refs; rejects PathExpression and anything else.
    /// </summary>
    private static bool IsGroundedForStreaming(XQueryExpression expr)
    {
        return expr switch
        {
            IntegerLiteral or DecimalLiteral or DoubleLiteral or StringLiteral or BooleanLiteral => true,
            VariableReference => true,
            PathExpression => false,
            _ => false,
        };
    }

    /// <summary>
    /// Builds a <see cref="StreamPathMatcher"/> from an XPath expression when
    /// it is an absolute path consisting purely of child-axis name-test steps
    /// with no predicates, optionally ending in a <c>text()</c> KindTest
    /// (signaled via <paramref name="textNodeTail"/>). Returns null for any
    /// other shape so the buffered execution path can handle it.
    /// </summary>
    private static StreamPathMatcher? TryBuildPathMatcher(
        XQueryExpression select,
        out bool textNodeTail,
        out string? attributeName,
        out IReadOnlyList<XQueryExpression> predicates,
        out IReadOnlyList<IntermediatePredicate> intermediatePredicates,
        out bool atomize)
    {
        textNodeTail = false;
        attributeName = null;
        predicates = Array.Empty<XQueryExpression>();
        intermediatePredicates = Array.Empty<IntermediatePredicate>();
        atomize = false;
        // Captured (forward-countable-positional / motionless) predicates on intermediate
        // (non-leaf) child-axis element steps, paired with the part index they occupy in
        // `parts` so the ancestor offset (levels above the matched leaf) can be derived
        // once the full element-step count is known.
        List<(int PartIndex, IReadOnlyList<XQueryExpression> Predicates, bool IsPositional, bool IsWildcard)>? capturedIntermediate = null;
        // Peek through fn:data(path) — for untyped nodes, data() returns the string value
        // which equals what value-of/sequence emits for the node directly. When the body
        // merely READS the context item (value-of/sequence) unwrapping is transparent; when
        // the body COPIES it (<xsl:copy/>) the atomization is load-bearing (an attribute
        // node would copy as an attribute, an atomic copies as text). Signal `atomize` so
        // the subscription dispatch pushes the ATOMIZED value as the context item — see
        // ForEachSubscription.AtomizeContextItem (si-copy-002).
        if (select is FunctionCallExpression dataCall
            && dataCall.Name.LocalName == "data"
            && dataCall.Arguments.Count == 1
            && (dataCall.Name.Namespace == NamespaceId.None || dataCall.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn))
        {
            select = dataCall.Arguments[0];
            atomize = true;
        }
        // Peek through a trailing copy-of()/snapshot() step — e.g.
        // `records/record/copy-of()`. The parser builds this as a SimpleMap whose
        // Left is the streamable path and whose Right is a zero-argument
        // copy-of()/snapshot() applied per item (snapshot of the context node).
        // For a streamable for-each, dispatching the body per matched element with
        // that element's materialized snapshot as context is exactly what the
        // streaming subscription already does — so the trailing snapshot step is a
        // no-op for path-matching purposes and we proceed with the Left path.
        if (select is SimpleMapExpression simpleMap && IsTrailingSnapshotStep(simpleMap.Right))
        {
            select = simpleMap.Left;
        }
        if (select is not PathExpression path) return null;
        // Accept either absolute (/path) or relative-from-root (path) when no initial
        // expression is set. Source-document body's implicit context item is the
        // document root, so relative paths have the same semantics as absolute.
        if (!path.IsAbsolute && path.InitialExpression != null) return null;
        if (path.Steps.Count == 0) return null;

        var parts = new List<string>(path.Steps.Count);
        for (int i = 0; i < path.Steps.Count; i++)
        {
            var step = path.Steps[i];
            bool isLastStep = i == path.Steps.Count - 1;

            // Allow Axis.Attribute on the LAST step with a NameTest, signaling the
            // processor to dispatch the matched element's attribute by that name.
            if (isLastStep && step.Axis == Axis.Attribute)
            {
                if (step.NodeTest is not NameTest attrName) return null;
                if (attrName.IsLocalNameWildcard || attrName.IsNamespaceWildcard) return null;
                if (step.Predicates.Count > 0) return null; // predicates on the attribute step itself — skip
                attributeName = attrName.LocalName;
                break;
            }

            // Only fixed-depth child-axis element steps are streamable through the
            // for-each SUBSCRIPTION path, which materializes-and-skips the matched
            // element's whole subtree from the live reader. A descendant-axis (`//`)
            // wildcard would be UNSOUND here: an ancestor element can also satisfy the
            // any-depth pattern, and matching it consumes its descendants before they
            // are seen (e.g. `//node()[name()=$g]` matches the root, swallowing the
            // very nodes the predicate would select). Descendant for-each remains
            // DEFERRED — it needs the non-consuming watcher mechanism, not this one.
            if (step.Axis != Axis.Child) return null;

            // Predicates: the step that supplies the matched element context — i.e., the
            // last element step OR the step immediately before an attribute-tail step —
            // carries the final-step Predicates (last-step path, unchanged). A predicate
            // on an EARLIER (intermediate, non-leaf) child-axis element step is captured
            // as an IntermediatePredicate ONLY when it is forward-countable-positional
            // (employee[1]) or motionless against the ancestor (department[@name='sales']);
            // it is evaluated in the subscription dispatch against the matched node's
            // ancestor before the body fires. A predicate that is NEITHER stays REJECTED
            // (return null → buffered fallback) — never silently dropped.
            if (step.Predicates.Count > 0)
            {
                bool isLastElementStep = isLastStep
                    || (i == path.Steps.Count - 2 && path.Steps[i + 1].Axis == Axis.Attribute);
                if (isLastElementStep)
                {
                    predicates = step.Predicates;
                }
                else
                {
                    // Intermediate (non-leaf) child-axis element-step predicate. `parts.Count`
                    // is the index this step will occupy once added below.
                    bool wildcardStep = step.NodeTest is NameTest ntw && ntw.IsLocalNameWildcard;
                    if (ArePredicatesMotionlessAgainstAncestor(step.Predicates))
                    {
                        (capturedIntermediate ??= []).Add((parts.Count, step.Predicates, false, wildcardStep));
                    }
                    else if (ArePredicatesForwardCountablePositional(step.Predicates))
                    {
                        (capturedIntermediate ??= []).Add((parts.Count, step.Predicates, true, wildcardStep));
                    }
                    else
                    {
                        return null; // neither forward-countable-positional nor motionless
                    }
                }
            }

            // text() KindTest is acceptable only as the LAST step.
            if (isLastStep
                && step.NodeTest is KindTest kindTest
                && kindTest.Kind == XdmNodeKind.Text
                && kindTest.Name == null
                && kindTest.TypeName == null)
            {
                textNodeTail = true;
                break;
            }

            // Determine the element-name pattern for this CHILD-axis step:
            //   - NameTest with a concrete local name → that name
            //   - NameTest local-name wildcard (`*`)  → "*" (any element child)
            // The StreamPathMatcher already honors "*" as a per-step element wildcard
            // and still enforces the overall step count / ancestor alignment, so
            // `/*/*` matches grandchildren-of-root only (never the root, never
            // great-grandchildren).
            //
            // The flat matcher keys purely on LOCAL names — it never carried a
            // namespace constraint, so a concrete prefix step (`gml:description`) has
            // always matched by local name alone. That makes a NAMESPACE-wildcard
            // step (`*:name`, "any namespace, this local name") exactly what the
            // matcher already does: fold it to the local name. `*:*` (both wildcards)
            // is any element, i.e. "*". A local-name wildcard bound to a SPECIFIC
            // namespace (`ns:*`) would require a namespace constraint the flat matcher
            // can't honor, so that case remains deferred.
            if (step.NodeTest is not NameTest nameTest) return null;
            if (nameTest.IsLocalNameWildcard)
            {
                // `ns:*` (specific namespace + local wildcard) needs a namespace
                // constraint the matcher can't apply; only fully-open `*` / `*:*`
                // fold to the element wildcard.
                if (nameTest.NamespaceUri is not null && !nameTest.IsNamespaceWildcard)
                    return null;
                parts.Add("*");
            }
            else
            {
                parts.Add(nameTest.LocalName);
            }
        }

        if (parts.Count == 0) return null;

        // The matched leaf element is the LAST element-name part. An intermediate
        // predicate captured at part index `p` sits (parts.Count-1 - p) element levels
        // above the leaf — the IntermediatePredicate.AncestorOffset convention (1 ==
        // immediate parent), matching how the subscription dispatch indexes _ancestorNames
        // (idx = _ancestorNames.Count - AncestorOffset) at the matched leaf's StartElement.
        if (capturedIntermediate is { Count: > 0 })
        {
            int leafIndex = parts.Count - 1;
            var ips = new List<IntermediatePredicate>(capturedIntermediate.Count);
            foreach (var (partIndex, preds, isPositional, isWildcard) in capturedIntermediate)
            {
                int ancestorOffset = leafIndex - partIndex;
                ips.Add(new IntermediatePredicate(ancestorOffset, preds, isPositional, isWildcard));
            }
            intermediatePredicates = ips;
        }

        return new StreamPathMatcher(string.Join("/", parts));
    }
}
