using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Tests for streaming expression evaluation — consuming sub-expressions
/// inside xsl:source-document streamable="yes" must evaluate correctly
/// by reading from the XML stream, not against an empty synthetic document.
///
/// Each test verifies a specific value to catch the known failure mode:
/// before the fix, all aggregations returned 0/"" because the XPath
/// evaluated against an empty document node.
/// </summary>
public class StreamingExpressionTests
{
    private static async Task<string> TransformWithFile(
        string stylesheet, string inputXml, string inputFileName = "sample1.xml")
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"streaming-test-{Guid.NewGuid():N}");
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

    // ================================================================
    // Tester's exact reproduction case
    // ================================================================

    [Fact]
    public async Task MapWithCountAndMax_StreamedCorrectly()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0" xmlns:xs="http://www.w3.org/2001/XMLSchema"
              exclude-result-prefixes="#all" expand-text="yes">
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="sample1.xml">
                  <xsl:variable name="tally" select="map{ 'count': count(transactions/transaction),
                                                          'max':   max(transactions/transaction/@value)}"/>
                  <value count="{$tally('count')}" max="{$tally('max')}"/>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = """
            <transactions>
              <transaction value="12.51"/>
              <transaction value="3.99"/>
            </transactions>
            """;

        var result = await TransformWithFile(stylesheet, input);

        // Before fix: count="0" max="" — evaluating against empty synthetic doc
        // After fix: count="2" max="12.51" — accumulated from stream
        result.Should().Contain("count=\"2\"", "count should reflect actual elements in stream");
        result.Should().Contain("max=\"12.51\"", "max should reflect actual attribute values in stream");
        result.Should().NotContain("count=\"0\"", "count=0 means the stream was not read");
    }

    // ================================================================
    // Individual aggregation functions
    // ================================================================

    [Fact]
    public async Task Count_ReturnsActualCount_NotZero()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0" expand-text="yes">
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="data.xml">
                  <xsl:variable name="n" select="count(items/item)"/>
                  <result>{$n}</result>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = "<items><item/><item/><item/></items>";
        var result = await TransformWithFile(stylesheet, input, "data.xml");

        result.Should().Contain("<result>3</result>", "count must be 3, not 0");
        result.Should().NotContain("<result>0</result>", "0 means stream was not consumed");
    }

    [Fact]
    public async Task Sum_ReturnsActualSum_NotZero()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0" expand-text="yes">
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="data.xml">
                  <xsl:variable name="total" select="sum(orders/order/@amount)"/>
                  <result>{$total}</result>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = """<orders><order amount="10"/><order amount="20"/><order amount="30"/></orders>""";
        var result = await TransformWithFile(stylesheet, input, "data.xml");

        result.Should().Contain("<result>60</result>", "sum of 10+20+30 must be 60");
        result.Should().NotContain("<result>0</result>");
    }

    [Fact]
    public async Task Min_ReturnsActualMinimum()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0" expand-text="yes">
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="data.xml">
                  <xsl:variable name="lowest" select="min(prices/price/@value)"/>
                  <result>{$lowest}</result>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = """<prices><price value="50"/><price value="10"/><price value="30"/></prices>""";
        var result = await TransformWithFile(stylesheet, input, "data.xml");

        result.Should().Contain("<result>10</result>", "min of 50,10,30 must be 10");
    }

    [Fact]
    public async Task Avg_ReturnsActualAverage()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0" expand-text="yes">
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="data.xml">
                  <xsl:variable name="mean" select="avg(scores/score/@value)"/>
                  <result>{$mean}</result>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = """<scores><score value="10"/><score value="20"/><score value="30"/></scores>""";
        var result = await TransformWithFile(stylesheet, input, "data.xml");

        result.Should().Contain("<result>20</result>", "avg of 10,20,30 must be 20");
    }

    [Fact]
    public async Task StringJoin_ReturnsJoinedValues_NotEmpty()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0" expand-text="yes">
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="data.xml">
                  <xsl:variable name="names" select="string-join(people/person/@name, ', ')"/>
                  <result>{$names}</result>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = """<people><person name="Alice"/><person name="Bob"/><person name="Charlie"/></people>""";
        var result = await TransformWithFile(stylesheet, input, "data.xml");

        result.Should().Contain("Alice", "first name must appear");
        result.Should().Contain("Bob", "second name must appear");
        result.Should().Contain("Charlie", "third name must appear");
        result.Should().NotContain("<result></result>", "empty result means stream was not consumed");
    }

    // ================================================================
    // Empty input — verify correct defaults, not crashes
    // ================================================================

    [Fact]
    public async Task Count_EmptyInput_ReturnsZero()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0" expand-text="yes">
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="data.xml">
                  <xsl:variable name="n" select="count(items/nonexistent)"/>
                  <result>{$n}</result>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = "<items><item/></items>";
        var result = await TransformWithFile(stylesheet, input, "data.xml");

        // This SHOULD be 0 — there are genuinely no matching elements
        result.Should().Contain("<result>0</result>");
    }

    [Fact]
    public async Task Max_EmptyInput_ReturnsEmptySequence()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0" expand-text="yes">
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="data.xml">
                  <xsl:variable name="m" select="max(items/nonexistent/@value)"/>
                  <result>{if (empty($m)) then 'none' else string($m)}</result>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = "<items><item/></items>";
        var result = await TransformWithFile(stylesheet, input, "data.xml");

        // max() of empty sequence = empty sequence, which should produce 'none'
        result.Should().Contain("none");
    }

    // ================================================================
    // Multiple watchers in same source-document
    // ================================================================

    [Fact]
    public async Task MultipleVariables_EachGetsCorrectValue()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0" expand-text="yes">
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="data.xml">
                  <xsl:variable name="c" select="count(data/item)"/>
                  <xsl:variable name="s" select="sum(data/item/@value)"/>
                  <xsl:variable name="mx" select="max(data/item/@value)"/>
                  <result count="{$c}" sum="{$s}" max="{$mx}"/>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = """<data><item value="5"/><item value="15"/><item value="10"/></data>""";
        var result = await TransformWithFile(stylesheet, input, "data.xml");

        result.Should().Contain("count=\"3\"", "3 items in stream");
        result.Should().Contain("sum=\"30\"", "5+15+10=30");
        result.Should().Contain("max=\"15\"", "max of 5,15,10 is 15");
    }

    // ================================================================
    // Deeper paths
    // ================================================================

    [Fact]
    public async Task NestedPath_CountsCorrectLevel()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0" expand-text="yes">
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="data.xml">
                  <xsl:variable name="n" select="count(catalog/category/product)"/>
                  <result>{$n}</result>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = """
            <catalog>
              <category><product/><product/></category>
              <category><product/></category>
            </catalog>
            """;

        var result = await TransformWithFile(stylesheet, input, "data.xml");

        result.Should().Contain("<result>3</result>", "3 products across 2 categories");
    }

    // ================================================================
    // Value-of with consuming expression
    // ================================================================

    [Fact]
    public async Task ValueOf_WithCount_ProducesCorrectOutput()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0">
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="data.xml">
                  <result>
                    <xsl:value-of select="count(items/item)"/>
                  </result>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = "<items><item/><item/><item/><item/></items>";
        var result = await TransformWithFile(stylesheet, input, "data.xml");

        result.Should().Contain("4", "value-of should produce count from stream");
        result.Should().NotContain(">0<", "0 means stream was not consumed");
    }

    // ================================================================
    // Verify non-streaming still works (no regression)
    // ================================================================

    [Fact]
    public async Task NonStreamable_SourceDocument_StillWorks()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0" expand-text="yes">
              <xsl:template name="xsl:initial-template">
                <xsl:source-document href="data.xml">
                  <xsl:variable name="n" select="count(items/item)"/>
                  <result>{$n}</result>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var input = "<items><item/><item/></items>";
        var result = await TransformWithFile(stylesheet, input, "data.xml");

        result.Should().Contain("<result>2</result>", "non-streaming should still work");
    }
}
