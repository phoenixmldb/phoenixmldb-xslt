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

    [Fact]
    public async Task XslCopy_ElementWithCopiedAttributes_BuildsNodeTree()
    {
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/">
                <xsl:variable name="v" as="element()">
                  <xsl:for-each select="a"><xsl:copy><xsl:copy-of select="@*"/></xsl:copy></xsl:for-each>
                </xsl:variable>
                <out>{name($v)}|{$v/@k}</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, """<a k="7"/>""");
        result.Should().Contain("<out>a|7</out>");
    }

    [Fact]
    public async Task NestedApplyTemplatesOverAtomics_BuildsChildElementsNatively()
    {
        // A temp tree whose nested elements come from apply-templates over an atomic sequence
        // (text-value-templates supplying the leaf text) must build the full element tree in
        // document order: csv → 2 rows → 2 fields each, each field carrying its own text.
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" expand-text="yes">
              <xsl:mode name="pf" on-no-match="shallow-copy"/>
              <xsl:template match="/">
                <xsl:variable name="result" as="element()">
                  <csv>
                    <xsl:apply-templates select="tokenize('a,b|c,d','\|')" mode="pl"/>
                  </csv>
                </xsl:variable>
                <out>{count($result/row)}|{count($result/row[1]/field)}|{string($result/row[2]/field[2])}</out>
              </xsl:template>
              <xsl:template match="." mode="pl">
                <row><xsl:apply-templates select="tokenize(.,',')" mode="pf"/></row>
              </xsl:template>
              <xsl:template match="." mode="pf">
                <field>{.}</field>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, "<r/>");
        result.Should().Contain(">2|2|d</out>");
    }

    [Fact]
    public async Task MixedContent_TextElementCommentPi_PreservesDocumentOrder()
    {
        // slice 4: text interleaved with a child element, then a comment and a PI, must land as
        // five child nodes in document order (t1, b, t2, comment, pi) — the same whether the
        // value came from the reparse path (production) or the native node-build (differential).
        const string xslt = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" expand-text="yes">
              <xsl:template match="/"><xsl:variable name="v" as="element()">
                <a>t1<b/>t2<xsl:comment>c</xsl:comment><xsl:processing-instruction name="pi">d</xsl:processing-instruction></a>
              </xsl:variable><out>{count($v/node())}</out></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await TransformAsync(xslt, "<r/>");
        result.Should().Contain("<out>5</out>"); // t1, b, t2, comment, pi
    }
}
