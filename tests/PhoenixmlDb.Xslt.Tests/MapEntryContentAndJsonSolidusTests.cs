using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <list type="bullet">
/// <item>xsl:map-entry content that constructs nodes gives the nodes (XSLT 3.0 §21.1.2):
/// <c>&lt;xsl:map-entry key="'k'"&gt;&lt;e/&gt;&lt;/xsl:map-entry&gt;</c> is an element, in the
/// namespaces it was written in. It was a string, the markup, so json-node-output-method did
/// not apply to it (W3C output-0702).</item>
/// <item>The json output method writes a solidus as <c>\/</c>, as PhoenixmlDb.XQuery's
/// serializer does and QT3 expects, unless a character map maps it (W3C output-0708).</item>
/// </list>
/// </summary>
public sealed class MapEntryContentAndJsonSolidusTests
{
    private static async Task<string> Run(string output, string body, string extra = "")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              {{output}}
              {{extra}}
              <xsl:template name="xsl:initial-template">{{body}}</xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    private const string MapWithContent = """
        <xsl:variable name="m" as="map(*)"><xsl:map>
          <xsl:map-entry key="'k'"><e xmlns="urn:d"><f/></e></xsl:map-entry>
          <xsl:map-entry key="'t'">just text</xsl:map-entry>
        </xsl:map></xsl:variable>
        """;

    [Theory]
    [InlineData("$m?k instance of element()", "true")]
    [InlineData("$m?k instance of xs:string", "false")]
    [InlineData("namespace-uri($m?k)", "urn:d")]
    [InlineData("count($m?k/*)", "1")]
    [InlineData("$m?t", "just text")]
    public async Task Map_entry_content_gives_the_nodes_it_constructs(string select, string expected)
        => (await Run("<xsl:output method='text'/>", MapWithContent + $"<xsl:value-of select=\"{select}\"/>")).Should().Be(expected);

    [Fact]
    public async Task A_map_entry_element_gets_the_json_node_output_method()
        => (await Run("<xsl:output method='json' json-node-output-method='html'/>",
                "<xsl:map><xsl:map-entry key=\"'d'\"><html><head><title>T</title></head></html></xsl:map-entry></xsl:map>"))
            .Should().Be("{\"d\":\"<html><head><meta http-equiv=\\\"Content-Type\\\" content=\\\"text\\/html; charset=UTF-8\\\"><title>T<\\/title><\\/head><\\/html>\"}");

    [Fact]
    public async Task The_json_method_escapes_a_solidus()
        => (await Run("<xsl:output method='json'/>", "<xsl:sequence select=\"map{'a': 'x/y'}\"/>"))
            .Should().Be("{\"a\":\"x\\/y\"}");

    [Fact]
    public async Task A_character_map_that_maps_the_solidus_keeps_it()
        => (await Run("<xsl:output method='json' use-character-maps='m'/>", "<xsl:sequence select=\"map{'a': 'x/y'}\"/>",
                "<xsl:character-map name='m'><xsl:output-character character='/' string='/'/></xsl:character-map>"))
            .Should().Be("{\"a\":\"x/y\"}");

    [Theory]
    [InlineData("'x/y'", "x/y")]
    [InlineData("map{'a/b': 'c'}", "/b")]
    public async Task The_adaptive_method_does_not_escape_it(string select, string expected)
        => (await Run("<xsl:output method='adaptive'/>", $"<xsl:sequence select=\"{select}\"/>")).Should().Contain(expected).And.NotContain("\\/");

    [Fact]
    public async Task A_json_result_document_escapes_it_too()
        => (await Run("<xsl:output method='json' name='j'/>",
                "<xsl:result-document format='j'><xsl:sequence select=\"map{'a': 'x/y'}\"/></xsl:result-document>"))
            .Should().Be("{\"a\":\"x\\/y\"}");
}
