using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An xsl:merge-key compares with its collation — its collation attribute, else the UCA
/// collation for its lang, else the default collation — and every merge source must agree on it
/// (XTDE2210, W3C merge-075). Merge keys compared by codepoint whatever the collation said, so
/// keys a collation calls equal were merged as different.
/// </summary>
public sealed class MergeKeyCollationTests
{
    private const string CaseBlind = "http://www.w3.org/2005/xpath-functions/collation/html-ascii-case-insensitive";

    private static async Task<string> RunAsync(string collationA, string collationB)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:merge>
                  <xsl:merge-source name="a" select="doc/a/k"><xsl:merge-key select="." {{collationA}}/></xsl:merge-source>
                  <xsl:merge-source name="b" select="doc/b/k"><xsl:merge-key select="." {{collationB}}/></xsl:merge-source>
                  <xsl:merge-action>[<xsl:value-of select="current-merge-group()" separator=","/>]</xsl:merge-action>
                </xsl:merge>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc><a><k>a</k><k>C</k></a><b><k>A</k><k>c</k></b></doc>")).Trim();
    }

    [Fact]
    public async Task KeysEqualUnderTheCollation_MergeIntoOneGroup()
        => (await RunAsync($"collation=\"{CaseBlind}\"", $"collation=\"{CaseBlind}\"")).Should().Be("[a,A][C,c]");

    [Fact]
    public async Task SourcesDisagreeingOnTheCollation_IsXTDE2210()
    {
        var act = () => RunAsync($"collation=\"{CaseBlind}\"", "");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE2210");
    }
}
