using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An accumulator rule that reads another accumulator's value at the same node must see that
/// accumulator's value for THIS node, whichever was declared first. The walk ran each phase in
/// declaration order and stored values as it went, so a rule reading a later-declared
/// accumulator got the provisional entry: from an end rule, the value before this node's
/// descendants — the previous node's. Silent wrong data; W3C accumulator-077 passed only because
/// its one lookup hit the entry written one item late.
/// </summary>
public sealed class AccumulatorDependencyOrderTests
{
    private const string Source = "<r><i><id>1</id></i><i><id>2</id></i><i><id>3</id></i></r>";

    private static async Task<string> RunAsync(string accumulators, string body)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:mode use-accumulators="#all"/>
              {{accumulators}}
              <xsl:template match="/">{{body}}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync(Source)).Trim();
    }

    // "seen" reads "last-id" from its end rule on each <i>: the ids seen so far, in order.
    private const string Seen = """
        <xsl:accumulator name="seen" as="xs:integer*" initial-value="()">
          <xsl:accumulator-rule match="i" phase="end" select="$value, accumulator-after('last-id')"/>
        </xsl:accumulator>
        """;

    private const string LastId = """
        <xsl:accumulator name="last-id" as="xs:integer?" initial-value="()">
          <xsl:accumulator-rule match="i/id/text()" select="xs:integer(.)"/>
        </xsl:accumulator>
        """;

    [Theory]
    [InlineData(Seen + LastId)] // the reader declared first — the order that was wrong
    [InlineData(LastId + Seen)] // the reader declared second — the order that already worked
    public async Task AnEndRule_SeesTheOtherAccumulatorsValueForThisNode(string accumulators)
        => (await RunAsync(accumulators, """<xsl:value-of select="/r/accumulator-after('seen')"/>"""))
            .Should().Be("1 2 3", "the previous item's id, and () for the first, was the defect");

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    public async Task AStartRule_SeesTheOtherAccumulatorsBeforeValueForThisNode(string readerPosition)
    {
        // "count" numbers each <i> at its start; "tagged" records that number at the same <i>.
        const string counter = """
            <xsl:accumulator name="count" as="xs:integer" initial-value="0">
              <xsl:accumulator-rule match="i" phase="start" select="$value + 1"/>
            </xsl:accumulator>
            """;
        const string tagged = """
            <xsl:accumulator name="tagged" as="xs:integer*" initial-value="()">
              <xsl:accumulator-rule match="i" phase="start" select="$value, accumulator-before('count')"/>
            </xsl:accumulator>
            """;
        var accumulators = readerPosition == "first" ? tagged + counter : counter + tagged;
        (await RunAsync(accumulators, """<xsl:value-of select="/r/accumulator-after('tagged')"/>"""))
            .Should().Be("1 2 3");
    }

    /// <summary>
    /// The old walk missed this cycle entirely: the cycle check caught an accumulator reading
    /// itself, but here a's end rule read b's provisional entry and finished, and b then read a's
    /// fresh value — a value came out, no error. Evaluating b on demand from inside a's rule is
    /// what exposes the cycle.
    /// </summary>
    [Fact]
    public async Task ACycleBetweenTwoAccumulators_IsXTDE3400()
    {
        const string cycle = """
            <xsl:accumulator name="a" initial-value="0">
              <xsl:accumulator-rule match="i" phase="end" select="accumulator-after('b')"/>
            </xsl:accumulator>
            <xsl:accumulator name="b" initial-value="0">
              <xsl:accumulator-rule match="i" phase="end" select="accumulator-after('a')"/>
            </xsl:accumulator>
            """;
        var act = () => RunAsync(cycle, """<xsl:value-of select="/r/accumulator-after('a')"/>""");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE3400");
    }
}
