using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests.Streamability;

/// <summary>
/// Phase 2 (bucket B2) — the streaming absorbing/windowing functions must carry and
/// apply their positional/default arguments during the forward pass instead of
/// dropping them:
/// <list type="bullet">
///   <item><c>sum(seq, $zero)</c> — emit <c>$zero</c> when the stream is empty.</item>
///   <item><c>head/tail/subsequence/remove/insert-before(seq, …)</c> — the positional
///     window rides the watcher/subscription rather than being discarded.</item>
/// </list>
/// These tests exercise the scanner registration directly on the AST (the resolve-time
/// application is covered by the conformance oracle canaries).
/// </summary>
public class StreamingAbsorbingWindowTests
{
    private static NameTest Name(string local) => new() { LocalName = local, NamespaceUri = null };

    private static StepExpression Step(Axis axis, string local) => new()
    {
        Axis = axis,
        NodeTest = Name(local),
        Predicates = Array.Empty<XQueryExpression>(),
    };

    // /BOOKLIST/BOOKS/ITEM/PRICE — a striding, absolute, child-axis base path.
    private static PathExpression StridingPath() => new()
    {
        IsAbsolute = true,
        InitialExpression = null,
        Steps = new[]
        {
            Step(Axis.Child, "BOOKLIST"),
            Step(Axis.Child, "BOOKS"),
            Step(Axis.Child, "ITEM"),
            Step(Axis.Child, "PRICE"),
        },
    };

    private static FunctionCallExpression Fn(string local, params XQueryExpression[] args) => new()
    {
        Name = new QName(NamespaceId.None, local),
        Arguments = args,
    };

    private static IReadOnlyList<StreamWatcher> ScanValueOf(XQueryExpression select)
    {
        var body = new XsltSequenceConstructor
        {
            Instructions = new XsltInstruction[] { new XsltValueOf { Select = select } },
        };
        return new StreamingExpressionScanner().Scan(body);
    }

    private static IReadOnlyList<ForEachSubscription> ScanForEachSubs(XQueryExpression select)
    {
        var scanner = new StreamingExpressionScanner();
        var body = new XsltSequenceConstructor
        {
            Instructions = new XsltInstruction[]
            {
                new XsltForEach
                {
                    Select = select,
                    Body = new XsltSequenceConstructor
                    {
                        Instructions = new XsltInstruction[]
                        {
                            new XsltValueOf { Select = ContextItemExpression.Instance },
                        },
                    },
                },
            },
        };
        return scanner.ScanWithSubscriptions(body).Subscriptions;
    }

    // =======================================================================
    // sum(seq, $zero) — the default rides on the Sum watcher.
    // =======================================================================

    [Fact]
    public void Scanner_SumWithDefault_CarriesDefaultExpression()
    {
        // sum(/BOOKLIST/BOOKS/ITEM/PRICE, 42) — the 42 default must be carried so an
        // empty stream yields 42, not empty (sf-sum-011).
        var def = new IntegerLiteral { Value = 42 };
        var expr = Fn("sum", StridingPath(), def);

        var watchers = ScanValueOf(expr);

        watchers.Should().ContainSingle();
        var w = watchers[0];
        w.Aggregation.Should().Be(WatcherAggregation.Sum);
        w.SumDefaultExpression.Should().BeSameAs(def,
            "the two-arg sum default must ride on the watcher for the empty-stream case");
    }

    [Fact]
    public void Scanner_SumWithoutDefault_HasNoDefaultExpression()
    {
        // sum(/BOOKLIST/BOOKS/ITEM/PRICE) — single-arg form carries no default; its empty
        // contract (numeric 0 / null) stays untouched.
        var expr = Fn("sum", StridingPath());

        var watchers = ScanValueOf(expr);

        watchers.Should().ContainSingle();
        var w = watchers[0];
        w.Aggregation.Should().Be(WatcherAggregation.Sum);
        w.SumDefaultExpression.Should().BeNull();
    }

