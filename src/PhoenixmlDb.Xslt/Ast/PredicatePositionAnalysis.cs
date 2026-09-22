using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Decides whether evaluating a pattern predicate requires <c>position()</c>/<c>last()</c>
/// context to be computed first.
/// </summary>
/// <remarks>
/// <para>
/// Establishing that context costs a scan of every sibling matching the step's node test
/// (<c>ComputeNodePosition</c>), so paying for it on a predicate that cannot observe it makes
/// matching O(n²) in the number of siblings — see #95, where <c>w:p[@zzz]</c> costs the same as
/// a predicate that walks every descendant twice, because neither cost is the predicate.
/// </para>
/// <para>
/// The answer is a STATIC property of the expression, so it is decided once at parse time and
/// cached on <see cref="PatternStep"/> rather than recomputed per candidate node.
/// </para>
/// <para>
/// <b>This errs towards "yes".</b> Returning true is merely slow; returning false when the
/// predicate is positional would change which nodes match. So the rule is not "prove it needs
/// position" — it is "prove it CANNOT", and everything unrecognised keeps the old behaviour.
/// </para>
/// </remarks>
internal static class PredicatePositionAnalysis
{
    /// <summary>
    /// True if <paramref name="expr"/> mentions <c>position()</c> or <c>last()</c> anywhere.
    /// </summary>
    /// <remarks>
    /// The single implementation of this question. <c>StreamabilityChecker</c> asks it too, and
    /// a second copy is how one of a pair ends up with a rule its twin lacks.
    /// </remarks>
    internal static bool ContainsPositionOrLast(XQueryExpression expr)
    {
        var detector = new PositionLastDetector();
        detector.Walk(expr);
        return detector.Found;
    }

    /// <summary>
    /// True when any predicate in <paramref name="predicates"/> could observe position or size.
    /// </summary>
    internal static bool NeedsPositionContext(IReadOnlyList<XQueryExpression> predicates)
    {
        for (var i = 0; i < predicates.Count; i++)
        {
            if (NeedsPositionContext(predicates[i]))
                return true;
        }
        return false;
    }

    private static bool NeedsPositionContext(XQueryExpression predicate)
    {
        // A lexical mention settles it. position() inside a called function refers to that
        // function's own (absent) focus, not the caller's, so a lexical walk is sufficient —
        // and a nested predicate's last() re-indexes against the inner sequence, which this
        // treats as "needed" too. Conservative, therefore safe.
        if (ContainsPositionOrLast(predicate))
            return true;

        // XPath 3.1 §3.3.2: a predicate whose value is NUMERIC is positional — [3] means
        // position() = 3. Anything else is taken as an effective boolean value, which cannot
        // observe position. So the question is whether this expression can be numeric.
        return CanBeNumeric(predicate);
    }

    private static bool CanBeNumeric(XQueryExpression expr) => expr switch
    {
        // [3], [1.5] — the positional form the spec names.
        IntegerLiteral or DoubleLiteral or DecimalLiteral => true,
        // ['x'], [true()] as a literal — effective boolean value, not positional.
        StringLiteral or BooleanLiteral => false,

        // Comparisons and logical connectives yield xs:boolean, never a number.
        // This is the arm that makes #95's two cases cheap: `@zzz` is a step (below), and
        // `not(normalize-space(.)) and not(.//w:drawing)` is an And.
        BinaryExpression bin => bin.Operator switch
        {
            BinaryOperator.Equal or BinaryOperator.NotEqual
                or BinaryOperator.LessThan or BinaryOperator.LessOrEqual
                or BinaryOperator.GreaterThan or BinaryOperator.GreaterOrEqual
                or BinaryOperator.GeneralEqual or BinaryOperator.GeneralNotEqual
                or BinaryOperator.GeneralLessThan or BinaryOperator.GeneralLessOrEqual
                or BinaryOperator.GeneralGreaterThan or BinaryOperator.GeneralGreaterOrEqual
                or BinaryOperator.Is or BinaryOperator.Precedes or BinaryOperator.Follows
                or BinaryOperator.And or BinaryOperator.Or => false,
            // Arithmetic, union/intersect/except, range, concat, lookup: either numeric or
            // not worth proving. Keep the old path.
            _ => true,
        },

        // A step or path yields nodes; its effective boolean value is existence.
        StepExpression or PathExpression => false,

        // Functions whose return type is boolean by definition. Deliberately a short list of
        // the ones that actually appear in match predicates — an unknown function stays on the
        // old path rather than being guessed at.
        FunctionCallExpression fn => fn.Name.LocalName switch
        {
            "not" or "boolean" or "empty" or "exists" or "starts-with" or "ends-with"
                or "contains" or "matches" or "deep-equal" or "nilled" => false,
            _ => true,
        },

        // Anything else — variable references, arithmetic, conditionals, unknown shapes.
        _ => true,
    };

    /// <summary>Detects <c>position()</c> or <c>last()</c> calls anywhere in an expression.</summary>
    private sealed class PositionLastDetector : XQueryExpressionWalker
    {
        public bool Found { get; private set; }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (Found) return null;
            if (expr.Name.LocalName is "position" or "last" && expr.Arguments.Count == 0)
            {
                Found = true;
                return null;
            }
            foreach (var arg in expr.Arguments) Walk(arg);
            return null;
        }
    }
}
