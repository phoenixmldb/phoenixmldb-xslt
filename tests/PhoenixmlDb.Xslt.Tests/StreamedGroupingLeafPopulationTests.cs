using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Streamed xsl:for-each-group over a population that is not the context element's child
/// elements: attribute nodes (<c>transaction/@date</c>) or text nodes (<c>ITEM/PRICE/text()</c>).
/// Grouping on <c>.</c> atomizes a leaf, which is motionless, but the checker rejected it as
/// XTSE3430; once admitted, the streamed loop walked the child elements and grouped the wrong
/// nodes (W3C si-group-024/025/033).
/// </summary>
public sealed class StreamedGroupingLeafPopulationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-sgl-" + Guid.NewGuid().ToString("N"));

    public StreamedGroupingLeafPopulationTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "t.xml"), """
            <account>
              <transaction date="d1" value="1"/><transaction date="d1" value="2"/>
              <transaction date="d2" value="3"/>
              <ITEM><PRICE>5</PRICE></ITEM><ITEM><PRICE>5</PRICE></ITEM><ITEM><PRICE>7</PRICE></ITEM>
            </account>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> Run(string select)
    {
        var xsl = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode name="s" streamable="yes"/>
              <xsl:template name="main">
                <xsl:source-document streamable="yes" href="t.xml"><xsl:apply-templates select="account" mode="s"/></xsl:source-document>
              </xsl:template>
              <xsl:template match="account" mode="s">
                <out><xsl:for-each-group select="{select}" group-adjacent="."><g k="{"{"}current-grouping-key(){"}"}" n="{"{"}count(current-group()){"}"}"/></xsl:for-each-group></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task Attribute_population_groups_on_the_attribute_values() =>
        (await Run("transaction/@date")).Should().Contain("<g k=\"d1\" n=\"2\"/><g k=\"d2\" n=\"1\"/>");

    [Fact]
    public async Task Text_population_groups_on_the_text_values() =>
        (await Run("ITEM/PRICE/text()")).Should().Contain("<g k=\"5\" n=\"2\"/><g k=\"7\" n=\"1\"/>");
}
