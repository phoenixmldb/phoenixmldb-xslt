using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Streaming whole-subtree copy: inside a streamable xsl:source-document whose body
/// has no xsl:apply-templates, a lexical <c>xsl:copy-of select="child::node()"</c>
/// (the "whole document unchanged" shape) must forward the live reader's subtree
/// events into the output at its lexical position. Before the fix the select
/// evaluated against the CLOSED synthetic document node and yielded empty, dropping
/// the copy. Covers si-lre-011 / si-copy-011 / si-document-011 / si-element-011 /
/// si-result-document-011.
/// </summary>
public class StreamingCopyOfSubtreeForwardTests
{
    private static async Task<string> Run(string stylesheet, string inputXml, string file)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"streaming-cof-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, file), inputXml);
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(stylesheet, new Uri(dir + "/"));
            t.SetInitialTemplate("main");
            return await t.TransformAsync((string?)null);
        }
        finally { Directory.Delete(dir, true); }
    }

    private const string Doc = """
        <BOOKLIST>
          <BOOKS><ITEM><TITLE>Alpha</TITLE></ITEM></BOOKS>
          <CATEGORIES><CATEGORY CODE="P"/></CATEGORIES>
        </BOOKLIST>
        """;

    // <head/> and <tail/> already emit today; only the copy-of was empty. The forward
    // must place the whole BOOKLIST subtree between them, inside the <doc> wrapper.
    private const string Sheet = """
        <xsl:stylesheet version="3.0"
            xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
          <xsl:strip-space elements="*"/>
          <xsl:template name="main">
            <out>
              <xsl:source-document streamable="yes" href="d.xml">
                <head/>
                <doc>
                  <xsl:copy-of select="child::node()"/>
                </doc>
                <tail/>
              </xsl:source-document>
            </out>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task CopyOfChildNode_AtDocumentLevel_ForwardsWholeSubtree()
    {
        var r = await Run(Sheet, Doc, "d.xml");
        r.Should().Be(
            "<out><head/><doc><BOOKLIST><BOOKS><ITEM><TITLE>Alpha</TITLE></ITEM></BOOKS>"
            + "<CATEGORIES><CATEGORY CODE=\"P\"/></CATEGORIES></BOOKLIST></doc><tail/></out>");
    }
}
