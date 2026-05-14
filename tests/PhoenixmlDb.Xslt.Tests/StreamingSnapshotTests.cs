using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// snapshot() and copy-of() in streamable templates: the streaming pass sees
/// the element only once (one Start/EndElement burst with intervening
/// Text/child events). To deliver an XdmNode subtree to XPath, the engine
/// must buffer the matched subtree before executing the template body.
/// </summary>
public class StreamingSnapshotTests
{
    private static async Task<string> Run(string ss, string xml)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync(xml);
    }

    [Fact]
    public async Task Streaming_copy_of_self_emits_subtree()
    {
        // The simplest copy-of probe: copy-of(.) on the matched element should
        // emit the entire matched subtree verbatim.
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="row">
                    <out><xsl:copy-of select="."/></out>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><row id=\"1\"><a>x</a><b>y</b></row><row id=\"2\"><a>p</a></row></root>");
        r.Should().Contain("<out><row id=\"1\"><a>x</a><b>y</b></row></out>", $"actual={r}");
        r.Should().Contain("<out><row id=\"2\"><a>p</a></row></out>", $"actual={r}");
    }

    [Fact]
    public async Task Streaming_snapshot_self_emits_subtree()
    {
        var r = await Run("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="row">
                    <out><xsl:sequence select="snapshot(.)"/></out>
                </xsl:template>
            </xsl:stylesheet>
            """, "<root><row id=\"1\"><a>x</a></row></root>");
        r.Should().Contain("<out><row id=\"1\"><a>x</a></row></out>", $"actual={r}");
    }
}
