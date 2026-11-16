using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Each input of a streamed xsl:merge must be in order under the merge keys, or the merge is
/// XTDE2220 (XSLT 3.0 §15.4.2), as it is unstreamed. The streamed merge did not check, and merged
/// an input out of order as if it were in order (W3C merge-072/074). Expected values are
/// Saxon-HE 12.10's.
/// </summary>
public sealed class StreamedMergeOrderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-mrgord-" + Guid.NewGuid().ToString("N"));

    public StreamedMergeOrderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> Run(string first, string second, string keyAttributes)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "a.xml"), first, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_dir, "b.xml"), second, TestContext.Current.CancellationToken);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">
                <xsl:merge>
                  <xsl:merge-source name="a" streamable="yes" for-each-source="'a.xml'" select="l/k">
                    <xsl:merge-key select="." {{keyAttributes}}/>
                  </xsl:merge-source>
                  <xsl:merge-source name="b" streamable="yes" for-each-source="'b.xml'" select="l/k">
                    <xsl:merge-key select="." {{keyAttributes}}/>
                  </xsl:merge-source>
                  <xsl:merge-action><xsl:value-of select="current-merge-key()"/><xsl:text> </xsl:text></xsl:merge-action>
                </xsl:merge>
              </xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("<l><k>a</k><k>c</k></l>", "<l><k>b</k><k>d</k></l>", "", "a b c d")]
    // B before a in codepoint order.
    [InlineData("<l><k>B</k><k>a</k></l>", "<l><k>C</k></l>", "", "B C a")]
    [InlineData("<l><k>a</k><k>B</k></l>", "<l><k>c</k></l>", "collation='http://www.w3.org/2013/collation/UCA?lang=en'", "a B c")]
    public async Task Inputs_in_order_are_merged(string first, string second, string keyAttributes, string expected)
        => (await Run(first, second, keyAttributes)).Should().Be(expected);

    [Theory]
    [InlineData("<l><k>a</k><k>c</k></l>", "<l><k>d</k><k>b</k></l>", "")]
    // In order by codepoint, not under the collation the key names.
    [InlineData("<l><k>B</k><k>a</k></l>", "<l><k>c</k></l>", "collation='http://www.w3.org/2013/collation/UCA?lang=en'")]
    // Descending.
    [InlineData("<l><k>c</k><k>a</k></l>", "<l><k>a</k><k>b</k></l>", "order='descending'")]
    public async Task An_input_out_of_order_is_XTDE2220(string first, string second, string keyAttributes)
    {
        var run = () => Run(first, second, keyAttributes);
        (await run.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE2220");
    }
}
