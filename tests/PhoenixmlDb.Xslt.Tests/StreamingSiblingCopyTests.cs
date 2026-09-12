using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Under a streamable mode, a matched template that copies its element and applies templates to
/// its children produces siblings as siblings. The dispatch helper materialises the WHOLE element
/// and leaves the reader on its end tag, but the apply-templates in the body kept driving the
/// reader — so it consumed the element's FOLLOWING SIBLINGS as if they were its children:
/// &lt;book&gt;&lt;bktlong&gt;&lt;bktshort&gt;…, with book never closed (W3C attr/mode mode-1418).
/// </summary>
public sealed class StreamingSiblingCopyTests
{
    private const string Source = "<book><bktlong>long text</bktlong><bktshort>short</bktshort></book>";

    private static async Task<string> RunAsync(string streamable, bool streamed)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:mode name="s" on-no-match="shallow-skip" streamable="{streamable}"/>
              <xsl:template match="book|bktlong|bktshort" mode="s">
                <xsl:copy><xsl:apply-templates mode="s"/></xsl:copy>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        t.SetInitialMode("s");
        return (streamed ? await t.TransformAsync(new StringReader(Source)) : await t.TransformAsync(Source)).Trim();
    }

    [Fact]
    public async Task StreamedSiblings_StaySiblings()
        => (await RunAsync("yes", streamed: true)).Should().Be("<book><bktlong/><bktshort/></book>",
            "the streamed run nested bktshort inside bktlong and never closed book");

    [Fact]
    public async Task TheUnstreamedRun_IsUnchanged()
        => (await RunAsync("no", streamed: false)).Should().Be("<book><bktlong/><bktshort/></book>");

    /// <summary>Nested elements still nest — the fix must not flatten a real hierarchy.</summary>
    [Fact]
    public async Task StreamedNesting_IsPreserved()
    {
        var ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:mode name="s" on-no-match="shallow-skip" streamable="yes"/>
              <xsl:template match="a|b|c" mode="s"><xsl:copy><xsl:apply-templates mode="s"/></xsl:copy></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        t.SetInitialMode("s");
        (await t.TransformAsync(new StringReader("<a><b><c/></b><b/></a>"))).Trim()
            .Should().Be("<a><b><c/></b><b/></a>");
    }
}
