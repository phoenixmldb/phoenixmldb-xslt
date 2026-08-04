using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A template with a declared <c>as=</c> type that is invoked while an enclosing
/// element is being constructed natively (inside <c>xsl:copy</c> / LRE content)
/// writes its result straight into that construction — so the per-invocation
/// result capture is empty even though the template really did produce a node.
/// Validating the declared cardinality against that empty capture reported a
/// spurious <c>XTTE0505 … expected exactly one item, got 0</c>.
/// Surfaced by XSpec <c>gather-specs.xsl</c>, where a nested <c>x:scenario</c>
/// (<c>as="element(x:scenario)"</c>) is copied inside its parent's
/// <c>xsl:copy</c>. Reported by Martin Honnen.
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

    [Fact(Skip = "OPEN BUG (XSpec #3): a typed template invoked inside a parent xsl:copy " +
                 "loses its result entirely — the nested element is absent from the output and " +
                 "the empty capture then raises a spurious XTTE0505. Instrumented state at the " +
                 "nested invocation: _serializingElementDepth=1, _collectedAttributesStack=1, " +
                 "no TreeConstructor, bodyOutput=0, accumulator=0. See " +
                 "repros/martin/xspec-typed-template-copy/README.md.")]
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
