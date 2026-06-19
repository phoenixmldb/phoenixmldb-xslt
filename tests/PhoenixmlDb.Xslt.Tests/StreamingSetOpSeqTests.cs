using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// OP-bucket streaming phase 2 (SETOP+SEQ): a streamable per-item set/sequence
/// operator <c>LEFT ! (streamed-op COMBINE grounded)</c> emitted through
/// <c>xsl:copy-of</c> — LEFT a striding path (e.g. <c>ITEM[1]</c>), RIGHT a
/// set/sequence operator (<c>union</c>/<c>except</c>/<c>intersect</c>/comma/
/// square-array) combining the per-item streamed nodes with grounded nodes.
/// Mirrors the conformance r-014 shape in sx-UnionExpr/sx-ExceptExpr/etc.
/// Before the fix, <c>xsl:copy-of</c> did not trigger the SM-ctx streaming
/// handoff so the wrapper emitted empty (<c>&lt;out/&gt;</c>).
/// </summary>
public class StreamingSetOpSeqTests
{
    private static async Task<string> TransformWithFile(string stylesheet, string inputXml, string inputFileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"streaming-setop-seq-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, inputFileName);
        await File.WriteAllTextAsync(inputPath, inputXml);
        try
        {
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync(stylesheet, new Uri(tempDir + "/"));
            transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return await transformer.TransformAsync((string?)null);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private const string ItemXml = """
        <BOOKLIST><BOOKS>
          <ITEM><TITLE>T1</TITLE><AUTHOR>A1</AUTHOR></ITEM>
          <ITEM><TITLE>T2</TITLE></ITEM>
        </BOOKS></BOOKLIST>
        """;

    // header: $insertion = (<x/>, <y/>); RIGHT = (* union $insertion) over ITEM[1].
    private static string Sheet(string rightExpr) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:variable name="insertion" as="element()*"><x/><y/></xsl:variable>
          <xsl:template name="xsl:initial-template">
            <xsl:source-document streamable="yes" href="b.xml">
              <out><xsl:copy-of select="/BOOKLIST/BOOKS/ITEM[1] ! ({{rightExpr}})"/></out>
            </xsl:source-document>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task PerItemUnion_StreamsGroundedAndChildren()
    {
        var result = await TransformWithFile(Sheet("* union $insertion"), ItemXml, "b.xml");
        // union of ITEM[1]'s element children with the two grounded elements.
        // The result is sorted by node identity (XdmNode.Id). Across the
        // grounded/streamed boundary the grounded $insertion nodes were allocated
        // before the per-item materialized children, so they sort first. The W3C
        // catalog (sx-union-014/016/114/116) accepts either ordering via <any-of>;
        // this is the grounded-first variant the streaming pass produces.
        result.Trim().Should().Be("<out><x/><y/><TITLE>T1</TITLE><AUTHOR>A1</AUTHOR></out>");
    }

    [Fact]
    public async Task PerItemExcept_KeepsStreamedChildren()
    {
        var result = await TransformWithFile(Sheet("* except $insertion"), ItemXml, "b.xml");
        // grounded $insertion nodes are distinct identities → none of * removed.
        result.Trim().Should().Be("<out><TITLE>T1</TITLE><AUTHOR>A1</AUTHOR></out>");
    }

    [Fact]
    public async Task PerItemComma_StreamedThenGrounded()
    {
        var result = await TransformWithFile(Sheet("*, $insertion"), ItemXml, "b.xml");
        result.Trim().Should().Be("<out><TITLE>T1</TITLE><AUTHOR>A1</AUTHOR><x/><y/></out>");
    }
}
