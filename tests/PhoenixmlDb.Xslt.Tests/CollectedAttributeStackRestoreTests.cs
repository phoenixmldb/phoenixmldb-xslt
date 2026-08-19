using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSpec census 07 → 08 (<c>XTSE0010</c>, 62 suites). Instructions that run a
/// sequence constructor in a nested output context save the
/// <c>_collectedAttributesStack</c>, clear it, and restore it in a
/// <c>finally</c>. The restore was
/// <c>Clear(); foreach (var sb in savedList) Push(sb);</c> — but
/// <see cref="Stack{T}"/> enumerates TOP-FIRST, so pushing that order back
/// REVERSED the stack.
///
/// At nesting depth 1 the reversal is invisible. At depth ≥ 2 the enclosing
/// element seals against its PARENT's buffer: it loses every attribute created
/// by the <c>xsl:attribute</c> instruction, and the parent silently gains them.
/// Every element built after the first occurrence is shifted by one.
///
/// Only <c>xsl:attribute</c> is affected — literal and AVT attributes on an LRE
/// are written straight into the start tag and never enter the stack. That is
/// why ordinary stylesheets were unaffected while XSpec's compiler, which must
/// compute attribute names and values, emitted
/// <c>&lt;xsl:stylesheet&gt;</c> with no <c>version</c> (hence XTSE0010),
/// <c>Q{uri}</c> UQNames with an empty local part, and valueless
/// <c>xsl:attribute</c> elements — one defect wearing several masks.
/// </summary>
public sealed class CollectedAttributeStackRestoreTests
{
    private static async Task<string> RunAsync(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync("<doc><src keep=\"1\"/></doc>");
    }

    [Fact]
    public async Task XslAttribute_SurvivesSiblingUntypedRtfVariable()
    {
        // The minimal shape: xsl:attribute, then an untyped xsl:variable with a
        // sequence-constructor body, inside a nested constructed element.
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="xml" indent="no"/>
              <xsl:template match="/">
                <wrapper>
                  <xsl:element name="probe" namespace="">
                    <xsl:attribute name="keep" select="'YES'"/>
                    <xsl:variable name="v"><lit a="1"/></xsl:variable>
                    <n><xsl:value-of select="count($v/*/@*)"/></n>
                  </xsl:element>
                </wrapper>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        r.Should().Contain("<probe keep=\"YES\">", "the xsl:attribute must survive the RTF variable");
        r.Should().NotContain("<wrapper keep=", "and must not leak onto the parent element");
    }

    [Fact]
    public async Task XslAttribute_OnLiteralResultElement_SurvivesUntypedRtfVariable()
    {
        // Same defect via an LRE parent — the trigger is the xsl:attribute
        // instruction, not the element-construction form.
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="xml" indent="no"/>
              <xsl:template match="/">
                <wrapper>
                  <probe>
                    <xsl:attribute name="keep" select="'YES'"/>
                    <xsl:variable name="v">text</xsl:variable>
                    <n><xsl:value-of select="$v"/></n>
                  </probe>
                </wrapper>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        r.Should().Contain("<probe keep=\"YES\">");
        r.Should().NotContain("<wrapper keep=");
    }

    [Fact]
    public async Task ConsecutiveConstructedElements_KeepTheirOwnAttributes()
    {
        // The reversal is cumulative: once it happens, EVERY later element is
        // shifted by one. Three siblings each carrying their own xsl:attribute
        // pin the rotation directly.
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="xml" indent="no"/>
              <xsl:template match="/">
                <wrapper>
                  <xsl:for-each select="1 to 3">
                    <xsl:element name="e" namespace="">
                      <xsl:attribute name="i" select="."/>
                      <xsl:variable name="v"><lit/></xsl:variable>
                      <xsl:value-of select="count($v/*)"/>
                    </xsl:element>
                  </xsl:for-each>
                </wrapper>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        r.Should().Contain("<e i=\"1\">1</e>");
        r.Should().Contain("<e i=\"2\">1</e>");
        r.Should().Contain("<e i=\"3\">1</e>");
        r.Should().NotContain("<wrapper i=");
    }

    [Fact]
    public async Task XsltNamespaceStylesheetElement_KeepsGeneratedVersionAttribute()
    {
        // The XSpec XTSE0010 shape reduced: a code generator building an
        // xsl:stylesheet whose version/exclude-result-prefixes are computed with
        // xsl:attribute, alongside an untyped RTF variable in the same body.
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:output method="xml" indent="no"/>
              <xsl:variable name="xsl-ns" as="xs:string" select="'http://www.w3.org/1999/XSL/Transform'"/>
              <xsl:template match="/" as="element()">
                <xsl:element name="xsl:stylesheet" namespace="{$xsl-ns}">
                  <xsl:attribute name="exclude-result-prefixes" select="'#all'"/>
                  <xsl:attribute name="version" select="'3.0'"/>
                  <xsl:element name="xsl:template" namespace="{$xsl-ns}">
                    <xsl:attribute name="name" select="'main'"/>
                    <xsl:variable name="body"><placeholder/></xsl:variable>
                    <xsl:sequence select="$body/*"/>
                  </xsl:element>
                </xsl:element>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        r.Should().Contain("version=\"3.0\"", "XTSE0010: the generated stylesheet needs its version");
        r.Should().Contain("exclude-result-prefixes=\"#all\"");
        r.Should().Contain("name=\"main\"");
    }
}
