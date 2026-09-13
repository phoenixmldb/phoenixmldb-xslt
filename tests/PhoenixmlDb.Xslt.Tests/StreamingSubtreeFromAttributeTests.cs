using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Two ways a streaming template could produce nothing and not say so.
///
/// <b>Deferred bodies.</b> A matched template whose body holds a consuming aggregate is pushed
/// onto a deferred stack so the watchers accumulating that aggregate can finish first, and run
/// when the element closes. Only the main streaming processor drained that stack; the nested
/// <c>xsl:apply-templates</c> driver pushed onto it and walked away, so the body never ran —
/// the template matched and contributed nothing. Draining alone was not enough either: that
/// driver materialises the subtree before matching, so a deferred body finds no child events
/// left and reads zero.
///
/// <b>Attribute value templates on a literal result element.</b> The subtree-buffer detector
/// inspected an LRE's content and not its attributes, so
/// <c>&lt;chapter total="{sum(.//x)}"/&gt;</c> was judged to need no buffer, ran against the
/// shallow streamed element, and emitted <c>total="0"</c> — an empty sum looks like an answer.
///
/// Every test compares the streamed result against the same stylesheet run unstreamed, so
/// neither side can drift alone.
/// </summary>
public sealed class StreamingSubtreeFromAttributeTests : System.IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "strmsub-" + Path.GetRandomFileName());

    public StreamingSubtreeFromAttributeTests() => Directory.CreateDirectory(_dir);

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

    [Fact]
    public async Task DescendantAggregateInAnAttributeValueTemplate_MatchesUnstreamed()
    {
        // The shape that emitted total="0": no element content, everything in the AVT.
        var (streamed, plain) = await BothWays(
            """<xsl:template match="chap"><chapter total="{sum(.//page-count)}"/></xsl:template>""",
            "<doc><chap><section><page-count>3</page-count></section>" +
            "<section><page-count>4</page-count></section></chap></doc>").ConfigureAwait(true);
        streamed.Should().Be(plain);
        streamed.Should().Contain("total=\"7\"");
    }

    [Fact]
    public async Task ChildCountInAnAttributeValueTemplate_MatchesUnstreamed()
    {
        var (streamed, plain) = await BothWays(
            """<xsl:template match="chap"><chapter n="{count(*)}"/></xsl:template>""",
            "<doc><chap><a/><b/><c/></chap></doc>").ConfigureAwait(true);
        streamed.Should().Be(plain);
        streamed.Should().Contain("n=\"3\"");
    }

    [Fact]
    public async Task NestedApplyTemplates_ReachesChildrenAndKeepsTheirOutput()
    {
        // The deferred-stack case: the outer template's apply-templates dispatched the child,
        // whose body then never ran.
        var (streamed, plain) = await BothWays(
            """<xsl:template match="doc"><d><xsl:apply-templates/></d></xsl:template>"""
            + """<xsl:template match="chap"><c n="{count(*)}"/></xsl:template>""",
            "<doc><chap><a/><b/></chap></doc>").ConfigureAwait(true);
        streamed.Should().Be(plain);
        streamed.Should().Contain("n=\"2\"");
    }

    [Fact]
    public async Task AttributeAxisInAnAvt_DoesNotForceABufferButStillMatches()
    {
        // Accept-side: @nr is on the start tag, so this must stay on the cheap path AND be right.
        var (streamed, plain) = await BothWays(
            """<xsl:template match="chap"><chapter nr="{@nr}"/></xsl:template>""",
            "<doc><chap nr='7'><a/></chap></doc>").ConfigureAwait(true);
        streamed.Should().Be(plain);
        streamed.Should().Contain("nr=\"7\"");
    }

    [Fact]
    public async Task LiteralAttribute_IsUnaffected()
    {
        var (streamed, plain) = await BothWays(
            """<xsl:template match="chap"><chapter k="v"/></xsl:template>""",
            "<doc><chap><a/></chap></doc>").ConfigureAwait(true);
        streamed.Should().Be(plain);
        streamed.Should().Contain("k=\"v\"");
    }
}
