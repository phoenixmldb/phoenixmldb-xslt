using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// When several of an accumulator's rules match a node in the same phase, the one LAST in
/// document order applies (XSLT 3.0 §18.2.4: "Let Q be the xsl:accumulator-rule in R that is
/// last in document order"). Priority plays no part. Every selection site took the first match;
/// W3C accumulator-081 gave 1 where the spec gives 2.
/// </summary>
public sealed class AccumulatorRuleSelectionTests
{
    private static async Task<string> RunAsync(string rules, string phase = "start")
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:mode use-accumulators="#all"/>
              <xsl:accumulator name="a" as="xs:string" initial-value="'none'">{{rules}}</xsl:accumulator>
              <xsl:template match="/"><xsl:value-of select="/r/e/accumulator-after('a')"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<r><e/></r>")).Trim();
    }

    [Theory]
    [InlineData("""<xsl:accumulator-rule match="e" select="'first'"/><xsl:accumulator-rule match="e" select="'last'"/>""", "last")]
    // Default priority does not decide it: the more specific pattern is FIRST here and still loses.
    [InlineData("""<xsl:accumulator-rule match="r/e" select="'specific'"/><xsl:accumulator-rule match="*" select="'last'"/>""", "last")]
    [InlineData("""<xsl:accumulator-rule match="*" select="'first'"/><xsl:accumulator-rule match="r/e" select="'last'"/>""", "last")]
    // A later rule for the OTHER phase does not shadow an earlier one for this phase.
    [InlineData("""<xsl:accumulator-rule match="e" select="'start'"/><xsl:accumulator-rule match="e" phase="end" select="$value || '+end'"/>""", "start+end")]
    public async Task TheLastMatchingRuleInDocumentOrderApplies(string rules, string expected)
        => (await RunAsync(rules)).Should().Be(expected);
}
