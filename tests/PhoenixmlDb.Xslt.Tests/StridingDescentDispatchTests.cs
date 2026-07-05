using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Repro for the striding-descent dispatch gap: a streamed
/// <c>xsl:apply-templates select="&lt;downward path&gt;"</c> must run the MATCHED
/// template's body per selected node.
/// </summary>
public class StridingDescentDispatchTests
{
    private static async Task<string> TransformStreamable(string stylesheet, string inputXml)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(inputXml);
    }

    [Fact]
    public async Task StreamedApplyTemplates_StridingSelect_RunsMatchedRuleBody()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="utf-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="yes"/>
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/">
                <out><xsl:apply-templates select="root/item"/></out>
              </xsl:template>
              <xsl:template match="item"><prio5/></xsl:template>
            </xsl:stylesheet>
            """;
        var input = "<root><item>a</item><item>b</item></root>";
        var result = await TransformStreamable(stylesheet, input);
        result.Should().Be("<out><prio5/><prio5/></out>");
    }
}
