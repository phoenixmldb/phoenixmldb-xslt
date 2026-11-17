using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §11.3: "Where the sequence contains two or more attribute nodes with the same
/// expanded QName, the one that comes last is the only one that takes effect." The built-in
/// shallow-copy wrote both, and every route compared lexical names, so two prefixes for one
/// namespace both stayed; either way the output was not (namespace-)well-formed (xslt#551).
/// Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class DuplicateAttributeTests
{
    private const string Input = """<r><m a="1" b="2"/></r>""";

    private static async Task<string> RunAsync(string rules, bool disableStreaming = false)
    {
        var t = new XsltTransformer { DisableStreaming = disableStreaming };
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:u" xmlns:q="urn:u" exclude-result-prefixes="#all">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:attribute-set name="s"><xsl:attribute name="a" select="'set'"/></xsl:attribute-set>
              {rules}
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync(Input)).Trim();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_built_in_shallow_copy_keeps_the_last_of_two_attribute_templates(bool streamable)
        => (await RunAsync($"""
                <xsl:mode on-no-match="shallow-copy" streamable="{(streamable ? "yes" : "no")}"/>
                <xsl:template match="@*"><xsl:attribute name="Z" select="name()"/></xsl:template>
                """)).Should().Be("""<r><m Z="b"></m></r>""");

    [Fact]
    public async Task The_built_in_shallow_copy_replaces_a_copied_attribute()
        => (await RunAsync("""
                <xsl:mode on-no-match="shallow-copy"/>
                <xsl:template match="@b"><xsl:attribute name="a" select="'from-b'"/></xsl:template>
                """)).Should().Be("""<r><m a="from-b"></m></r>""");

    [Fact]
    public async Task Two_prefixes_for_one_namespace_are_one_attribute()
        => (await RunAsync("""<xsl:template match="/"><e p:a="1"><xsl:attribute name="q:a" select="2"/></e></xsl:template>"""))
            .Should().Contain("""q:a="2""").And.NotContain("p:a");

    // The routes that already kept the last attribute by its lexical name: guards.
    [Theory]
    [InlineData("""<xsl:template match="/"><e a="1"><xsl:attribute name="a" select="2"/><xsl:attribute name="a" select="3"/></e></xsl:template>""", """<e a="3"/>""")]
    [InlineData("""<xsl:template match="/"><xsl:element name="e"><xsl:attribute name="a" select="1"/><xsl:attribute name="a" select="2"/></xsl:element></xsl:template>""", """<e a="2"/>""")]
    [InlineData("""<xsl:template match="m"><xsl:copy><xsl:copy-of select="@a"/><xsl:attribute name="a" select="2"/></xsl:copy></xsl:template><xsl:template match="/"><xsl:apply-templates select="//m"/></xsl:template>""", """<m a="2"/>""")]
    [InlineData("""<xsl:template match="/"><f xsl:use-attribute-sets="s s" a="lre"/></xsl:template>""", """<f a="lre"/>""")]
    [InlineData("""<xsl:template match="/"><e><xsl:sequence select="//@a, //@a"/></e></xsl:template>""", """<e a="1"/>""")]
    public async Task Each_route_keeps_the_last_attribute_of_a_name(string rules, string expected)
        => (await RunAsync(rules)).Should().Be(expected);
}
