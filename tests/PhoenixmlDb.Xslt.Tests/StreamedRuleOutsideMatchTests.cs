using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A template rule of a streamable mode that reads outside the element it matched gets the
/// answer a tree gives (xslt#298).
/// </summary>
/// <remarks>
/// The streaming pass can buffer the matched element's subtree, not its surroundings, and such a
/// body is not streamable. The engine streamed it anyway and answered wrongly with no error:
/// count(preceding-sibling::*) was 0 for every item, last() was 1 and count(../*) was 0. The spec
/// answer is XTSE3430, but the streamability analysis cannot yet tell these bodies apart reliably
/// (BUGS #16), so the transformation is evaluated against a tree instead.
/// </remarks>
public class StreamedRuleOutsideMatchTests
{
    private const string Input = "<root><item><foo>a</foo></item><item><foo>b</foo></item><item><foo>c</foo></item></root>";

    private static async Task<string> RunAsync(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
              <xsl:template match="item"><xsl:copy>{body}</xsl:copy></xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync(Input)).Trim();
    }

    [Theory]
    [InlineData("{count(preceding-sibling::*)}", "<root><item>0</item><item>1</item><item>2</item></root>")]
    [InlineData("{count(following-sibling::item)}", "<root><item>2</item><item>1</item><item>0</item></root>")]
    [InlineData("{last()}", "<root><item>3</item><item>3</item><item>3</item></root>")]
    [InlineData("{count(../*)}", "<root><item>3</item><item>3</item><item>3</item></root>")]
    [InlineData("{count(/root/item)}", "<root><item>3</item><item>3</item><item>3</item></root>")]
    // Control: a rule that stays inside its match.
    [InlineData("{foo}", "<root><item>a</item><item>b</item><item>c</item></root>")]
    public async Task ARuleReadingOutsideItsMatch_GetsTheTreeAnswer(string body, string expected)
        => (await RunAsync(body)).Should().Be(expected);
}
