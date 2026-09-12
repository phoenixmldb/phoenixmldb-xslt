using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A template with an empty body suppresses its element AND its subtree. Under streaming only the
/// suppressed element itself was recognised at its end tag: its DESCENDANTS still popped the
/// deferred-close stack, which closes an ancestor early — the root element ended after the first
/// child that contained one, and the rest of the document followed outside it
/// (W3C attr/mode mode-1416).
/// </summary>
public sealed class StreamingSuppressedSubtreeTests
{
    private const string Source = """
        <book><chapter><title>One</title><v>text <deity>God</deity> more</v></chapter><chapter><title>Two</title></chapter></book>
        """;

    private static async Task<string> RunAsync(bool streamed)
    {
        var ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
              <xsl:template match="v"/>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (streamed ? await t.TransformAsync(new StringReader(Source)) : await t.TransformAsync(Source)).Trim();
    }

    [Fact]
    public async Task ASuppressedSubtree_DoesNotCloseAnAncestor()
        => (await RunAsync(streamed: true))
            .Should().Be("<book><chapter><title>One</title></chapter><chapter><title>Two</title></chapter></book>",
                "a descendant of the suppressed <v> popped the deferred close, ending <book> early");

    [Fact]
    public async Task TheUnstreamedRunAgrees()
        => (await RunAsync(streamed: true)).Should().Be(await RunAsync(streamed: false));
}
