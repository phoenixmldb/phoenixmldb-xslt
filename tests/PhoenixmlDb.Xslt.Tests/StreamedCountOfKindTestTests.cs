using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// count() of a path ending in text() or another kind test, in a streamed xsl:source-document,
/// gives what the same stylesheet gives unstreamed. The streaming watcher matched such a path
/// at its PARENT element: count(BOOKS/text()) was 1 with the whitespace stripped and 1 for
/// four text nodes, and count(BOOKS/node()) counted BOOKS (W3C stream-007, stream-008).
/// </summary>
public sealed class StreamedCountOfKindTestTests
{
    private const string Books = """
        <BOOKLIST>
          <BOOKS>
            <ITEM><PRICE>4.95</PRICE></ITEM>
            <ITEM><PRICE>6.58</PRICE></ITEM>
          </BOOKS>
          <CATEGORIES>a<C/>b<C/>c</CATEGORIES>
        </BOOKLIST>
        """;

    private static async Task<string> Run(string select, bool streamable)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"streamed-count-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "b.xml"), Books);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync($$"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:output method="xml" omit-xml-declaration="yes"/>
                  <xsl:strip-space elements="*"/>
                  <xsl:template name="xsl:initial-template">
                    <xsl:source-document streamable="{{(streamable ? "yes" : "no")}}" href="b.xml">
                      <out><xsl:value-of select="{{select}}"/></out>
                    </xsl:source-document>
                  </xsl:template>
                </xsl:stylesheet>
                """, new Uri(dir + "/"));
            t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return (await t.TransformAsync((string?)null)).Trim();
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("count(/BOOKLIST/BOOKS/text())", "<out>0</out>")]
    [InlineData("count(/BOOKLIST/CATEGORIES/text())", "<out>3</out>")]
    [InlineData("count(/BOOKLIST/BOOKS/node())", "<out>2</out>")]
    [InlineData("count(/BOOKLIST/BOOKS/element())", "<out>2</out>")]
    [InlineData("count(/BOOKLIST/CATEGORIES/node())", "<out>5</out>")]
    [InlineData("count(/BOOKLIST/BOOKS/ITEM/PRICE/text())", "<out>2</out>")]
    [InlineData("sum(/BOOKLIST/BOOKS/ITEM/PRICE/text())", "<out>11.530000000000001</out>")]
    public async Task Streamed_and_unstreamed_agree(string select, string expected)
    {
        (await Run(select, streamable: false)).Should().Be(expected);
        (await Run(select, streamable: true)).Should().Be(expected);
    }
}
