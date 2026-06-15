using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Martin Honnen 2026-06-14: parse JSON to an XdmSequence (TransformToSequenceAsync),
/// then feed it to a json-output identity stylesheet via TransformAsync(XdmSequence).
/// The map/array result must serialize as JSON, not as its CLR type name.
/// </summary>
public class MartinJsonSequenceRoundTripTests
{
    private const string ParseStylesheet = """
        <xsl:stylesheet version="3.0"
            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
            xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xsl:param name="j" as="xs:string"/>
          <xsl:template match="/"><xsl:sequence select="parse-json($j)"/></xsl:template>
        </xsl:stylesheet>
        """;

    private const string JsonIdentity = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
          <xsl:output method="json" indent="yes"/>
          <xsl:template match=".">
            <xsl:sequence select="."/>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private static async System.Threading.Tasks.Task<string> RoundTripAsync(string json, string identityStylesheet)
    {
        var parser = new XsltTransformer();
        await parser.LoadStylesheetAsync(ParseStylesheet);
        parser.SetParameter("j", json);
        var seq = await parser.TransformToSequenceAsync(null);

        var ident = new XsltTransformer();
        await ident.LoadStylesheetAsync(identityStylesheet);
        return await ident.TransformAsync(seq);
    }

    [Fact]
    public async Task JsonObjectArray_input_serializes_as_json_not_typename()
    {
        var json = """
            [
              { "name": "item 1", "categories": [ "cat 1", "cat 2" ] },
              { "name": "item 2", "categories": [ "cat 1", "cat 3" ] }
            ]
            """;
        var result = await RoundTripAsync(json, JsonIdentity);

        result.Should().NotContain("OrderedXdmMap");
        result.Should().NotContain("System.Object[]");
        result.Should().Contain("item 1");
        result.Should().Contain("cat 3");
    }

    [Fact]
    public async Task LookupAll_over_json_array_serializes_members_as_json()
    {
        // Martin: `?*` over a JSON array previously came back as a plain System.Object[].
        var json = """
            [
              { "name": "item 1", "categories": [ "cat 1", "cat 2" ] },
              { "name": "item 2", "categories": [ "cat 1", "cat 3" ] }
            ]
            """;
        const string lookupAll = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="json" indent="yes"/>
              <xsl:template match="."><xsl:sequence select="?*"/></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await RoundTripAsync(json, lookupAll);

        result.Should().NotContain("System.Object[]");
        result.Should().NotContain("OrderedXdmMap");
        result.Should().Contain("item 1");
        result.Should().Contain("item 2");
    }

    [Fact]
    public async Task JsonObject_input_serializes_as_json_not_typename()
    {
        var json = """{ "name": "item 1", "value": 42 }""";
        var result = await RoundTripAsync(json, JsonIdentity);

        result.Should().NotContain("OrderedXdmMap");
        result.Should().Contain("item 1");
        result.Should().Contain("42");
    }
}
