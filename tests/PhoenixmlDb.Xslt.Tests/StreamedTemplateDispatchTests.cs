using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Three defects in how a streamed source document is dispatched to templates, found together
/// behind W3C attr/streamable-064/065/066 (BUGS #94). Each hid the next.
///
/// 1. An element with NO matching template, under a streaming pass whose current template is
///    guaranteed-streamable, tripped the #143 guard. The guard concluded structure was about to
///    collapse and DEEP-COPIED the element — so every template below it was skipped and the
///    source came out verbatim. The streaming loop was going to process the children anyway;
///    that IS the built-in text-only-copy rule.
/// 2. With the copy gone, templates below it ran — and a template reading its own element with
///    value-of select="." read the SHALLOW streamed element: empty, or a throw under
///    StrictStringValue. The scanner builds watchers for child paths, not for a bare ".", so
///    nothing buffered or deferred it.
/// 3. The document-level streaming interception accepted ANY document as the streamed one. A
///    doc() result processed in another mode recursed with a select-less apply-templates, took
///    over the live reader, and consumed the whole stream in the wrong mode.
/// </summary>
public sealed class StreamedTemplateDispatchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-stdisp-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const string Book = """
        <book><title>T</title><chapter><v>In the <deity>G</deity> beginning</v><v>second</v></chapter></book>
        """;

    private const string Sections = "<sections><title>A</title><title>B</title></sections>";

    private async Task<string> RunAsync(string templates)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "book.xml"), Book);
        await File.WriteAllTextAsync(Path.Combine(_dir, "sections.xml"), Sections);
        var xsl = $$"""
            <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="yes"/>
              <xsl:strip-space elements="*"/>
              <xsl:template match="text()" mode="#all"/>
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
    /// Defect 1. `book` and `chapter` match nothing; `v` does. The templates must fire — output is
    /// the v template's result, NOT a verbatim copy of the source.
    /// </summary>
    [Fact]
    public async Task Templates_below_an_unmatched_element_still_fire()
    {
        var output = await RunAsync("""
            <xsl:template name="main"><out><xsl:source-document streamable="yes" href="book.xml">
              <xsl:apply-templates select="."/></xsl:source-document></out></xsl:template>
            <xsl:template match="/"><xsl:apply-templates/></xsl:template>
            <xsl:template match="v"><verse/></xsl:template>
            """);

        output.Should().Contain("<verse/><verse/>", "the v template must run for both verses");
        output.Should().NotContain("<book>", "an unmatched element must not be deep-copied");
        output.Should().NotContain("<deity>", "a verbatim copy of the source is the defect");
    }

    /// <summary>
    /// Defect 2. The canonical streaming shape: a matched template reading its own element's text.
    /// On unfixed source this read a shallow element — empty, or a throw under StrictStringValue.
    /// </summary>
    [Fact]
    public async Task A_matched_template_reads_its_own_elements_text()
    {
        var output = await RunAsync("""
            <xsl:template name="main"><out><xsl:source-document streamable="yes" href="book.xml">
              <xsl:apply-templates/></xsl:source-document></out></xsl:template>
            <xsl:template match="v"><verse><xsl:value-of select="."/></verse></xsl:template>
            """);

        output.Should().Contain("<verse>In the G beginning</verse><verse>second</verse>");
    }

    /// <summary>
    /// Defects 1 and 2 together, as W3C streamable-064 has them: tunnel parameters from two levels
    /// must reach a template two unmatched elements down, and its text must be there.
    /// </summary>
    [Fact]
    public async Task Tunnel_parameters_reach_a_template_below_unmatched_elements()
    {
        var output = await RunAsync("""
            <xsl:template name="main"><out><xsl:source-document streamable="yes" href="book.xml">
              <xsl:apply-templates select="."><xsl:with-param name="p" select="17" tunnel="yes"/></xsl:apply-templates>
            </xsl:source-document></out></xsl:template>
            <xsl:template match="/"><xsl:apply-templates><xsl:with-param name="q" select="23" tunnel="yes"/></xsl:apply-templates></xsl:template>
            <xsl:template match="v">
              <xsl:param name="p" tunnel="yes" required="yes"/><xsl:param name="q" tunnel="yes" required="yes"/>
              <v p="{$p}" q="{$q}"><xsl:value-of select="."/></v>
            </xsl:template>
            """);

        output.Should().Contain("""<v p="17" q="23">In the G beginning</v><v p="17" q="23">second</v>""");
    }

    /// <summary>
    /// Defect 3, as W3C streamable-065 has it: processing ANOTHER document in another mode, in
    /// the middle of a streamed pass, must not take over the live reader. On unfixed source the
    /// doc() pass consumed the whole stream and the output was an empty &lt;out/&gt;.
    /// </summary>
    [Fact]
    public async Task Another_document_in_scope_does_not_consume_the_stream()
    {
        var output = await RunAsync("""
            <xsl:template name="main"><out><xsl:source-document streamable="yes" href="book.xml">
              <xsl:apply-templates select="."/></xsl:source-document></out></xsl:template>
            <xsl:template match="/">
              <xsl:variable name="t" as="element()*"><xsl:apply-templates select="doc('sections.xml')" mode="other"/></xsl:variable>
              <xsl:apply-templates><xsl:with-param name="t" select="$t" tunnel="yes"/></xsl:apply-templates>
            </xsl:template>
            <xsl:template match="title" mode="other"><xsl:copy-of select="."/></xsl:template>
            <xsl:template match="chapter">
              <xsl:param name="t" tunnel="yes" as="element()*"/>
              <chapter n="{count($t)}"><xsl:sequence select="$t"/></chapter>
            </xsl:template>
            """);

        output.Should().Contain("""<chapter n="2"><title>A</title><title>B</title></chapter>""",
            "the streamed book must still be processed after the other document was");
    }
}
