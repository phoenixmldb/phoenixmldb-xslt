using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The function conversion rules for a type derived from xs:string by restriction (xs:token,
/// xs:NMTOKEN, xs:NCName, xs:ID and the others): only a value of that type, or of a type derived
/// from it, is accepted as it is; an untyped value or node is cast; an xs:string is not cast, so
/// as="xs:NCName" select="'abc'" is XTTE0570 (XTTE0590 for a value passed to a parameter). The
/// engine cast it. Each shape gives the same result as Saxon-HE 12.10.
/// </summary>
public class DerivedStringTypeConversionTests
{
    private static async Task<string> Run(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:output method="text"/>
              <xsl:variable name="d" select="parse-xml('&lt;r&gt;abc&lt;/r&gt;')"/>
              <xsl:template name="t"><xsl:param name="p" as="xs:NCName"/><xsl:value-of select="$p"/></xsl:template>
              <xsl:template name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("""<xsl:variable name="v" as="xs:NCName" select="xs:ID('a')"/><xsl:value-of select="$v"/>""", "a")]
    [InlineData("""<xsl:variable name="v" as="xs:language" select="xs:language('en')"/><xsl:value-of select="$v"/>""", "en")]
    [InlineData("""<xsl:variable name="v" as="xs:NCName">abc</xsl:variable><xsl:value-of select="$v"/>""", "abc")]
    [InlineData("""<xsl:call-template name="t"><xsl:with-param name="p" select="$d/r"/></xsl:call-template>""", "abc")]
    [InlineData("""<xsl:variable name="v" as="xs:Name" select="xs:NCName('a')"/><xsl:value-of select="$v"/>""", "a")]
    [InlineData("""<xsl:variable name="v" as="xs:normalizedString" select="xs:token('a')"/><xsl:value-of select="$v"/>""", "a")]
    [InlineData("""<xsl:variable name="v" as="xs:NMTOKEN" select="$d/r"/><xsl:value-of select="$v"/>""", "abc")]
    [InlineData("""<xsl:variable name="v" as="xs:string" select="xs:NCName('abc')"/><xsl:value-of select="$v"/>""", "abc")]
    public async Task A_value_of_the_type_or_untyped_is_accepted(string body, string expected)
        => (await Run(body)).Should().Be(expected);

    [Theory]
    [InlineData("""<xsl:variable name="v" as="xs:ID" select="xs:NCName('a')"/><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:variable name="v" as="xs:token" select="'a'"/><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:variable name="v" as="xs:NCName"><xsl:sequence select="'abc'"/></xsl:variable><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:call-template name="t"><xsl:with-param name="p" select="'abc'"/></xsl:call-template>""", "XTTE0590")]
    [InlineData("""<xsl:variable name="v" as="xs:NMTOKEN*" select="('a', 'b')"/><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:variable name="v" as="xs:token" select="xs:anyURI('u')"/><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:variable name="v" as="xs:NMTOKEN" select="'abc'"/><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:variable name="v" as="xs:normalizedString" select="'abc'"/><xsl:value-of select="$v"/>""", "XTTE0570")]
    public async Task A_value_of_another_type_is_a_type_error(string body, string code)
    {
        var act = () => Run(body);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain(code);
    }
}
