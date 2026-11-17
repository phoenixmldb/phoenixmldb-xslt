using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An error raised inside an xsl:function and caught by xsl:catch left the function's own
/// result list installed, so the catch and everything after it wrote into a list nobody read:
/// an as="xs:string*" variable over three iterations held "caught3" only, and text after the
/// xsl:try was lost. Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class TryCatchFunctionErrorTests
{
    private static async Task<string> RunAsync(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="f:boom" as="item()*"><xsl:param name="i"/><xsl:sequence select="'partial', error()"/></xsl:function>
              <xsl:function name="f:deep" as="xs:string"><xsl:param name="i"/><xsl:sequence select="string(f:boom($i))"/></xsl:function>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync("<r/>")).Trim();
    }

    [Theory]
    [InlineData("""
        <xsl:variable name="r" as="xs:string*">
          <xsl:for-each select="1 to 3">
            <xsl:try>
              <xsl:sequence select="if (. eq 2) then 'ok' else f:deep(.)"/>
              <xsl:catch><xsl:sequence select="'caught' || ."/></xsl:catch>
            </xsl:try>
          </xsl:for-each>
        </xsl:variable>
        <xsl:value-of select="count($r), $r"/>
        """, "3 caught1 ok caught3")]
    [InlineData("""
        <xsl:for-each select="1 to 3">
          <xsl:try>
            <xsl:value-of select="if (. eq 2) then 'ok' else f:deep(.)"/>
            <xsl:catch><xsl:value-of select="'caught' || ."/></xsl:catch>
          </xsl:try>
          <xsl:text>;</xsl:text>
        </xsl:for-each>
        """, "caught1;ok;caught3;")]
    [InlineData("""
        <xsl:variable name="v" as="xs:string*">
          <xsl:sequence select="'a'"/>
          <xsl:try><xsl:sequence select="'b', f:deep(1)"/><xsl:catch><xsl:sequence select="'c'"/></xsl:catch></xsl:try>
          <xsl:sequence select="'d'"/>
        </xsl:variable>
        <xsl:value-of select="$v"/>
        """, "a c d")]
    [InlineData("""
        <xsl:variable name="v" as="xs:string*">
          <xsl:try><xsl:sequence select="'b', error()"/><xsl:catch><xsl:sequence select="'c'"/></xsl:catch></xsl:try>
        </xsl:variable>
        <xsl:value-of select="$v"/>
        """, "c")]
    public async Task Keeps_the_items_around_a_caught_function_error(string body, string expected)
        => (await RunAsync(body)).Should().Be(expected);
}