    [Fact]
    public void SumWatcher_EmptyWithDefault_SignalsSumIsEmpty()
    {
        // A Sum watcher that accumulated nothing must report SumIsEmpty so the
        // transformer substitutes the default rather than serializing an empty value-of.
        var w = new StreamWatcher
        {
            SourceExpression = new IntegerLiteral { Value = 0 },
            ContextRootDepth = -1,
            PathMatcher = new StreamPathMatcher("BOOKLIST/BOOKS/ITEM/PRICE"),
            Aggregation = WatcherAggregation.Sum,
            SumDefaultExpression = new IntegerLiteral { Value = 42 },
        };

        w.SumIsEmpty.Should().BeTrue("no leaves were fed to the watcher");
    }

    // =======================================================================
    // head/tail/subsequence/remove(seq) as a for-each select — the window rides
    // the subscription (SubsequenceStart / SubsequenceLength / RemoveIndex).
    // =======================================================================

    [Fact]
    public void Scanner_ForEachOverHead_RidesUnitWindow()
    {
        // <xsl:for-each select="head(/BOOKLIST/BOOKS/ITEM/PRICE)"> — head = subsequence
        // start 1, length 1.
        var subs = ScanForEachSubs(Fn("head", StridingPath()));

        subs.Should().ContainSingle();
        subs[0].SubsequenceStart.Should().Be(1);
        subs[0].SubsequenceLength.Should().Be(1);
    }

    [Fact]
    public void Scanner_ForEachOverTail_RidesSkipFirstWindow()
    {
        // tail() = subsequence start 2, no length bound.
        var subs = ScanForEachSubs(Fn("tail", StridingPath()));

        subs.Should().ContainSingle();
        subs[0].SubsequenceStart.Should().Be(2);
        subs[0].SubsequenceLength.Should().BeNull();
    }

    [Fact]
    public void Scanner_ForEachOverSubsequence_RidesWindow()
    {
        // subsequence(seq, 3) — start 3, unbounded.
        var subs = ScanForEachSubs(Fn("subsequence", StridingPath(), new IntegerLiteral { Value = 3L }));

        subs.Should().ContainSingle();
        subs[0].SubsequenceStart.Should().Be(3);
        subs[0].SubsequenceLength.Should().BeNull();
    }

    [Fact]
    public void Scanner_ForEachOverRemove_RidesSkipIndex()
    {
        // remove(seq, 2) — emit all but the 2nd match.
        var subs = ScanForEachSubs(Fn("remove", StridingPath(), new IntegerLiteral { Value = 2L }));

        subs.Should().ContainSingle();
        subs[0].RemoveIndex.Should().Be(2);
    }

    // =======================================================================
    // NEGATIVE — a non-forward-decidable positional arg stays conservative
    // (no subscription registered → buffer fallback), never silently dropped.
    // =======================================================================

    [Fact]
    public void Scanner_ForEachOverSubsequenceWithGroundedVariableStart_RidesExpressionBound()
    {
        // subsequence(seq, $n) where $n is a GROUNDED variable (does not navigate the
        // input) IS forward-decidable — the bound is a constant per run, evaluated once by
        // the processor. The window rides as an expression bound (sf-subsequence-015).
        var startVar = new VariableReference { Name = new QName(NamespaceId.None, "n") };
        var subs = ScanForEachSubs(Fn("subsequence", StridingPath(), startVar));

        subs.Should().ContainSingle();
        subs[0].SubsequenceStartExpression.Should().BeSameAs(startVar,
            "a grounded variable start is forward-decidable and rides as an expression bound");
    }

    [Fact]
    public void Scanner_ForEachOverSubsequenceWithStreamNavigatingStart_StaysConservative()
    {
        // subsequence(seq, count(/OTHER/PATH)) — the start NAVIGATES the input, so it is
        // genuinely NOT forward-decidable (it would require a second stream pass). The
        // window must NOT register: no subscription at all, buffer fallback — never a
        // silent drop of the bound.
        var streamBound = Fn("count", new PathExpression
        {
            IsAbsolute = true,
            InitialExpression = null,
            Steps = new[] { Step(Axis.Child, "OTHER"), Step(Axis.Child, "PATH") },
        });
        var subs = ScanForEachSubs(Fn("subsequence", StridingPath(), streamBound));

        subs.Where(s => s.SubsequenceStartExpression != null || s.SubsequenceStart != 1)
            .Should().BeEmpty("a stream-navigating start is not forward-decidable → no window subscription");
    }

