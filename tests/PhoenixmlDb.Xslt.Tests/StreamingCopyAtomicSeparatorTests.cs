using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// §5.7.2 sequence normalization under streaming shallow <c>xsl:copy</c>. When a
/// striding streamed for-each copies ATOMIC values (<c>xsl:copy select="data(.)"</c>
/// or bare <c>xsl:copy</c> over an atomic context item), the result sequence must
/// insert a single space between ADJACENT atomic values, exactly as the copy-of and
/// value-of paths do.
///
/// Before the fix the atomic branch of the streaming copy emitter wrote each value
/// with no separator (<c>-15.00-5.00-2.33-248.05</c>) instead of the space-joined
/// <c>-15.00 -5.00 -2.33 -248.05</c>. A text node adjacent to an atomic value breaks
/// the run and takes NO separator (text→atomic merges), so a run of copied text
/// nodes followed by atomic values yields e.g. <c>…16.47101 102</c>.
///
/// Mirrors W3C streaming cases si-copy-001 / si-copy-007 / si-copy-010.
/// </summary>
public class StreamingCopyAtomicSeparatorTests
{
    private static async Task<string> TransformWithFile(string stylesheet, string inputXml, string inputFileName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"streaming-copy-sep-{Guid.NewGuid():N}");
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

    private const string TxnXml = """
        <account>
          <transaction value="13.24"/>
          <transaction value="-15.00"/>
          <transaction value="6.00"/>
          <transaction value="-5.00"/>
          <transaction value="-2.33"/>
          <transaction value="-248.05"/>
        </account>
        """;

    private const string PricesXml = """
        <BOOKLIST><BOOKS>
          <ITEM><PRICE>4.95</PRICE></ITEM>
          <ITEM><PRICE>6.58</PRICE></ITEM>
        </BOOKS></BOOKLIST>
        """;

    [Fact]
    public async Task Copy_ConsumingAtomicValues_SpaceSeparated()
    {
        // si-copy-001 shape: xsl:copy select="data(.)" over a striding for-each of
        // negative @value attributes. Adjacent atomic values get a single space.
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:strip-space elements="*"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="t.xml">
                    <xsl:for-each select="account/transaction[@value &lt; 0]/@value">
                      <xsl:copy select="data(.)"/>
                    </xsl:for-each>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformWithFile(sheet, TxnXml, "t.xml");
        result.Should().Be("<out>-15.00 -5.00 -2.33 -248.05</out>");
    }

    [Fact]
    public async Task Copy_TextNodesThenAtomicValues_TextMergesAtomicSeparated()
    {
        // si-copy-007 shape: striding copy of PRICE text() nodes (which merge with no
        // separator) followed by grounded atomic values 101, 102 (space-separated).
        // The text→atomic boundary takes NO separator.
        var sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:strip-space elements="*"/>
              <xsl:template name="xsl:initial-template">
                <out>
                  <xsl:source-document streamable="yes" href="p.xml">
                    <xsl:for-each select="/BOOKLIST/BOOKS/ITEM/PRICE/text(), 101, 102">
                      <xsl:copy/>
                    </xsl:for-each>
                  </xsl:source-document>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformWithFile(sheet, PricesXml, "p.xml");
        result.Should().Be("<out>4.956.58101 102</out>");
    }
}
