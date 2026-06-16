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

    private const string ParseStylesheetNamedInit = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema">
          <xsl:output method="adaptive"/>
          <xsl:param name="j" as="xs:string"/>
          <xsl:template name="xsl:initial-template"><xsl:sequence select="parse-json($j)"/></xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task CountDot_over_json_array_is_one_not_per_member()
    {
        // Martin Honnen: <xsl:template match="." name="xsl:initial-template">
        //   <xsl:sequence select="count(.)"/> over a parsed JSON array gave [1,1,1,1]
        // (template fired once per member). Saxon gives 1. Must be a single "1".
        var json = """[ {"name":"item 1"},{"name":"item 2"},{"name":"item 3"},{"name":"item 4"} ]""";
        var parser = new XsltTransformer();
        await parser.LoadStylesheetAsync(ParseStylesheetNamedInit);
        parser.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        parser.SetParameter("j", json);
        var seq = await parser.TransformToSequenceAsync(null);

        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="json" indent="yes"/>
              <xsl:template match="." name="xsl:initial-template"><xsl:sequence select="count(.)"/></xsl:template>
            </xsl:stylesheet>
            """);
        (await t.TransformAsync(seq)).Trim().Should().Be("1");
    }

    [Fact]
    public async Task ApplyTemplates_over_json_array_treats_array_as_single_item()
    {
        // Martin Honnen 2026-06-14: the parsed array fed as the initial context item to a
        // named xsl:initial-template (match=".") was iterated as its 4 members — apply-templates
        // flattened the List<object?> array — so ?* / lookups operated on a single map.
        var json = """
            [
              { "name": "item 1", "categories": [ "cat 1", "cat 2" ] },
              { "name": "item 2", "categories": [ "cat 1", "cat 3" ] },
              { "name": "item 3", "categories": [ "cat 2", "cat 3" ] },
              { "name": "item 4", "categories": [ "cat 2", "cat 4" ] }
            ]
            """;
        const string group = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:mf="http://example.com/mf"
              exclude-result-prefixes="#all" expand-text="yes">
              <xsl:function name="mf:group" as="item()*">
                <xsl:param name="items" as="item()*"/>
                <xsl:param name="grouping-key-selector" as="function(item()) as item()"/>
                <xsl:for-each-group select="$items" group-by="$grouping-key-selector(.)">
                  <xsl:sequence select="map { 'category' : current-grouping-key(), 'items' : array { current-group()?name } }"/>
                </xsl:for-each-group>
              </xsl:function>
              <xsl:output method="json" indent="no"/>
              <xsl:template match="." name="xsl:initial-template">
                <xsl:sequence select="array { mf:group(?*, function($item) { $item?categories }) }"/>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var parser = new XsltTransformer();
        await parser.LoadStylesheetAsync(ParseStylesheetNamedInit);
        parser.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        parser.SetParameter("j", json);
        var seq = await parser.TransformToSequenceAsync(null);

        var grouper = new XsltTransformer();
        await grouper.LoadStylesheetAsync(group);
        var result = await grouper.TransformAsync(seq);

        result.Should().NotContain("Lookup requires");
        result.Should().NotContain("OrderedXdmMap");
        // Each item appears under each of its categories.
        result.Should().Contain("\"category\":\"cat 1\"").And.Contain("\"category\":\"cat 4\"");
        result.Should().Contain("item 1").And.Contain("item 4");
    }

    [Fact]
    public async Task ForEach_over_array_iterates_once_treating_array_as_one_item()
    {
        // Sweep guard: an XDM array is a single item, so for-each iterates once with
        // the context item being the whole array (not once per member).
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="a" select="[1, 2, 3, 4]"/>
                <xsl:for-each select="$a">iter[count={count(.)},arr={. instance of array(*)}];</xsl:for-each>
              </xsl:template>
            </xsl:stylesheet>
            """);
        var r = await t.TransformAsync("<x/>");
        r.Should().Be("iter[count=1,arr=true];");
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
