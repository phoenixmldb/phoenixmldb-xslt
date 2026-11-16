using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The body of xsl:param or xsl:with-param with no as= makes a temporary tree, a document node,
/// even when it holds only text (XSLT 3.0 §9.4), as a variable's body does. Text alone was an
/// untypedAtomic, so <c>$p/node()</c> raised XPTY0019; and an xsl:with-param body of xsl:text
/// with elements lost the text. Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class TextOnlyParamBodyTests
{
    private const string Probe =
        "<xsl:value-of select='count($p/node()), $p instance of document-node(), string($p), $p = \"5\", $p + 1'/>";

    private static async Task<string> Run(string templates, string method = "text")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="{{method}}" omit-xml-declaration="yes"/>
              <xsl:template match="/"><xsl:apply-templates select="r"/></xsl:template>
              {{templates}}
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync("<r/>")).Trim();
    }

    [Theory]
    [InlineData("<xsl:template match='r'><xsl:param name='p'>5</xsl:param>" + Probe + "</xsl:template>")]
    [InlineData("<xsl:template match='r'><xsl:param name='p'><xsl:text>5</xsl:text></xsl:param>" + Probe + "</xsl:template>")]
    [InlineData("<xsl:template match='r'><xsl:param name='p'><xsl:value-of select='5'/></xsl:param>" + Probe + "</xsl:template>")]
    [InlineData("<xsl:template match='r'><xsl:call-template name='t'><xsl:with-param name='p'>5</xsl:with-param></xsl:call-template></xsl:template>"
        + "<xsl:template name='t'><xsl:param name='p'/>" + Probe + "</xsl:template>")]
    [InlineData("<xsl:template match='r'><xsl:call-template name='t'><xsl:with-param name='p'><xsl:text>5</xsl:text></xsl:with-param></xsl:call-template></xsl:template>"
        + "<xsl:template name='t'><xsl:param name='p'/>" + Probe + "</xsl:template>")]
    [InlineData("<xsl:template match='r'><xsl:variable name='p'><xsl:text>5</xsl:text></xsl:variable>" + Probe + "</xsl:template>")]
    public async Task A_text_only_body_is_a_document_with_one_text_node(string templates)
        => (await Run(templates)).Should().Be("1 true 5 true 6");

    [Fact]
    public async Task A_with_param_body_keeps_text_and_elements_in_order()
        => (await Run("<xsl:template match='r'><xsl:call-template name='t'><xsl:with-param name='p'><xsl:text>[</xsl:text><b/><xsl:text>]</xsl:text></xsl:with-param></xsl:call-template></xsl:template>"
                + "<xsl:template name='t'><xsl:param name='p'/><o><xsl:copy-of select='$p'/></o></xsl:template>", "xml"))
            .Should().Be("<o>[<b/>]</o>");

    [Fact]
    public async Task Markup_characters_in_the_text_stay_text()
        => (await Run("<xsl:template match='r'><xsl:param name='p'><xsl:text>a&lt;b&amp;c</xsl:text></xsl:param><xsl:value-of select='string($p), count($p/node())'/></xsl:template>"))
            .Should().Be("a<b&c 1");
}
