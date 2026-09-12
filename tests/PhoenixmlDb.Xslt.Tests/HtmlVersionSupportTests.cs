using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// SESU0013: the html and xhtml output methods take a version this processor can serialize. An
/// unsupported one was accepted in silence and the result serialized as if nothing had been asked
/// for, so a stylesheet requesting a version we cannot produce got output that merely looked fine
/// (W3C decl/output output-0194).
/// </summary>
public sealed class HtmlVersionSupportTests
{
    private static async Task<string> RunAsync(string outputAttributes)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output {outputAttributes}/>
              <xsl:template match="/"><html><body>hello</body></html></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync("<doc/>");
    }

    [Theory]
    [InlineData("""method="html" version="0.0" """)]
    [InlineData("""method="html" version="3.2" """)]
    [InlineData("""method="xhtml" version="2.0" """)]
    public async Task AnUnsupportedVersion_IsSESU0013(string outputAttributes)
    {
        var act = () => RunAsync(outputAttributes);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("SESU0013");
    }

    [Theory]
    [InlineData("""method="html" version="4.0" """)]
    [InlineData("""method="html" version="4.01" """)]
    [InlineData("""method="html" version="5.0" """)]
    [InlineData("""method="html" version="5" """)]
    [InlineData("""method="xhtml" version="1.0" """)]
    [InlineData("""method="xhtml" version="1.1" """)]
    [InlineData("""method="html" """)]
    [InlineData("""method="xml" version="1.0" """)]
    public async Task ASupportedVersion_Serializes(string outputAttributes)
        => (await RunAsync(outputAttributes)).Should().Contain("hello");
}
