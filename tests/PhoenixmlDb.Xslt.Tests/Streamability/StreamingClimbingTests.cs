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
/// Task 1.3 — CLIMBING (ancestor/upward-navigation) streaming dispatch.
///
/// A "striding-then-climbing" operand — a downward striding base path that locates a
/// leaf, followed by an <c>ancestor::</c> / <c>ancestor-or-self::</c> step (optionally
/// then an attribute) — is streamable WITHOUT buffering the whole input: at the leaf's
/// StartElement every ancestor is already open on the streaming element stack, so the
/// upward navigation is fully resolvable there. These tests exercise the scanner
/// registration + the climbing resolution against a synthetic ancestor stack.
///
/// SOUNDNESS boundary preserved from Tasks 1.1/1.2: a BARE climbing path with no
/// striding anchor (e.g. <c>ancestor::X</c> at the body's context) is NOT registered —
/// there is no leaf to key the climb to. Those negative cases live in
/// <see cref="StreamingOperatorRewriteTests"/> and must stay conservative.
/// </summary>
public class StreamingClimbingTests
{
    private static NameTest Name(string local) => new() { LocalName = local, NamespaceUri = null };
    private static NameTest AnyName() => new() { LocalName = "*", NamespaceUri = "*" };

    private static StepExpression Step(Axis axis, NodeTest test) => new()
    {
        Axis = axis,
        NodeTest = test,
        Predicates = System.Array.Empty<XQueryExpression>(),
    };

    private static StepExpression Step(Axis axis, string local) => Step(axis, Name(local));

    // /BOOKLIST/BOOKS/ITEM/PRICE/ancestor::*  — striding prefix then climb to all ancestor elements.
    private static PathExpression StridingThenClimb(Axis climbAxis = Axis.Ancestor) => new()
    {
        IsAbsolute = true,
        InitialExpression = null,
        Steps = new[]
        {
            Step(Axis.Child, "BOOKLIST"),
            Step(Axis.Child, "BOOKS"),
            Step(Axis.Child, "ITEM"),
            Step(Axis.Child, "PRICE"),
            Step(climbAxis, AnyName()),
        },
    };

    // /BOOKLIST/BOOKS/ITEM/ancestor-or-self::*/@CAT
    private static PathExpression ClimbThenAttribute() => new()
    {
        IsAbsolute = true,
        InitialExpression = null,
        Steps = new[]
        {
            Step(Axis.Child, "BOOKLIST"),
            Step(Axis.Child, "BOOKS"),
            Step(Axis.Child, "ITEM"),
            Step(Axis.AncestorOrSelf, AnyName()),
            Step(Axis.Attribute, Name("CAT")),
        },
    };

    private static IReadOnlyList<StreamWatcher> ScanSelect(XQueryExpression select)
    {
        var body = new XsltSequenceConstructor
        {
            Instructions = new XsltInstruction[] { new XsltValueOf { Select = select } },
        };
        return new StreamingExpressionScanner().Scan(body);
    }

    // =======================================================================
    // Scanner: a striding-then-climbing path registers a climbing watcher.
    // =======================================================================

    [Fact]
    public void Scanner_StridingThenAncestorClimb_RegistersClimbingWatcher()
    {
        var path = StridingThenClimb(Axis.Ancestor);

        var watchers = ScanSelect(path);

        watchers.Should().ContainSingle();
        var w = watchers[0];
        w.SourceExpression.Should().BeSameAs(path);
        w.ClimbAxis.Should().Be(ClimbAxisKind.Ancestor);
        w.ValueAttribute.Should().BeNull("bare ancestor::* climb yields element nodes, not attribute values");
    }

    [Fact]
    public void Scanner_StridingThenAncestorOrSelfClimbAttribute_RegistersClimbingAttributeWatcher()
    {
        var path = ClimbThenAttribute();

        var watchers = ScanSelect(path);

        watchers.Should().ContainSingle();
        var w = watchers[0];
        w.ClimbAxis.Should().Be(ClimbAxisKind.AncestorOrSelf);
        w.ValueAttribute.Should().Be("CAT");
    }

    // =======================================================================
    // NEGATIVE: a bare climbing path (no striding anchor) stays conservative.
    // =======================================================================

