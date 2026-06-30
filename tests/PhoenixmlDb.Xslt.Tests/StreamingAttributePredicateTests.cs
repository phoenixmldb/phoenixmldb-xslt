using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression guard for the W3C streaming cases sf-sum-019 / sf-avg-019 / sf-min-019:
/// a striding-then-climbing attribute-axis path carrying a MOTIONLESS predicate on the
/// attribute leaf step — sum(a/b/@v[xs:decimal(.) gt 0]). The predicate filters which
/// attribute values contribute to the streamed aggregate; before the fix it was dropped
/// (the attribute step's predicates were never read), so all values were aggregated.
/// </summary>
public class StreamingAttributePredicateTests
{
    private static async Task<string> Run(string stylesheet, string inputXml, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"streaming-attr-pred-{Guid.NewGuid():N}");
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

    // account/transaction/@value with three positives (1,2,4 -> sum 7) and three
    // non-positives (0, -1, -3) that the motionless predicate must exclude.
    private const string Transactions = """
        <account>
          <transaction value="1"/>
          <transaction value="-1"/>
          <transaction value="2"/>
          <transaction value="0"/>
          <transaction value="4"/>
          <transaction value="-3"/>
        </account>
        """;

    private static string Sheet(string agg) => $$"""
        <xsl:stylesheet version="3.0"
            xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
            xmlns:xs="http://www.w3.org/2001/XMLSchema"
            exclude-result-prefixes="xs">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:mode streamable="yes"/>
          <xsl:template name="xsl:initial-template">
            <xsl:source-document streamable="yes" href="t.xml">
              <out><xsl:value-of select="round({{agg}}(account/transaction/@value[xs:decimal(.) gt 0]))"/></out>
            </xsl:source-document>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task Sum_AttributeAxis_MotionlessPredicate_ExcludesNonPositive()
    {
        // 1 + 2 + 4 = 7 (excludes -1, 0, -3).
        var r = await Run(Sheet("sum"), Transactions, "t.xml");
        r.Trim().Should().Be("<out>7</out>");
    }

    [Fact]
    public async Task Avg_AttributeAxis_MotionlessPredicate_ExcludesNonPositive()
    {
        // (1 + 2 + 4) / 3 = 2.33... -> round = 2.
        var r = await Run(Sheet("avg"), Transactions, "t.xml");
        r.Trim().Should().Be("<out>2</out>");
    }

    [Fact]
    public async Task Min_AttributeAxis_MotionlessPredicate_ExcludesNonPositive()
    {
        // min of positives {1,2,4} = 1 (NOT -3/0 which the predicate drops).
        var r = await Run(Sheet("min"), Transactions, "t.xml");
        r.Trim().Should().Be("<out>1</out>");
    }
}
