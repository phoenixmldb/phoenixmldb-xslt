using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// current-merge-key() with no current merge key is XTDE3510 (XSLT 3.0 §15.4). It raised
/// XTDE3480, which is current-merge-group's code (W3C merge-056, merge-101).
/// </summary>
public sealed class CurrentMergeKeyErrorTests
{
    private static async Task RunAsync(string body)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">{body}</xsl:template>
              <xsl:template name="n"><xsl:value-of select="current-merge-key()"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        await t.TransformAsync("<doc><e k='1'/></doc>");
    }

    [Theory]
    [InlineData("""<xsl:value-of select="current-merge-key()"/>""")]
    // Called from a named template inside a merge action: the key is not visible there.
    [InlineData("""<xsl:merge><xsl:merge-source select="doc/e"><xsl:merge-key select="@k"/></xsl:merge-source><xsl:merge-action><xsl:call-template name="n"/></xsl:merge-action></xsl:merge>""")]
    public async Task CurrentMergeKey_WithNoCurrentKey_IsXTDE3510(string body)
    {
        var act = () => RunAsync(body);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE3510");
    }
}
