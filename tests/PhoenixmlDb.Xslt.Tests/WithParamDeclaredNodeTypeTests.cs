using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xsl:with-param</c> with a body and a declared type of one node (<c>as="element()"</c>)
/// passes the node the body constructs, as <c>xsl:variable</c> does. It passed a temporary
/// tree, so the receiver had a document node (xslt#358).
/// </summary>
public class WithParamDeclaredNodeTypeTests
{
    private static async Task<string> RunAsync(string withParam, string use, string call = "call-template name=\"t\"")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            version="3.0" expand-text="yes" exclude-result-prefixes="#all">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template name="t" match="in" mode="m"><xsl:param name="p"/><r>{use}</r></xsl:template>
              <xsl:template match="/"><xsl:{call}>{withParam}</xsl:{call.Split(' ')[0]}></xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync("<in/>");
    }

    private const string Kind = "{name($p)}|{$p instance of element()}|{$p instance of document-node()}|{empty($p/..)}";

    [Theory]
    [InlineData("as=\"element()\"")]
    [InlineData("as=\"element()?\"")]
    [InlineData("as=\"element(e)\"")]
    [InlineData("as=\"node()\"")]
    [InlineData("as=\"element()*\"")]
    public async Task A_body_with_a_declared_node_type_passes_the_node(string asAttribute)
        => (await RunAsync($"<xsl:with-param name=\"p\" {asAttribute}><e a=\"1\">x</e></xsl:with-param>", Kind))
            .Should().Be("<r>e|true|false|true</r>");

    [Fact]
    public async Task It_does_so_for_apply_templates_too()
        => (await RunAsync("<xsl:with-param name=\"p\" as=\"element()\"><e/></xsl:with-param>", Kind, "apply-templates select=\"in\" mode=\"m\""))
            .Should().Be("<r>e|true|false|true</r>");

    [Fact]
    public async Task With_no_declared_type_it_is_a_temporary_tree_as_before()
        => (await RunAsync("<xsl:with-param name=\"p\"><e/></xsl:with-param>", "{name($p)}|{$p instance of document-node()}|{name($p/*)}"))
            .Should().Be("<r>|true|e</r>");

    [Theory]
    [InlineData("<xsl:with-param name=\"p\" as=\"text()\">abc</xsl:with-param>", "{$p instance of text()}|{$p}", "true|abc")]
    [InlineData("<xsl:with-param name=\"p\" as=\"attribute()\"><xsl:attribute name=\"a\">1</xsl:attribute></xsl:with-param>", "{$p instance of attribute()}|{name($p)}|{$p}", "true|a|1")]
    [InlineData("<xsl:with-param name=\"p\" as=\"element()?\"/>", "{empty($p)}", "true")]
    [InlineData("<xsl:with-param name=\"p\" as=\"xs:integer\">42</xsl:with-param>", "{$p instance of xs:integer}|{$p + 1}", "true|43")]
    [InlineData("<xsl:with-param name=\"p\" as=\"element()\"><e><f/></e></xsl:with-param>", "<xsl:copy-of select=\"$p\"/>", "<e><f/></e>")]
    public async Task Other_declared_types_give_their_value(string withParam, string use, string expected)
        => (await RunAsync(withParam, use)).Should().Be($"<r>{expected}</r>");
}
