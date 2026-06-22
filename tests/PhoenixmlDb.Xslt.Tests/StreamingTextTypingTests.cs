using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

public class StreamingTextTypingTests
{
    private static async Task<string> Run(string stylesheet, string inputXml, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"streaming-texttype-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, file), inputXml);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(stylesheet, new Uri(dir + "/"));
            t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return await t.TransformAsync((string?)null);
        }
        finally { Directory.Delete(dir, true); }
    }

    private const string Books = """
        <BOOKLIST><BOOKS>
          <ITEM><PRICE>4.95</PRICE></ITEM>
          <ITEM><PRICE>6.58</PRICE></ITEM>
        </BOOKS></BOOKLIST>
        """;

    private static string Sheet(string select) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:mode streamable="yes"/>
          <xsl:template name="xsl:initial-template">
            <xsl:source-document streamable="yes" href="b.xml">
              <out><xsl:value-of select="{{select}}"/></out>
            </xsl:source-document>
          </xsl:template>
        </xsl:stylesheet>
        """;

    // M4 core: head(//PRICE/text()) ! (. + 1) — arithmetic on the streamed text.
    [Fact]
    public async Task Head_TextTail_BareArithmetic()
    {
        var r = await Run(Sheet("head(//PRICE/text()) ! (. + 1)"), Books, "b.xml");
        r.Trim().Should().Be("<out>5.95</out>");
    }

    // M4 core via copy-of (the sf-head-023 shape): head(//PRICE/text()) ! (.+1).
    [Fact]
    public async Task Head_TextTail_BareArithmetic_CopyOf()
    {
        var sheet = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:mode streamable="yes"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="yes" href="b.xml">
                  <out><xsl:copy-of select="head(//PRICE/text()) ! (.+1)"/></out>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await Run(sheet, Books, "b.xml");
        r.Trim().Should().Be("<out>5.95</out>");
    }

    // Consumer canary: bare value-of over streamed text → string value unchanged.
    [Fact]
    public async Task ValueOf_StreamedText_StringValueUnchanged()
    {
        var r = await Run(Sheet("head(//PRICE/text())"), Books, "b.xml");
        r.Trim().Should().Be("<out>4.95</out>");
    }

    // Consumer canary: string-join over streamed text → joined string.
    [Fact]
    public async Task StringJoin_StreamedText_Joins()
    {
        var r = await Run(Sheet("string-join(//PRICE/text(), ',')"), Books, "b.xml");
        r.Trim().Should().Be("<out>4.95,6.58</out>");
    }
}
