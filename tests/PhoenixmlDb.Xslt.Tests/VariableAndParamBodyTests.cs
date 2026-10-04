using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The content of xsl:variable and xsl:param is a sequence constructor like any other: an
/// as= type governs a template parameter's default as it does a variable's, whitespace-only
/// text is stripped (§4.3), and expand-text applies.
/// </summary>
/// <remarks>
/// A template parameter's default was evaluated as an untyped body, so as="element()" over an
/// element constructor gave a document node (W3C as-1212). Text-only content bypassed the
/// sequence-constructor parser and became a single literal string: whitespace-only content was
/// kept (as-0129: as="document-node()?" over " " held a text node instead of the empty sequence),
/// and text value templates were not expanded (&lt;xsl:variable&gt;{1+1}&lt;/xsl:variable&gt; was
/// "{1+1}"; Saxon gives "2").
/// </remarks>
public sealed class VariableAndParamBodyTests
{
    private static async Task<string> RunAsync(string declarations, string body, string rootAttributes = "")
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all" {rootAttributes}>
              <xsl:output method="text"/>
              {declarations}
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync("<doc/>");
    }

    [Theory]
    [InlineData("""<xsl:param name="p" as="element()"><e>1</e></xsl:param>""")]
    [InlineData("""<xsl:param name="p" as="element()"><xsl:element name="e">1</xsl:element></xsl:param>""")]
    [InlineData("""<xsl:param name="p" as="element()+"><e>1</e><e>2</e></xsl:param>""")]
    public async Task TemplateParamDefault_HasItsDeclaredType(string param)
        => (await RunAsync(
                $"""<xsl:template name="t">{param}<xsl:value-of select="$p instance of element()+, local-name($p[1])"/></xsl:template>""",
                """<xsl:call-template name="t"/>"""))
            .Should().Be("true e");

    [Fact]
    public async Task TemplateParamDefault_OfTheWrongType_IsXTTE0600()
    {
        var act = () => RunAsync(
            """<xsl:template name="t"><xsl:param name="p" as="element()"><xsl:comment>c</xsl:comment></xsl:param><xsl:value-of select="$p"/></xsl:template>""",
            """<xsl:call-template name="t"/>""");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTTE0600");
    }

    [Theory]
    [InlineData("document-node()?")]
    [InlineData("element()?")]
    [InlineData("item()*")]
    public async Task WhitespaceOnlyBody_IsStripped(string type)
        => (await RunAsync($"""<xsl:variable name="v" as="{type}"> </xsl:variable>""",
            """<xsl:value-of select="count($v)"/>""")).Should().Be("0");

    [Fact]
    public async Task WhitespaceOnlyBody_WithoutAs_IsAZeroLengthValue()
        => (await RunAsync("""<xsl:variable name="v">  </xsl:variable>""",
            """<xsl:value-of select="string-length($v), string($v) = ''"/>""")).Should().Be("0 true");

    [Fact]
    public async Task WhitespaceOnlyBody_UnderXmlSpacePreserve_IsKept()
        => (await RunAsync("""<xsl:variable name="v" xml:space="preserve">  </xsl:variable>""",
            """<xsl:value-of select="string-length($v)"/>""")).Should().Be("2");

    /// <summary>A no-break space is not XML whitespace and is never stripped.</summary>
    [Fact]
    public async Task NoBreakSpaceBody_IsKept()
        => (await RunAsync("""<xsl:variable name="v">&#160;</xsl:variable>""",
            """<xsl:value-of select="string-to-codepoints($v)"/>""")).Should().Be("160");

    [Fact]
    public async Task TextValueTemplate_InATextOnlyBody_IsExpanded()
        => (await RunAsync(
            """<xsl:variable name="g">{1+1}</xsl:variable><xsl:param name="p" as="xs:string">{2+3}</xsl:param>""",
            """<xsl:variable name="l">x{4+4}y</xsl:variable><xsl:value-of select="$g, $p, $l"/>""",
            rootAttributes: """expand-text="yes" """)).Should().Be("2 5 x8y");

    [Fact]
    public async Task TextOnlyBody_WithoutExpandText_KeepsItsBraces()
        => (await RunAsync("""<xsl:variable name="g">{1+1}</xsl:variable>""",
            """<xsl:value-of select="$g"/>""")).Should().Be("{1+1}");
}
