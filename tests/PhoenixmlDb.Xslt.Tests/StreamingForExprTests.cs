using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Streaming XPath <c>for</c> expression: <c>for $x in CONSUMING-PATH return EXPR</c>
/// over a streamed source-document, inside an <c>xsl:value-of</c> wrapped by an LRE.
/// The <c>in</c> operand is a striding child-axis path ending in a grounding step
/// (<c>string()</c>/<c>data()</c>/<c>copy-of()</c>/<c>snapshot()</c>); per matched item
/// the grounded value binds to the range variable and the <c>return</c> expression is
/// evaluated and concatenated. Mirrors W3C strm/sx-ForExpr (sx-for-001..004).
/// </summary>
public class StreamingForExprTests
{
    private static async Task<string> TransformWithFile(string stylesheet, string inputXml, string inputFileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"streaming-forexpr-{Guid.NewGuid():N}");
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

    // Mirrors strm/docs/books.xml DIMENSIONS shape (subset, two items).
    private const string BooksXml = """
        <BOOKLIST><BOOKS OWNER="MHK">
          <ITEM CAT="MMP"><DIMENSIONS UNIT="in">8.3 5.7 1.1</DIMENSIONS></ITEM>
          <ITEM CAT="P"><DIMENSIONS UNIT="in">1.0 5.2 7.8</DIMENSIONS></ITEM>
        </BOOKS></BOOKLIST>
        """;

    private static string Sheet(string selectExpr) => $$"""
        <xsl:stylesheet version="3.0"
            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
            xmlns:xs="http://www.w3.org/2001/XMLSchema"
            exclude-result-prefixes="xs">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:template name="xsl:initial-template">
            <xsl:source-document streamable="yes" href="b.xml">
              <out><xsl:value-of select="{{selectExpr}}"/></out>
            </xsl:source-document>
          </xsl:template>
        </xsl:stylesheet>
        """;

    // 8.3+5.7+1.1 = 15.1, + string-length("8.3 5.7 1.1")=11 => 26.1
    // 1.0+5.2+7.8 = 14.0, + string-length("1.0 5.2 7.8")=11 => 25
    [Fact]
    public async Task ForExpr_StringGroundingStep_StreamsArithmetic()
    {
        var result = await TransformWithFile(
            Sheet("for $x in /BOOKLIST/BOOKS/ITEM/DIMENSIONS/string() return sum(tokenize($x, ' ')!xs:decimal(.)) + string-length($x)"),
            BooksXml, "b.xml");
        result.Trim().Should().Be("<out>26.1 25</out>");
    }

    [Fact]
    public async Task ForExpr_DataGroundingStep_StreamsArithmetic()
    {
        var result = await TransformWithFile(
            Sheet("for $x in /BOOKLIST/BOOKS/ITEM/DIMENSIONS/data() return sum(tokenize($x, ' ')!xs:decimal(.)) + string-length($x)"),
            BooksXml, "b.xml");
        result.Trim().Should().Be("<out>26.1 25</out>");
    }

    [Fact]
    public async Task ForExpr_CopyOfGroundingStep_StreamsArithmetic()
    {
        var result = await TransformWithFile(
            Sheet("for $x in /BOOKLIST/BOOKS/ITEM/DIMENSIONS/copy-of() return sum(tokenize($x, ' ')!xs:decimal(.)) + string-length($x)"),
            BooksXml, "b.xml");
        result.Trim().Should().Be("<out>26.1 25</out>");
    }

    // snapshot() grounds the node; count($x/ancestor::node()) counts the synthesized
    // ancestor chain on the snapshot: document, BOOKLIST, BOOKS, ITEM = 4.
    // 15.1 + 4 = 19.1 ; 14.0 + 4 = 18. Matches W3C sx-for-004 semantics.
    [Fact]
    public async Task ForExpr_SnapshotGroundingStep_StreamsAncestorCount()
    {
        var result = await TransformWithFile(
            Sheet("for $x in /BOOKLIST/BOOKS/ITEM/DIMENSIONS/snapshot() return sum(tokenize($x, ' ')!xs:decimal(.)) + count($x/ancestor::node())"),
            BooksXml, "b.xml");
        result.Trim().Should().Be("<out>19.1 18</out>");
    }
}
