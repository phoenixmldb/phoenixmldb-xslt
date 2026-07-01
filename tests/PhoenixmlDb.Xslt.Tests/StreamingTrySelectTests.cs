using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xsl:try</c> wrapping a streamed aggregate inside a <c>streamable="yes"</c>
/// <c>xsl:source-document</c>. Mirrors W3C strm/si-try (si-try-111/112/113/115/119/122):
/// an <c>&lt;xsl:try select="AGG(...)"&gt;&lt;xsl:catch select="()"/&gt;&lt;/xsl:try&gt;</c>
/// whose operand is a mixed comma sequence, a simple-map cast chain, a for-expression,
/// or a climbing/attribute axis. Those operand shapes have no bare-aggregate streaming
/// watcher, so the try select must be routed to the whole-input buffer (exactly as the
/// equivalent xsl:value-of / xsl:sequence select is) — see StreamingSubtreeBufferDetector.
/// Also covers the body-form si-try-200 (xsl:try wrapping a streaming xsl:apply-templates).
/// </summary>
public class StreamingTrySelectTests
{
    // 3 ITEMs. PAGES: 100, 200, 300. DIMENSIONS: "1 2 3", "2 2 2", "3 3 3".
    private const string BooksXml = """
        <BOOKLIST><BOOKS OWNER="MHK">
          <ITEM CAT="A"><PAGES>100</PAGES><DIMENSIONS UNIT="in">1 2 3</DIMENSIONS></ITEM>
          <ITEM CAT="B"><PAGES>200</PAGES><DIMENSIONS UNIT="in">2 2 2</DIMENSIONS></ITEM>
          <ITEM CAT="C"><PAGES>300</PAGES><DIMENSIONS UNIT="in">3 3 3</DIMENSIONS></ITEM>
        </BOOKS></BOOKLIST>
        """;

    // Attribute-value avg over a climbing/attribute axis with a motionless predicate.
    // values: 10, -5, 20, 0. Positive-filtered: 10, 20 => avg 15.
    private const string AccountXml = """
        <account>
          <transaction value="10"/>
          <transaction value="-5"/>
          <transaction value="20"/>
          <transaction value="0"/>
        </account>
        """;

    private static async Task<string> RunSourceDoc(string selectExpr, string doc, string fileName)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"strm-try-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, fileName), doc);
        try
        {
            var sheet = $$"""
                <xsl:stylesheet version="3.0"
                    xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                    xmlns:xs="http://www.w3.org/2001/XMLSchema"
                    xmlns:err="http://www.w3.org/2005/xqt-errors"
                    exclude-result-prefixes="xs err">
                  <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
                  <xsl:strip-space elements="*"/>
                  <xsl:template name="xsl:initial-template">
                    <xsl:source-document streamable="yes" href="{{fileName}}">
                      <out><xsl:try select="{{selectExpr}}"><xsl:catch select="()"/></xsl:try></out>
                    </xsl:source-document>
                  </xsl:template>
                </xsl:stylesheet>
                """;
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(sheet, new Uri(dir + "/"));
            t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return (await t.TransformAsync((string?)null)).Trim();
        }
        finally { Directory.Delete(dir, true); }
    }

    // si-try-111: avg() selecting nothing (ITEM is not a child of the document node) → empty.
    [Fact]
    public async Task TrySelect_EmptyAggregate_ReturnsEmpty()
    {
        var r = await RunSourceDoc("avg(ITEM/PAGES)", BooksXml, "books.xml");
        Assert.Equal("<out/>", r);
    }

    // si-try-112: avg over a mixed comma sequence (streamed path + literals).
    // (100,200,300, 31, 32) => sum 663 / 5 = 132.6
    [Fact]
    public async Task TrySelect_MixedSequenceLiterals_Averages()
    {
        var r = await RunSourceDoc("avg((./BOOKLIST/BOOKS/ITEM/PAGES/number(), 31, 32))", BooksXml, "books.xml");
        Assert.Equal("<out>132.6</out>", r);
    }

    // si-try-113: round(avg(mixed sequence with tail() filter), 2).
    // tail(PAGES)=200,300; (200,300,31,32) => 563/4 = 140.75
    [Fact]
    public async Task TrySelect_MixedSequenceWithTail_RoundsAverage()
    {
        var r = await RunSourceDoc("round(avg((tail(./BOOKLIST/BOOKS/ITEM/PAGES)/number(), 31, 32)), 2)", BooksXml, "books.xml");
        Assert.Equal("<out>140.75</out>", r);
    }

    // si-try-115: format-number(avg(for $d in outermost(//DIMENSIONS) return product), fmt).
    // products: 1*2*3=6, 2*2*2=8, 3*3*3=27 => avg 41/3 = 13.666… => "13.667"
    [Fact]
    public async Task TrySelect_ForExpressionProduct_FormatsAverage()
    {
        var r = await RunSourceDoc(
            "format-number(avg(for $d in data(outermost(//DIMENSIONS)) return let $x := tokenize($d, '\\s')!number() return $x[1]*$x[2]*$x[3]), '99.999')",
            BooksXml, "books.xml");
        Assert.Equal("<out>13.667</out>", r);
    }

    // si-try-119: round(avg(attribute axis with motionless predicate)). positive: 10,20 => 15.
    [Fact]
    public async Task TrySelect_AttributeAxisFiltered_Averages()
    {
        var r = await RunSourceDoc("round(avg(account/transaction/@value[xs:decimal(.) gt 0]))", AccountXml, "big-transactions.xml");
        Assert.Equal("<out>15</out>", r);
    }

    // si-try-122: avg over a simple-map cast chain (NMTOKENS split + decimal cast).
    // all DIMENSIONS tokens: 1,2,3,2,2,2,3,3,3 => sum 21 / 9 = 2.333…  (7/3)
    [Fact]
    public async Task TrySelect_SimpleMapCastChain_Averages()
    {
        var r = await RunSourceDoc("round(avg(BOOKLIST/BOOKS/ITEM/DIMENSIONS!xs:NMTOKENS(.)!xs:decimal(.)), 4)", BooksXml, "books.xml");
        Assert.Equal("<out>2.3333</out>", r);
    }

    // si-try-200: body-form xsl:try wrapping a streaming xsl:apply-templates (shallow-copy identity).
    [Fact]
    public async Task TryBody_ApplyTemplatesShallowCopy_EchoesInput()
    {
        var inputXml = "<root><section><title>s1</title></section><section><title>s2</title></section></root>";
        var sheet = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
               version="3.0" exclude-result-prefixes="#all" expand-text="yes">
               <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
               <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
               <xsl:template match="/">
                  <xsl:try>
                     <xsl:apply-templates/>
                     <xsl:catch/>
                  </xsl:try>
               </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(sheet, new Uri(Path.GetTempPath() + "/"));
        var r = (await t.TransformAsync(inputXml)).Trim();
        Assert.Equal(inputXml, r);
    }
}
