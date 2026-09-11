using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A stylesheet function's result must reach its caller whole and once. Two defects, both
/// behind W3C xslt30-test function-1022, lost or doubled content on the way out.
/// <list type="bullet">
/// <item><b>Lost text.</b> A function result is assembled from two channels: <c>_output</c>
/// holds elements and text in source order, and accumulated TextNodeItems are skipped as
/// duplicates of that text. xsl:copy-of of a text node wrote ONLY the accumulator, so
/// <c>f:id($r/node())</c> over <c>&lt;r&gt; inner &lt;b/&gt;&lt;/r&gt;</c> returned just the
/// element.</item>
/// <item><b>Doubled content.</b> A function body ran with the caller's temp-tree constructor
/// still active, so xsl:copy-of inside it cloned into the variable being built, and then the
/// function's result was copied in as well.</item>
/// </list>
/// function-1022 expects XPTY0004 from <c>subsequence()</c> given an empty position; with the
/// text node gone the position it computes was found and the error never raised.
/// </summary>
public sealed class FunctionResultContentTests
{
    private static async Task<string> RunAsync(string body)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:f="urn:f" exclude-result-prefixes="f">
              <xsl:output method="xml" indent="no" omit-xml-declaration="yes"/>
              <xsl:function name="f:id"><xsl:param name="s"/><xsl:copy-of select="$s"/></xsl:function>
              <xsl:function name="f:wrap"><xsl:param name="s"/><a><xsl:copy-of select="$s"/></a></xsl:function>
              <xsl:function name="f:wrap-id"><xsl:param name="s"/><a><xsl:copy-of select="f:id($s)"/></a></xsl:function>
              <xsl:template match="/">
                <xsl:variable name="d"><r> inner <b>x</b></r></xsl:variable>
                {{body}}
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc/>")).Trim();
    }

    [Theory]
    [InlineData("""<out><xsl:copy-of select="f:id($d/r/node())"/></out>""", "<out> inner <b>x</b></out>")]
    [InlineData("""<out><a><xsl:copy-of select="f:id($d/r/node())"/></a></out>""", "<out><a> inner <b>x</b></a></out>")]
    [InlineData("""<out><xsl:copy-of select="f:wrap-id($d/r/node())"/></out>""", "<out><a> inner <b>x</b></a></out>")]
    public async Task ACopiedTextNode_SurvivesTheFunctionBoundary(string body, string expected)
        => (await RunAsync(body)).Should().Be(expected);

    [Fact]
    public async Task AFunctionReturningTextThenElement_ReturnsBothItems()
        => (await RunAsync("""<out><xsl:value-of select="let $r := f:id($d/r/node()) return (count($r), $r[1] instance of text())"/></out>"""))
            .Should().Be("<out>2 true</out>");

    [Theory]
    [InlineData("f:id($d/r/node())", " inner x", "<v> inner <b>x</b></v>")]
    [InlineData("f:wrap($d/r/node())", " inner x", "<v><a> inner <b>x</b></a></v>")]
    [InlineData("f:wrap-id($d/r/node())", " inner x", "<v><a> inner <b>x</b></a></v>")]
    public async Task AFunctionResultCopiedIntoAVariable_AppearsOnce(string call, string stringValue, string tree)
    {
        // Previously f:wrap($d/r/node()) gave "<v><a> inner <b>x</b></a><a> inner <b>x</b></a></v>".
        var body = $"""
            <xsl:variable name="v"><xsl:copy-of select="{call}"/></xsl:variable>
            <out><xsl:value-of select="string($v)"/>|<v><xsl:copy-of select="$v"/></v></out>
            """;
        (await RunAsync(body)).Should().Be($"<out>{stringValue}|{tree}</out>");
    }
}
