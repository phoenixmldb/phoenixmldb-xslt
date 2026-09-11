using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A relative for-each-source URI resolves against the static base URI in scope — which xml:base
/// on the xsl:merge changes — as doc() would; and a source that cannot be retrieved is FODC0002.
/// The URI went to the resolver raw, and an unretrievable source was skipped in silence, so a
/// source addressed relative to an xml:base contributed nothing (W3C merge-041).
/// </summary>
public sealed class MergeForEachSourceBaseUriTests
{
    private static async Task<string> RunAsync(string mergeAttributes, string forEachSource)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:merge {{mergeAttributes}}>
                  <xsl:merge-source for-each-source="{{forEachSource}}" select="r/e"><xsl:merge-key select="@k"/></xsl:merge-source>
                  <xsl:merge-action><xsl:value-of select="current-merge-key()"/></xsl:merge-action>
                </xsl:merge>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc/>")).Trim();
    }

    [Fact]
    public async Task ARelativeSource_ResolvesAgainstXmlBase()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "in.xml"), "<r><e k='1'/><e k='2'/></r>");
        try
        {
            var baseUri = new Uri(dir + Path.DirectorySeparatorChar).AbsoluteUri;
            (await RunAsync($"xml:base=\"{baseUri}\"", "'in.xml'")).Should().Be("12");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task AnUnretrievableSource_IsFODC0002()
    {
        var missing = new Uri(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.xml")).AbsoluteUri;
        var act = () => RunAsync("", $"'{missing}'");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("FODC0002");
    }
}
