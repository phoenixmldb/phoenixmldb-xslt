using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// fn:has-children() on a streamed element (BUGS #92, W3C streamable-135).
///
/// A streamed element is shallow — its children have not been read yet — so has-children()
/// answered from an empty child list and was ALWAYS false. The answer is the next event, so the
/// streaming reader now looks one event ahead and replays what it read, invisibly to everything
/// else reading from it.
///
/// The cheap fix, !reader.IsEmptyElement, passes streamable-135 and is wrong: &lt;a&gt;&lt;/a&gt; is not an
/// empty element and has no children. BUGS #92 warned that the corpus cannot tell the two apart,
/// so the cases that can are here.
/// </summary>
public sealed class StreamedHasChildrenTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-hasch-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> RunAsync(string source, string extraDecls = "")
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "in.xml"), source);
        var xsl = $$"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="yes"/>
              {{extraDecls}}
              <xsl:template name="main"><out><xsl:source-document streamable="yes" href="in.xml">
                <xsl:apply-templates/></xsl:source-document></out></xsl:template>
              <xsl:template match="*[has-children()]"><xsl:copy><xsl:attribute name="kids" select="'yes'"/><xsl:apply-templates/></xsl:copy></xsl:template>
              <xsl:template match="*[not(has-children())]"><xsl:copy><xsl:attribute name="kids" select="'no'"/></xsl:copy></xsl:template>
            </xsl:stylesheet>
            """;
        var path = Path.Combine(_dir, "s.xsl");
        await File.WriteAllTextAsync(path, xsl);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl, new Uri(path));
        t.SetInitialTemplate("main");
        return await t.TransformAsync("<dummy/>");
    }

    /// <summary>The streamable-135 shape: an element WITH children must say so.</summary>
    [Fact]
    public async Task An_element_with_children_has_children()
        => (await RunAsync("<r><a><b/></a></r>")).Should().Contain("<a kids=\"yes\"><b kids=\"no\"");

    [Fact]
    public async Task A_self_closing_element_has_no_children()
        => (await RunAsync("<r><a/></r>")).Should().Contain("<a kids=\"no\"");

    /// <summary>
    /// The case the corpus cannot see: a start and end tag with nothing between. IsEmptyElement is
    /// false here, so the cheap fix answers yes — wrongly.
    /// </summary>
    [Fact]
    public async Task A_start_and_end_tag_with_nothing_between_has_no_children()
        => (await RunAsync("<r><a></a></r>")).Should().Contain("<a kids=\"no\"");

    /// <summary>A text-only element has a child: its text node.</summary>
    [Fact]
    public async Task Text_is_a_child()
        => (await RunAsync("<r><a>x</a></r>")).Should().Contain("""<a kids="yes">x</a>""");

    /// <summary>
    /// Whitespace-only content is a child unless xsl:strip-space removes it. Asked of the DOCUMENT
    /// element, which the streaming loop dispatches live from its start tag — that is where the
    /// lookahead runs and has to read PAST stripped whitespace to find the real answer.
    ///
    /// Only the has-children answer is asserted. Streamed output does not yet apply xsl:strip-space
    /// at all (a separate defect), so the whitespace itself is not what is under test here.
    /// </summary>
    [Fact]
    public async Task Whitespace_is_a_child_when_not_stripped()
        => (await RunAsync("<a>  </a>")).Should().Contain("""<a kids="yes">""");

    [Fact]
    public async Task Stripped_whitespace_is_not_a_child()
        => (await RunAsync("<a>  </a>", """<xsl:strip-space elements="a"/>""")).Should().Contain("<a kids=\"no\"");

    [Fact]
    public async Task A_child_behind_stripped_whitespace_is_still_found()
        => (await RunAsync("<a>  <b/>  </a>", """<xsl:strip-space elements="a"/>""")).Should().Contain("""<a kids="yes">""");

    /// <summary>
    /// The replay must be invisible: the start tag's attributes are read AFTER the peek by
    /// copy-of(@*), and the children by apply-templates. Neither may lose anything.
    /// </summary>
    [Fact]
    public async Task Attributes_and_children_survive_the_lookahead()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "in.xml"), """<r><a id="1" n="x"><b>t</b></a></r>""");
        var xsl = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="yes"/>
              <xsl:template name="main"><out><xsl:source-document streamable="yes" href="in.xml">
                <xsl:apply-templates/></xsl:source-document></out></xsl:template>
              <xsl:template match="a[has-children()]"><a2><xsl:copy-of select="@*"/><xsl:apply-templates/></a2></xsl:template>
            </xsl:stylesheet>
            """;
        var path = Path.Combine(_dir, "s.xsl");
        await File.WriteAllTextAsync(path, xsl);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl, new Uri(path));
        t.SetInitialTemplate("main");
        (await t.TransformAsync("<dummy/>")).Should().Contain("""<a2 id="1" n="x">t</a2>""");
    }
}
