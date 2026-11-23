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
}
