using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The built-in rule of on-no-match="deep-copy" is xsl:copy-of select="." validation="preserve"
/// (XSLT 3.0 §6.7.2): an element or attribute it copies keeps its type annotation. The copy was
/// untyped.
/// </summary>
public sealed class DeepCopyPreservesTypeTests
{
    [Theory]
    [InlineData("element()", "//v", "element(v, xs:integer)")]
    [InlineData("attribute()", "//@a", "attribute(a, xs:boolean)")]
    public async Task A_deep_copy_keeps_the_type(string asType, string select, string test)
    {
        var transformer = new XsltTransformer { SourceValidation = PhoenixmlDb.Xslt.Ast.ValidationMode.Strict };
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:import-schema>
                <xs:schema>
                  <xs:element name="r"><xs:complexType><xs:sequence><xs:element ref="v"/></xs:sequence><xs:attribute name="a" type="xs:boolean"/></xs:complexType></xs:element>
                  <xs:element name="v" type="xs:integer"/>
                </xs:schema>
              </xsl:import-schema>
              <xsl:output method="text"/>
              <xsl:mode on-no-match="deep-copy"/>
              <xsl:template match="/">
                <xsl:variable name="copy" as="{asType}"><xsl:apply-templates select="{select}"/></xsl:variable>
                <xsl:value-of select="$copy instance of {test}"/>
              </xsl:template>
            </xsl:stylesheet>
            """);

        (await transformer.TransformAsync("<r a='true'><v>5</v></r>")).Should().Be("true");
    }
}
