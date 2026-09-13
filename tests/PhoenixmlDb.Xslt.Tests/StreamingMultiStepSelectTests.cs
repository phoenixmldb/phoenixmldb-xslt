using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The striding-descent driver handles what the streaming processor's forward pass cannot: a
/// select naming a grandchild set, where only top-level elements are ever offered for matching.
/// It was reachable from the document level only.
///
/// Inside a matched template the two entry conditions look nothing alike — at the document level
/// the processor is still active and streaming execution is not yet flagged; inside a matched
/// body the processor has been cleared for the duration of the body and streaming execution is
/// already flagged. Only the first was written, so
/// <c>&lt;xsl:apply-templates select="chapter/chtitle"/&gt;</c> inside <c>match="book"</c> fell
/// through, evaluated against the shallow streamed element, selected nothing, and let the
/// built-in rule copy the subtree's raw text out instead.
///
/// The steps parsed and the driver existed the whole time; only the route to it was closed.
/// </summary>
public sealed class StreamingMultiStepSelectTests : System.IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "multistep-" + Path.GetRandomFileName());

    public StreamingMultiStepSelectTests() => Directory.CreateDirectory(_dir);

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

    private const string Doc =
        "<lib><book><chapter><chtitle>one</chtitle></chapter>"
        + "<chapter><chtitle>two</chtitle></chapter></book></lib>";

    [Fact]
    public async Task MultiStepSelectInsideAMatchedTemplate_ReachesTheTemplate()
    {
        // The shape that leaked raw text: two steps, from a matched element.
        var (streamed, plain) = await BothWays(
            """<xsl:template match="book"><xsl:apply-templates select="chapter/chtitle"/></xsl:template>"""
            + """<xsl:template match="chtitle"><t><xsl:value-of select="upper-case(.)"/></t></xsl:template>""",
            Doc).ConfigureAwait(true);
        streamed.Should().Be(plain);
        streamed.Should().Contain("<t>ONE</t>").And.Contain("<t>TWO</t>");
    }

    [Fact]
    public async Task MultiStepSelect_DoesNotLeakTheSubtreeText()
    {
        // The specific wrong output: built-in rules copying character data through.
        var (streamed, _) = await BothWays(
            """<xsl:template match="book"><xsl:apply-templates select="chapter/chtitle"/></xsl:template>"""
            + """<xsl:template match="chtitle"><t/></xsl:template>""",
            Doc).ConfigureAwait(true);
        streamed.Should().NotContain("one").And.NotContain("two");
    }

    [Fact]
    public async Task SingleStepSelectInsideAMatchedTemplate_StillWorks()
    {
        // Accept-side: the single-step route was already correct and must stay so.
        var (streamed, plain) = await BothWays(
            """<xsl:template match="chapter"><xsl:apply-templates select="chtitle"/></xsl:template>"""
            + """<xsl:template match="chtitle"><t><xsl:value-of select="."/></t></xsl:template>""",
            Doc).ConfigureAwait(true);
        streamed.Should().Be(plain);
        streamed.Should().Contain("<t>one</t>");
    }

    [Fact]
    public async Task ThreeStepSelect_AlsoReachesTheTemplate()
    {
        var (streamed, plain) = await BothWays(
            """<xsl:template match="lib"><xsl:apply-templates select="book/chapter/chtitle"/></xsl:template>"""
            + """<xsl:template match="chtitle"><t><xsl:value-of select="."/></t></xsl:template>""",
            Doc).ConfigureAwait(true);
        streamed.Should().Be(plain);
        streamed.Should().Contain("<t>one</t>").And.Contain("<t>two</t>");
    }
}
