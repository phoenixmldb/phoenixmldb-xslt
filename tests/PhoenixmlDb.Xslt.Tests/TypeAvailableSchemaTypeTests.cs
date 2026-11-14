using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// type-available() knows the types of the schemas a stylesheet imports, not only the built-in
/// ones: a user-defined type was never available (W3C type-available-0149).
/// </summary>
public sealed class TypeAvailableSchemaTypeTests
{
    private static async Task<string> Run(string names)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:types" exclude-result-prefixes="xs t">
              <xsl:output method="text"/>
              <xsl:import-schema>
                <xs:schema>
                  <xs:simpleType name="short-string"><xs:restriction base="xs:string"><xs:maxLength value="5"/></xs:restriction></xs:simpleType>
                </xs:schema>
              </xsl:import-schema>
              <xsl:import-schema namespace="urn:types">
                <xs:schema targetNamespace="urn:types">
                  <xs:complexType name="pair"><xs:sequence><xs:element name="a"/></xs:sequence></xs:complexType>
                </xs:schema>
              </xsl:import-schema>
              <xsl:template match="/">
                <xsl:value-of select="({{names}}) ! type-available(.)"/>
              </xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync("<x/>")).Trim();
    }

    [Fact]
    public async Task An_imported_type_is_available_by_any_form_of_its_name()
        => (await Run("'short-string', 't:pair', 'Q{urn:types}pair', 'xs:integer'")).Should().Be("true true true true");

    [Fact]
    public async Task A_type_no_schema_declares_is_not_available()
        => (await Run("'long-string', 't:short-string', 'Q{urn:other}pair', 'u:pair'")).Should().Be("false false false false");
}
