using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A copy of a descendant of a streamed element keeps the namespaces in scope on it, those bound
/// on its ancestors included. Inside the streamed snapshot only the snapshot root had them, so a
/// copy of <c>&lt;b:B g:id="1"&gt;</c> whose <c>g</c> was bound on the document element was written
/// without <c>xmlns:g</c>, which is not namespace-well-formed (W3C si-element-049). Expected
/// values are Saxon-HE 12.10's.
/// </summary>
public sealed class StreamedSnapshotInScopeNamespacesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-snapns-" + Guid.NewGuid().ToString("N"));

    public StreamedSnapshotInScopeNamespacesTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "d.xml"),
            "<R xmlns='urn:d' xmlns:g='urn:g' xmlns:b='urn:b'><M><b:B g:id='1'><b:c/></b:B></M></R>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> Run(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">
                <out><xsl:source-document streamable="yes" href="d.xml">{{body}}</xsl:source-document></out>
              </xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("<xsl:for-each select='*/*'><x><xsl:copy-of select='node()'/></x></xsl:for-each>")]
    [InlineData("<xsl:for-each select='*/*'><xsl:element name='{name()}' namespace='{namespace-uri()}'><xsl:copy-of select='node()'/></xsl:element></xsl:for-each>")]
    public async Task The_copied_descendant_declares_every_namespace_in_scope_on_it(string body)
    {
        // Parsing fails on an undeclared prefix; then the namespaces in scope on the copy are those of the source.
        var copy = System.Xml.Linq.XDocument.Parse(await Run(body))
            .Descendants(System.Xml.Linq.XName.Get("B", "urn:b")).Single();
        copy.GetNamespaceOfPrefix("g")?.NamespaceName.Should().Be("urn:g");
        copy.GetDefaultNamespace().NamespaceName.Should().Be("urn:d");
        copy.Attribute(System.Xml.Linq.XName.Get("id", "urn:g"))?.Value.Should().Be("1");
    }
}
