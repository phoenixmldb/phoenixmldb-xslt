using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xsl:copy</c> is a SHALLOW copy — the node itself, its name and namespaces, and nothing
/// below it. So <c>xsl:copy select="."</c> reads only the start tag and is motionless, unlike
/// <c>xsl:copy-of</c> or <c>xsl:value-of</c> of the same <c>.</c>, which take the whole subtree
/// or its string value.
///
/// The shared expression check does not draw that distinction: it flags any bare context item
/// as consuming, which is correct for every other instruction that uses it. The result was the
/// expensive direction of a streamability error — a crawling <c>xsl:for-each</c> whose body
/// merely shallow-copies each element was REJECTED as non-streamable, so the stylesheet did not
/// run at all rather than producing a wrong answer (W3C streamable-030, a plain identity-style
/// crawl).
/// </summary>
public sealed class StreamingShallowCopyTests : System.IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shallowcopy-" + Path.GetRandomFileName());

    public StreamingShallowCopyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private async Task<(bool Accepted, string Output)> Run(string forEachBody)
    {
        var src = Path.Combine(_dir, "in.xml");
        await File.WriteAllTextAsync(src, "<r><a id='1'><b/></a></r>").ConfigureAwait(true);
        var href = src.Replace("\\", "/", System.StringComparison.Ordinal);
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:mode streamable="yes"/>
              <xsl:template name="main">
                <out><xsl:source-document streamable="yes" href="{href}"><xsl:apply-templates select="."/></xsl:source-document></out>
              </xsl:template>
              <xsl:template match="r">
                <xsl:variable name="t" as="node()*">
                  <xsl:for-each select=".//*">{forEachBody}</xsl:for-each>
                </xsl:variable>
                <n><xsl:value-of select="count($t)"/></n>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        try
        {
            await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
            t.SetInitialTemplate("main");
            return (true, await t.TransformAsync("<i/>").ConfigureAwait(true));
        }
        catch (System.Exception ex)
        {
            return (false, ex.Message);
        }
    }

    [Fact]
    public async Task ShallowCopyOfTheContextItem_IsMotionless()
    {
        // The rejection: a plain identity-style crawl would not run at all.
        var (accepted, output) = await Run("""<xsl:copy select="."/>""").ConfigureAwait(true);
        accepted.Should().BeTrue(because: "a shallow copy reads only the start tag");
        output.Should().Contain("<n>2</n>");
    }

    [Fact]
    public async Task ShallowCopyWithAttributes_IsMotionless()
    {
        var (accepted, _) = await Run("""<xsl:copy select="."/><xsl:copy-of select="@*"/>""")
            .ConfigureAwait(true);
        accepted.Should().BeTrue();
    }

    [Fact]
    public async Task DeepCopyOfTheContextItem_IsStillRejected()
    {
        // The distinction that matters: copy-of takes the whole subtree, so a crawling
        // for-each doing it per element genuinely is not streamable.
        var (accepted, message) = await Run("""<xsl:copy-of select="."/>""").ConfigureAwait(true);
        accepted.Should().BeFalse();
        message.Should().Contain("XTSE3430");
    }

    [Fact]
    public async Task ShallowCopyOfADownwardSelect_IsStillRejected()
    {
        // xsl:copy select="child::x" reads below the context node to decide what to copy.
        var (accepted, message) = await Run("""<xsl:copy select="child::b"/>""").ConfigureAwait(true);
        accepted.Should().BeFalse();
        message.Should().Contain("XTSE3430");
    }

    [Fact]
    public async Task ConsumingContentInsideAShallowCopy_IsStillRejected()
    {
        // The content of an xsl:copy is walked as usual — exempting the select must not
        // exempt what is nested inside it.
        var (accepted, message) = await Run("""<xsl:copy select="."><xsl:value-of select="."/></xsl:copy>""")
            .ConfigureAwait(true);
        accepted.Should().BeFalse();
        message.Should().Contain("XTSE3430");
    }
}
