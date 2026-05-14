using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:fork in streaming mode: each prong (xsl:sequence, xsl:for-each-group,
/// xsl:result-document) sees the same input but can compute independent results.
/// In streaming mode the scanner walks every prong for consuming aggregates so
/// the deferred-template machinery can substitute their values when the body runs.
/// </summary>
public class StreamingForkTests
{
    private static async Task<string> Run(string ss, string xml)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync(xml);
    }

    [Fact]
    public async Task Streaming_fork_two_sequence_prongs_emits_both_aggregates()
    {
        // Two xsl:sequence prongs computing different aggregates over the same children.
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <xsl:copy>
                        <xsl:fork>
                            <xsl:sequence select="count(a)"/>
                            <xsl:sequence select="sum(a)"/>
                        </xsl:fork>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><body><a>10</a><a>20</a><a>30</a></body></root>");
        r.Should().Contain("3", $"actual={r}");
        r.Should().Contain("60", $"actual={r}");
    }

    [Fact]
    public async Task Streaming_fork_inside_value_constructor_preserves_order()
    {
        // Fork prongs emit in declaration order regardless of streaming evaluation order.
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <xsl:copy>
                        <xsl:fork>
                            <xsl:sequence>
                                <first><xsl:value-of select="count(a)"/></first>
                            </xsl:sequence>
                            <xsl:sequence>
                                <second><xsl:value-of select="sum(a)"/></second>
                            </xsl:sequence>
                        </xsl:fork>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><body><a>1</a><a>2</a><a>3</a></body></root>");
        var firstIdx = r.IndexOf("<first>", System.StringComparison.Ordinal);
        var secondIdx = r.IndexOf("<second>", System.StringComparison.Ordinal);
        firstIdx.Should().BeGreaterThan(0, $"actual={r}");
        secondIdx.Should().BeGreaterThan(firstIdx, $"prong order broken. actual={r}");
        r.Should().Contain("<first>3</first>", $"actual={r}");
        r.Should().Contain("<second>6</second>", $"actual={r}");
    }

    [Fact]
    public async Task Streaming_fork_three_distinct_aggregates_all_resolve()
    {
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <stats>
                        <xsl:fork>
                            <xsl:sequence>
                                <c><xsl:value-of select="count(n)"/></c>
                            </xsl:sequence>
                            <xsl:sequence>
                                <mx><xsl:value-of select="max(n)"/></mx>
                            </xsl:sequence>
                            <xsl:sequence>
                                <mn><xsl:value-of select="min(n)"/></mn>
                            </xsl:sequence>
                        </xsl:fork>
                    </stats>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><body><n>50</n><n>10</n><n>30</n><n>20</n></body></root>");
        r.Should().Contain("<c>4</c>", $"actual={r}");
        r.Should().Contain("<mx>50</mx>", $"actual={r}");
        r.Should().Contain("<mn>10</mn>", $"actual={r}");
    }
}
