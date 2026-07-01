using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine.Streamability;

/// <summary>
/// The streaming environment in which an expression is classified: the posture of the
/// context item and whether that context item is live-streamed.
/// </summary>
/// <param name="ContextPosture">
/// The posture of the context item (<c>.</c>) at this point, per XSLT 3.0 §19.8.2. For a
/// streamed source-document body or a matched streaming template the context item is
/// <see cref="Posture.Striding"/>; for a grounded/materialised context it is
/// <see cref="Posture.Grounded"/>.
/// </param>
/// <param name="InStreamedScope">
/// Whether the context item is a live-streamed node. When <c>false</c> the whole expression
/// is grounded regardless of navigation (nothing streamed to consume).
/// </param>
public readonly record struct StreamingContext(Posture ContextPosture, bool InStreamedScope);

/// <summary>
/// Compositional streamability classifier for the XPath/XQuery <b>expression</b> language,
/// implementing the XSLT 3.0 §19.8 (Streamability) posture/sweep composition rules.
/// </summary>
/// <remarks>
/// <para>
/// This is a <b>shadow</b> component (Task 0.3): it is pure analysis, wired into nothing and
/// deleting nothing. It computes the <see cref="PostureSweep"/> of an expression given the
/// posture of its context item, exactly as §19.8 describes:
/// </para>
/// <list type="bullet">
///   <item><description><b>Posture</b> (§19.1) — <i>where</i> the result nodes sit relative to
///   the streamed context (grounded / striding / crawling / climbing / roaming).</description></item>
///   <item><description><b>Sweep</b> (§19.1) — whether evaluating the expression <i>advances</i>
///   the stream (motionless / consuming / free-ranging).</description></item>
/// </list>
/// <para>
/// Any expression kind not yet modelled is conservatively classified
/// <c>(Roaming, FreeRanging)</c> — i.e. NOT guaranteed streamable — so nothing is silently
/// mis-accepted. Those cases are marked with <c>// TODO</c>.
/// </para>
/// </remarks>
public static class StreamabilityClassifier
{
    /// <summary>
    /// Computes the composed <see cref="PostureSweep"/> of <paramref name="expr"/> in the
    /// streaming environment <paramref name="ctx"/>, per XSLT 3.0 §19.8.
    /// </summary>
    /// <param name="expr">The expression to classify.</param>
    /// <param name="ctx">The streaming context (context-item posture and streamed-scope flag).</param>
    /// <returns>The composed posture and sweep of the expression's result.</returns>
    public static PostureSweep Classify(XQueryExpression expr, StreamingContext ctx)
    {
        ArgumentNullException.ThrowIfNull(expr);

        return expr switch
        {
            // §19.8: literals and the empty sequence contain no streamed nodes and read no
            // streamed input — grounded and motionless.
            LiteralExpression => Grounded(Sweep.Motionless),
            EmptySequence => Grounded(Sweep.Motionless),

            // §19.8.2: the context item posture is supplied by the environment; reading it
            // does not advance the stream.
            ContextItemExpression => new PostureSweep(ctx.ContextPosture, Sweep.Motionless),

            // §19.8: a variable reference has the posture of its binding. Without a resolved
            // binding we take the context posture as an approximation for streamed scope, and
            // grounded outside it. Reading a bound value is motionless (the value is already
            // in hand — the binding's own consumption was accounted for at its binding site).
            VariableReference => new PostureSweep(
                ctx.InStreamedScope ? ctx.ContextPosture : Posture.Grounded, Sweep.Motionless),

            PathExpression path => ClassifyPath(path, ctx),
            StepExpression step => ClassifyStep(step, ctx.ContextPosture, ctx),

            FunctionCallExpression fc => ClassifyFunctionCall(fc, ctx),

            BinaryExpression bin => ClassifyBinary(bin, ctx),
            UnaryExpression un => ClassifyUnary(un, ctx),
            FilterExpression filt => ClassifyFilter(filt, ctx),
            SimpleMapExpression sm => ClassifySimpleMap(sm, ctx),
            FlworExpression flwor => ClassifyFlwor(flwor, ctx),

            SequenceExpression seq => ClassifySequence(seq.Items, ctx),
            RangeExpression => Grounded(Sweep.Motionless), // integer range — atomic, no input.

            // §19.8: cast/castable/treat/instance-of atomize-or-inspect their operand and yield
            // a grounded (atomic/boolean) result; posture collapses to grounded, sweep is the
            // operand's sweep (its subtree may be atomized).
            CastExpression c => Grounded(AtomizeSweep(c.Expression, ctx)),
            CastableExpression c => Grounded(AtomizeSweep(c.Expression, ctx)),
            TreatExpression t => Classify(t.Expression, ctx), // treat is a no-op on posture/sweep.
            // instance of does NOT atomize (it inspects node kinds) and yields a grounded
            // boolean, but its operand's own posture/sweep still propagates: a non-streamable
            // operand (e.g. a consuming-predicate filter that roams) poisons the whole test.
            InstanceOfExpression io => GroundedFrom(io.Expression, ctx),

            StringConcatExpression sc => Grounded(WorstAtomizeSweep(sc.Operands, ctx)),

            // TODO(Task 0.4+): model conditionals, quantifiers, constructors, maps/arrays,
            // arrow/dynamic calls, lookups, switch/typeswitch, try/catch. Until then, treat
            // every unmodelled node kind conservatively as NOT streamable so nothing is
            // silently accepted (§19.8.6: only grounded/striding/crawling/climbing +
            // motionless/consuming is guaranteed streamable).
            _ => NotStreamable(),
        };
    }

    // -----------------------------------------------------------------------
    // Path / step classification
    // -----------------------------------------------------------------------

    private static PostureSweep ClassifyPath(PathExpression path, StreamingContext ctx)
    {
        // Determine the posture flowing INTO the first step.
        Posture current;
        Sweep accumulatedSweep;

        if (path.IsAbsolute)
        {
            // §19.8: an absolute path (/...) roots at the document node. Within a streamed
            // document that root is the streamed document node — striding. Outside streamed
            // scope it is grounded.
            current = ctx.InStreamedScope ? Posture.Striding : Posture.Grounded;
            accumulatedSweep = Sweep.Motionless;
        }
        else if (path.InitialExpression is not null)
        {
            var init = Classify(path.InitialExpression, ctx);
            // A non-streamable initial expression poisons the whole path — do NOT let a
            // subsequent step (e.g. reverse(path)/@id) reset the posture back to a streamable
            // axis posture.
            if (init.Sweep == Sweep.FreeRanging || !IsStreamablePosture(init.Posture))
                return new PostureSweep(Posture.Roaming, Sweep.FreeRanging);

            // §19.8.8.3 (family 3): a UNION / COMMA of node sequences yields a crawling (or,
            // when a grounded/free operand is mixed in, roaming) sequence whose members may
            // overlap or nest. Applying a further downward STEP to such a combined operand
            // requires re-reading the input for each member ⇒ not guaranteed streamable
            // (($v | //ITEM)/PRICE ; (//A,//B)/C ; (/x/A | /x/B)/PRICE). A bare union with no
            // trailing step stays classifiable by ClassifyBinary/ClassifySequence.
            if (path.Steps.Count > 0 && IsNodeCombiningExpression(path.InitialExpression))
                return new PostureSweep(Posture.Roaming, Sweep.FreeRanging);

            current = init.Posture;
            accumulatedSweep = init.Sweep;
        }
        else
        {
            // Relative path: starts from the context item.
            current = ctx.ContextPosture;
            accumulatedSweep = Sweep.Motionless;
        }

        foreach (var step in path.Steps)
        {
            var stepPs = ClassifyStep(step, current, ctx);
            current = stepPs.Posture;
            accumulatedSweep = CombineSweep(accumulatedSweep, stepPs.Sweep);

            // Once the path leaves the streamable regime, stop refining — the whole path is
            // not streamable.
            if (current is Posture.Roaming or Posture.Artistic || accumulatedSweep == Sweep.FreeRanging)
                return new PostureSweep(Posture.Roaming, Sweep.FreeRanging);
        }

        return new PostureSweep(current, accumulatedSweep);
    }

