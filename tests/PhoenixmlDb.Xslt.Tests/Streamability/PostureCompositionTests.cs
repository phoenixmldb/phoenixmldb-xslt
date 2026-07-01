using FluentAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests.Streamability;

/// <summary>
/// Task 0.2 foundation tests for the XSLT 3.0 §19 compositional streamability model.
/// Covers the <see cref="PostureSweep.IsGuaranteedStreamable"/> rule (§19.8.6) and the
/// <see cref="StreamabilityAnnotation"/> side-table. Purely additive — no engine behaviour
/// is exercised or changed here.
/// </summary>
public class PostureCompositionTests
{
    [Fact]
    public void Grounded_Motionless_IsGuaranteedStreamable()
    {
        new PostureSweep(Posture.Grounded, Sweep.Motionless).IsGuaranteedStreamable.Should().BeTrue();
    }

    [Fact]
    public void Striding_Consuming_IsGuaranteedStreamable()
    {
        new PostureSweep(Posture.Striding, Sweep.Consuming).IsGuaranteedStreamable.Should().BeTrue();
    }

    [Fact]
    public void Climbing_Motionless_IsGuaranteedStreamable()
    {
        new PostureSweep(Posture.Climbing, Sweep.Motionless).IsGuaranteedStreamable.Should().BeTrue();
    }

    [Fact]
    public void Crawling_Consuming_IsGuaranteedStreamable()
    {
        new PostureSweep(Posture.Crawling, Sweep.Consuming).IsGuaranteedStreamable.Should().BeTrue();
    }

