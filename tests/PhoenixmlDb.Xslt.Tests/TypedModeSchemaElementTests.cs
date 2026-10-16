using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// In a mode with typed="strict" or typed="lax", the element name of the first step of a
/// template rule's pattern is matched as schema-element(E) when the schemas declare E (XSLT 3.0
/// §6.6.1): an element of that name with another type does not match it. It was matched by name.
/// </summary>
public sealed class TypedModeSchemaElementTests
{
    private const string Stylesheet = """
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
          <xsl:import-schema>
            <xs:schema><xs:element name="e" type="xs:decimal"/></xs:schema>
          </xsl:import-schema>
          <xsl:output method="text"/>
          <xsl:mode typed="TYPED"/>
          <xsl:template name="main">
            <xsl:variable name="valid" as="element()"><e xsl:validation="strict">2.5</e></xsl:variable>
            <xsl:variable name="other" as="element()"><e xsl:type="xs:string">x</e></xsl:variable>
            <xsl:apply-templates select="$valid, $other"/>
          </xsl:template>
          <xsl:template match="e">[declared]</xsl:template>
          <xsl:template match="*:e" priority="-1">[other]</xsl:template>
        </xsl:stylesheet>
        """;

    [Theory]
    [InlineData("strict")]
    [InlineData("lax")]
    public async Task A_declared_name_matches_only_an_element_of_its_declaration(string typed)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(Stylesheet.Replace("TYPED", typed, StringComparison.Ordinal));
        transformer.SetInitialTemplate("main");

        (await transformer.TransformAsync((string?)null!)).Should().Be("[declared][other]");
    }
}
