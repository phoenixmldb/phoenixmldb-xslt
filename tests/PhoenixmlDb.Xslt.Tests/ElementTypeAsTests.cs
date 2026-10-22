using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// as="element(N, T)" checks the element's type annotation: T or a type derived from it. The type
/// was dropped when the as attribute was read, so element(*, xs:integer) accepted any element (W3C
/// error-0570b). The element a template with as= returns keeps its annotation: it was read back
/// from its text, untyped, so element(*, T) of a schema type failed for a valid result (W3C as-3701).
/// </summary>
public sealed class ElementTypeAsTests
{
    private static async Task<string> RunAsync(string declarations, string body)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            xmlns:t="urn:t" version="3.0">
              <xsl:import-schema namespace="urn:t">
                <xs:schema targetNamespace="urn:t">
                  <xs:complexType name="T"><xs:simpleContent><xs:extension base="xs:string"/></xs:simpleContent></xs:complexType>
                </xs:schema>
              </xsl:import-schema>
              <xsl:output method="text"/>
              <xsl:variable name="tree"><e xsl:type="xs:NMTOKEN">A</e></xsl:variable>
              {declarations}
              <xsl:template name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return await transformer.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("", """<xsl:variable name="v" select="$tree/e" as="element(*, xs:integer)"/><xsl:value-of select="$v"/>""")]
    [InlineData("""<xsl:variable name="g" select="$tree/e" as="element(*, xs:integer)"/>""", """<xsl:value-of select="$g"/>""")]
    [InlineData("", """<xsl:variable name="v" as="element(e, xs:integer)"><xsl:sequence select="$tree/e"/></xsl:variable><xsl:value-of select="$v"/>""")]
    public async Task An_element_of_another_type_is_XTTE0570(string declarations, string body)
    {
        var act = () => RunAsync(declarations, body);
        (await act.Should().ThrowAsync<XsltException>()).WithMessage("*XTTE0570*");
    }

    [Theory]
    [InlineData("", """<xsl:variable name="v" select="$tree/e" as="element(*, xs:NMTOKEN)"/><xsl:value-of select="$v"/>""")]
    [InlineData("", """<xsl:variable name="v" select="$tree/e" as="element(*, xs:string)"/><xsl:value-of select="$v"/>""")]
    [InlineData("""<xsl:variable name="g" select="$tree/e" as="element(e, xs:token)"/>""", """<xsl:value-of select="$g"/>""")]
    public async Task An_element_of_the_type_or_a_derived_type_is_accepted(string declarations, string body)
        => (await RunAsync(declarations, body)).Should().Be("A");

    [Theory]
    [InlineData("""<xsl:template name="make" as="element()"><t:x xsl:type="t:T">A</t:x></xsl:template>""")]
    [InlineData("""<xsl:template name="make" as="element(*, t:T)"><t:x xsl:type="t:T">A</t:x></xsl:template>""")]
    public async Task The_element_a_template_returns_keeps_its_type(string template)
    {
        var result = await RunAsync(template, """
            <xsl:variable name="v" as="element(*, t:T)"><xsl:call-template name="make"/></xsl:variable>
            <xsl:value-of select="$v instance of element(*, t:T)"/>
            """);
        result.Should().Be("true");
    }
}
