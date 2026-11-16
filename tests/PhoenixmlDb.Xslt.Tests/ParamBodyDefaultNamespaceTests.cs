using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A literal result element in the body of xsl:param or xsl:with-param is in the default
/// namespace in scope where it is written. The body was run under the caller's output namespace
/// scopes, so an element in the default namespace already declared by an enclosing result element
/// wrote no declaration of its own, and the markup read back put it in no namespace:
/// <c>&lt;abbr xmlns=""&gt;</c> (DocBook's inline.charseq and biblioentry.label; W3C docbook-001).
/// </summary>
public sealed class ParamBodyDefaultNamespaceTests
{
    private static async Task<string> Run(string templates)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns="urn:d">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/"><html><xsl:apply-templates select="r"/></html></xsl:template>
              {{templates}}
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync("<r/>")).Trim();
    }

    [Theory]
    [InlineData("<xsl:template match='r'><xsl:param name='p'><e/></xsl:param><p><xsl:copy-of select='$p'/></p></xsl:template>")]
    [InlineData("<xsl:template match='r'><xsl:call-template name='t'><xsl:with-param name='p'><e/></xsl:with-param></xsl:call-template></xsl:template>"
        + "<xsl:template name='t'><xsl:param name='p'/><p><xsl:copy-of select='$p'/></p></xsl:template>")]
    [InlineData("<xsl:template match='r'><xsl:call-template name='t'/></xsl:template>"
        + "<xsl:template name='t'><xsl:param name='p'><e/></xsl:param><p><xsl:copy-of select='$p'/></p></xsl:template>")]
    [InlineData("<xsl:template match='r'><xsl:apply-templates select='.' mode='m'><xsl:with-param name='p'><e/></xsl:with-param></xsl:apply-templates></xsl:template>"
        + "<xsl:template match='r' mode='m'><xsl:param name='p'/><p><xsl:copy-of select='$p'/></p></xsl:template>")]
    [InlineData("<xsl:template match='r'><xsl:call-template name='t'><xsl:with-param name='p' as='element()*'><e/></xsl:with-param></xsl:call-template></xsl:template>"
        + "<xsl:template name='t'><xsl:param name='p'/><p><xsl:copy-of select='$p'/></p></xsl:template>")]
    public async Task The_element_keeps_the_default_namespace(string templates)
        => (await Run(templates)).Should().Be("<html xmlns=\"urn:d\"><p><e/></p></html>");

    [Fact]
    public async Task Its_namespace_uri_is_the_default_namespace()
        => (await Run("<xsl:template match='r'><xsl:param name='p'><e/></xsl:param><p><xsl:value-of select='namespace-uri($p/*)'/></p></xsl:template>"))
            .Should().Be("<html xmlns=\"urn:d\"><p>urn:d</p></html>");
}
