using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The text of an element named by <c>cdata-section-elements</c> is written as CDATA sections
/// (Serialization 3.1 §5.1.4), whatever instruction writes it, and one text node is one section.
/// Text from xsl:text was written as plain text, and two text writes in one element gave two
/// sections, or a section and plain text. Expected results: Saxon-HE 10.9 for the same stylesheet.
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
    public async Task The_text_of_a_CDATA_section_element_is_one_CDATA_section_per_text_node(string body, string expected)
        => (await Run(body)).Should().Be($"<r>{expected}</r>");
}
