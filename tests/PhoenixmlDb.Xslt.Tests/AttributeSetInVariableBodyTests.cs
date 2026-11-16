using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The attributes of xsl:use-attribute-sets on a literal result element belong to that element,
/// in a variable's body too. There the set's xsl:attribute instructions were added to the
/// variable's value as free attribute items, so they left the element and landed on the element
/// the variable was copied into: <c>&lt;out layout="fixed"&gt;&lt;blk&gt;&lt;t/&gt;</c> (DocBook FO
/// header tables; W3C docbook-002). Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class AttributeSetInVariableBodyTests
{
    private static async Task<string> Run(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:attribute-set name="s"><xsl:attribute name="layout">fixed</xsl:attribute></xsl:attribute-set>
              <xsl:attribute-set name="u" use-attribute-sets="s"><xsl:attribute name="w">100%</xsl:attribute></xsl:attribute-set>
              <xsl:template match="/"><out><xsl:apply-templates select="r"/></out></xsl:template>
              <xsl:template name="add"><xsl:attribute name="border">b</xsl:attribute></xsl:template>
              <xsl:template match="r">{{body}}</xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync("<r/>")).Trim();
    }

    [Theory]
    [InlineData("<xsl:variable name='c'><t xsl:use-attribute-sets='s'><col/></t></xsl:variable><blk><xsl:copy-of select='$c'/></blk>",
        "<out><blk><t layout=\"fixed\"><col/></t></blk></out>")]
    [InlineData("<xsl:variable name='c'><t xsl:use-attribute-sets='s'><xsl:call-template name='add'/><col/></t></xsl:variable><blk><xsl:copy-of select='$c'/></blk>",
        "<out><blk><t layout=\"fixed\" border=\"b\"><col/></t></blk></out>")]
    [InlineData("<xsl:variable name='c'><t xsl:use-attribute-sets='u'/></xsl:variable><blk><xsl:copy-of select='$c'/></blk>",
        "<out><blk><t layout=\"fixed\" w=\"100%\"/></blk></out>")]
    [InlineData("<xsl:variable name='c' as='element()'><t xsl:use-attribute-sets='s'/></xsl:variable><blk><xsl:copy-of select='$c'/></blk>",
        "<out><blk><t layout=\"fixed\"/></blk></out>")]
    [InlineData("<xsl:variable name='c'><t xsl:use-attribute-sets='s'/></xsl:variable><blk><xsl:value-of select='count($c/*/@*)'/></blk>",
        "<out><blk>1</blk></out>")]
    public async Task The_set_attributes_stay_on_the_element(string body, string expected)
        => (await Run(body)).Should().Be(expected);
}
