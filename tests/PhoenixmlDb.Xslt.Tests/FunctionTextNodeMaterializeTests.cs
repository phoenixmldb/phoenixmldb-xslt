using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A text node returned by an <c>xsl:function</c> must be a real node the caller can navigate.
///
/// While the body runs, a text item is carried as <c>TextNodeItem</c> — a bare record with no
/// identity, parent or store. That marker is load-bearing: the assembly that builds the function
/// result uses it to recognise that a text item in the accumulator is the same text already
/// written to the output buffer. Materialize it too early and the text is emitted twice
/// (<c>xsl:text</c> A + B came back as AB, A, B), which is why the existing conversion is
/// restricted to a declared Text/Node return type.
///
/// Nothing converted the rest, and the rest is mostly <c>as="item()*"</c> — the DEFAULT, so the
/// common case. The value answered <c>instance of text()</c> but any axis step on it raised
/// XPTY0020, quoting the internal type name to the author, while <c>root()</c> and <c>path()</c>
/// returned empty. Declaring <c>as="text()*"</c> worked; not declaring anything did not.
///
/// The conversion now runs after the assembly has chosen its channel, where the marker has no
/// job left.
/// </summary>
public class FunctionTextNodeMaterializeTests
{
    private static async Task<string> Run(string body, string returnType = "item()*")
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f"
                            exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="f:t" as="{returnType}">
                <xsl:param name="s" as="xs:string"/>
                <xsl:value-of select="$s"/>
              </xsl:function>
              <xsl:template match="/" name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        return await t.TransformAsync("<in/>").ConfigureAwait(true);
    }

    [Theory]
    [InlineData("item()*")]
    [InlineData("text()*")]
    [InlineData("node()*")]
    public async Task FunctionTextResult_SupportsAnAxisStep(string returnType)
    {
        // as="item()*" is the one that failed, and it is what a function gets by default.
        (await Run("""<xsl:value-of select="count(f:t('abc')/self::text())"/>""", returnType)
            .ConfigureAwait(true)).Should().Be("1");
    }

    [Theory]
    [InlineData("item()*")]
    [InlineData("text()*")]
    public async Task FunctionTextResult_IsItsOwnRoot(string returnType)
    {
        // XDM: the root of a parentless node is the node itself, never the empty sequence.
        (await Run("""<xsl:value-of select="count(root(f:t('abc')))"/>""", returnType)
            .ConfigureAwait(true)).Should().Be("1");
    }

    [Fact]
    public async Task FunctionTextResult_IsStillATextNode()
    {
        (await Run("""<xsl:value-of select="f:t('abc') instance of text()"/>""")
            .ConfigureAwait(true)).Should().Be("true");
    }

    [Fact]
    public async Task FunctionTextResult_KeepsItsStringValue()
    {
        (await Run("""<xsl:value-of select="string(f:t('abc'))"/>""").ConfigureAwait(true))
            .Should().Be("abc");
    }

    [Fact]
    public async Task FunctionTextResult_IsDeepEqualToAnEquivalentTextNode()
    {
        // Ties to DeepEqualTextNodeItemTests: both representations must agree.
        var ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f"
                            exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="f:t" as="item()*">
                <xsl:param name="s" as="xs:string"/>
                <xsl:value-of select="$s"/>
              </xsl:function>
              <xsl:variable name="helper" as="element()"><item>abc</item></xsl:variable>
              <xsl:template match="/" name="xsl:initial-template">
                <xsl:value-of select="deep-equal($helper/text(), f:t('abc'))"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        (await t.TransformAsync("<in/>").ConfigureAwait(true)).Should().Be("true");
    }

    [Fact]
    public async Task TwoTextInstructions_ReturnTwoItems_NotTheConcatenationAsWell()
    {
        // The hazard the early conversion caused: A + B came back as AB, A, B. This is the
        // guard that the conversion still happens after the assembly, not before it.
        var ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f"
                            exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="f:ab" as="item()*">
                <xsl:text>A</xsl:text><xsl:text>B</xsl:text>
              </xsl:function>
              <xsl:template match="/" name="xsl:initial-template">
                <xsl:value-of select="count(f:ab()) || ':' || string-join(f:ab() ! string(.), '|')"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        (await t.TransformAsync("<in/>").ConfigureAwait(true)).Should().Be("2:A|B");
    }

    [Fact]
    public async Task AtomicReturnType_StillReturnsAnAtomicValue_NotATextNode()
    {
        // The conversion must not reach an atomic return type: a string there stays a string.
        (await Run("""<xsl:value-of select="f:t('abc') instance of xs:string"/>""", "xs:string")
            .ConfigureAwait(true)).Should().Be("true");
    }
}