    /// <summary>
    /// Classifies a single axis step applied to an operand of posture <paramref name="inPosture"/>,
    /// per the §19.8.8 axis/posture composition rules.
    /// </summary>
    private static PostureSweep ClassifyStep(StepExpression step, Posture inPosture, StreamingContext ctx)
    {
        // A step on a grounded operand yields grounded nodes (they live in a materialised
        // tree, §19.8.2). The sweep still reflects the navigation cost of the predicates,
        // but downward navigation of a grounded tree consumes nothing streamed — however to
        // stay conservative and match the §19.8 "grounded absorbs" intuition we report the
        // navigation as consuming when it descends (the operand's string-value/subtree is
        // walked). Predicates are folded in below.
        bool grounded = inPosture == Posture.Grounded;

        // The output posture contributed by THIS axis (before grounding collapse).
        Posture axisPosture;
        Sweep axisSweep;

        switch (step.Axis)
        {
            // §19.8.8.2: child of a STRIDING operand is striding, but child of a CRAWLING
            // operand stays CRAWLING (the crawling operand's items nest, so their children can
            // nest too — e.g. //ITEM/TITLE, whose ITEMs sit at arbitrary depth, is crawling,
            // NOT striding). Advances the stream either way.
            case Axis.Child:
                axisPosture = inPosture == Posture.Crawling ? Posture.Crawling : Posture.Striding;
                axisSweep = Sweep.Consuming;
                break;

            // self:: does not move — preserves the incoming posture, motionless.
            case Axis.Self:
                axisPosture = inPosture;
                axisSweep = Sweep.Motionless;
                break;

            // descendant / descendant-or-self may nest → crawling; advances the stream.
            case Axis.Descendant:
            case Axis.DescendantOrSelf:
                axisPosture = Posture.Crawling;
                axisSweep = Sweep.Consuming;
                break;

            // Attribute / namespace of the context node — reachable without advancing past it.
            case Axis.Attribute:
            case Axis.Namespace:
                axisPosture = Posture.Climbing;
                axisSweep = Sweep.Motionless;
                break;

            // Upward axes → climbing; reachable without consuming forward input (§19.8.8:
            // ancestors/parent are already-seen nodes on a streaming reader's ancestor stack).
            case Axis.Parent:
            case Axis.Ancestor:
            case Axis.AncestorOrSelf:
                axisPosture = Posture.Climbing;
                axisSweep = Sweep.Motionless;
                break;

            // following / preceding / sibling axes require arbitrary access → roaming/free-ranging.
            case Axis.Following:
            case Axis.Preceding:
            case Axis.FollowingSibling:
            case Axis.PrecedingSibling:
            default:
                return new PostureSweep(Posture.Roaming, Sweep.FreeRanging);
        }

        // Fold predicates (each is evaluated with a candidate node produced by this axis as
        // context). §19.8.8: on a STREAMED (non-grounded) sequence, a predicate is only
        // streamable if it is MOTIONLESS — a predicate that CONSUMES the candidate node
        // (navigates its children/descendants, or atomizes its element subtree, e.g.
        // ITEM[AUTHOR='X'] or PAGES[. lt 1000]) forces the whole striding sequence to be
        // buffered while each item is re-inspected ⇒ ROAMING. A predicate using position()/
        // last() (free-ranging) is likewise not streamable. Motionless predicates
        // ([@x='1'], [xs:decimal(@v) gt 0], constant [1]) preserve the posture.
        var predSweep = Sweep.Motionless;
        foreach (var pred in step.Predicates)
        {
            var p = Classify(pred, ctx with { ContextPosture = axisPosture });
            if (!grounded && p.Sweep != Sweep.Motionless)
            {
                // Consuming/free-ranging predicate on a streamed axis → not guaranteed
                // streamable (a free-ranging predicate is free-ranging; a merely consuming
                // one still roams because the filtered sequence must be re-navigated).
                return p.Sweep == Sweep.FreeRanging
                    ? new PostureSweep(Posture.Roaming, Sweep.FreeRanging)
                    : new PostureSweep(Posture.Roaming, Sweep.Consuming);
            }
            predSweep = CombineSweep(predSweep, p.Sweep);
        }

        var resultSweep = CombineSweep(axisSweep, predSweep);
        var resultPosture = grounded ? Posture.Grounded : axisPosture;
        return new PostureSweep(resultPosture, resultSweep);
    }

    // -----------------------------------------------------------------------
    // Function calls
    // -----------------------------------------------------------------------

