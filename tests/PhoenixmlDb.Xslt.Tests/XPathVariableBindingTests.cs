using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An XPath expression that is not a bare variable reference is evaluated with only the variables it references
/// bound, and its plan, namespace ids and variable names are kept per expression. These pin the shapes whose
/// values must not change: closures, temporary trees, pseudo-variables, xsl:evaluate parameters, shadowing, and
/// a compiled stylesheet reused against documents whose namespaces intern differently.
/// </summary>
public sealed class XPathVariableBindingTests
{
    private static async Task<string> RunAsync(string templates, string source = "<x/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet(templates));
        return (await t.TransformAsync(source)).Trim();
    }

    private static string Stylesheet(string templates) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                        xmlns:p="urn:p" exclude-result-prefixes="p">
          <xsl:output method="text"/>
          {{templates}}
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task Closure_KeepsTheValueItCapturedWhereItWasCreated()
    {
        const string templates = """
            <xsl:template match="/">
              <xsl:variable name="k" select="10"/>
              <xsl:variable name="f" select="function($x) { $x + $k }"/>
              <xsl:call-template name="use"><xsl:with-param name="f" select="$f"/></xsl:call-template>
            </xsl:template>
            <xsl:template name="use">
              <xsl:param name="f"/>
              <xsl:variable name="k" select="1000"/>
              <xsl:value-of select="$f(1)"/>
            </xsl:template>
            """;
        (await RunAsync(templates)).Should().Be("11");
    }

    [Fact]
    public async Task TemporaryTreeVariable_IsNavigable()
    {
        const string templates = """
            <xsl:template match="/">
              <xsl:variable name="t"><a><b>x</b></a></xsl:variable>
              <xsl:value-of select="$t/a/b || '!'"/>
            </xsl:template>
            """;
        (await RunAsync(templates)).Should().Be("x!");
    }

    [Fact]
    public async Task CurrentGroup_AndALocal_InOneExpression()
    {
        const string templates = """
            <xsl:template match="/">
              <xsl:for-each-group select="(1, 2, 3, 4)" group-by=". mod 2">
                <xsl:variable name="w" select="'g'"/>
                <xsl:value-of select="$w || sum(current-group())"/>
              </xsl:for-each-group>
            </xsl:template>
            """;
        (await RunAsync(templates)).Should().Be("g4g6");
    }

    [Fact]
    public async Task EvaluateWithParam_IsBound()
    {
        const string templates = """
            <xsl:template match="/">
              <xsl:variable name="m" select="2"/>
              <xsl:evaluate xpath="'$p * 2'"><xsl:with-param name="p" select="21"/></xsl:evaluate>
              <xsl:value-of select="' ' || $m * 3"/>
            </xsl:template>
            """;
        (await RunAsync(templates)).Should().Be("42 6");
    }

    [Fact]
    public async Task InnerLocal_ShadowsOuterLocalAndGlobal()
    {
        const string templates = """
            <xsl:variable name="n" select="-1"/>
            <xsl:template match="/">
              <xsl:value-of select="$n + 1"/>
              <xsl:variable name="n" select="1"/>
              <xsl:for-each select="(1, 2)">
                <xsl:variable name="n" select=". * 10"/>
                <xsl:value-of select="' ' || $n + 1"/>
              </xsl:for-each>
              <xsl:value-of select="' ' || $n + 1"/>
            </xsl:template>
            """;
        (await RunAsync(templates)).Should().Be("0 11 21 2");
    }

    [Fact]
    public async Task ReusedStylesheet_MatchesNamespacedNamesInEveryDocument()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet("""
            <xsl:template match="/">
              <xsl:value-of select="count(//p:item) || ':' || string-join(//p:item/@v, ',')"/>
            </xsl:template>
            """));
        // The second document interns two other namespaces first, so p's namespace id differs between the runs.
        var first = (await t.TransformAsync("""<r xmlns:p="urn:p"><p:item v="a"/><p:item v="b"/></r>""")).Trim();
        var second = (await t.TransformAsync(
            """<r xmlns:q="urn:q" xmlns:s="urn:s" xmlns:p="urn:p"><q:x/><s:y/><p:item v="c"/></r>""")).Trim();
        first.Should().Be("2:a,b");
        second.Should().Be("1:c");
    }

    [Fact]
    public async Task BackwardsCompatibleMode_FollowsEachTemplatesVersion()
    {
        // The backwards-compatible flag is kept between reads; alternating version="1.0" and 3.0 templates must
        // still switch XPath 1.0 first-item argument conversion on and off for every call.
        const string templates = """
            <xsl:template match="/">
              <xsl:call-template name="v1"/>|<xsl:call-template name="v3"/>|<xsl:call-template name="v1"/>
            </xsl:template>
            <xsl:template name="v1" version="1.0">
              <xsl:value-of select="substring-before(('a-b', 'c-d'), '-')"/>
            </xsl:template>
            <xsl:template name="v3">
              <xsl:try>
                <xsl:value-of select="substring-before(('a-b', 'c-d'), '-')"/>
                <xsl:catch errors="*">E</xsl:catch>
              </xsl:try>
            </xsl:template>
            """;
        (await RunAsync(templates)).Should().Be("a|E|a");
    }
}
