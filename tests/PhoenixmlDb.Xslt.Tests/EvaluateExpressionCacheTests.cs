using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:evaluate keeps the expressions it has parsed for the next evaluation of the same text
/// (W3C evaluate-010 evaluates two strings 100,000 times, and parsed each time). A cached
/// expression gives the result of a fresh one: other texts, other parameter values and other
/// schema-awareness are kept apart, and a text that is not valid is refused every time.
/// </summary>
public class EvaluateExpressionCacheTests
{
    private static async Task<string> Run(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template name="main">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task Repeated_texts_give_each_their_own_result()
        => (await Run("""
                <r><xsl:for-each select="('$v + 1', '$v * 10', '$v + 1', '$v * 10')">
                  <xsl:evaluate xpath="." ><xsl:with-param name="v" select="position()"/></xsl:evaluate>
                  <xsl:text> </xsl:text>
                </xsl:for-each></r>
                """)).Should().Be("<r>2 20 4 40 </r>");

    [Fact]
    public async Task A_text_that_is_not_valid_is_refused_each_time()
    {
        var act = () => Run("""
                <xsl:for-each select="1 to 3">
                  <xsl:try>
                    <xsl:evaluate xpath="'1 +'"/>
                    <xsl:catch><xsl:value-of select="$err:code" xmlns:err="http://www.w3.org/2005/xqt-errors"/></xsl:catch>
                  </xsl:try>
                </xsl:for-each>
                """);
        (await act()).Should().Be("err:XTDE3160err:XTDE3160err:XTDE3160");
    }
}
