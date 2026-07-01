using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

public class StreamingWrappedAggregationTests
{
    private static async Task<string> Run(string stylesheet, string inputXml, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"streaming-wrapagg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, file), inputXml);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(stylesheet, new Uri(dir + "/"));
            t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return await t.TransformAsync((string?)null);
        }
        finally { Directory.Delete(dir, true); }
    }

    private const string Books = """
        <BOOKLIST><BOOKS>
          <ITEM><PRICE>4.95</PRICE></ITEM>
          <ITEM><PRICE>6.58</PRICE></ITEM>
          <ITEM><PRICE>10.00</PRICE></ITEM>
        </BOOKS></BOOKLIST>
        """;

    private static string Sheet(string select) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:mode streamable="yes"/>
          <xsl:template name="xsl:initial-template">
            <xsl:source-document streamable="yes" href="b.xml">
              <out><xsl:value-of select="{{select}}"/></out>
            </xsl:source-document>
          </xsl:template>
        </xsl:stylesheet>
        """;

    // head(//PRICE/text()) ! (.+1): first PRICE text node value + 1.
    [Fact]
    public async Task Head_DescendantTextTail_SimpleMapTail()
    {
        var r = await Run(Sheet("head(//PRICE/text()) ! (number(.) + 1)"), Books, "b.xml");
        r.Trim().Should().Be("<out>5.95</out>");
    }

    // outermost(/BOOKLIST/BOOKS/ITEM/PRICE)[position() mod 2 = 0]: child axis, even positions.
    [Fact]
    public async Task Outermost_ChildAxis_PositionalPredicate()
    {
        var r = await Run(Sheet("outermost(/BOOKLIST/BOOKS/ITEM/PRICE)[position() mod 2 = 0]"), Books, "b.xml");
        // PRICEs are 4.95, 6.58, 10.00 → outermost = all three (flat) → even = 6.58
        r.Trim().Should().Be("<out>6.58</out>");
    }

    // remove(//PRICE, 2)[position() lt 3]: remove 2nd, then first 2 of the rest.
    [Fact]
    public async Task Remove_DescendantPath_PositionalPredicate()
    {
        var r = await Run(Sheet("remove(//PRICE, 2)[position() lt 3]"), Books, "b.xml");
        // remove pos 2 → (4.95, 10.00); [position() lt 3] → both
        r.Trim().Should().Be("<out>4.95 10.00</out>");
    }

    // Regression pin for sx-arithmetic-002 (broke at engine e4489d5). A Group A
    // wrapped-aggregation FilterExpression `outermost(//PRICE)[1]` used as the LEFT
    // operand of arithmetic: `outermost(//PRICE)[1] + $two`. The BinaryExpression
    // watcher-rewrite must substitute ONLY the watcher's captured base and PRESERVE
    // the outer positional predicate around the substituted variable — producing
    // `$__streaming_watcher_N[1] + $two`, a single-item arithmetic (4.95 + 2 = 6.95),
    // NOT the whole unfiltered PRICE sequence (which raised "An arithmetic operand
    // is a sequence of more than one item").
    [Fact]
    public async Task Outermost_OuterPositionalPredicate_ArithmeticOperand()
    {
        const string sheet = """
            <xsl:stylesheet version="3.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:mode streamable="yes"/>
              <xsl:param name="two" select="2"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="b.xml">
                  <out><xsl:copy-of select="outermost(//PRICE)[1] + $two"/></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await Run(sheet, Books, "b.xml");
        // first PRICE 4.95 + $two 2 = 6.95 (single item, not a >1-item sequence error)
        r.Trim().Should().Be("<out>6.95</out>");
    }
}
