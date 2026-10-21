using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The query context of an XPath evaluation is reset and used for the next one (xquery#294).
/// Nothing of one evaluation may reach another: a variable, the focus, a namespace binding, the
/// backwards-compatible mode. A function item runs its body later in the context it was created
/// in, so that context is not reused.
/// </summary>
public sealed class QueryContextReuseTests
{
    private static async Task<string> Run(string body, string extra = "")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {extra}
              <xsl:template name="main">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task A_function_item_made_in_one_evaluation_runs_after_many_others()
        => (await Run("""
                <xsl:variable name="k" select="10"/>
                <xsl:variable name="f" select="function($x) { $x + $k }"/>
                <xsl:for-each select="1 to 50"><xsl:if test=". mod 2 = 0"/></xsl:for-each>
                <xsl:value-of select="$f(1), $f(2)"/>
                """)).Should().Be("11 12");

    [Fact]
    public async Task Evaluations_do_not_see_each_others_variables_or_focus()
        => (await Run("""
                <xsl:for-each select="('a', 'b')">
                  <xsl:variable name="v" select="string(.) || position()"/>
                  <xsl:value-of select="$v, position(), last()"/>
                  <xsl:text>;</xsl:text>
                </xsl:for-each>
                <xsl:value-of select="for $i in 1 to 2 return $i * 3"/>
                """)).Should().Be("a1 1 2;b2 2 2;3 6");

    [Fact]
    public async Task Backwards_compatible_mode_stays_with_its_instruction()
        => (await Run("""
                <xsl:value-of select="(1, 2) + 1" version="1.0"/>
                <xsl:text>|</xsl:text>
                <xsl:try>
                  <xsl:value-of select="(1, 2) + 1"/>
                  <xsl:catch>XPTY0004</xsl:catch>
                </xsl:try>
                """)).Should().Be("2|XPTY0004");

    // The body of a function item made in an XSLT 1.0 instruction keeps that instruction's
    // backwards-compatible arithmetic when a 3.0 instruction calls it: it runs in the context it
    // was made in. Reset and reused for the evaluations in between, that context would have
    // lost the mode, and (1, 2) + 1 would be XPTY0004.
    [Fact]
    public async Task A_function_item_keeps_the_settings_of_the_context_it_was_made_in()
        => (await Run("""
                <xsl:variable name="f" select="function($s) { $s + 1 }" version="1.0"/>
                <xsl:for-each select="1 to 20"><xsl:if test=". mod 2 = 0"/></xsl:for-each>
                <xsl:value-of select="$f((1, 2))"/>
                """)).Should().Be("2");
}
