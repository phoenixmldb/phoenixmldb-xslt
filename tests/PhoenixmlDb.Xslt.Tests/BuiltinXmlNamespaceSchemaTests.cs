using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSD 1.0 §4.2.6.2 lets a schema import the XML namespace with no <c>schemaLocation</c>,
/// because every processor is expected to already know it. .NET's <c>XmlSchemaSet</c> does not,
/// so a schema containing
/// <code>
///   &lt;xs:import namespace="http://www.w3.org/XML/1998/namespace"/&gt;
///   &lt;xs:attribute ref="xml:lang" use="optional"/&gt;
/// </code>
/// failed to compile with "the 'xml:lang' attribute is not declared", and that took the whole
/// <c>xsl:import-schema</c> down with XQST0059 — the stylesheet would not load at all. The
/// failure is in the imported .xsd, so no amount of care in the stylesheet avoids it.
///
/// The default schema provider is now seeded with the XML namespace's four attributes.
/// </summary>
public sealed class BuiltinXmlNamespaceSchemaTests : System.IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "xmlns-schema-" + Path.GetRandomFileName());

    public BuiltinXmlNamespaceSchemaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string WriteSchema(string body)
    {
        var path = Path.Combine(_dir, "s.xsd");
        File.WriteAllText(path, $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"
                       xmlns:xml="http://www.w3.org/XML/1998/namespace"
                       elementFormDefault="qualified">
              <xs:import namespace="http://www.w3.org/XML/1998/namespace"/>
              {body}
            </xs:schema>
            """);
        return path;
    }

    private async Task<string> RunWithSchema(string schemaBody)
    {
        var path = WriteSchema(schemaBody).Replace("\\", "/", System.StringComparison.Ordinal);
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="text"/>
              <xsl:import-schema schema-location="{path}"/>
              <xsl:template match="/" name="xsl:initial-template">loaded</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        return await t.TransformAsync("<in/>").ConfigureAwait(true);
    }

    private static async Task<XsltTransformer> LoadWithSchema(string path, string body = "loaded")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:o="urn:o" expand-text="yes">
              <xsl:output method="text"/>
              <xsl:import-schema schema-location="{path.Replace("\\", "/", System.StringComparison.Ordinal)}"/>
              <xsl:template match="/" name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """).ConfigureAwait(true);
        return t;
    }

    [Theory]
    // with an import of the XML namespace, which only the default provider's own knowledge satisfies
    [InlineData("<xs:element name=\"e\"><xs:complexType><xs:attribute ref=\"xml:id\"/></xs:complexType></xs:element>")]
    [InlineData("<xs:element name=\"e\" type=\"xs:integer\"/>")]
    public async Task Two_transformers_that_import_one_schema_share_its_compiled_form(string schemaBody)
    {
        // Each XsltTransformer read and compiled the schema again (xslt#355).
        var path = WriteSchema(schemaBody);
        var first = await LoadWithSchema(path);
        var second = await LoadWithSchema(path);

        first.UsesSharedSchemaSetForTest.Should().BeTrue();
        second.SchemaSetForTest.Should().BeSameAs(first.SchemaSetForTest);
        (await second.TransformAsync("<in/>")).Should().Be("loaded");
    }

    [Fact]
    public async Task A_schema_that_changes_on_disk_is_compiled_again()
    {
        var path = Path.Combine(_dir, "o.xsd");
        void Write(int most) => File.WriteAllText(path, $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:o">
              <xs:simpleType name="small"><xs:restriction base="xs:integer"><xs:maxInclusive value="{most}"/></xs:restriction></xs:simpleType>
            </xs:schema>
            """);
        async Task<XsltTransformer> Load()
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync($"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:o="urn:o">
                  <xsl:output method="text"/>
                  <xsl:import-schema namespace="urn:o" schema-location="{path.Replace("\\", "/", System.StringComparison.Ordinal)}"/>
                  <xsl:template match="/"><xsl:value-of select="'70' castable as o:small"/></xsl:template>
                </xsl:stylesheet>
                """).ConfigureAwait(true);
            return t;
        }

        Write(9);
        File.SetLastWriteTimeUtc(path, new System.DateTime(2024, 1, 1, 0, 0, 0, System.DateTimeKind.Utc));
        var first = await Load();
        first.UsesSharedSchemaSetForTest.Should().BeTrue();
        (await first.TransformAsync("<in/>")).Should().Be("false");

        Write(99);
        var second = await Load();
        (await second.TransformAsync("<in/>")).Should().Be("true");
        (await first.TransformAsync("<in/>")).Should().Be("false", "a transformer keeps the schema it was compiled with");
    }

    [Theory]
    // All four attributes the XML namespace defines — a schema may reference any of them.
    [InlineData("lang")]
    [InlineData("space")]
    [InlineData("base")]
    [InlineData("id")]
    public async Task SchemaReferencingAnXmlNamespaceAttribute_Loads(string attr)
    {
        var body = $"""
            <xs:element name="e">
              <xs:complexType><xs:attribute ref="xml:{attr}" use="optional"/></xs:complexType>
            </xs:element>
            """;
        (await RunWithSchema(body).ConfigureAwait(true)).Should().Be("loaded");
    }

    [Fact]
    public async Task SchemaUsingTheSpecialAttrsGroup_Loads()
    {
        // xml.xsd also defines this attributeGroup, and real schemas reference it.
        var body = """
            <xs:element name="e">
              <xs:complexType><xs:attributeGroup ref="xml:specialAttrs"/></xs:complexType>
            </xs:element>
            """;
        (await RunWithSchema(body).ConfigureAwait(true)).Should().Be("loaded");
    }

    [Fact]
    public async Task ASchemaWithARealErrorStillReports_XQST0059()
    {
        // Seeding must not turn schema loading into a no-op that swallows genuine errors.
        var path = Path.Combine(_dir, "bad.xsd").Replace("\\", "/", System.StringComparison.Ordinal);
        await File.WriteAllTextAsync(path,
            """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"><xs:element name="e" type="xs:nosuchtype"/></xs:schema>""").ConfigureAwait(true);
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:import-schema schema-location="{path}"/>
              <xsl:template match="/" name="xsl:initial-template">loaded</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        var act = async () => await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        (await act.Should().ThrowAsync<System.Exception>().ConfigureAwait(true))
            .Which.Message.Should().Contain("XQST0059");
    }
}
