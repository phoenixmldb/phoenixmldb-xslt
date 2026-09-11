using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A second batch of error codes that named a different rule than the one broken, plus the
/// duration ordering defect behind one of them.
/// </summary>
public sealed class MiscErrorCodeTests2
{
    private static async Task<string> RunAsync(string body, string declarations = "", string source = "<doc/>")
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {declarations}
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync(source)).Trim();
    }

    private static async Task AssertErrorAsync(string body, string code, string declarations = "")
    {
        var act = () => RunAsync(body, declarations);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain(code);
    }

    /// <summary>An xpath string that does not parse is XTDE3160, not XTDE3150 (error-3160a).</summary>
    [Fact]
    public Task Evaluate_WithAnXPathThatDoesNotParse_IsXTDE3160()
        => AssertErrorAsync("""<xsl:evaluate xpath="concat(current-date(), '/')"/>""", "XTDE3160");

    /// <summary>
    /// The context item is one item or none. A longer sequence was pushed whole and the dynamic
    /// expression's first axis step failed on "an item of type Object[]" (error-3210a).
    /// </summary>
    [Fact]
    public Task Evaluate_WithASequenceContextItem_IsXTTE3210()
        => AssertErrorAsync(
            """<xsl:variable name="v" as="element()*"><x/><y/></xsl:variable><xsl:evaluate xpath="'count(//x)'" context-item="$v"/>""",
            "XTTE3210");

    [Fact]
    public async Task Evaluate_WithASingleContextItem_StillEvaluates()
        => (await RunAsync("""<xsl:evaluate xpath="'count(//x)'" context-item="/"/>""", source: "<doc><x/><x/></doc>"))
            .Should().Be("2");

    /// <summary>
    /// A streamability other than unclassified classifies how the function consumes its first
    /// argument, so a function with no xsl:param cannot carry one (error-3155a).
    /// </summary>
    [Fact]
    public Task Function_WithNoParam_AndAStreamability_IsXTSE3155()
        => AssertErrorAsync("", "XTSE3155",
            """<xsl:function name="f:x" streamability="absorbing"><xsl:sequence select="22"/></xsl:function>""");

    [Fact]
    public async Task Function_WithAParam_KeepsItsStreamability()
        => (await RunAsync("""<xsl:value-of select="f:x(2)"/>""",
            """<xsl:function name="f:x" streamability="unclassified"><xsl:param name="n"/><xsl:sequence select="$n * 11"/></xsl:function>"""))
            .Should().Be("22");

    /// <summary>
    /// Durations of the same kind compare by length. They fell through to a string comparison,
    /// which orders P10D before P2D — so a sorted merge source looked unsorted (XTDE2220) and the
    /// real cross-source type error was never reached (error-2230a).
    /// </summary>
    [Fact]
    public Task Merge_WithIncomparableKeyTypes_IsXTTE2230()
        => AssertErrorAsync("""
            <xsl:merge>
              <xsl:merge-source name="a" select="1 to 50"><xsl:merge-key select="."/></xsl:merge-source>
              <xsl:merge-source name="b" select="1 to 50"><xsl:merge-key select="xs:dayTimeDuration('P1D') * ."/></xsl:merge-source>
              <xsl:merge-action>22</xsl:merge-action>
            </xsl:merge>
            """, "XTTE2230");

    [Theory]
    [InlineData("xs:dayTimeDuration('P1D') * .", "P1D P2D P10D")]
    [InlineData("xs:yearMonthDuration('P1M') * .", "P1M P2M P10M")]
    public async Task DurationMergeKeys_AreOrderedByLength(string key, string expected)
        => (await RunAsync($"""
            <xsl:merge>
              <xsl:merge-source select="(1, 2, 10)"><xsl:merge-key select="{key}"/></xsl:merge-source>
              <xsl:merge-action><xsl:value-of select="current-merge-key()"/><xsl:text> </xsl:text></xsl:merge-action>
            </xsl:merge>
            """)).Should().Be(expected);
}
