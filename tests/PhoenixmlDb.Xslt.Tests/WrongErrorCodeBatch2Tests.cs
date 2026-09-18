using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Three throw sites that detected the right condition and named the wrong code. Each is a
/// separate site rather than a separate case: the failures were grouped by the message the
/// engine produced, which is what identifies a site, and not by the (expected, actual) pair,
/// which scatters (BUGS.md #45).
///
/// The assertions check the code is right AND that the neighbouring code it used to report is
/// gone — a test that only looked for the new code would still pass if both were emitted.
/// </summary>
public sealed class WrongErrorCodeBatch2Tests
{
    private static async Task<Exception> RunExpectingErrorAsync(string stylesheet, string source = "<doc/>")
    {
        var t = new XsltTransformer();
        var act = async () =>
        {
            await t.LoadStylesheetAsync(stylesheet);
            await t.TransformAsync(source);
        };
        return (await act.Should().ThrowAsync<Exception>()).Which;
    }

    private static async Task AssertCodeAsync(string stylesheet, string wanted, string notWanted)
    {
        var message = (await RunExpectingErrorAsync(stylesheet)).Message;
        message.Should().Contain(wanted);
        message.Should().NotContain(notWanted);
    }

    // ---- fn:xml-to-json, escaped="true" with a bad escape ---------------------------------
    // FOJS0006 is "invalid XML representation of JSON" — the shape of the input tree. A well
    // formed j:string whose content is not valid JSON escaping is a different error with its
    // own code, FOJS0007 (W3C xml-to-json-C017/C018/C024).

