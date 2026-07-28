using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Document-level <c>copy-of(.)</c> / <c>snapshot(.)</c> in a streamable body — a
/// whole-document copy interleaved with grounded siblings. At the document level the
/// context item is the document node, so the copy spans the entire streamed input;
/// there is no per-node streaming dispatch for it. The document-level whole-input-buffer
/// detector must recognize the bare-context-item snapshot/copy-of shape and buffer, or the
/// copy silently evaluates against the synthetic empty document node and vanishes, leaving
/// only the grounded siblings (W3C sf-copy-of-011 / sf-snapshot-0311). (#143)
/// </summary>
public class StreamingSnapshotCopyOfPathTests
{
    private static async Task<string> Run(string ss, string xml)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync(xml);
    }

    [Fact]
    public async Task Streaming_doc_level_copy_of_context_item_copies_whole_input()
    {
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode streamable="yes"/>
                <xsl:template match="/">
                    <out>
                        <head/>
                        <xsl:sequence select="copy-of(.)"/>
                        <tail/>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """, "<BOOKLIST><BOOK id=\"1\"><T>A</T></BOOK><BOOK id=\"2\"><T>B</T></BOOK></BOOKLIST>");
        r.Should().Contain("<head/>", $"actual={r}");
        r.Should().Contain("<tail/>", $"actual={r}");
        r.Should().Contain("<BOOKLIST><BOOK id=\"1\"><T>A</T></BOOK><BOOK id=\"2\"><T>B</T></BOOK></BOOKLIST>",
            $"whole-document copy must appear between head and tail; actual={r}");
    }

    [Fact]
    public async Task Streaming_doc_level_snapshot_context_item_copies_whole_input()
    {
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode streamable="yes"/>
                <xsl:template match="/">
                    <out>
                        <head/>
                        <xsl:sequence select="snapshot(.)"/>
                        <tail/>
                    </out>
                </xsl:template>
            </xsl:stylesheet>
            """, "<BOOKLIST><BOOK id=\"1\"><T>A</T></BOOK></BOOKLIST>");
        r.Should().Contain("<head/>", $"actual={r}");
        r.Should().Contain("<tail/>", $"actual={r}");
        r.Should().Contain("<BOOKLIST><BOOK id=\"1\"><T>A</T></BOOK></BOOKLIST>",
            $"whole-document snapshot must appear between head and tail; actual={r}");
    }
}
