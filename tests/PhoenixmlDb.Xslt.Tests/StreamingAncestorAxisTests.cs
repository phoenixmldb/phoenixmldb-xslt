using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A streamed element must have the same ancestor axis as the same element read normally.
///
/// The streaming processor sets the parent on its node CONTEXT, but the element it then
/// materializes from that context never received it — MaterializeElement does not pass it on.
/// Only the root element got a parent, assigned explicitly straight afterwards, so every
/// element below the root was an orphan: <c>ancestor::*</c>, <c>parent::*</c> and even
/// <c>count(..)</c> came back empty. The comment at that site says deeper elements "keep their
/// real element parent from ancestorStack", which is what made it look handled.
///
/// The text-node path in the same loop has always taken its parent id off that same stack.
/// Elements simply never did — the two halves of one rule, one of them implemented.
/// </summary>
public class StreamingAncestorAxisTests
{
    private const string Doc = "<a><b><c/></b></a>";

    private static async Task<string> Run(string select, bool streamable)
    {
        var mode = streamable ? """<xsl:mode streamable="yes"/>""" : "";
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {mode}
              <xsl:template match="c"><xsl:value-of select="{select}"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        return await t.TransformAsync(Doc).ConfigureAwait(true);
    }

    [Theory]
    // The ancestor axis is in DOCUMENT order — outermost first — not innermost-first.
    [InlineData("string-join(ancestor::*/local-name(), ' ')")]
    [InlineData("string-join(ancestor-or-self::*/local-name(), ' ')")]
    [InlineData("count(..)")]
    [InlineData("../local-name()")]
    // Every node in an XDM tree is rooted at a document node, so this counts it too.
    [InlineData("count(ancestor::node())")]
    [InlineData("count(ancestor::document-node())")]
    [InlineData("count(root(.))")]
    public async Task StreamedAncestorAxis_MatchesTheUnstreamedAnswer(string select)
    {
        var streamed = await Run(select, streamable: true).ConfigureAwait(true);
        var unstreamed = await Run(select, streamable: false).ConfigureAwait(true);
        streamed.Should().Be(unstreamed);
    }

    [Fact]
    public async Task StreamedAncestorAxis_IsInDocumentOrder()
    {
        // Pinned literally, so a regression that returns the chain innermost-first fails here
        // rather than passing an "equals the other run" comparison that also regressed.
        (await Run("string-join(ancestor::*/local-name(), ' ')", streamable: true)
            .ConfigureAwait(true)).Should().Be("a b");
    }
}
