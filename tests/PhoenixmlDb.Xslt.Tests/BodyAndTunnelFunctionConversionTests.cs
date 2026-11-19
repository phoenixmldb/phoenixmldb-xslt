using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The function conversion rules for a value that a body or a tunnel parameter supplies (XSLT 3.0
/// §9.3, §9.4). Text that a body writes is a text node, which atomizes to xs:untypedAtomic and is
/// cast; an item that xsl:sequence adds keeps its type, and an xs:string is not cast to
/// xs:integer. A tunnel parameter's value keeps whether it came from select, or from a with-param
/// with its own as type. The engine wrote xsl:sequence items in a body with a one-item atomic type
/// out as text, and kept a tunnel value bare, so both were cast. Each shape gives the same result
/// as Saxon-HE 12.10.
/// </summary>
public class BodyAndTunnelFunctionConversionTests
{
    private static async Task<string> Run(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:output method="text"/>
              <xsl:variable name="d" select="parse-xml('&lt;r n=&quot;12&quot;&gt;3.5&lt;/r&gt;')"/>
              <xsl:template name="t"><xsl:param name="p" as="xs:integer"/><xsl:value-of select="$p"/></xsl:template>
              <xsl:template name="mid"><xsl:call-template name="tt"/></xsl:template>
              <xsl:template name="tt"><xsl:param name="p" as="xs:integer" tunnel="yes"/><xsl:value-of select="$p"/></xsl:template>
              <xsl:template name="ts"><xsl:param name="p" as="xs:string"/><xsl:value-of select="string-length($p)"/></xsl:template>
              <xsl:template name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("""<xsl:variable name="v" as="xs:integer">12</xsl:variable><xsl:value-of select="$v"/>""", "12")]
    [InlineData("""<xsl:variable name="v" as="xs:integer"><xsl:value-of select="'12'"/></xsl:variable><xsl:value-of select="$v"/>""", "12")]
    [InlineData("""<xsl:variable name="v" as="xs:integer"><xsl:sequence select="12"/></xsl:variable><xsl:value-of select="$v"/>""", "12")]
    [InlineData("""<xsl:variable name="v" as="xs:boolean"><xsl:sequence select="false()"/></xsl:variable><xsl:value-of select="$v"/>""", "false")]
    [InlineData("""<xsl:variable name="v" as="xs:boolean"><xsl:value-of select="false()"/></xsl:variable><xsl:value-of select="$v"/>""", "false")]
    [InlineData("""<xsl:variable name="v" as="xs:double"><xsl:sequence select="1"/></xsl:variable><xsl:value-of select="$v"/>""", "1")]
    [InlineData("""<xsl:call-template name="mid"><xsl:with-param name="p" select="12" tunnel="yes"/></xsl:call-template>""", "12")]
    [InlineData("""<xsl:call-template name="mid"><xsl:with-param name="p" select="$d/r/@n" tunnel="yes"/></xsl:call-template>""", "12")]
    [InlineData("""<xsl:call-template name="mid"><xsl:with-param name="p" tunnel="yes">12</xsl:with-param></xsl:call-template>""", "12")]
    [InlineData("""<xsl:call-template name="mid"><xsl:with-param name="p" tunnel="yes"><xsl:sequence select="'12'"/></xsl:with-param></xsl:call-template>""", "12")]
    [InlineData("""<xsl:call-template name="t"><xsl:with-param name="p" as="xs:integer">12</xsl:with-param></xsl:call-template>""", "12")]
    [InlineData("""<xsl:call-template name="t"><xsl:with-param name="p"><xsl:sequence select="'12'"/></xsl:with-param></xsl:call-template>""", "12")]
    // xsl:value-of select="''" makes a zero-length text node, so the value is the zero-length string,
    // not the empty sequence (W3C select-2303, message-0304, match-249).
    [InlineData("""<xsl:variable name="v" as="xs:string"><xsl:value-of select="''"/></xsl:variable><xsl:value-of select="string-length($v)"/>""", "0")]
    [InlineData("""<xsl:call-template name="ts"><xsl:with-param name="p" as="xs:string"><xsl:value-of select="''"/></xsl:with-param></xsl:call-template>""", "0")]
    public async Task A_value_the_rules_convert_is_converted(string body, string expected)
        => (await Run(body)).Should().Be(expected);

    [Theory]
    [InlineData("""<xsl:variable name="v" as="xs:integer"><xsl:sequence select="'12'"/></xsl:variable><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:variable name="v" as="xs:integer?"><xsl:sequence select="'12'"/></xsl:variable><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:variable name="v" as="xs:string"><xsl:sequence select="1"/></xsl:variable><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:variable name="v" as="xs:string"><xsl:text>a</xsl:text><xsl:value-of select="'b'"/></xsl:variable><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:call-template name="mid"><xsl:with-param name="p" select="'12'" tunnel="yes"/></xsl:call-template>""", "XTTE0590")]
    [InlineData("""<xsl:call-template name="t"><xsl:with-param name="p" as="xs:integer"><xsl:sequence select="'12'"/></xsl:with-param></xsl:call-template>""", "XTTE0570")]
    [InlineData("""<xsl:call-template name="t"><xsl:with-param name="p" as="xs:string" select="'12'"/></xsl:call-template>""", "XTTE0590")]
    [InlineData("""<xsl:call-template name="mid"><xsl:with-param name="p" as="xs:string" select="'12'" tunnel="yes"/></xsl:call-template>""", "XTTE0590")]
    public async Task A_value_of_another_type_is_a_type_error(string body, string code)
    {
        var act = () => Run(body);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain(code);
    }
}
