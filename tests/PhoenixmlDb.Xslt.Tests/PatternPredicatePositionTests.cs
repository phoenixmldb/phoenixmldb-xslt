using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A match pattern's predicate only gets <c>position()</c>/<c>last()</c> context computed when it
/// can observe one. Establishing that context scans every sibling matching the step's node test,
/// so computing it unconditionally made ANY predicated pattern O(n²) in sibling count (#95) —
/// <c>w:p[@zzz]</c> cost the same as a predicate walking every descendant twice.
/// </summary>
/// <remarks>
/// These fixtures exist because the optimisation is a behaviour risk, not just a speed change:
/// skip the computation for a predicate that IS positional and the wrong nodes match. Verified
/// by forcing the guard to always skip, which breaks exactly the first four and leaves the last
/// two untouched — that asymmetry is the evidence the guard is both necessary and sufficient.
/// </remarks>
public class PatternPredicatePositionTests
{
    private const string Input = """<body xmlns:w="w"><w:p>A</w:p><w:p>B</w:p><w:p>C</w:p><w:p>D</w:p></body>""";

    private static string Stylesheet(string pattern) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:w="w">
          <xsl:output method="text"/>
          <xsl:template match="{{pattern}}" priority="5">[<xsl:value-of select="."/>]</xsl:template>
          <xsl:template match="w:p"><xsl:value-of select="."/></xsl:template>
          <xsl:template match="/body"><xsl:apply-templates select="w:p"/></xsl:template>
          <xsl:template match="text()"/>
        </xsl:stylesheet>
        """;

    // The first four are positional and MUST still compute context. Each fails with a distinct
    // wrong answer when the guard skips: the literal and position() forms match nothing, and
    // last() matches EVERYTHING, because a size of 1 makes every node the last one.
    [Theory]
    [InlineData("w:p[3]", "AB[C]D")]
    [InlineData("w:p[last()]", "ABC[D]")]
    [InlineData("w:p[position()=2]", "A[B]CD")]
    [InlineData("w:p[position() mod 2 = 1]", "[A]B[C]D")]
    // These two cannot observe position, which is why skipping is safe — and is the whole
    // performance win, since both are the shapes #95 measured as quadratic.
    [InlineData("w:p[@zzz]", "ABCD")]
    [InlineData("w:p[not(normalize-space(.))]", "ABCD")]
    public async Task MatchPattern_Predicate_SelectsTheSameNodesRegardlessOfPositionOptimisation(
        string pattern, string expected)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(Stylesheet(pattern));

        var result = await transformer.TransformAsync(Input);

        result.Trim().Should().Be(expected,
            "the position/last guard must change only how fast a pattern matches, never which nodes it matches");
    }
}
