using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// format-number() with an exponent part in the picture (XPath 3.1 §4.7.4). The XSLT engine had
/// none: '0.0000e0' gave 123.45600 for 123.456 (W3C format-number-069a).
/// </summary>
public sealed class FormatNumberExponentTests
{
    private static async Task<string> Run(string decimalFormat, string select)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              {{decimalFormat}}
              <xsl:output method="text"/>
              <xsl:template name="main"><xsl:value-of select="{{select}}"/></xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("main");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("", "format-number(123.456, '0.0000e0')", "1.2346e2")]
    [InlineData("", "format-number(-0.00123, '0.0000e0')", "-1.2300e-3")]
    [InlineData("<xsl:decimal-format exponent-separator=\"E\"/>", "format-number(123.456, '0.0000E0')", "1.2346E2")]
    [InlineData("", "format-number(123.456, '#,##0.00')", "123.46")]
    public async Task The_exponent_part_is_formatted(string decimalFormat, string select, string expected)
        => (await Run(decimalFormat, select)).Should().Be(expected);
}
