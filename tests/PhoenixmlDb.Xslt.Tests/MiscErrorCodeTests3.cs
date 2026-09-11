using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A third batch of error codes that named a different rule than the one broken.
/// </summary>
public sealed class MiscErrorCodeTests3
{
    private static async Task<string> RunAsync(string stylesheet, string source = "<doc/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return (await t.TransformAsync(source)).Trim();
    }

    private static async Task AssertErrorAsync(string stylesheet, string code)
    {
        var act = () => RunAsync(stylesheet);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain(code);
    }

    private static string Wrap(string declarations, string body = "") => $"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          {declarations}
          <xsl:template match="/">{body}</xsl:template>
        </xsl:stylesheet>
        """;

    /// <summary>
    /// The content of xsl:key is a sequence constructor, so a declaration there is not allowed
    /// content at all: XTSE0010. It was reported as XTSE1205 "both a use attribute and content",
    /// which describes a different mistake (key-092).
    /// </summary>
    [Fact]
    public Task Key_WithADeclarationAsContent_IsXTSE0010()
        => AssertErrorAsync(Wrap("""
            <xsl:key name="k" match="p" use=".//term"><xsl:template match="/"/></xsl:key>
            """), "XTSE0010");

    [Fact]
    public Task Key_WithBothUseAndASequenceConstructor_IsStillXTSE1205()
        => AssertErrorAsync(Wrap("""
            <xsl:key name="k" match="p" use=".//term"><xsl:value-of select="."/></xsl:key>
            """), "XTSE1205");

    /// <summary>
    /// A streamable attribute set may only use streamable ones — using a non-streamable set would
    /// consume the input twice (XTSE0730). The list was not checked at all (error-0730a).
    /// </summary>
    [Fact]
    public Task AStreamableAttributeSet_UsingANonStreamableOne_IsXTSE0730()
        => AssertErrorAsync(Wrap("""
            <xsl:attribute-set name="a" streamable="yes" use-attribute-sets="b"><xsl:attribute name="x" select="1"/></xsl:attribute-set>
            <xsl:attribute-set name="b"><xsl:attribute name="y" select="1"/></xsl:attribute-set>
            """), "XTSE0730");

    [Fact]
    public async Task AStreamableAttributeSet_UsingAStreamableOne_Compiles()
        => (await RunAsync(Wrap("""
            <xsl:attribute-set name="a" streamable="yes" use-attribute-sets="b"><xsl:attribute name="x" select="1"/></xsl:attribute-set>
            <xsl:attribute-set name="b" streamable="yes"><xsl:attribute name="y" select="2"/></xsl:attribute-set>
            """, "ok"))).Should().Be("ok");

    /// <summary>
    /// When document() is given a NODE, a relative reference resolves against that node's base
    /// URI. A parentless text node built by a variable has none, leaving nothing to resolve
    /// against: XTDE1162. It fell back to the static base and then reported a retrieval failure
    /// (error-1162a).
    /// </summary>
    [Fact]
    public Task Document_WithANodeThatHasNoBaseUri_IsXTDE1162()
        => AssertErrorAsync(Wrap("""<xsl:variable name="t" as="text()"><xsl:value-of select="'abcd.xml'"/></xsl:variable>""",
            """<xsl:copy-of select="document($t)"/>"""), "XTDE1162");

    /// <summary>A string argument still resolves against the static base URI.</summary>
    [Fact]
    public Task Document_WithAStringUri_StillUsesTheStaticBase()
        => AssertErrorAsync(Wrap("", """<xsl:copy-of select="document('abcd.xml')"/>"""), "FODC0005");
}
