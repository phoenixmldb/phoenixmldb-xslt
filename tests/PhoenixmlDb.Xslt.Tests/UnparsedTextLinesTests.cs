using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// fn:unparsed-text-lines returns a sequence of lines. The XSLT overloads returned a list the
/// engine carries as an XDM array — one item — so count() was 1 and xsl:for-each saw a single
/// item whose string value was every line joined by spaces (W3C unparsed-text-lines-001..005).
/// </summary>
public sealed class UnparsedTextLinesTests
{
    [Theory]
    [InlineData("a\nb\nc", "a|b|c")]
    [InlineData("a\r\nb\r\nc", "a|b|c")]
    [InlineData("a\rb\rc", "a|b|c")]          // a bare CR is a line ending too
    [InlineData("a\nb\n", "a|b")]              // no empty line after the final line ending
    [InlineData("a\n\nb", "a||b")]             // an empty line in the middle is kept
    [InlineData("only", "only")]
    [InlineData("", "")]
    public void SplitLines_FollowsTheSpec(string text, string expected)
    {
        var lines = UnparsedTextHelper.SplitLines(text) switch
        {
            null => [],
            object?[] many => many.Select(l => (string)l!).ToArray(),
            var one => [(string)one],
        };
        string.Join('|', lines).Should().Be(expected);
    }

    [Fact]
    public async Task UnparsedTextLines_IsASequence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"utl-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "one\ntwo\nthree\n");
        try
        {
            var href = new Uri(path).AbsoluteUri;
            var ss = $"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:output method="text"/>
                  <xsl:template match="/"><xsl:value-of select="count(unparsed-text-lines('{href}'))"/>|<xsl:for-each select="unparsed-text-lines('{href}', 'utf-8')">[<xsl:value-of select="."/>]</xsl:for-each></xsl:template>
                </xsl:stylesheet>
                """;
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(ss);
            (await t.TransformAsync("<doc/>")).Trim().Should().Be("3|[one][two][three]");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
