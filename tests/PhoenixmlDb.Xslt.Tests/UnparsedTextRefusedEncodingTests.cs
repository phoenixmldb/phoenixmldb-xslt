using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// .NET knows utf-7 by name and refuses to provide it, with NotSupportedException rather than
/// the ArgumentException an unknown name gives. That escaped the two-argument unparsed-text
/// functions raw: unparsed-text-available, which must answer true or false, threw.
/// </summary>
public sealed class UnparsedTextRefusedEncodingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"xslt-utf7-{Guid.NewGuid():N}");

    public UnparsedTextRefusedEncodingTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "ok.txt"), "abc");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> RunAsync(string select)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:value-of select="{select}"/></xsl:template>
            </xsl:stylesheet>
            """, baseUri: new Uri(Path.Combine(_dir, "main.xsl")));
        return await transformer.TransformAsync("<r/>");
    }

    [Fact]
    public async Task Available_answers_false_for_an_encoding_the_runtime_refuses()
    {
        (await RunAsync("unparsed-text-available('ok.txt', 'utf-7')")).Should().Be("false");
        (await RunAsync("unparsed-text-available('ok.txt', 'utf-8')")).Should().Be("true");
    }

    [Theory]
    [InlineData("unparsed-text('ok.txt', 'utf-7')")]
    [InlineData("unparsed-text-lines('ok.txt', 'utf-7')")]
    [InlineData("unparsed-text('ok.txt', 'no-such-encoding')")]
    public async Task Reading_with_such_an_encoding_is_FOUT1190(string select)
    {
        var run = async () => await RunAsync(select);
        (await run.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("FOUT1190");
    }
}