    [Fact]
    public void Roaming_Consuming_IsNotGuaranteedStreamable()
    {
        new PostureSweep(Posture.Roaming, Sweep.Consuming).IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Grounded_FreeRanging_IsNotGuaranteedStreamable()
    {
        new PostureSweep(Posture.Grounded, Sweep.FreeRanging).IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Artistic_Motionless_IsNotGuaranteedStreamable()
    {
        new PostureSweep(Posture.Artistic, Sweep.Motionless).IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Annotation_RoundTrips_SetThenTryGet()
    {
        var node = new object();
        var ps = new PostureSweep(Posture.Striding, Sweep.Consuming);

        StreamabilityAnnotation.Set(node, ps);

        StreamabilityAnnotation.TryGet(node, out var got).Should().BeTrue();
        got.Should().Be(ps);
    }

    [Fact]
    public void Annotation_TryGet_OnUnannotatedNode_ReturnsFalse()
    {
        var node = new object();

        StreamabilityAnnotation.TryGet(node, out _).Should().BeFalse();
    }

    // ---------------------------------------------------------------------------
    // Task 0.3: compositional classifier truth-table (XSLT 3.0 §19.8).
    // Context = a streamed source-document body / matched streaming template, whose
    // context item posture is Striding with InStreamedScope=true — the classic
    // streaming entry posture per §19.8.2 ("the initial posture of the context item").
    // ---------------------------------------------------------------------------

    private static readonly StreamingContext Streamed = new(Posture.Striding, InStreamedScope: true);
    private static readonly StreamingContext GroundedCtx = new(Posture.Grounded, InStreamedScope: true);

    // ---- Small AST factory helpers -----------------------------------------

    private static NameTest Name(string local) => new() { LocalName = local, NamespaceUri = null };

    private static StepExpression Step(Axis axis, string local, params XQueryExpression[] preds) => new()
    {
        Axis = axis,
        NodeTest = Name(local),
        Predicates = preds,
    };

    private static PathExpression RelPath(params StepExpression[] steps) => new()
    {
        IsAbsolute = false,
        InitialExpression = null,
        Steps = steps,
    };

    private static FunctionCallExpression Fn(string local, params XQueryExpression[] args) => new()
    {
        Name = new QName(NamespaceId.None, local),
        Arguments = args,
    };

    private static readonly ContextItemExpression Dot = ContextItemExpression.Instance;

    // ---- Row 1: grounded literals / grounded variable -----------------------

    [Fact]
    public void Row01_IntegerLiteral_IsGroundedMotionless()
    {
        StreamabilityClassifier.Classify(new IntegerLiteral { Value = 42L }, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Motionless));
    }

    [Fact]
    public void Row01_StringLiteral_IsGroundedMotionless()
    {
        StreamabilityClassifier.Classify(new StringLiteral { Value = "x" }, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Motionless));
    }

    [Fact]
    public void Row01_VariableInGroundedContext_IsGroundedMotionless()
    {
        // $g resolved against a grounded context — no streamed nodes.
        var g = new VariableReference { Name = new QName(NamespaceId.None, "g") };
        StreamabilityClassifier.Classify(g, GroundedCtx)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Motionless));
    }

    // ---- Row 2: context item (.) -------------------------------------------

    [Fact]
    public void Row02_ContextItem_TakesContextPosture_Motionless()
    {
        StreamabilityClassifier.Classify(Dot, Streamed)
            .Should().Be(new PostureSweep(Posture.Striding, Sweep.Motionless));
    }

    // ---- Row 3: child::PRICE / PRICE ---------------------------------------

    [Fact]
    public void Row03_ChildStep_IsStridingConsuming()
    {
        StreamabilityClassifier.Classify(RelPath(Step(Axis.Child, "PRICE")), Streamed)
            .Should().Be(new PostureSweep(Posture.Striding, Sweep.Consuming));
    }

    // ---- Row 4: PRICE/child::X (two downward steps) -------------------------

    [Fact]
    public void Row04_TwoChildSteps_IsStridingConsuming()
    {
        StreamabilityClassifier.Classify(
                RelPath(Step(Axis.Child, "PRICE"), Step(Axis.Child, "X")), Streamed)
            .Should().Be(new PostureSweep(Posture.Striding, Sweep.Consuming));
    }

    // ---- Row 5: descendant::A / .//A ---------------------------------------

    [Fact]
    public void Row05_DescendantStep_IsCrawlingConsuming()
    {
        StreamabilityClassifier.Classify(RelPath(Step(Axis.Descendant, "A")), Streamed)
            .Should().Be(new PostureSweep(Posture.Crawling, Sweep.Consuming));
    }

    // ---- Row 6: @code / attribute::code ------------------------------------

    [Fact]
    public void Row06_AttributeStep_IsClimbingMotionless()
    {
        StreamabilityClassifier.Classify(RelPath(Step(Axis.Attribute, "code")), Streamed)
            .Should().Be(new PostureSweep(Posture.Climbing, Sweep.Motionless));
    }

    // ---- Row 7: parent / .. / ancestor -------------------------------------

    [Fact]
    public void Row07_ParentStep_IsClimbingMotionless()
    {
        StreamabilityClassifier.Classify(RelPath(Step(Axis.Parent, "X")), Streamed)
            .Should().Be(new PostureSweep(Posture.Climbing, Sweep.Motionless));
    }

    [Fact]
    public void Row07_AncestorStep_IsClimbingMotionless()
    {
        StreamabilityClassifier.Classify(RelPath(Step(Axis.Ancestor, "X")), Streamed)
            .Should().Be(new PostureSweep(Posture.Climbing, Sweep.Motionless));
    }

    // ---- Row 8: node-property functions ------------------------------------

    [Theory]
    [InlineData("name")]
    [InlineData("local-name")]
    [InlineData("node-name")]
    [InlineData("namespace-uri")]
    public void Row08_NodePropertyFunction_IsGroundedMotionless(string fn)
    {
        StreamabilityClassifier.Classify(Fn(fn, Dot), Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Motionless));
    }

    // ---- Row 9: string-value / atomization functions -----------------------

    [Theory]
    [InlineData("string")]
    [InlineData("data")]
    [InlineData("normalize-space")]
    public void Row09_StringValueFunction_IsGroundedConsuming(string fn)
    {
        StreamabilityClassifier.Classify(Fn(fn, Dot), Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    // ---- Row 10: child::A[@x='1'] (motionless predicate) -------------------

    [Fact]
    public void Row10_ChildWithMotionlessPredicate_IsStridingConsuming()
    {
        var pred = new BinaryExpression
        {
            Left = RelPath(Step(Axis.Attribute, "x")),
            Operator = BinaryOperator.GeneralEqual,
            Right = new StringLiteral { Value = "1" },
        };
        StreamabilityClassifier.Classify(RelPath(Step(Axis.Child, "A", pred)), Streamed)
            .Should().Be(new PostureSweep(Posture.Striding, Sweep.Consuming));
    }

    // ---- Row 11: union of two striding → crawling (§19.8.8.3) --------------

    [Fact]
    public void Row11_UnionOfStriding_IsCrawlingConsuming()
    {
        var union = new BinaryExpression
        {
            Left = RelPath(Step(Axis.Child, "A")),
            Operator = BinaryOperator.Union,
            Right = RelPath(Step(Axis.Child, "B")),
        };
        StreamabilityClassifier.Classify(union, Streamed)
            .Should().Be(new PostureSweep(Posture.Crawling, Sweep.Consuming));
    }

    // ---- Row 12: transmission functions preserve operand posture ----------

    [Fact]
    public void Row12_Head_PreservesStridingConsuming()
    {
        StreamabilityClassifier.Classify(Fn("head", RelPath(Step(Axis.Child, "A"))), Streamed)
            .Should().Be(new PostureSweep(Posture.Striding, Sweep.Consuming));
    }

    [Fact]
    public void Row12_Subsequence_PreservesStridingConsuming()
    {
        StreamabilityClassifier.Classify(
                Fn("subsequence", RelPath(Step(Axis.Child, "A")), new IntegerLiteral { Value = 2L }),
                Streamed)
            .Should().Be(new PostureSweep(Posture.Striding, Sweep.Consuming));
    }

    // ---- Row 13: for $x in child::A/string() return $x + 1 ----------------

    [Fact]
    public void Row13_ForWithGroundedBinding_GroundedBody_IsGroundedConsuming()
    {
        // for $x in (child::A ! string()) return $x + 1
        var inExpr = new SimpleMapExpression
        {
            Left = RelPath(Step(Axis.Child, "A")),
            Right = Fn("string", Dot),
        };
        var body = new BinaryExpression
        {
            Left = new VariableReference { Name = new QName(NamespaceId.None, "x") },
            Operator = BinaryOperator.Add,
            Right = new IntegerLiteral { Value = 1L },
        };
        var flwor = new FlworExpression
        {
            Clauses = new FlworClause[]
            {
                new ForClause
                {
                    Bindings = new[]
                    {
                        new ForBinding
                        {
                            Variable = new QName(NamespaceId.None, "x"),
                            Expression = inExpr,
                        },
                    },
                },
            },
            ReturnExpression = body,
        };
        StreamabilityClassifier.Classify(flwor, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    // ---- Row 14: general comparison → boolean, operand consuming ----------

    [Fact]
    public void Row14_GeneralComparison_IsGroundedConsuming()
    {
        var cmp = new BinaryExpression
        {
            Left = new DecimalLiteral { Value = 4.32m },
            Operator = BinaryOperator.GeneralEqual,
            Right = RelPath(Step(Axis.Child, "A"), Step(Axis.Attribute, "v")),
        };
        StreamabilityClassifier.Classify(cmp, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    // ---- Row 15: absorbing aggregate → atomic -----------------------------

    [Theory]
    [InlineData("count")]
    [InlineData("sum")]
    public void Row15_Aggregate_IsGroundedConsuming(string fn)
    {
        StreamabilityClassifier.Classify(Fn(fn, RelPath(Step(Axis.Child, "A"))), Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    // ---- Row 16: simple-map per-item grounding ----------------------------

    [Fact]
    public void Row16_SimpleMapToString_IsGroundedConsuming()
    {
        var sm = new SimpleMapExpression
        {
            Left = RelPath(Step(Axis.Child, "A")),
            Right = Fn("string", Dot),
        };
        StreamabilityClassifier.Classify(sm, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    // ---- Row 17: roaming axes ---------------------------------------------

    [Fact]
    public void Row17_FollowingSibling_IsRoamingFreeRanging()
    {
        StreamabilityClassifier.Classify(RelPath(Step(Axis.FollowingSibling, "X")), Streamed)
            .Should().Be(new PostureSweep(Posture.Roaming, Sweep.FreeRanging));
    }

    [Fact]
    public void Row17_Preceding_IsRoamingFreeRanging()
    {
        StreamabilityClassifier.Classify(RelPath(Step(Axis.Preceding, "X")), Streamed)
            .Should().Be(new PostureSweep(Posture.Roaming, Sweep.FreeRanging));
    }

    // ---- Row 18: unknown/unsupported node ---------------------------------

    [Fact]
    public void Row18_UnsupportedNode_IsRoamingFreeRanging()
    {
        // A map constructor is not modelled by the classifier yet → conservative.
        var map = new MapConstructor { Entries = System.Array.Empty<MapEntry>() };
        StreamabilityClassifier.Classify(map, Streamed)
            .Should().Be(new PostureSweep(Posture.Roaming, Sweep.FreeRanging));
    }

    // ---- Grounded-context rows --------------------------------------------

    [Fact]
    public void GroundedContext_ChildStep_IsGroundedConsuming()
    {
        // A path from a grounded context selects grounded nodes; the child navigation
        // still consumes the (grounded) operand's subtree (§19.8.2 posture propagation:
        // a grounded context yields a grounded result).
        StreamabilityClassifier.Classify(RelPath(Step(Axis.Child, "PRICE")), GroundedCtx)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    [Fact]
    public void GroundedContext_ContextItem_IsGroundedMotionless()
    {
        StreamabilityClassifier.Classify(Dot, GroundedCtx)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Motionless));
    }
}
