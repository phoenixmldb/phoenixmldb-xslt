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
    /// Walks an expression tree and substitutes any watched sub-expression
    /// (by reference identity against <paramref name="watchers"/>) with a
    /// <see cref="VariableReference"/> to the synthetic
    /// <c>$__streaming_watcher_N</c> binding. Returns the original
    /// <paramref name="expr"/> unchanged when no watched node is found anywhere
    /// inside, so the plan cache stays warm for non-streaming hot paths.
    /// Handles only the AST shapes that matter for current streaming patterns
    /// (PathExpression with InitialExpression and FunctionCallExpression args);
    /// other shapes are returned unchanged but their children are still walked
    /// when descending through the recognised shapes.
    /// </summary>
    // internal (not private) so the streamability unit tests can assert on the
    // rewritten AST directly (Task 1.1). Behaviour is unchanged for callers.
    internal static XQueryExpression RewriteWithWatcherVariables(
        PhoenixmlDb.XQuery.Ast.XQueryExpression expr,
        IReadOnlyList<StreamWatcher> watchers)
    {
        // Direct match: replace the whole node with $__streaming_watcher_N.
        for (var wi = 0; wi < watchers.Count; wi++)
        {
            var w = watchers[wi];
            if (ReferenceEquals(expr, w.SourceExpression))
            {
                var varRef = new PhoenixmlDb.XQuery.Ast.VariableReference
                {
                    Name = new QName(NamespaceId.None, $"__streaming_watcher_{wi}")
                };

                // Group A wrapped-aggregation watcher (outermost/head/remove(path)
                // carrying OUTER positional predicates). The watcher's SourceExpression
                // is the WHOLE FilterExpression `wrapped(path)[pred…]`, but the bound
                // $__streaming_watcher_N variable holds only the GROUNDED BASE sequence
                // (inner result + outermost-dedup + remove-skip) — the outer predicates
                // are NOT baked into the variable value. When this watcher is used as an
                // operand of a surrounding expression (e.g. `outermost(//PRICE)[1] + $two`),
                // substituting the bare variable DROPS the outer `[1]`, yielding the whole
                // unfiltered sequence and an "arithmetic operand is a sequence of more than
                // one item" error (sx-arithmetic-002 regression). Preserve the outer
                // predicates by re-wrapping the variable in a FilterExpression:
                // `$__streaming_watcher_N[pred…]`. NB this branch only fires for wrappers
                // with OuterPredicates; a filtered attribute-axis general-comparison watcher
                // ((a/b/@v[pred]) = C) folds its predicate into the inner path (empty
                // OuterPredicates), so the whole operand is substituted with no residual
                // predicate — the two cases stay distinct.
                if (w.OuterPredicates.Count > 0)
                {
                    return new PhoenixmlDb.XQuery.Ast.FilterExpression
                    {
                        Primary = varRef,
                        Predicates = w.OuterPredicates,
                    };
                }

                // SimpleMap-source Sequence watcher (`path ! TAIL`): the bound variable
                // holds only the RAW captured LEFT leaves — the per-item TAIL (`.+1`,
                // `xs:decimal(.)`, …) is NOT baked in. Substituting the bare variable drops
                // the tail, so a NESTED use — e.g. `(path)!(.+1) = 5.95` inside an
                // `if(...)` condition — would compare the raw leaves and pick the wrong
                // branch (sx-if-213). Preserve the tail by re-attaching it to the variable:
                // rebuild the SimpleMap chain with the deep-left path replaced by
                // $__streaming_watcher_N, so the tail re-applies per grounded leaf. (The
                // top-level value-of / general-comparison paths re-apply the tail before
                // reaching here and return early, so this only affects nested uses.)
                if (w.SourceExpression is PhoenixmlDb.XQuery.Ast.SimpleMapExpression smSrc)
                {
                    return ReplaceSimpleMapLeftmost(smSrc, varRef);
                }

                return varRef;
            }
        }

        switch (expr)
        {
            case PhoenixmlDb.XQuery.Ast.PathExpression pe:
            {
                var initial = pe.InitialExpression;
                XQueryExpression? newInitial = null;
                if (initial != null)
                {
                    var rw = RewriteWithWatcherVariables(initial, watchers);
                    if (!ReferenceEquals(rw, initial)) newInitial = rw;
                }
                // Steps may carry predicates with watched sub-expressions; for
                // the current shape (snapshot(path)//tail/@attr) predicates are
                // not exercised, so leave them as-is to avoid touching shape
                // we have no test coverage for. If a future shape needs it,
                // extend here.
                if (newInitial == null) return expr;
                return new PhoenixmlDb.XQuery.Ast.PathExpression
                {
                    IsAbsolute = pe.IsAbsolute,
                    InitialExpression = newInitial,
                    Steps = pe.Steps,
                };
            }

            case PhoenixmlDb.XQuery.Ast.FunctionCallExpression fc:
            {
                XQueryExpression[]? newArgs = null;
                for (var i = 0; i < fc.Arguments.Count; i++)
                {
                    var orig = fc.Arguments[i];
                    var rw = RewriteWithWatcherVariables(orig, watchers);
                    if (!ReferenceEquals(rw, orig))
                    {
                        if (newArgs == null)
                        {
                            newArgs = new XQueryExpression[fc.Arguments.Count];
                            for (var j = 0; j < i; j++) newArgs[j] = fc.Arguments[j];
                        }
                        newArgs[i] = rw;
                    }
                    else if (newArgs != null)
                    {
                        newArgs[i] = orig;
                    }
                }
                if (newArgs == null) return expr;
                return new PhoenixmlDb.XQuery.Ast.FunctionCallExpression
                {
                    Name = fc.Name,
                    Arguments = newArgs,
                };
            }

            // Binary operator (arithmetic / value / general comparison) whose
            // operands may be watched sub-expressions — e.g. a lone
            // (a/b/@v[pred]) = 4.32 where the LEFT is a streamed filtered
            // attribute sequence. Substitute each watched operand with its
            // $__streaming_watcher_N variable so the comparison runs against the
            // accumulated sequence instead of re-evaluating the (now-closed)
            // path against the synthetic empty document (which short-circuits
            // to false). Without this the whole binary is returned unchanged and
            // the watcher value is never seen — the regression behind
            // sx-GeneralComp-*-019/119 over a predicated attribute axis.
            case PhoenixmlDb.XQuery.Ast.BinaryExpression bin:
            {
                var newLeft = RewriteWithWatcherVariables(bin.Left, watchers);
                var newRight = RewriteWithWatcherVariables(bin.Right, watchers);
                if (ReferenceEquals(newLeft, bin.Left) && ReferenceEquals(newRight, bin.Right))
                    return expr;
                return new PhoenixmlDb.XQuery.Ast.BinaryExpression
                {
                    Left = newLeft,
                    Operator = bin.Operator,
                    Right = newRight,
                };
            }

            // SimpleMap: descend into Left so a watched LHS (e.g.
            // outermost(//PRICE) inside `outermost(//PRICE) ! string(.)`) gets
            // replaced with $__streaming_watcher_N. The Right is a per-item
            // expression that runs against each materialized item — leave it
            // unchanged so per-item context evaluation works naturally.
            case PhoenixmlDb.XQuery.Ast.SimpleMapExpression sme:
            {
                var newLeft = RewriteWithWatcherVariables(sme.Left, watchers);
                if (ReferenceEquals(newLeft, sme.Left)) return expr;
                return new PhoenixmlDb.XQuery.Ast.SimpleMapExpression
                {
                    Left = newLeft,
                    Right = sme.Right,
                    IsPathStep = sme.IsPathStep,
                };
            }

            // Task 1.1 — operator-node recursion (the `if`/`exists`/`=` false-branch
            // cluster). A watched striding base path reached through one of these
            // operators was previously left un-substituted, so at eval it ran against
            // the closed synthetic document → empty sequence → `exists()`=false,
            // `path = literal`=false, etc. Reconstruct each node with its rewritten
            // children so the watched path becomes $__streaming_watcher_N.

            // if (test) then A else B — recurse into all three branches. The test
            // is the dominant case (e.g. `if (exists(<streamed-path>)) then 0 else 1`).
            case PhoenixmlDb.XQuery.Ast.IfExpression iff:
            {
                var newCond = RewriteWithWatcherVariables(iff.Condition, watchers);
                var newThen = RewriteWithWatcherVariables(iff.Then, watchers);
                var newElse = iff.Else != null
                    ? RewriteWithWatcherVariables(iff.Else, watchers)
                    : null;
                if (ReferenceEquals(newCond, iff.Condition)
                    && ReferenceEquals(newThen, iff.Then)
                    && ReferenceEquals(newElse, iff.Else))
                    return expr;
                return new PhoenixmlDb.XQuery.Ast.IfExpression
                {
                    Condition = newCond,
                    Then = newThen,
                    Else = newElse,
                };
            }

            // (a, b, c) — a sequence/comma. A watched path may be one item of the
            // sequence (e.g. `(<streamed-path>, 31, 32) = 346`). Rewrite each item.
            case PhoenixmlDb.XQuery.Ast.SequenceExpression seq:
            {
                XQueryExpression[]? newItems = null;
                for (var i = 0; i < seq.Items.Count; i++)
                {
                    var orig = seq.Items[i];
                    var rw = RewriteWithWatcherVariables(orig, watchers);
                    if (!ReferenceEquals(rw, orig))
                    {
                        if (newItems == null)
                        {
                            newItems = new XQueryExpression[seq.Items.Count];
                            for (var j = 0; j < i; j++) newItems[j] = seq.Items[j];
                        }
                        newItems[i] = rw;
                    }
                    else if (newItems != null)
                    {
                        newItems[i] = orig;
                    }
                }
                if (newItems == null) return expr;
                return new PhoenixmlDb.XQuery.Ast.SequenceExpression { Items = newItems };
            }

            // some/every $x in <expr> satisfies <expr> — a watched path may be an
            // in-clause source or appear in the satisfies body. Rewrite each binding
            // source and the satisfies expression.
            case PhoenixmlDb.XQuery.Ast.QuantifiedExpression quant:
            {
                PhoenixmlDb.XQuery.Ast.QuantifiedBinding[]? newBindings = null;
                for (var i = 0; i < quant.Bindings.Count; i++)
                {
                    var b = quant.Bindings[i];
                    var rw = RewriteWithWatcherVariables(b.Expression, watchers);
                    if (!ReferenceEquals(rw, b.Expression))
                    {
                        if (newBindings == null)
                        {
                            newBindings = new PhoenixmlDb.XQuery.Ast.QuantifiedBinding[quant.Bindings.Count];
                            for (var j = 0; j < quant.Bindings.Count; j++) newBindings[j] = quant.Bindings[j];
                        }
                        newBindings[i] = new PhoenixmlDb.XQuery.Ast.QuantifiedBinding
                        {
                            Variable = b.Variable,
                            TypeDeclaration = b.TypeDeclaration,
                            Expression = rw,
                        };
                    }
                }
                var newSatisfies = RewriteWithWatcherVariables(quant.Satisfies, watchers);
                if (newBindings == null && ReferenceEquals(newSatisfies, quant.Satisfies))
                    return expr;
                return new PhoenixmlDb.XQuery.Ast.QuantifiedExpression
                {
                    Quantifier = quant.Quantifier,
                    Bindings = newBindings ?? quant.Bindings,
                    Satisfies = newSatisfies,
                };
            }

            // <expr> instance of T — recurse into the operand.
            case PhoenixmlDb.XQuery.Ast.InstanceOfExpression iof:
            {
                var newInner = RewriteWithWatcherVariables(iof.Expression, watchers);
                if (ReferenceEquals(newInner, iof.Expression)) return expr;
                return new PhoenixmlDb.XQuery.Ast.InstanceOfExpression
                {
                    Expression = newInner,
                    TargetType = iof.TargetType,
                };
            }

            // <expr> treat as T — recurse into the operand.
            case PhoenixmlDb.XQuery.Ast.TreatExpression treat:
            {
                var newInner = RewriteWithWatcherVariables(treat.Expression, watchers);
                if (ReferenceEquals(newInner, treat.Expression)) return expr;
                return new PhoenixmlDb.XQuery.Ast.TreatExpression
                {
                    Expression = newInner,
                    TargetType = treat.TargetType,
                };
            }

            // <expr> cast as T — recurse into the operand.
            case PhoenixmlDb.XQuery.Ast.CastExpression cast:
            {
                var newInner = RewriteWithWatcherVariables(cast.Expression, watchers);
                if (ReferenceEquals(newInner, cast.Expression)) return expr;
                return new PhoenixmlDb.XQuery.Ast.CastExpression
                {
                    Expression = newInner,
                    TargetType = cast.TargetType,
                };
            }

            // <expr> castable as T — recurse into the operand.
            case PhoenixmlDb.XQuery.Ast.CastableExpression castable:
            {
                var newInner = RewriteWithWatcherVariables(castable.Expression, watchers);
                if (ReferenceEquals(newInner, castable.Expression)) return expr;
                return new PhoenixmlDb.XQuery.Ast.CastableExpression
                {
                    Expression = newInner,
                    TargetType = castable.TargetType,
                };
            }

            // Task 1.2 — (Primary)[pred…] filter whose Primary is a watched striding
            // base path (e.g. `(/BOOKLIST/BOOKS/ITEM/PRICE)[1]` in sx-arithmetic-001).
            // Substitute the Primary with $__streaming_watcher_N and keep the outer
            // predicates so `[1]` applies to the grounded materialized sequence in
            // memory. NB the whole-FilterExpression-is-the-watched-source case (wrapped
            // head/outermost/remove aggregation carrying OuterPredicates) is handled by
            // the reference-equality direct-match block above and never reaches here.
            case PhoenixmlDb.XQuery.Ast.FilterExpression filt:
            {
                var newPrimary = RewriteWithWatcherVariables(filt.Primary, watchers);
                if (ReferenceEquals(newPrimary, filt.Primary)) return expr;
                return new PhoenixmlDb.XQuery.Ast.FilterExpression
                {
                    Primary = newPrimary,
                    Predicates = filt.Predicates,
                };
            }
        }

        return expr;
    }


    /// <summary>
    /// Drives the active streaming reader over one level of a striding DOWNWARD path,
    /// selecting the child elements of the current parent that match the name-test at
    /// <paramref name="stepIndex"/>. Non-final matches recurse into the child's children
    /// for the next step; final matches are dispatched through
    /// <see cref="MatchAndExecuteStreamingNodeAsync"/> (which materializes the matched
    /// subtree and runs the matched template body per selected node). Non-matching or
    /// deeper elements are skipped. On entry the reader is positioned just before the
    /// children of the current parent (for step 0 that is the document node — the reader
    /// sits before the root element). Handles only the SIMPLE non-recursive body case
    /// (no re-emitting xsl:copy, no climbing) — the same scope as
    /// <see cref="ApplyTemplatesStreamingAsync"/>.
    /// </summary>
    private async ValueTask DriveStridingDescentLevelAsync(
        System.Xml.XmlReader reader,
        List<PhoenixmlDb.XQuery.Ast.NameTest> steps,
        int stepIndex,
        QName? mode,
        CancellationToken ct)
    {
        var nameTest = steps[stepIndex];
        var isFinal = stepIndex == steps.Count - 1;
        // Depth of the children we iterate: reader.Depth after reading a start-tag equals
        // that element's depth; the parent's children live at parentDepth+1. For step 0
        // the parent is the document node (depth -1 conceptually); the root element arrives
        // at depth 0. Track the depth of the parent whose children we scan.
        int parentDepth = reader.Depth; // element currently open (or -1/0 sentinel at doc start)
        var position = 0;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType == System.Xml.XmlNodeType.EndElement
                && reader.Depth == parentDepth)
            {
                // Exhausted this parent's children.
                break;
            }

            if (reader.NodeType != System.Xml.XmlNodeType.Element)
                continue; // text/comment/PI between children — not selected by a name test

            var childDepth = reader.Depth;
            var matches = StridingNameTestMatchesReader(nameTest, reader);

            if (matches && isFinal)
            {
                var elem = await ReadStreamingElementForDispatchAsync(reader, ct).ConfigureAwait(false);
                position++;
                await MatchAndExecuteStreamingNodeAsync(elem, mode, position).ConfigureAwait(false);
                // Mirror ApplyTemplatesStreamingAsync: if the matched body pushed a
                // deferred open element (shallow-copy / xsl:copy), close it now — the
                // element's full subtree was already consumed.
                if (_streamingOpenElements.Count > 0)
                {
                    var qn = _streamingOpenElements.Pop();
                    WriteStreamingEndTag(qn);
                }
            }
            else if (matches && !reader.IsEmptyElement)
            {
                // Intermediate match — descend one level for the next step.
                await DriveStridingDescentLevelAsync(
                    reader, steps, stepIndex + 1, mode, ct).ConfigureAwait(false);
                // DriveStridingDescentLevelAsync consumed through this element's
                // EndElement; continue scanning the parent's remaining children.
            }
            else if (!reader.IsEmptyElement)
            {
                // Non-matching element (or intermediate with empty content) — skip its
                // whole subtree so we stay at the current level.
                await SkipStreamingSubtreeAsync(reader, childDepth, ct).ConfigureAwait(false);
            }
        }
    }


    /// <summary>
    /// Extracts the child-element local name a streaming for-each-group <c>select</c>
    /// picks, so the reader-driven accumulator can skip non-selected siblings. Returns
    /// <c>null</c> ("match every child element") for a wildcard leaf (<c>*</c>) or any
    /// shape this simple extractor doesn't recognise (the caller then admits all child
    /// elements — the pre-existing behaviour, sound for <c>select="*"</c>).
    /// <para>
    /// Handles the streamable striding shapes in the si-for-each-group cluster:
    /// <c>transaction</c> (single child step), <c>product</c>, and
    /// <c>record/copy-of()</c> (whose striding step is <c>record</c>; the trailing
    /// grounding <c>copy-of()</c> function step is ignored). A leaf whose last
    /// element step is <c>text()</c> or an attribute is treated as "no element-name
    /// filter" (null) — those forms are handled elsewhere or fall through unchanged.
    /// </para>
    /// </summary>
    private static string? StreamingForEachGroupSelectLeafName(XQueryExpression select)
    {
        if (select is not PhoenixmlDb.XQuery.Ast.PathExpression path || path.Steps.Count == 0)
            return null;

        // Walk from the end to the last child-axis element name step, skipping a
        // trailing grounding function step (copy-of()/snapshot()) which parses as a
        // separate step with a non-NameTest node test.
        for (int i = path.Steps.Count - 1; i >= 0; i--)
        {
            var step = path.Steps[i];
            if (step.Axis != PhoenixmlDb.XQuery.Ast.Axis.Child)
            {
                // The striding leaf must be a child-axis element step; anything else
                // (attribute/text tail, a leading grounding step) → no name filter.
                if (step.NodeTest is PhoenixmlDb.XQuery.Ast.NameTest) return null;
                continue;
            }
            if (step.NodeTest is PhoenixmlDb.XQuery.Ast.NameTest nt)
                return nt.IsLocalNameWildcard ? null : nt.LocalName;
            // A kind test (text()/node()/element()) leaf → no simple element-name filter.
            return null;
        }
        return null;
    }


    /// <summary>
    /// Flattens the engine's namespace scope stack into the prefix→URI bindings currently
    /// in scope (innermost wins on conflicts). Returned to the schema provider so fragment
    /// validation can resolve prefixes whose declarations live on enclosing elements.
    /// </summary>
    private Dictionary<string, string> SnapshotInScopeNamespaces()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // Stack iterates innermost→outermost. Add innermost-first; outer scopes won't
        // overwrite already-set inner bindings (TryAdd preserves the closer declaration).
        foreach (var scope in _outputNsScopes)
        {
            foreach (var kv in scope)
                result.TryAdd(kv.Key, kv.Value);
        }
        // Stylesheet-level prefix bindings are the outermost layer.
        foreach (var kv in _stylesheet.Namespaces)
            result.TryAdd(kv.Key, kv.Value);
        return result;
    }


    private async ValueTask WalkAccumulatorsAsync(
        object node,
        IReadOnlyList<XsltAccumulator> accumulators,
        object?[] currentValues,
        Dictionary<NodeId, (object? before, object? after)>[] nodeValueMaps,
        XdmInMemoryStore nodeStore)
    {
        NodeId nodeId;
        if (node is XdmDocument doc)
            nodeId = doc.Id;
        else if (node is XdmNode xdmNode)
            nodeId = xdmNode.Id;
        else
            return;

        // Ensure cached delegates are populated (mirrors CreateMatchContext).
        // _matchCtxNodeResolver may capture a different nodeStore here, so we
        // build a per-call resolver only if the cached one doesn't already match.
        EnsureMatchCtxDelegates();
        var matchContext = new XsltContext
        {
            CurrentNode = node,
            Position = 1,
            Last = 1,
            NodeResolver = id => nodeStore.GetNode(id),
            PredicateEvaluator = _matchCtxPredicateEvaluator,
            PositionComputer = _matchCtxPositionComputer,
            KeyPatternEvaluator = _matchCtxKeyPatternEvaluator,
            IdPatternEvaluator = _matchCtxIdPatternEvaluator,
        };

        var frame = new AccumulatorWalkFrame(node, nodeId, matchContext, accumulators, currentValues, nodeValueMaps);
        var savedFrame = _accumulatorWalkFrame;
        _accumulatorWalkFrame = frame;
        try
        {
            // Start-phase rules for ALL accumulators at this node, in declaration order — but a
            // rule that asks for another accumulator's value at this node runs that one first
            // (see EnsureAccumulatorPhaseAsync), so declaration order never decides a value.
            for (var i = 0; i < accumulators.Count; i++)
                await RunAccumulatorStartPhaseAsync(frame, i).ConfigureAwait(false);

            // Process children
            IReadOnlyList<NodeId>? children = null;
            if (node is XdmDocument d)
                children = d.Children;
            else if (node is XdmElement elem)
                children = elem.Children;

            if (children != null)
            {
                foreach (var childId in children)
                {
                    var child = nodeStore.GetNode(childId);
                    if (child != null)
                        await WalkAccumulatorsAsync(child, accumulators, currentValues, nodeValueMaps, nodeStore).ConfigureAwait(false);
                }
            }

            // End-phase rules for ALL accumulators at this node, on the same terms.
            frame.InEndPhase = true;
            for (var i = 0; i < accumulators.Count; i++)
                await RunAccumulatorEndPhaseAsync(frame, i).ConfigureAwait(false);
        }
        finally
        {
            _accumulatorWalkFrame = savedFrame;
        }
    }


    /// <summary>The accumulator walk's state at the node it is visiting.</summary>
    /// <remarks>
    /// Exists so a rule can ask for another accumulator's value at the SAME node before the walk
    /// has reached that accumulator. Each phase ran accumulators in declaration order and stored
    /// values as it went, so an end rule reading a later-declared accumulator's after-value got
    /// the provisional entry the start phase left — the value from BEFORE this node's
    /// descendants. W3C accumulator-077: header-map's end rule on header-item read
    /// accumulator-after('header-id') and got the previous header-item's id, () for the first.
    /// </remarks>
    private sealed class AccumulatorWalkFrame(
        object node,
        NodeId nodeId,
        XsltContext matchContext,
        IReadOnlyList<XsltAccumulator> accumulators,
        object?[] currentValues,
        Dictionary<NodeId, (object? before, object? after)>[] nodeValueMaps)
    {
        public object Node { get; } = node;
        public NodeId NodeId { get; } = nodeId;
        public XsltContext MatchContext { get; } = matchContext;
        public IReadOnlyList<XsltAccumulator> Accumulators { get; } = accumulators;
        public object?[] CurrentValues { get; } = currentValues;
        public Dictionary<NodeId, (object? before, object? after)>[] NodeValueMaps { get; } = nodeValueMaps;
        public bool InEndPhase { get; set; }
        /// <summary>Per accumulator: 0 = not run in the current phase, 1 = running, 2 = done.</summary>
        public byte[] StartState { get; } = new byte[accumulators.Count];
        public byte[] EndState { get; } = new byte[accumulators.Count];
    }


    private AccumulatorWalkFrame? _accumulatorWalkFrame;


    /// <summary>
    /// The rule an accumulator applies at a traversal event: of its rules for this phase whose
    /// pattern matches the node, the one LAST in document order (XSLT 3.0 §18.2.4, "Let Q be the
    /// xsl:accumulator-rule in R that is last in document order"). Priority plays no part.
    /// </summary>
    /// <remarks>
    /// Every site took the FIRST match, so of two rules matching one node the earlier won —
    /// W3C accumulator-081 gave 1 where the spec gives 2. One helper, so the in-memory walk and the
    /// streaming processor cannot pick differently.
    /// </remarks>
    internal static XsltAccumulatorRule? SelectAccumulatorRule(
        XsltAccumulator accumulator, AccumulatorPhase phase, object node, XsltContext matchContext)
    {
        var rules = accumulator.Rules;
        for (var r = rules.Count - 1; r >= 0; r--)
        {
            var rule = rules[r];
            if (rule.Phase == phase && rule.Match.Matches(node, matchContext))
                return rule;
        }
        return null;
    }


    private async ValueTask RunAccumulatorStartPhaseAsync(AccumulatorWalkFrame frame, int i)
    {
        if (frame.StartState[i] != 0)
            return;
        frame.StartState[i] = 1;
        var currentValues = frame.CurrentValues;
        // Skip evaluation if the accumulator is in error state
        if (currentValues[i] is AccumulatorDeferredError)
        {
            frame.NodeValueMaps[i][frame.NodeId] = (before: currentValues[i], after: currentValues[i]);
            frame.StartState[i] = 2;
            return;
        }

        var acc = frame.Accumulators[i];
        if (SelectAccumulatorRule(acc, AccumulatorPhase.Start, frame.Node, frame.MatchContext) is { } rule)
        {
            try
            {
                currentValues[i] = await EvaluateAccumulatorRuleAsync(
                    frame.Node, rule, currentValues[i], acc).ConfigureAwait(false);
            }
            catch (Exception ex) when (AccumulatorDeferredError.IsDeferrable(ex))
            {
                currentValues[i] = new AccumulatorDeferredError(ex);
            }
        }

        // Store before-value immediately so other accumulators can reference it
        frame.NodeValueMaps[i][frame.NodeId] = (before: currentValues[i], after: currentValues[i]);
        frame.StartState[i] = 2;
    }


    private async ValueTask RunAccumulatorEndPhaseAsync(AccumulatorWalkFrame frame, int i)
    {
        if (frame.EndState[i] != 0)
            return;
        frame.EndState[i] = 1;
        var currentValues = frame.CurrentValues;
        // Skip evaluation if the accumulator is in error state
        if (currentValues[i] is AccumulatorDeferredError)
        {
            frame.EndState[i] = 2;
            return;
        }

        _evaluatingAccEndPhase ??= new();
        var acc = frame.Accumulators[i];
        if (SelectAccumulatorRule(acc, AccumulatorPhase.End, frame.Node, frame.MatchContext) is { } rule)
        {
            var cycleKey = (acc.Name, frame.NodeId);
            _evaluatingAccEndPhase.Add(cycleKey);
            try
            {
                currentValues[i] = await EvaluateAccumulatorRuleAsync(
                    frame.Node, rule, currentValues[i], acc).ConfigureAwait(false);
            }
            catch (Exception ex) when (AccumulatorDeferredError.IsDeferrable(ex))
            {
                currentValues[i] = new AccumulatorDeferredError(ex);
            }
            finally
            {
                _evaluatingAccEndPhase.Remove(cycleKey);
            }
        }
        // Update this accumulator's after-value immediately so subsequent accumulators can see it
        frame.NodeValueMaps[i][frame.NodeId] = (frame.NodeValueMaps[i][frame.NodeId].before, after: currentValues[i]);
        frame.EndState[i] = 2;
    }


    /// <summary>
    /// Before a rule reads another accumulator's value at the node the walk is visiting, runs
    /// that accumulator's rule for the current phase if it has not run yet.
    /// </summary>
    /// <remarks>
    /// accumulator-before needs the start phase done; accumulator-after, read from an end rule,
    /// needs the end phase done. A request for an accumulator whose rule is running at this node
    /// is a cycle among the accumulators, XTDE3400 — the end-phase case was already reported by
    /// <see cref="GetAccumulatorValue"/>, and a start-phase cycle would otherwise read a value
    /// that does not exist yet.
    /// </remarks>
    internal async ValueTask EnsureAccumulatorPhaseAsync(QName accumulatorName, object node, bool isAfter)
    {
        if (_accumulatorWalkFrame is not { } frame)
            return;
        var nodeId = node switch { XdmDocument d => d.Id, XdmNode n => n.Id, _ => (NodeId?)null };
        if (nodeId != frame.NodeId)
            return;
        var index = -1;
        for (var i = 0; i < frame.Accumulators.Count; i++)
        {
            if (frame.Accumulators[i].Name.Equals(accumulatorName))
            {
                index = i;
                break;
            }
        }
        if (index < 0)
            return;

        if (!frame.InEndPhase)
        {
            if (isAfter)
                return;
            if (frame.StartState[index] == 1)
                throw Error($"XTDE3400: Cyclic dependency detected in accumulator '{accumulatorName}' at the current node");
            await RunAccumulatorStartPhaseAsync(frame, index).ConfigureAwait(false);
        }
        else if (isAfter && frame.EndState[index] == 0)
        {
            await RunAccumulatorEndPhaseAsync(frame, index).ConfigureAwait(false);
        }
    }


    internal async ValueTask EnsureAccumulatorsComputedAsync(QName accumulatorName, object node)
    {
        if (_nodeStore == null || !_stylesheet.Accumulators.ContainsKey(accumulatorName))
            return;

        // Check if this node already has accumulator values (e.g., from snapshot/copy-of propagation).
        // This prevents recomputing on synthetic/snapshot trees that already carry copied values.
        if (node is XdmNode nodeWithValues)
        {
            if (_accumulatorValues?.TryGetValue(accumulatorName, out var existingNodeValues) == true
                && existingNodeValues.ContainsKey(nodeWithValues.Id))
                return;
        }
        else if (node is XdmDocument docWithValues)
        {
            if (_accumulatorValues?.TryGetValue(accumulatorName, out var existingDocValues) == true
                && existingDocValues.ContainsKey(docWithValues.Id))
                return;
        }

        // Find the document containing this node
        var doc = FindDocumentForNode(node);

        // For orphan nodes (e.g., elements extracted from variables), create a temporary
        // document wrapper so accumulators can be computed fresh on the subtree.
        // But skip if the node already has accumulator values (e.g., from copy-of with
        // accumulator propagation) — we don't want to overwrite copied values.
        if (doc == null && node is XdmNode orphanNode)
        {
            // Walk up to the root of the orphan's subtree
            var root = orphanNode;
            while (root.Parent.HasValue && root.Parent.Value != NodeId.None)
            {
                var parent = _nodeStore.GetNode(root.Parent.Value);
                if (parent is XdmDocument parentDoc)
                {
                    doc = parentDoc;
                    break;
                }
                if (parent is XdmNode parentNode)
                    root = parentNode;
                else
                    break;
            }

            if (doc == null)
            {
                // True orphan — create temp document wrapper for accumulator computation
                var tempDocId = _nodeStore.NextId();
                var rootId = root.Id;
                NodeId? docElemId = root is XdmElement ? rootId : (NodeId?)null;
                doc = new XdmDocument
                {
                    Id = tempDocId,
                    Document = new DocumentId(1),
                    Parent = NodeId.None,
                    Children = [rootId],
                    DocumentElement = docElemId,
                    DocumentElementLocalName = (root as XdmElement)?.LocalName
                };
                doc._stringValue = root.StringValue;
                _nodeStore.Register(doc);
            }
        }

        if (doc == null)
            return;

        // Already computed accumulators for this document?
        if (_accumulatorComputedDocuments?.Contains(doc.Id) == true)
            return;

        // Compute all accumulators for this document (simultaneous walk per §6.5.3)
        var accumulators = _stylesheet.Accumulators.Values.ToList();
        await PreComputeAccumulatorsAsync(doc, accumulators, _nodeStore).ConfigureAwait(false);
    }


    internal async ValueTask EnsureAccumulatorsComputedForInputAsync(QName accName, object? input)
    {
        if (input is XdmNode node)
        {
            await EnsureAccumulatorsComputedAsync(accName, node).ConfigureAwait(false);
        }
        else if (input is object?[] arr)
        {
            foreach (var item in arr)
            {
                if (item is XdmNode n)
                {
                    await EnsureAccumulatorsComputedAsync(accName, n).ConfigureAwait(false);
                    break; // All items in same sequence share same document
                }
            }
        }
        else if (input is IEnumerable<object?> seq)
        {
            foreach (var item in seq)
            {
                if (item is XdmNode n)
                {
                    await EnsureAccumulatorsComputedAsync(accName, n).ConfigureAwait(false);
                    break;
                }
            }
        }
    }


    /// <summary>
    /// Scans serialized XML content to find top-level node boundaries and significance.
    /// Returns null if scanning fails (content will be kept unchanged).
    /// </summary>
    private static List<(int start, int end, bool significant)>? ScanWherePopulatedNodes(string xml)
    {
        var nodes = new List<(int start, int end, bool significant)>();
        int pos = 0;
        int len = xml.Length;

        while (pos < len)
        {
            if (xml[pos] == '<')
            {
                // Comment: <!-- ... --> — significant only if non-empty content
                if (pos + 3 < len && xml[pos + 1] == '!' && xml[pos + 2] == '-' && xml[pos + 3] == '-')
                {
                    int start = pos;
                    int endIdx = xml.IndexOf("-->", pos + 4, StringComparison.Ordinal);
                    if (endIdx < 0)
                        return null;
                    // Empty comment <!---->: content between <!-- and --> is empty
                    bool hasContent = endIdx > pos + 4;
                    pos = endIdx + 3;
                    nodes.Add((start, pos, hasContent));
                }
                // PI: <?target data?> — significant only if has data after target
                else if (pos + 1 < len && xml[pos + 1] == '?')
                {
                    int start = pos;
                    int endIdx = xml.IndexOf("?>", pos + 2, StringComparison.Ordinal);
                    if (endIdx < 0)
                        return null;
                    // Extract target+data between <? and ?>. PI is insignificant if no data after target.
                    var piContent = xml.AsSpan(pos + 2, endIdx - pos - 2);
                    var spaceIdx = piContent.IndexOf(' ');
                    bool hasData = spaceIdx >= 0 && piContent[(spaceIdx + 1)..].Trim().Length > 0;
                    pos = endIdx + 2;
                    nodes.Add((start, pos, hasData));
                }
                // Closing tag at top level — shouldn't happen but skip
                else if (pos + 1 < len && xml[pos + 1] == '/')
                {
                    int endIdx = xml.IndexOf('>', pos + 2);
                    if (endIdx < 0)
                        return null;
                    pos = endIdx + 1;
                }
                // Element
                else
                {
                    int start = pos;
                    var (endPos, hasContent) = ScanWherePopulatedElement(xml, pos);
                    if (endPos < 0)
                        return null;
                    pos = endPos;
                    nodes.Add((start, pos, hasContent));
                }
            }
            else
            {
                // Text node at top level
                int start = pos;
                while (pos < len && xml[pos] != '<')
                    pos++;
                // Text is significant if non-empty
                nodes.Add((start, pos, pos > start));
            }
        }
        return nodes;
    }


    /// <summary>
    /// Scans a single top-level element from its opening '&lt;' to its closing '&gt;'.
    /// Returns (endPosition, hasSignificantContent).
    /// An element is significant if it has at least one child that is not a zero-length text node.
    /// Element children always count as significant (non-recursive check per spec).
    /// </summary>
    private static (int endPos, bool hasSignificantContent) ScanWherePopulatedElement(string xml, int pos)
    {
        // pos points to '<'
        pos++; // skip '<'

        // Skip element name
        while (pos < xml.Length && xml[pos] != '>' && xml[pos] != '/' && !char.IsWhiteSpace(xml[pos]))
            pos++;

        // Skip attributes
        pos = SkipElementAttributes(xml, pos);
        if (pos >= xml.Length)
            return (-1, false);

        // Self-closing <foo/> — no children → insignificant
        if (xml[pos] == '/' && pos + 1 < xml.Length && xml[pos + 1] == '>')
            return (pos + 2, false);

        // Opening tag ends with '>'
        pos++; // skip '>'

        // Scan content for matching close tag and check for significant children
        int depth = 1;
        bool hasSignificantChild = false;

        while (pos < xml.Length && depth > 0)
        {
            if (xml[pos] == '<')
            {
                if (pos + 1 < xml.Length && xml[pos + 1] == '/')
                {
                    // Closing tag
                    depth--;
                    if (depth == 0)
                    {
                        int endIdx = xml.IndexOf('>', pos + 2);
                        if (endIdx < 0)
                            return (-1, false);
                        return (endIdx + 1, hasSignificantChild);
                    }
                    int closeEnd = xml.IndexOf('>', pos + 2);
                    if (closeEnd < 0)
                        return (-1, false);
                    pos = closeEnd + 1;
                }
                else if (pos + 3 < xml.Length && xml[pos + 1] == '!' && xml[pos + 2] == '-' && xml[pos + 3] == '-')
                {
                    // Comment child — significant at depth 1
                    if (depth == 1)
                        hasSignificantChild = true;
                    int endIdx = xml.IndexOf("-->", pos + 4, StringComparison.Ordinal);
                    if (endIdx < 0)
                        return (-1, false);
                    pos = endIdx + 3;
                }
                else if (pos + 1 < xml.Length && xml[pos + 1] == '?')
                {
                    // PI child — significant at depth 1
                    if (depth == 1)
                        hasSignificantChild = true;
                    int endIdx = xml.IndexOf("?>", pos + 2, StringComparison.Ordinal);
                    if (endIdx < 0)
                        return (-1, false);
                    pos = endIdx + 2;
                }
                else
                {
                    // Child element — always significant at depth 1 (element children count regardless)
                    if (depth == 1)
                        hasSignificantChild = true;
                    // Scan past this child element's opening tag
                    int tagPos = pos + 1;
                    // Skip element name
                    while (tagPos < xml.Length && xml[tagPos] != '>' && xml[tagPos] != '/' && !char.IsWhiteSpace(xml[tagPos]))
                        tagPos++;
                    tagPos = SkipElementAttributes(xml, tagPos);
                    if (tagPos >= xml.Length)
                        return (-1, false);

                    if (xml[tagPos] == '/' && tagPos + 1 < xml.Length && xml[tagPos + 1] == '>')
                    {
                        pos = tagPos + 2; // self-closing child
                    }
                    else
                    {
                        depth++;
                        pos = tagPos + 1;
                    }
                }
            }
            else
            {
                // Text content
                int textStart = pos;
                while (pos < xml.Length && xml[pos] != '<')
                    pos++;
                // Text is significant if non-empty at depth 1
                if (depth == 1 && pos > textStart)
                    hasSignificantChild = true;
            }
        }

        return (-1, false); // Malformed — never found closing tag
    }

}
