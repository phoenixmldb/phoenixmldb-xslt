using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Text written into an xsl:variable belongs to that variable once, and in document order.
/// Directly in a function body, text is written to both the result accumulator and the output
/// buffer, because the function-result assembly treats the accumulated copy as a duplicate. That
/// rule keyed on "inside a function at any depth", so it also fired inside a local variable,
/// whose drain emitted both copies: <c>string($v)</c> came back <c>xx</c>.
/// </summary>
public sealed class FunctionLocalVariableTextTests
{
    private const string Functions = """
        <xsl:function name="f:typed" as="xs:string"><xsl:variable name="v"><xsl:value-of select="'x'"/></xsl:variable><xsl:sequence select="string($v)"/></xsl:function>
        <xsl:function name="f:untyped"><xsl:variable name="v"><xsl:value-of select="'x'"/></xsl:variable><xsl:sequence select="string($v)"/></xsl:function>
        <xsl:function name="f:text" as="xs:string"><xsl:variable name="v"><xsl:text>x</xsl:text></xsl:variable><xsl:sequence select="string($v)"/></xsl:function>
        <xsl:function name="f:mixed"><xsl:param name="n"/><xsl:variable name="v">a<b>c</b><xsl:value-of select="$n"/></xsl:variable><xsl:sequence select="string($v)"/></xsl:function>
        """;

    private static async Task<string> RunAsync(string body)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              {{Functions}}
              <xsl:template match="/">{{body}}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc/>")).Trim();
    }

    [Theory]
    [InlineData("f:typed()")]
    [InlineData("f:untyped()")]
    [InlineData("f:text()")]
    public async Task ALocalVariablesText_IsNotDoubled_AtEitherDepth(string call)
        => (await RunAsync($"""[<xsl:value-of select="{call}"/>]<out><xsl:value-of select="{call}"/></out>"""))
            .Should().Be("[x]<out>x</out>", "the answer was xx at the top level, and for two of the three inside <out> too");

    [Fact]
    public async Task MixedContent_InALocalVariable_IsNotDoubled()
        => (await RunAsync("""<xsl:value-of select="f:mixed('d')"/>""")).Should().Be("acd");

    /// <summary>
    /// Outside any function too: an untyped variable builds a tree, and its accumulator only
    /// catches typed results from nested instructions, drained after the text. xsl:text went to
    /// that accumulator, so it moved to the end.
    /// </summary>
    [Theory]
    [InlineData("""<xsl:value-of select="string($v)"/>""", "acde")]
    [InlineData("""<xsl:copy-of select="$v"/>""", "a<b>c</b>de")]
    public async Task TextInAnUntypedVariable_KeepsDocumentOrder(string use, string expected)
        => (await RunAsync($"""<xsl:variable name="v">a<b>c</b><xsl:text>d</xsl:text><xsl:value-of select="'e'"/></xsl:variable>{use}"""))
            .Should().Be(expected);

    [Fact]
    public async Task TextDirectlyInAFunctionBody_StillOrdersWithElements()
    {
        // The both-channels rule exists for this: text and an element returned together keep order.
        var ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:function name="f:te"><xsl:text>t</xsl:text><e/><xsl:value-of select="'v'"/></xsl:function>
              <xsl:template match="/"><out><xsl:copy-of select="f:te()"/></out></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        (await t.TransformAsync("<doc/>")).Trim().Should().Be("<out>t<e/>v</out>");
    }
}