    private static PostureSweep ClassifyFunctionCall(FunctionCallExpression fc, StreamingContext ctx)
    {
        // §19.8: a constructor-function call in the XSD namespace (xs:decimal(…), xs:integer(…),
        // xs:date(…), xs:NMTOKENS(…), …) atomizes its single operand and yields a grounded
        // atomic/list result — identical streamability to a CastExpression. Route it through the
        // SAME logic so it is not left Unknown → NotStreamable. (Most xs:* casts parse to a
        // CastExpression already handled in Classify; the residual constructor-CALL form — e.g.
        // list types, or an explicit function call — lands here.)
        if (fc.Name.Namespace == PhoenixmlDb.Core.NamespaceId.Xsd && fc.Arguments.Count == 1)
            return Grounded(AtomizeSweep(fc.Arguments[0], ctx));

        var role = FunctionRole(fc.Name.LocalName, fc.Arguments.Count);

        switch (role)
        {
            case FnRole.NodeProperty:
                // §19.8 (Inspection): name()/local-name()/node-name()/namespace-uri() read only
                // the node's own properties — grounded (atomic) result, motionless.
                return Grounded(Sweep.Motionless);

            case FnRole.StringValue:
                // string()/data()/normalize-space()/string-length() atomize the subtree string
                // value — grounded (atomic) result, consuming the operand's subtree. A
                // non-streamable operand propagates.
                if (!AllOperandsStreamable(fc.Arguments, ctx))
                    return NotStreamable();
                return Grounded(CombineSweep(Sweep.Consuming, WorstSweep(fc.Arguments, ctx)));

            case FnRole.Atomizing:
                // §19.8 / fn spec: numeric (number/abs/round/floor/…), string-manipulation
                // (contains/substring/matches/translate/…), and boolean/comparison predicate
                // (starts-with/ends-with/has-children/deep-equal/…) functions ATOMIZE their
                // node operands and yield a grounded atomic/boolean result. The sweep is the
                // WORST ATOMIZE-sweep across the operands — posture-aware, unlike StringValue's
                // forced Consuming: atomizing a striding/crawling ELEMENT operand is consuming
                // (its subtree string-value is walked), but atomizing a climbing ATTRIBUTE
                // operand is motionless (an attribute has no subtree). This is what makes
                // number(@v) motionless-streamable but number(child::PRICE) consuming. A
                // non-streamable operand (roaming filter, reverse(), …) propagates.
                if (!AllOperandsStreamable(fc.Arguments, ctx))
                    return NotStreamable();
                return Grounded(WorstAtomizeSweep(fc.Arguments, ctx));

            case FnRole.Aggregate:
                // count()/sum()/avg()/... absorb the operand sequence to an atomic — grounded,
                // consuming (the operand is fully walked). But a NON-streamable operand (e.g. a
                // consuming-predicate filter ITEM[AUTHOR='X'] that roams, or reverse()) makes
                // the aggregate non-streamable too — the operand's roaming/free-ranging must
                // propagate, not be flattened to "consuming".
                if (!AllOperandsStreamable(fc.Arguments, ctx))
                    return NotStreamable();
                return Grounded(CombineSweep(Sweep.Consuming, WorstSweep(fc.Arguments, ctx)));

            case FnRole.Transmission:
                // §19.8.8: head()/tail()/subsequence()/... transmit a subset of the operand
                // unchanged — posture is preserved from the (first) sequence operand.
                if (fc.Arguments.Count > 0)
                {
                    var operand = Classify(fc.Arguments[0], ctx);
                    var restSweep = fc.Arguments.Count > 1
                        ? WorstSweep(fc.Arguments.Skip(1).ToList(), ctx)
                        : Sweep.Motionless;
                    return new PostureSweep(operand.Posture, CombineSweep(operand.Sweep, restSweep));
                }
                return Grounded(Sweep.Motionless);

            case FnRole.Boolean:
                // boolean()/exists()/empty()/not() reduce to an atomic boolean — grounded; the
                // sweep is the worst of the arguments' sweeps. A non-streamable argument (a
                // roaming consuming-predicate filter, reverse(), …) propagates: exists() of a
                // roaming sequence is itself not streamable.
                if (!AllOperandsStreamable(fc.Arguments, ctx))
                    return NotStreamable();
                return Grounded(WorstSweep(fc.Arguments, ctx));

            case FnRole.CurrentGroup:
                // §18.5.4: current-group() yields the buffered group items. Within a streamed
                // group-adjacent/starting/ending burst these behave like a striding sequence the
                // processor already holds — motionless (no additional forward stream advance).
                return new PostureSweep(Posture.Striding, Sweep.Motionless);

            case FnRole.Positional:
                // §19.8.8: position()/last() yield an atomic (grounded) integer but knowing
                // last() (or comparing position() to it) requires look-ahead over the whole
                // sequence being filtered — free-ranging.
                return Grounded(Sweep.FreeRanging);

            case FnRole.FreeRanging:
                // §19.8.8: reverse() (and kin) must buffer the entire operand sequence, so the
                // result roams and the sweep is free-ranging regardless of the operand posture.
                return NotStreamable();

            default:
                // TODO(Task 0.4+): most functions are unmodelled. Absorbing functions that
                // take node sequences and return atomics would be grounded+consuming, but we
                // cannot assume that in general, so stay conservative.
                return NotStreamable();
        }
    }

    // -----------------------------------------------------------------------
    // Operators
    // -----------------------------------------------------------------------

    private static PostureSweep ClassifyBinary(BinaryExpression bin, StreamingContext ctx)
    {
        switch (bin.Operator)
        {
            // §19.8.8.3: union of striding operands is CRAWLING (the merged node-sets may
            // interleave descendants), not striding. Intersect/except behave likewise.
            case BinaryOperator.Union:
            case BinaryOperator.Intersect:
            case BinaryOperator.Except:
            {
                var l = Classify(bin.Left, ctx);
                var r = Classify(bin.Right, ctx);
                if (!IsNodePosture(l.Posture) || !IsNodePosture(r.Posture)
                    || l.Sweep == Sweep.FreeRanging || r.Sweep == Sweep.FreeRanging)
                    return NotStreamable();

                var sweep = CombineSweep(l.Sweep, r.Sweep);
                // Two striding operands compose to crawling (§19.8.8.3); otherwise take the
                // "widest" node posture reached (crawling ⊃ striding).
                var posture = WidenNodePosture(l.Posture, r.Posture);
                return new PostureSweep(posture, sweep);
            }

            // Arithmetic, value/general comparisons, logical, is/precedes/follows, range,
            // concat, otherwise: all yield atomic/boolean results — grounded. These ATOMIZE
            // their node operands, so atomizing a striding/crawling element operand walks its
            // subtree (Consuming) — see AtomizeSweep. (Node-identity ops is/<</>> compare
            // identity not value, but treating them as atomizing is conservatively sound.)
            //
            // §19.8.5 multiple-consuming-operands rule: if BOTH operands independently consume
            // the streamed context (e.g. count(*) + count(*/*)), the input would have to be
            // read twice ⇒ free-ranging. This is the composition that makes the "multiple
            // downward selections in a loop body" cases non-streamable (si-*-905).
            default:
            {
                var la = AtomizeSweep(bin.Left, ctx);
                var ra = AtomizeSweep(bin.Right, ctx);
                if (la == Sweep.FreeRanging || ra == Sweep.FreeRanging)
                    return NotStreamable();
                if (Consumes(bin.Left, ctx) && Consumes(bin.Right, ctx))
                    return Grounded(Sweep.FreeRanging);
                return Grounded(CombineSweep(la, ra));
            }
        }
    }

    private static PostureSweep ClassifyUnary(UnaryExpression un, StreamingContext ctx)
        // Unary +/-/not yield atomic/boolean results — grounded, operand's sweep.
        => Grounded(SweepOf(un.Operand, ctx));

    private static PostureSweep ClassifyFilter(FilterExpression filt, StreamingContext ctx)
    {
        // A filter preserves the primary's posture; predicates only add to the sweep
        // (§19.8: a predicate cannot change the posture of what it filters).
        var primary = Classify(filt.Primary, ctx);
        if (primary.Sweep == Sweep.FreeRanging || !IsStreamablePosture(primary.Posture))
            return NotStreamable();

        // §19.8.8 (same rule as ClassifyStep): a consuming/free-ranging predicate on a
        // STREAMED (non-grounded) primary forces the sequence to be buffered ⇒ roaming.
        // Only motionless predicates preserve a streamed posture.
        bool grounded = primary.Posture == Posture.Grounded;
        var sweep = primary.Sweep;
        foreach (var pred in filt.Predicates)
        {
            var p = Classify(pred, ctx with { ContextPosture = primary.Posture });
            if (!grounded && p.Sweep != Sweep.Motionless)
            {
                return p.Sweep == Sweep.FreeRanging
                    ? new PostureSweep(Posture.Roaming, Sweep.FreeRanging)
                    : new PostureSweep(Posture.Roaming, Sweep.Consuming);
            }
            sweep = CombineSweep(sweep, p.Sweep);
        }
        return new PostureSweep(primary.Posture, sweep);
    }

