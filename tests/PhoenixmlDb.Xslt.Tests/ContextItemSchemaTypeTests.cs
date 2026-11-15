using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The type in xsl:context-item as="element(*, T)" must be a type of the schemas in scope:
/// XPST0008 otherwise, when the stylesheet is compiled (W3C context-item-903). It was not
/// checked, and the template failed only when called, with XTTE0590, for a context item that
/// could never match.
/// </summary>
public sealed class ContextItemSchemaTypeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-citype-" + Guid.NewGuid().ToString("N"));

    public ContextItemSchemaTypeTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "s.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:my">
              <xs:simpleType name="percentage"><xs:restriction base="xs:decimal"/></xs:simpleType>
            </xs:schema>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> Run(string importSchema, string asType)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:my="urn:my" exclude-result-prefixes="xs my">
              <xsl:output method="text"/>
              {{importSchema}}
              <xsl:template name="t">
                <xsl:context-item as="{{asType}}" use="optional"/>
                <xsl:text>ok</xsl:text>
              </xsl:template>
              <xsl:template name="xsl:initial-template"><xsl:call-template name="t"/></xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    private const string Import = """<xsl:import-schema namespace="urn:my" schema-location="s.xsd"/>""";

    [Theory]
    [InlineData("", "element(*, my:percentage)")]
    [InlineData("", "attribute(*, my:percentage)")]
    [InlineData(Import, "element(*, my:other)")]
    public async Task A_type_no_schema_in_scope_has_is_XPST0008(string importSchema, string asType)
    {
        var act = () => Run(importSchema, asType);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XPST0008");
    }

    [Theory]
    [InlineData(Import, "element(*, my:percentage)")]
    [InlineData("", "element(*, xs:untyped)")]
    [InlineData("", "element(*, xs:anyType)")]
    [InlineData("", "element()")]
    public async Task A_type_in_scope_is_accepted(string importSchema, string asType)
        => (await Run(importSchema, asType)).Should().Be("ok");
}
