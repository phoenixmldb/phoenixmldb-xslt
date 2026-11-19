using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The value of a <c>select</c> on a local <c>xsl:variable</c>, an <c>xsl:with-param</c>, a
/// template or <c>xsl:iterate</c> parameter is converted to the declared type by the function
/// conversion rules (XSLT 3.0 §9.3; XPath 3.1 §3.1.5.2), as for a global variable: an untyped
/// value is cast and numeric and URI values are promoted. A value of another type is XTTE0570
/// for a variable, XTTE0590 for a value supplied to a parameter, and XTTE0600 for a
/// parameter's default or initial value. Each shape gives the same result as Saxon-HE 12.10.
/// The engine cast such a value: a local <c>as="xs:integer" select="'12'"</c> gave 12.
/// </summary>
public class LocalSelectFunctionConversionTests
{
    private static async Task<string> Run(string body, string extra = "")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:output method="text"/>
              <xsl:variable name="d" select="parse-xml('&lt;r n=&quot;12&quot;&gt;3.5&lt;/r&gt;')"/>
              <xsl:template name="t"><xsl:param name="p" as="xs:integer"/><xsl:value-of select="$p"/></xsl:template>
              <xsl:template match="e" mode="m"><xsl:param name="p" as="xs:double"/><xsl:value-of select="$p"/></xsl:template>
              {extra}
              <xsl:template name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("""<xsl:variable name="v" as="xs:integer" select="$d/r/@n"/><xsl:value-of select="$v"/>""", "12")]
    [InlineData("""<xsl:variable name="v" as="xs:integer">12</xsl:variable><xsl:value-of select="$v"/>""", "12")]
    [InlineData("""<xsl:variable name="v" as="xs:double" select="12"/><xsl:value-of select="$v"/>""", "12")]
    [InlineData("""<xsl:variable name="v" as="xs:string" select="xs:anyURI('u')"/><xsl:value-of select="$v"/>""", "u")]
    [InlineData("""<xsl:call-template name="t"><xsl:with-param name="p" select="$d/r/@n"/></xsl:call-template>""", "12")]
    [InlineData("""<xsl:call-template name="t"><xsl:with-param name="p">12</xsl:with-param></xsl:call-template>""", "12")]
    [InlineData("""<xsl:call-template name="t"><xsl:with-param name="p" select="12"/></xsl:call-template>""", "12")]
    [InlineData("""<xsl:apply-templates select="parse-xml('&lt;e/&gt;')/e" mode="m"><xsl:with-param name="p" select="2"/></xsl:apply-templates>""", "2")]
    [InlineData("""<xsl:iterate select="1,2"><xsl:param name="p" as="xs:integer" select="0"/><xsl:value-of select="$p"/><xsl:next-iteration><xsl:with-param name="p" select="$p + 1"/></xsl:next-iteration></xsl:iterate>""", "01")]
    [InlineData("""<xsl:iterate select="1"><xsl:param name="p" as="xs:integer" select="$d/r/@n"/><xsl:value-of select="$p"/></xsl:iterate>""", "12")]
    public async Task A_value_the_rules_convert_is_converted(string body, string expected)
        => (await Run(body)).Should().Be(expected);

    [Theory]
    [InlineData("""<xsl:variable name="v" as="xs:integer" select="'12'"/><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:variable name="v" as="xs:boolean" select="'true'"/><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:call-template name="t"><xsl:with-param name="p" select="'12'"/></xsl:call-template>""", "XTTE0590")]
    [InlineData("""<xsl:apply-templates select="parse-xml('&lt;e/&gt;')/e" mode="m"><xsl:with-param name="p" select="'1.5'"/></xsl:apply-templates>""", "XTTE0590")]
    [InlineData("""<xsl:iterate select="1"><xsl:param name="p" as="xs:integer" select="'12'"/><xsl:value-of select="$p"/></xsl:iterate>""", "XTTE0600")]
    [InlineData("""<xsl:iterate select="1,2"><xsl:param name="p" as="xs:integer" select="0"/><xsl:value-of select="$p"/><xsl:next-iteration><xsl:with-param name="p" select="'7'"/></xsl:next-iteration></xsl:iterate>""", "XTTE0590")]
    public async Task A_value_of_another_type_is_a_type_error(string body, string code)
    {
        var act = () => Run(body);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain(code);
    }

    [Fact]
    public async Task A_template_parameter_default_of_another_type_is_XTTE0600_when_it_is_used()
    {
        var act = () => Run("""<xsl:call-template name="td"/>""",
            """<xsl:template name="td"><xsl:param name="p" as="xs:integer" select="'5'"/><xsl:value-of select="$p"/></xsl:template>""");

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTTE0600");
    }
}
