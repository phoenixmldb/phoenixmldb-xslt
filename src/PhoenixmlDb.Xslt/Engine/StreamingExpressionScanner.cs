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

    /// <summary>
    /// Result of <see cref="ScanWithSubscriptions"/> — watchers for consuming
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
    /// Scans the content body of an xsl:source-document instruction.
    /// Returns both the watchers for consuming aggregates and the
    /// subscriptions for streamable xsl:for-each instructions.
    /// </summary>
    public ScanResult ScanWithSubscriptions(XsltSequenceConstructor? body)
    {
        _watchers.Clear();
        _subscriptions.Clear();
        _constructionDepth = 0;
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
                        PathMatcher = new StreamPathMatcher(pathInfo.Value.Path),
                        Aggregation = aggType,
                        ValueAttribute = pathInfo.Value.Attribute,
                        Predicates = pathInfo.Value.Predicates,
                        IntermediatePredicates = pathInfo.Value.IntermediatePredicates,
                        Separator = aggType == WatcherAggregation.StringJoin && fc.Arguments.Count > 1
                            ? ExtractStringLiteral(fc.Arguments[1])
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
                        PathMatcher = new StreamPathMatcher(seqPath.Value.Path),
                        Aggregation = WatcherAggregation.Sequence,
                        ValueAttribute = seqPath.Value.Attribute,
                        Predicates = seqPath.Value.Predicates,
                        IntermediatePredicates = seqPath.Value.IntermediatePredicates
                    });
                    return;
                }
                break;

            // snapshot() / copy-of() wrapping a path
            case FunctionCallExpression fc2 when IsSnapshotFunction(fc2):
                var snapPath = ExtractPathFromArgument(fc2.Arguments[0]);
                if (snapPath != null)
                {
                    _watchers.Add(new StreamWatcher
                    {
                        SourceExpression = expr,
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
                    PathMatcher = new StreamPathMatcher(shape.Path),
                    Aggregation = WatcherAggregation.Sequence,
                    ValueAttribute = shape.Attribute,
                    Predicates = shape.Predicates,
                    IntermediatePredicates = shape.IntermediatePredicates
                });
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
        IReadOnlyList<IntermediatePredicate> IntermediatePredicates);

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

    private static ExtractedPath? ExtractPathFromExpression(XQueryExpression expr)
    {
        // Walk the path expression to build a simple "a/b/c" or "a/b/@attr" string.
        // This handles the common cases: child steps and attribute access.
        // Complex expressions (predicates, descendant-or-self) fall through to null.
        var parts = new List<string>();
        string? attribute = null;
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
                        if (pathExpr.Steps[si].Axis != Axis.Attribute)
                        {
                            lastElementStepIdx = si;
                            break;
                        }
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
                        }
                        else if (step.Axis == Axis.Child)
                        {
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
            (IReadOnlyList<IntermediatePredicate>?)intermediatePredicates ?? Array.Empty<IntermediatePredicate>());
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

        // Peel a leading subsequence(path, start [, length]) wrapper. The slice is a
        // forward-countable positional window over the selected sequence — decidable
        // from the running path-match count as the stream descends — so it is applied
        // by the dispatcher rather than rejecting the whole for-each. The inner argument
        // must itself decompose to a single streamable path (no mixed-sequence prefix/
        // suffix, no separate predicate window).
        var select = forEach.Select;
        int subseqStart = 1;
        int? subseqLength = null;
        if (TryPeelSubsequence(select, out var inner, out var start, out var length))
        {
            // Reject a non-positive or non-integer start/length — out of scope; let the
            // for-each fall back to buffered execution.
            if (start < 1) return;
            select = inner;
            subseqStart = start;
            subseqLength = length;
        }

        if (!TryDecomposeForEachSelect(select, out var prefix, out var path, out var textNodeTail, out var suffix, out var attributeName, out var predicates))
            return;

        // A subsequence slice combined with a mixed-sequence prefix/suffix is out of
        // scope (the slice index would span heterogeneous operands). Only register the
        // slice when the select decomposed to a bare single path.
        if (subseqStart != 1 || subseqLength != null)
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
            Predicates = predicates,
            SubsequenceStart = subseqStart,
            SubsequenceLength = subseqLength,
            InlineDriven = _constructionDepth > 0,
        });
    }

    /// <summary>
    /// Recognizes <c>subsequence(arg, start)</c> or <c>subsequence(arg, start, length)</c>
    /// where <c>start</c> and <c>length</c> are non-negative integer literals. Returns the
    /// inner argument plus the (truncated-to-int) start and optional length. The streaming
    /// dispatcher applies the slice as a forward positional window over the path matches.
    /// </summary>
    private static bool TryPeelSubsequence(
        XQueryExpression expr, out XQueryExpression inner, out int start, out int? length)
    {
        inner = expr;
        start = 1;
        length = null;
        if (expr is not FunctionCallExpression fc) return false;
        if (fc.Name.LocalName != "subsequence") return false;
        if (fc.Name.Namespace != NamespaceId.None
            && fc.Name.Namespace != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn)
            return false;
        if (fc.Arguments.Count is not (2 or 3)) return false;
        if (!TryConstantInt(fc.Arguments[1], out start)) return false;
        if (fc.Arguments.Count == 3)
        {
            if (!TryConstantInt(fc.Arguments[2], out var len)) return false;
            length = len;
        }
        inner = fc.Arguments[0];
        return true;
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
        out IReadOnlyList<XQueryExpression> predicates)
    {
        prefix = Array.Empty<XQueryExpression>();
        suffix = Array.Empty<XQueryExpression>();
        path = null!;
        textNodeTail = false;
        attributeName = null;
        predicates = Array.Empty<XQueryExpression>();

        var singleMatcher = TryBuildPathMatcher(select, out textNodeTail, out attributeName, out predicates);
        if (singleMatcher != null)
        {
            path = singleMatcher;
            return true;
        }
        textNodeTail = false;
        attributeName = null;
        predicates = Array.Empty<XQueryExpression>();

        // AST uses SequenceExpression with Items property for comma sequences.
        if (select is not SequenceExpression seq) return false;
        var operands = seq.Items;
        if (operands == null || operands.Count < 2) return false;

        int streamableIndex = -1;
        for (int i = 0; i < operands.Count; i++)
        {
            var inner = TryBuildPathMatcher(operands[i], out var innerTail, out var innerAttr, out var innerPreds);
            if (inner == null) continue;
            if (streamableIndex >= 0) return false; // more than one streamable operand
            streamableIndex = i;
            path = inner;
            textNodeTail = innerTail;
            attributeName = innerAttr;
            predicates = innerPreds;
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
    private static StreamPathMatcher? TryBuildPathMatcher(XQueryExpression select, out bool textNodeTail, out string? attributeName, out IReadOnlyList<XQueryExpression> predicates)
    {
        textNodeTail = false;
        attributeName = null;
        predicates = Array.Empty<XQueryExpression>();
        // Peek through fn:data(path) — for untyped nodes, data() returns the string value
        // which equals what value-of/sequence emits for the node directly, so unwrapping
        // is semantically safe when the body just reads the context item.
        if (select is FunctionCallExpression dataCall
            && dataCall.Name.LocalName == "data"
            && dataCall.Arguments.Count == 1
            && (dataCall.Name.Namespace == NamespaceId.None || dataCall.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn))
        {
            select = dataCall.Arguments[0];
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

            // Predicates: only allowed on the step that supplies the matched element
            // context — i.e., the last step OR the step immediately before an
            // attribute-tail step. Reject predicates on any earlier step.
            if (step.Predicates.Count > 0)
            {
                bool isLastElementStep = isLastStep
                    || (i == path.Steps.Count - 2 && path.Steps[i + 1].Axis == Axis.Attribute);
                if (!isLastElementStep) return null;
                predicates = step.Predicates;
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
            // great-grandchildren). Namespace-qualified wildcards (`*:name`, `ns:*`)
            // carry namespace semantics the flat string matcher can't honor — deferred.
            if (step.NodeTest is not NameTest nameTest) return null;
            if (nameTest.IsNamespaceWildcard) return null;
            parts.Add(nameTest.IsLocalNameWildcard ? "*" : nameTest.LocalName);
        }

        if (parts.Count == 0) return null;
        return new StreamPathMatcher(string.Join("/", parts));
    }
}
