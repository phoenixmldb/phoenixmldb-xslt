using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:where-populated discards every item "deemed empty" (XSLT 3.0 §8.4): childless documents and elements, other nodes
/// and atomic values with a zero-length string value, and arrays whose members are all deemed empty. Only zero-length
/// attributes and xs:string values were discarded (W3C coco-003, -012, -103). Maps are exempt until #117: a streamed
/// xsl:map is still empty when the filter runs. The last tests are guards: populated items and nested content survive.
/// </summary>
public sealed class WherePopulatedDeemedEmptyTests
{
    private static async Task<string> RunAsync(string body)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/">{{body}}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc/>")).Trim();
    }

    [Fact]
    public async Task Zero_length_atomic_values_of_any_type_are_discarded()
        => (await RunAsync("""
                <out><xsl:where-populated><xsl:sequence select="23, '', xs:untypedAtomic(''), 0, xs:base64Binary('')"/></xsl:where-populated></out>
                """)).Should().Be("<out>23 0</out>");

    [Fact]
    public async Task A_childless_document_is_discarded()
        => (await RunAsync("""
                <xsl:variable name="t" as="document-node()?">
                  <xsl:where-populated><xsl:document><xsl:copy-of select="//nothing"/></xsl:document></xsl:where-populated>
                </xsl:variable>
                <out empty="{empty($t)}"/>
                """)).Should().Be("<out empty=\"true\"/>");

    // Maps are exempt from the rule until #117, so the guard is that a populated map survives.
    [Fact]
    public async Task A_populated_map_is_kept()
        => (await RunAsync("""
                <xsl:variable name="m" as="map(*)?">
                  <xsl:where-populated><xsl:map><xsl:map-entry key="'a'" select="1"/></xsl:map></xsl:where-populated>
                </xsl:variable>
                <out count="{count($m)}"/>
                """)).Should().Be("<out count=\"1\"/>");

    [Fact]
    public async Task Arrays_whose_members_are_all_empty_are_discarded()
        => (await RunAsync("""
                <xsl:variable name="a" as="item()*"><xsl:where-populated><xsl:sequence select="[]"/></xsl:where-populated></xsl:variable>
                <xsl:variable name="b" as="item()*"><xsl:where-populated><xsl:sequence select="[[], '']"/></xsl:where-populated></xsl:variable>
                <out a="{count($a)}" b="{count($b)}"/>
                """)).Should().Be("<out a=\"0\" b=\"0\"/>");

    [Fact]
    public async Task Populated_items_survive()
        => (await RunAsync("""
                <xsl:variable name="v" as="item()*">
                  <xsl:where-populated><xsl:sequence select="['x'], map{'k': 1}, 'y'"/><e>z</e></xsl:where-populated>
                </xsl:variable>
                <out count="{count($v)}"/>
                """)).Should().Be("<out count=\"4\"/>");

    [Fact]
    public async Task A_childless_element_inside_a_constructed_child_is_kept()
        => (await RunAsync("""
                <xsl:variable name="t"><t a="1"/></xsl:variable>
                <out><xsl:where-populated><g><xsl:sequence select="$t/t"/></g></xsl:where-populated></out>
                """)).Should().Be("<out><g><t a=\"1\"/></g></out>");
}
