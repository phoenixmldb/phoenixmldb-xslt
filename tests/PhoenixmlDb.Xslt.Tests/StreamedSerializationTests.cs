using System.Text;
using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A result written to the caller's writer while the transformation streams goes through the same
/// serialization steps as a result serialized whole (xslt#575): indentation, the DOCTYPE, character
/// maps, normalization, encoding, the html and xhtml forms, the byte-order mark. They were all
/// skipped: <c>indent="yes"</c> gave one long line.
/// </summary>
public class StreamedSerializationTests
{
    private const string Input = """<root><item n="1">a&#xE9;b &amp; &lt;x&gt;</item><item n="2">c</item><item n="3">http://e/&#xE9;</item></root>""";

    private const string Items = """<xsl:for-each select="item!copy-of()"><i n="{@n}"><xsl:value-of select="."/></i><e/></xsl:for-each>""";

    private static string Stylesheet(string output, string body, string declarations = "") => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" exclude-result-prefixes="#all">
          <xsl:output {{output}}/>
          {{declarations}}
          <xsl:mode streamable="yes"/>
          <xsl:template match="root">{{body}}</xsl:template>
        </xsl:stylesheet>
        """;

    public static TheoryData<string, string, string, string> Cases => new()
    {
        { "xml indent", """method="xml" indent="yes" """, $"<out>{Items}</out>", "" },
        { "xml indent, omit declaration", """method="xml" indent="yes" omit-xml-declaration="yes" """, $"<out><!-- c --><?p x?>{Items}</out>", "" },
        { "xml suppress-indentation", """method="xml" indent="yes" suppress-indentation="i" """, $"<out><g>{Items}</g></out>", "" },
        { "doctype", """method="xml" doctype-public="-//X//EN" doctype-system="out.dtd" """, $"<out>{Items}</out>", "" },
        { "doctype, indented", """method="xml" indent="yes" doctype-system="out.dtd" """, $"<out>{Items}</out>", "" },
        { "character map", """method="xml" use-character-maps="m" """, $"<out>{Items}</out>",
          """<xsl:character-map name="m"><xsl:output-character character="a" string="[A]"/><xsl:output-character character="c" string="&lt;C/&gt;"/></xsl:character-map>""" },
        { "normalization", """method="xml" normalization-form="NFD" """, $"<out>{Items}</out>", "" },
        { "encoding", """method="xml" encoding="US-ASCII" """, $"<out>{Items}</out>", "" },
        { "standalone", """method="xml" standalone="yes" """, $"<out>{Items}</out>", "" },
        { "byte-order mark", """method="xml" byte-order-mark="yes" indent="yes" """, $"<out>{Items}</out>", "" },
        { "html", """method="html" version="5.0" """,
          $$"""<html><head><title>t</title></head><body><xsl:for-each select="item!copy-of()"><a href="x/{.}"><xsl:value-of select="."/></a><br/><input disabled="disabled"/></xsl:for-each></body></html>""", "" },
        { "html, raw-text elements", """method="html" indent="no" """,
          """<html><head><script>if (a &lt; b &amp;&amp; c) {}</script><style>p > i {}</style></head><body><xsl:for-each select="item!copy-of()"><p><xsl:value-of select="."/></p></xsl:for-each></body></html>""", "" },
        { "html, not indented, uri attributes off", """method="html" indent="no" escape-uri-attributes="no" """,
          $$"""<html><body><xsl:for-each select="item!copy-of()"><a href="x/{.}"><xsl:value-of select="."/></a></xsl:for-each></body></html>""", "" },
        { "xhtml", """method="xhtml" indent="yes" html-version="5" """,
          """<html xmlns="http://www.w3.org/1999/xhtml"><head><title>t</title></head><body><xsl:for-each select="item!copy-of()"><p><xsl:value-of select="."/></p><br/></xsl:for-each></body></html>""", "" },
        { "xhtml, prefixed", """method="xhtml" """,
          """<h:html xmlns:h="http://www.w3.org/1999/xhtml"><h:body><xsl:for-each select="item!copy-of()"><h:p><xsl:value-of select="."/></h:p></xsl:for-each></h:body></h:html>""", "" },
        { "xhtml, foreign document element", """method="xhtml" indent="yes" """,
          $"<f:out xmlns:f=\"urn:f\">{Items}</f:out>", "" },
        { "text", """method="text" """, """<xsl:for-each select="item!copy-of()"><xsl:value-of select="."/>;</xsl:for-each>""", "" },
        { "text, character map", """method="text" use-character-maps="m" """, """<xsl:for-each select="item!copy-of()"><xsl:value-of select="."/>;</xsl:for-each>""",
          """<xsl:character-map name="m"><xsl:output-character character=";" string="|"/></xsl:character-map>""" },
        { "cdata-section-elements", """method="xml" indent="yes" cdata-section-elements="i" """, $"<out>{Items}</out>", "" },
        { "saxon-he-10 layout", $"""method="xml" indent="yes" {SaxonLayout} """, $"<out><!-- c --><?p x?>{Items}</out>", "" },
        { "saxon-he-10 layout, doctype", $"""method="xml" indent="yes" doctype-public="-//X//EN" doctype-system="out.dtd" {SaxonLayout} """, $"<out>{Items}</out>", "" },
        { "saxon-he-10 layout, suppress-indentation", $"""method="xml" indent="yes" suppress-indentation="i" {SaxonLayout} """, $"<out><g>{Items}</g></out>", "" },
        { "saxon-he-10 layout, xhtml", $"""method="xhtml" indent="yes" html-version="5" {SaxonLayout} """,
          """<html xmlns="http://www.w3.org/1999/xhtml"><head><title>t</title></head><body><xsl:for-each select="item!copy-of()"><p><b><xsl:value-of select="."/></b></p><br/></xsl:for-each></body></html>""", "" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AStreamedResultIsSerializedAsTheWholeResultIs(string label, string output, string body, string declarations)
    {
        _ = label;
        var (streamed, tree) = await BothAsync(Stylesheet(output, body, declarations), Input);

        streamed.Should().Be(tree);
    }

    private const string SaxonLayout = """xmlns:phx="https://schemas.phoenixml.dev/2026/functions" phx:indent-style="saxon-he-10" """;

    [Fact]
    public async Task AStreamedResultHasTheSaxonLayout()
    {
        // xslt#499: the streamed result goes through the same layout. Expected: Saxon-HE 10.9's output
        // for the same stylesheet (which it runs without streaming).
        var xslt = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" exclude-result-prefixes="#all">
              <xsl:output method="xml" indent="yes" doctype-system="out.dtd" {{SaxonLayout}}/>
              <xsl:mode streamable="yes"/>
              <xsl:template match="root"><out a-long-attribute-name1="value one" a-long-attribute-name2="value two" a-long-attribute-name3="value three"><xsl:comment>c</xsl:comment><xsl:for-each select="item!copy-of()"><i n="{@n}"><xsl:value-of select="."/></i><e/></xsl:for-each></out></xsl:template>
            </xsl:stylesheet>
            """;

