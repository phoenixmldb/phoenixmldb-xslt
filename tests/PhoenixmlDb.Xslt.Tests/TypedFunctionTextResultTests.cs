using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A stylesheet function whose declared return type admits text (<c>node()*</c>,
/// <c>text()*</c>, <c>item()*</c>) must return TEXT NODES for text its body produces — most
/// often via the built-in rule copying a source text node.
///
/// The function seam never enabled text-node collection for the body, so that text fell
/// through to the output buffer and the result assembly handed the buffer back as a plain
/// string; a caller declaring <c>as="node()*"</c> then got a String and raised
/// <c>XTTE0780</c>. The typed <c>xsl:variable</c> seam has always done this correctly, so the
/// two disagreed on identical bodies — see
/// <see cref="TypedFunction_AndTypedVariable_AgreeOnTheSameBody"/>.
///
/// Reported by Martin Honnen: XSpec's <c>x:resolve-import</c> (<c>as="node()*"</c>) applies
/// templates over an <c>x:description</c> whose children include ordinary text. It was the
/// single largest XSpec blocker — 87 of 162 suites in the census, of which this clears 76.
/// </summary>
public sealed class TypedFunctionTextResultTests
{
    private const string Stylesheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:x="urn:x" exclude-result-prefixes="#all">
          <xsl:output method="text"/>
          <xsl:function name="x:resolve" as="node()*">
            <xsl:param name="d" as="element(desc)"/>
            <xsl:apply-templates select="$d" mode="gather"/>
          </xsl:function>
          <xsl:template match="desc" as="node()*" mode="gather">
            <xsl:apply-templates mode="#current"/>
          </xsl:template>
          <xsl:template match="item" as="element(item)" mode="gather">
            <xsl:copy><xsl:attribute name="added" select="'1'"/></xsl:copy>
          </xsl:template>
          <xsl:template match="/">
            <xsl:variable name="r" as="node()*" select="x:resolve(desc)"/>
            <xsl:value-of select="count($r)"/>|<xsl:value-of
              select="string-join($r ! (node-name(.), 'text')[1], ',')"/>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<string> RunAsync(string source)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet);
        return (await t.TransformAsync(source)).Trim();
    }

    /// <summary>Element only — the case that always worked; guards the fix's blast radius.</summary>
    [Fact]
    public async Task TypedFunction_ElementOnly_ReturnsElement() =>
        (await RunAsync("<desc><item/></desc>")).Should().Be("1|item");

    /// <summary>Text only — the reported failure. A text node IS a node.</summary>
    [Fact]
    public async Task TypedFunction_TextOnly_ReturnsTextNodeNotString() =>
        (await RunAsync("<desc>some text</desc>")).Should().Be("1|text");

    /// <summary>Mixed, both orderings — document order must survive the collection.</summary>
    [Theory]
    [InlineData("<desc><item/>some text</desc>", "2|item,text")]
    [InlineData("<desc>some text<item/></desc>", "2|text,item")]
    public async Task TypedFunction_MixedContent_PreservesDocumentOrder(string source, string expected) =>
        (await RunAsync(source)).Should().Be(expected);

    /// <summary>
    /// The invariant behind the bug: a typed function and a typed variable must agree on the
    /// same body. The variable path was always right; the function path was not.
    /// </summary>
    [Fact]
    public async Task TypedFunction_AndTypedVariable_AgreeOnTheSameBody()
    {
        const string viaVariable = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:template match="desc" as="node()*" mode="gather">
                <xsl:apply-templates mode="#current"/>
              </xsl:template>
              <xsl:template match="/">
                <xsl:variable name="r" as="node()*">
                  <xsl:apply-templates select="desc" mode="gather"/>
                </xsl:variable>
                <xsl:value-of select="count($r)"/>|<xsl:value-of
                  select="string-join($r ! (node-name(.), 'text')[1], ',')"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(viaVariable);
        var variableResult = (await t.TransformAsync("<desc>some text</desc>")).Trim();

        variableResult.Should().Be("1|text");
        (await RunAsync("<desc>some text</desc>")).Should().Be(variableResult);
    }

    /// <summary>
    /// Guard against over-reach: an ATOMIC return type must still atomize the body's text
    /// rather than hand back a node, so the coercion path stays intact.
    /// </summary>
    [Fact]
    public async Task TypedFunction_AtomicReturnType_StillAtomizesText()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:x="urn:x" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="x:s" as="xs:string">
                <xsl:param name="d" as="element(desc)"/>
                <xsl:value-of select="$d"/>
              </xsl:function>
              <xsl:template match="/"><xsl:value-of select="x:s(desc) || '/' || (x:s(desc) instance of xs:string)"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        (await t.TransformAsync("<desc>abc</desc>")).Trim().Should().Be("abc/true");
    }
}
