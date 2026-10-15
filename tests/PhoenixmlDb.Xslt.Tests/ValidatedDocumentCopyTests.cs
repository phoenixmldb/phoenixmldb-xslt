using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:copy-of of a document node with a validation attribute, into a temporary tree. The copy
/// was written as text and read again, so the type annotations were lost: id() and idref()
/// found nothing in a validated copy. With validation="strip" or "preserve", the is-id and
/// is-idrefs properties are unchanged (XSLT 3.0 §25.4.1); a copy cleared them.
/// </summary>
public sealed class ValidatedDocumentCopyTests
{
    private const string Stylesheet = """
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
          <xsl:output method="text"/>
          <xsl:import-schema>
            <xs:schema>
              <xs:element name="data"><xs:complexType><xs:sequence><xs:element ref="thing" maxOccurs="unbounded"/></xs:sequence></xs:complexType></xs:element>
              <xs:element name="thing"><xs:complexType><xs:attribute name="id" type="xs:ID"/><xs:attribute name="parts" type="xs:IDREFS"/></xs:complexType></xs:element>
            </xs:schema>
          </xsl:import-schema>
          <xsl:variable name="data"><data><thing id="p1" parts="r1"/><thing id="r1"/></data></xsl:variable>
          <xsl:variable name="valid"><xsl:copy-of select="$data" validation="strict"/></xsl:variable>
          <xsl:template name="main">
            <xsl:variable name="v">
              <xsl:copy-of select="SELECT" validation="VALIDATION"/>
            </xsl:variable>
            <xsl:variable name="d" as="document-node()">
              <xsl:copy-of select="SELECT" validation="VALIDATION"/>
            </xsl:variable>
            <xsl:value-of select="count(id('p1', $v)), count(idref('r1', $v)), count(id('p1', $d)), count(idref('r1', $d))"/>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Theory]
    [InlineData("$data", "strict")]
    [InlineData("$data", "lax")]
    [InlineData("$valid", "preserve")]
    [InlineData("$valid", "strip")]
    public async Task Id_and_idref_find_nodes_in_a_validated_copy_of_a_document(string select, string validation)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(Stylesheet.Replace("SELECT", select, StringComparison.Ordinal).Replace("VALIDATION", validation, StringComparison.Ordinal));
        transformer.SetInitialTemplate("main");

        (await transformer.TransformAsync((string?)null!)).Should().Be("1 1 1 1");
    }
}
