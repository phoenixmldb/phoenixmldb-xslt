using PhoenixmlDb.XQuery.Ast;

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
            CastExpression c => Grounded(SweepOf(c.Expression, ctx)),
            CastableExpression c => Grounded(SweepOf(c.Expression, ctx)),
            TreatExpression t => Classify(t.Expression, ctx), // treat is a no-op on posture/sweep.
            InstanceOfExpression io => Grounded(SweepOf(io.Expression, ctx)),

            StringConcatExpression sc => Grounded(WorstSweep(sc.Operands, ctx)),

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
            // Downward, non-overlapping, document order → striding; advances the stream.
            case Axis.Child:
                axisPosture = Posture.Striding;
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

        // Fold predicate sweeps (predicates are evaluated with each candidate node as context).
        var predSweep = Sweep.Motionless;
        foreach (var pred in step.Predicates)
        {
            // Predicate context item is a node produced by this axis, whose posture is
            // axisPosture. A predicate never changes the step's result posture; it only
            // contributes to the sweep.
            var p = Classify(pred, ctx with { ContextPosture = axisPosture });
            predSweep = CombineSweep(predSweep, p.Sweep);
            if (predSweep == Sweep.FreeRanging)
                return new PostureSweep(Posture.Roaming, Sweep.FreeRanging);
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
        var role = FunctionRole(fc.Name.LocalName, fc.Arguments.Count);

        switch (role)
        {
            case FnRole.NodeProperty:
                // §19.8 (Inspection): name()/local-name()/node-name()/namespace-uri() read only
                // the node's own properties — grounded (atomic) result, motionless.
                return Grounded(Sweep.Motionless);

            case FnRole.StringValue:
                // string()/data()/normalize-space()/string-length() atomize the subtree string
                // value — grounded (atomic) result, consuming the operand's subtree.
                return Grounded(CombineSweep(Sweep.Consuming, WorstSweep(fc.Arguments, ctx)));

            case FnRole.Aggregate:
                // count()/sum()/avg()/... absorb the operand sequence to an atomic — grounded,
                // consuming (the operand is fully walked).
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
                // sweep is the worst of the arguments' sweeps.
                return Grounded(WorstSweep(fc.Arguments, ctx));

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
            // concat, otherwise: all yield atomic/boolean results — grounded. Sweep is the
            // worst of the two operand sweeps (both must be evaluated).
            default:
                return Grounded(WorstSweep([bin.Left, bin.Right], ctx));
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

        var sweep = primary.Sweep;
        foreach (var pred in filt.Predicates)
        {
            var p = Classify(pred, ctx with { ContextPosture = primary.Posture });
            sweep = CombineSweep(sweep, p.Sweep);
            if (sweep == Sweep.FreeRanging)
                return NotStreamable();
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

    private static PostureSweep NotStreamable() => new(Posture.Roaming, Sweep.FreeRanging);

    private static Sweep SweepOf(XQueryExpression e, StreamingContext ctx) => Classify(e, ctx).Sweep;

    private static Sweep WorstSweep(IReadOnlyList<XQueryExpression> exprs, StreamingContext ctx)
    {
        var sweep = Sweep.Motionless;
        foreach (var e in exprs)
            sweep = CombineSweep(sweep, Classify(e, ctx).Sweep);
        return sweep;
    }

    /// <summary>
    /// Combines two sweeps taking the more-consuming of the two: Motionless &lt; Consuming &lt;
    /// FreeRanging. (§19.8: sequential composition of sweeps.)
    /// </summary>
    private static Sweep CombineSweep(Sweep a, Sweep b)
        => (Sweep)System.Math.Max((int)a, (int)b);

    private static bool IsNodePosture(Posture p)
        => p is Posture.Striding or Posture.Crawling or Posture.Climbing;

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
        Aggregate,     // absorbs a sequence to an atomic — Absorption, consuming, grounded result
        Transmission,  // passes a subset of the operand through — posture-preserving
        Boolean,       // reduces to an atomic boolean — grounded
    }

    private static FnRole FunctionRole(string localName, int arity)
    {
        _ = arity; // arity-sensitivity is a later-phase refinement.
        return localName switch
        {
            "name" or "local-name" or "node-name" or "namespace-uri"
                or "generate-id" or "position" or "last" => FnRole.NodeProperty,

            "string" or "data" or "normalize-space" or "string-length"
                or "upper-case" or "lower-case" => FnRole.StringValue,

            "count" or "sum" or "avg" or "max" or "min"
                or "string-join" or "concat" => FnRole.Aggregate,

            "head" or "tail" or "subsequence" or "remove" or "insert-before"
                or "reverse" or "subsequence-before" or "subsequence-after" => FnRole.Transmission,

            "boolean" or "not" or "exists" or "empty" or "true" or "false" => FnRole.Boolean,

            _ => FnRole.Unknown,
        };
    }
}
