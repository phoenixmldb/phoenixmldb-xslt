using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A matched streaming template whose body needs its element's whole subtree runs against a copy
/// materialised from the live reader. That copy was built DETACHED, so its ancestor axis stopped at
/// itself: <c>../@id</c> read nothing and <c>count(ancestor::*)</c> was 0 (W3C stream-211,
/// streamable-138/139; BUGS #93).
///
/// The obvious fix — link the copy to its streamed parent — used to break accumulator-after(),
/// because the accumulator machinery finds a node's tree by walking to its root: with a parent, the
/// walk left the buffered subtree and landed on the streamed tree, where nothing was computed
/// (XTDE3362). So the copy now gets its parent, and the tree-finding walks treat it as a root
/// regardless. <c>AccumulatorAfter_CountsTheSubtree</c> guards that half.
/// </summary>
public sealed class BufferedSubtreeAncestorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-bufanc-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> RunAsync(string templates)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "in.xml"),
            """<root><item id="i1"><foo>one</foo></item><item id="i2"><foo>two</foo></item></root>""");
        var xsl = $$"""
            <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="yes"/>
              <xsl:template match="text()"/>
              <xsl:template name="main"><out><xsl:source-document streamable="yes" href="in.xml">
                <xsl:apply-templates/></xsl:source-document></out></xsl:template>
              {{templates}}
            </xsl:transform>
            """;
        var path = Path.Combine(_dir, "s.xsl");
        await File.WriteAllTextAsync(path, xsl);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl, new Uri(path));
        t.SetInitialTemplate("main");
        return await t.TransformAsync("<dummy/>");
    }

    /// <summary>
    /// The body reads its own text, so the subtree is buffered, and reads its parent's attribute.
    /// On unfixed source the buffered copy had no parent and @parent came out empty.
    /// </summary>
    [Fact]
    public async Task A_buffered_template_reads_its_parents_attribute()
    {
        var output = await RunAsync("""
            <xsl:template match="foo"><f parent="{../@id}"><xsl:value-of select="."/></f></xsl:template>
            """);

        output.Should().Contain("""<f parent="i1">one</f><f parent="i2">two</f>""");
    }

    /// <summary>The whole ancestor chain, not just the parent (streamable-138/139).</summary>
    [Fact]
    public async Task A_buffered_template_sees_its_whole_ancestor_chain()
    {
        var output = await RunAsync("""
            <xsl:template match="foo"><f depth="{count(ancestor::*)}"><xsl:value-of select="."/></f></xsl:template>
            """);

        output.Should().Contain("""<f depth="2">one</f>""", "foo sits under item under root");
    }
}
