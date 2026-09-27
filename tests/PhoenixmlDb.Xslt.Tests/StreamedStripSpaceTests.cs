using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:strip-space applies to streamed input exactly as to a buffered document.
///
/// It did not apply at all: the tree path strips whitespace while building the document, and the
/// streaming path builds no document, so the same stylesheet produced &lt;r&gt;&lt;a&gt;x&lt;/a&gt;&lt;/r&gt; unstreamed and
/// kept every whitespace node streamed. The W3C harness compares whitespace-insensitively by
/// default, so the corpus never flagged it.
///
/// Every test is a PARITY test: the same stylesheet and input, streamed and unstreamed, must give
/// the same output. That needs nobody to derive the right answer by hand — a disagreement between
/// the two paths is the defect.
/// </summary>
public sealed class StreamedStripSpaceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-strip-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> RunAsync(bool streamed, string source, string declarations, string templates = "")
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "in.xml"), source);
        var s = streamed ? "yes" : "no";
        var xsl = $$"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="{{s}}" on-no-match="shallow-copy"/>
              {{declarations}}
              <xsl:template name="main"><out><xsl:source-document streamable="{{s}}" href="in.xml">
                <xsl:apply-templates/></xsl:source-document></out></xsl:template>
              {{templates}}
            </xsl:stylesheet>
            """;
        var path = Path.Combine(_dir, $"s-{s}.xsl");
        await File.WriteAllTextAsync(path, xsl);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl, new Uri(path));
        t.SetInitialTemplate("main");
        return await t.TransformAsync("<dummy/>");
    }

    private async Task<string> AssertParityAsync(string source, string declarations, string templates = "")
    {
        var unstreamed = await RunAsync(false, source, declarations, templates);
        var streamed = await RunAsync(true, source, declarations, templates);
        streamed.Should().Be(unstreamed, "streamed and unstreamed runs of one stylesheet must agree");
        return streamed;
    }

    private const string Indented = "<r>\n  <a>x</a>\n  <a> </a>\n  <b>\n    <c/>\n  </b>\n</r>";

    [Fact]
    public async Task Strip_space_star_removes_whitespace_when_streamed()
        => (await AssertParityAsync(Indented, """<xsl:strip-space elements="*"/>"""))
            .Should().Contain("<r><a>x</a><a></a><b><c></c></b></r>");

    /// <summary>Only the named element strips; whitespace elsewhere stays.</summary>
    [Fact]
    public async Task Strip_space_on_one_element_leaves_the_others()
        => await AssertParityAsync(Indented, """<xsl:strip-space elements="b"/>""");

    /// <summary>A preserve-space declaration outranks a less specific strip-space.</summary>
    [Fact]
    public async Task Preserve_space_outranks_strip_space_when_streamed()
        => await AssertParityAsync(Indented, """<xsl:strip-space elements="*"/><xsl:preserve-space elements="b"/>""");

    /// <summary>
    /// xml:space="preserve" in the source protects its whitespace from strip-space (XSLT 3.0 §4.3).
    /// This parity test found the UNSTREAMED path wrong: it ignored xml:space and stripped anyway.
    /// </summary>
    [Fact]
    public async Task Xml_space_preserve_is_respected_both_ways()
        => (await AssertParityAsync("""<r>  <p xml:space="preserve">  <i/>  </p>  </r>""", """<xsl:strip-space elements="*"/>"""))
            .Should().Contain("""<r><p xml:space="preserve">  <i></i>  </p></r>""");

    /// <summary>xml:space="default" below a preserve switches stripping back on.</summary>
    [Fact]
    public async Task Xml_space_default_below_preserve_strips_again()
        => (await AssertParityAsync("""<r><p xml:space="preserve"> <q xml:space="default"> <i/> </q> </p></r>""", """<xsl:strip-space elements="*"/>"""))
            .Should().Contain("""<q xml:space="default"><i></i></q>""");

    /// <summary>
    /// A template that needs its element's whole subtree runs on a copy materialised from the
    /// stream. That copy must be stripped too — it was built by a separate reader loop.
    /// </summary>
    [Fact]
    public async Task A_materialised_subtree_is_stripped_too()
        => await AssertParityAsync(Indented, """<xsl:strip-space elements="*"/>""",
            """<xsl:template match="b"><B n="{count(node())}"><xsl:copy-of select="node()"/></B></xsl:template>""");

    /// <summary>Guard: with no strip-space declaration, whitespace is kept — streamed as unstreamed.</summary>
    [Fact]
    public async Task Without_strip_space_whitespace_is_kept()
        => (await AssertParityAsync(Indented, ""))
            .Should().Contain("<a> </a>");
}
