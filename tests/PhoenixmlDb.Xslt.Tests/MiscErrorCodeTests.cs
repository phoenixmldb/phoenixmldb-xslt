using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Error codes for a batch of W3C misc/error cases, each of which reported a neighbouring code (or
/// none): the code says which rule was broken, so a wrong one sends the reader to the wrong rule.
/// </summary>
public sealed class MiscErrorCodeTests
{
    private static async Task<string> RunAsync(string stylesheet, string source = "<doc/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return (await t.TransformAsync(source)).Trim();
    }

    private static string Wrap(string declarations, string body = "") => $"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:my="urn:my">
          <xsl:output method="text"/>
          {declarations}
          <xsl:template match="/">{body}</xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task AssertErrorAsync(string stylesheet, string code, string source = "<doc/>")
    {
        var act = () => RunAsync(stylesheet, source);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain(code);
    }

    /// <summary>A name that is not a QName is XTSE0020, not "template not found" (error-0020f).</summary>
    [Fact]
    public Task CallTemplate_WithANameThatIsNotAQName_IsXTSE0020()
        => AssertErrorAsync(Wrap("""<xsl:template name="x"/>""", """<xsl:call-template name="x/y"/>"""), "XTSE0020");

    /// <summary>An invalid token in a mode list is the list's own error, XTSE0550 (error-0550a/e/f).</summary>
    [Theory]
    [InlineData("name!1223")]
    [InlineData("a b c#")]
    public Task Template_WithAnInvalidModeToken_IsXTSE0550(string mode)
        => AssertErrorAsync(Wrap($"""<xsl:template match="p" mode="{mode}"/>"""), "XTSE0550");

    /// <summary>
    /// A required parameter the invocation cannot supply is the dynamic XTDE0700: there is no
    /// xsl:call-template for the static XTSE0690 to point at (error-0700b).
    /// </summary>
    [Fact]
    public async Task AnInitialTemplate_WithARequiredParameter_IsXTDE0700()
    {
        var ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template"><xsl:param name="y" required="yes"/><xsl:value-of select="$y"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var act = () => t.TransformAsync((string?)null);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE0700");
    }

    /// <summary>A regex the engine cannot compile escaped with no error code at all (error-1140a).</summary>
    [Fact]
    public Task AnalyzeString_WithAnInvalidRegex_IsXTDE1140()
        => AssertErrorAsync(Wrap("", """<xsl:analyze-string select="'bananas'" regex="[A-Z"><xsl:matching-substring/></xsl:analyze-string>"""), "XTDE1140");

    /// <summary>A format that is not a QName is XTDE1460, not the generic namespace error (error-1460c).</summary>
    [Fact]
    public Task ResultDocument_WithAFormatThatIsNotAQName_IsXTDE1460()
        => AssertErrorAsync(Wrap("""<xsl:output name="xyz" method="xml"/>""", """<xsl:result-document format="not:a:qname"><apple/></xsl:result-document>"""), "XTDE1460");

    /// <summary>
    /// A prefixed key name never expands to no namespace, so key('my:k') must not find a key
    /// declared as plain 'k' — it is XTDE1260 (error-1260e).
    /// </summary>
    [Fact]
    public Task Key_WithAPrefixedNameOfAnUnprefixedKey_IsXTDE1260()
        => AssertErrorAsync(Wrap("""<xsl:key name="k" match="*" use="17"/>""", """<xsl:value-of select="key('my:k', 'abc')"/>"""), "XTDE1260");

    /// <summary>key() with two arguments needs a context node; in a function body there is none (error-1270a).</summary>
    [Fact]
    public Task Key_WithTwoArgumentsAndNoFocus_IsXTDE1270()
        => AssertErrorAsync(Wrap(
            """<xsl:key name="k" match="*" use="'pqr'"/><xsl:function name="my:f"><xsl:sequence select="key('k', 'abc')"/></xsl:function>""",
            """<xsl:value-of select="my:f()"/>"""), "XTDE1270");

    /// <summary>A key that does resolve still answers, and a namespaced key still resolves by prefix.</summary>
    [Fact]
    public async Task KeysThatResolve_StillAnswer()
        => (await RunAsync(Wrap(
            """<xsl:key name="k" match="a" use="@id"/><xsl:key name="my:nk" match="b" use="@id"/>""",
            """<xsl:value-of select="count(key('k', '1')), count(key('my:nk', '2'))"/>"""),
            """<doc><a id="1"/><b id="2"/></doc>""")).Should().Be("1 1");
}
