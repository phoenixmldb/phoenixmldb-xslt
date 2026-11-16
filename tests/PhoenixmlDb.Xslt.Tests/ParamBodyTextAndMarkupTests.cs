using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The value of an xsl:param or xsl:with-param body that writes text with xsl:text or
/// xsl:value-of and also constructs elements is all of it, in order. That text was kept apart from
/// the markup and, when there was any, it alone was the value: the elements were dropped, so
/// <c>&lt;xsl:text&gt;[&lt;/xsl:text&gt;&lt;b/&gt;&lt;xsl:text&gt;]&lt;/xsl:text&gt;</c> gave "[]"
/// (DocBook's biblioentry.label; W3C docbook-001). Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class ParamBodyTextAndMarkupTests
{
    private static async Task<string> Run(string paramBody, string use = "<xsl:copy-of select='$p'/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/"><out><xsl:apply-templates select="r"/></out></xsl:template>
              <xsl:template match="r"><xsl:param name="p">{{paramBody}}</xsl:param><p>{{use}}</p></xsl:template>
              <xsl:template match="a"><i>A</i></xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync("<r><a/></r>")).Trim();
    }

    [Theory]
    [InlineData("<xsl:text>[</xsl:text><b>X</b><xsl:text>]</xsl:text>", "<out><p>[<b>X</b>]</p></out>")]
    [InlineData("<xsl:text>[</xsl:text><xsl:apply-templates select='a'/>", "<out><p>[<i>A</i></p></out>")]
    [InlineData("<xsl:value-of select='\"v\"'/><b/><xsl:text>t</xsl:text>", "<out><p>v<b/>t</p></out>")]
    [InlineData("<xsl:text>a&lt;b</xsl:text><b/>", "<out><p>a&lt;b<b/></p></out>")]
    public async Task Text_and_elements_are_kept_in_order(string paramBody, string expected)
        => (await Run(paramBody)).Should().Be(expected);

    [Fact]
    public async Task The_value_has_the_text_and_element_children()
        => (await Run("<xsl:text>[</xsl:text><b>X</b><xsl:text>]</xsl:text>", "<xsl:value-of select='count($p/node())'/>"))
            .Should().Be("<out><p>3</p></out>");
}
