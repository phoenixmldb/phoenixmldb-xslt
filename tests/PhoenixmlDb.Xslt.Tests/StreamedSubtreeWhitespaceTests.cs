using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A subtree the streaming pass materialises keeps its whitespace-only text nodes, as the tree
/// path does, unless xsl:strip-space removes them (xslt#301, Martin Honnen).
/// </summary>
/// <remarks>
/// The materialiser skipped every whitespace-only text node "for parity with strip-space". The
/// reader already applies xsl:strip-space to streamed input, so the skip removed whitespace that
/// nothing asked to remove: snapshot(), copy-of(.) and any body run against the buffered subtree
/// turned indented input into <c>&lt;item&gt;&lt;foo/&gt;&lt;bar/&gt;&lt;/item&gt;</c>. Each case is compared with the same
/// stylesheet evaluated on a tree (DisableStreaming), which needs no expected string.
/// </remarks>
public class StreamedSubtreeWhitespaceTests
{
    private const string Input = "<root>\n  <item>\n    <foo>foo 1</foo>\n    <bar>bar 1</bar>\n  </item>\n</root>";

    private static async Task<string> RunAsync(string declarations, string body, bool disableStreaming)
    {
        var t = new XsltTransformer { DisableStreaming = disableStreaming };
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output omit-xml-declaration="yes"/>
              {declarations}
              <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
              <xsl:mode name="grounded" on-no-match="shallow-copy"/>
              <xsl:template match="item">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync(Input);
    }

    [Theory]
    [InlineData("<xsl:copy-of select=\".\"/>")]
    [InlineData("<xsl:apply-templates select=\"snapshot()\" mode=\"grounded\"/>")]
    [InlineData("<xsl:apply-templates select=\"copy-of()\" mode=\"grounded\"/>")]
    public async Task ABufferedSubtree_KeepsItsWhitespace(string body)
    {
        var streamed = await RunAsync("", body, disableStreaming: false);
        streamed.Should().Be(await RunAsync("", body, disableStreaming: true));
        streamed.Should().Contain("<item>\n    <foo>foo 1</foo>\n    <bar>bar 1</bar>\n  </item>");
    }

    [Fact]
    public async Task StripSpace_StillStripsABufferedSubtree()
    {
        const string strip = "<xsl:strip-space elements=\"item\"/>";
        var streamed = await RunAsync(strip, "<xsl:copy-of select=\".\"/>", disableStreaming: false);
        streamed.Should().Be(await RunAsync(strip, "<xsl:copy-of select=\".\"/>", disableStreaming: true));
        streamed.Should().Contain("<item><foo>foo 1</foo><bar>bar 1</bar></item>");
    }
}
