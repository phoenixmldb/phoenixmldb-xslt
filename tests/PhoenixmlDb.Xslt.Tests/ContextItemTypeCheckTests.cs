using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:context-item @as: a context item that does not match the required type is XTTE0590, whatever @use says.
/// use="optional" (the default) only permits an absent context item. The engine instead ran the template with an
/// absent focus when use was optional, so the body's "." raised XPDY0002 (W3C context-item-002, -004, -005, -013).
/// The matching-type, optional-absent and required-absent tests are guards.
/// </summary>
public sealed class ContextItemTypeCheckTests
{
    private static async Task<string> RunAsync(string templates, string? initialTemplate = null, string input = "<doc/>")
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              {{templates}}
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        if (initialTemplate != null)
            t.SetInitialTemplate(initialTemplate);
        return (await t.TransformAsync(initialTemplate != null ? null : input)).Trim();
    }

    private static async Task<string> ErrorOfAsync(string templates, string? initialTemplate = null, string input = "<doc/>")
    {
        var act = () => RunAsync(templates, initialTemplate, input);
        return (await act.Should().ThrowAsync<Exception>()).Which.Message;
    }

    [Fact]
    public async Task A_called_template_whose_context_item_has_the_wrong_type_is_XTTE0590()
        => (await ErrorOfAsync("""
                <xsl:template name="t">
                  <xsl:context-item as="xs:string"/>
                  <xsl:sequence select="."/>
                </xsl:template>
                <xsl:template match="/">
                  <out><xsl:for-each select="1 to 3"><xsl:call-template name="t"/></xsl:for-each></out>
                </xsl:template>
                """)).Should().Contain("XTTE0590");

    [Fact]
    public async Task A_matched_template_whose_context_item_has_the_wrong_type_is_XTTE0590()
        => (await ErrorOfAsync("""
                <xsl:template match="doc">
                  <xsl:context-item as="document-node()"/>
                  <xsl:copy-of select="."/>
                </xsl:template>
                <xsl:template match="/"><out><xsl:apply-templates select="doc"/></out></xsl:template>
                """)).Should().Contain("XTTE0590");

    [Fact]
    public async Task A_matching_context_item_type_runs()
        => (await RunAsync("""
                <xsl:template name="t">
                  <xsl:context-item as="xs:integer"/>
                  <xsl:value-of select=". * 2"/>
                </xsl:template>
                <xsl:template match="/">
                  <out><xsl:for-each select="1 to 3"><xsl:call-template name="t"/></xsl:for-each></out>
                </xsl:template>
                """)).Should().Be("<out>246</out>");

    [Fact]
    public async Task An_optional_context_item_may_be_absent()
        => (await RunAsync("""
                <xsl:template name="main">
                  <xsl:context-item as="node()" use="optional"/>
                  <out>ran</out>
                </xsl:template>
                """, initialTemplate: "main")).Should().Be("<out>ran</out>");

    [Fact]
    public async Task A_required_context_item_that_is_absent_is_XTTE3090()
        => (await ErrorOfAsync("""
                <xsl:template name="main">
                  <xsl:context-item as="node()" use="required"/>
                  <out>ran</out>
                </xsl:template>
                """, initialTemplate: "main")).Should().Contain("XTTE3090");
}
