using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:source-document type="T" validates the document it reads against T (XSLT 3.0 §25.4),
/// streamed or not. The attribute was not read: the document stayed untyped, and an invalid one
/// was processed without an error (W3C stream-103/104, non-stream-103/104).
/// </summary>
public sealed class SourceDocumentTypeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-sdtype-" + Guid.NewGuid().ToString("N"));

    public SourceDocumentTypeTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "s.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:complexType name="list-type">
                <xs:sequence><xs:element name="n" type="xs:decimal" maxOccurs="unbounded"/></xs:sequence>
              </xs:complexType>
            </xs:schema>
            """);
        File.WriteAllText(Path.Combine(_dir, "valid.xml"), "<list><n>1.5</n><n>2</n></list>");
        File.WriteAllText(Path.Combine(_dir, "invalid.xml"), "<list><n>one</n></list>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> Run(string href, bool streamable, string attributes = "type=\"list-type\"")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:import-schema schema-location="s.xsd"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="{{(streamable ? "yes" : "no")}}" href="{{href}}" {{attributes}}>
                  <out><xsl:value-of select="//n instance of element(*, xs:decimal)+"/></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_valid_document_has_the_types_of_the_type(bool streamable)
        => (await Run("valid.xml", streamable)).Should().Be("<out>true</out>");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_invalid_document_is_XTTE1540(bool streamable)
    {
        var act = () => Run("invalid.xml", streamable);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTTE1540");
    }

    [Fact]
    public async Task Without_the_type_the_document_is_untyped()
        => (await Run("valid.xml", streamable: false, attributes: "")).Should().Be("<out>false</out>");

    [Fact]
    public async Task Type_and_validation_together_are_XTSE1505()
    {
        var act = () => Run("valid.xml", streamable: false, attributes: "type=\"list-type\" validation=\"strict\"");

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTSE1505");
    }
}
