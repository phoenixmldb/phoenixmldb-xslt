using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xslt#627, BUGS.md #150: a document read from outside the transformation, other than the
/// principal source, kept a newline or tab in an attribute value, and the CR of a CRLF in text.
/// XML 1.0 makes each of them a space in an attribute value (§3.3.3) and a CRLF a newline
/// (§2.11); a character reference (<c>&amp;#10;</c>) stays what it is. The principal source was
/// read so; fn:doc, document(), fn:parse-xml, fn:parse-xml-fragment and xsl:source-document were
/// not. Expected results: Saxon-HE 10.9's for the same stylesheets.
/// </summary>
public sealed class ExternalDocumentNormalizationTests : IDisposable
{
    // An attribute with a newline and a tab, an attribute with a character reference, a CRLF in text.
    private const string Document = "<other b=\"p\n\tq\" c=\"r&#10;s\"><t>x\r\ny</t></other>";

    // The code points of @b, @c and t.
    private const string Normalized = "112 32 32 113 | 114 10 115 | 120 10 121";

    private readonly string _file = Path.Combine(Path.GetTempPath(), "phx-627-" + Guid.NewGuid().ToString("N") + ".xml");

    public ExternalDocumentNormalizationTests() => File.WriteAllText(_file, Document);

    public void Dispose()
    {
        try { File.Delete(_file); } catch (IOException) { }
    }

    private const string CodePoints =
        "string-join(($o/@b, $o/@c, $o/t) ! string-join(string-to-codepoints(.) ! string(.), ' '), ' | ')";

    [Theory]
    [InlineData("{$f(doc($u)/other)}")]
    [InlineData("{$f(document($u)/other)}")]
    [InlineData("{$f(parse-xml(unparsed-text($u))/other)}")]
    [InlineData("{$f(parse-xml-fragment(unparsed-text($u))/other)}")]
    [InlineData("""<xsl:source-document href="{$u}" streamable="no">{$f(/other)}</xsl:source-document>""")]
    public async Task A_document_from_outside_is_read_as_XML_1_0_says(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(
            """<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"><xsl:output method="text"/>"""
            + $"<xsl:param name=\"u\" select=\"'{new Uri(_file).AbsoluteUri}'\"/>"
            + """<xsl:template match="/" expand-text="yes"><xsl:variable name="f" select="function($o) { """ + CodePoints + """ }"/>"""
            + body + "</xsl:template></xsl:stylesheet>");

        (await t.TransformAsync("<x/>")).Should().Be(Normalized);
    }

    /// <summary>The principal source, as before.</summary>
    [Fact]
    public async Task The_principal_source_is_read_so_too()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(
            """<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"><xsl:output method="text"/>"""
            + """<xsl:template match="/" expand-text="yes"><xsl:variable name="f" select="function($o) { """ + CodePoints + """ }"/>{$f(/other)}</xsl:template></xsl:stylesheet>""");

        (await t.TransformAsync(Document)).Should().Be(Normalized);
    }

    /// <summary>A global context document given as text.</summary>
    [Fact]
    public async Task A_global_context_document_given_as_text_is_read_so_too()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(
            """<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"><xsl:output method="text"/>"""
            + """<xsl:global-context-item use="required"/>"""
            + """<xsl:variable name="f" select="function($o) { """ + CodePoints + """ }"/><xsl:param name="g" select="$f(/other)"/>"""
            + """<xsl:template match="/" expand-text="yes">{$g}</xsl:template></xsl:stylesheet>""");
        t.SetGlobalContextItem(Document);

        (await t.TransformAsync("<x/>")).Should().Be(Normalized);
    }

    /// <summary>
    /// A node this processor serialized itself and reads back (fn:transform's source-node) keeps
    /// what it has: its newline and tab were written as character references.
    /// </summary>
    [Fact]
    public async Task A_node_given_to_fn_transform_keeps_a_newline_in_an_attribute()
    {
        const string inner = "&lt;xsl:stylesheet version=&quot;3.0&quot; xmlns:xsl=&quot;http://www.w3.org/1999/XSL/Transform&quot;&gt;"
            + "&lt;xsl:output method=&quot;text&quot;/&gt;&lt;xsl:template match=&quot;/&quot;&gt;"
            + "&lt;xsl:value-of select=&quot;string-to-codepoints(/r/@a)&quot;/&gt;&lt;/xsl:template&gt;&lt;/xsl:stylesheet&gt;";
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(
            """<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"><xsl:output method="text"/>"""
            + $"<xsl:variable name=\"inner\" select=\"'{inner}'\"/>"
            + """<xsl:template match="/"><xsl:variable name="src"><r a="{codepoints-to-string((120,10,9,121))}"/></xsl:variable>"""
            + """<xsl:value-of select="transform(map{'stylesheet-text': $inner, 'source-node': $src, 'delivery-format':'raw'})?output"/></xsl:template></xsl:stylesheet>""");

        (await t.TransformAsync("<x/>")).Should().Be("120 10 9 121");
    }
}