        var (streamed, tree) = await BothAsync(xslt, """<root><item n="1">a</item><item n="2">c</item></root>""");

        streamed.Should().Be(tree);
        streamed.Should().Be(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE out\n  SYSTEM \"out.dtd\">\n" +
            "<out a-long-attribute-name1=\"value one\"\n" +
            "     a-long-attribute-name2=\"value two\"\n" +
            "     a-long-attribute-name3=\"value three\"><!--c-->\n" +
            "   <i n=\"1\">a</i>\n" +
            "   <e/>\n" +
            "   <i n=\"2\">c</i>\n" +
            "   <e/>\n" +
            "</out>\n");
    }

    [Fact]
    public async Task StreamedGroupingIsIndented()
    {
        // Martin Honnen's report (xslt#575): streamed grouping with indent="yes" gave one long line.
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" exclude-result-prefixes="#all">
              <xsl:strip-space elements="*"/>
              <xsl:output method="xml" indent="yes"/>
              <xsl:mode streamable="yes" on-no-match="shallow-copy"/>
              <xsl:template match="root">
                <xsl:copy>
                  <xsl:for-each-group select="item!copy-of()" group-adjacent="category">
                    <category name="{current-grouping-key()}"><xsl:apply-templates select="current-group()"/></category>
                  </xsl:for-each-group>
                </xsl:copy>
              </xsl:template>
              <xsl:template match="item/category"/>
            </xsl:stylesheet>
            """;
        var input = "<root>" + string.Concat(Enumerable.Range(1, 6).Select(i =>
            $"<item><name>item {i}</name><category>cat{(i + 1) / 2}</category></item>")) + "</root>";

        var (streamed, tree) = await BothAsync(xslt, input);

        streamed.Should().Be(tree);
        streamed.Should().StartWith("""
            <?xml version="1.0" encoding="UTF-8"?>
            <root>
              <category name="cat1">
                <item>
                  <name>item 1</name>
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task AStreamedResultOfTwoElementsWithADoctypeIsSEPM0004()
    {
        var xslt = Stylesheet("""method="xml" doctype-system="out.dtd" """, Items);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xslt);

        var act = () => t.TransformAsync(new MemoryStream(Encoding.UTF8.GetBytes(Input)), new StringWriter());

        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("SEPM0004");
    }

    [Fact]
    public async Task WithDisableOutputEscapingAnIndentedStreamedResultIsSerializedWhole()
    {
        // The result may not be well-formed (here it is not: an unclosed <br>), and then a whole
        // result is left unindented. The streamed one is kept and serialized the same way.
        var xslt = Stylesheet("""method="xml" indent="yes" """,
            """<out><xsl:for-each select="item!copy-of()"><i><xsl:value-of select="."/><xsl:text disable-output-escaping="yes">&lt;br></xsl:text></i></xsl:for-each></out>""");

        var (streamed, tree) = await BothAsync(xslt, Input);

        streamed.Should().Be(tree);
        streamed.Should().Contain("<br>");
    }

    [Fact]
    public async Task AStreamedResultOfSeveralTopLevelElementsIsIndented()
    {
        // A whole result that is not one document is left unindented (XmlReader cannot read it as
        // a document); a streamed one is read as a fragment, so what came first is written
        // already, and each element is indented. Recorded in StreamedResultFinalizer's remarks.
        var xslt = Stylesheet("""method="xml" indent="yes" omit-xml-declaration="yes" """,
            """<xsl:for-each select="item!copy-of()"><i><b><xsl:value-of select="@n"/></b></i></xsl:for-each>""");

        var (streamed, tree) = await BothAsync(xslt, Input);

        tree.Should().Be("<i><b>1</b></i><i><b>2</b></i><i><b>3</b></i>");
        streamed.Should().Be("<i>\n  <b>1</b>\n</i>\n<i>\n  <b>2</b>\n</i>\n<i>\n  <b>3</b>\n</i>");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(4096)]
    [InlineData(int.MaxValue)] // the whole result in one write, as the engine gives it from inside an open element (BUGS #140)
    public async Task ALargeStreamedResultIsSerializedWithBoundedMemory(int chunk)
    {
        // The finalizer holds a tag, a head, the text between two tags, and what it writes on to
        // the caller's writer in pieces of at least 8K characters: never the result. Fed the
        // result of 20 000 items a piece at a time, it writes what FinalizeOutput writes for the
        // whole, and never holds more than 32K characters, also when the result comes in one piece.
        var stylesheet = new StylesheetParser(new XQueryExpressionParser()).Parse(Stylesheet(
            """method="html" version="5.0" use-character-maps="m" """, "",
            """<xsl:character-map name="m"><xsl:output-character character="~" string="&amp;tilde;"/></xsl:character-map>"""));
        var engine = new XsltTransformEngine(stylesheet);
        var decl = stylesheet.PrincipalOutput;
        var markup = new StringBuilder("<html><head><title>t</title><script>a &lt; b</script></head><body>");
        for (var i = 0; i < (chunk == 1 ? 2_000 : 20_000); i++)
            markup.Append("<p class=\"c\">item ").Append(i).Append(" ~ &amp; café<br/><a href=\"x/é\">l</a></p>");
        markup.Append("</body></html>");
        var whole = markup.ToString();

        using var sink = new StringWriter();
        using var finalizer = new StreamedResultFinalizer(engine, decl, null, null, keepWhole: false, sink, default);
        for (var at = 0; at < whole.Length; at += chunk)
            await finalizer.WriteAsync(whole.AsMemory(at, Math.Min(chunk, whole.Length - at)));
        await finalizer.CompleteAsync(default);

        sink.ToString().Should().Be(engine.FinalizeOutput(whole, decl, null, XsltTransformEngine.FinalizeKind.StreamingBuffered));
        finalizer.MaxHeld.Should().BeLessThan(32_768, "the finalizer must not keep the result");
    }

    [Fact]
    public async Task ALargeIndentedStreamedResultIsSerializedWithBoundedMemory()
    {
        var stylesheet = new StylesheetParser(new XQueryExpressionParser()).Parse(Stylesheet("""method="xml" indent="yes" """, ""));
        var engine = new XsltTransformEngine(stylesheet);
        var decl = stylesheet.PrincipalOutput;
        var markup = new StringBuilder("<root>");
        for (var i = 0; i < 50_000; i++)
            markup.Append("<item n=\"").Append(i).Append("\"><name>item</name><e/></item>");
        markup.Append("</root>");
        var whole = markup.ToString();

        using var sink = new StringWriter();
        using var finalizer = new StreamedResultFinalizer(engine, decl, null, null, keepWhole: false, sink, default);
        for (var at = 0; at < whole.Length; at += 512)
            await finalizer.WriteAsync(whole.AsMemory(at, Math.Min(512, whole.Length - at)));
        await finalizer.CompleteAsync(default);

        sink.ToString().Should().Be(engine.FinalizeOutput(whole, decl, null, XsltTransformEngine.FinalizeKind.StreamingBuffered));
        finalizer.MaxHeld.Should().BeLessThan(16_384, "the finalizer must not keep the result");
    }

    private static async Task<(string Streamed, string Tree)> BothAsync(string xslt, string input)
    {
        var streaming = new XsltTransformer();
        await streaming.LoadStylesheetAsync(xslt);
        using var writer = new StringWriter();
        await streaming.TransformAsync(new MemoryStream(Encoding.UTF8.GetBytes(input)), writer);
        streaming.LastTransformStreamed.Should().BeTrue("the case must take the streamed path");

        var tree = new XsltTransformer { DisableStreaming = true };
        await tree.LoadStylesheetAsync(xslt);
        return (writer.ToString(), await tree.TransformAsync(input));
    }
}
