using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:merge-source/@for-each-source is xs:string*: each item is a URI to load. The function
/// conversion rules admit xs:anyURI and xs:untypedAtomic too, but only a .NET string was loaded —
/// an xs:anyURI (what uri-collection returns) became the context item itself and select failed
/// on it (W3C merge-039/098). Any other atomic type is XPTY0004 (merge-043).
/// </summary>
public sealed class MergeForEachSourceTests
{
    private static async Task<string> RunAsync(string forEachSource)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:merge>
                  <xsl:merge-source for-each-source="{forEachSource}" select="r/e">
                    <xsl:merge-key select="@k"/>
                  </xsl:merge-source>
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
    public async Task AnAnyUriSource_IsLoaded()
    {
        var a = Path.Combine(Path.GetTempPath(), $"m-{Guid.NewGuid():N}.xml");
        var b = Path.Combine(Path.GetTempPath(), $"m-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(a, "<r><e k='1'/><e k='3'/></r>");
        await File.WriteAllTextAsync(b, "<r><e k='2'/></r>");
        try
        {
            var (ua, ub) = (new Uri(a).AbsoluteUri, new Uri(b).AbsoluteUri);
            (await RunAsync($"(xs:anyURI('{ua}'), xs:untypedAtomic('{ub}'))")).Should().Be("123");
        }
        finally
        {
            File.Delete(a);
            File.Delete(b);
        }
    }

    [Fact]
    public async Task ANonUriSource_IsXPTY0004()
    {
        var act = () => RunAsync("1 to 2");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XPTY0004");
    }
}
