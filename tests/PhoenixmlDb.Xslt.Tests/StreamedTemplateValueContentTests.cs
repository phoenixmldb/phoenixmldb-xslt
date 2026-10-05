using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A template rule in a streamable mode that reads its matched element's children as a VALUE
/// (xslt#295, Martin Honnen). Both shapes below lost data without an error:
/// <list type="bullet">
/// <item>A text value template, <c>&lt;a&gt;{foo}&lt;/a&gt;</c>, was not recognised as reading the
/// subtree. It ran at the start tag, before the children had streamed in, and came out empty.</item>
/// <item>An <c>xsl:copy</c> whose content was <c>xsl:value-of select="foo"</c> was deferred to the
/// end tag, and the streamed copy was never closed: the next item nested inside it and the
/// document element's end tag was lost.</item>
/// </list>
/// </summary>
public class StreamedTemplateValueContentTests
{
    private const string Input = "<root><item><foo>foo 1</foo><bar>bar 1</bar></item><item><foo>foo 2</foo><bar>bar 2</bar></item></root>";

    private static async Task<string> RunAsync(string templateBody)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
              <xsl:template match="item">{templateBody}</xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync(Input)).Trim();
    }

    [Theory]
    [InlineData("<xsl:copy><a>{foo}</a></xsl:copy>", "<root><item><a>foo 1</a></item><item><a>foo 2</a></item></root>")]
    // One consuming operand each: two (Martin's original sheet) is not streamable, XTSE3430.
    [InlineData("<item><a>{string(foo)}</a></item>", "<root><item><a>foo 1</a></item><item><a>foo 2</a></item></root>")]
    [InlineData("<xsl:copy><xsl:value-of select=\"foo\"/></xsl:copy>", "<root><item>foo 1</item><item>foo 2</item></root>")]
    [InlineData("<xsl:copy><a><xsl:value-of select=\"foo\"/></a></xsl:copy>", "<root><item><a>foo 1</a></item><item><a>foo 2</a></item></root>")]
    // Controls: a literal element with value-of, and a body that reads no child.
    [InlineData("<item><xsl:value-of select=\"foo\"/></item>", "<root><item>foo 1</item><item>foo 2</item></root>")]
    [InlineData("<xsl:copy><a>x</a></xsl:copy>", "<root><item><a>x</a></item><item><a>x</a></item></root>")]
    public async Task ValueContent_IsKept_AndTheStructureIsIntact(string body, string expected)
        => (await RunAsync(body)).Should().Be(expected);
}
