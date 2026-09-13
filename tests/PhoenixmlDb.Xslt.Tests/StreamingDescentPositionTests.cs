using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>position()</c> over a multi-step select counts across the WHOLE selected sequence, not
/// per parent. <c>apply-templates select="chapter/chtitle"</c> selects every <c>chtitle</c> in
/// document order, so the one under the second chapter is position 2.
///
/// The striding descent recurses a level per step, and each level had its own counter, so every
/// chapter restarted at 1 and every chtitle reported position 1.
///
/// Only reachable once a multi-step select could get to that driver at all — before that the
/// select never arrived there. Fixing one thing made the next one visible, which is the normal
/// shape of this work.
/// </summary>
public sealed class StreamingDescentPositionTests : System.IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "descentpos-" + Path.GetRandomFileName());

    public StreamingDescentPositionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private async Task<(string Streamed, string Plain)> BothWays(string templates, string doc)
    {
        var src = Path.Combine(_dir, "in.xml");
        await File.WriteAllTextAsync(src, doc).ConfigureAwait(true);
        var href = src.Replace("\\", "/", System.StringComparison.Ordinal);

        async Task<string> Run(bool streaming)
        {
            var mode = streaming ? """<xsl:mode streamable="yes"/>""" : "";
            var attr = streaming ? """streamable="yes" """ : "";
            var ss = $"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
                  <xsl:output method="xml" omit-xml-declaration="yes"/>
                  <xsl:strip-space elements="*"/>
                  {mode}
                  <xsl:template name="main">
                    <out><xsl:source-document {attr}href="{href}"><xsl:apply-templates select="."/></xsl:source-document></out>
                  </xsl:template>
                  {templates}
                </xsl:stylesheet>
                """;
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
            t.SetInitialTemplate("main");
            return await t.TransformAsync("<i/>").ConfigureAwait(true);
        }

        return (await Run(true).ConfigureAwait(true), await Run(false).ConfigureAwait(true));
    }

    // One chtitle under each of two chapters — the shape where a per-parent counter restarts.
    private const string TwoParents =
        "<lib><book><chapter><chtitle>a</chtitle></chapter>"
        + "<chapter><chtitle>b</chtitle></chapter></book></lib>";

    private const string Templates =
        """<xsl:template match="book"><xsl:apply-templates select="chapter/chtitle"/></xsl:template>"""
        + """<xsl:template match="chtitle"><t p="{position()}"><xsl:value-of select="."/></t></xsl:template>""";

    [Fact]
    public async Task PositionCountsAcrossParents_NotWithinEachOne()
    {
        var (streamed, plain) = await BothWays(Templates, TwoParents).ConfigureAwait(true);
        streamed.Should().Be(plain);
        streamed.Should().Contain("p=\"1\"").And.Contain("p=\"2\"");
    }

    [Fact]
    public async Task SeveralUnderOneParent_StillCountFromOne()
    {
        // Accept-side: the single-parent case was already right and must stay so.
        var (streamed, plain) = await BothWays(Templates,
            "<lib><book><chapter><chtitle>a</chtitle><chtitle>b</chtitle><chtitle>c</chtitle>"
            + "</chapter></book></lib>").ConfigureAwait(true);
        streamed.Should().Be(plain);
        streamed.Should().Contain("p=\"1\"").And.Contain("p=\"2\"").And.Contain("p=\"3\"");
    }

    [Fact]
    public async Task UnevenParents_CountContinuously()
    {
        // Two under the first parent, one under the second: the second parent's child is 3.
        var (streamed, plain) = await BothWays(Templates,
            "<lib><book><chapter><chtitle>a</chtitle><chtitle>b</chtitle></chapter>"
            + "<chapter><chtitle>c</chtitle></chapter></book></lib>").ConfigureAwait(true);
        streamed.Should().Be(plain);
        streamed.Should().Contain("<t p=\"3\">c</t>");
    }

    [Fact]
    public async Task SingleStepSelect_PositionUnchanged()
    {
        // Accept-side: the single-step route does not go through the descent driver.
        var (streamed, plain) = await BothWays(
            """<xsl:template match="chapter"><xsl:apply-templates select="chtitle"/></xsl:template>"""
            + """<xsl:template match="chtitle"><t p="{position()}"/></xsl:template>""",
            TwoParents).ConfigureAwait(true);
        streamed.Should().Be(plain);
    }
}
