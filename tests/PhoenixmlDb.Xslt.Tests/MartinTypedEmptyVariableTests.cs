using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Martin Honnen 2026-08-01 (XSpec, compile-xslt-tests.xsl). A typed
/// <c>xsl:variable as="xs:integer?"</c> (or any atomic optional type) whose
/// sequence-constructor BODY executes but produces no items — e.g. an
/// <c>xsl:if</c> whose test is false — must bind the EMPTY SEQUENCE, not a
/// single empty string. XSpec's <c>x:saxon-version</c> is exactly this shape
/// (empty for a non-Saxon processor); binding "" instead of () made
/// <c>$x:saxon-version lt x:pack-version((11,0))</c> raise a spurious
/// <c>XPTY0004: Cannot compare xs:string with numeric type</c>, because a value
/// comparison with an empty operand must yield the empty sequence.
/// </summary>
public sealed class MartinTypedEmptyVariableTests
{
    [Fact]
    public async Task TypedOptionalAtomicVariable_EmptyBody_BindsEmptySequence()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="text"/>
              <xsl:variable as="xs:integer?" name="v">
                <xsl:if test="false()"><xsl:sequence select="42"/></xsl:if>
              </xsl:variable>
              <xsl:template match="/">count=<xsl:value-of select="count($v)"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync("<r/>");
        r.Trim().Should().Be("count=0");
    }

    [Fact]
    public async Task ValueComparison_EmptyOperand_YieldsEmptyNotError()
    {
        // The XSpec shape: an empty typed variable compared with `lt` against a
        // number must NOT raise XPTY0004; the comparison yields () → EBV false.
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="text"/>
              <xsl:variable as="xs:integer?" name="v">
                <xsl:if test="system-property('xsl:product-name') eq 'SAXON'"><xsl:sequence select="42"/></xsl:if>
              </xsl:variable>
              <xsl:template match="/">
                <xsl:choose>
                  <xsl:when test="$v lt 65536">deprecated</xsl:when>
                  <xsl:otherwise>ok</xsl:otherwise>
                </xsl:choose>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync("<r/>");
        r.Trim().Should().Be("ok");
    }

    [Fact]
    public async Task GlobalOptionalIntegerVariable_EmptyBody_IsNotAStringValue()
    {
        // Martin Honnen's minimal oracle (2026-08-01): the global x:saxon-version
        // shape. `$v instance of xs:string and $v = ''` must be FALSE — it was
        // returning true because the empty body bound the empty string.
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              version="3.0" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="text"/>
              <xsl:variable as="xs:integer?" name="v">
                <xsl:if test="system-property('xsl:product-name') eq 'SAXON'">
                  <xsl:sequence select="42"/>
                </xsl:if>
              </xsl:variable>
              <xsl:template match="/">
                <xsl:sequence select="$v instance of xs:string and $v = ''"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync("<r/>");
        r.Trim().Should().Be("false");
    }
}
