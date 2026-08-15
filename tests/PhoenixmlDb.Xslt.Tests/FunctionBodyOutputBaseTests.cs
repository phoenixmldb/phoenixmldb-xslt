using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Content a stylesheet function's body flushes must be taken relative to the function's own
/// base offset in the output buffer, never from position 0.
///
/// The simple-content path in <c>CreateElementCoreAsync</c> (reached inside comment / PI /
/// attribute content when text is being collected as sequence items) did
/// <c>_output.ToString()</c> + <c>_output.Clear()</c> over the WHOLE buffer. Inside a
/// stylesheet function that buffer already holds the caller's content, so the flush stole it —
/// visible here as the caller's text being swallowed into the constructed comment — and left
/// <c>_output</c> shorter than the offset the function had saved. The function's own
/// <c>ToString(savedOutput, …)</c> then threw
/// <c>ArgumentOutOfRangeException: startIndex cannot be larger than length of string</c>.
///
/// The fix declares the function body's base via <c>_outputLogicalStart</c> — the engine's
/// existing "content below here belongs to an enclosing scope" marker — and slices/truncates
/// from it.
///
/// Reported by Martin Honnen as an unhandled exception out of the XSpec compiler; it was the
/// largest remaining bucket in the census at 55 of 162 suites.
/// </summary>
public sealed class FunctionBodyOutputBaseTests
{
    [Fact]
    public async Task FunctionBody_FlushingSimpleContent_DoesNotStealCallerOutput()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:x="urn:x" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="xml" indent="no"/>
              <xsl:function name="x:f" as="node()*">
                <xsl:param name="n" as="xs:integer"/>
                <xsl:comment><xsl:element name="e">v<xsl:value-of select="$n"/></xsl:element></xsl:comment>
              </xsl:function>
              <xsl:template match="/">
                <out>text-before<xsl:copy-of select="x:f(1)"/>text-after</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync("<r/>");

        // Previously: <out><!--text-before v1--&gt;text-after</out> — the caller's text pulled
        // inside the comment, and the comment left unterminated.
        r.Should().Contain("<out>text-before<!--v1-->text-after</out>");
    }

    [Fact]
    public async Task NestedFunctionCalls_EachKeepTheirOwnOutputBase()
    {
        // Two levels deep, each with caller content around the call, so an inner flush that
        // ignored the base would corrupt the outer function's buffer as well as the template's.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:x="urn:x" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="xml" indent="no"/>
              <xsl:function name="x:inner" as="node()*">
                <xsl:comment><xsl:element name="e">I</xsl:element></xsl:comment>
              </xsl:function>
              <xsl:function name="x:outer" as="node()*">
                <xsl:text>O-start</xsl:text>
                <xsl:copy-of select="x:inner()"/>
                <xsl:text>O-end</xsl:text>
              </xsl:function>
              <xsl:template match="/">
                <out>T-start<xsl:copy-of select="x:outer()"/>T-end</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync("<r/>");

        r.Should().Contain("<out>T-startO-start<!--I-->O-endT-end</out>");
    }

    [Fact]
    public async Task FunctionBody_SimpleContentFlush_StillProducesItsOwnContent()
    {
        // Guard the other direction: basing the slice must not drop the function's own output.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:x="urn:x" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="x:f" as="node()*">
                <xsl:comment><xsl:element name="e">a</xsl:element>b</xsl:comment>
              </xsl:function>
              <xsl:template match="/"><xsl:value-of select="x:f() ! string(.)"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        (await t.TransformAsync("<r/>")).Trim().Should().Be("a b");
    }
}
