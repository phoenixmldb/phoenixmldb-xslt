using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The nodes a streamed descendant path selects (<c>//*</c>, <c>descendant::I</c>) keep their
/// ancestors for upward navigation, which is motionless: <c>//* ! count(ancestor-or-self::*)</c>
/// counts them. Each node was delivered without a parent, so every count was 1 (W3C sx-bang-006).
/// Siblings share their parent: <c>count(//I/..)</c> is 1. A copy has no parent. Expected values
/// are Saxon-HE 12.10's.
/// </summary>
public sealed class StreamedSequenceAncestorsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-seqanc-" + Guid.NewGuid().ToString("N"));

    public StreamedSequenceAncestorsTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "d.xml"), "<L><B><I><T/></I><I/></B><C/></L>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> Run(string select)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="d.xml"><xsl:value-of select="{{select}}"/></xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("//* ! count(ancestor-or-self::*)", "1 2 3 4 3 2")]
    [InlineData("//I ! name(..)", "B B")]
    [InlineData("descendant::I ! count(ancestor::*)", "2 2")]
    [InlineData("count(//I/..)", "1")]
    [InlineData("//I ! count(copy-of(.)/..)", "0 0")]
    public async Task Upward_navigation_from_a_streamed_node_finds_its_ancestors(string select, string expected)
        => (await Run(select)).Should().Be(expected);
}
