using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An xsl:merge-source with streamable="yes" must be guaranteed streamable (XTSE3430): its select
/// strides from the streamed document, and it cannot sort before merging. The streamability check
/// walked only the merge action, so a crawling select or sort-before-merge ran as if streamable.
/// </summary>
public sealed class StreamableMergeSourceTests
{
    private static string Stylesheet(string sourceAttributes) => $"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          <xsl:template name="xsl:initial-template">
            <xsl:merge>
              <xsl:merge-source for-each-source="'urn:unused'" {sourceAttributes}>
                <xsl:merge-key select="@k"/>
              </xsl:merge-source>
              <xsl:merge-action><xsl:value-of select="current-merge-key()"/></xsl:merge-action>
            </xsl:merge>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Theory]
    [InlineData("""streamable="yes" select="log//record" """)]
    [InlineData("""streamable="yes" select="descendant::record" """)]
    [InlineData("""streamable="yes" select="log/record" sort-before-merge="yes" """)]
    public async Task ANonStreamableSource_MarkedStreamable_IsXTSE3430(string attributes)
    {
        var act = () => new XsltTransformer().LoadStylesheetAsync(Stylesheet(attributes));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().StartWith("XTSE3430");
    }

    [Theory]
    [InlineData("""streamable="yes" select="log/record" """)]
    [InlineData("""streamable="no" select="log//record" sort-before-merge="yes" """)]
    [InlineData("""select="log//record" """)]
    public async Task AStridingOrUnstreamedSource_Compiles(string attributes)
    {
        var act = () => new XsltTransformer().LoadStylesheetAsync(Stylesheet(attributes));
        await act.Should().NotThrowAsync();
    }
}
