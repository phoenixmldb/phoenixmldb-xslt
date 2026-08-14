using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A typed (<c>as=</c>) template installs a sequence accumulator to capture its result, which
/// routes <c>xsl:copy</c> of an element down its serialize-then-reparse path: the copy is
/// written to the output buffer, sliced back out, and reparsed into an XDM node wrapped in a
/// synthetic root.
///
/// That serialized fragment carries only the namespace declarations it actually needed at its
/// position — any the ancestors already declare are suppressed as redundant. So a NESTED copy
/// reparsed bare has an undeclared prefix and throws, and the handler had already truncated
/// the buffer: the element was destroyed outright. The nested element vanished from the
/// output, and validating the declared cardinality against the resulting empty capture
/// reported a spurious <c>XTTE0505 … expected exactly one item, got 0</c>.
///
/// Fixed by declaring the in-scope namespaces on the synthetic wrapper so the fragment stands
/// alone. Note the failure needed all three of: a declared <c>as=</c> type, nesting, and an
/// <c>xsl:copy</c> parent — an LRE parent uses a different construction path and was fine.
///
/// Surfaced by XSpec <c>gather-specs.xsl</c>, where a nested <c>x:scenario</c>
/// (<c>as="element(x:scenario)"</c>) is copied inside its parent's <c>xsl:copy</c>.
/// Reported by Martin Honnen.
/// </summary>
public sealed class TypedTemplateInsideConstructionTests
{
    private const string NestedCopyStylesheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:x="http://www.jenitennison.com/xslt/xspec">
          <xsl:mode name="m" on-no-match="shallow-copy"/>
          <xsl:template match="/"><xsl:apply-templates select="x:desc/x:scenario" mode="m"/></xsl:template>
          <xsl:template match="x:scenario" as="element(x:scenario)" mode="m">
            <xsl:copy>
              <xsl:attribute name="added" select="'1'"/>
              <xsl:apply-templates select="attribute() | node()" mode="#current"/>
            </xsl:copy>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private const string NestedInput =
        """<x:desc xmlns:x="http://www.jenitennison.com/xslt/xspec"><x:scenario label="outer"><x:scenario label="inner"/></x:scenario></x:desc>""";

    [Fact]
    public async Task TypedTemplate_NestedInsideParentCopy_DoesNotRaiseSpuriousXTTE0505()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(NestedCopyStylesheet);
        var result = (await t.TransformAsync(NestedInput)).Trim();

        // Both the outer and the nested scenario must survive the copy, each with
        // the attribute the template adds. Previously this threw XTTE0505.
        result.Should().Contain("label=\"outer\"");
        result.Should().Contain("label=\"inner\"");
        result.Should().Contain("added=\"1\"");
    }

    /// <summary>
    /// The mechanism, isolated: the prefix is declared ONLY on the source root, so every copied
    /// descendant serializes without an xmlns declaration of its own and can only be reparsed
    /// against the in-scope set. Three levels deep, so a fix that only handles the first nested
    /// copy still fails here.
    /// </summary>
    [Fact]
    public async Task TypedTemplate_DeeplyNestedCopy_PreservesEveryLevel()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:x="http://www.jenitennison.com/xslt/xspec">
              <xsl:mode name="m" on-no-match="shallow-copy"/>
              <xsl:template match="/"><xsl:apply-templates select="x:desc/x:scenario" mode="m"/></xsl:template>
              <xsl:template match="x:scenario" as="element(x:scenario)" mode="m">
                <xsl:copy>
                  <xsl:attribute name="depth" select="count(ancestor::x:scenario)"/>
                  <xsl:apply-templates select="attribute() | node()" mode="#current"/>
                </xsl:copy>
              </xsl:template>
            </xsl:stylesheet>
            """;
        const string input = """
            <x:desc xmlns:x="http://www.jenitennison.com/xslt/xspec"><x:scenario label="l0"><x:scenario label="l1"><x:scenario label="l2"/></x:scenario></x:scenario></x:desc>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var result = await t.TransformAsync(input);

        foreach (var label in new[] { "l0", "l1", "l2" })
            result.Should().Contain($"label=\"{label}\"", "every nesting level must survive the copy");
        foreach (var depth in new[] { 0, 1, 2 })
            result.Should().Contain($"depth=\"{depth}\"");
        // The prefix binding must appear exactly once — on the outermost copy — and the
        // reparse must not have injected the synthetic wrapper's declarations into the output.
        result.Split("xmlns:x=").Length.Should().Be(2);
        result.Should().NotContain("_copy_root_");
    }

    [Fact]
    public async Task TypedTemplate_NotNested_StillValidatesCardinality()
    {
        // Guard against over-relaxing: when the body is NOT inside an enclosing
        // construction and genuinely yields nothing, XTTE0505 must still fire.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><xsl:apply-templates select="a"/></xsl:template>
              <xsl:template match="a" as="element(a)"><!-- produces nothing --></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var act = async () => await t.TransformAsync("<a/>");
        (await act.Should().ThrowAsync<System.Exception>()).WithMessage("*XTTE0505*");
    }
}
