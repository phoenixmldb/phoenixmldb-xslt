using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Elements built by json-to-xml carry the functions namespace under the node store's id, so a
/// template pattern naming them (match="fn:null") matches. They carried the predeclared
/// NamespaceId.Fn, which is not the store's id once the store has interned the URI — so
/// `instance of element(fn:null)` held and the template never matched (the W3C XSLT
/// implementation of xml-to-json, xml-to-json-A2/B2).
/// </summary>
public sealed class JsonToXmlPatternMatchTests
{
    [Theory]
    [InlineData("null", "null")]
    [InlineData("{\"a\": [1, true]}", "map/array/number/boolean")]
    public async Task Templates_match_json_to_xml_elements_by_name(string json, string expected)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:fn="http://www.w3.org/2005/xpath-functions">
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">
                <xsl:apply-templates select="json-to-xml('{{json.Replace("\"", "&quot;", StringComparison.Ordinal)}}')//*" mode="m"/>
              </xsl:template>
              <xsl:template match="fn:null | fn:map | fn:array | fn:number | fn:boolean" mode="m">[<xsl:value-of select="local-name()"/>]</xsl:template>
              <xsl:template match="*" mode="m">[UNMATCHED]</xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("xsl:initial-template");
        var result = await t.TransformAsync((string?)null);
        result.Should().NotContain("UNMATCHED")
            .And.Be("[" + expected.Replace("/", "][", StringComparison.Ordinal) + "]");
    }
}
