using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A streamable xsl:source-document whose body is just apply-templates select="." hands the
/// stream to the document-node template, whose body is what crawls it (outermost(.//x)). The
/// stream was set up from the source-document's own body, where no crawl was found, so the
/// template body ran against the empty synthetic document and produced nothing (W3C
/// si-apply-imports-068/-069/-070, si-next-match-067).
/// </summary>
public sealed class StreamedDocumentTemplateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-sdt-" + Guid.NewGuid().ToString("N"));

    public StreamedDocumentTemplateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_document_template_body_drives_the_stream_with_tunnel_params()
    {
        var doc = Path.Combine(_dir, "books.xml");
        await File.WriteAllTextAsync(doc, "<lib><shelf><book>A</book><book>B</book></shelf><book>C</book></lib>");
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:template name="main">
                <out>
                  <xsl:source-document streamable="yes" href="{{new Uri(doc).AbsoluteUri}}">
                    <xsl:apply-templates select=".">
                      <xsl:with-param name="p" select="7" tunnel="yes"/>
                    </xsl:apply-templates>
                  </xsl:source-document>
                </out>
              </xsl:template>
              <xsl:template match="/"><xsl:apply-templates select="outermost(.//book)"/></xsl:template>
              <xsl:template match="book"><xsl:param name="p" tunnel="yes"/><b p="{$p}"><xsl:value-of select="."/></b></xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("main");
        (await t.TransformAsync((string?)null)).Should().Contain("""<out><b p="7">A</b><b p="7">B</b><b p="7">C</b></out>""");
    }
}
