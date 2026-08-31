using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A GLOBAL variable declared with a node type must hold that node, exactly as a local one does.
///
/// The global binding path decides whether to take the sequence-collection route from a list of
/// item types, and that list held only Element, Attribute and Node. For comment() and
/// processing-instruction() it was false, the route was skipped, and the body fell through to a
/// legacy path that wraps the result in a DOCUMENT:
///
///   &lt;xsl:variable as="comment()" name="c"&gt;&lt;xsl:comment&gt;ct&lt;/xsl:comment&gt;&lt;/xsl:variable&gt;
///   $c instance of comment()        ->  false
///   $c instance of document-node()  ->  true, and string($c) is "" — the content is gone
///
/// The equivalent list on the LOCAL variable path covers nine item types. The same declaration
/// inside a template was correct throughout, which is what made this findable: global was wrong
/// and local was right for identical source.
///
/// Only the two proven-broken kinds were added. text() and document-node() already behave
/// correctly through the legacy route, so widening the list to match local exactly would change
/// working behaviour on no evidence — the tests below pin both halves of that decision.
/// </summary>
public class GlobalTypedNodeVariableTests
{
    private static async Task<string> Run(string body)
    {
        var xslt = $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              {body}
            </xsl:stylesheet>
            """;
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xslt);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task GlobalCommentTypedVariable_IsAComment_AndKeepsItsContent()
    {
        var result = await Run("""
            <xsl:variable as="comment()" name="c"><xsl:comment>comment-text</xsl:comment></xsl:variable>
            <xsl:template name="main">
              <out isComment="{$c instance of comment()}" isDoc="{$c instance of document-node()}"
                   val="{string($c)}"/>
            </xsl:template>
            """);
        result.Should().Contain("isComment=\"true\"").And.Contain("isDoc=\"false\"");
        result.Should().Contain("val=\"comment-text\"", "the content was silently dropped when it was wrapped in a document");
    }

    [Fact]
    public async Task GlobalPiTypedVariable_IsAProcessingInstruction()
    {
        var result = await Run("""
            <xsl:variable as="processing-instruction()" name="p"><xsl:processing-instruction name="pt">pv</xsl:processing-instruction></xsl:variable>
            <xsl:template name="main">
              <out isPi="{$p instance of processing-instruction()}" isDoc="{$p instance of document-node()}"
                   name="{name($p)}" val="{string($p)}"/>
            </xsl:template>
            """);
        result.Should().Contain("isPi=\"true\"").And.Contain("isDoc=\"false\"");
        result.Should().Contain("name=\"pt\"").And.Contain("val=\"pv\"");
    }

    /// <summary>
    /// The comparison that located the defect: identical source, global wrong and local right.
    /// Keeping both in one test means a regression on either side is attributable.
    /// </summary>
    [Fact]
    public async Task GlobalAndLocalCommentVariablesAgree()
    {
        var result = await Run("""
            <xsl:variable as="comment()" name="gc"><xsl:comment>ct</xsl:comment></xsl:variable>
            <xsl:template name="main">
              <xsl:variable as="comment()" name="lc"><xsl:comment>ct</xsl:comment></xsl:variable>
              <out global="{$gc instance of comment()}" local="{$lc instance of comment()}"/>
            </xsl:template>
            """);
        result.Should().Contain("global=\"true\"").And.Contain("local=\"true\"");
    }

    /// <summary>
    /// The kinds that were already correct through the legacy route and must stay that way —
    /// they are the reason the list was widened by exactly two entries rather than to match the
    /// local path wholesale.
    /// </summary>
    [Theory]
    [InlineData("text()", "<xsl:text>t</xsl:text>", "text()")]
    [InlineData("document-node()", "<xsl:document><e/></xsl:document>", "document-node()")]
    [InlineData("element()", "<xsl:element name=\"e\"/>", "element()")]
    [InlineData("namespace-node()", "<xsl:namespace name=\"\">u</xsl:namespace>", "namespace-node()")]
    public async Task OtherGlobalNodeTypesAreUnaffected(string declared, string body, string test)
    {
        var result = await Run("""
            <xsl:variable as="DECLARED" name="v">BODY</xsl:variable>
            <xsl:template name="main"><out ok="{$v instance of TEST}"/></xsl:template>
            """.Replace("DECLARED", declared, StringComparison.Ordinal)
               .Replace("BODY", body, StringComparison.Ordinal)
               .Replace("TEST", test, StringComparison.Ordinal));
        result.Should().Contain("ok=\"true\"");
    }
}
