using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// suppress-indentation with the html method. An element in no namespace or the XHTML namespace
/// is an HTML element, matched without case; any other element is matched by its expanded QName.
/// The names were matched by local name only and with case, so "div" also suppressed x:div in
/// another namespace, and "DIV" missed the HTML div.
/// </summary>
public sealed class HtmlSuppressIndentationTests
{
    private const string Body = "<html><body><div><p>a</p></div><x:div xmlns:x='urn:x'><p>b</p></x:div></body></html>";

    private static async Task<string> RunAsync(string suppress)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(
            "<xsl:stylesheet xmlns:xsl='http://www.w3.org/1999/XSL/Transform' version='3.0' xmlns:x='urn:x' exclude-result-prefixes='x'>"
            + $"<xsl:output method='html' indent='yes' suppress-indentation='{suppress}'/><xsl:template match='/'>{Body}</xsl:template></xsl:stylesheet>");
        return await transformer.TransformAsync("<a/>");
    }

    [Theory]
    [InlineData("div")]
    [InlineData("DIV")]
    public async Task An_html_name_suppresses_the_html_element_only(string suppress)
        => (await RunAsync(suppress)).Should().Be(
            "<html>\n  <body>\n    <div><p>a</p></div><x:div xmlns:x=\"urn:x\">\n    <p>b</p></x:div>\n  </body>\n</html>");

    [Fact]
    public async Task A_prefixed_name_suppresses_the_element_of_that_namespace_only()
        => (await RunAsync("x:div")).Should().Be(
            "<html>\n  <body>\n    <div>\n      <p>a</p>\n    </div><x:div xmlns:x=\"urn:x\"><p>b</p></x:div>\n  </body>\n</html>");
}
