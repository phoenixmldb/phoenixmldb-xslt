using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §18.2: <c>current-group()</c> / <c>current-grouping-key()</c> are part of the focus
/// established by <c>xsl:for-each-group</c>. A template invoked with <c>xsl:context-item
/// use="absent"</c> severs the focus, so inside it the current group and current grouping key are
/// ABSENT — the functions must raise XTDE1061 / XTDE1071 rather than leak the caller's group. A
/// normal invocation (no context-item declaration) retains the focus and keeps the group visible.
///
/// Regression coverage for si-fork-113/114 (streaming), which assert <c>@key/@size =
/// "#absent#"</c> in a context-absent template CALLED from within <c>xsl:for-each-group</c>.
/// (The applied-template variant si-fork-115 — where apply-templates itself resets the group —
/// is a separate, broader change and remains a known failure.)
/// </summary>
public class CurrentGroupContextAbsentTests
{
    private static async Task<string> Transform(string stylesheet, string input = "<in/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(input);
    }

    [Fact]
    public async Task ContextAbsentCallee_CurrentGroupingKey_IsAbsent_WhileNormalCalleeRetainsIt()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
                <out><xsl:for-each-group select="(1,2,3)" group-by=".">
                  <a>[<xsl:call-template name="absent"/>]</a>
                  <b>[<xsl:call-template name="normal"/>]</b>
                </xsl:for-each-group></out>
              </xsl:template>
              <xsl:template name="absent">
                <xsl:context-item use="absent"/>
                <xsl:value-of select="try { string(current-grouping-key()) } catch * { '#absent#' }"/>
              </xsl:template>
              <xsl:template name="normal">
                <xsl:value-of select="try { string(current-grouping-key()) } catch * { '#absent#' }"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // Context-absent callee → XTDE1071 → #absent#; normal callee retains the propagated key.
        (await Transform(ss)).Should().Be("<out><a>[#absent#]</a><b>[1]</b><a>[#absent#]</a><b>[2]</b><a>[#absent#]</a><b>[3]</b></out>");
    }

    [Fact]
    public async Task ContextAbsentCallee_CurrentGroup_IsAbsent()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
                <out><xsl:for-each-group select="(1,2,3)" group-by=".">
                  <a>[<xsl:call-template name="absent"/>]</a>
                </xsl:for-each-group></out>
              </xsl:template>
              <xsl:template name="absent">
                <xsl:context-item use="absent"/>
                <xsl:value-of select="try { string(count(current-group())) } catch * { '#absent#' }"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // current-group() in a context-absent callee → XTDE1061 → #absent#.
        (await Transform(ss)).Should().Be("<out><a>[#absent#]</a><a>[#absent#]</a><a>[#absent#]</a></out>");
    }
}
