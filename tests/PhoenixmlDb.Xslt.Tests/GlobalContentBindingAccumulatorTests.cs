using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A global variable or parameter bound from CONTENT is evaluated to its own value, so its body
/// must neither append into, nor take its value from, whatever construct happens to be
/// mid-evaluation around it. <c>BindGlobalFromContentAsync</c> saved and restored only the output
/// buffer's length, which left two defects joined at the same seam.
///
/// ISOLATION. Globals bind lazily, on first reference, and that reference can occur inside a typed
/// variable's body — where a sequence accumulator is active. The global's own
/// <c>xsl:sequence</c> then appended into THAT variable's accumulator, so the variable bound its
/// own item plus the global's: an <c>as="xs:anyURI"</c> variable came back holding two items.
/// DocBook xslTNG 2.8.0 reaches this on its main path — <c>vp:chunk-output-base-uri</c> binds
/// <c>xs:anyURI($chunk-output-base-uri)</c> where <c>chunk-output-base-uri</c> is a content-bound
/// global — and the two items surfaced far downstream as
/// <c>XPTY0004 ... $hierarchical-uri ... sequence of 2 items</c> from <c>flatten-path()</c>.
///
/// CONSUMPTION. That path binds from the SERIALIZED TEXT, so typed items produced by
/// <c>xsl:sequence</c> were dropped: an <c>xs:anyURI("")</c> serializes to nothing, and the
/// empty-content guard then bound the EMPTY SEQUENCE. Isolating without consuming merely trades
/// "two items" for "no items" — both wrong, and the second one silent.
///
/// The tests below cover CONSUMPTION, which reproduces in a self-contained stylesheet. The
/// ISOLATION half needs a content-bound global bound lazily while an accumulator is active, which
/// could not be provoked in a reduction (eleven attempts); it is covered by a 7-file reproduction
/// built from unmodified xslTNG 2.8.0 release files, where Saxon 12.10 yields one item and this
/// engine yielded two. See the pull request for that artifact.
/// </summary>
public sealed class GlobalContentBindingAccumulatorTests
{
    private static async Task<string> RunAsync(string stylesheet, string source = "<doc/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return (await t.TransformAsync(source)).Trim();
    }

    /// <summary>
    /// The reported shape: a global bound from content, whose body binds a typed variable and
    /// returns a function result through <c>xsl:sequence</c>. The value is an EMPTY anyURI, which
    /// serializes to nothing — so the text-only binding path lost it and bound ().
    /// </summary>
    [Fact]
    public async Task ContentBoundGlobal_KeepsATypedItemWhoseStringValueIsEmpty()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:param name="base" as="xs:string" select="''"/>
              <xsl:function name="f:id" as="xs:anyURI">
                <xsl:param name="u" as="xs:string"/>
                <xsl:sequence select="xs:anyURI($u)"/>
              </xsl:function>
              <xsl:variable name="g" as="xs:anyURI?">
                <xsl:variable name="r" as="xs:anyURI">
                  <xsl:choose>
                    <xsl:when test="false()"><xsl:sequence select="xs:anyURI('never')"/></xsl:when>
                    <xsl:otherwise><xsl:sequence select="xs:anyURI($base)"/></xsl:otherwise>
                  </xsl:choose>
                </xsl:variable>
                <xsl:sequence select="f:id($r)"/>
              </xsl:variable>
              <xsl:template match="/"><xsl:value-of select="count($g)"/></xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(ss)).Should().Be("1");
    }

    /// <summary>The same shape with a NON-empty value always worked, and must keep working.</summary>
    [Fact]
    public async Task ContentBoundGlobal_NonEmptyValueIsUnaffected()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="f:id" as="xs:anyURI">
                <xsl:param name="u" as="xs:string"/>
                <xsl:sequence select="xs:anyURI($u)"/>
              </xsl:function>
              <xsl:variable name="g" as="xs:anyURI?">
                <xsl:variable name="r" as="xs:anyURI"><xsl:sequence select="xs:anyURI('x')"/></xsl:variable>
                <xsl:sequence select="f:id($r)"/>
              </xsl:variable>
              <xsl:template match="/"><xsl:value-of select="count($g) || '|' || $g"/></xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(ss)).Should().Be("1|x");
    }

    /// <summary>
    /// The typed item must keep its DECLARED type, not be flattened to a string on the way
    /// through — the consumption path casts to the declared item type as the eager pass does.
    /// </summary>
    [Fact]
    public async Task ContentBoundGlobal_TypedItemKeepsItsType()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:variable name="n" as="xs:integer"><xsl:sequence select="41"/></xsl:variable>
              <xsl:template match="/"><xsl:value-of select="($n + 1) || '|' || ($n instance of xs:integer)"/></xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(ss)).Should().Be("42|true");
    }

    /// <summary>
    /// Guard: a body that genuinely produces NO item must still bind the empty sequence when the
    /// declared type permits one. Consuming the accumulator must not manufacture a value.
    /// </summary>
    [Fact]
    public async Task ContentBoundGlobal_GenuinelyEmptyBodyStillBindsEmptySequence()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:variable name="g" as="xs:anyURI?">
                <xsl:if test="false()"><xsl:sequence select="xs:anyURI('never')"/></xsl:if>
              </xsl:variable>
              <xsl:template match="/"><xsl:value-of select="count($g) || '|' || empty($g)"/></xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(ss)).Should().Be("0|true");
    }

    /// <summary>
    /// Guard: a global with content and NO <c>as</c> is still a temporary tree (one document
    /// node), whatever the body produced. The consumption branch must not intercept it.
    /// </summary>
    [Fact]
    public async Task ContentBoundGlobal_WithoutAsIsStillATemporaryTree()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:variable name="g"><xsl:text>a</xsl:text><xsl:value-of select="'b'"/></xsl:variable>
              <xsl:template match="/"><xsl:value-of
                select="count($g) || '|' || ($g instance of document-node()) || '|' || string($g)"/></xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(ss)).Should().Be("1|true|ab");
    }

    /// <summary>
    /// Guard: a sequence-typed global keeps every item the body produced, in order — the
    /// occurrence unwrap applies only to ExactlyOne/ZeroOrOne.
    /// </summary>
    [Fact]
    public async Task ContentBoundGlobal_SequenceTypeKeepsAllItems()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:variable name="g" as="xs:integer*">
                <xsl:sequence select="1"/><xsl:sequence select="2"/><xsl:sequence select="3"/>
              </xsl:variable>
              <xsl:template match="/"><xsl:value-of select="count($g) || '|' || string-join($g ! string(.), ',')"/></xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(ss)).Should().Be("3|1,2,3");
    }
}
