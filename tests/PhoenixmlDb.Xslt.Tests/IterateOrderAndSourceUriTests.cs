using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Two rules that were reported as whatever the code tripped over first: the order of children in
/// xsl:iterate, and an href that is not a URI reference at all.
/// </summary>
public sealed class IterateOrderAndSourceUriTests
{
    private static async Task<string> RunAsync(string body, string source = "<doc><a/><a/></doc>", Uri? baseUri = null)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss, baseUri);
        return (await t.TransformAsync(source)).Trim();
    }

    private static async Task AssertErrorAsync(string body, string code, Uri? baseUri = null)
    {
        var act = () => RunAsync(body, baseUri: baseUri);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain(code);
    }

    /// <summary>
    /// xsl:param must come first. A param whose select reads a variable declared above it was
    /// reported as "$x is not defined" — the consequence, not the mistake (W3C iterate-008/901).
    /// </summary>
    [Fact]
    public Task Iterate_WithAParamAfterOtherContent_IsXTSE0010()
        => AssertErrorAsync("""
            <xsl:iterate select="//a">
              <xsl:variable name="x" select="29"/>
              <xsl:param name="total" as="xs:decimal" select="$x"/>
              <xsl:next-iteration><xsl:with-param name="total" select="$total + 1"/></xsl:next-iteration>
            </xsl:iterate>
            """, "XTSE0010");

    [Fact]
    public Task Iterate_WithOnCompletionAfterTheBody_IsXTSE0010()
        => AssertErrorAsync("""
            <xsl:iterate select="//a">
              <x/>
              <xsl:on-completion>done</xsl:on-completion>
            </xsl:iterate>
            """, "XTSE0010");

    [Fact]
    public Task Iterate_WithTwoOnCompletions_IsXTSE0010()
        => AssertErrorAsync("""
            <xsl:iterate select="//a">
              <xsl:on-completion>one</xsl:on-completion>
              <xsl:on-completion>two</xsl:on-completion>
            </xsl:iterate>
            """, "XTSE0010");

    [Fact]
    public async Task Iterate_InTheRightOrder_StillRuns()
        => (await RunAsync("""
            <xsl:iterate select="//a">
              <xsl:param name="n" select="0"/>
              <xsl:on-completion><xsl:value-of select="$n"/></xsl:on-completion>
              <xsl:next-iteration><xsl:with-param name="n" select="$n + 1"/></xsl:next-iteration>
            </xsl:iterate>
            """)).Should().Be("2");

    /// <summary>
    /// A href that is not a URI reference — a Windows path — is FODC0005 (an invalid argument),
    /// not FODC0002 (a valid URI that cannot be retrieved). The backslashes were rewritten into a
    /// file: URI, which then reported "document not found" (W3C stream-006, non-stream-006).
    /// </summary>
    [Fact]
    public Task SourceDocument_WithAHrefThatIsNotAUri_IsFODC0005()
        => AssertErrorAsync("""<xsl:source-document href="c:\my\doc\books.xml"><x/></xsl:source-document>""", "FODC0005");

    [Fact]
    public Task SourceDocument_WithAValidUriThatIsMissing_IsStillFODC0002()
        => AssertErrorAsync("""<xsl:source-document href="absent-document.xml"><x/></xsl:source-document>""", "FODC0002",
            new Uri(Path.Combine(Path.GetTempPath(), "phoenixml-absent-base.xsl")));
}
