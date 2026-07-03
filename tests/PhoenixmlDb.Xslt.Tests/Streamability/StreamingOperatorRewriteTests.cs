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
/// Task 1.1 — the streaming body rewriter must recurse through operator nodes
/// (if/sequence/quantified/instance-of/cast/…) so a watched striding base path
/// nested inside such an operator is substituted with <c>$__streaming_watcher_N</c>
/// instead of being left to run against the closed synthetic document (→ empty
/// sequence → <c>exists()</c>=false / <c>path = literal</c>=false).
///
/// These tests exercise the two halves of the fix directly on the AST:
/// (1) <see cref="StreamingExpressionScanner"/> registers a Sequence watcher for a
///     striding child-axis base path discovered inside an operator, and
/// (2) <see cref="DefaultXsltExecutionContext.RewriteWithWatcherVariables"/> rebuilds
///     the operator node with the watched child replaced by the synthetic variable.
/// </summary>
public class StreamingOperatorRewriteTests
{
    // ---- AST factory helpers ----------------------------------------------

    private static NameTest Name(string local) => new() { LocalName = local, NamespaceUri = null };

    private static StepExpression Step(Axis axis, string local) => new()
    {
        Axis = axis,
        NodeTest = Name(local),
        Predicates = System.Array.Empty<XQueryExpression>(),
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

    private static IReadOnlyList<StreamWatcher> ScanSelect(XQueryExpression select)
    {
        var body = new XsltSequenceConstructor
        {
            Instructions = new XsltInstruction[] { new XsltValueOf { Select = select } },
        };
        return new StreamingExpressionScanner().Scan(body);
    }

    private static bool IsWatcherVar(XQueryExpression e) =>
        e is VariableReference vr && vr.Name.LocalName.StartsWith("__streaming_watcher_", System.StringComparison.Ordinal);

    // =======================================================================
    // Scanner: a striding base path nested inside an operator registers a watcher.
    // =======================================================================

    [Fact]
    public void Scanner_ExistsPathInsideIf_RegistersSequenceWatcher()
    {
        // if (exists(/BOOKLIST/BOOKS/ITEM/PRICE)) then 0 else 1   (sx-if-201)
        var path = StridingPath();
        var expr = new IfExpression
        {
            Condition = Fn("exists", path),
            Then = new IntegerLiteral { Value = 0 },
            Else = new IntegerLiteral { Value = 1 },
        };

        var watchers = ScanSelect(expr);

        watchers.Should().ContainSingle();
        var w = watchers[0];
        w.Aggregation.Should().Be(WatcherAggregation.Sequence);
        // The watcher keys on the striding base path node itself, so the rewriter's
        // reference-equality substitution can reach it through the operators.
        w.SourceExpression.Should().BeSameAs(path);
    }

    [Fact]
    public void Scanner_PathInsideSequenceOperand_RegistersSequenceWatcher()
    {
        // (/BOOKLIST/BOOKS/ITEM/PRICE, 31, 32)   — the striding LHS of a comparison.
        var path = StridingPath();
        var expr = new SequenceExpression
        {
            Items = new XQueryExpression[]
            {
                path,
                new IntegerLiteral { Value = 31 },
                new IntegerLiteral { Value = 32 },
            },
        };

        var watchers = ScanSelect(expr);

        watchers.Should().ContainSingle();
        watchers[0].SourceExpression.Should().BeSameAs(path);
        watchers[0].Aggregation.Should().Be(WatcherAggregation.Sequence);
    }

    // =======================================================================
    // NEGATIVE: a non-streamable / roaming path must NOT register a watcher.
    // =======================================================================

    [Fact]
    public void Scanner_RoamingPathInsideIf_DoesNotRegisterWatcher()
    {
        // if (exists(ancestor::BOOKLIST)) then 0 else 1 — a reverse (climbing) axis
        // is not a downward striding path; IsDownwardPath rejects it, so no watcher
        // is registered (climbing streaming is Task 1.3, not this slice).
        var roaming = new PathExpression
        {
            IsAbsolute = false,
            InitialExpression = null,
            Steps = new[] { Step(Axis.Ancestor, "BOOKLIST") },
        };
        var expr = new IfExpression
        {
            Condition = Fn("exists", roaming),
            Then = new IntegerLiteral { Value = 0 },
            Else = new IntegerLiteral { Value = 1 },
        };

        var watchers = ScanSelect(expr);

        watchers.Should().BeEmpty();
    }

    // =======================================================================
    // Rewriter: the watched path inside the operator becomes $__streaming_watcher_N.
    // =======================================================================

    [Fact]
    public void Rewriter_ExistsPathInsideIf_SubstitutesWatcherVariable()
    {
        // if (exists(<striding-path>)) then 0 else 1 — after rewrite the path node
        // inside exists(...) must be a $__streaming_watcher_N variable reference.
        var path = StridingPath();
        var existsCall = Fn("exists", path);
        var expr = new IfExpression
        {
            Condition = existsCall,
            Then = new IntegerLiteral { Value = 0 },
            Else = new IntegerLiteral { Value = 1 },
        };

        var watcher = new StreamWatcher
        {
            SourceExpression = path,
            ContextRootDepth = -1,
            PathMatcher = new StreamPathMatcher("BOOKLIST/BOOKS/ITEM/PRICE"),
            Aggregation = WatcherAggregation.Sequence,
        };

        var rewritten = DefaultXsltExecutionContext.RewriteWithWatcherVariables(
            expr, new[] { watcher });

        rewritten.Should().NotBeSameAs(expr);
        var iff = rewritten.Should().BeOfType<IfExpression>().Subject;
        var cond = iff.Condition.Should().BeOfType<FunctionCallExpression>().Subject;
        cond.Arguments.Should().ContainSingle();
        IsWatcherVar(cond.Arguments[0]).Should().BeTrue(
            "the striding path inside exists(...) inside the if-condition must be substituted");
    }

    [Fact]
    public void Rewriter_PathInsideSequence_SubstitutesWatcherVariable()
    {
        // (<striding-path>, 31, 32) — only the path item is substituted; literals stay.
        var path = StridingPath();
        var expr = new SequenceExpression
        {
            Items = new XQueryExpression[]
            {
                path,
                new IntegerLiteral { Value = 31 },
                new IntegerLiteral { Value = 32 },
            },
        };

        var watcher = new StreamWatcher
        {
            SourceExpression = path,
            ContextRootDepth = -1,
            PathMatcher = new StreamPathMatcher("BOOKLIST/BOOKS/ITEM/PRICE"),
            Aggregation = WatcherAggregation.Sequence,
        };

        var rewritten = DefaultXsltExecutionContext.RewriteWithWatcherVariables(
            expr, new[] { watcher });

        var seq = rewritten.Should().BeOfType<SequenceExpression>().Subject;
        IsWatcherVar(seq.Items[0]).Should().BeTrue();
        seq.Items[1].Should().BeOfType<IntegerLiteral>();
        seq.Items[2].Should().BeOfType<IntegerLiteral>();
    }

    // =======================================================================
    // Task 1.2 — a streamable striding base path reached through a
    // FilterExpression composition point ((path)[pred]) is watched and
    // substituted. This is the sx-arithmetic-001 shape: (path)[1] + 2.
    // =======================================================================

    [Fact]
    public void Scanner_PathInsideFilterPrimary_RegistersSequenceWatcher()
    {
        // (/BOOKLIST/BOOKS/ITEM/PRICE)[1] + 2 — the striding base path is the
        // Primary of a FilterExpression, itself the LHS of an arithmetic binary.
        var path = StridingPath();
        var filter = new FilterExpression
        {
            Primary = path,
            Predicates = new XQueryExpression[] { new IntegerLiteral { Value = 1 } },
        };
        var expr = new BinaryExpression
        {
            Left = filter,
            Operator = BinaryOperator.Add,
            Right = new IntegerLiteral { Value = 2 },
        };

        var watchers = ScanSelect(expr);

        watchers.Should().ContainSingle();
        var w = watchers[0];
        w.Aggregation.Should().Be(WatcherAggregation.Sequence);
        // Keyed on the striding base path node so the rewriter's reference-equality
        // substitution reaches it through the FilterExpression + BinaryExpression.
        w.SourceExpression.Should().BeSameAs(path);
    }

    [Fact]
    public void Rewriter_PathInsideFilterPrimary_SubstitutesWatcherVariable()
    {
        // (<striding-path>)[1] — after rewrite the Primary of the FilterExpression
        // must be a $__streaming_watcher_N variable reference; the predicate stays.
        var path = StridingPath();
        var filter = new FilterExpression
        {
            Primary = path,
            Predicates = new XQueryExpression[] { new IntegerLiteral { Value = 1 } },
        };

        var watcher = new StreamWatcher
        {
            SourceExpression = path,
            ContextRootDepth = -1,
            PathMatcher = new StreamPathMatcher("BOOKLIST/BOOKS/ITEM/PRICE"),
            Aggregation = WatcherAggregation.Sequence,
        };

        var rewritten = DefaultXsltExecutionContext.RewriteWithWatcherVariables(
            filter, new[] { watcher });

        rewritten.Should().NotBeSameAs(filter);
        var filt = rewritten.Should().BeOfType<FilterExpression>().Subject;
        IsWatcherVar(filt.Primary).Should().BeTrue(
            "the striding base path that is the Primary of a filter must be substituted");
        filt.Predicates.Should().ContainSingle();
        filt.Predicates[0].Should().BeOfType<IntegerLiteral>();
    }

    [Fact]
    public void Scanner_RoamingPathInsideFilterPrimary_DoesNotRegisterWatcher()
    {
        // (ancestor::BOOKLIST)[1] — a reverse (climbing) axis inside a filter is not
        // a striding downward path; IsDownwardPath rejects it, so no watcher registers
        // (climbing streaming is Task 1.3, this stays conservative buffer-fallback).
        var roaming = new PathExpression
        {
            IsAbsolute = false,
            InitialExpression = null,
            Steps = new[] { Step(Axis.Ancestor, "BOOKLIST") },
        };
        var filter = new FilterExpression
        {
            Primary = roaming,
            Predicates = new XQueryExpression[] { new IntegerLiteral { Value = 1 } },
        };

        var watchers = ScanSelect(filter);

        watchers.Should().BeEmpty();
    }

    [Fact]
    public void Rewriter_NoWatchMatch_ReturnsSameReference()
    {
        // No watcher matches any node → the tree is returned unchanged (plan-cache warm).
        var expr = new IfExpression
        {
            Condition = Fn("exists", StridingPath()),
            Then = new IntegerLiteral { Value = 0 },
            Else = new IntegerLiteral { Value = 1 },
        };

        var unrelated = new StreamWatcher
        {
            SourceExpression = new IntegerLiteral { Value = 99 },
            ContextRootDepth = -1,
            PathMatcher = new StreamPathMatcher("OTHER"),
            Aggregation = WatcherAggregation.Sequence,
        };

        var rewritten = DefaultXsltExecutionContext.RewriteWithWatcherVariables(
            expr, new[] { unrelated });

        rewritten.Should().BeSameAs(expr);
    }
}
