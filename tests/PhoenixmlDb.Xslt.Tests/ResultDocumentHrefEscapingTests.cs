using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xsl:result-document/@href</c> is a URI reference in the lexical space of xs:anyURI, so it may
/// hold characters a URI may not, and is resolved after they are escaped (XSLT 3.0 §5.8). The
/// destination that <c>current-output-uri()</c> reports is that escaped absolute URI, while the
/// result document keeps the href as written, which names the file. It reported
/// <c>…/result-[1].xml</c>, and an href already escaped (<c>%5B</c>) was escaped a second time
/// (<c>%255B</c>) (xslt#562). Accepting the href is right; no error is raised.
/// </summary>
public sealed class ResultDocumentHrefEscapingTests
{
    // Made from a local path, as the CLI makes it from -o: System.Uri escapes the "%" of an escape
    // again when it resolves against such a URI, and not against one parsed from "file:///…".
    private static readonly string Directory = Path.Combine(Path.GetTempPath(), "results");
    private static readonly Uri Base = new(Path.Combine(Directory, "out.xml"));
    private static readonly string DirectoryUri = new Uri(Directory + Path.DirectorySeparatorChar).AbsoluteUri;

    private static async Task<(string Uri, IReadOnlyCollection<string> Hrefs)> RunAsync(string href)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:param name="href"/>
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:result-document href="{$href}" method="text"><xsl:value-of select="current-output-uri()"/></xsl:result-document>
              </xsl:template>
            </xsl:stylesheet>
            """);
        t.SetBaseOutputUri(Base);
        t.SetParameter("href", href);
        await t.TransformAsync("<doc/>");
        var documents = t.SecondaryResultDocuments;
        return (documents.Values.Single().Trim(), documents.Keys.ToList());
    }

    [Theory]
    // the issue as reported
    [InlineData("result-[1].xml", "~/result-%5B1%5D.xml")]
    // an escape already in the href is kept, not escaped again
    [InlineData("result-%5B1%5D.xml", "~/result-%5B1%5D.xml")]
    [InlineData("100%25.xml", "~/100%25.xml")]
    // a query and a fragment are kept as such, their brackets escaped
    [InlineData("q?a=[1]#f[2]", "~/q?a=%5B1%5D#f%5B2%5D")]
    // brackets stay in the host of an authority, the one place they are allowed
    [InlineData("http://[::1]/a[1].xml", "http://[::1]/a%5B1%5D.xml")]
    public async Task The_reported_uri_is_the_escaped_absolute_uri(string href, string expected)
    {
        var (uri, hrefs) = await RunAsync(href);
        uri.Should().Be(expected.StartsWith('~') ? DirectoryUri + expected[2..] : expected);
        // The document is kept under the href as written: that is the name of the file.
        hrefs.Should().Equal(href);
    }

    /// <summary>Escaped before as now (System.Uri did it); a guard for the escaping now done first.</summary>
    [Theory]
    [InlineData("a b.xml", "~/a%20b.xml")]
    [InlineData("café.xml", "~/caf%C3%A9.xml")]
    [InlineData("100%.xml", "~/100%25.xml")]
    [InlineData("x{y}.xml", "~/x%7By%7D.xml")]
    public async Task A_space_a_non_ASCII_character_and_a_lone_percent_are_escaped(string href, string expected)
    {
        var (uri, hrefs) = await RunAsync(href);
        uri.Should().Be(expected.StartsWith('~') ? DirectoryUri + expected[2..] : expected);
        hrefs.Should().Equal(href);
    }
}
