using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:mode warning-on-multiple-match="yes" reports a node that matches more than one template
/// rule of the same import precedence and priority, which on-multiple-match="fail" would make an
/// error. The attribute was validated and then dropped, so it produced no diagnostic
/// (W3C attr/mode mode-0802/0804).
/// </summary>
public sealed class WarningOnMultipleMatchTests
{
    private static async Task<(string Output, List<string> Warnings)> RunAsync(string modeAttributes, string rules)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:mode {modeAttributes}/>
              <xsl:template match="/"><xsl:apply-templates select="doc/item"/></xsl:template>
              {rules}
            </xsl:stylesheet>
            """;
        var warnings = new List<string>();
        var t = new XsltTransformer { WarningListener = warnings.Add };
        await t.LoadStylesheetAsync(ss);
        var output = await t.TransformAsync("<doc><item/></doc>");
        return (output.Trim(), warnings);
    }

    private const string TwoEqualRules = """
        <xsl:template match="doc/*">first</xsl:template>
        <xsl:template match="doc/item">second</xsl:template>
        """;

    [Theory]
    [InlineData("""warning-on-multiple-match="yes" """)]
    [InlineData("""warning-on-multiple-match=" yes " """)]
    [InlineData("""warning-on-multiple-match="true" """)]
    [InlineData("""warning-on-multiple-match="1" """)]
    public async Task EqualRankRules_AreReported(string modeAttributes)
    {
        var (output, warnings) = await RunAsync(modeAttributes, TwoEqualRules);
        warnings.Should().ContainSingle();
        warnings[0].Should().Contain("Multiple template rules match")
            .And.Contain("doc/item").And.Contain("doc/*");
        // A warning, not an error: the last rule in declaration order is still used.
        output.Should().Be("second");
    }

    [Theory]
    [InlineData("")]
    [InlineData("""warning-on-multiple-match="no" """)]
    public async Task WithoutTheAttribute_NothingIsReported(string modeAttributes)
        => (await RunAsync(modeAttributes, TwoEqualRules)).Warnings.Should().BeEmpty();

    /// <summary>A rule that wins on priority has no rival: that is ordinary conflict resolution.</summary>
    [Fact]
    public async Task HigherPriorityRule_IsNotReported()
    {
        var (output, warnings) = await RunAsync("""warning-on-multiple-match="yes" """, """
            <xsl:template match="doc/*">first</xsl:template>
            <xsl:template match="doc/item" priority="1">second</xsl:template>
            """);
        warnings.Should().BeEmpty();
        output.Should().Be("second");
    }

    /// <summary>Two branches of one union pattern are one rule (spec bug 30402), not a conflict.</summary>
    [Fact]
    public async Task UnionBranchesOfOneRule_AreNotReported()
        => (await RunAsync("""warning-on-multiple-match="yes" """,
                """<xsl:template match="doc/item | item">one</xsl:template>""")).Warnings.Should().BeEmpty();

    /// <summary>on-multiple-match="fail" still raises XTDE0540; the warning doesn't replace it.</summary>
    [Fact]
    public async Task FailStillRaisesXTDE0540()
    {
        var act = () => RunAsync("""on-multiple-match="fail" warning-on-multiple-match="yes" """, TwoEqualRules);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE0540");
    }
}
