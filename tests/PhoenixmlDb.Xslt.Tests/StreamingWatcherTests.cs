using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Tests for the streaming watcher infrastructure: consuming aggregates
/// (count, sum, string-join, etc.) inside streamable-mode templates. The
/// engine pre-scans each template body, registers a watcher per consuming
/// aggregate, defers body execution until the parent EndElement, and
/// substitutes watcher results into the expressions when the body finally
/// runs.
/// </summary>
public class StreamingWatcherTests
{
    private static async Task<string> Run(string ss, string xml)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync(xml);
    }

    [Fact]
    public async Task Streaming_count_of_children_returns_correct_count()
    {
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <count><xsl:value-of select="count(*)"/></count>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><body><a/><a/><a/></body></root>");
        r.Should().Contain("<count>3</count>", $"actual={r}");
    }

    [Fact]
    public async Task Streaming_sum_of_text_content_returns_correct_sum()
    {
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <total><xsl:value-of select="sum(n)"/></total>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><body><n>10</n><n>20</n><n>5</n></body></root>");
        r.Should().Contain("<total>35</total>", $"actual={r}");
    }

    [Fact]
    public async Task Streaming_string_join_of_text_content_returns_joined_string()
    {
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <names><xsl:value-of select="string-join(n, ', ')"/></names>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><body><n>alice</n><n>bob</n><n>carol</n></body></root>");
        r.Should().Contain("<names>alice, bob, carol</names>", $"actual={r}");
    }

    [Fact]
    public async Task Streaming_max_of_text_content_returns_correct_max()
    {
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <highest><xsl:value-of select="max(n)"/></highest>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><body><n>10</n><n>50</n><n>20</n></body></root>");
        r.Should().Contain("<highest>50</highest>", $"actual={r}");
    }

    [Fact]
    public async Task Streaming_sum_with_attribute_value_returns_correct_sum()
    {
        // sum() over an attribute value works because watchers receive the attr
        // dictionary at StartElement (via _ancestorNames + attributes path).
        // sum() over element text content (sum(n)) is a separate gap — text is
        // delivered as separate Text events between Start/End, and the current
        // FireWatchers signature accumulates text as null. That's a follow-up
        // (text-aware watchers) on top of the deferred-execution foundation.
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <total><xsl:value-of select="sum(n/@v)"/></total>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><body><n v=\"10\"/><n v=\"20\"/><n v=\"5\"/></body></root>");
        r.Should().Contain("<total>35</total>", $"actual={r}");
    }

    [Fact]
    public async Task Streaming_count_inside_xsl_fork_yields_aggregate()
    {
        // Martin's probe: aggregates inside xsl:fork inside xsl:copy. The scanner
        // must recurse into both xsl:copy.Content and xsl:fork.Sequences for
        // count(a) to register a watcher and the deferred body to substitute it.
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <xsl:copy>
                        <xsl:fork>
                            <xsl:sequence select="count(a)"/>
                        </xsl:fork>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><body><a/><a/><a/></body></root>");
        r.Should().Contain("<body>3</body>", $"actual={r}");
    }
}
