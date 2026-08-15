using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A matched attribute template with a declared type (<c>as="attribute()"</c>) captures its own
/// result and then propagates it to whatever sequence accumulator is active. Under the built-in
/// shallow-copy rule that accumulator was the ENCLOSING one — a <c>node()*</c>-typed function or
/// variable body — so the attribute escaped the element being copied and surfaced as a loose
/// attribute node in the caller's sequence, with the element losing it.
///
/// The child-application half of shallow-copy already suspended the accumulator for exactly this
/// reason; the attribute half never did.
///
/// XSpec's <c>gather-specs.xsl</c> is precisely this shape — mode <c>x:gather-specs</c> is
/// <c>on-no-match="shallow-copy"</c> and carries a typed template for
/// <c>@as|@function|@mode|@name|@port|@template</c> — so every <c>x:variable</c>/<c>x:param</c>
/// shed its attributes into <c>x:resolve-import</c>'s result. Those loose attributes were then
/// fatal in <c>combine.xsl</c>, which drops the sequence into an <c>xsl:document</c>
/// (<c>XTDE0420</c>). It accounted for 76 of the 162 XSLT suites in the census.
///
/// The bug predates the typed-function text fix but was unreachable behind it: the function
/// raised <c>XTTE0780</c> before it could return the malformed sequence.
/// </summary>
public sealed class ShallowCopyAttributeTemplateTests
{
    private const string Stylesheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:x="urn:x" exclude-result-prefixes="#all">
          <xsl:output method="text"/>
          <xsl:mode name="g" on-multiple-match="fail" on-no-match="shallow-copy"/>

          <xsl:function name="x:resolve" as="node()*">
            <xsl:param name="d" as="element(root)"/>
            <xsl:apply-templates select="$d" mode="g"/>
          </xsl:function>

          <xsl:template match="root" as="node()*" mode="g">
            <xsl:apply-templates mode="#current"/>
          </xsl:template>

          <!-- no template for <var>: the built-in shallow-copy rule must apply this
               attribute template INSIDE the copy it constructs -->
          <xsl:template match="@as | @name" as="attribute()" mode="g">
            <xsl:attribute name="{local-name()}" select="normalize-space(.)"/>
          </xsl:template>

          <xsl:template match="/">
            <xsl:variable name="r" as="node()*" select="x:resolve(root)"/>
            <xsl:value-of select="count($r)"/>|<xsl:value-of
              select="string-join($r ! (if (. instance of attribute()) then 'ATTR:'||name(.)
                                        else if (. instance of text()) then 'text'
                                        else 'elem:'||name(.)), ',')"/>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<string> RunAsync(string source)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet);
        return (await t.TransformAsync(source)).Trim();
    }

    /// <summary>
    /// The reported shape. Previously produced four items —
    /// <c>text[&lt;var] , ATTR:as , ATTR:name , text[>&lt;/var>]</c> — the element shredded and
    /// its attributes loose.
    /// </summary>
    [Fact]
    public async Task ShallowCopy_TypedAttributeTemplate_AttributesStayOnTheCopy() =>
        (await RunAsync("""<root><var as="xs:integer" name="v"/></root>"""))
            .Should().Be("1|elem:var");

    /// <summary>The attribute template's rewrite must actually take effect on the copy.</summary>
    [Fact]
    public async Task ShallowCopy_TypedAttributeTemplate_AppliesItsRewrite()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:x="urn:x" exclude-result-prefixes="#all">
              <xsl:output method="xml" indent="no"/>
              <xsl:mode name="g" on-no-match="shallow-copy"/>
              <xsl:function name="x:resolve" as="node()*">
                <xsl:param name="d" as="element(root)"/>
                <xsl:apply-templates select="$d" mode="g"/>
              </xsl:function>
              <xsl:template match="root" as="node()*" mode="g"><xsl:apply-templates mode="#current"/></xsl:template>
              <xsl:template match="@name" as="attribute()" mode="g">
                <xsl:attribute name="name" select="'trimmed-' || normalize-space(.)"/>
              </xsl:template>
              <xsl:template match="/"><xsl:copy-of select="x:resolve(root)"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var r = await t.TransformAsync("""<root><var name="  v  "/></root>""");
        r.Should().Contain("""name="trimmed-v" """.TrimEnd());
        r.Should().Contain("<var");
    }

    /// <summary>
    /// Shallow-copy with ordinary text content must still round-trip through a typed function —
    /// the element stays one node rather than fragmenting into markup text.
    /// </summary>
    [Fact]
    public async Task ShallowCopy_WithTextContent_StaysOneElement() =>
        (await RunAsync("<root><var>hello</var></root>")).Should().Be("1|elem:var");

    /// <summary>
    /// Guard: a typed attribute template invoked where there IS no element under construction
    /// still contributes its attribute to the caller's sequence. Suspending the accumulator must
    /// be scoped to the shallow copy, not applied globally.
    /// </summary>
    [Fact]
    public async Task TypedAttributeTemplate_OutsideAnyCopy_StillReachesTheSequence()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:x="urn:x" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:mode name="g" on-no-match="deep-skip"/>
              <xsl:function name="x:attrs" as="attribute()*">
                <xsl:param name="d" as="element(var)"/>
                <xsl:apply-templates select="$d/@*" mode="g"/>
              </xsl:function>
              <xsl:template match="@name" as="attribute()" mode="g">
                <xsl:attribute name="renamed" select="."/>
              </xsl:template>
              <xsl:template match="/">
                <xsl:variable name="a" as="attribute()*" select="x:attrs(root/var)"/>
                <xsl:value-of select="count($a)"/>|<xsl:value-of select="string-join($a ! name(.), ',')"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        (await t.TransformAsync("""<root><var name="v"/></root>""")).Trim().Should().Be("1|renamed");
    }
}
