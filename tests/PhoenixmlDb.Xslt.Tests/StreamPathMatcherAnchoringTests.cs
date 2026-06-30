using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Covers the context-root anchoring of <see cref="StreamPathMatcher"/>. A
/// <c>contextRootDepth</c> of <c>null</c> (the default) preserves the legacy floating
/// behavior; a value anchors the leftmost concrete step as a child of the context
/// root at that stack index. The source-document context root is the document NODE,
/// stack index <c>-1</c> (above element index 0), so its child elements fire with an
/// empty ancestor stack. A deferred matched-template anchors to the matched element's
/// stack index (its <c>ParentDepth</c>).
/// </summary>
public class StreamPathMatcherAnchoringTests
{
    [Fact]
    public void RelativePath_FloatsWhenDepthOmitted()
    {
        // Legacy: a relative path matches at any depth (contextRootDepth = null).
        new StreamPathMatcher("ITEM/PAGES")
            .Matches(new[] { "BOOKLIST", "BOOKS", "ITEM" }, "PAGES")
            .Should().BeTrue();
    }

    [Fact]
    public void RelativePath_AnchoredAtSourceDocument_DoesNotFloat()
    {
        // ITEM is NOT a child of the document node, so source-doc anchoring (-1)
        // rejects it. This is the sf-avg/min/max-011 bug the original fix closed.
        new StreamPathMatcher("ITEM/PAGES")
            .Matches(new[] { "BOOKLIST", "BOOKS", "ITEM" }, "PAGES", -1)
            .Should().BeFalse();
    }

    [Fact]
    public void FullPath_AnchoredAtSourceDocument_Matches()
    {
        new StreamPathMatcher("BOOKLIST/BOOKS/ITEM/PRICE")
            .Matches(new[] { "BOOKLIST", "BOOKS", "ITEM" }, "PRICE", -1)
            .Should().BeTrue();
    }

    [Fact]
    public void Length1_DocumentElement_AnchoredAtSourceDocument_Matches()
    {
        // The sf-count-011 shape: <xsl:if test="child::BOOKLIST"> on the document
        // node. The document ELEMENT BOOKLIST fires with an EMPTY ancestor stack, so
        // a length-1 path anchored at the document node (-1) must match.
        new StreamPathMatcher("BOOKLIST")
            .Matches(System.Array.Empty<string>(), "BOOKLIST", -1)
            .Should().BeTrue();
    }

    [Fact]
    public void Length1_AnchoredAtSourceDocument_TooDeep_DoesNotMatch()
    {
        // A length-1 source-doc path must NOT match a same-named element nested
        // below the document element.
        new StreamPathMatcher("BOOKLIST")
            .Matches(new[] { "X" }, "BOOKLIST", -1)
            .Should().BeFalse();
    }

    [Fact]
    public void Length1_AnchoredAtDepth1_BodyCase_Matches()
    {
        // <root><body><n/>…: the matched template fired on <body> (stack index 1), so
        // the relative path "n" is anchored as a child of body.
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
    public void MultiStep_AnchoredAtDepth1_BodyCase_Matches()
    {
        // <root><body><x><y/>…: template fired on <body> (stack index 1); "x/y" is
        // anchored so x is a child of body.
        new StreamPathMatcher("x/y")
            .Matches(new[] { "root", "body", "x" }, "y", 1)
            .Should().BeTrue();
    }

    [Fact]
    public void DescendantAxis_StillFloatsUnderAnchoring()
    {
        // "**" is a descendant float: even with anchoring it matches at any depth.
        new StreamPathMatcher("**/PAGES")
            .Matches(new[] { "BOOKLIST", "BOOKS", "ITEM" }, "PAGES", -1)
            .Should().BeTrue();
    }
}
