using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A cycle in use-attribute-sets that the static check cannot see — the reference sits inside an attribute's sequence
/// constructor, on a literal result element built in a variable — is caught at run time as XTDE0640. Expanding an
/// attribute set swaps out the scope stack, the output buffer and the collected-attribute stack, and the restore ran
/// only on the success path: the error left the scope stack empty, so the caller's PopScope raised
/// InvalidOperationException("Stack empty") over the real error (W3C attribute-set-0106a, override-as-003,
/// accept-047b, accept-047c).
/// </summary>
public sealed class AttributeSetCircularityTests
{
    private static async Task<string> ErrorOfAsync(string stylesheetBody, string source = "<doc/>")
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              {{stylesheetBody}}
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        try
        {
            await t.LoadStylesheetAsync(ss);
            return "ok:" + (await t.TransformAsync(source)).Trim();
        }
        catch (XsltException ex)
        {
            return ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            // The bug this guards: the engine crashed with "Stack empty" instead of reporting the XSLT error.
            return "CRASH:" + ex.Message;
        }
    }

    // The recursion goes through a literal result element inside an attribute body, which the static check cannot
    // follow, so it is only detected while the set is being expanded — the path that used to crash.
    private const string RecursiveSet = """
        <xsl:attribute-set name="set1">
          <xsl:attribute name="color">
            <xsl:for-each select="*">
              <xsl:variable name="x"><e xsl:use-attribute-sets="set1"/></xsl:variable>
              <xsl:value-of select="string-join($x//@*, '|')"/>
            </xsl:for-each>
          </xsl:attribute>
          <xsl:attribute name="texture">matt</xsl:attribute>
        </xsl:attribute-set>
        <xsl:template match="/">
          <out><test1 xsl:use-attribute-sets="set1"/></out>
        </xsl:template>
        """;

    [Fact]
    public async Task A_cycle_found_while_expanding_raises_XTDE0640()
        => (await ErrorOfAsync(RecursiveSet, "<doc><section index=\"s1\"><p>Hello</p></section></doc>"))
            .Should().StartWith("XTDE0640");

    [Fact]
    public async Task A_statically_visible_cycle_is_still_XTSE0720()
        => (await ErrorOfAsync("""
                <xsl:attribute-set name="a" use-attribute-sets="b"><xsl:attribute name="x">1</xsl:attribute></xsl:attribute-set>
                <xsl:attribute-set name="b" use-attribute-sets="a"><xsl:attribute name="y">2</xsl:attribute></xsl:attribute-set>
                <xsl:template match="/"><out xsl:use-attribute-sets="a"/></xsl:template>
                """)).Should().StartWith("XTSE0720");

    [Fact]
    public async Task A_normal_attribute_set_still_applies()
        => (await ErrorOfAsync("""
                <xsl:attribute-set name="a"><xsl:attribute name="x">1</xsl:attribute></xsl:attribute-set>
                <xsl:template match="/"><out xsl:use-attribute-sets="a"/></xsl:template>
                """)).Should().Be("ok:<out x=\"1\"/>");
}
