using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An xml:base in a stylesheet module that is not a URI is a static error, XTSE0020; it left
/// the transformer as a UriFormatException. <c>file:/path</c> (one slash) is a URI, and is
/// read as <c>file:///path</c>.
/// </summary>
public sealed class InvalidXmlBaseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-xml-base").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Head = "<xsl:stylesheet xmlns:xsl='http://www.w3.org/1999/XSL/Transform' version='3.0'><xsl:output omit-xml-declaration='yes'/>";

    private async Task<string> RunAsync(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Head + body + "</xsl:stylesheet>", new Uri(Path.Combine(_dir, "main.xsl")));
        return await t.TransformAsync("<x/>");
    }

    [Theory]
    [InlineData("<xsl:import xml:base='http://[not-a-host/' href='x.xsl'/><xsl:template match='/'>ok</xsl:template>")]
    [InlineData("<xsl:template match='/'><out xml:base='http://[not-a-host/'/></xsl:template>")]
    public async Task An_xml_base_that_is_not_a_URI_is_XTSE0020(string body)
    {
        var act = () => RunAsync(body);
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().StartWith("XTSE0020");
    }

    [Fact]
    public async Task A_one_slash_file_URI_is_read_as_a_file_URI()
    {
        var module = Path.Combine(_dir, "lib", "x.xsl");
        Directory.CreateDirectory(Path.GetDirectoryName(module)!);
        await File.WriteAllTextAsync(module, Head + "<xsl:template name='t'>from-x</xsl:template></xsl:stylesheet>", TestContext.Current.CancellationToken);
        var oneSlash = "file:" + new Uri(Path.Combine(_dir, "lib") + "/").AbsolutePath;
        (await RunAsync($"<xsl:import xml:base='{oneSlash}' href='x.xsl'/><xsl:template match='/'><xsl:call-template name='t'/></xsl:template>"))
            .Should().Be("from-x");
    }
}
