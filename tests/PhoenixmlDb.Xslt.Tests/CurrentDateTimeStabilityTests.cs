using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// fn:current-dateTime, fn:current-date and fn:current-time must return the same value throughout one transformation
/// (F&amp;O 3.1 §16.6). The engine built a fresh XQuery context, with its own clock read, for each XPath evaluation, so
/// two calls in different expressions of one transformation differed by the time between them (xslt#107). The
/// separate-transformation test is a guard: each transformation takes its own snapshot.
/// </summary>
public sealed class CurrentDateTimeStabilityTests
{
    // Enough work between the two reads that separate clock reads cannot land on the same tick.
    private const string Busy = "sum(for $i in 1 to 300000 return $i mod 7)";

    private static async Task<string> RunAsync(string templates)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {{templates}}
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc/>")).Trim();
    }

    [Fact]
    public async Task Current_dateTime_is_the_same_in_separate_expressions_across_templates()
    {
        var output = await RunAsync($$"""
            <xsl:template match="/">
              <xsl:variable name="first" select="current-dateTime()"/>
              <xsl:variable name="busy" select="{{Busy}}"/>
              <xsl:value-of select="$busy ge 0"/>
              <xsl:text>|</xsl:text>
              <xsl:call-template name="later"><xsl:with-param name="first" select="$first"/></xsl:call-template>
            </xsl:template>
            <xsl:template name="later">
              <xsl:param name="first" as="xs:dateTime"/>
              <xsl:value-of select="current-dateTime() eq $first"/>
            </xsl:template>
            """);
        output.Should().Be("true|true");
    }

    [Fact]
    public async Task Current_date_and_time_agree_with_current_dateTime()
    {
        var output = await RunAsync($$"""
            <xsl:template match="/">
              <xsl:variable name="dt" select="current-dateTime()"/>
              <xsl:variable name="busy" select="{{Busy}}"/>
              <xsl:value-of select="($busy ge 0) and current-date() eq xs:date($dt) and current-time() eq xs:time($dt)"/>
            </xsl:template>
            """);
        output.Should().Be("true");
    }

    [Fact]
    public async Task A_current_dateTime_function_item_returns_the_same_value()
    {
        var output = await RunAsync($$"""
            <xsl:template match="/">
              <xsl:variable name="first" select="current-dateTime()"/>
              <xsl:variable name="busy" select="{{Busy}}"/>
              <xsl:variable name="f" select="current-dateTime#0"/>
              <xsl:value-of select="($busy ge 0) and $f() eq $first"/>
            </xsl:template>
            """);
        output.Should().Be("true");
    }

    [Fact]
    public async Task Separate_transformations_take_separate_snapshots()
    {
        const string template = """<xsl:template match="/"><xsl:value-of select="current-dateTime()"/></xsl:template>""";
        var first = DateTimeOffset.Parse(await RunAsync(template), System.Globalization.CultureInfo.InvariantCulture);
        await Task.Delay(20);
        var second = DateTimeOffset.Parse(await RunAsync(template), System.Globalization.CultureInfo.InvariantCulture);
        second.Should().BeAfter(first);
    }
}
