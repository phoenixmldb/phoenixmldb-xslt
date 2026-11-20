using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// In a streamed pass, a template's position() is the node's position among all the children that
/// apply-templates selects (text, comments and processing instructions included), as it is when the
/// same stylesheet is not streamed. Text, comments and PIs were not counted (the forward pass gave
/// them position 1 and numbered elements among elements only), and a template's own
/// apply-templates skipped comments, PIs and whitespace-only text altogether.
/// </summary>
public class StreamedPositionTests
{
    private static async Task<string> TransformAsync(string stylesheet, string input)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(input);
    }

    private static string Stylesheet(bool streamable, string templates) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          <xsl:mode{{(streamable ? " streamable=\"yes\"" : "")}}/>
          {{templates}}
        </xsl:stylesheet>
        """;

    // Each case: templates, input, the result of the same stylesheet not streamed.
    public static TheoryData<string, string, string> Cases => new()
    {
        // The built-in rule over w: text children count.
        { """<xsl:template match="i"><xsl:value-of select="position()"/>;</xsl:template><xsl:template match="text()"/>""",
          "<r><w>x<i/>y<i/></w><w><i/></w></r>", "2;4;1;" },
        // Comments and processing instructions count too, and their own rules see their position.
        { """<xsl:template match="i|comment()|processing-instruction()"><xsl:value-of select="position()"/>;</xsl:template>""",
          "<r><w><!--c--><i/><?p x?><i/></w></r>", "1;2;3;4;" },
        // A template's own apply-templates (the nested streamed route).
        { """<xsl:template match="w"><xsl:apply-templates/>|</xsl:template><xsl:template match="i|comment()|processing-instruction()|text()"><xsl:value-of select="position()"/>;</xsl:template>""",
          "<r><w>x<!--c--><i/><?p x?>y<i/></w></r>", "1;2;3;4;5;6;|" },
        // Whitespace-only text is a text node (no xsl:strip-space): it takes a position.
        { """<xsl:template match="w"><xsl:apply-templates/>|</xsl:template><xsl:template match="i"><xsl:value-of select="position()"/>;</xsl:template><xsl:template match="text()"/>""",
          "<r><w> <i/> <i/></w></r>", "2;4;|" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task StreamedPosition_IsTheSameAsUnstreamed(string templates, string input, string expected)
    {
        var unstreamed = await TransformAsync(Stylesheet(streamable: false, templates), input);
        var streamed = await TransformAsync(Stylesheet(streamable: true, templates), input);

        unstreamed.Should().Be(expected);
        streamed.Should().Be(unstreamed);
    }
}
