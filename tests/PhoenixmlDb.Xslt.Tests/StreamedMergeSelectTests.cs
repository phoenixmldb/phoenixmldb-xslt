using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The select of a streamed xsl:merge-source is a path from the document node, as it is
/// unstreamed. The streaming reader tested a one-step select against the children of the
/// document element (select="i" over &lt;i k="1"&gt;&lt;i k="2"/&gt;&lt;/i&gt; merged k=2;
/// unstreamed, and Saxon, merge k=1), and a path of several child steps was not streamed.
/// </summary>
public sealed class StreamedMergeSelectTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-mergesel-" + Guid.NewGuid().ToString("N"));

    public StreamedMergeSelectTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "nested.xml"), "<i k='1'><i k='2'/></i>");
        File.WriteAllText(Path.Combine(_dir, "a.xml"), "<log><day><r k='1'/><r k='4'/></day><skip><r k='9'/></skip><day><r k='5'/></day></log>");
        File.WriteAllText(Path.Combine(_dir, "b.xml"), "<log><day><r k='2'/><r k='3'/></day></log>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> Run(bool streamable, string sources)
    {
        var s = streamable ? "yes" : "no";
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">
                <xsl:merge>
                  {{sources.Replace("$s", s, StringComparison.Ordinal)}}
                  <xsl:merge-action>[<xsl:value-of select="current-merge-key()"/>]</xsl:merge-action>
                </xsl:merge>
              </xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    private const string Key = "<xsl:merge-key select=\"xs:integer(@k)\"/>";

    [Theory]
    [InlineData("i", "[1]")]
    [InlineData("i/i", "[2]")]
    [InlineData("x", "")]
    public async Task A_path_starts_at_the_document_node(string select, string expected)
    {
        var sources = $"<xsl:merge-source for-each-source=\"'nested.xml'\" streamable=\"$s\" select=\"{select}\">{Key}</xsl:merge-source>";

        (await Run(streamable: false, sources)).Should().Be(expected);
        (await Run(streamable: true, sources)).Should().Be(expected);
    }

    [Fact]
    public async Task A_path_of_several_steps_merges_only_the_elements_on_it()
    {
        var sources = $"<xsl:merge-source for-each-source=\"'a.xml', 'b.xml'\" streamable=\"$s\" select=\"log/day/r\">{Key}</xsl:merge-source>";

        (await Run(streamable: false, sources)).Should().Be("[1][2][3][4][5]");
        (await Run(streamable: true, sources)).Should().Be("[1][2][3][4][5]");
    }
}
