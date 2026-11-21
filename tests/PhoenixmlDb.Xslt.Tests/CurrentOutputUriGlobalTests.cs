using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §25.3: the current output URI is cleared while evaluating global variables,
/// stylesheet parameters and patterns, so <c>current-output-uri()</c> is the empty sequence there.
/// It is not cleared for a local variable. With a base output URI set, a global variable or
/// parameter saw it, and so did a pattern (xslt#561; W3C current-output-uri-001, -008).
/// </summary>
public sealed class CurrentOutputUriGlobalTests
{
    private const string Base = "file:///results/out.xml";

    private static async Task<string> RunAsync(string declarations, string body)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {{declarations}}
              <xsl:template match="/">{{body}}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        t.SetBaseOutputUri(new Uri(Base));
        return (await t.TransformAsync("<doc><a/></doc>")).Trim();
    }

    [Fact]
    public async Task A_global_variable_sees_no_current_output_uri()
        => (await RunAsync(
                """<xsl:variable name="g" select="'[' || string(current-output-uri()) || ']'"/>""",
                """<xsl:value-of select="$g"/>"""))
            .Should().Be("[]");

    [Fact]
    public async Task A_global_variable_with_content_sees_no_current_output_uri()
        => (await RunAsync(
                """<xsl:variable name="g"><xsl:value-of select="'[' || string(current-output-uri()) || ']'"/></xsl:variable>""",
                """<xsl:value-of select="$g"/>"""))
            .Should().Be("[]");

    [Fact]
    public async Task A_stylesheet_parameter_default_sees_no_current_output_uri()
        => (await RunAsync(
                """<xsl:param name="p" select="'[' || string(current-output-uri()) || ']'"/>""",
                """<xsl:value-of select="$p"/>"""))
            .Should().Be("[]");

    /// <summary>
    /// A global first read inside xsl:result-document is evaluated there, on demand; it still sees
    /// none, and the result document's own current output URI is back once it is bound.
    /// </summary>
    [Fact]
    public async Task A_global_evaluated_on_demand_sees_none_and_the_caller_keeps_its_own()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:variable name="g" select="'[' || string(current-output-uri()) || ']'"/>
              <xsl:template match="/">
                <xsl:result-document href="second.txt" method="text">
                  <xsl:value-of select="(current-output-uri(), $g, current-output-uri())" separator=" "/>
                </xsl:result-document>
              </xsl:template>
            </xsl:stylesheet>
            """);
        t.SetBaseOutputUri(new Uri(Base));
        await t.TransformAsync("<doc/>");
        t.SecondaryResultDocuments["second.txt"].Trim()
            .Should().Be("file:///results/second.txt [] file:///results/second.txt");
    }

    [Fact]
    public async Task A_local_variable_keeps_the_current_output_uri()
        => (await RunAsync(
                "",
                """<xsl:variable name="l" select="string(current-output-uri())"/><xsl:value-of select="$l"/>"""))
            .Should().Be(Base);

    [Fact]
    public async Task A_template_rule_pattern_sees_no_current_output_uri()
        => (await RunAsync(
                """
                <xsl:template match="a[exists(current-output-uri())]">wrong</xsl:template>
                <xsl:template match="a[empty(current-output-uri())]">right</xsl:template>
                """,
                """<xsl:apply-templates select="doc/a"/>"""))
            .Should().Be("right");

    [Fact]
    public async Task A_grouping_pattern_sees_no_current_output_uri()
        => (await RunAsync(
                "",
                """<xsl:for-each-group select="-1 to 1" group-starting-with=".[. = count(current-output-uri())]">{<xsl:value-of select="current-group()"/>}</xsl:for-each-group>"""))
            .Should().Be("{-1}{0 1}");
}