    // =======================================================================
    // one-or-more(seq) / exactly-one(seq) — the fn: cardinality wrapper is a
    // focus-setting pass-through over a striding path and must be peeled so the
    // for-each registers its subscription (sf-one-or-more-015). Left unpeeled the
    // path is unreached (subs=0) and one-or-more sees an empty synthetic sequence.
    // =======================================================================

    [Fact]
    public void Scanner_ForEachOverOneOrMoreStridingPath_RegistersSubscription()
    {
        // <xsl:for-each select="one-or-more(/BOOKLIST/BOOKS/ITEM/PRICE)"> — the wrapper
        // is peeled to the striding path, which registers a single subscription.
        var subs = ScanForEachSubs(Fn("one-or-more", StridingPath()));

        subs.Should().ContainSingle(
            "the one-or-more cardinality wrapper is a pass-through and must be peeled so the striding for-each subscribes");
    }

    [Fact]
    public void Scanner_ForEachOverExactlyOneStridingPath_RegistersSubscription()
    {
        // exactly-one(...) is the same cardinality-checking pass-through.
        var subs = ScanForEachSubs(Fn("exactly-one", StridingPath()));

        subs.Should().ContainSingle(
            "the exactly-one cardinality wrapper is a pass-through and must be peeled so the striding for-each subscribes");
    }

    [Fact]
    public void Scanner_ForEachOverOneOrMoreNonStridingArg_StaysConservative()
    {
        // one-or-more($grounded) where the argument is NOT a streamable striding path
        // (here a bare grounded variable reference) must NOT register a subscription:
        // peeling exposes a non-path core that TryDecomposeForEachSelect rejects.
        var grounded = new VariableReference { Name = new QName(NamespaceId.None, "extra") };
        var subs = ScanForEachSubs(Fn("one-or-more", grounded));

        subs.Should().BeEmpty(
            "peeling one-or-more must not force a subscription when the operand is not a streamable striding path");
    }

    // unordered(seq) / trace(seq[, label]) — both are pure identity pass-throughs over
    // their value operand and must be peeled so the striding for-each subscribes
    // (sf-unordered-015, sf-trace-015). Left unpeeled the wrapped path ran against the
    // closed synthetic document → empty output.
    // =======================================================================

    [Fact]
    public void Scanner_ForEachOverUnorderedStridingPath_RegistersSubscription()
    {
        // <xsl:for-each select="unordered(/BOOKLIST/BOOKS/ITEM/PRICE)"> — the ordering
        // wrapper is a pass-through; peeling exposes the striding path.
        var subs = ScanForEachSubs(Fn("unordered", StridingPath()));

        subs.Should().ContainSingle(
            "the unordered() wrapper is an identity pass-through and must be peeled so the striding for-each subscribes");
    }

    [Fact]
    public void Scanner_ForEachOverTraceStridingPath_RegistersSubscription()
    {
        // <xsl:for-each select="trace(/BOOKLIST/BOOKS/ITEM/PRICE, 'r-015')"> — trace
        // returns its first argument unchanged; the label is diagnostic only.
        var subs = ScanForEachSubs(Fn("trace", StridingPath(), new StringLiteral { Value = "r-015" }));

        subs.Should().ContainSingle(
            "the trace() wrapper returns its value operand unchanged and must be peeled so the striding for-each subscribes");
    }

    [Fact]
    public void Scanner_ForEachOverUnorderedNonStridingArg_StaysConservative()
    {
        // unordered($grounded) where the argument is NOT a streamable striding path must
        // NOT register a subscription: peeling exposes a non-path core that
        // TryDecomposeForEachSelect rejects.
        var grounded = new VariableReference { Name = new QName(NamespaceId.None, "extra") };
        var subs = ScanForEachSubs(Fn("unordered", grounded));

        subs.Should().BeEmpty(
            "peeling unordered must not force a subscription when the operand is not a streamable striding path");
    }
}
