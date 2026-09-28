using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A streamed xsl:iterate or xsl:apply-templates over a child step visits only the children
/// that step selects.
///
/// Both drivers accepted any single child step — a name, *, node(), text() — and then ignored
/// which: iterate visited every child element, and apply-templates was not even handed the
/// select. W3C sx-gc-eq-801 iterates select="ProteinEntry" under an element whose FIRST child is
/// &lt;Database&gt;; the body's test was false there, xsl:break ended the iteration, and the
/// output was empty.
///
/// Parity tests: the same stylesheet streamed and unstreamed must give identical output.
/// </summary>
public sealed class StreamedChildSelectTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-csel-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> ParityAsync(string source, string templates)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "in.xml"), source);
        string? previous = null;
        foreach (var s in new[] { "no", "yes" })
        {
            var xsl = $$"""
                <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                  <xsl:mode streamable="{{s}}"/>
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
            var output = await t.TransformAsync("<dummy/>");
            if (previous != null)
                output.Should().Be(previous, "streamed and unstreamed runs of one stylesheet must agree");
            previous = output;
        }
        return previous!;
    }

    /// <summary>The sx-gc-eq-801 shape: a non-matching first child, then a break on mismatch.</summary>
    [Fact]
    public async Task Iterate_skips_children_its_select_does_not_name()
        => (await ParityAsync("<db><info>x</info><entry><uid>A</uid></entry><entry><uid>B</uid></entry></db>", """
            <xsl:template match="db"><r>
              <xsl:iterate select="entry">
                <xsl:choose>
                  <xsl:when test="uid = 'A'"><hit/></xsl:when>
                  <xsl:otherwise><xsl:break/></xsl:otherwise>
                </xsl:choose>
              </xsl:iterate>
            </r></xsl:template>
            """)).Should().Contain("<r><hit/></r>");

    [Fact]
    public async Task Iterate_counts_position_over_the_selected_children_only()
        => (await ParityAsync("<db><x/><e/><x/><e/></db>", """
            <xsl:template match="db"><r><xsl:iterate select="e"><p n="{position()}"/></xsl:iterate></r></xsl:template>
            """)).Should().Contain("""<r><p n="1"/><p n="2"/></r>""");

    [Fact]
    public async Task Apply_templates_processes_only_the_selected_children()
        => (await ParityAsync("<db><x>no</x><e>yes</e><x>no</x></db>", """
            <xsl:template match="db"><r><xsl:apply-templates select="e"/></r></xsl:template>
            <xsl:template match="e"><E><xsl:value-of select="."/></E></xsl:template>
            <xsl:template match="x"><X/></xsl:template>
            """)).Should().Contain("<r><E>yes</E></r>");

    /// <summary>Guard: * still selects every child element.</summary>
    [Fact]
    public async Task A_wildcard_still_selects_every_child_element()
        => (await ParityAsync("<db><x/><e/></db>", """
            <xsl:template match="db"><r><xsl:iterate select="*"><c n="{local-name()}"/></xsl:iterate></r></xsl:template>
            """)).Should().Contain("""<r><c n="x"/><c n="e"/></r>""");
}
