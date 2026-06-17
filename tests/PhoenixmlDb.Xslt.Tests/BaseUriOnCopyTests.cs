using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §11.9.1: xsl:copy / xsl:copy-of must preserve the SOURCE document's
/// base URI on copied nodes. After copying an element into a temp tree (variable),
/// base-uri() of the copy must return the source input document's URI, not the
/// stylesheet URI.
/// </summary>
public class BaseUriOnCopyTests
{
    private const string SourceUri = "file:///tmp/baseuri-test/in.xml";
    private const string Input = """<doc><child fileref="../media/x.mp3"/></doc>""";

    private static async System.Threading.Tasks.Task<string> RunAsync(
        string stylesheet, string input, string sourceUri = SourceUri)
    {
        var transformer = new XsltTransformer();
        transformer.SetSourceDocumentUri(new System.Uri(sourceUri));
        await transformer.LoadStylesheetAsync(stylesheet);
        return (await transformer.TransformAsync(input)).Trim();
    }

    [Fact]
    public async Task Copy_of_into_variable_preserves_source_base_uri()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="v"><xsl:copy-of select="/*"/></xsl:variable>
                <xsl:value-of select="base-uri($v/doc/child)"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await RunAsync(ss, Input);
        result.Should().Be("file:///tmp/baseuri-test/in.xml");
    }

    [Fact]
    public async Task Identity_copy_into_variable_preserves_source_base_uri()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="v">
                  <xsl:apply-templates select="/*" mode="m"/>
                </xsl:variable>
                <xsl:value-of select="base-uri($v/doc/child)"/>
              </xsl:template>
              <xsl:template match="@*|node()" mode="m">
                <xsl:copy><xsl:apply-templates select="@*|node()" mode="m"/></xsl:copy>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await RunAsync(ss, Input);
        result.Should().Be("file:///tmp/baseuri-test/in.xml");
    }

    [Fact]
    public async Task Copy_of_element_with_xml_base_keeps_xml_base()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="v"><xsl:copy-of select="/*"/></xsl:variable>
                <xsl:value-of select="base-uri($v/doc)"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var input = """<doc xml:base="http://example.com/sub/"><child/></doc>""";
        var result = await RunAsync(ss, input);
        result.Should().Be("http://example.com/sub/");
    }

    [Fact]
    public async Task Literal_result_element_uses_static_base_not_source()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="v"><made/></xsl:variable>
                <xsl:value-of select="base-uri($v/made)"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await RunAsync(ss, Input);
        result.Should().NotBe("file:///tmp/baseuri-test/in.xml");
    }

    [Fact]
    public async Task Document_node_variable_via_xsl_document_preserves_source_base_uri()
    {
        // xsl:document temporarily clears the sequence accumulator, so xsl:sequence of a
        // SOURCE element serializes to TEXT and is reparsed — exactly the boundary the
        // base-URI sentinel bridges. base-uri() of the copied child must be the source URI.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="d" as="document-node()">
                  <xsl:document><xsl:sequence select="/*"/></xsl:document>
                </xsl:variable>
                <xsl:value-of select="base-uri($d/doc/child)"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await RunAsync(ss, Input);
        result.Should().Be("file:///tmp/baseuri-test/in.xml");
    }

    [Fact]
    public async Task Node_sequence_copy_of_preserves_source_base_uri()
    {
        // as="element()*": copy-of of source elements into a sequence-typed variable.
        // Handled by the accumulator path (CopySourceBaseUri stamped directly) — should
        // already pass, guards against regression.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="v" as="element()*"><xsl:copy-of select="/*"/></xsl:variable>
                <xsl:value-of select="base-uri($v[1]/child)"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await RunAsync(ss, Input);
        result.Should().Be("file:///tmp/baseuri-test/in.xml");
    }

    [Fact]
    public async Task Sentinel_never_leaks_into_final_output()
    {
        // Build a temp tree from a source copy (EMIT fires internally), then copy the
        // temp tree back to the result as XML. The internal base-URI sentinel must be
        // fully stripped on reparse and never appear in user-visible output.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/">
                <xsl:variable name="d" as="document-node()">
                  <xsl:document><xsl:sequence select="/*"/></xsl:document>
                </xsl:variable>
                <xsl:copy-of select="$d"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await RunAsync(ss, Input);
        result.Should().NotContain("_pxbase_");
        result.Should().NotContain("phoenixmldb/internal/base-uri");
        result.Should().Be("""<doc><child fileref="../media/x.mp3"/></doc>""");
    }
}
