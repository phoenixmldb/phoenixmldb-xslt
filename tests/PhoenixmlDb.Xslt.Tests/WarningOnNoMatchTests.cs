using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:mode warning-on-no-match="yes" reports each node processed in that mode with no matching
/// template rule, so an author can find what is falling through to the built-in rule. The
/// attribute was parsed, validated and then dropped: it produced no diagnostic at all
/// (W3C attr/mode mode-1427/1428/1440/1442).
/// </summary>
public sealed class WarningOnNoMatchTests
{
    private static async Task<(string Output, List<string> Warnings)> RunAsync(string modeAttributes)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:mode {modeAttributes}/>
              <xsl:template match="known">seen</xsl:template>
            </xsl:stylesheet>
            """;
        var warnings = new List<string>();
        var t = new XsltTransformer { WarningListener = warnings.Add };
        await t.LoadStylesheetAsync(ss);
        var output = await t.TransformAsync("<doc><known/><unknown/></doc>");
        return (output.Trim(), warnings);
    }

    [Theory]
    [InlineData("""warning-on-no-match="yes" """)]
    [InlineData("""warning-on-no-match="true" """)]
    [InlineData("""warning-on-no-match="1" """)]
    public async Task ANodeWithNoMatchingRule_IsReported(string modeAttributes)
    {
        var (_, warnings) = await RunAsync(modeAttributes);
        warnings.Should().NotBeEmpty();
        warnings.Should().Contain(w => w.Contains("No template rule matches", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("""warning-on-no-match="no" """)]
    public async Task WithoutTheAttribute_NothingIsReported(string modeAttributes)
        => (await RunAsync(modeAttributes)).Warnings.Should().BeEmpty();

    /// <summary>The warning is a diagnostic: the transform runs to completion regardless.</summary>
    [Fact]
    public async Task TheTransformStillRuns()
        => (await RunAsync("""warning-on-no-match="yes" """)).Output.Should().Be("seen");
}
