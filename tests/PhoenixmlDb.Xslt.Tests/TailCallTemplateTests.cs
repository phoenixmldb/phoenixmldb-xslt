using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:call-template in tail position runs in constant stack. Nesting past the recursion limit was a silent
/// return, so deeper recursion lost its output with no diagnostic: the ISO Schematron skeleton's
/// sch-check:strip-strings walks each assert test one character per call, and for a test longer than ~1,150
/// characters it returned a truncated string that made test-paren report a balanced expression as
/// "Unclosed parenthesis" (403 asserts in 52 state EMS schematrons). Raising the limit instead exhausts the
/// native stack at a few thousand frames. Saxon runs these tail-recursive shapes at any depth; the expected
/// values are its results.
/// </summary>
public sealed class TailCallTemplateTests
{
    private static async Task<string> RunAsync(string stylesheet, string source = "<x/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return (await t.TransformAsync(source)).Trim();
    }

    private static string Stylesheet(string templates, string body) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          {{templates}}
          <xsl:template match="/">{{body}}</xsl:template>
        </xsl:stylesheet>
        """;

    // Emits a character, then calls itself as the last instruction of an xsl:if — strip-strings' shape.
    private const string Emit = """
        <xsl:template name="emit">
          <xsl:param name="s"/>
          <xsl:if test="string-length($s) &gt; 0">
            <xsl:value-of select="substring($s, 1, 1)"/>
            <xsl:call-template name="emit"><xsl:with-param name="s" select="substring($s, 2)"/></xsl:call-template>
          </xsl:if>
        </xsl:template>
        """;

    // Accumulator in the otherwise branch of an xsl:choose — test-paren's shape.
    private const string Count = """
        <xsl:template name="count">
          <xsl:param name="n"/>
          <xsl:param name="acc" select="0"/>
          <xsl:choose>
            <xsl:when test="$n = 0"><xsl:value-of select="$acc"/></xsl:when>
            <xsl:otherwise>
              <xsl:call-template name="count">
                <xsl:with-param name="n" select="$n - 1"/>
                <xsl:with-param name="acc" select="$acc + 1"/>
              </xsl:call-template>
            </xsl:otherwise>
          </xsl:choose>
        </xsl:template>
        """;

    [Theory]
    [InlineData(1_000)]
    [InlineData(100_000)]
    public async Task TailRecursiveCount_RunsAtAnyDepth(int depth)
    {
        (await RunAsync(Stylesheet(Count, $"""<xsl:call-template name="count"><xsl:with-param name="n" select="{depth}"/></xsl:call-template>""")))
            .Should().Be(depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task TailRecursiveEmit_KeepsEveryCharacterInOrder()
    {
        var r = await RunAsync(Stylesheet(Emit,
            """<xsl:variable name="s" select="string-join(for $i in 1 to 20000 return string($i mod 10), '')"/><xsl:variable name="out"><xsl:call-template name="emit"><xsl:with-param name="s" select="$s"/></xsl:call-template></xsl:variable><xsl:value-of select="string-length($out), $out = $s"/>"""));
        r.Should().Be("20000 true", "every character is emitted, in order, from 20,000 tail calls");
    }

    [Fact]
    public async Task TunnelParameters_ReachEveryTailCall()
    {
        const string templates = """
            <xsl:template name="loop">
              <xsl:param name="n"/>
              <xsl:param name="t" tunnel="yes"/>
              <xsl:if test="$n = 0"><xsl:value-of select="$t"/></xsl:if>
              <xsl:if test="$n &gt; 0">
                <xsl:call-template name="loop"><xsl:with-param name="n" select="$n - 1"/></xsl:call-template>
              </xsl:if>
            </xsl:template>
            """;
        (await RunAsync(Stylesheet(templates,
            """<xsl:call-template name="loop"><xsl:with-param name="n" select="5000"/><xsl:with-param name="t" tunnel="yes" select="'tunnelled'"/></xsl:call-template>""")))
            .Should().Be("tunnelled");
    }

    [Fact]
    public async Task CallerLocals_AreNotVisibleToTheTailCalledTemplate()
    {
        const string templates = """
            <xsl:variable name="v" select="'GLOBAL'"/>
            <xsl:template name="a"><xsl:variable name="v" select="'LOCAL'"/><xsl:call-template name="b"/></xsl:template>
            <xsl:template name="b"><xsl:value-of select="$v"/></xsl:template>
            """;
        (await RunAsync(Stylesheet(templates, """<xsl:call-template name="a"/>"""))).Should().Be("GLOBAL");
    }

    [Fact]
    public async Task TemplateWithAs_StillCorrect()
    {
        const string templates = """
            <xsl:template name="sum" as="xs:integer" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:param name="n"/>
              <xsl:choose>
                <xsl:when test="$n = 0"><xsl:sequence select="0"/></xsl:when>
                <xsl:otherwise>
                  <xsl:variable name="rest" as="xs:integer"><xsl:call-template name="sum"><xsl:with-param name="n" select="$n - 1"/></xsl:call-template></xsl:variable>
                  <xsl:sequence select="$n + $rest"/>
                </xsl:otherwise>
              </xsl:choose>
            </xsl:template>
            """;
        (await RunAsync(Stylesheet(templates, """<xsl:call-template name="sum"><xsl:with-param name="n" select="100"/></xsl:call-template>""")))
            .Should().Be("5050");
    }

    [Fact]
    public async Task CallTemplateLastInAMatchTemplate_KeepsDocumentOrder()
    {
        // The last instruction of a template RULE (reached by apply-templates, not call-template) must not be
        // deferred to an enclosing call-template frame — its output would land after the frame's remaining body.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:call-template name="outer"/></xsl:template>
              <xsl:template name="outer"><xsl:apply-templates select="r/i"/>|end</xsl:template>
              <xsl:template match="i"><xsl:call-template name="mark"><xsl:with-param name="v" select="string(.)"/></xsl:call-template></xsl:template>
              <xsl:template name="mark"><xsl:param name="v"/>[<xsl:value-of select="$v"/>]</xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(ss, "<r><i>1</i><i>2</i></r>")).Should().Be("[1][2]|end");
    }

    [Fact]
    public async Task NonTailRecursionPastTheLimit_IsAnError_NotTruncatedOutput()
    {
        const string templates = """
            <xsl:template name="deep">
              <xsl:param name="n"/>
              <xsl:if test="$n &gt; 0"><xsl:call-template name="deep"><xsl:with-param name="n" select="$n - 1"/></xsl:call-template>x</xsl:if>
            </xsl:template>
            """;
        var act = () => RunAsync(Stylesheet(templates, """<xsl:call-template name="deep"><xsl:with-param name="n" select="50000"/></xsl:call-template>"""));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTDE0000");
    }
}
