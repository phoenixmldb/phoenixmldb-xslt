using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

public class StreamingForEachGroupTest
{
    [Fact]
    public async Task StreamingForEachGroup_starting_with_h1_wraps_groups_in_section()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                xmlns:xs="http://www.w3.org/2001/XMLSchema"
                exclude-result-prefixes="#all">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <xsl:copy>
                        <xsl:for-each-group select="*" group-starting-with="h1">
                            <section>
                                <xsl:apply-templates select="current-group()"/>
                            </section>
                        </xsl:for-each-group>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """);

        var input = """
            <html>
              <body>
                <h1>Section 1</h1>
                <p>p1</p>
                <p>p2</p>
                <h1>Section 2</h1>
                <p>p3</p>
              </body>
            </html>
            """;
        var result = await transformer.TransformAsync(input);
        result.Should().Contain("<section>", $"actual:\n{result}");
        result.Should().Contain("<h1>Section 1</h1>", $"actual:\n{result}");
        result.Should().NotContain("<:body", $"actual:\n{result}");
        var sectionCount = System.Text.RegularExpressions.Regex.Count(result, "<section>");
        sectionCount.Should().Be(2, $"actual:\n{result}");
    }

    [Fact]
    public async Task StreamingForEachGroup_adjacent_wraps_adjacent_list_items_in_list()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                xmlns:xs="http://www.w3.org/2001/XMLSchema"
                exclude-result-prefixes="#all">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <xsl:copy>
                        <xsl:for-each-group select="*" group-adjacent="boolean(self::list-item)">
                            <xsl:choose>
                                <xsl:when test="current-grouping-key()">
                                    <list>
                                        <xsl:apply-templates select="current-group()"/>
                                    </list>
                                </xsl:when>
                                <xsl:otherwise>
                                    <xsl:apply-templates select="current-group()"/>
                                </xsl:otherwise>
                            </xsl:choose>
                        </xsl:for-each-group>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """);

        var input = """
            <root>
                <body>
                    <p>p1</p>
                    <list-item>li1</list-item>
                    <list-item>li2</list-item>
                    <p>p2</p>
                    <list-item>li3</list-item>
                </body>
            </root>
            """;
        var result = await transformer.TransformAsync(input);
        result.Should().Contain("<list>", $"actual:\n{result}");
        result.Should().NotContain("<:body", $"actual:\n{result}");
        var listCount = System.Text.RegularExpressions.Regex.Count(result, "<list>");
        listCount.Should().Be(2, $"actual:\n{result}");
    }
}
