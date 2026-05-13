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
}
