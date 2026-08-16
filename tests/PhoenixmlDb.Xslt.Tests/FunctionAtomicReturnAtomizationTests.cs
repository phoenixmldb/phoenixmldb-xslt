using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A stylesheet function with an ATOMIC declared return type atomizes its result before
/// converting it (the function conversion rules): a node's typed value is taken and then cast
/// to the declared type, so returning an attribute or element where <c>xs:decimal</c> is
/// declared is legal — not <c>XTTE0780</c>.
///
/// The result assembly coerced <c>TextNodeItem</c> to atomic types but left XDM nodes alone,
/// so a body that selected a node raised
/// <c>XTTE0780: … requires type Decimal but got XdmAttribute</c>.
///
/// XSpec's <c>x:xslt-version</c> is exactly this shape — <c>as="xs:decimal"</c> over
/// <c>(ancestor-or-self::*[@xslt-version][1]/@xslt-version, 3.0)[1]</c>, which yields an
/// attribute node whenever the stylesheet declares a version and the literal 3.0 otherwise.
/// It was the largest remaining census bucket at 83 of 162 suites.
/// </summary>
public sealed class FunctionAtomicReturnAtomizationTests
{
    private const string Stylesheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:x="urn:x" exclude-result-prefixes="#all">
          <xsl:output method="text"/>
          <xsl:function name="x:v" as="xs:decimal">
            <xsl:param name="e" as="element()"/>
            <xsl:sequence select="($e/@ver, 3.0)[1]"/>
          </xsl:function>
          <xsl:template match="/">
            <xsl:value-of select="x:v(r) || '|' || (x:v(r) instance of xs:decimal)"/>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<string> RunAsync(string source)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet);
        return (await t.TransformAsync(source)).Trim();
    }

    /// <summary>The reported shape: the selected attribute node atomizes and casts.</summary>
    [Fact]
    public async Task AtomicReturnType_AtomizesAnAttributeNode() =>
        (await RunAsync("""<r ver="2.0"/>""")).Should().Be("2|true");

    /// <summary>The other arm of the same expression still works.</summary>
    [Fact]
    public async Task AtomicReturnType_LiteralFallbackUnchanged() =>
        (await RunAsync("<r/>")).Should().Be("3|true");

    /// <summary>Element nodes atomize to their string value just the same.</summary>
    [Fact]
    public async Task AtomicReturnType_AtomizesAnElementNode()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:x="urn:x" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="x:n" as="xs:integer">
                <xsl:param name="e" as="element()"/>
                <xsl:sequence select="$e/num"/>
              </xsl:function>
              <xsl:template match="/"><xsl:value-of select="x:n(r) + 1"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        (await t.TransformAsync("<r><num>41</num></r>")).Trim().Should().Be("42");
    }

    /// <summary>
    /// Guard: a NODE declared return type must still receive the node itself, not its atomized
    /// value — atomization applies only when an atomic type is declared.
    /// </summary>
    [Fact]
    public async Task NodeReturnType_StillReturnsTheNode()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:x="urn:x" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="x:a" as="attribute()">
                <xsl:param name="e" as="element()"/>
                <xsl:sequence select="$e/@ver"/>
              </xsl:function>
              <xsl:template match="/"><xsl:value-of
                select="name(x:a(r)) || '=' || string(x:a(r))"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        (await t.TransformAsync("""<r ver="2.0"/>""")).Trim().Should().Be("ver=2.0");
    }

    /// <summary>
    /// Guard: a value that cannot be cast to the declared atomic type must still be rejected.
    /// </summary>
    [Fact]
    public async Task AtomicReturnType_UncastableValueStillFails()
    {
        var act = async () => await RunAsync("""<r ver="not-a-number"/>""");
        (await act.Should().ThrowAsync<System.Exception>())
            .Which.Message.Should().MatchRegex("XTTE0780|FORG0001|XPTY0004");
    }
}
