using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A space separates ADJACENT ATOMIC values in a sequence constructor (XSLT 3.0 §5.7.2); nodes
/// get none. An array, or a nested sequence, was classified as one atomic item, so two runs of
/// elements came out with " " between them (W3C square-array-002/-014/-016/-102/-114/-116 and the
/// sx- streaming set, sf-copy-of-008, sf-snapshot-0308). The harness discarded whitespace-only
/// text, so every one of those cases passed anyway until #140.
/// </summary>
public sealed class NestedSequenceSeparatorTests
{
    private static async Task<string> RunAsync(string body, string? source = null)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <xsl:variable name="x" as="element()*"><P>1</P><P>2</P></xsl:variable>
                <xsl:variable name="y" as="element()*"><a/><b/></xsl:variable>
                <out>{{body}}</out>
              </xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync(source ?? "<r><I><P>3</P></I></r>")).Trim();
    }

    [Theory]
    [InlineData("""<xsl:copy-of select="[$x, $y]"/>""", "<out><P>1</P><P>2</P><a /><b /></out>")]
    [InlineData("""<xsl:copy-of select="([$x], [$y])"/>""", "<out><P>1</P><P>2</P><a /><b /></out>")]
    [InlineData("""<xsl:sequence select="[$x, $y]"/>""", "<out><P>1</P><P>2</P><a /><b /></out>")]
    public async Task Arrays_of_elements_get_no_separator(string body, string expected)
        => Normalize(await RunAsync(body)).Should().Be(Normalize(expected));

    [Theory]
    [InlineData("""<xsl:copy-of select="[1, 2]"/>""", "<out>1 2</out>")]
    [InlineData("""<xsl:sequence select="[1, (2, 3)]"/>""", "<out>1 2 3</out>")]
    public async Task Adjacent_atomics_still_get_one(string body, string expected)
        => Normalize(await RunAsync(body)).Should().Be(Normalize(expected));

    [Fact]
    public async Task A_streamed_copy_of_grounded_and_streamed_nodes_gets_no_separator()
    {
        var dir = Path.Combine(Path.GetTempPath(), "phx-nested-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var doc = Path.Combine(dir, "d.xml");
            await File.WriteAllTextAsync(doc, "<r><I><P>3</P></I><I><P>4</P></I></r>");
            var result = await RunAsync($$"""
                <xsl:source-document streamable="yes" href="{{new Uri(doc).AbsoluteUri}}">
                  <xsl:sequence select="copy-of(($x, /r/I/P))"/>
                </xsl:source-document>
                """);
            Normalize(result).Should().Be(Normalize("<out><P>1</P><P>2</P><P>3</P><P>4</P></out>"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Normalize(string xml) => xml.Replace("<?xml version=\"1.0\" encoding=\"UTF-8\"?>", "", StringComparison.Ordinal)
        .Replace(" />", "/>", StringComparison.Ordinal).Trim();
}
