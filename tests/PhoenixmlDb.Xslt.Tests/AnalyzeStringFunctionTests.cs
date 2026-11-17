using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// fn:analyze-string in XSLT returned a System.Xml.Linq element, not an XDM node, so no axis
/// step could navigate it: analyze-string('a#b', '#')/* raised XPTY0019, while XQuery gave 3.
/// Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class AnalyzeStringFunctionTests
{
    private static async Task<string> RunAsync(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema"
                xmlns:fn="http://www.w3.org/2005/xpath-functions" xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="f:parts" as="xs:string*">
                <xsl:param name="s" as="xs:string"/>
                <xsl:sequence select="analyze-string($s, ',')/fn:non-match ! string()"/>
              </xsl:function>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync("<r/>")).Trim();
    }

    [Theory]
    [InlineData("""<xsl:value-of select="count(analyze-string('a#b', '#')/*)"/>""", "3")]
    [InlineData("""<xsl:value-of select="let $r := analyze-string('a#b', '#') return count($r/fn:match)"/>""", "1")]
    [InlineData("""<xsl:value-of select="f:parts('x,y,z')" separator="|"/>""", "x|y|z")]
    [InlineData("""<xsl:iterate select="analyze-string('a1b22', '[0-9]+')/*"><xsl:value-of select="local-name()"/>;</xsl:iterate>""", "non-match;match;non-match;match;")]
    [InlineData("""<xsl:value-of select="analyze-string('ab', '(a)(b)')//fn:group/@nr" separator=","/>""", "1,2")]
    [InlineData("""<xsl:value-of select="analyze-string('ab', 'b', 'i') instance of element(fn:analyze-string-result)"/>""", "true")]
    [InlineData("""<xsl:value-of select="empty(analyze-string('ab', 'b')/..)"/>""", "true")]
    public async Task The_result_is_an_XDM_element(string body, string expected)
        => (await RunAsync(body)).Should().Be(expected);

    /// <summary>
    /// XSLT's parse-xml-fragment wrapped a fragment with several top-level nodes in an element
    /// to parse it and left the wrapper in the tree: parse-xml-fragment("&lt;b/&gt;&lt;c/&gt;")/* was 1.
    /// </summary>
    [Theory]
    [InlineData("""<xsl:value-of select="count(parse-xml-fragment('&lt;b/&gt;&lt;c/&gt;')/*)"/>""", "2")]
    [InlineData("""<xsl:variable name="f" select="parse-xml-fragment('x&lt;b/&gt;y&lt;c/&gt;')"/><xsl:value-of select="count($f/node()), $f/*[1]/.. is $f, string($f), name($f/*[2])"/>""", "4 true xy c")]
    [InlineData("""<xsl:value-of select="count(parse-xml-fragment('&lt;b&gt;1&lt;/b&gt;')/*)"/>""", "1")]
    public async Task A_fragment_has_its_top_level_nodes_as_children(string body, string expected)
        => (await RunAsync(body)).Should().Be(expected);
}
