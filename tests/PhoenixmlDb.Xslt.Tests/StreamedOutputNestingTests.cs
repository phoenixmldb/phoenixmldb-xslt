using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Streamed output keeps its nesting (#181).
///
/// The streaming loop closes, at an element's end tag, an output tag that the element's dispatch
/// left open — a built-in shallow-copy or an xsl:copy. It popped whenever the open-tag stack was
/// non-empty, without asking whether THIS element opened one. An element whose template built its
/// own complete result opened nothing, so its end closed the PARENT's tag and the rest of the
/// document landed outside the root. Separately, a streamed apply-templates inside the template
/// for a self-closing element read on past it and took its following siblings as children.
///
/// Parity tests: the same stylesheet streamed and unstreamed must give identical output.
/// </summary>
public sealed class StreamedOutputNestingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-nest-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> RunAsync(bool streamed, string source, string templateForA)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "in.xml"), source);
        var s = streamed ? "yes" : "no";
        var xsl = $$"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="{{s}}" on-no-match="shallow-copy"/>
              <xsl:template name="main"><out><xsl:source-document streamable="{{s}}" href="in.xml">
                <xsl:apply-templates/></xsl:source-document></out></xsl:template>
              <xsl:template match="a">{{templateForA}}</xsl:template>
            </xsl:stylesheet>
            """;
        var path = Path.Combine(_dir, $"s-{s}.xsl");
        await File.WriteAllTextAsync(path, xsl);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl, new Uri(path));
        t.SetInitialTemplate("main");
        return await t.TransformAsync("<dummy/>");
    }

    [Theory]
    // An empty element written with start and end tag, template building a complete result.
    [InlineData("<r><a></a><c/></r>", "<A/>", "<r><A/><c></c></r>")]
    // The same, one level down — the parent that got closed is not the root.
    [InlineData("<r><x><a></a></x><c/></r>", "<a><xsl:apply-templates/></a>", "<r><x><a/></x><c></c></r>")]
    // With apply-templates in the body and siblings after it.
    [InlineData("<r><a></a><a><b/></a></r>", "<a><xsl:apply-templates/></a>", "<r><a/><a><b></b></a></r>")]
    // Self-closing element, complete result: the self-closing branch had the same flaw.
    [InlineData("<r><a/><c/></r>", "<A/>", "<r><A/><c></c></r>")]
    // Self-closing element with apply-templates: read on past <a/> into its siblings.
    [InlineData("<r><a/><a><b/></a></r>", "<a><xsl:apply-templates/></a>", "<r><a/><a><b></b></a></r>")]
    public async Task Streamed_output_nests_like_unstreamed(string source, string templateForA, string expected)
    {
        var unstreamed = await RunAsync(false, source, templateForA);
        var streamed = await RunAsync(true, source, templateForA);

        streamed.Should().Be(unstreamed, "streamed and unstreamed runs of one stylesheet must agree");
        streamed.Should().Contain(expected);
    }
}
