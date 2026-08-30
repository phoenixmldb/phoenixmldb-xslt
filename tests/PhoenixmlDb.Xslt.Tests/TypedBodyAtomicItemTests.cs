using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A variable body declared <c>as="item()*"</c> must keep atomic values atomic.
///
/// When the body of an <c>as="…"</c>-typed variable produced a string item, the engine wrapped
/// it in a text node for <c>text()</c>, <c>node()</c> AND <c>item()</c>. The first two are
/// right — those types demand nodes, so a string contributed by xsl:sequence has to become one.
/// <c>item()</c> is the union of nodes and atomic values, so wrapping there silently retyped
/// every atomic the body produced:
///
///   &lt;xsl:template name="two" as="item()*"&gt;&lt;xsl:sequence select="('a','b')"/&gt;&lt;/…&gt;
///   &lt;xsl:variable name="v" as="item()*"&gt;&lt;xsl:call-template name="two"/&gt;&lt;/…&gt;
///   $v[1] instance of xs:string   ->  false; it was a text node
///
/// The values still LOOK right — they serialize identically — so nothing visible broke until
/// something compared them by type. deep-equal($v, ('a','b')) was false.
///
/// Found through XSpec: an x:expect with @test compares the test expression's value against
/// @select via deep-equal, so every expect whose test yielded atomic values failed on the type
/// rather than the value. undeclare-ns_stylesheet went 0/12 -> 12/12 and
/// external_undeclare-ns_stylesheet 0/3 -> 3/3 on this one change.
///
/// as="xs:string*" masked it throughout — that type coerces the text nodes straight back to
/// strings — which is why the defect needed a body typed loosely enough to preserve what it
/// was given.
/// </summary>
public class TypedBodyAtomicItemTests
{
    private static async Task<string> Run(string body)
    {
        var xslt = $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              {body}
            </xsl:stylesheet>
            """;
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xslt);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task ItemStar_KeepsStringsFromACalledTemplateAtomic()
    {
        var result = await Run("""
            <xsl:template name="two" as="item()*"><xsl:sequence select="('a','b')"/></xsl:template>
            <xsl:template name="main">
              <xsl:variable name="v" as="item()*"><xsl:call-template name="two"/></xsl:variable>
              <out count="{count($v)}" isString="{$v[1] instance of xs:string}"
                   equal="{deep-equal($v, ('a','b'))}"/>
            </xsl:template>
            """);
        result.Should().Contain("count=\"2\"").And.Contain("isString=\"true\"").And.Contain("equal=\"true\"");
    }

    [Fact]
    public async Task ItemStar_KeepsNonStringAtomicsToo()
    {
        var result = await Run("""
            <xsl:template name="nums" as="item()*"><xsl:sequence select="(1, 2.5, true())"/></xsl:template>
            <xsl:template name="main">
              <xsl:variable name="v" as="item()*"><xsl:call-template name="nums"/></xsl:variable>
              <out isInt="{$v[1] instance of xs:integer}" isBool="{$v[3] instance of xs:boolean}"/>
            </xsl:template>
            """);
        result.Should().Contain("isInt=\"true\"").And.Contain("isBool=\"true\"");
    }

    /// <summary>
    /// The half that must keep wrapping: text()* and node()* demand nodes, so a string item
    /// still has to become a text node. A fix that dropped the wrapping outright would pass the
    /// tests above and break these.
    /// </summary>
    [Theory]
    [InlineData("text()*")]
    [InlineData("node()*")]
    public async Task NodeTypedBodies_StillWrapStringsIntoTextNodes(string type)
    {
        var result = await Run("""
            <xsl:template name="two" as="item()*"><xsl:sequence select="('a','b')"/></xsl:template>
            <xsl:template name="main">
              <xsl:variable name="v" as="TYPE"><xsl:call-template name="two"/></xsl:variable>
              <out isText="{$v[1] instance of text()}" count="{count($v)}"/>
            </xsl:template>
            """.Replace("TYPE", type, StringComparison.Ordinal));
        result.Should().Contain("isText=\"true\"").And.Contain("count=\"2\"");
    }

    /// <summary>
    /// Literal character content in an item()* body still becomes a text node — it reaches the
    /// result through the body's text output, not as a string item in the accumulator, so the
    /// change does not affect it.
    /// </summary>
    [Fact]
    public async Task ItemStar_LiteralTextIsStillATextNode()
    {
        var result = await Run("""
            <xsl:template name="main">
              <xsl:variable name="v" as="item()*">hello</xsl:variable>
              <out isText="{$v[1] instance of text()}" v="{$v}"/>
            </xsl:template>
            """);
        result.Should().Contain("isText=\"true\"").And.Contain("v=\"hello\"");
    }
}
