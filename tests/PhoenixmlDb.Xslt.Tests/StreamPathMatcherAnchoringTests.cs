using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Covers the context-root anchoring of <see cref="StreamPathMatcher"/>. A
/// <c>contextRootDepth</c> of <c>-1</c> (the default) preserves the legacy floating
/// behavior; a non-negative depth anchors the leftmost concrete step as a child of
/// the context root at that absolute depth in the ancestor-name stack.
/// </summary>
public class StreamPathMatcherAnchoringTests
{
    [Fact]
    public void RelativePath_FloatsWhenDepthOmitted()
    {
        // Legacy: a relative path matches at any depth (contextRootDepth = -1).
        new StreamPathMatcher("ITEM/PAGES")
            .Matches(new[] { "BOOKLIST", "BOOKS", "ITEM" }, "PAGES")
            .Should().BeTrue();
    }

    [Fact]
    public void RelativePath_AnchoredAtDepthZero_DoesNotFloat()
    {
        // ITEM is NOT a child of the document root, so anchored depth-0 rejects it.
        new StreamPathMatcher("ITEM/PAGES")
            .Matches(new[] { "BOOKLIST", "BOOKS", "ITEM" }, "PAGES", 0)
            .Should().BeFalse();
    }

    [Fact]
    public void Length1_AnchoredAtDepthZero_MatchesRootChild()
    {
        new StreamPathMatcher("PAGES")
            .Matches(new[] { "ITEM" }, "PAGES", 0)
            .Should().BeTrue();
    }

    [Fact]
    public void FullPath_AnchoredAtDepthZero_Matches()
    {
        new StreamPathMatcher("BOOKLIST/BOOKS/ITEM/PRICE")
            .Matches(new[] { "BOOKLIST", "BOOKS", "ITEM" }, "PRICE", 0)
            .Should().BeTrue();
    }

    [Fact]
    public void Length1_AnchoredAtDepth1_BodyCase_Matches()
    {
        // <root><body><n/>…: the matched template fired on <body> (depth 1), so the
        // relative path "n" is anchored as a child of body.
        new StreamPathMatcher("n")
            .Matches(new[] { "root", "body" }, "n", 1)
            .Should().BeTrue();
    }

    [Fact]
    public void Length1_AnchoredAtDepth1_TooShallow_DoesNotMatch()
    {
        new StreamPathMatcher("n")
            .Matches(new[] { "root" }, "n", 1)
            .Should().BeFalse();
    }

    [Fact]
    public void DescendantAxis_StillFloatsUnderAnchoring()
    {
        // "**" is a descendant float: even with anchoring it matches at any depth.
        new StreamPathMatcher("**/PAGES")
            .Matches(new[] { "BOOKLIST", "BOOKS", "ITEM" }, "PAGES", 0)
            .Should().BeTrue();
    }
}
