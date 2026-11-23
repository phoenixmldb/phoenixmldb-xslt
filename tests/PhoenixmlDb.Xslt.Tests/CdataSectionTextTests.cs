using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The text of an element named by <c>cdata-section-elements</c> is written as CDATA sections
/// (Serialization 3.1 §5.1.4), whatever instruction writes it, and one text node is one section.
/// Text from xsl:text was written as plain text, and two text writes in one element gave two
/// sections, or a section and plain text; text from xsl:number and the text of a copied element
/// were not sections either, and xsl:number did not escape its format token. Expected results:
/// Saxon-HE 10.9 for the same stylesheets.
/// </summary>
public class CdataSectionTextTests
{
    private static async Task<string> Run(string body)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes" cdata-section-elements="a"/>
              <xsl:template name="xsl:initial-template"><r>{body}</r></xsl:template>
            </xsl:stylesheet>
            """);
        transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return await transformer.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("<a><xsl:text>t</xsl:text></a>", "<a><![CDATA[t]]></a>")]
    [InlineData("<a>lit<xsl:text>t</xsl:text></a>", "<a><![CDATA[litt]]></a>")]
    [InlineData("<a><xsl:value-of select=\"'v'\"/><xsl:text>t</xsl:text></a>", "<a><![CDATA[vt]]></a>")]
    [InlineData("<a>x<b/>y</a>", "<a><![CDATA[x]]><b/><![CDATA[y]]></a>")]
    [InlineData("<a><xsl:text>p]]&gt;q</xsl:text></a>", "<a><![CDATA[p]]]]><![CDATA[>q]]></a>")]
    [InlineData("<a><xsl:text>p]]</xsl:text><xsl:text>&gt;q</xsl:text></a>", "<a><![CDATA[p]]]]><![CDATA[>q]]></a>")]
    [InlineData("<a><xsl:value-of select=\"''\"/></a>", "<a/>")]
    [InlineData("<b><xsl:text>not cdata</xsl:text></b>", "<b>not cdata</b>")]
    [InlineData("<a><xsl:number value=\"7\"/></a>", "<a><![CDATA[7]]></a>")]
    [InlineData("<a><xsl:value-of select=\"'v'\"/><xsl:number value=\"3\"/></a>", "<a><![CDATA[v3]]></a>")]
    [InlineData("<b><xsl:number value=\"3\" format=\"&lt;1&amp;\"/></b>", "<b>&lt;3&amp;</b>")]
    public async Task The_text_of_a_CDATA_section_element_is_one_CDATA_section_per_text_node(string body, string expected)
        => (await Run(body)).Should().Be($"<r>{expected}</r>");

    private const string Source = """<doc xmlns:x="urn:p"><a>src-a</a><x:a>ns-a</x:a><d><a>deep</a></d></doc>""";

    /// <summary>
    /// An element that is copied, not constructed: by xsl:copy-of (from the source, or from a
    /// temporary tree) and by xsl:copy. Its text was written without a CDATA section. The source
    /// binds the namespace to another prefix than cdata-section-elements does: names match by URI.
    /// </summary>
    [Theory]
    [InlineData("<xsl:copy-of select=\"doc/a\"/>", "<a xmlns:x=\"urn:p\"><![CDATA[src-a]]></a>")]
    [InlineData("<xsl:copy-of select=\"doc/p:a\"/>", "<x:a xmlns:x=\"urn:p\"><![CDATA[ns-a]]></x:a>")]
    [InlineData("<xsl:copy-of select=\"doc/d\"/>", "<d xmlns:x=\"urn:p\"><a><![CDATA[deep]]></a></d>")]
    [InlineData("<xsl:copy-of select=\"$tree\"/>", "<a><![CDATA[tree]]></a>")]
    [InlineData("<xsl:apply-templates select=\"doc/p:a\" mode=\"copy\"/>", "<x:a xmlns:x=\"urn:p\"><![CDATA[ns-a]]></x:a>")]
    [InlineData("<xsl:for-each select=\"doc/a\"><xsl:copy><xsl:value-of select=\".\"/><xsl:number value=\"2\"/></xsl:copy></xsl:for-each>",
        "<a xmlns:x=\"urn:p\"><![CDATA[src-a2]]></a>")]
    public async Task The_text_of_a_copied_CDATA_section_element_is_a_CDATA_section(string body, string expected)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p" exclude-result-prefixes="p">
              <xsl:output method="xml" omit-xml-declaration="yes" cdata-section-elements="a p:a"/>
              <xsl:variable name="tree"><a>tree</a></xsl:variable>
              <xsl:template match="/"><r>{body}</r></xsl:template>
              <xsl:template match="node()|@*" mode="copy"><xsl:copy><xsl:apply-templates select="node()|@*" mode="copy"/></xsl:copy></xsl:template>
            </xsl:stylesheet>
            """);
        (await transformer.TransformAsync(Source)).Should().Be($"<r>{expected}</r>");
    }

    private const string StreamedSource = """<doc><a>src-a</a><t>src-t</t><x:a xmlns:x="urn:p">ns-a</x:a><d><a>deep</a></d><b>x</b></doc>""";

    private const string WholeDoc = """<doc><a><![CDATA[src-a]]></a><t>src-t</t><x:a xmlns:x="urn:p"><![CDATA[ns-a]]></x:a><d><a><![CDATA[deep]]></a></d><b>x</b></doc>""";

    /// <summary>Mode attributes, templates, and the expected result (Saxon-HE 10.9, which runs them without streaming).
    /// The namespace is declared where it is used: a copy that repeats an inherited declaration is another matter.</summary>
    public static TheoryData<string, string, string, string> StreamedCases => new()
    {
        { "copy-of a streamed element", "", """<xsl:template match="doc"><r><xsl:copy-of select="a"/></r></xsl:template>""",
          """<r><a><![CDATA[src-a]]></a></r>""" },
        { "copy-of a streamed subtree", "", """<xsl:template match="doc"><r><xsl:copy-of select="d"/></r></xsl:template>""",
          """<r><d><a><![CDATA[deep]]></a></d></r>""" },
        { "copy-of a namespaced element", "", """<xsl:template match="doc"><r><xsl:copy-of select="p:a"/></r></xsl:template>""",
          """<r><x:a xmlns:x="urn:p"><![CDATA[ns-a]]></x:a></r>""" },
        { "shallow-copy (on-no-match)", """on-no-match="shallow-copy" """, "", WholeDoc },
        { "deep-copy (on-no-match)", """on-no-match="deep-copy" """, "", WholeDoc },
        { "xsl:copy, then the built-in text rule", """on-no-match="shallow-copy" """,
          """<xsl:template match="a"><xsl:copy><xsl:apply-templates/></xsl:copy></xsl:template>""", WholeDoc },
        { "xsl:copy in for-each, value-of", "",
          """<xsl:template match="doc"><r><xsl:for-each select="a"><xsl:copy><xsl:value-of select="."/></xsl:copy></xsl:for-each></r></xsl:template>""",
          """<r><a><![CDATA[src-a]]></a></r>""" },
        { "xsl:sequence of a streamed element", "", """<xsl:template match="doc"><r><xsl:sequence select="a"/></r></xsl:template>""",
          """<r><a><![CDATA[src-a]]></a></r>""" },
        { "the built-in text rule in a literal element", "",
          """<xsl:template match="doc"><r><a><xsl:apply-templates select="t/text()"/></a></r></xsl:template>""",
          """<r><a><![CDATA[src-t]]></a></r>""" },
        { "value-of a streamed element", "", """<xsl:template match="doc"><r><a><xsl:value-of select="t"/></a></r></xsl:template>""",
          """<r><a><![CDATA[src-t]]></a></r>""" },
        { "copy-of() then copy-of", "",
          """<xsl:template match="doc"><r><xsl:for-each select="a!copy-of()"><xsl:copy-of select="."/></xsl:for-each></r></xsl:template>""",
          """<r><a><![CDATA[src-a]]></a></r>""" },
    };

    /// <summary>
    /// The same when the transformation streams: the result as a string, and the result written to
    /// the caller's writer as it is produced. With streaming off, the same stylesheet is the control.
    /// </summary>
    [Theory]
    [MemberData(nameof(StreamedCases))]
    public async Task The_text_of_a_CDATA_section_element_is_a_CDATA_section_when_the_transformation_streams(
        string label, string mode, string templates, string expected)
    {
        _ = label;
        var xslt = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p" exclude-result-prefixes="#all">
              <xsl:output method="xml" omit-xml-declaration="yes" cdata-section-elements="a p:a"/>
              <xsl:mode streamable="yes" {mode}/>
              {templates}
            </xsl:stylesheet>
            """;

        var toString = new XsltTransformer();
        await toString.LoadStylesheetAsync(xslt);
        var asString = await toString.TransformAsync(StreamedSource);
        toString.LastTransformStreamed.Should().BeTrue("the case must stream");

        var toWriter = new XsltTransformer();
        await toWriter.LoadStylesheetAsync(xslt);
        using var writer = new StringWriter();
        await toWriter.TransformAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(StreamedSource)), writer);
        toWriter.LastTransformStreamed.Should().BeTrue("the case must stream");

        var tree = new XsltTransformer { DisableStreaming = true };
        await tree.LoadStylesheetAsync(xslt);
        var asTree = await tree.TransformAsync(StreamedSource);

        (asString, writer.ToString(), asTree).Should().Be((expected, expected, expected));
    }
}
