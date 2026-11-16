using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An XHTML <c>html</c> document element makes xhtml the default output method, unless the
/// principal stylesheet module's version is 1.0: then the method stays xml (XSLT 3.0 §26.1,
/// W3C backwards-019; Saxon-HE 12.10 does the same). We used xhtml, which adds a Content-Type
/// meta element and escapes URI attributes.
/// </summary>
public sealed class Xslt10XhtmlDefaultMethodTests
{
    private const string Html = """<html xmlns="http://www.w3.org/1999/xhtml"><head><title>T</title></head><body><a href="a b.html">x</a></body></html>""";

    private static async Task<string> Run(string version, string body = Html)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="{{version}}" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">{{body}}</xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync("<doc/>");
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1")]
    public async Task A_version_1_stylesheet_keeps_the_xml_method(string version)
    {
        var result = await Run(version);
        result.Should().NotContain("<meta").And.Contain("href=\"a b.html\"");
    }

    /// <summary>The rule is for an implicit result; an explicit xsl:result-document keeps xhtml (backwards-019b).</summary>
    [Fact]
    public async Task An_explicit_result_document_in_a_version_1_stylesheet_uses_xhtml()
        => (await Run("1.0", $"<xsl:result-document>{Html}</xsl:result-document>")).Should().Contain("<meta http-equiv=\"Content-Type\"");

    [Theory]
    [InlineData("2.0")]
    [InlineData("3.0")]
    public async Task A_later_version_uses_the_xhtml_method(string version)
        => (await Run(version)).Should().Contain("<meta http-equiv=\"Content-Type\"");
}
