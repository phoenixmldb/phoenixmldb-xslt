using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// In a function body, the built-in shallow-copy and deep-copy rules copy a document node as a
/// document node. They built one only in a typed template or variable body; in a function body they
/// returned the document's children, so f() as="document-node()" was XTTE0780 and an untyped f()
/// returned the element (W3C merge-096).
/// </summary>
public sealed class FunctionBuiltInDocumentCopyTests
{
    private static async Task<string> RunAsync(string onNoMatch, string asAttribute)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:mode name="c" on-no-match="{onNoMatch}"/>
              <xsl:function name="f:copy" {asAttribute}><xsl:param name="n"/><xsl:apply-templates select="$n" mode="c"/></xsl:function>
              <xsl:template match="/">
                <xsl:variable name="r" select="f:copy(.)"/>
                <xsl:value-of select="count($r), $r ! (if (. instance of document-node()) then 'doc' else name()), count($r/a/b), $r is ."/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<a><b/></a>")).Trim();
    }

    [Theory]
    [InlineData("shallow-copy", """as="document-node()" """)]
    [InlineData("shallow-copy", "")]
    [InlineData("deep-copy", """as="document-node()" """)]
    [InlineData("deep-copy", "")]
    public async Task ABuiltInCopyOfADocument_InAFunction_IsANewDocumentNode(string onNoMatch, string asAttribute)
        => (await RunAsync(onNoMatch, asAttribute)).Should().Be("1 doc 1 false");
}
