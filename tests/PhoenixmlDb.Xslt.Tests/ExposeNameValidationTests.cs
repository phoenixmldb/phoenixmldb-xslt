using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xsl:expose</c> validation. A declaration that selected nothing — because the component kind
/// was not one xsl:expose can select, because the token was not a name, or because the name simply
/// matched no component — used to do nothing at all, silently, and the package then ran.
///
/// The matching itself was also incomplete: §3.6.3.1 admits <c>*</c>, <c>prefix:*</c>,
/// <c>*:local</c> and the <c>Q{uri}…</c> forms, and only the first two were understood. That had no
/// symptom precisely because an unmatched token was ignored, so the two defects hid each other.
/// </summary>
public sealed class ExposeNameValidationTests
{
    private const string Head = """
        <xsl:package name="urn:p" package-version="1.0.0" version="3.0"
                     xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f">
          <xsl:variable name="v1" select="0"/>
          <xsl:variable name="f:v2" select="0"/>
          <xsl:template name="t1">0</xsl:template>
          <xsl:function name="f:g"><xsl:sequence select="0"/></xsl:function>
          <xsl:attribute-set name="a1"><xsl:attribute name="A" select="0"/></xsl:attribute-set>
          <xsl:mode name="m1"/>
          <xsl:param name="prm"/>
          <xsl:template name="xsl:initial-template" visibility="public"><ok/></xsl:template>
        """;

    private static string Package(string exposeDeclarations) => $"{Head}\n  {exposeDeclarations}\n</xsl:package>";

    private static async Task<Exception> LoadExpectingErrorAsync(string stylesheet)
    {
        var t = new XsltTransformer();
        var act = async () => await t.LoadStylesheetAsync(stylesheet);
        return (await act.Should().ThrowAsync<Exception>()).Which;
    }

    private static async Task AssertLoadsAsync(string stylesheet)
    {
        var t = new XsltTransformer();
        var act = async () => await t.LoadStylesheetAsync(stylesheet);
        await act.Should().NotThrowAsync();
    }

    // ---- XTSE3020: a non-wildcard token matching no component ------------------------------
    // "It is a static error if a token in the names attribute of xsl:expose, other than a
    // wildcard, matches no component in the containing package." (§3.6.3.1)

    [Theory]
    // no component of that name at all (W3C expose-901, -902)
    [InlineData("""<xsl:expose visibility="public" component="variable" names="nosuch"/>""")]
    [InlineData("""<xsl:expose visibility="public" component="attribute-set" names="nosuch"/>""")]
    // the name exists, but as a different kind of component (W3C expose-905)
    [InlineData("""<xsl:expose visibility="public" component="attribute-set" names="v1"/>""")]
    [InlineData("""<xsl:expose visibility="public" component="template" names="v1"/>""")]
    // the right name and kind, the wrong arity (W3C expose-903)
    [InlineData("""<xsl:expose visibility="public" component="function" names="f:g#2"/>""")]
    // an arity on a template name, which only functions carry (W3C expose-925)
    [InlineData("""<xsl:expose visibility="public" component="template" names="t1#0"/>""")]
    // an xsl:param is not a variable component for this purpose (W3C expose-908)
    [InlineData("""<xsl:expose visibility="public" component="variable" names="prm"/>""")]
    public async Task Expose_WithANameThatMatchesNoComponent_IsXTSE3020(string expose)
        => (await LoadExpectingErrorAsync(Package(expose))).Message.Should().Contain("XTSE3020");

    /// <summary>
    /// Guard, and it is the one the spec states explicitly: the error is for a token "other than a
    /// wildcard". A wildcard that happens to match nothing is not an error — otherwise every
    /// package that exposes <c>component="mode" names="*"</c> without declaring a mode would break.
    /// </summary>
    [Theory]
    [InlineData("""<xsl:expose visibility="public" component="mode" names="*"/>""")]
    [InlineData("""<xsl:expose visibility="public" component="variable" names="nosuchprefix:*"/>""")]
    [InlineData("""<xsl:expose visibility="public" component="variable" names="*:nosuchlocal"/>""")]
    public Task Expose_WithAWildcardThatMatchesNothing_IsNotAnError(string expose)
        => AssertLoadsAsync(Package(expose));