    private static PostureSweep ClassifySimpleMap(SimpleMapExpression sm, StreamingContext ctx)
    {
        // expr ! step : the left operand is navigated, then the right is evaluated per item
        // with that item as context. The result posture is the right operand's posture
        // computed against the left's item posture; sweeps combine.
        var left = Classify(sm.Left, ctx);
        if (left.Sweep == Sweep.FreeRanging || !IsStreamablePosture(left.Posture))
            return NotStreamable();

        var right = Classify(sm.Right, ctx with { ContextPosture = left.Posture });
        if (right.Sweep == Sweep.FreeRanging || !IsStreamablePosture(right.Posture))
            return NotStreamable();

        return new PostureSweep(right.Posture, CombineSweep(left.Sweep, right.Sweep));
    }

    private static PostureSweep ClassifyFlwor(FlworExpression flwor, StreamingContext ctx)
    {
        // Only for/let clauses are modelled here (Task 0.3). The binding posture flows into
        // the return expression as the new context posture for the last-bound range variable;
        // but since the return expression references the variable (not `.`), we approximate by
        // classifying the return under a context whose posture is the last for-binding's item
        // posture, and combine sweeps across all bindings + return.
        var sweep = Sweep.Motionless;
        Posture lastBindingPosture = ctx.ContextPosture;

        foreach (var clause in flwor.Clauses)
        {
            switch (clause)
            {
                case ForClause fc:
                    foreach (var b in fc.Bindings)
                    {
                        var bs = Classify(b.Expression, ctx);
                        if (bs.Sweep == Sweep.FreeRanging || !IsStreamablePosture(bs.Posture))
                            return NotStreamable();
                        sweep = CombineSweep(sweep, bs.Sweep);
                        lastBindingPosture = bs.Posture;
                    }
                    break;

                case LetClause lc:
                    foreach (var b in lc.Bindings)
                    {
                        var bs = Classify(b.Expression, ctx);
                        if (bs.Sweep == Sweep.FreeRanging || !IsStreamablePosture(bs.Posture))
                            return NotStreamable();
                        sweep = CombineSweep(sweep, bs.Sweep);
                    }
                    break;

                case WhereClause wc:
                {
                    var ws = Classify(wc.Condition, ctx with { ContextPosture = lastBindingPosture });
                    if (ws.Sweep == Sweep.FreeRanging)
                        return NotStreamable();
                    sweep = CombineSweep(sweep, ws.Sweep);
                    break;
                }

                default:
                    // TODO(Task 0.4+): order by / group by / count / window / while.
                    return NotStreamable();
            }
        }

        // The return expression's own `.` posture is the last for-binding's item posture
        // (a range variable ranges over individual items of its binding sequence).
        var ret = Classify(flwor.ReturnExpression, ctx with { ContextPosture = lastBindingPosture });
        if (ret.Sweep == Sweep.FreeRanging || !IsStreamablePosture(ret.Posture))
            return NotStreamable();

        return new PostureSweep(ret.Posture, CombineSweep(sweep, ret.Sweep));
    }

    private static PostureSweep ClassifySequence(IReadOnlyList<XQueryExpression> items, StreamingContext ctx)
    {
        if (items.Count == 0)
            return Grounded(Sweep.Motionless);

        Posture posture = Posture.Grounded;
        var sweep = Sweep.Motionless;
        var first = true;
        foreach (var item in items)
        {
            var ps = Classify(item, ctx);
            if (ps.Sweep == Sweep.FreeRanging || !IsStreamablePosture(ps.Posture))
                return NotStreamable();
            sweep = CombineSweep(sweep, ps.Sweep);
            posture = first ? ps.Posture : WidenNodePosture(posture, ps.Posture);
            first = false;
        }
        return new PostureSweep(posture, sweep);
    }

    // -----------------------------------------------------------------------
    // Sweep / posture helpers
    // -----------------------------------------------------------------------

    private static PostureSweep Grounded(Sweep sweep) => new(Posture.Grounded, sweep);

    /// <summary>
    /// A grounded (atomic/boolean) result derived from inspecting <paramref name="e"/> WITHOUT
    /// atomizing it, that nonetheless propagates non-streamability: if the operand roams or is
    /// free-ranging, the whole expression is not guaranteed streamable.
    /// </summary>
    private static PostureSweep GroundedFrom(XQueryExpression e, StreamingContext ctx)
    {
        var ps = Classify(e, ctx);
        if (ps.Sweep == Sweep.FreeRanging || !IsStreamablePosture(ps.Posture))
            return NotStreamable();
        return Grounded(ps.Sweep);
    }

    private static PostureSweep NotStreamable() => new(Posture.Roaming, Sweep.FreeRanging);

    private static Sweep SweepOf(XQueryExpression e, StreamingContext ctx) => Classify(e, ctx).Sweep;

    /// <summary>
    /// The sweep contributed by ATOMIZING an operand (as a comparison / cast / arithmetic /
    /// string-value operand does). §19.8: atomizing a <b>striding or crawling</b> node walks
    /// its element string-value (the whole subtree) ⇒ Consuming, even if navigating to the
    /// node was itself motionless. Atomizing a <b>climbing</b> node (an attribute / namespace)
    /// or a <b>grounded</b> value reads no streamed subtree ⇒ the operand's own sweep. A
    /// free-ranging operand stays free-ranging. This is exactly what separates the streamable
    /// attribute predicate <c>@v[xs:decimal(.) gt 0]</c> (climbing <c>.</c> → motionless) from
    /// the non-streamable element predicate <c>PAGES[. lt 1000]</c> (striding <c>.</c> →
    /// consuming ⇒ the filtered sequence roams).
    /// </summary>
    private static Sweep AtomizeSweep(XQueryExpression e, StreamingContext ctx)
    {
        var ps = Classify(e, ctx);
        if (ps.Sweep == Sweep.FreeRanging)
            return Sweep.FreeRanging;
        return ps.Posture is Posture.Striding or Posture.Crawling
            ? CombineSweep(ps.Sweep, Sweep.Consuming)
            : ps.Sweep;
    }

    private static Sweep WorstAtomizeSweep(IReadOnlyList<XQueryExpression> exprs, StreamingContext ctx)
    {
        var sweep = Sweep.Motionless;
        foreach (var e in exprs)
            sweep = CombineSweep(sweep, AtomizeSweep(e, ctx));
        return sweep;
    }

    /// <summary>
    /// Whether evaluating <paramref name="e"/> (as an atomized operand) advances the streamed
    /// input — i.e. its atomize-sweep is Consuming or worse. Only meaningful in streamed scope;
    /// a grounded/motionless operand (a literal, a bare attribute read) does not consume.
    /// </summary>
    private static bool Consumes(XQueryExpression e, StreamingContext ctx)
        => ctx.InStreamedScope && AtomizeSweep(e, ctx) != Sweep.Motionless;

    private static Sweep WorstSweep(IReadOnlyList<XQueryExpression> exprs, StreamingContext ctx)
    {
        var sweep = Sweep.Motionless;
        foreach (var e in exprs)
            sweep = CombineSweep(sweep, Classify(e, ctx).Sweep);
        return sweep;
    }

