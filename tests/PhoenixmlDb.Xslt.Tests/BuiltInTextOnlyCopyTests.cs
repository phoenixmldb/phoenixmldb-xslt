using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The text-only-copy built-in rule recursing through elements no rule matches. Unmatched
/// children take a direct route (no per-node apply-templates), so these pin what a matched
/// descendant must still see: its position and size among ALL its parent's children (a wrong
/// position fails the first test) and the pattern-variable barrier (no barrier fails the second).
/// The third checks the output of adjacent atomic results; the serializer separates them on its
/// own, so it does not depend on the per-node atomic flag the route threads through.
/// </summary>
public class BuiltInTextOnlyCopyTests
{
    private static async Task<string> Run(string ss, string xml)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync(xml);
    }

    [Fact]
    public async Task Matched_child_under_unmatched_elements_sees_its_position_among_all_children()
    {
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:output method="text"/>
                <xsl:template match="b">[<xsl:value-of select="position()"/>/<xsl:value-of select="last()"/>]</xsl:template>
            </xsl:stylesheet>
            """, "<r><a>t1<b/>t2<!--c--><b/><c><b/></c></a></r>");
        // a's children: "t1", b, "t2", comment, b, c, so the b's are 2/6 and 5/6; c's only child is 1/1.
        r.Should().Be("t1[2/6]t2[5/6][1/1]");
    }

    [Fact]
    public async Task Pattern_variable_reached_through_next_match_built_in_sees_the_global()
    {
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:output method="text"/>
                <xsl:variable name="g" select="'global'"/>
                <xsl:template match="r">
                    <xsl:variable name="g" select="'local'"/>
                    <xsl:next-match/>
                </xsl:template>
                <xsl:template match="b[$g = 'global']">G</xsl:template>
            </xsl:stylesheet>
            """, "<r><a><b/></a></r>");
        r.Should().Be("G");
    }

    [Fact]
    public async Task Adjacent_atomic_results_of_matched_children_are_space_separated()
    {
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                xmlns:xs="http://www.w3.org/2001/XMLSchema">
                <xsl:template match="b" as="xs:integer"><xsl:sequence select="1"/></xsl:template>
                <xsl:template match="/"><out><xsl:apply-templates/></out></xsl:template>
            </xsl:stylesheet>
            """, "<r><a><b/><b/></a></r>");
        r.Should().Contain(">1 1</out>");
    }
}
