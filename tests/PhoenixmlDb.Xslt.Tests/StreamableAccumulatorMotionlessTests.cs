using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XTSE3430 for a streamable accumulator was one blanket rule — "the pattern must not contain
/// predicates" — which is both too strict and too narrow. A predicate that only reads attributes
/// of the matched element is motionless and must be allowed; a rule's <c>select</c> that navigates
/// into children is not motionless and was never checked at all.
/// </summary>
public sealed class StreamableAccumulatorMotionlessTests
{
    private const string Source = """<doc><chap><fig alt="a"><caption>x</caption></fig></chap></doc>""";

    private static async Task<Exception?> LoadAsync(string accumulatorRules)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:accumulator name="a" as="xs:integer" initial-value="0" streamable="yes">
                {accumulatorRules}
              </xsl:accumulator>
              <xsl:mode streamable="yes" on-no-match="shallow-skip" use-accumulators="a"/>
              <xsl:template match="fig"><xsl:value-of select="accumulator-before('a')"/></xsl:template>
              <xsl:variable name="seven" select="7"/>
            </xsl:stylesheet>
            """;
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(ss);
            await t.TransformAsync(Source);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>
    /// The accept case, taken from W3C accumulator-034: both predicates read only attributes of the
    /// node being matched, which the stream has in hand at the start tag.
    /// </summary>
    [Fact]
    public async Task AnAttributeOnlyPredicate_IsMotionless()
        => (await LoadAsync("""
            <xsl:accumulator-rule match="chap[not(@nr = $seven)]" select="0"/>
            <xsl:accumulator-rule match="fig[every $n in data(@*) satisfies $n = '83']" select="$value + 2"/>
            """)).Should().BeNull("a predicate over attributes needs nothing the stream has not delivered");

    /// <summary>W3C accumulator-030: the select reads a child element.</summary>
    [Fact]
    public async Task ARuleSelectThatReadsAChild_IsRejected()
    {
        var ex = await LoadAsync("""<xsl:accumulator-rule match="fig" select="$value + string-length(caption)"/>""");
        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("XTSE3430");
    }

    /// <summary>A predicate that descends is still rejected — the loosening is only for attributes.</summary>
    [Fact]
    public async Task APredicateThatReadsAChild_IsStillRejected()
    {
        var ex = await LoadAsync("""<xsl:accumulator-rule match="fig[caption]" select="$value + 1"/>""");
        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("XTSE3430");
    }
}