    /// <summary>Guard: a token that does match is still accepted, for each component kind.</summary>
    [Theory]
    [InlineData("""<xsl:expose visibility="public" component="variable" names="v1"/>""")]
    [InlineData("""<xsl:expose visibility="public" component="template" names="t1"/>""")]
    [InlineData("""<xsl:expose visibility="public" component="function" names="f:g#0"/>""")]
    [InlineData("""<xsl:expose visibility="public" component="attribute-set" names="a1"/>""")]
    [InlineData("""<xsl:expose visibility="public" component="mode" names="m1"/>""")]
    public Task Expose_WithANameThatMatches_Loads(string expose)
        => AssertLoadsAsync(Package(expose));

    // ---- the *:local and Q{uri} token forms ------------------------------------------------

    /// <summary>
    /// <c>*:local</c> and the <c>Q{uri}…</c> forms are part of the token grammar and were not
    /// understood by the xsl:expose matcher, though its xsl:accept twin handled all of them. Each
    /// of these names a component that exists, so under the XTSE3020 rule above a matcher that
    /// still failed to see it would now raise — which is what makes this an assertion about
    /// matching rather than about error reporting.
    /// </summary>
    [Theory]
    [InlineData("""<xsl:expose visibility="public" component="variable" names="*:v2"/>""")]
    [InlineData("""<xsl:expose visibility="public" component="function" names="Q{urn:f}g#0"/>""")]
    [InlineData("""<xsl:expose visibility="public" component="variable" names="Q{urn:f}v2"/>""")]
    [InlineData("""<xsl:expose visibility="public" component="variable" names="Q{urn:f}*"/>""")]
    public Task Expose_WithAWildcardOrEQNameTokenForm_MatchesTheComponent(string expose)
        => AssertLoadsAsync(Package(expose));

    // ---- XTSE0020: the component attribute's value space ------------------------------------
    // component = "template" | "function" | "attribute-set" | "variable" | "mode", plus "*".

    /// <summary>
    /// An accumulator IS a component, but not one xsl:expose can select, so this is an invalid
    /// attribute value rather than an unmatched name (W3C expose-906, -907).
    /// </summary>
    [Theory]
    [InlineData("accumulator")]
    [InlineData("param")]
    [InlineData("key")]
    public async Task Expose_WithAComponentKindItCannotSelect_IsXTSE0020(string kind)
        => (await LoadExpectingErrorAsync(Package(
                $"""<xsl:expose visibility="public" component="{kind}" names="*"/>""")))
            .Message.Should().Contain("XTSE0020");

    /// <summary>Guard: every kind the spec does list still loads, including the "*" erratum form.</summary>
    [Theory]
    [InlineData("template")]
    [InlineData("function")]
    [InlineData("attribute-set")]
    [InlineData("variable")]
    [InlineData("mode")]
    [InlineData("*")]
    public Task Expose_WithAComponentKindItCanSelect_Loads(string kind)
        => AssertLoadsAsync(Package($"""<xsl:expose visibility="public" component="{kind}" names="*"/>"""));

    // ---- XTSE0020: a token that is not a name ----------------------------------------------

    /// <summary>
    /// Each token is "either a NameTest or a NamedFunctionRef". The #-prefixed pseudo-names that
    /// xsl:mode and xsl:template accept are neither, so this is not the XTSE3020 "matches no
    /// component" case even though it is also true that it matches none (W3C expose-904, -915).
    /// </summary>
    [Theory]
    [InlineData("#unnamed")]
    [InlineData("#default")]
    [InlineData("#all")]
    public async Task Expose_WithAPseudoNameToken_IsXTSE0020(string token)
    {
        var message = (await LoadExpectingErrorAsync(Package(
            $"""<xsl:expose visibility="public" component="mode" names="m1 {token}"/>"""))).Message;
        message.Should().Contain("XTSE0020");
        message.Should().NotContain("XTSE3020");
    }
}
