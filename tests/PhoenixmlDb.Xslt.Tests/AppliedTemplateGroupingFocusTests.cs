using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §14.2.1/§14.2.2: an invocation construct (apply-templates, call-template, ...)
/// leaves the current group and grouping key unchanged, as in XSLT 2.0, unless it is within a
/// declared-streamable construct, where it sets both to absent in the called template.
/// XSpec's compiler relies on the first (threads.xsl: for-each-group, apply-templates,
/// current-group() in the matched rule); W3C si-fork-115 checks the second.
/// </summary>
public class AppliedTemplateGroupingFocusTests
{
    private static async Task<string> Run(bool streamable, string source = "<r><i k='a'/><i k='a'/><i k='b'/></r>")
    {
        var xsl = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode name="m" streamable="{(streamable ? "yes" : "no")}"/>
              <xsl:mode streamable="{(streamable ? "yes" : "no")}"/>
              <xsl:template match="r">
                <out><xsl:for-each-group select="i" group-by="@k"><xsl:apply-templates select="." mode="m"/></xsl:for-each-group></out>
              </xsl:template>
              <xsl:template match="i" mode="m">
                <xsl:try>
                  <g key="{"{"}current-grouping-key(){"}"}" n="{"{"}count(current-group()){"}"}"/>
                  <xsl:catch errors="*:XTDE1071 *:XTDE1061"><g absent="yes"/></xsl:catch>
                </xsl:try>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        return await t.TransformAsync(source);
    }

    [Fact]
    public async Task Outside_streaming_an_applied_template_keeps_the_group_and_key() =>
        (await Run(streamable: false)).Should().Contain("<g key=\"a\" n=\"2\"/><g key=\"b\" n=\"1\"/>");

    // The streamable half (both absent) is W3C si-fork-115 end to end: a streamable-mode template
    // that reads current-group() is already rejected statically (XTSE3430), so it cannot be
    // exercised here.
}
