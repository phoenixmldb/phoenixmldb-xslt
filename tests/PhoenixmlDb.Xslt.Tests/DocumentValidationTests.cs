using AwesomeAssertions;
using PhoenixmlDb.Xslt.Ast;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XsltTransformer.DocumentValidation validates the documents fn:doc reads against the schemas
/// in scope, as Saxon's -val does: their nodes are typed, and one that is not valid fails the
/// transformation. Without it they are read untyped, as before. Only the principal source could
/// be validated (SourceValidation), so a stylesheet matching typed nodes of a document it reads
/// matched nothing (W3C validation-2001, validation-2002).
/// </summary>
public sealed class DocumentValidationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("docval").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<string> Run(ValidationMode? validation, string document)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "d.xml"), document);
        var transformer = new XsltTransformer { DocumentValidation = validation };
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:import-schema>
                <xs:schema>
                  <xs:element name="r"><xs:complexType><xs:sequence><xs:element ref="v"/></xs:sequence></xs:complexType></xs:element>
                  <xs:element name="v" type="xs:integer"/>
                </xs:schema>
              </xsl:import-schema>
              <xsl:output method="text"/>
              <xsl:template name="main">
                <xsl:value-of select="doc('d.xml')//v instance of element(v, xs:integer)"/>
              </xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "s.xsl")));
        transformer.SetInitialTemplate("main");
        return await transformer.TransformAsync((string?)null);
    }

    [Fact]
    public async Task A_document_read_by_doc_is_typed_when_validated()
        => (await Run(ValidationMode.Strict, "<r><v>5</v></r>")).Should().Be("true");

    [Fact]
    public async Task A_document_read_by_doc_is_untyped_by_default()
        => (await Run(null, "<r><v>5</v></r>")).Should().Be("false");

    [Fact]
    public async Task A_document_that_is_not_valid_fails_the_transformation()
    {
        var act = () => Run(ValidationMode.Strict, "<r><v>five</v></r>");
        (await act.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain("XTTE1510");
    }
}
