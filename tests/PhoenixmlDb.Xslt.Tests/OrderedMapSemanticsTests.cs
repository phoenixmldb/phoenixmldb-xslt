using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Locks XSLT 4.0 ordered-map semantics: xsl:map / map constructors iterate in
/// entry/insertion order. XSLT 3.0 left this unspecified; 4.0 makes it a contract.
/// These tests serialize maps as JSON and assert the keys appear in insertion
/// order, so a reordering regression fails loudly.
/// </summary>
public class OrderedMapSemanticsTests
{
    private static async Task<string> JsonOf(string mapBody)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:map="http://www.w3.org/2005/xpath-functions/map">
              <xsl:output method="json"/>
              <xsl:template match="/">
                {{mapBody}}
              </xsl:template>
            </xsl:stylesheet>
            """);
        return await transformer.TransformAsync("<root/>");
    }

    private static void AssertKeyOrder(string json, params string[] keysInOrder)
    {
        var positions = keysInOrder
            .Select(k => (key: k, pos: json.IndexOf('"' + k + '"', System.StringComparison.Ordinal)))
            .ToList();
        foreach (var (key, pos) in positions)
            pos.Should().BeGreaterThanOrEqualTo(0, $"key '{key}' should appear in: {json}");
        for (var i = 1; i < positions.Count; i++)
            positions[i].pos.Should().BeGreaterThan(positions[i - 1].pos,
                $"'{positions[i].key}' should follow '{positions[i - 1].key}' in: {json}");
    }

    [Fact]
    public async Task Xsl_map_preserves_entry_order_not_sorted()
    {
        var json = await JsonOf("""
            <xsl:map>
              <xsl:map-entry key="'zebra'" select="1"/>
              <xsl:map-entry key="'apple'" select="2"/>
              <xsl:map-entry key="'mango'" select="3"/>
            </xsl:map>
            """);
        AssertKeyOrder(json, "zebra", "apple", "mango");
    }

    [Fact]
    public async Task Map_constructor_expression_preserves_order()
    {
        var json = await JsonOf("""
            <xsl:sequence select="map { 'gamma': 1, 'alpha': 2, 'beta': 3 }"/>
            """);
        AssertKeyOrder(json, "gamma", "alpha", "beta");
    }

    [Fact]
    public async Task Map_put_new_key_appends_at_end()
    {
        var json = await JsonOf("""
            <xsl:sequence select="map:put(map { 'a': 1, 'b': 2 }, 'c', 3)"/>
            """);
        AssertKeyOrder(json, "a", "b", "c");
    }

    [Fact]
    public async Task Map_put_existing_key_preserves_position()
    {
        var json = await JsonOf("""
            <xsl:sequence select="map:put(map { 'a': 1, 'b': 2, 'c': 3 }, 'a', 99)"/>
            """);
        AssertKeyOrder(json, "a", "b", "c");
    }

    [Fact]
    public async Task Map_remove_then_put_new_key_appends_at_end()
    {
        var json = await JsonOf("""
            <xsl:sequence select="map:put(map:remove(map { 'a':1, 'b':2, 'c':3, 'd':4 }, 'b'), 'e', 5)"/>
            """);
        AssertKeyOrder(json, "a", "c", "d", "e");
    }

    [Fact]
    public async Task Grouping_builds_map_in_first_seen_order()
    {
        // The use case Martin described: grouping with map target should emit groups
        // in the order their first member appears (insertion order), like the
        // XML-target version did.
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:map="http://www.w3.org/2005/xpath-functions/map">
              <xsl:output method="json"/>
              <xsl:template match="/">
                <xsl:variable name="m" as="map(*)">
                  <xsl:map>
                    <xsl:for-each-group select="items/i" group-by="@cat">
                      <xsl:map-entry key="string(current-grouping-key())"
                                     select="count(current-group())"/>
                    </xsl:for-each-group>
                  </xsl:map>
                </xsl:variable>
                <xsl:sequence select="$m"/>
              </xsl:template>
            </xsl:stylesheet>
            """);
        var json = await transformer.TransformAsync("""
            <items>
              <i cat="fruit">apple</i>
              <i cat="veg">carrot</i>
              <i cat="fruit">banana</i>
              <i cat="grain">rice</i>
              <i cat="veg">pea</i>
            </items>
            """);
        AssertKeyOrder(json, "fruit", "veg", "grain");
    }
}
