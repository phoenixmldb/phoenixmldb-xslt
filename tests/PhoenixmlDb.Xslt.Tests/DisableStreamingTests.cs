using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <see cref="XsltTransformer.DisableStreaming"/> makes the string overload evaluate against a
/// tree even when the initial mode is streamable. The CLI's --no-stream sets it.
/// </summary>
/// <remarks>
/// The engine's TransformAsync(string) took the streaming pass for any streamable initial mode,
/// so --no-stream had no effect: Martin Honnen's xslt#295 output was the same with and without it.
/// The observable here is a non-streamable body, which the streaming pass evaluates wrongly
/// (preceding-sibling:: sees nothing). It should be rejected with XTSE3430; until it is, the two
/// paths answer differently, and the tree answer is the right one.
/// </remarks>
public class DisableStreamingTests
{
    private const string Stylesheet = """
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
          <xsl:output omit-xml-declaration="yes"/>
          <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
          <xsl:template match="item"><xsl:copy>{count(preceding-sibling::*)}</xsl:copy></xsl:template>
        </xsl:stylesheet>
        """;

    private const string Input = "<root><item/><item/><item/></root>";

    [Fact]
    public async Task DisableStreaming_EvaluatesAgainstTheTree()
    {
        var t = new XsltTransformer { DisableStreaming = true };
        await t.LoadStylesheetAsync(Stylesheet);
        (await t.TransformAsync(Input)).Trim().Should().Be("<root><item>0</item><item>1</item><item>2</item></root>");
    }

    [Fact]
    public void DisableStreaming_IsOffByDefault()
        => new XsltTransformer().DisableStreaming.Should().BeFalse();
}
