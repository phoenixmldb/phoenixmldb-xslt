using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// At the document level of a streamed transform, a path over the input with a POSITIONAL
/// predicate — chap[2], chap[last()], chap[position() gt 1] — was served by the watcher dispatch,
/// which matches steps by name and ignored the predicate: copy-of(/doc/chap[2]) delivered every
/// chap (W3C accumulator-048s/-049s). Such selects now materialise the input.
/// </summary>
public sealed class StreamedPositionalPredicateTests
{
    // Chapters with 1, 2 and 3 children, so a count identifies which one was selected.
    private const string Source = "<doc><chap><p/></chap><chap><p/><p/></chap><chap><p/><p/><p/></chap></doc>";

    private static async Task<string> RunAsync(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:output method="text"/>
              <xsl:template match="/">{{body}}</xsl:template>
            </xsl:stylesheet>
            """);
        // A Stream source takes the streamed path; a string source is parsed into memory first
        // and would never reach the document-level watcher dispatch this pins.
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Source));
        return (await t.TransformAsync(input)).Trim();
    }

    // Each body is a single xsl:value-of over a variable, so the body stays on the streaming path:
    // a comma sequence or other composite select would make the whole body buffer and hide the bug.
    [Theory]
    [InlineData("""<xsl:variable name="c" as="element()"><xsl:copy-of select="/doc/chap[2]"/></xsl:variable><xsl:value-of select="count($c/*)"/>""", "2")]
    [InlineData("""<xsl:variable name="c" as="element()" select="copy-of(/doc/chap[last()])"/><xsl:value-of select="count($c/*)"/>""", "3")]
    [InlineData("""<xsl:variable name="c" as="element()*" select="copy-of(/doc/chap[position() gt 1])"/><xsl:value-of select="count($c/*)"/>""", "5")]
    public async Task A_positional_predicate_selects_by_position(string body, string expected)
        => (await RunAsync(body)).Should().Be(expected);
}
