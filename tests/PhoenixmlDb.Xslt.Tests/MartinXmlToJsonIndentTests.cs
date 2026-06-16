using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Martin Honnen 2026-06-15: fn:xml-to-json(., map { 'indent' : true() }) must indent
/// the JSON output. PhoenixmlDb 1.4.8 validated but ignored the 'indent' option and
/// emitted everything on one line.
/// </summary>
public class MartinXmlToJsonIndentTests
{
    private static async System.Threading.Tasks.Task<string> RunAsync(string indentExpr)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns="http://www.w3.org/2005/xpath-functions"
                exclude-result-prefixes="#all" expand-text="yes">
              <xsl:output method="text"/>
              <xsl:template match="/" name="xsl:initial-template">
                <xsl:variable name="json-xml">
                  <map>
                    <string key="title">hello</string>
                    <array key="properties">
                      <map><string key="a">1</string></map>
                      <map><string key="b">2</string></map>
                    </array>
                  </map>
                </xsl:variable>
                <xsl:value-of select="$json-xml => xml-to-json({{indentExpr}})"/>
              </xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync("<in/>");
    }

    [Fact]
    public async Task IndentTrue_produces_multiline_indented_json()
    {
        var r = await RunAsync("map { 'indent' : true() }");
        r.Should().Contain("\n", because: "indent:true must produce multi-line output");
        r.Should().Contain("\"title\": \"hello\"", because: "indented JSON uses 'key': value spacing");
        // Content is preserved exactly (no member dropped / reordered).
        r.Should().Contain("\"a\": \"1\"").And.Contain("\"b\": \"2\"");
    }

    [Fact]
    public async Task IndentFalse_stays_single_line()
    {
        var r = (await RunAsync("map { 'indent' : false() }")).Trim();
        r.Should().NotContain("\n");
        r.Should().Contain("\"title\":\"hello\"");
    }

    [Fact]
    public async Task IndentTrue_is_valid_round_trippable_json()
    {
        var r = await RunAsync("map { 'indent' : true() }");
        // The indented output must still parse as the same JSON structure.
        using var doc = System.Text.Json.JsonDocument.Parse(r);
        doc.RootElement.GetProperty("title").GetString().Should().Be("hello");
        doc.RootElement.GetProperty("properties").GetArrayLength().Should().Be(2);
    }
}
