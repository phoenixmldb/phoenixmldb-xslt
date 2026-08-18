using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An <c>xsl:function</c> that declares no <c>as=</c> has the default return type
/// <c>item()*</c> (XSLT 3.0 §10.3). Its body therefore constructs a SEQUENCE just like a
/// declared one, and text the body produces is a TEXT NODE.
///
/// The companion fix in <see cref="TypedFunctionTextResultTests"/> enabled text-node
/// collection for bodies whose declared type admits text, but gated it on the type being
/// declared at all. An omitted <c>as=</c> left that null, collection stayed off, and the text
/// fell into the output buffer to be handed back as an atomic — so the omitted form and an
/// explicit <c>as="item()*"</c> disagreed on identical bodies, which is exactly the class of
/// divergence that fix set out to remove.
///
/// Surfaced by W3C xslt30-test <c>sf-boolean-119</c> / <c>sf-not-119</c>, whose stylesheet
/// declares an untyped <c>Q{f}text()</c> and then takes <c>boolean()</c> of a sequence of its
/// results. Over text nodes that is <c>true</c>; over atomics it raises <c>FORG0006</c>,
/// "effective boolean value not defined for a sequence of two or more items starting with a
/// non-node value".
/// </summary>
public sealed class UntypedFunctionTextResultTests
{
    private static string Sheet(string declaration) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f"
          exclude-result-prefixes="#all">
          <xsl:output method="text"/>
          <xsl:function name="f:text"{{declaration}}>
            <xsl:param name="v" as="xs:string"/>
            <xsl:value-of select="$v"/>
          </xsl:function>
          <xsl:template match="/">
            <xsl:value-of select="count(//p ! f:text(string(.)))"/>|<xsl:value-of
              select="//p ! f:text(string(.)) instance of text()+"/>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private const string Source = "<doc><p>10</p><p>20</p></doc>";

    private static async Task<string> RunAsync(string declaration)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Sheet(declaration));
        return (await t.TransformAsync(Source)).Trim();
    }

    /// <summary>The reported failure: no <c>as=</c> at all must still yield text nodes.</summary>
    [Fact]
    public async Task UntypedFunction_ReturnsTextNodesNotAtomics() =>
        (await RunAsync("")).Should().Be("2|true");

    /// <summary>
    /// The invariant behind the bug: omitting <c>as=</c> means <c>item()*</c>, so the two
    /// spellings must agree. The explicit form was always right; the omitted one was not.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" as=\"item()*\"")]
    [InlineData(" as=\"node()*\"")]
    [InlineData(" as=\"text()*\"")]
    public async Task EveryFormAdmittingText_AgreesOnTheSameBody(string declaration) =>
        (await RunAsync(declaration)).Should().Be("2|true");

    /// <summary>
    /// The sf-boolean-119 shape itself. Over text nodes the effective boolean value of a
    /// multi-item sequence is true; over atomics it is FORG0006.
    /// </summary>
    [Fact]
    public async Task BooleanOverUntypedFunctionResults_IsTrue_NotFORG0006()
    {
        const string sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f"
              exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="f:text">
                <xsl:param name="v" as="xs:string"/>
                <xsl:value-of select="$v"/>
              </xsl:function>
              <xsl:template match="/">
                <xsl:value-of select="boolean(//p ! f:text(string(.)))"/>|<xsl:value-of
                  select="not(//p ! f:text(string(.)))"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(sheet);
        (await t.TransformAsync(Source)).Trim().Should().Be("true|false");
    }
}
