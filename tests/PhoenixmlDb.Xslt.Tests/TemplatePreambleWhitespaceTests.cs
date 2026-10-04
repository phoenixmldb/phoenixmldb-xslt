using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Under xml:space="preserve", a whitespace-only text node in a template is stripped only when
/// its next sibling is xsl:param or xsl:context-item (XSLT 3.0 §4.3); whitespace after the last
/// of them is content. It was stripped until some other content had been seen, so a template
/// that is xsl:context-item plus whitespace produced nothing (W3C context-item-019).
/// </summary>
public sealed class TemplatePreambleWhitespaceTests
{
    private static async Task<string> RunAsync(string templateBody)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><out><xsl:call-template name="t"/></out></xsl:template>
              <xsl:template name="t" xml:space="preserve">{templateBody}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync("<doc/>");
    }

    [Theory]
    [InlineData("""  <xsl:context-item as="document-node()"/>  """, "<out>  </out>")]
    [InlineData(""" <xsl:param name="p"/> <xsl:param name="q"/>  """, "<out>  </out>")]
    [InlineData("""  <xsl:param name="p"/>  <b/>""", "<out>  <b/></out>")]
    public async Task WhitespaceAfterThePreamble_IsKept(string body, string expected)
        => (await RunAsync(body)).Should().Contain(expected);

    [Fact]
    public async Task WhitespaceBeforeAParam_IsStripped()
        => (await RunAsync("""   <xsl:param name="p" select="'x'"/><v><xsl:value-of select="$p"/></v>"""))
            .Should().Contain("<out><v>x</v></out>");
}
