using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Streaming-mode regression tests for the core consuming-select operators:
/// xsl:apply-templates and xsl:for-each. Both classes of bug were the same shape
/// as the for-each-group report: pre-evaluating select="*" in streaming returned
/// empty, the body ran zero times, and children leaked through.
/// </summary>
public class StreamingCoreOpsTests
{
    private static async Task<string> Run(string ss, string xml)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync(xml);
    }

    [Fact]
    public async Task Streaming_apply_templates_select_star_preserves_ordering()
    {
        // <pre/> emits before children, children stream-process, <post/> emits after.
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <xsl:copy>
                        <pre/>
                        <xsl:apply-templates select="*"/>
                        <post/>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><body><a>1</a><a>2</a></body></root>");
        // <pre/> first, then both <a> children, then <post/> — all inside <body>.
        var preIdx = r.IndexOf("<pre/>", System.StringComparison.Ordinal);
        var postIdx = r.IndexOf("<post/>", System.StringComparison.Ordinal);
        var aIdx = r.IndexOf("<a>", System.StringComparison.Ordinal);
        preIdx.Should().BeGreaterThan(0, $"actual={r}");
        aIdx.Should().BeGreaterThan(preIdx, $"<a> should appear after <pre/>. actual={r}");
        postIdx.Should().BeGreaterThan(aIdx, $"<post/> should appear after children. actual={r}");
    }

    [Fact]
    public async Task Streaming_apply_templates_default_select_preserves_ordering()
    {
        // Same as above but with default apply-templates (no explicit select).
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <xsl:copy>
                        <pre/>
                        <xsl:apply-templates/>
                        <post/>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><body><a>1</a><a>2</a></body></root>");
        var preIdx = r.IndexOf("<pre/>", System.StringComparison.Ordinal);
        var postIdx = r.IndexOf("<post/>", System.StringComparison.Ordinal);
        var aIdx = r.IndexOf("<a>", System.StringComparison.Ordinal);
        aIdx.Should().BeGreaterThan(preIdx, $"<a> should appear after <pre/>. actual={r}");
        postIdx.Should().BeGreaterThan(aIdx, $"<post/> should appear after children. actual={r}");
    }

    [Fact]
    public async Task Streaming_iterate_select_star_runs_body_per_child()
    {
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <xsl:copy>
                        <xsl:iterate select="*">
                            <wrap><xsl:value-of select="position()"/></wrap>
                        </xsl:iterate>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><body><a/><a/><a/></body></root>");
        System.Text.RegularExpressions.Regex.Count(r, "<wrap>").Should().Be(3, $"actual={r}");
        r.Should().Contain("<wrap>1</wrap>", $"actual={r}");
        r.Should().Contain("<wrap>3</wrap>", $"actual={r}");
    }

    [Fact]
    public async Task Streaming_iterate_threads_state_via_next_iteration()
    {
        // Running counter: each iteration increments a param via xsl:next-iteration
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                xmlns:xs="http://www.w3.org/2001/XMLSchema">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <xsl:copy>
                        <xsl:iterate select="*">
                            <xsl:param name="counter" as="xs:integer" select="0"/>
                            <step n="{$counter + 1}"/>
                            <xsl:next-iteration>
                                <xsl:with-param name="counter" select="$counter + 1"/>
                            </xsl:next-iteration>
                        </xsl:iterate>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><body><a/><a/><a/></body></root>");
        r.Should().Contain("n=\"1\"", $"actual={r}");
        r.Should().Contain("n=\"2\"", $"actual={r}");
        r.Should().Contain("n=\"3\"", $"actual={r}");
    }

    [Fact]
    public async Task Streaming_for_each_select_star_wraps_each_child()
    {
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <xsl:copy>
                        <xsl:for-each select="*">
                            <wrap><xsl:value-of select="local-name()"/></wrap>
                        </xsl:for-each>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><body><a/><b/><c/></body></root>");
        var wrapCount = System.Text.RegularExpressions.Regex.Count(r, "<wrap>");
        wrapCount.Should().Be(3, $"actual={r}");
        r.Should().Contain("<wrap>a</wrap>", $"actual={r}");
        r.Should().Contain("<wrap>b</wrap>", $"actual={r}");
        r.Should().Contain("<wrap>c</wrap>", $"actual={r}");
    }
}
