using FluentAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
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

    private static PathExpression AbsPath(params StepExpression[] steps) => new()
    {
        IsAbsolute = true,
        InitialExpression = null,
        Steps = steps,
    };

    private static PathExpression PathFrom(XQueryExpression init, params StepExpression[] steps) => new()
    {
        IsAbsolute = false,
        InitialExpression = init,
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
        // A dynamic map/array lookup (?key) is not modelled by the classifier yet → conservative.
        // (MapConstructor / ArrayConstructor are now modelled — see Task B rows below.)
        var lookup = new LookupExpression
        {
            Base = new VariableReference { Name = new QName(NamespaceId.None, "m") },
            Key = new StringLiteral { Value = "k" },
        };
        StreamabilityClassifier.Classify(lookup, Streamed)
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

    // =======================================================================
    // Task 0.4: XSLT INSTRUCTION truth-table (§19.8).
    // Same streamed entry context (Striding, InStreamedScope=true) unless noted.
    // Instruction ASTs are built directly (no parser dependency).
    // =======================================================================

    // ---- Instruction AST factory helpers -----------------------------------

    private static XsltSequenceConstructor Body(params XsltInstruction[] instructions) => new()
    {
        Instructions = instructions,
    };

    private static XsltAttributeValueTemplate Avt(params AvtPart[] parts) => new() { Parts = parts };

    private static AvtExpression AvtExpr(XQueryExpression e) => new() { Expression = e };

    private static readonly QName OutName = new(NamespaceId.None, "out");

    // ---- Ins 1: THE 013 SHAPE (headline) -----------------------------------
    // xsl:copy › xsl:iterate select=child::* › xsl:copy-of select=.
    //   iterate select child::* → (Striding, Consuming)
    //   body copy-of select=. → transmits per-item Striding, Motionless
    //   iterate → (Striding, Consuming)
    //   xsl:copy construction with consuming content → (Grounded, Consuming), streamable.

    [Fact]
    public void Ins01_The013Shape_CopyIterateCopyOf_IsGroundedConsuming_AndStreamable()
    {
        var copyOfDot = new XsltCopyOf { Select = Dot };
        var iterate = new XsltIterate
        {
            Select = RelPath(Step(Axis.Child, "*")),
            Body = Body(copyOfDot),
        };
        var copy = new XsltCopy { Content = Body(iterate) };

        var ps = StreamabilityClassifier.Classify(copy, Streamed);

        ps.Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
        ps.IsGuaranteedStreamable.Should().BeTrue();
    }

    // ---- Ins 2: LRE with a consuming attribute AVT -------------------------

    [Fact]
    public void Ins02_LreWithConsumingAvt_IsGroundedConsuming_AndStreamable()
    {
        // <out code="{count(//*)}"/> — count(//*) is grounded+consuming; the LRE stays grounded.
        var countAll = Fn("count", RelPath(Step(Axis.DescendantOrSelf, "*")));
        var lre = new XsltLiteralResultElement
        {
            Name = OutName,
            Attributes = new Dictionary<QName, XsltAttributeValueTemplate>
            {
                [new QName(NamespaceId.None, "code")] = Avt(AvtExpr(countAll)),
            },
            Content = Body(),
        };

        var ps = StreamabilityClassifier.Classify(lre, Streamed);

        ps.Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
        ps.IsGuaranteedStreamable.Should().BeTrue();
    }

    // ---- Ins 3: xsl:value-of select=sum(child::PRICE) ----------------------

    [Fact]
    public void Ins03_ValueOfSum_IsGroundedConsuming_AndStreamable()
    {
        var vo = new XsltValueOf { Select = Fn("sum", RelPath(Step(Axis.Child, "PRICE"))) };

        var ps = StreamabilityClassifier.Classify(vo, Streamed);

        ps.Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
        ps.IsGuaranteedStreamable.Should().BeTrue();
    }

    // ---- Ins 4: xsl:for-each select=child::ITEM body=[value-of child::PRICE] -

    [Fact]
    public void Ins04_ForEachWithValueOfBody_IsStreamable_Consuming()
    {
        var forEach = new XsltForEach
        {
            Select = RelPath(Step(Axis.Child, "ITEM")),
            Body = Body(new XsltValueOf { Select = RelPath(Step(Axis.Child, "PRICE")) }),
        };

        var ps = StreamabilityClassifier.Classify(forEach, Streamed);

        // body is value-of ⇒ grounded; posture grounded, sweep consuming.
        ps.Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
        ps.IsGuaranteedStreamable.Should().BeTrue();
    }

    // ---- Ins 5: xsl:for-each-group group-by → NOT streamable ---------------

    [Fact]
    public void Ins05_ForEachGroup_GroupBy_IsRoamingFreeRanging_NotStreamable()
    {
        var feg = new XsltForEachGroup
        {
            Select = RelPath(Step(Axis.Child, "X")),
            GroupBy = RelPath(Step(Axis.Attribute, "k")),
            Body = Body(),
        };

        var ps = StreamabilityClassifier.Classify(feg, Streamed);

        ps.Should().Be(new PostureSweep(Posture.Roaming, Sweep.FreeRanging));
        ps.IsGuaranteedStreamable.Should().BeFalse();
    }

    // ---- Ins 6: group-adjacent + copy-of(current-group()) → streamable -----

    [Fact]
    public void Ins06_ForEachGroup_GroupAdjacent_CurrentGroupCopy_IsStreamable_Consuming()
    {
        var feg = new XsltForEachGroup
        {
            Select = RelPath(Step(Axis.Child, "X")),
            GroupAdjacent = RelPath(Step(Axis.Attribute, "k")),
            Body = Body(new XsltCopyOf { Select = Fn("current-group") }),
        };

        var ps = StreamabilityClassifier.Classify(feg, Streamed);

        ps.Sweep.Should().Be(Sweep.Consuming);
        ps.IsGuaranteedStreamable.Should().BeTrue();
    }

    // ---- Ins 7: xsl:apply-templates select=child::account ------------------

    [Fact]
    public void Ins07_ApplyTemplatesChildSelect_IsStridingConsuming_AndStreamable()
    {
        var ap = new XsltApplyTemplates { Select = RelPath(Step(Axis.Child, "account")) };

        var ps = StreamabilityClassifier.Classify(ap, Streamed);

        ps.Should().Be(new PostureSweep(Posture.Striding, Sweep.Consuming));
        ps.IsGuaranteedStreamable.Should().BeTrue();
    }

    // ---- Ins 8: xsl:iterate with grounded param + on-completion ------------

    [Fact]
    public void Ins08_IterateWithGroundedParamAndOnCompletion_IsStreamable()
    {
        // <xsl:iterate select="child::*">
        //   <xsl:param name="m" select="map{}"/>   (grounded accumulator)
        //   <xsl:value-of select="."/>              (grounded body)
        //   <xsl:on-completion><xsl:sequence select="$m"/></xsl:on-completion>
        // </xsl:iterate>
        var iterate = new XsltIterate
        {
            Select = RelPath(Step(Axis.Child, "*")),
            Params = new List<XsltParam>(),
            Body = Body(new XsltValueOf { Select = Dot }),
            OnCompletion = Body(new XsltSequence
            {
                Select = new VariableReference { Name = new QName(NamespaceId.None, "m") },
            }),
        };

        var ps = StreamabilityClassifier.Classify(iterate, Streamed);

        ps.IsGuaranteedStreamable.Should().BeTrue();
        ps.Sweep.Should().Be(Sweep.Consuming);
    }

    // ---- Ins 9: xsl:copy-of select=snapshot(child::A) ----------------------

    [Fact]
    public void Ins09_CopyOfSnapshot_IsGroundedConsuming_AndStreamable()
    {
        var co = new XsltCopyOf { Select = Fn("snapshot", RelPath(Step(Axis.Child, "A"))) };

        var ps = StreamabilityClassifier.Classify(co, Streamed);

        ps.Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
        ps.IsGuaranteedStreamable.Should().BeTrue();
    }

    // ---- Ins 10: xsl:if test=exists(child::A) then=[<x/>] ------------------

    [Fact]
    public void Ins10_IfExistsThenConstruct_IsStreamable()
    {
        var iff = new XsltIf
        {
            Test = Fn("exists", RelPath(Step(Axis.Child, "A"))),
            Then = Body(new XsltLiteralResultElement
            {
                Name = new QName(NamespaceId.None, "x"),
                Content = Body(),
            }),
        };

        var ps = StreamabilityClassifier.Classify(iff, Streamed);

        ps.IsGuaranteedStreamable.Should().BeTrue();
        ps.Posture.Should().Be(Posture.Grounded);
        ps.Sweep.Should().Be(Sweep.Consuming);
    }

    // ---- Ins 11: mixed body [<hdr/>, for-each(child::A){...}] --------------

    [Fact]
    public void Ins11_MixedGroundedAndStreamedBody_IsStreamable_Consuming()
    {
        var hdr = new XsltLiteralResultElement
        {
            Name = new QName(NamespaceId.None, "hdr"),
            Content = Body(),
        };
        var loop = new XsltForEach
        {
            Select = RelPath(Step(Axis.Child, "A")),
            Body = Body(new XsltValueOf { Select = Dot }),
        };
        var body = Body(hdr, loop);

        var ps = StreamabilityClassifier.Classify(body, Streamed);

        ps.IsGuaranteedStreamable.Should().BeTrue();
        ps.Sweep.Should().Be(Sweep.Consuming);
    }

    // ---- Ins 12: unknown instruction → (Roaming, FreeRanging) --------------

    [Fact]
    public void Ins12_UnknownInstruction_IsRoamingFreeRanging_NotStreamable()
    {
        // xsl:merge is not modelled by the instruction classifier yet → conservative.
        var merge = new XsltMerge { Action = Body() };

        var ps = StreamabilityClassifier.Classify(merge, Streamed);

        ps.Should().Be(new PostureSweep(Posture.Roaming, Sweep.FreeRanging));
        ps.IsGuaranteedStreamable.Should().BeFalse();
    }

    // =======================================================================
    // Task 0.6: NEGATIVE truth-table — the 5 over-accept defect families the
    // §19.8 classifier must REJECT (the W3C oracle expects XTSE3430). Each is
    // paired with an adjacent POSITIVE shape already pinned above that MUST
    // stay streamable, so the fix is a genuine tightening, not a blanket reject.
    // =======================================================================

    // ---- Family 1: consuming / positional predicate on a striding sequence -

    [Fact]
    public void Neg1_AggregateOverConsumingPredicateFilter_IsNotStreamable()
    {
        // count(child::ITEM[child::AUTHOR = 'X']) — the predicate navigates the candidate's
        // children (consuming) ⇒ the filtered striding sequence roams ⇒ count is not
        // streamable (sf-count-901, sf-boolean-901, sx-treat-901, …).
        var authorEq = new BinaryExpression
        {
            Left = RelPath(Step(Axis.Child, "AUTHOR")),
            Operator = BinaryOperator.GeneralEqual,
            Right = new StringLiteral { Value = "X" },
        };
        var filtered = RelPath(Step(Axis.Child, "ITEM", authorEq));

        StreamabilityClassifier.Classify(Fn("count", filtered), Streamed)
            .IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Neg1_ElementValuePredicate_IsNotStreamable()
    {
        // child::PAGES[. lt 1000] — atomizing the striding element context (.) walks its
        // subtree (consuming) ⇒ roaming (sx-gc-*-902).
        var pred = new BinaryExpression
        {
            Left = Dot,
            Operator = BinaryOperator.LessThan,
            Right = new IntegerLiteral { Value = 1000L },
        };
        StreamabilityClassifier.Classify(RelPath(Step(Axis.Child, "PAGES", pred)), Streamed)
            .IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Neg1_LastInPredicate_IsNotStreamable()
    {
        // child::ITEM[position() ne last()] — last() needs look-ahead ⇒ free-ranging
        // (sx-gc-eq-901).
        var pred = new BinaryExpression
        {
            Left = Fn("position"),
            Operator = BinaryOperator.NotEqual,
            Right = Fn("last"),
        };
        StreamabilityClassifier.Classify(RelPath(Step(Axis.Child, "ITEM", pred)), Streamed)
            .IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Neg1_MotionlessAttributePredicate_StaysStreamable()
    {
        // GUARD: sum(child::b/@v[. gt 0]) — the predicate is on an ATTRIBUTE (@v), whose
        // atomization (.) is motionless (an attribute has no subtree to walk), so this MUST
        // stay streamable (sf-*-019 attribute-predicate shape).
        var pred = new BinaryExpression
        {
            Left = Dot,
            Operator = BinaryOperator.GreaterThan,
            Right = new IntegerLiteral { Value = 0L },
        };
        var path = RelPath(Step(Axis.Child, "b"), Step(Axis.Attribute, "v", pred));
        StreamabilityClassifier.Classify(Fn("sum", path), Streamed)
            .IsGuaranteedStreamable.Should().BeTrue();
    }

    // ---- Family 2: reverse(path) → free-ranging (not transmission) ---------

    [Fact]
    public void Neg2_ReverseOfPath_IsNotStreamable()
    {
        // reverse(/chapter//section)/@id — reverse buffers the whole sequence ⇒ free-ranging
        // (sf-reverse-901). Keep head()/subsequence() (Row12) as posture-preserving.
        var reversed = Fn("reverse", AbsPath(Step(Axis.DescendantOrSelf, "section")));
        StreamabilityClassifier.Classify(PathFrom(reversed, Step(Axis.Attribute, "id")), Streamed)
            .IsGuaranteedStreamable.Should().BeFalse();
    }

    // ---- Family 3: union/comma of node sequences, then navigated → roaming -

    [Fact]
    public void Neg3_UnionThenStep_IsNotStreamable()
    {
        // ($v | //ITEM)/PRICE — union then a child step ⇒ not guaranteed streamable
        // (sx-union-201/202, sx-comma-201).
        var union = new BinaryExpression
        {
            Left = new VariableReference { Name = new QName(NamespaceId.None, "v") },
            Operator = BinaryOperator.Union,
            Right = AbsPath(Step(Axis.DescendantOrSelf, "ITEM")),
        };
        StreamabilityClassifier.Classify(PathFrom(union, Step(Axis.Child, "PRICE")), Streamed)
            .IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Neg3_CommaThenStep_IsNotStreamable()
    {
        // (//A, //B)/C — comma-sequence of two node sets then a step ⇒ roaming (sx-comma-201).
        var comma = new SequenceExpression
        {
            Items = new XQueryExpression[]
            {
                AbsPath(Step(Axis.DescendantOrSelf, "A")),
                AbsPath(Step(Axis.DescendantOrSelf, "B")),
            },
        };
        StreamabilityClassifier.Classify(PathFrom(comma, Step(Axis.Child, "C")), Streamed)
            .IsGuaranteedStreamable.Should().BeFalse();
    }

    // ---- Family 4: use-attribute-sets construction → conservative reject ---

    [Fact]
    public void Neg4_LreWithUseAttributeSets_IsNotStreamable()
    {
        // <e xsl:use-attribute-sets="as-2"/> — attribute-set streamability is unmodelled;
        // conservative reject (si-lre-901/902, si-element-901/902, si-copy-901/902).
        var lre = new XsltLiteralResultElement
        {
            Name = new QName(NamespaceId.None, "e"),
            UseAttributeSets = { new QName(NamespaceId.None, "as-2") },
            Content = Body(),
        };
        StreamabilityClassifier.Classify(lre, Streamed).IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Neg4_ElementWithUseAttributeSets_IsNotStreamable()
    {
        var el = new XsltElement
        {
            Name = Avt(new AvtLiteral { Value = "e" }),
            UseAttributeSets = { new QName(NamespaceId.None, "as-2") },
            Content = Body(),
        };
        StreamabilityClassifier.Classify(el, Streamed).IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Neg4_CopyWithUseAttributeSets_IsNotStreamable()
    {
        var cp = new XsltCopy
        {
            Select = RelPath(Step(Axis.Child, "*")),
            UseAttributeSets = { new QName(NamespaceId.None, "as-2") },
            Content = Body(),
        };
        StreamabilityClassifier.Classify(cp, Streamed).IsGuaranteedStreamable.Should().BeFalse();
    }

    // ---- Family 5: crawling for-each select / multi-downward / sequence-. --

    [Fact]
    public void Neg5_ForEachCrawlingSelect_IsNotStreamable()
    {
        // <xsl:for-each select="//ITEM/TITLE"> — the population select is crawling ⇒ not
        // streamable in a loop (si-for-each-806).
        var forEach = new XsltForEach
        {
            Select = AbsPath(Step(Axis.DescendantOrSelf, "ITEM"), Step(Axis.Child, "TITLE")),
            Body = Body(new XsltValueOf { Select = Dot }),
        };
        StreamabilityClassifier.Classify(forEach, Streamed).IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Neg5_ForEachMultipleDownwardSelections_IsNotStreamable()
    {
        // body = <xsl:value-of select="count(*) + count(*/*)"/> — two independent downward
        // selections of the same input ⇒ free-ranging (si-for-each-905, si-iterate-905).
        var countStar = Fn("count", RelPath(Step(Axis.Child, "*")));
        var countStarStar = Fn("count", RelPath(Step(Axis.Child, "*"), Step(Axis.Child, "*")));
        var sum = new BinaryExpression
        {
            Left = countStar,
            Operator = BinaryOperator.Add,
            Right = countStarStar,
        };
        var forEach = new XsltForEach
        {
            Select = RelPath(Step(Axis.Child, "node")),
            Body = Body(new XsltValueOf { Select = sum }),
        };
        StreamabilityClassifier.Classify(forEach, Streamed).IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Neg5_ForEachSequenceOfContextItem_IsNotStreamable()
    {
        // body = <xsl:sequence select="."/> RETURNS the striding streamed node (not grounded)
        // ⇒ not streamable; contrast the 013-shape which uses xsl:copy-of (grounds the copy)
        // (si-for-each-907, si-iterate-907).
        var forEach = new XsltForEach
        {
            Select = RelPath(Step(Axis.Child, "transaction")),
            Body = Body(new XsltSequence { Select = Dot }),
        };
        StreamabilityClassifier.Classify(forEach, Streamed).IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void Neg5_ForEachSequenceOfContextItem_ContrastCopyOfStaysStreamable()
    {
        // GUARD: the same loop but with xsl:copy-of (grounds the copy) MUST stay streamable —
        // the 013-shape discriminator.
        var forEach = new XsltForEach
        {
            Select = RelPath(Step(Axis.Child, "transaction")),
            Body = Body(new XsltCopyOf { Select = Dot }),
        };
        StreamabilityClassifier.Classify(forEach, Streamed).IsGuaranteedStreamable.Should().BeTrue();
    }

    // =======================================================================
    // Phase 1.5 Task A: function-role table completion (§19.8 / fn spec
    // streamability categories). These pin the roles added so the classifier
    // stops CONSERVATIVELY over-rejecting streamable bodies whose only "sin"
    // was containing an unroled standard function. Same streamed entry context.
    // =======================================================================

    private static FunctionCallExpression XsFn(string local, params XQueryExpression[] args) => new()
    {
        Name = new QName(NamespaceId.Xsd, local, "xs"),
        Arguments = args,
    };

    // ---- A1: tokenize is grounded-atomic-sequence producing (consuming) -----

    [Fact]
    public void A01_TokenizeOfStringChild_IsGroundedConsuming()
    {
        // tokenize(string(child::A), ' ')
        var call = Fn("tokenize", Fn("string", RelPath(Step(Axis.Child, "A"))),
            new StringLiteral { Value = " " });
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    [Fact]
    public void A01_TokenizeInSimpleMap_IsGroundedConsuming_TheSiIterate037Core()
    {
        // //text() ! tokenize(., '\s+') — the si-iterate-037 core.
        var sm = new SimpleMapExpression
        {
            Left = AbsPath(Step(Axis.DescendantOrSelf, "text()")),
            Right = Fn("tokenize", Dot, new StringLiteral { Value = "\\s+" }),
        };
        StreamabilityClassifier.Classify(sm, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    // ---- A2: outermost / innermost stay CONSERVATIVE over a streamed operand -
    // The §19.8 anti-goal (posture preservation) is DEFERRED: the W3C streaming corpus
    // requires XTSE3430 for innermost()/outermost() over a non-grounded operand
    // (sf-innermost-901), so modelling them as posture-preserving transmissions OVER-ACCEPTS
    // (the shadow over-accept pin caught it). They remain rejecting (Roaming, FreeRanging)
    // until a grounded-operand-only refinement lands in a later phase.

    [Fact]
    public void A02_OutermostOfCrawling_StaysConservative_NotStreamable()
    {
        var call = Fn("outermost", AbsPath(Step(Axis.DescendantOrSelf, "PRICE")));
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Roaming, Sweep.FreeRanging));
    }

    [Fact]
    public void A02_InnermostOfStriding_StaysConservative_NotStreamable()
    {
        var call = Fn("innermost", RelPath(Step(Axis.Child, "PRICE")));
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Roaming, Sweep.FreeRanging));
    }

    // ---- A3: cardinality / identity transmissions preserve posture ----------

    [Fact]
    public void A03_OneOrMoreChild_PreservesStridingConsuming()
    {
        var call = Fn("one-or-more", RelPath(Step(Axis.Child, "A")));
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Striding, Sweep.Consuming));
    }

    [Theory]
    [InlineData("zero-or-one")]
    [InlineData("exactly-one")]
    [InlineData("unordered")]
    [InlineData("trace")]
    public void A03_TransmissionPreservesStridingConsuming(string fn)
    {
        var call = Fn(fn, RelPath(Step(Axis.Child, "A")));
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Striding, Sweep.Consuming));
    }

    // ---- A4: atomizing / numeric / string functions → grounded --------------

    [Fact]
    public void A04_NumberOfChild_IsGroundedConsuming()
    {
        // number(child::PRICE) — atomizes a striding element operand ⇒ consuming.
        var call = Fn("number", RelPath(Step(Axis.Child, "PRICE")));
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    [Theory]
    [InlineData("abs")]
    [InlineData("round")]
    [InlineData("floor")]
    [InlineData("ceiling")]
    [InlineData("string-length")]
    [InlineData("normalize-unicode")]
    [InlineData("translate")]
    [InlineData("encode-for-uri")]
    public void A04_AtomizingUnaryFunctionOfChild_IsGroundedConsuming(string fn)
    {
        var call = Fn(fn, RelPath(Step(Axis.Child, "PRICE")));
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    [Fact]
    public void A04_NumberOfAttribute_IsGroundedMotionless()
    {
        // number(@v) — atomizing a climbing attribute of the context node is motionless (an
        // attribute has no subtree to walk, and reaching it advanced nothing). Contrast
        // number(child::A/@v), which is consuming because the child::A step itself consumes.
        var call = Fn("number", RelPath(Step(Axis.Attribute, "v")));
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Motionless));
    }

    // ---- A5: boolean / aggregate predicate functions → grounded -------------

    [Fact]
    public void A05_ContainsOfString_IsGroundedConsuming()
    {
        // contains(string(.), 'x') — string(.) atomizes striding . ⇒ consuming.
        var call = Fn("contains", Fn("string", Dot), new StringLiteral { Value = "x" });
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    [Theory]
    [InlineData("starts-with")]
    [InlineData("ends-with")]
    [InlineData("matches")]
    [InlineData("substring-before")]
    [InlineData("substring-after")]
    public void A05_StringPredicateOfChild_IsGroundedConsuming(string fn)
    {
        var call = Fn(fn, RelPath(Step(Axis.Child, "A")), new StringLiteral { Value = "x" });
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    [Fact]
    public void A05_HasChildrenOfChild_IsGroundedConsuming()
    {
        var call = Fn("has-children", RelPath(Step(Axis.Child, "A")));
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    // ---- A6: xs:* type-constructor calls route through cast logic ------------

    [Fact]
    public void A06_XsDecimalOfAttribute_IsGroundedMotionless()
    {
        // xs:decimal(@v) — climbing attribute operand atomizes motionless (routes through the
        // same cast logic as a CastExpression).
        var call = XsFn("decimal", RelPath(Step(Axis.Attribute, "v")));
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Motionless));
    }

    [Fact]
    public void A06_XsIntegerOfChild_IsGroundedConsuming()
    {
        // xs:integer(child::PRICE) — element operand atomizes consuming.
        var call = XsFn("integer", RelPath(Step(Axis.Child, "PRICE")));
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    [Fact]
    public void A06_XsNmtokensOfChild_IsGroundedConsuming()
    {
        // xs:NMTOKENS (a list type that parses to a constructor call, not a CastExpression) —
        // still routes through cast logic.
        var call = XsFn("NMTOKENS", RelPath(Step(Axis.Child, "PRICE")));
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    // ---- A7: distinct-values remains grounded-consuming ---------------------

    [Fact]
    public void A07_DistinctValuesOfChild_IsGroundedConsuming()
    {
        var call = Fn("distinct-values", RelPath(Step(Axis.Child, "A")));
        StreamabilityClassifier.Classify(call, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    // =======================================================================
    // Phase 1.5 Task B: expression-level if / map-constructor / array-constructor
    // classification (§19.8, §19.8.2). Mirrors the XsltIf/XsltChoose, XsltMap and
    // XsltArray instruction arms so the expression side stops conservatively
    // over-rejecting them. Same streamed entry context (Striding, InStreamedScope).
    // =======================================================================

    private static IfExpression If(XQueryExpression cond, XQueryExpression then, XQueryExpression? els) =>
        new() { Condition = cond, Then = then, Else = els };

    private static MapEntry Entry(XQueryExpression key, XQueryExpression value) =>
        new() { Key = key, Value = value };

    private static MapConstructor Map(params MapEntry[] entries) => new() { Entries = entries };

    private static ArrayConstructor SquareArray(params XQueryExpression[] members) =>
        new() { Kind = ArrayConstructorKind.Square, Members = members };

    private static ArrayConstructor CurlyArray(params XQueryExpression[] members) =>
        new() { Kind = ArrayConstructorKind.Curly, Members = members };

    // ---- B1: if with two streamable node branches → widened & streamable ----

    [Fact]
    public void B01_IfExistsThenChildElseChild_IsStreamable()
    {
        // if (exists(child::A)) then child::A else child::B
        // widen(Striding, Striding) = Crawling; sweep = Consuming ⇒ streamable.
        var iff = If(
            Fn("exists", RelPath(Step(Axis.Child, "A"))),
            RelPath(Step(Axis.Child, "A")),
            RelPath(Step(Axis.Child, "B")));

        var ps = StreamabilityClassifier.Classify(iff, Streamed);

        ps.Sweep.Should().Be(Sweep.Consuming);
        ps.IsGuaranteedStreamable.Should().BeTrue();
    }

    // ---- B2: if then=child::A else=grounded → (Grounded, Consuming) ---------

    [Fact]
    public void B02_IfThenChildElseGrounded_IsGroundedConsuming()
    {
        // if (child::A) then string(.) else 0 — string(.) is grounded+consuming, 0 grounded;
        // widen(Grounded, Grounded)=Grounded, sweep combines to Consuming.
        var iff = If(
            RelPath(Step(Axis.Child, "A")),
            Fn("string", Dot),
            new IntegerLiteral { Value = 0L });

        StreamabilityClassifier.Classify(iff, Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
    }

    // ---- B3: if with a roaming else → propagates (NOT streamable) ----------

    [Fact]
    public void B03_IfThenChildElseRoaming_IsNotStreamable()
    {
        // if (T) then child::A else following::X — the roaming else branch propagates.
        var iff = If(
            Fn("exists", RelPath(Step(Axis.Child, "A"))),
            RelPath(Step(Axis.Child, "A")),
            RelPath(Step(Axis.Following, "X")));

        StreamabilityClassifier.Classify(iff, Streamed)
            .IsGuaranteedStreamable.Should().BeFalse();
    }

    // ---- B4: braced-if (null else) → then branch + implicit empty else ------

    [Fact]
    public void B04_BracedIfNoElse_IsStreamable()
    {
        // if (exists(child::A)) { child::A } — implicit empty-sequence else (grounded).
        var iff = If(
            Fn("exists", RelPath(Step(Axis.Child, "A"))),
            RelPath(Step(Axis.Child, "A")),
            null);

        var ps = StreamabilityClassifier.Classify(iff, Streamed);

        ps.Should().Be(new PostureSweep(Posture.Striding, Sweep.Consuming));
        ps.IsGuaranteedStreamable.Should().BeTrue();
    }

    // ---- B5: constructed map is grounded, sweep combines over key/value -----

    [Fact]
    public void B05_MapWithConsumingValue_IsGroundedConsuming()
    {
        // map { 'k': string(child::A) } — value consumes; the constructed map is grounded.
        var map = Map(Entry(new StringLiteral { Value = "k" }, Fn("string", RelPath(Step(Axis.Child, "A")))));

        var ps = StreamabilityClassifier.Classify(map, Streamed);

        ps.Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
        ps.IsGuaranteedStreamable.Should().BeTrue();
    }

    // ---- B6: constructed array is grounded, sweep combines over members -----

    [Fact]
    public void B06_SquareArrayWithConsumingMembers_IsGroundedConsuming()
    {
        // [ child::A/string(), child::B/string() ] — members consume; the array is grounded.
        var arr = SquareArray(
            new SimpleMapExpression { Left = RelPath(Step(Axis.Child, "A")), Right = Fn("string", Dot) },
            new SimpleMapExpression { Left = RelPath(Step(Axis.Child, "B")), Right = Fn("string", Dot) });

        var ps = StreamabilityClassifier.Classify(arr, Streamed);

        ps.Should().Be(new PostureSweep(Posture.Grounded, Sweep.Consuming));
        ps.IsGuaranteedStreamable.Should().BeTrue();
    }

    // ---- B7: a free-ranging array member propagates via the sweep ----------

    [Fact]
    public void B07_CurlyArrayWithFreeRangingMember_IsNotStreamable()
    {
        // array { following::X } — the constructed array is grounded for POSTURE, but the
        // free-ranging member sweep propagates ⇒ NOT guaranteed streamable.
        var arr = CurlyArray(RelPath(Step(Axis.Following, "X")));

        StreamabilityClassifier.Classify(arr, Streamed)
            .IsGuaranteedStreamable.Should().BeFalse();
    }

    [Fact]
    public void B07_EmptyMap_IsGroundedMotionless()
    {
        StreamabilityClassifier.Classify(Map(), Streamed)
            .Should().Be(new PostureSweep(Posture.Grounded, Sweep.Motionless));
    }
}
