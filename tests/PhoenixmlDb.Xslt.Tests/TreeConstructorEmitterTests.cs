using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// SP-B temp-tree node-model migration: pins for the from-scratch emitters that build
/// XDM nodes directly via <c>TreeConstructor</c> (behind the <c>PXDB_TEMPTREE_DIFF</c>
/// differential in slice 1). Each test asserts the observable temp-tree behavior through
/// the public transform surface, so it holds whether the value came from the legacy
/// serialize-reparse path (production, toggle off) or the node-build path.
/// </summary>
public class TreeConstructorEmitterTests
{
    private static async Task<string> TransformAsync(string stylesheet, string input)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(input);
    }

    [Fact]
    public async Task XslElement_SimpleContent_BuildsElementNodeWithAttributeAndText()
    {
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/">
                <xsl:variable name="v" as="element()">
                  <xsl:element name="a"><xsl:attribute name="k">1</xsl:attribute>hi</xsl:element>
                </xsl:variable>
                <out>{name($v)}|{$v/@k}|{string($v)}</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, "<r/>");
        result.Should().Contain("<out>a|1|hi</out>");
    }

    [Fact]
    public async Task Lre_NestedWithNamespace_BuildsNodeTree()
    {
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/">
                <xsl:variable name="v" as="element()">
                  <a xmlns:p="urn:p"><p:b>x</p:b></a>
                </xsl:variable>
                <out>{$v/*/local-name()}|{namespace-uri($v/*)}</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, "<r/>");
        result.Should().Contain("<out>b|urn:p</out>");
    }
}
