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

    /// <summary>
    /// Wrapping each level in an element does not halve the limit (xslt#199): element construction
    /// used to count toward the recursion depth, so this shape stopped at ~600 levels.
    /// </summary>
    [Theory]
    [InlineData("""<xsl:template name="r"><xsl:param name="n"/><xsl:if test="$n gt 0"><x><xsl:call-template name="r"><xsl:with-param name="n" select="$n - 1"/></xsl:call-template></x></xsl:if></xsl:template>""",
                """<xsl:call-template name="r"><xsl:with-param name="n" select="1100"/></xsl:call-template>""")]
    [InlineData("""<xsl:template match="*" mode="r"><xsl:param name="n"/><xsl:if test="$n gt 0"><xsl:element name="x"><xsl:apply-templates select="." mode="r"><xsl:with-param name="n" select="$n - 1"/></xsl:apply-templates></xsl:element></xsl:if></xsl:template>""",
                """<xsl:apply-templates select="parse-xml('&lt;a/&gt;')/*" mode="r"><xsl:with-param name="n" select="1100"/></xsl:apply-templates>""")]
    public async Task RecursionThatBuildsAnElementPerLevel_ReachesTheLimit(string templates, string call)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              {{templates}}
              <xsl:template match="/"><xsl:variable name="t">{{call}}</xsl:variable><xsl:value-of select="'built'"/></xsl:template>
            </xsl:stylesheet>
            """);
        // The tree is built but not navigated: XPath navigation has its own depth limit (1000).
        (await t.TransformAsync("<doc/>")).Trim().Should().Be("built");
    }

    /// <summary>Unbounded recursion still stops with XTDE0000 rather than running away.</summary>
    [Fact]
    public async Task UnboundedRecursionThroughAnElement_StillRaisesXTDE0000()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><xsl:call-template name="r"/></xsl:template>
              <xsl:template name="r"><x><xsl:call-template name="r"/></x></xsl:template>
            </xsl:stylesheet>
            """);
        var act = async () => await t.TransformAsync("<doc/>");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE0000");
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
