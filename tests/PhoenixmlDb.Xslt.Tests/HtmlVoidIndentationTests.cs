using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// method="html" indent="yes": a void element (meta, link, hr, br) has no end tag, so it opens
/// no indentation level. Counting it as one pushed everything after it a level deeper for the rest
/// of the document (title indented inside meta, the body's siblings ever further right).
/// </summary>
public class HtmlVoidIndentationTests
{
    [Fact]
    public async Task Void_elements_do_not_deepen_the_indentation_of_what_follows()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="html" indent="yes" include-content-type="no"/>
              <xsl:template match="/"><html><head><meta charset="utf-8"/><link rel="x"/><title>T</title></head><body><hr/><p>c</p></body></html></xsl:template>
            </xsl:stylesheet>
            """);

        var r = (await t.TransformAsync("<r/>")).Replace("\r\n", "\n").Trim();

        r.Should().Be("""
            <html>
              <head>
                <meta charset="utf-8">
                <link rel="x">
                <title>T</title>
              </head>
              <body>
                <hr>
                <p>c</p>
              </body>
            </html>
            """.Replace("\r\n", "\n"));
    }
}
