using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A collection URI naming a directory is the XML files in it; the query selects by file-name
/// glob (<c>select=</c>) and can recurse (<c>recurse=yes</c>). Only collections registered by the
/// host resolved, so <c>uri-collection('dir?select=*.xml')</c> was FODC0002 (W3C merge-097).
/// </summary>
public sealed class DirectoryCollectionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phoenixml-coll-").FullName;

    public DirectoryCollectionTests()
    {
        File.WriteAllText(Path.Combine(_dir, "b.xml"), "<r><item>b1</item><item>b2</item></r>");
        File.WriteAllText(Path.Combine(_dir, "a.xml"), "<r><item>a1</item><item>a2</item></r>");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "not xml");
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "c.xml"), "<r><item>c1</item></r>");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string DirUri => new Uri(_dir + Path.DirectorySeparatorChar).AbsoluteUri;

    private static async Task<string> RunAsync(string body)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc/>")).Trim();
    }

    [Theory]
    [InlineData("?select=*.xml", "a.xml b.xml")]
    [InlineData("?select=a*", "a.xml")]
    [InlineData("?select=?.xml;recurse=yes", "a.xml b.xml c.xml")]
    [InlineData("", "a.xml b.xml")]
    public async Task ADirectoryUri_IsTheXmlFilesInIt(string query, string expected)
        => (await RunAsync($"""<xsl:value-of select="uri-collection('{DirUri}{query}') ! tokenize(., '/')[last()]"/>"""))
            .Should().Be(expected);

    [Fact]
    public async Task ADirectoryCollection_HoldsTheSameDocumentsAsDoc()
        => (await RunAsync($"""<xsl:value-of select="let $c := collection('{DirUri}?select=*.xml') return (count($c//item), $c[1] is doc(document-uri($c[1])))"/>"""))
            .Should().Be("4 true");

    /// <summary>
    /// A streamable merge source delivers snapshots, even where the merge runs in memory: the
    /// group's first item has its ancestors but not its siblings (W3C merge-097s).
    /// </summary>
    [Theory]
    [InlineData("yes", "1")]
    [InlineData("no", "2")]
    public async Task AStreamableMergeSource_DeliversSnapshots(string streamable, string expected)
        => (await RunAsync($"""
            <xsl:merge>
              <xsl:merge-source for-each-source="'{DirUri}a.xml'" select="*/*" streamable="{streamable}">
                <xsl:merge-key select="true()"/>
              </xsl:merge-source>
              <xsl:merge-action><xsl:value-of select="count(root(current-merge-group()[1])//item)"/></xsl:merge-action>
            </xsl:merge>
            """)).Should().Be(expected);
}
