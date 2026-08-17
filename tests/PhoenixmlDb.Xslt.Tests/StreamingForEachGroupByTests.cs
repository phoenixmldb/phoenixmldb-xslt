using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xsl:for-each-group group-by</c> declared under <c>streamable="yes"</c>.
///
/// Note what this does and does not cover. group-by under a streamable mode is served today by
/// the BUFFERED path — <c>StreamingSubtreeBufferDetector</c> materializes the matched subtree
/// and the non-streaming branch groups over it — so these assert the behaviour users actually
/// get. They are not evidence that anything streams.
///
/// group-by is inherently BLOCKING: a key may recur at the very end of the population, so no
/// group can be emitted until the population is exhausted. Even a native streamed form would
/// retain every selected member and never reach constant memory.
///
/// The properties asserted here are the ones that distinguish group-by from group-adjacent and
/// that any future streamed implementation must preserve: groups in first-appearance order, a
/// key recurring after an intervening different key still joining the group opened earlier, and
/// <c>current-grouping-key()</c> bound.
/// </summary>
public sealed class StreamingForEachGroupByTests
{
    private static async Task<string> RunAsync(string input)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <xsl:copy>
                        <xsl:for-each-group select="*" group-by="@cat">
                            <group key="{current-grouping-key()}">
                                <xsl:value-of select="current-group()/@id" separator=","/>
                            </group>
                        </xsl:for-each-group>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """);
        return await transformer.TransformAsync(input);
    }

    /// <summary>
    /// Groups appear in first-appearance order, and a key that recurs after an intervening
    /// different key still joins the group opened earlier — the property that makes group-by
    /// blocking and distinguishes it from group-adjacent.
    /// </summary>
    [Fact]
    public async Task GroupBy_UnderStreaming_GroupsNonAdjacentMembers()
    {
        var result = await RunAsync("""
            <html><body>
              <p id="1" cat="a"/>
              <p id="2" cat="b"/>
              <p id="3" cat="a"/>
              <p id="4" cat="c"/>
              <p id="5" cat="b"/>
              <p id="6" cat="a"/>
            </body></html>
            """);

        result.Should().Contain("""<group key="a">1,3,6</group>""", $"actual:\n{result}");
        result.Should().Contain("""<group key="b">2,5</group>""", $"actual:\n{result}");
        result.Should().Contain("""<group key="c">4</group>""", $"actual:\n{result}");
        result.IndexOf("key=\"a\"", System.StringComparison.Ordinal)
            .Should().BeLessThan(result.IndexOf("key=\"b\"", System.StringComparison.Ordinal),
                "groups are emitted in first-appearance order");
    }

    /// <summary>A single group, and the whole population in it.</summary>
    [Fact]
    public async Task GroupBy_UnderStreaming_SingleGroup()
    {
        var result = await RunAsync("""
            <html><body><p id="1" cat="x"/><p id="2" cat="x"/></body></html>
            """);
        result.Should().Contain("""<group key="x">1,2</group>""", $"actual:\n{result}");
    }

    /// <summary>
    /// Declaring the mode streamable must not change the GROUPING, whatever path serves it.
    /// Compares the grouped payload rather than the whole document: a streamable mode does not
    /// copy the whitespace text siblings around the matched element the way the plain mode does,
    /// which is a serialization difference outside for-each-group and not a grouping one.
    /// </summary>
    [Fact]
    public async Task GroupBy_StreamableAndPlainMode_ProduceTheSameGroups()
    {
        const string input = """
            <html><body>
              <p id="1" cat="a"/><p id="2" cat="b"/><p id="3" cat="a"/><p id="4" cat="b"/>
            </body></html>
            """;
        var streamed = await RunAsync(input);

        var buffered = new XsltTransformer();
        await buffered.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                exclude-result-prefixes="#all">
                <xsl:mode on-no-match="shallow-copy"/>
                <xsl:template match="body">
                    <xsl:copy>
                        <xsl:for-each-group select="*" group-by="@cat">
                            <group key="{current-grouping-key()}">
                                <xsl:value-of select="current-group()/@id" separator=","/>
                            </group>
                        </xsl:for-each-group>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """);
        var bufferedResult = await buffered.TransformAsync(input);

        static string Groups(string s)
        {
            var start = s.IndexOf("<group", System.StringComparison.Ordinal);
            var end = s.LastIndexOf("</group>", System.StringComparison.Ordinal);
            return start >= 0 && end > start ? s[start..(end + 8)] : s;
        }
        Groups(streamed).Should().Be(Groups(bufferedResult));
    }
}
