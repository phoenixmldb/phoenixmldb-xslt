using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// as="xs:dateTimeStamp" takes an xs:dateTimeStamp (the constructor, a cast, fn:current-dateTime()) or
/// an untyped value with a timezone, which is cast; an xs:dateTime is not converted to it (XTTE0570,
/// XTTE0590 for a value passed to a parameter). The type fell to item(), so it took any value, 1 and
/// 'abc' among them. Each shape gives the same result as Saxon-HE 12.10, except that an untyped value
/// without a timezone raises the cast's FORG0001 where Saxon reports XTTE0570.
/// </summary>
public class DateTimeStampConversionTests
{
    private static async Task<string> Run(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:output method="text"/>
              <xsl:variable name="d" select="parse-xml('&lt;r t=&quot;2026-01-01T00:00:00Z&quot; n=&quot;2026-01-01T00:00:00&quot;/&gt;')"/>
              <xsl:template name="t"><xsl:param name="p" as="xs:dateTimeStamp"/><xsl:value-of select="$p instance of xs:dateTimeStamp"/></xsl:template>
              <xsl:template name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("""<xsl:variable name="v" as="xs:dateTimeStamp" select="xs:dateTimeStamp('2026-01-01T00:00:00Z')"/><xsl:value-of select="$v instance of xs:dateTimeStamp, $v"/>""", "true 2026-01-01T00:00:00Z")]
    [InlineData("""<xsl:variable name="v" as="xs:dateTimeStamp" select="$d/r/@t"/><xsl:value-of select="$v instance of xs:dateTimeStamp, $v"/>""", "true 2026-01-01T00:00:00Z")]
    [InlineData("""<xsl:variable name="v" as="xs:dateTimeStamp">2026-01-01T00:00:00Z</xsl:variable><xsl:value-of select="$v instance of xs:dateTimeStamp, $v"/>""", "true 2026-01-01T00:00:00Z")]
    [InlineData("""<xsl:variable name="v" as="xs:dateTime" select="xs:dateTimeStamp('2026-01-01T00:00:00Z')"/><xsl:value-of select="$v instance of xs:dateTimeStamp, $v"/>""", "true 2026-01-01T00:00:00Z")]
    [InlineData("""<xsl:variable name="v" as="xs:dateTimeStamp?" select="()"/><xsl:value-of select="count($v)"/>""", "0")]
    [InlineData("""<xsl:call-template name="t"><xsl:with-param name="p" select="current-dateTime()"/></xsl:call-template>""", "true")]
    public async Task A_value_of_the_type_or_untyped_is_accepted(string body, string expected)
        => (await Run(body)).Should().Be(expected);

    [Theory]
    [InlineData("""<xsl:variable name="v" as="xs:dateTimeStamp" select="1"/><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:variable name="v" as="xs:dateTimeStamp" select="'2026-01-01T00:00:00Z'"/><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:variable name="v" as="xs:dateTimeStamp" select="xs:dateTime('2026-01-01T00:00:00Z')"/><xsl:value-of select="$v"/>""", "XTTE0570")]
    [InlineData("""<xsl:call-template name="t"><xsl:with-param name="p" select="xs:dateTime('2026-01-01T00:00:00Z')"/></xsl:call-template>""", "XTTE0590")]
    [InlineData("""<xsl:variable name="v" as="xs:dateTimeStamp" select="$d/r/@n"/><xsl:value-of select="$v"/>""", "FORG0001")]
    public async Task A_value_of_another_type_is_an_error(string body, string code)
    {
        var act = () => Run(body);

        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        (ex is PhoenixmlDb.XQuery.Execution.XQueryRuntimeException rt ? rt.ErrorCode : ex.Message).Should().Contain(code);
    }
}
