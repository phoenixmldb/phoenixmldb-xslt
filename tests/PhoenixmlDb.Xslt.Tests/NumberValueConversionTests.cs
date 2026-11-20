using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The value of xsl:number value="..." is converted to xs:numeric (XSLT 3.0 §12.3): an untyped
/// value, such as an atomized node, is cast to xs:double, and an xs:float or a derived integer
/// (xs:int, xs:short) is a number. They were NaN, so value="." over &lt;num&gt;100&lt;/num&gt; was XTDE0980
/// (W3C avt-0801). Every expected value is Saxon-HE 12.10's.
/// </summary>
public class NumberValueConversionTests
{
    private static async Task<string> Run(string value)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:output method="text"/>
              <xsl:template match="/doc"><xsl:for-each select="num"><xsl:number value="{value}"/></xsl:for-each></xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync("<doc><num>100</num></doc>");
    }

    [Theory]
    [InlineData(".", "100")]
    [InlineData("data(.)", "100")]
    [InlineData("xs:int(3)", "3")]
    [InlineData("xs:short(4)", "4")]
    [InlineData("xs:float(2.5)", "3")]
    [InlineData("xs:untypedAtomic('7')", "7")]
    [InlineData("(xs:int(1), xs:untypedAtomic('2'), 3.0)", "1.2.3")]
    public async Task A_value_that_converts_to_a_number_is_numbered(string value, string expected)
        => (await Run(value)).Should().Be(expected);

    [Fact]
    public async Task An_untyped_value_that_is_not_a_number_is_XTDE0980()
    {
        var act = () => Run("xs:untypedAtomic('abc')");

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE0980");
    }
}
