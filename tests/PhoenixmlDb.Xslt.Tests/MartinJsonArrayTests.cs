using FluentAssertions;
using PhoenixmlDb.Xdm;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Martin Honnen 2026-06-12 (JSON-from-XSLT). Two issues:
/// (2) fn:parse-json of a top-level JSON ARRAY returned a flattened sequence instead of
///     an array(*) — the XSLT engine's XsltParseJsonFunction.ConvertArray returned object?[]
///     (a sequence the engine flattens) rather than List&lt;object?&gt; (the XDM array, a
///     single item). So parse-json($json)?* lost its members and a subsequent ?lookup hit a
///     string → XPTY0004.
/// (1) indent="yes" applied to output built from a JSON-map input fed as an XdmSequence.
/// </summary>
public sealed class MartinJsonArrayTests
{
    private static XsltTransformer InitialTemplateTransformer()
    {
        var t = new XsltTransformer();
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return t;
    }

    [Fact]
    public async Task ParseJson_TopLevelArray_IsArrayNotFlattenedSequence()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" expand-text="yes">
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">{count(parse-json('[1,2,3]'))}|{parse-json('[1,2,3]') instance of array(*)}|{parse-json('[1,2,3]')?2}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = InitialTemplateTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync((string?)null);
        r.Should().Be("1|true|2", because: "parse-json('[...]') must yield a single array(*), not a flattened 3-item sequence");
    }

    [Fact]
    public async Task ParseJson_ArrayGrouping_ProducesGroups_NoLookupError()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:mf="http://example.com/mf"
              exclude-result-prefixes="#all" expand-text="yes">
              <xsl:param name="json-sample" as="xs:string" expand-text="no">[{"name":"item 1","categories":["cat 1","cat 2"]},{"name":"item 2","categories":["cat 1","cat 3"]},{"name":"item 3","categories":["cat 2","cat 3"]},{"name":"item 4","categories":["cat 2","cat 4"]}]</xsl:param>
              <xsl:function name="mf:group" as="item()*">
                <xsl:param name="items" as="item()*"/>
                <xsl:param name="grouping-key-selector" as="function(item()) as item()"/>
                <xsl:for-each-group select="$items" group-by="$grouping-key-selector(.)">
                  <xsl:sequence select="map { 'category' : current-grouping-key(), 'items' : array { current-group()?name } }"/>
                </xsl:for-each-group>
              </xsl:function>
              <xsl:output method="json" indent="no"/>
              <xsl:template match="." name="xsl:initial-template">
                <xsl:sequence select="array { mf:group(parse-json($json-sample)?*, function($item) { $item?categories }) }"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = InitialTemplateTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync((string?)null);
        // Pre-fix this threw XPTY0004. Now it groups by category.
        r.Should().Contain("cat 1").And.Contain("cat 4");
        r.Should().Contain("item 1").And.Contain("item 4");
    }

    [Fact]
    public async Task Indent_AppliesToOutputBuiltFromJsonMapInput()
    {
        // Stage 1: parse a JSON object into a map carried in an XdmSequence (Martin's input).
        const string parseStage = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="adaptive"/>
              <xsl:param name="json" as="xs:string"/>
              <xsl:template name="xsl:initial-template"><xsl:sequence select="parse-json($json)"/></xsl:template>
            </xsl:stylesheet>
            """;
        var parser = InitialTemplateTransformer();
        await parser.LoadStylesheetAsync(parseStage);
        parser.SetParameter("json", """{ "name": "John", "age": 30, "city": "New York" }""");
        var map = await parser.TransformToSequenceAsync((XdmSequence?)null);
        map.Head.Should().BeAssignableTo<System.Collections.Generic.IDictionary<object, object?>>();

        // Stage 2: build <person> from the map with indent="yes" and serialize to a string.
        const string consumeStage = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:output method="xml" indent="yes"/>
              <xsl:template match="."><person><name>{?name}</name><age>{?age}</age><city>{?city}</city></person></xsl:template>
            </xsl:stylesheet>
            """;
        var consumer = new XsltTransformer();
        await consumer.LoadStylesheetAsync(consumeStage);
        var result = await consumer.TransformAsync(map);

        // indent="yes" must put each child on its own indented line, not all on one line.
        result.Should().Contain("\n  <name>John</name>",
            because: "indent='yes' must pretty-print children built from a JSON-map input");
        result.Should().Contain("\n  <city>New York</city>");
    }
}
