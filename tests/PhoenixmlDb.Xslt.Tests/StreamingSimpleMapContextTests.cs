using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// OP-bucket streaming phase 1 (SM-ctx): a streamable simple-map
/// <c>LEFT ! RIGHT</c> whose LEFT is a striding/downward path and whose RIGHT
/// consumes the streamed context node per item. The streamed shape is the one
/// the conformance set sx-SimpleMappingExpr exercises: an <c>xsl:sequence</c>
/// selecting the simple-map inside a streamable <c>xsl:source-document</c>.
/// Before the fix the consuming RIGHT evaluated against an empty/synthetic
/// context and produced no output.
/// </summary>
public class StreamingSimpleMapContextTests
{
    private static async Task<string> TransformWithFile(string stylesheet, string inputXml, string inputFileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"streaming-simplemap-ctx-{Guid.NewGuid():N}");
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

    private const string AccountXml = """
        <account nr="A1">
          <transaction value="13.24">2006-02-13</transaction>
          <transaction value="-8.00">2006-02-14</transaction>
          <transaction value="6.00">2006-02-16</transaction>
        </account>
        """;

    // RIGHT atomizes + uses position() per streamed item (sx-bang-003 shape).
    [Fact]
    public async Task SimpleMapContext_AtomizeWithPosition_StreamsPerItem()
    {
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="acc.xml">
                  <out>
                    <xsl:value-of select="account/transaction ! (position(), @value!string())"/>
                  </out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformWithFile(stylesheet, AccountXml, "acc.xml");
        result.Trim().Should().Be("<out>1 13.24 2 -8.00 3 6.00</out>");
    }

    // RIGHT navigates the parent axis on each materialized item (sx-bang-008 shape).
    [Fact]
    public async Task SimpleMapContext_ParentAxis_StreamsPerItem()
    {
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="acc.xml">
                  <out>
                    <xsl:value-of select="account/transaction ! name(..)"/>
                  </out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformWithFile(stylesheet, AccountXml, "acc.xml");
        result.Trim().Should().Be("<out>account account account</out>");
    }

    // RIGHT is a conditional over an attribute-node item (sx-bang-011 shape).
    [Fact]
    public async Task SimpleMapContext_ConditionalOverAttribute_StreamsPerItem()
    {
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="acc.xml">
                  <out>
                    <xsl:value-of select="account/transaction/@value ! (if (. > 0) then string(.) else ())"/>
                  </out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformWithFile(stylesheet, AccountXml, "acc.xml");
        result.Trim().Should().Be("<out>13.24 6.00</out>");
    }
}
