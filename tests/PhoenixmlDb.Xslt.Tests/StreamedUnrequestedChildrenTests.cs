using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A streamed element's children are processed only when its template asks for them (#180).
///
/// The streaming loop reads an element's children after its template returns, and only an EMPTY
/// body stopped it. So a template for a producing a literal &lt;A/&gt; streamed as &lt;A/&gt;&lt;b/&gt;: the
/// built-in rule ran on children nobody requested. Now a matched template whose body never read
/// into the children leaves them unprocessed, as in an unstreamed run.
///
/// Parity tests: the same stylesheet streamed and unstreamed must give identical output. The
/// guards matter as much as the fixes — several constructs hand the children to the loop ON
/// PURPOSE without reading them, and must keep working.
/// </summary>
public sealed class StreamedUnrequestedChildrenTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-unreq-").FullName;

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
                  <xsl:mode streamable="{{s}}" on-no-match="shallow-copy"/>
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
            // <a></a> and <a/> are the same XML; a streamed xsl:copy closes its tag at the
            // element's end and so writes the first form. Compare the documents, not the spelling.
            var output = System.Text.RegularExpressions.Regex.Replace(
                await t.TransformAsync("<dummy/>"), @"<([\w:.-]+)([^<>]*?)></\1>", "<$1$2/>");
            if (previous != null)
                output.Should().Be(previous, "streamed and unstreamed runs of one stylesheet must agree");
            previous = output;
        }
        return previous!;
    }

    [Fact]
    public async Task A_literal_result_does_not_pull_in_the_children()
        => (await ParityAsync("<r><a><b/></a></r>", """<xsl:template match="a"><A/></xsl:template>"""))
            .Should().Contain("<r><A/></r>");

    [Fact]
    public async Task An_empty_copy_does_not_pull_in_the_children()
        => (await ParityAsync("<r><a><b/>t</a></r>", """<xsl:template match="a"><xsl:copy/></xsl:template>"""))
            .Should().Contain("<r><a/></r>");

    [Fact]
    public async Task A_copy_with_only_attributes_does_not_pull_in_the_children()
        => (await ParityAsync("<r><a><b/></a></r>", """<xsl:template match="a"><xsl:copy><xsl:attribute name="k" select="'v'"/></xsl:copy></xsl:template>"""))
            .Should().Contain("""<r><a k="v"/></r>""");

    // ---- guards: constructs that DO ask for the children -----------------------------------

    [Fact]
    public async Task Copy_with_apply_templates_still_processes_the_children()
        => (await ParityAsync("<r><a><b/>t</a></r>", """<xsl:template match="a"><xsl:copy><xsl:apply-templates/></xsl:copy></xsl:template>"""))
            .Should().Contain("<r><a><b/>t</a></r>");

    /// <summary>next-match falls through to the built-in shallow-copy, which leaves the children
    /// to the loop without reading them itself.</summary>
    [Fact]
    public async Task Next_match_to_the_built_in_rule_still_processes_the_children()
        => (await ParityAsync("<r><a><b/></a></r>", """<xsl:template match="a"><X><xsl:next-match/></X></xsl:template>"""))
            .Should().Contain("<r><X><a><b/></a></X></r>");

    /// <summary>Martin Honnen's shape (1.3.10): grouping the children inside xsl:copy.</summary>
    [Fact]
    public async Task Grouping_children_inside_copy_still_works()
        => (await ParityAsync("<r><body><h1>A</h1><p>1</p><h1>B</h1><p>2</p></body></r>", """
            <xsl:template match="body"><xsl:copy>
              <xsl:for-each-group select="*" group-starting-with="h1"><sec><xsl:copy-of select="current-group()"/></sec></xsl:for-each-group>
            </xsl:copy></xsl:template>
            """))
            .Should().Contain("<body><sec><h1>A</h1><p>1</p></sec><sec><h1>B</h1><p>2</p></sec></body>");
}