    /// <summary>
    /// True iff every operand is individually guaranteed-streamable (streamable posture AND
    /// not free-ranging). Used by absorbing/boolean functions so a roaming operand (e.g. a
    /// consuming-predicate filter) is not silently flattened to "consuming".
    /// </summary>
    private static bool AllOperandsStreamable(IReadOnlyList<XQueryExpression> exprs, StreamingContext ctx)
    {
        foreach (var e in exprs)
        {
            var ps = Classify(e, ctx);
            if (ps.Sweep == Sweep.FreeRanging || !IsStreamablePosture(ps.Posture))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Combines two sweeps taking the more-consuming of the two: Motionless &lt; Consuming &lt;
    /// FreeRanging. (§19.8: sequential composition of sweeps.)
    /// </summary>
    private static Sweep CombineSweep(Sweep a, Sweep b)
        => (Sweep)System.Math.Max((int)a, (int)b);

    /// <summary>
    /// True if <paramref name="e"/> COMBINES multiple node sequences into one (a union /
    /// intersect / except, or a comma-sequence of ≥2 items). §19.8.8.3: navigating a further
    /// step off such a combined operand is not guaranteed streamable.
    /// </summary>
    private static bool IsNodeCombiningExpression(XQueryExpression e) => e switch
    {
        BinaryExpression { Operator: BinaryOperator.Union or BinaryOperator.Intersect
            or BinaryOperator.Except } => true,
        SequenceExpression seq => seq.Items.Count > 1,
        _ => false,
    };

    private static bool IsNodePosture(Posture p)
        => p is Posture.Striding or Posture.Crawling or Posture.Climbing;

    /// <summary>
    /// The postures a <c>for-each</c> / <c>iterate</c> population (select) expression may have
    /// and still stream: STRIDING (a flat set of siblings, the classic streamable loop) or
    /// GROUNDED (a materialised sequence — e.g. <c>.//*/name()</c> mapped to strings, or a
    /// grounded map's keys). A CRAWLING select (nested descendants, <c>//ITEM/TITLE</c>) is
    /// NOT streamable in a loop (si-for-each-806); climbing/roaming likewise.
    /// </summary>
    private static bool IsForEachPopulationPosture(Posture p)
        => p is Posture.Striding or Posture.Grounded;

    private static bool IsStreamablePosture(Posture p)
        => p is Posture.Grounded or Posture.Striding or Posture.Crawling or Posture.Climbing;

    /// <summary>
    /// Widens two node postures per §19.8.8.3: any pairing that involves descendant nesting is
    /// crawling; two striding operands compose to crawling (their node-sets may interleave).
    /// </summary>
    private static Posture WidenNodePosture(Posture a, Posture b)
    {
        if (a == Posture.Grounded) return b;
        if (b == Posture.Grounded) return a;
        if (a == Posture.Climbing || b == Posture.Climbing) return Posture.Climbing;
        // striding∪striding, striding∪crawling, crawling∪crawling → crawling.
        return Posture.Crawling;
    }

    // -----------------------------------------------------------------------
    // Minimal function-role table (Task 0.3). NOT the unified legacy name-list —
    // that consolidation is a later phase. Roles determine motionless/consuming/
    // grounding behaviour per §19.8's operand-usage classification.
    // -----------------------------------------------------------------------

    private enum FnRole
    {
        Unknown,
        NodeProperty,  // reads node identity/name only — Inspection, motionless, grounded result
        StringValue,   // atomizes subtree string-value — Absorption, consuming, grounded result
        Atomizing,     // atomizes operand(s) → grounded atomic/boolean; posture-aware sweep
        Aggregate,     // absorbs a sequence to an atomic — Absorption, consuming, grounded result
        Transmission,  // passes a subset of the operand through — posture-preserving
        Boolean,       // reduces to an atomic boolean — grounded
        CurrentGroup,  // current-group() — striding over the buffered group, motionless
        Positional,    // position()/last() — need look-ahead over the sequence → free-ranging
        FreeRanging,   // reverse() and other whole-sequence-buffering fns → free-ranging
    }

    private static FnRole FunctionRole(string localName, int arity)
    {
        _ = arity; // arity-sensitivity is a later-phase refinement.
        return localName switch
        {
            "name" or "local-name" or "node-name" or "namespace-uri"
                or "generate-id" => FnRole.NodeProperty,

            // §19.8.8: position()/last() require the size/index of the sequence being
            // filtered — knowing last() needs look-ahead to the end of a striding sequence,
            // so a predicate using them cannot be evaluated in a single forward pass. They
            // are free-ranging (a bare [1] constant-positional filter uses an IntegerLiteral,
            // NOT these functions, and stays motionless).
            "position" or "last" => FnRole.Positional,

            "string" or "data" or "normalize-space" => FnRole.StringValue,

            // §19.8 / fn spec: numeric, string-manipulation and boolean/comparison functions
            // that ATOMIZE their node operand(s) and return a grounded atomic/boolean. Routed
            // through the posture-aware Atomizing role so number(@v) is motionless-streamable
            // while number(child::PRICE) is consuming (element subtree walked). Sweep is the
            // worst ATOMIZE-sweep of the operands.
            //
            // Numeric.
            "number" or "abs" or "round" or "round-half-to-even" or "floor" or "ceiling"
            // String length / manipulation / predicates.
                or "string-length" or "upper-case" or "lower-case" or "translate"
                or "normalize-unicode" or "substring" or "substring-before" or "substring-after"
                or "contains" or "starts-with" or "ends-with" or "matches" or "replace"
            // URI escaping (operate on the atomized string value).
                or "encode-for-uri" or "escape-html-uri" or "iri-to-uri"
            // Sequence-inspecting boolean/comparison predicates that atomize/deep-walk operands.
                or "has-children" or "deep-equal" or "compare" or "index-of" or "codepoint-equal"
                => FnRole.Atomizing,

            "count" or "sum" or "avg" or "max" or "min"
                or "string-join" or "concat"
            // §19.8 / fn spec: tokenize/distinct-values/string-to-codepoints absorb their
            // (atomized) operand and produce a grounded atomic sequence — consuming. Grouped
            // with the absorbing aggregates (forced-consuming) per the fn streamability
            // categories; a non-streamable operand still propagates.
                or "tokenize" or "distinct-values" or "string-to-codepoints"
                => FnRole.Aggregate,

            // §19.8.8: fn:reverse must buffer its whole operand sequence to emit it in
            // reverse order — it is NOT a posture-preserving transmission like head/tail/
            // subsequence (which emit a forward-order subset). Reversing a streamed path is
            // free-ranging.
            "reverse" => FnRole.FreeRanging,

            // §19.8: POSTURE-PRESERVING transmissions — the result is (a subset / reordering-
            // free view of) the SAME nodes as the argument, so result posture = arg posture and
            // result sweep = combine(arg sweep, secondary-arg sweeps). CRITICAL: one-or-more/…
            // must NOT ground or motionless-collapse their argument — one-or-more(child::A)
            // stays (Striding, Consuming); a roaming arg propagates.
            //
            // NOTE (Phase 1.5 Task A): fn:outermost / fn:innermost are DELIBERATELY NOT modelled
            // as transmissions and left conservative (Unknown → NotStreamable). Although each
            // returns a subset of its argument's nodes, the W3C streaming corpus REQUIRES
            // XTSE3430 for innermost()/outermost() over a NON-GROUNDED (streamed) operand
            // (sf-innermost-901: innermost(/chapter//section)/@id) — determining the innermost /
            // outermost members of a streamed node-set needs the whole set buffered. Modelling
            // them as posture-preserving over a striding/crawling operand OVER-ACCEPTS those
            // (the shadow over-accept pin caught it), so they stay rejecting until a grounded-
            // operand-only refinement is added in a later phase.
            "head" or "tail" or "subsequence" or "remove" or "insert-before"
                or "subsequence-before" or "subsequence-after"
                or "one-or-more" or "zero-or-one" or "exactly-one"
                or "unordered" or "trace" => FnRole.Transmission,

            "boolean" or "not" or "exists" or "empty" or "true" or "false" => FnRole.Boolean,

            // §19.8: fn:snapshot / fn:copy-of make a GROUNDED deep copy of their operand,
            // consuming the operand's subtree in the process (Absorption → grounded result,
            // consuming sweep). This is exactly the streamable "copy the current node away"
            // idiom used inside streamed for-each/iterate bodies.
            "snapshot" or "copy-of" => FnRole.Aggregate,

            // §18.5.4 current-group() / current-grouping-key(): inside a streamable
            // group-adjacent / group-starting-with / group-ending-with, the current group is
            // the buffered burst the processor is holding — a striding sequence over the
            // grouped items, motionless (already in hand, no further stream advance to read it).
            "current-group" => FnRole.CurrentGroup,
            "current-grouping-key" => FnRole.NodeProperty,

            _ => FnRole.Unknown,
        };
    }

    // =======================================================================
    // Task 0.4: XSLT INSTRUCTION classification (§19.8).
    //
    // Shadow-only, like the expression classifier above: wired into nothing.
    // Composes an instruction's (posture, sweep) over its select / content / body
    // by delegating navigation to the EXPRESSION Classify overload.
    //
    // Key §19.8 divergence from the expression classifier: XSLT *construction*
    // instructions (xsl:copy, xsl:element, LRE, xsl:document, xsl:attribute,
    // xsl:comment, xsl:processing-instruction, xsl:text, xsl:map, xsl:array, …)
    // build BRAND-NEW nodes in a result tree, so their result posture is GROUNDED
    // (§19.8.2 — constructed nodes are grounded), even though their content may
    // freely consume the streamed input. That is the opposite of how the XQuery
    // direct-constructor grammar is treated (left unsupported / roaming above).
    // =======================================================================

    /// <summary>
    /// Computes the composed <see cref="PostureSweep"/> of an XSLT <paramref name="insn"/>
    /// in the streaming environment <paramref name="ctx"/>, per XSLT 3.0 §19.8.
    /// </summary>
    /// <param name="insn">The XSLT instruction to classify.</param>
    /// <param name="ctx">The streaming context (context-item posture and streamed-scope flag).</param>
    /// <returns>The composed posture and sweep of the instruction's contribution to the result.</returns>
    public static PostureSweep Classify(XsltInstruction insn, StreamingContext ctx)
    {
        ArgumentNullException.ThrowIfNull(insn);

        return insn switch
        {
            // ---- Sequence constructor (a body) --------------------------------
            XsltSequenceConstructor sc => Classify(sc, ctx),

            // ---- Construction instructions → GROUNDED result (§19.8.2) --------
            // Result posture is Grounded (new nodes); sweep is the combined sweep of
            // whatever they read to build that content (select / content / AVTs).
            // §19.8 (family 4): a construction instruction carrying use-attribute-sets pulls
            // in an attribute set whose own body may be non-streamable. The classifier does
            // not yet model attribute-set streamability, so CONSERVATIVELY reject any
            // construction that references one — this can only ever be a correct rejection
            // (never an over-accept). Modelling attribute-set posture is a later phase.
            XsltLiteralResultElement { UseAttributeSets.Count: > 0 } => NotStreamable(),
            XsltElement { UseAttributeSets.Count: > 0 } => NotStreamable(),
            XsltCopy { UseAttributeSets.Count: > 0 } => NotStreamable(),

            XsltLiteralResultElement lre => Grounded(LreSweep(lre, ctx)),
            XsltElement el => Grounded(CombineSweep(AvtSweep(el.Name, ctx),
                CombineSweep(AvtSweep(el.Namespace, ctx), BodySweep(el.Content, ctx)))),
            XsltCopy cp => Grounded(cp.Select is not null
                // xsl:copy with select copies the selected node's shallow shell; the select
                // is navigated (its sweep flows through) but the constructed copy is grounded.
                ? CombineSweep(SweepOf(cp.Select, ctx), BodySweep(cp.Content, ctx))
                : BodySweep(cp.Content, ctx)),
            XsltDocument doc => Grounded(BodySweep(doc.Content, ctx)),
            XsltResultDocument rd => Grounded(BodySweep(rd.Content, ctx)),
            XsltComment cm => Grounded(SelectOrContentSweep(cm.Select, cm.Content, ctx)),
            XsltProcessingInstruction pi => Grounded(CombineSweep(AvtSweep(pi.Name, ctx),
                SelectOrContentSweep(pi.Select, pi.Content, ctx))),
            XsltNamespace ns => Grounded(CombineSweep(AvtSweep(ns.Name, ctx),
                SelectOrContentSweep(ns.Select, ns.Content, ctx))),
            XsltText => Grounded(Sweep.Motionless),
            XsltLiteralText => Grounded(Sweep.Motionless),
            XsltTextValueTemplate tvt => Grounded(AvtSweep(tvt.Template, ctx)),
            XsltAttribute at => Grounded(CombineSweep(AvtSweep(at.Name, ctx),
                CombineSweep(AvtSweep(at.Namespace, ctx),
                    SelectOrContentSweep(at.Select, at.Content, ctx)))),
            XsltMap m => Grounded(BodySweep(m.Content, ctx)),
            XsltMapEntry me => Grounded(CombineSweep(SweepOf(me.Key, ctx),
                SelectOrContentSweep(me.Select, me.Content, ctx))),
            XsltArray ar => Grounded(BodySweep(ar.Content, ctx)),
            XsltArrayMember am => Grounded(SelectOrContentSweep(am.Select, am.Content, ctx)),

            // ---- Transmitting / copying instructions --------------------------
            // §19.8: xsl:copy-of makes a GROUNDED deep copy of the selected sequence — its
            // result is grounded (detached from the stream), even though reading the select
            // consumes the input. This is exactly what makes the 013-shape (copy-of select=".")
            // streamable inside a for-each/iterate body, while xsl:sequence select="." (which
            // transmits the striding node reference, below) is NOT. The select must itself be
            // streamable.
            XsltCopyOf co => ClassifyCopyOf(co.Select, ctx),
            // xsl:sequence transmits the selected sequence UNCHANGED — it preserves the
            // select's posture AND sweep (a streamed node stays streamed).
            XsltSequence sq => sq.Select is not null
                ? ClassifySelectTransmit(sq.Select, ctx)
                : Grounded(BodySweep(sq.Content, ctx)),
            XsltValueOf vo => vo.Select is not null
                // xsl:value-of result is an atomic string ⇒ Grounded posture, select's sweep.
                ? Grounded(SweepOf(vo.Select, ctx))
                : Grounded(BodySweep(vo.Content, ctx)),
            XsltVariableInstruction vi => vi.Select is not null
                ? ClassifySelectTransmit(vi.Select, ctx)
                : Grounded(BodySweep(vi.Content, ctx)),

            // ---- Iteration ----------------------------------------------------
            XsltForEach fe => ClassifyForEach(fe, ctx),
            XsltIterate it => ClassifyIterate(it, ctx),
            XsltForEachGroup feg => ClassifyForEachGroup(feg, ctx),
            XsltApplyTemplates ap => ClassifyApplyTemplates(ap, ctx),

            // ---- Conditionals -------------------------------------------------
            XsltIf iff => ClassifyIf(iff, ctx),
            XsltChoose ch => ClassifyChoose(ch, ctx),
            XsltWherePopulated wp => Classify(wp.Content, ctx),
            XsltOnEmpty oe => ClassifyBranchWithOptionalSelect(oe.Select, oe.Content, ctx),
            XsltOnNonEmpty one => ClassifyBranchWithOptionalSelect(one.Select, one.Content, ctx),
            XsltTry tr => ClassifyTry(tr, ctx),

            // ---- Iteration control (grounded, motionless) ---------------------
            // §19.8: xsl:break / xsl:next-iteration are control transfers; they emit no
            // streamed nodes of their own and (bar their optional select) advance nothing.
            XsltBreak br => Grounded(br.Select is not null ? SweepOf(br.Select, ctx) : Sweep.Motionless),
            XsltNextIteration => Grounded(Sweep.Motionless),

            // TODO(§19.8, later phase): xsl:merge, xsl:fork, xsl:analyze-string,
            // xsl:evaluate, xsl:switch, xsl:for-each-member, xsl:number, xsl:perform-sort,
            // xsl:call-template, xsl:next-match, xsl:apply-imports, xsl:message, xsl:assert.
            // Conservatively NOT streamable so nothing is silently accepted.
            _ => NotStreamable(),
        };
    }

    /// <summary>
    /// Computes the composed <see cref="PostureSweep"/> of a sequence-constructor body per
    /// §19.8: fold over its child instructions — posture widens over the children, sweep is
    /// the worst of the children's sweeps. An empty (or grounded-only) body is
    /// <c>(Grounded, Motionless)</c>.
    /// </summary>
    /// <param name="body">The sequence constructor whose child instructions are combined.</param>
    /// <param name="ctx">The streaming context in which the body is evaluated.</param>
    /// <returns>The composed posture and sweep of the body.</returns>
    public static PostureSweep Classify(XsltSequenceConstructor body, StreamingContext ctx)
    {
        ArgumentNullException.ThrowIfNull(body);

        Posture posture = Posture.Grounded;
        var sweep = Sweep.Motionless;
        var first = true;
        foreach (var child in body.Instructions)
        {
            var ps = Classify(child, ctx);
            if (ps.Sweep == Sweep.FreeRanging || !IsStreamablePosture(ps.Posture))
                return NotStreamable();
            sweep = CombineSweep(sweep, ps.Sweep);
            // §19.8: a sequence of constructed + streamed items takes the widened streamed
            // posture (Grounded is absorbed by any streamed posture — see WidenNodePosture).
            posture = first ? ps.Posture : WidenNodePosture(posture, ps.Posture);
            first = false;
        }
        return new PostureSweep(posture, sweep);
    }

    // -----------------------------------------------------------------------
    // Instruction helpers
    // -----------------------------------------------------------------------

    /// <summary>xsl:sequence transmits the select's posture AND sweep unchanged.</summary>
    private static PostureSweep ClassifySelectTransmit(XQueryExpression select, StreamingContext ctx)
    {
        var s = Classify(select, ctx);
        if (s.Sweep == Sweep.FreeRanging || !IsStreamablePosture(s.Posture))
            return NotStreamable();
        return s;
    }

    /// <summary>
    /// xsl:copy-of makes a GROUNDED deep copy of the selected sequence: the result posture is
    /// Grounded (detached), the sweep is the select's sweep (reading it consumes the stream).
    /// A non-streamable select propagates.
    /// </summary>
    private static PostureSweep ClassifyCopyOf(XQueryExpression select, StreamingContext ctx)
    {
        var s = Classify(select, ctx);
        if (s.Sweep == Sweep.FreeRanging || !IsStreamablePosture(s.Posture))
            return NotStreamable();
        return Grounded(s.Sweep);
    }

    private static PostureSweep ClassifyForEach(XsltForEach fe, StreamingContext ctx)
    {
        // §19.8: the select is the "population expression" (navigation role); the body is
        // classified with the context item = the per-item posture of the select's result.
        var s = Classify(fe.Select, ctx);
        // Family 5a: the population select must be STRIDING or GROUNDED. A CRAWLING select
        // (e.g. //ITEM/TITLE) yields overlapping/nested items whose independent processing
        // in a loop is not guaranteed streamable (si-for-each-806). Climbing/roaming likewise.
        if (!IsForEachPopulationPosture(s.Posture) || s.Sweep == Sweep.FreeRanging)
            return NotStreamable();

        var body = Classify(fe.Body, ctx with { ContextPosture = s.Posture });
        // Family 5b: the body must produce a GROUNDED result — it may consume the current
        // item but must not RETURN a streamed node (xsl:sequence select="." is striding →
        // not streamable, si-for-each-907), and multiple downward selections in the body
        // (count(*)+count(*/*)) already surface as free-ranging (si-for-each-905).
        if (body.Sweep == Sweep.FreeRanging || body.Posture != Posture.Grounded)
            return NotStreamable();

        return new PostureSweep(Posture.Grounded, CombineSweep(s.Sweep, body.Sweep));
    }

    private static PostureSweep ClassifyIterate(XsltIterate it, StreamingContext ctx)
    {
        // §19.8: xsl:iterate is like xsl:for-each over the select, but its xsl:param values
        // are grounded accumulators carried across iterations and xsl:on-completion emits
        // grounded state. Streamable iff the population select is striding/grounded and the
        // body (which may contain xsl:break/xsl:next-iteration) produces a grounded result.
        var s = Classify(it.Select, ctx);
        // Family 5a: population select must be striding or grounded (a crawling select is not
        // streamable in an iterate loop, same as for-each).
        if (!IsForEachPopulationPosture(s.Posture) || s.Sweep == Sweep.FreeRanging)
            return NotStreamable();

        var body = Classify(it.Body, ctx with { ContextPosture = s.Posture });
        // Family 5b: body must be grounded (xsl:sequence select="." → striding → rejected,
        // si-iterate-907; count(*)+count(*/*) → free-ranging, si-iterate-905).
        if (body.Sweep == Sweep.FreeRanging || body.Posture != Posture.Grounded)
            return NotStreamable();

        var sweep = CombineSweep(s.Sweep, body.Sweep);

        // on-completion runs after the stream is exhausted, over grounded accumulator state —
        // it must not itself free-range on the (now consumed) input.
        if (it.OnCompletion is not null)
        {
            var oc = Classify(it.OnCompletion, ctx with { ContextPosture = Posture.Grounded });
            if (oc.Sweep == Sweep.FreeRanging || !IsStreamablePosture(oc.Posture))
                return NotStreamable();
            sweep = CombineSweep(sweep, oc.Sweep);
        }

        // xsl:iterate emits the body's result per item plus grounded on-completion state.
        return new PostureSweep(body.Posture, sweep);
    }

    private static PostureSweep ClassifyForEachGroup(XsltForEachGroup feg, StreamingContext ctx)
    {
        // §19.8 / §18.5: group-by requires holding all groups simultaneously — NOT streamable.
        if (feg.GroupBy is not null)
            return NotStreamable(); // (Roaming, FreeRanging)

        // group-adjacent / group-starting-with / group-ending-with are the streamable grouping
        // forms: the processor buffers only the current group. The grouping key must itself be
        // motionless (a simple key over the current item), else grouping is not streamable.
        var s = Classify(feg.Select, ctx);
        if (s.Sweep == Sweep.FreeRanging || !IsStreamablePosture(s.Posture))
            return NotStreamable();

        Sweep keySweep = Sweep.Motionless;
        if (feg.GroupAdjacent is not null)
        {
            var k = Classify(feg.GroupAdjacent, ctx with { ContextPosture = s.Posture });
            // A consuming (let alone free-ranging) adjacency key breaks streamable grouping.
            if (k.Sweep != Sweep.Motionless)
                return NotStreamable();
            keySweep = k.Sweep;
        }
        // group-starting-with / group-ending-with use patterns (not expressions); a pattern
        // match against the current item is motionless — nothing extra to fold here.

        // Inside the body the current group is a striding burst the processor holds.
        var body = Classify(feg.Body, ctx with { ContextPosture = Posture.Striding });
        if (body.Sweep == Sweep.FreeRanging || !IsStreamablePosture(body.Posture))
            return NotStreamable();

        // The grouping itself advances the stream (Consuming), so combine at least Consuming.
        var sweep = CombineSweep(CombineSweep(s.Sweep, Sweep.Consuming),
            CombineSweep(keySweep, body.Sweep));
        return new PostureSweep(body.Posture, sweep);
    }

    private static PostureSweep ClassifyApplyTemplates(XsltApplyTemplates ap, StreamingContext ctx)
    {
        // §19.8: apply-templates streams over the selected nodes; a null select means the
        // context node's children (striding). The instruction's own posture/sweep is that of
        // its select — the invoked templates are classified independently at their own sites.
        if (ap.Select is null)
        {
            // No select ⇒ child::node() of the context. Striding + consuming when streamed.
            return ctx.InStreamedScope
                ? new PostureSweep(Posture.Striding, Sweep.Consuming)
                : Grounded(Sweep.Motionless);
        }
        return ClassifySelectTransmit(ap.Select, ctx);
    }

    private static PostureSweep ClassifyIf(XsltIf iff, StreamingContext ctx)
    {
        // §19.8: combine the test's sweep with the branch's posture/sweep. A single-branch
        // xsl:if has an implicit empty else ⇒ posture widens branch with grounded-empty.
        var test = Classify(iff.Test, ctx);
        if (test.Sweep == Sweep.FreeRanging)
            return NotStreamable();
        var then = Classify(iff.Then, ctx);
        if (then.Sweep == Sweep.FreeRanging || !IsStreamablePosture(then.Posture))
            return NotStreamable();
        return new PostureSweep(then.Posture, CombineSweep(test.Sweep, then.Sweep));
    }

    private static PostureSweep ClassifyChoose(XsltChoose ch, StreamingContext ctx)
    {
        // §19.8: posture = widen over all branch postures; sweep = combine over every test
        // sweep and every branch sweep (each branch and test is evaluated in the worst case).
        Posture posture = Posture.Grounded;
        var sweep = Sweep.Motionless;
        var first = true;
        foreach (var when in ch.When)
        {
            var test = Classify(when.Test, ctx);
            if (test.Sweep == Sweep.FreeRanging)
                return NotStreamable();
            var body = Classify(when.Body, ctx);
            if (body.Sweep == Sweep.FreeRanging || !IsStreamablePosture(body.Posture))
                return NotStreamable();
            sweep = CombineSweep(sweep, CombineSweep(test.Sweep, body.Sweep));
            posture = first ? body.Posture : WidenNodePosture(posture, body.Posture);
            first = false;
        }
        if (ch.Otherwise is not null)
        {
            var ob = Classify(ch.Otherwise, ctx);
            if (ob.Sweep == Sweep.FreeRanging || !IsStreamablePosture(ob.Posture))
                return NotStreamable();
            sweep = CombineSweep(sweep, ob.Sweep);
            posture = first ? ob.Posture : WidenNodePosture(posture, ob.Posture);
            first = false;
        }
        return new PostureSweep(posture, sweep);
    }

    private static PostureSweep ClassifyTry(XsltTry tr, StreamingContext ctx)
    {
        // §19.8: xsl:try combines its body (or select) with every catch branch — posture
        // widens over body + catches, sweep combines over all of them.
        var bodyPs = tr.SelectExpression is not null
            ? ClassifySelectTransmit(tr.SelectExpression, ctx)
            : tr.Body is not null ? Classify(tr.Body, ctx) : Grounded(Sweep.Motionless);
        if (bodyPs.Sweep == Sweep.FreeRanging || !IsStreamablePosture(bodyPs.Posture))
            return NotStreamable();

        Posture posture = bodyPs.Posture;
        var sweep = bodyPs.Sweep;
        foreach (var c in tr.Catches)
        {
            var cPs = c.SelectExpression is not null
                ? ClassifySelectTransmit(c.SelectExpression, ctx)
                : c.Body is not null ? Classify(c.Body, ctx) : Grounded(Sweep.Motionless);
            if (cPs.Sweep == Sweep.FreeRanging || !IsStreamablePosture(cPs.Posture))
                return NotStreamable();
            sweep = CombineSweep(sweep, cPs.Sweep);
            posture = WidenNodePosture(posture, cPs.Posture);
        }
        return new PostureSweep(posture, sweep);
    }

    private static PostureSweep ClassifyBranchWithOptionalSelect(
        XQueryExpression? select, XsltSequenceConstructor? content, StreamingContext ctx)
    {
        if (select is not null)
            return ClassifySelectTransmit(select, ctx);
        if (content is not null)
            return Classify(content, ctx);
        return Grounded(Sweep.Motionless);
    }

    /// <summary>
    /// Combined sweep of an instruction that offers a <c>select</c> XOR a <c>content</c> body.
    /// </summary>
    private static Sweep SelectOrContentSweep(
        XQueryExpression? select, XsltSequenceConstructor? content, StreamingContext ctx)
    {
        if (select is not null)
            return SweepOf(select, ctx);
        return BodySweep(content, ctx);
    }

    /// <summary>Combined sweep of a (possibly null) sequence-constructor body.</summary>
    private static Sweep BodySweep(XsltSequenceConstructor? body, StreamingContext ctx)
    {
        if (body is null)
            return Sweep.Motionless;
        var ps = Classify(body, ctx);
        // A free-ranging body poisons the enclosing construction's sweep to free-ranging.
        return ps.Sweep;
    }

    /// <summary>
    /// The combined sweep contributed by a literal-result-element: its content plus every
    /// attribute AVT. §19.8: an AVT like <c>{count(//*)}</c> makes the LRE consuming (though
    /// the LRE is still grounded because it constructs a new element).
    /// </summary>
    private static Sweep LreSweep(XsltLiteralResultElement lre, StreamingContext ctx)
    {
        var sweep = BodySweep(lre.Content, ctx);
        foreach (var avt in lre.Attributes.Values)
            sweep = CombineSweep(sweep, AvtSweep(avt, ctx));
        return sweep;
    }

    /// <summary>
    /// The combined sweep of an attribute-value template: the worst sweep across its
    /// embedded <c>{expr}</c> parts (literal parts are motionless).
    /// </summary>
    private static Sweep AvtSweep(XsltAttributeValueTemplate? avt, StreamingContext ctx)
    {
        if (avt is null)
            return Sweep.Motionless;
        var sweep = Sweep.Motionless;
        foreach (var part in avt.Parts)
        {
            if (part is AvtExpression ae)
                sweep = CombineSweep(sweep, SweepOf(ae.Expression, ctx));
        }
        return sweep;
    }
}
