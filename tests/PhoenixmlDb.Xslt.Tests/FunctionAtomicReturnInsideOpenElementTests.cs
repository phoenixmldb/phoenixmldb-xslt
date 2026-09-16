using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A stylesheet function body is evaluated as its OWN sequence, whatever the caller had under
/// construction. <c>EnterTypedBody</c> isolates the body's construction state, but one piece of
/// that isolation — <c>_serializingElementDepth</c> — was reset only for declared types that are
/// node-ish AND admit multiple items. An ATOMIC declared type such as <c>as="xs:string?"</c>
/// satisfies neither clause, so the body inherited the caller's element depth.
///
/// Called with nothing under construction that depth is 0, the accumulator branch in
/// <c>WriteText</c> (which requires <c>_serializingElementDepth == 0</c>) runs, and
/// <c>xsl:value-of</c> leaves a <c>TextNodeItem</c> that the atomic-return coercion turns into a
/// string. Called from inside an open <c>xsl:copy</c> that branch is skipped. NON-empty text still
/// survives through the output buffer, but a ZERO-LENGTH <c>xsl:value-of</c> reaches neither
/// channel: the body produces no item at all and the function returns the empty sequence where
/// <c>""</c> is required. A strict <c>as="xs:string"</c> binding then raises XTTE0570 — on the
/// second element and never the first.
///
/// Reported twice by the same user: issue #4 (SchXslt2 1.9) was the flat case, fixed in 1a993ce by
/// atomizing the accumulator's TextNodeItem — an item the nested case never creates. SchXslt2's
/// <c>transpile.xsl</c> hits the nested case, because its
/// <c>schxslt:in-scope-language()</c> is called from a template that does
/// <c>xsl:copy</c> + <c>apply-templates</c>, so every element below the first failed to transpile.
/// Saxon 12.10 returns one item at every depth.
/// </summary>
public sealed class FunctionAtomicReturnInsideOpenElementTests
{
    /// <summary>
    /// SchXslt2's shape verbatim: an <c>as="xs:string?"</c> function whose body is an
    /// <c>xsl:value-of</c>, called while the caller has an element open.
    /// </summary>
    private const string CountingStylesheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:t" exclude-result-prefixes="#all">
          <xsl:output method="text"/>
          <xsl:function name="t:lang" as="xs:string?">
            <xsl:param name="context" as="node()"/>
            <xsl:value-of select="lower-case($context/ancestor-or-self::*[@xml:lang][1]/@xml:lang)"/>
          </xsl:function>
          <xsl:template match="/"><xsl:apply-templates select="*"/></xsl:template>
          <xsl:template match="*">
            <xsl:value-of select="count(t:lang(.))"/>
            <xsl:copy><xsl:apply-templates select="node()"/></xsl:copy>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<string> RunAsync(string stylesheet, string source)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return (await t.TransformAsync(source)).Trim();
    }

    /// <summary>
    /// The reported shape. The outermost call always returned one item; the nested ones returned
    /// the empty sequence, so this read "1" followed by a failure.
    /// </summary>
    [Fact]
    public async Task ZeroLengthValueOf_ReturnsOneItem_AtEveryDepth() =>
        (await RunAsync(CountingStylesheet, "<a><b><c/></b></a>")).Should().Be("111");

    /// <summary>
    /// The failure Martin saw: the zero-length result bound to a strict <c>as="xs:string"</c>.
    /// Raised XTTE0570 on the second element.
    /// </summary>
    [Fact]
    public async Task ZeroLengthValueOf_BindsToAStrictStringVariable()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:t" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="t:lang" as="xs:string?">
                <xsl:param name="context" as="node()"/>
                <xsl:value-of select="lower-case($context/ancestor-or-self::*[@xml:lang][1]/@xml:lang)"/>
              </xsl:function>
              <xsl:template match="/"><xsl:apply-templates select="*"/></xsl:template>
              <xsl:template match="*">
                <xsl:variable name="v" as="xs:string" select="t:lang(.)"/>
                <xsl:value-of select="'[' || $v || ']'"/>
                <xsl:copy><xsl:apply-templates select="node()"/></xsl:copy>
              </xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(ss, "<a><b><c/></b></a>")).Should().Be("[][][]");
    }

    /// <summary>
    /// A NON-empty return took a different channel (the output buffer) and always worked. It must
    /// keep working, and keep its value.
    /// </summary>
    [Fact]
    public async Task NonEmptyValueOf_IsUnaffected() =>
        (await RunAsync(CountingStylesheet, """<a xml:lang="EN"><b><c/></b></a>""")).Should().Be("111");

    /// <summary>
    /// Guard, and the case a careless fix breaks: a body that genuinely produces NO item must
    /// still return the empty sequence rather than being rewritten to "". The distinction is
    /// "an xsl:value-of ran and produced a zero-length text node" versus "nothing was produced".
    /// </summary>
    [Fact]
    public async Task GenuinelyEmptyBody_StillReturnsEmptySequence()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:t" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="t:none" as="xs:string?">
                <xsl:param name="context" as="node()"/>
                <xsl:sequence select="()"/>
              </xsl:function>
              <xsl:function name="t:none2" as="xs:string?">
                <xsl:param name="context" as="node()"/>
                <xsl:if test="false()"><xsl:value-of select="'x'"/></xsl:if>
              </xsl:function>
              <xsl:template match="/"><xsl:apply-templates select="*"/></xsl:template>
              <xsl:template match="*">
                <xsl:value-of select="count(t:none(.)) || count(t:none2(.))"/>
                <xsl:copy><xsl:apply-templates select="node()"/></xsl:copy>
              </xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(ss, "<a><b><c/></b></a>")).Should().Be("000000");
    }

    /// <summary>
    /// The element the caller was building must be unaffected: the function's text belongs to the
    /// function's result, and must not leak into the tree under construction.
    /// </summary>
    [Fact]
    public async Task TheCallersElementIsUnchanged()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:t="urn:t" exclude-result-prefixes="#all">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:function name="t:lang" as="xs:string?">
                <xsl:param name="context" as="node()"/>
                <xsl:value-of select="lower-case($context/ancestor-or-self::*[@xml:lang][1]/@xml:lang)"/>
              </xsl:function>
              <xsl:template match="/"><xsl:apply-templates select="*"/></xsl:template>
              <xsl:template match="*">
                <xsl:variable name="v" as="xs:string" select="t:lang(.)"/>
                <xsl:copy><xsl:apply-templates select="node()"/></xsl:copy>
              </xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(ss, "<a><b><c/></b></a>")).Should().Be("<a><b><c/></b></a>");
    }
}
