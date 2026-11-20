using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A sequence variable is bound for XPath without a copy, and a large one is scanned for temporary
/// trees only once (ConvertRtfForXQuery keeps the answer "no temporary tree"). A sequence that does
/// hold a temporary tree must still have it converted on every read, the second one included.
/// </summary>
public class SequenceVariableScanOnceTests
{
    private static async Task<string> TransformAsync(string stylesheet, string input)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(input);
    }

    [Theory]
    [InlineData(3)]    // below the length that is cached
    [InlineData(40)]   // above it
    public async Task TemporaryTreeInALargeSequence_IsNavigableOnEveryRead(int count)
    {
        var stylesheet = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="t"><b>in-tree</b></xsl:variable>
              <xsl:variable name="v" as="item()*">
                <xsl:sequence select="1 to {{count}}"/>
                <xsl:sequence select="$t"/>
              </xsl:variable>
              <xsl:template match="/">
                <out first="{$v[last()]/b}" second="{$v[last()]/b}" n="{count($v)}"/>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<r/>");

        result.Should().Be($"<out first=\"in-tree\" second=\"in-tree\" n=\"{count + 1}\"/>");
    }

    [Fact]
    public async Task LargeSequenceReadForEachNode_GivesTheSameResultAsOneRead()
    {
        // The shape of a compiled validator: a global sequence read for each node.
        var stylesheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="ids" select="//@id"/>
              <xsl:template match="/">
                <out><xsl:apply-templates select="//e"/></out>
              </xsl:template>
              <xsl:template match="e">
                <xsl:value-of select="if (@ref = head($ids) or exists($ids[. = current()/@ref])) then 'y' else 'n'"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var input = "<r>" + string.Concat(Enumerable.Range(1, 50).Select(i => $"<e id=\"i{i}\" ref=\"i{(i % 7 == 0 ? 999 : i)}\"/>")) + "</r>";

        var result = await TransformAsync(stylesheet, input);

        var expected = string.Concat(Enumerable.Range(1, 50).Select(i => i % 7 == 0 ? "n" : "y"));
        result.Should().Be($"<out>{expected}</out>");
    }
}