    private static string XmlToJson(string stringContent) => $"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                        xmlns:j="http://www.w3.org/2005/xpath-functions">
          <xsl:output method="text"/>
          <xsl:template match="/">
            <xsl:variable name="in"><j:string escaped="true">{stringContent}</j:string></xsl:variable>
            <xsl:sequence select="xml-to-json($in)"/>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Theory]
    [InlineData(@"\Q")]          // unknown escape letter          (C017)
    [InlineData(@"\uDEFG")]      // \u followed by non-hex digits  (C018)
    [InlineData(@"\")]           // backslash at end of string     (C024)
    public Task XmlToJson_WithABadEscapeSequence_IsFOJS0007(string content)
        => AssertCodeAsync(XmlToJson(content), "FOJS0007", "FOJS0006");

    /// <summary>
    /// Guard: the site this narrows is only reached for bad ESCAPES. An input tree that is
    /// structurally wrong still reports FOJS0006, so the change cannot be credited for
    /// renaming every xml-to-json error (W3C xml-to-json-C001 is this shape).
    /// </summary>
    [Fact]
    public Task XmlToJson_WithTwoTopLevelElements_IsStillFOJS0006()
        => AssertCodeAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:j="http://www.w3.org/2005/xpath-functions">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="in"><j:string>a</j:string><j:string>b</j:string></xsl:variable>
                <xsl:sequence select="xml-to-json($in)"/>
              </xsl:template>
            </xsl:stylesheet>
            """, "FOJS0006", "FOJS0007");

    /// <summary>Guard: a valid escape still serializes, so the validator did not become a blanket reject.</summary>
    [Fact]
    public async Task XmlToJson_WithAValidEscapeSequence_Succeeds()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(XmlToJson(@"a\nb"));
        (await t.TransformAsync("<doc/>")).Trim().Should().Be(@"""a\nb""");
    }

    // ---- fn:json-to-xml, the "liberal" option -------------------------------------------
    // A wrongly-typed option VALUE is a type error. The sibling options in the same method,
    // "validate" and "escape", already raised XPTY0004; "liberal" alone raised FOJS0001, the
    // JSON *syntax* error, which describes the document rather than the option
    // (W3C json-to-xml-error-020/021, error-3260a).

    private static string JsonToXml(string liberalValue) => $$"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          <xsl:template match="/">
            <xsl:sequence select="json-to-xml('{}', map{'liberal': {{liberalValue}}})"/>
          </xsl:template>
        </xsl:stylesheet>
        """;

    [Theory]
    [InlineData("'yes'")]   // a string where a boolean is required
    [InlineData("()")]      // an empty sequence
    [InlineData("1")]       // an integer
    public Task JsonToXml_WithANonBooleanLiberalOption_IsXPTY0004(string value)
        => AssertCodeAsync(JsonToXml(value), "XPTY0004", "FOJS0001");

    /// <summary>
    /// Guard: genuine JSON syntax errors keep FOJS0001, so the option check did not take the
    /// code away from the error it belongs to.
    /// </summary>
    [Fact]
    public Task JsonToXml_WithMalformedJson_IsStillFOJS0001()
        => AssertCodeAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:sequence select="json-to-xml('{&quot;a&quot;:}')"/>
              </xsl:template>
            </xsl:stylesheet>
            """, "FOJS0001", "XPTY0004");

    // ---- xsl:expose naming a component and making it abstract ----------------------------
    // Neither code was raised: the component simply became abstract, and the blanket XTSE3080
    // ("a top-level package must not contain an abstract component") fired later. Which of the
    // two applies is decided by the DECLARATION, not by the xsl:expose: XTSE3010 is worded to
    // occur "only when the component declaration has an explicit visibility attribute", so an
    // implicitly-private declaration falls to XTSE3025 instead.

    private static string ExposeAbstract(string declaredVisibility) => $"""
        <xsl:package name="urn:p" package-version="1.0.0" version="3.0"
                     xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f">
          <xsl:function name="f:g"{declaredVisibility}><xsl:sequence select="0"/></xsl:function>
          <xsl:template name="xsl:initial-template" visibility="public"><ok/></xsl:template>
          <xsl:expose visibility="abstract" component="function" names="f:g#0"/>
        </xsl:package>
        """;

    /// <summary>
    /// An explicit visibility on the declaration — of any value, not just public — makes the
    /// exposed abstract visibility "inconsistent with its declared visibility": XTSE3010.
    /// W3C expose-917 declares public, expose-912b declares private, and both want XTSE3010.
    /// </summary>
    [Theory]
    [InlineData(@" visibility=""public""")]
    [InlineData(@" visibility=""private""")]
    [InlineData(@" visibility=""final""")]
    public Task Expose_MakingAnExplicitlyDeclaredComponentAbstract_IsXTSE3010(string declared)
        => AssertCodeAsync(ExposeAbstract(declared), "XTSE3010", "XTSE3080");

    /// <summary>
    /// The same package with the visibility attribute removed is the other code. This pair is
    /// the whole point: the two stylesheets differ in one attribute and must report differently
    /// (W3C expose-912b vs expose-912c, expose-917 vs expose-916).
    /// </summary>
    [Fact]
    public Task Expose_MakingAnImplicitlyPrivateComponentAbstract_IsXTSE3025()
        => AssertCodeAsync(ExposeAbstract(""), "XTSE3025", "XTSE3080");

    /// <summary>
    /// Guard: a wildcard stays XTSE3025 whatever the declaration says. XTSE3010 requires the
    /// component to be "listed explicitly by name", so the explicit-visibility branch above
    /// must not reach a wildcard exposure (W3C expose-919/920/922).
    /// </summary>
    [Fact]
    public Task Expose_WildcardMakingAnExplicitlyPublicComponentAbstract_IsStillXTSE3025()
        => AssertCodeAsync("""
            <xsl:package name="urn:p" package-version="1.0.0" version="3.0"
                         xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f">
              <xsl:function name="f:g" visibility="public"><xsl:sequence select="0"/></xsl:function>
              <xsl:template name="xsl:initial-template" visibility="public"><ok/></xsl:template>
              <xsl:expose visibility="abstract" component="function" names="*"/>
            </xsl:package>
            """, "XTSE3025", "XTSE3010");

    /// <summary>
    /// Guard: exposing a component as something other than abstract is not an error at all, so
    /// the new branch cannot be reached by ordinary exposure.
    /// </summary>
    [Fact]
    public async Task Expose_MakingAComponentPublic_IsNotAnError()
    {
        var t = new XsltTransformer();
        var act = async () => await t.LoadStylesheetAsync("""
            <xsl:package name="urn:p" package-version="1.0.0" version="3.0"
                         xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f">
              <xsl:function name="f:g"><xsl:sequence select="0"/></xsl:function>
              <xsl:template name="xsl:initial-template" visibility="public"><ok/></xsl:template>
              <xsl:expose visibility="public" component="function" names="f:g#0"/>
            </xsl:package>
            """);
        // The package compiles. It is not run: with no initial template named, applying
        // templates to <doc/> reaches only built-in rules, so the absence of output says
        // nothing either way — compiling without a static error is the whole claim here.
        await act.Should().NotThrowAsync();
    }
}