    [Fact]
    public void Scanner_BareAncestorClimb_DoesNotRegisterWatcher()
    {
        // ancestor::BOOKLIST — no downward leaf to anchor the climb; not streamable here.
        var bare = new PathExpression
        {
            IsAbsolute = false,
            InitialExpression = null,
            Steps = new[] { Step(Axis.Ancestor, "BOOKLIST") },
        };

        var watchers = ScanSelect(bare);

        watchers.Should().BeEmpty();
    }

    // =======================================================================
    // Resolution: climbing a synthetic ancestor stack yields the ancestor names /
    // attribute values in the correct (document) order.
    // =======================================================================

    [Fact]
    public void Climb_AncestorAxis_YieldsAncestorNamesInDocumentOrder()
    {
        // Leaf PRICE with open ancestors [BOOKLIST, BOOKS, ITEM] (outermost first).
        // ancestor::* over PRICE = (BOOKLIST, BOOKS, ITEM) in document order.
        var w = new StreamWatcher
        {
            SourceExpression = new IntegerLiteral { Value = 0 },
            PathMatcher = new StreamPathMatcher("BOOKLIST/BOOKS/ITEM/PRICE"),
            Aggregation = WatcherAggregation.Sequence,
            ClimbAxis = ClimbAxisKind.Ancestor,
        };

        var ancestors = new List<StreamWatcher.ClimbAncestor>
        {
            new(1, "BOOKLIST", null),
            new(2, "BOOKS", null),
            new(3, "ITEM", null),
        };
        w.OnClimbMatch(ancestors, leafName: "PRICE", leafAttributes: null);

        var result = (object[])w.GetResult()!;
        result.Select(NodeName).Should().Equal("BOOKLIST", "BOOKS", "ITEM");
    }

    [Fact]
    public void Climb_AncestorOrSelfAttribute_YieldsSelfAndAncestorAttributeValues()
    {
        // ancestor-or-self::*/@CAT over leaf ITEM(CAT=MMP) with ancestors
        // [BOOKLIST(-), BOOKS(OWNER=MHK)] — only ITEM carries @CAT, so result = ("MMP").
        var w = new StreamWatcher
        {
            SourceExpression = new IntegerLiteral { Value = 0 },
            PathMatcher = new StreamPathMatcher("BOOKLIST/BOOKS/ITEM"),
            Aggregation = WatcherAggregation.Sequence,
            ClimbAxis = ClimbAxisKind.AncestorOrSelf,
            ValueAttribute = "CAT",
        };

        var ancestors = new List<StreamWatcher.ClimbAncestor>
        {
            new(1, "BOOKLIST", null),
            new(2, "BOOKS", new Dictionary<string, string> { ["OWNER"] = "MHK" }),
        };
        w.OnClimbMatch(ancestors, leafName: "ITEM",
            leafAttributes: new Dictionary<string, string> { ["CAT"] = "MMP" });

        var result = (object[])w.GetResult()!;
        result.Select(o => o.ToString()!).Should().Equal("MMP");
    }

    [Fact]
    public void Climb_MultipleLeaves_DeduplicatesSharedAncestorsByOpenId()
    {
        // Two PRICE leaves under distinct ITEMs sharing BOOKLIST/BOOKS.
        // ancestor::* de-dups by node identity (open id) in document order:
        // (BOOKLIST, BOOKS, ITEM#3, ITEM#4).
        var w = new StreamWatcher
        {
            SourceExpression = new IntegerLiteral { Value = 0 },
            PathMatcher = new StreamPathMatcher("BOOKLIST/BOOKS/ITEM/PRICE"),
            Aggregation = WatcherAggregation.Sequence,
            ClimbAxis = ClimbAxisKind.Ancestor,
        };

        w.OnClimbMatch(new List<StreamWatcher.ClimbAncestor>
        {
            new(1, "BOOKLIST", null), new(2, "BOOKS", null), new(3, "ITEM", null),
        }, "PRICE", null);
        w.OnClimbMatch(new List<StreamWatcher.ClimbAncestor>
        {
            new(1, "BOOKLIST", null), new(2, "BOOKS", null), new(4, "ITEM", null),
        }, "PRICE", null);

        var result = (object[])w.GetResult()!;
        result.Select(NodeName).Should().Equal("BOOKLIST", "BOOKS", "ITEM", "ITEM");
    }

    private static string NodeName(object o) => o switch
    {
        PhoenixmlDb.Xdm.Nodes.XdmElement e => e.LocalName,
        _ => o.ToString() ?? "",
    };
}
