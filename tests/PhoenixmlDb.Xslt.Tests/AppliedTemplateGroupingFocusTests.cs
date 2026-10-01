using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §14.2: a template rule invoked by xsl:apply-templates has no current group and no
/// current grouping key, so current-grouping-key() raises XTDE1071 there. The caller's key
/// leaked in (W3C si-fork-115). A named template keeps them.
/// </summary>
public class AppliedTemplateGroupingFocusTests
{
    private static async Task<string> Run(string inner)
    {
        var xsl = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <out><xsl:for-each-group select="r/i" group-by="@k">{inner}</xsl:for-each-group></out>
              </xsl:template>
              <xsl:template match="i" mode="m">
                <xsl:try><h key="{"{"}current-grouping-key(){"}"}"/><xsl:catch errors="*:XTDE1071"><h key="absent"/></xsl:catch></xsl:try>
              </xsl:template>
              <xsl:template name="n">
                <h key="{"{"}current-grouping-key(){"}"}"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        return await t.TransformAsync("<r><i k='a'/><i k='b'/></r>");
    }

    [Fact]
    public async Task An_applied_template_has_no_grouping_key() =>
        (await Run("<xsl:apply-templates select=\"current-group()\" mode=\"m\"/>"))
            .Should().Contain("<h key=\"absent\"/><h key=\"absent\"/>");

    [Fact]
    public async Task A_called_template_keeps_the_grouping_key() =>
        (await Run("<xsl:call-template name=\"n\"/>"))
            .Should().Contain("<h key=\"a\"/><h key=\"b\"/>");
}
