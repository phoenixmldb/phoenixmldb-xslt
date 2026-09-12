using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Under a streamable mode, accumulator-before() at a matched element answers what the streaming
/// pass accumulated. A template body that needs the whole subtree runs on a buffered copy of the
/// streamed element, and those fresh node ids carried none of the pass's values: the lookup found
/// nothing and recomputed the accumulator from its initial value over that one node, so every
/// match reported the same value (W3C accumulator-001s and siblings).
/// </summary>
public sealed class StreamingAccumulatorValueTests
{
    private const string Source = """
        <doc>
          <chap><div><fig alt="a"/></div><div><fig alt="b"/></div></chap>
          <chap><div><fig alt="c"/></div><div><fig alt="d"/></div></chap>
        </doc>
        """;

    private static async Task<string> RunAsync(string streamable, bool streamed)
    {
        // The fig template reads @alt as well as the accumulator, so its body needs the buffered
        // subtree — the path where the values went missing.
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:accumulator name="figNr" as="xs:integer" initial-value="0" streamable="{streamable}">
                <xsl:accumulator-rule match="chap" select="0"/>
                <xsl:accumulator-rule match="fig" select="$value + 1"/>
              </xsl:accumulator>
              <xsl:mode streamable="{streamable}" on-no-match="shallow-skip" use-accumulators="figNr"/>
              <xsl:template match="fig"><xsl:value-of select="accumulator-before('figNr')"/><xsl:value-of select="@alt"/><xsl:text> </xsl:text></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (streamed ? await t.TransformAsync(new StringReader(Source)) : await t.TransformAsync(Source)).Trim();
    }

    [Fact]
    public async Task AStreamedAccumulator_KeepsItsRunningValue_AtAMatchedElement()
        => (await RunAsync("yes", streamed: true)).Should().Be("1a 2b 1c 2d",
            "the streamed run reported 1 at every fig, whatever the pass had accumulated");

    [Fact]
    public async Task TheNonStreamedRun_IsUnchanged()
        => (await RunAsync("no", streamed: false)).Should().Be("1a 2b 1c 2d");

    /// <summary>
    /// The post-descent value is NOT final when the copy is taken — the element's end-phase rules
    /// have not run yet — so accumulator-after() is still answered by the walk over the buffered
    /// subtree (accumulator-015s/036s/069s regressed when it was answered from the copy).
    /// </summary>
    /// <remarks>
    /// One chap, so the subtree walk and the whole-stream value agree. They do not agree for a
    /// LATER sibling — a streamed accumulator-after counts only the matched subtree, so a second
    /// chap reports 2 where the non-streamed run reports 4. That gap is older than this fix and is
    /// left as it was; asserting it here either way would enshrine it.
    /// </remarks>
    [Fact]
    public async Task AccumulatorAfter_CountsTheSubtree()
    {
        var ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:accumulator name="figNr" as="xs:integer" initial-value="0" streamable="yes">
                <xsl:accumulator-rule match="fig" select="$value + 1"/>
              </xsl:accumulator>
              <xsl:mode streamable="yes" on-no-match="shallow-skip" use-accumulators="figNr"/>
              <xsl:template match="chap"><xsl:value-of select="accumulator-after('figNr'), count(.//fig)"/><xsl:text> </xsl:text></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        const string oneChap = """<doc><chap><div><fig alt="a"/></div><div><fig alt="b"/></div></chap></doc>""";
        (await t.TransformAsync(new StringReader(oneChap))).Trim().Should().Be("2 2");
    }
}
