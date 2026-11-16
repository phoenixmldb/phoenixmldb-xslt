using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// In a streamed xsl:source-document, a positional predicate on the last element step of a path
/// (<c>BOOKS/ITEM[2]</c>, <c>BOOKS/ITEM[1]/@CAT</c>, <c>BOOKS/*[2]</c>) selects by the element's
/// position among its siblings. Each match was filtered on its own at position 1, and a bare
/// <c>[N]</c> was taken as its effective boolean value, so every element passed: string-join's
/// separator <c>ITEM[1]/@CAT</c> was six values (W3C st-string-join-002). Expected values are
/// Saxon-HE 12.10's.
/// </summary>
public sealed class StreamedLeafPositionalPredicateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-leafpos-" + Guid.NewGuid().ToString("N"));

    public StreamedLeafPositionalPredicateTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "b.xml"),
            "<L><B><I C='a'><T>one</T></I><X/><I C='b'><T>two</T></I><I C='c'><T>three</T></I></B></L>");
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
                <xsl:source-document streamable="yes" href="b.xml"><xsl:value-of select="{{select}}"/></xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("string-join((1 to 3)!string(), /L/B/I[1]/@C)", "1a2a3")]
    [InlineData("count(/L/B/I[2])", "1")]
    [InlineData("string(/L/B/I[3]/@C)", "c")]
    [InlineData("count(/L/B/*[2])", "1")]
    [InlineData("string-join(/L/B/*[2]/@C)", "")]
    [InlineData("string-join(/L/B/I[2]/T)", "two")]
    [InlineData("count(/L/B/I[position() lt 3]/@C)", "2")]
    public async Task The_element_is_selected_by_its_position(string select, string expected)
        => (await Run(select)).Should().Be(expected);
}
