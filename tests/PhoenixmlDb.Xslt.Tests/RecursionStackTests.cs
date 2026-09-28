using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Deep recursion reaches the engine's recursion-depth limit (1200) before it exhausts the native
/// stack, whatever thread the host calls from (xslt#197).
/// </summary>
/// <remarks>
/// Each recursion level is a chain of synchronous async frames, so the transformation ran out of
/// stack on the caller's thread: around 800 levels on Linux, under 100 on Windows/.NET 8 (1 MB
/// threads). The engine now runs the transformation on its own large-stack thread. The
/// small-stack test stands in for Windows on every platform.
/// </remarks>
public sealed class RecursionStackTests
{
    private const string Stylesheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
            xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f">
          <xsl:output method="text"/>
          <xsl:param name="n" as="xs:integer"/>
          <xsl:template name="sum" as="xs:integer">
            <xsl:param name="n"/>
            <xsl:choose>
              <xsl:when test="$n = 0"><xsl:sequence select="0"/></xsl:when>
              <xsl:otherwise>
                <xsl:variable name="rest" as="xs:integer"><xsl:call-template name="sum"><xsl:with-param name="n" select="$n - 1"/></xsl:call-template></xsl:variable>
                <xsl:sequence select="$n + $rest"/>
              </xsl:otherwise>
            </xsl:choose>
          </xsl:template>
          <xsl:function name="f:sum" as="xs:integer">
            <xsl:param name="n" as="xs:integer"/>
            <xsl:sequence select="if ($n = 0) then 0 else $n + f:sum($n - 1)"/>
          </xsl:function>
          <xsl:template match="/"><xsl:call-template name="sum"><xsl:with-param name="n" select="$n"/></xsl:call-template>|<xsl:value-of select="f:sum($n)"/></xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<string> RunAsync(int n)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet);
        t.SetParameter("n", (long)n);
        return (await t.TransformAsync("<x/>")).Trim();
    }

    private static string Expected(int n) => $"{n * (n + 1) / 2}|{n * (n + 1) / 2}";

    [Fact]
    public async Task RecursionNearTheLimit_DoesNotExhaustTheStack()
    {
        (await RunAsync(1150)).Should().Be(Expected(1150));
    }

    [Fact]
    public void CallerOnASmallStack_StillRecursesDeeply()
    {
        // 256 KB is well below Windows' 1 MB default; before the fix this failed at under 100 levels.
        string? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = RunAsync(1000).GetAwaiter().GetResult(); }
            catch (Exception ex) { error = ex; }
        }, 256 * 1024);
        thread.Start();
        thread.Join();
        error.Should().BeNull();
        result.Should().Be(Expected(1000));
    }
}
