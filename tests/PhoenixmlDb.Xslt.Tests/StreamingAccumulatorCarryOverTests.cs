using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A template body that needs the whole matched subtree runs on a buffered copy of it, and the
/// accumulator values for that copy are produced by a fresh walk over the copy. The walk started
/// from the accumulator's initial value, so an accumulator that deliberately runs across the whole
/// document restarted at every match: the second chap counted its own two figures instead of the
/// four the stream had seen (register #66).
/// </summary>
/// <remarks>
/// The remedy seeds the subtree walk with the value the streaming pass recorded at the element the
/// copy was taken from, rather than with the initial value. accumulator-after() is still answered
/// by the walk — the post-descent value is not final when the copy is taken, which is why it could
/// not simply be read from the streamed element (accumulator-015s/036s/069s).
/// </remarks>
public sealed class StreamingAccumulatorCarryOverTests
{
    private const string TwoChaps = """
        <doc>
          <chap><div><fig alt="a"/></div><div><fig alt="b"/></div></chap>
          <chap><div><fig alt="c"/></div><div><fig alt="d"/></div></chap>
        </doc>
        """;

    // count(.//fig) forces the buffered-subtree path; accumulator-after() is the value under test.
    private static string Stylesheet(string streamable) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
          <xsl:output method="text"/>
          <xsl:accumulator name="figNr" as="xs:integer" initial-value="0" streamable="{{streamable}}">
            <xsl:accumulator-rule match="fig" select="$value + 1"/>
          </xsl:accumulator>
          <xsl:mode streamable="{{streamable}}" on-no-match="shallow-skip" use-accumulators="figNr"/>
          <xsl:template match="chap"><xsl:value-of select="accumulator-after('figNr'), count(.//fig)"/><xsl:text> </xsl:text></xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<string> RunAsync(bool streamed)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet(streamed ? "yes" : "no"));
        return (streamed ? await t.TransformAsync(new StringReader(TwoChaps)) : await t.TransformAsync(TwoChaps)).Trim();
    }

    /// <summary>
    /// The non-streamed run is the oracle here — the two paths implement one semantics and this
    /// one was already right, so the streamed answer is compared against it rather than against a
    /// reading of the spec.
    /// </summary>
    [Fact]
    public async Task TheNonStreamedRun_CountsTheWholeDocument()
        => (await RunAsync(streamed: false)).Should().Be("2 2 4 2");

    [Fact]
    public async Task AStreamedAccumulatorAfter_CarriesAcrossMatchedSubtrees()
        => (await RunAsync(streamed: true)).Should().Be("2 2 4 2",
            "the streamed run reported 2 2 2 2 — every match restarted the accumulator at its initial value");
}
