using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A fixed (non-AVT) lang on xsl:number must be a language code; an invalid one is the static
/// error XTSE0020, reported whether or not the instruction ever runs (W3C number-0825).
/// </summary>
public sealed class NumberLangStaticCheckTests
{
    private static async Task<string> RunAsync(string lang)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:number value="3" format="1" {lang}/></xsl:template>
              <xsl:template name="never"><xsl:number value="1" {lang}/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync("<doc/>");
    }

    [Theory]
    [InlineData("""lang="#####" """)]
    [InlineData("""lang="en_US" """)]
    public async Task InvalidLanguageCode_IsXTSE0020(string lang)
    {
        var act = () => RunAsync(lang);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTSE0020");
    }

    [Theory]
    [InlineData("""lang="en" """)]
    [InlineData("""lang="de-CH" """)]
    [InlineData("""lang="" """)]
    [InlineData("""lang="{'en'}" """)]
    public async Task ValidOrDynamicLang_IsAccepted(string lang)
        => (await RunAsync(lang)).Trim().Should().Be("3");
}
