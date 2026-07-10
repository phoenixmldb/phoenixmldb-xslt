using FluentAssertions;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// W3C attr/mode conformance cases covering default-mode / initial-mode eligibility on
/// xsl:package: a package-level xsl:mode with no explicit visibility is private and hence
/// not an eligible initial mode unless it is the package's default mode (XTDE0045); the
/// unnamed mode is always eligible and invocable via initial-mode="#unnamed".
/// </summary>
public class ModePackageDiagTests
{
    private static async Task<string> Run(string xsl, string mode)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        t.SetInitialMode(mode);
        return await t.TransformAsync("<doc><a>1</a></doc>");
    }

    [Fact]
    public async Task Mode1702_UnnamedInvocableWithPackageDefaultMode()
    {
        // default-mode="a" on the package, but initial-mode="#unnamed" selects the unnamed
        // mode; the template match="/" mode="#unnamed" fires -> <ok/>.
        var xsl = """
            <xsl:package package-version="1.0.0" version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" declared-modes="no" default-mode="a">
              <xsl:mode />
              <xsl:template match="/" mode="#unnamed"><ok/></xsl:template>
            </xsl:package>
            """;
        (await Run(xsl, "#unnamed")).Should().Contain("<ok/>");
    }

    [Fact]
    public async Task Mode1706_UnnamedSelectedOverDefaultMode()
    {
        var xsl = """
            <xsl:package package-version="1.0.0" version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" declared-modes="yes" default-mode="a">
              <xsl:mode name="a" visibility="public" />
              <xsl:mode/>
              <xsl:mode name="private-mode" />
              <xsl:template match="/"><ok/></xsl:template>
              <xsl:template match="/" mode="#unnamed"><ok-unnamed/></xsl:template>
            </xsl:package>
            """;
        (await Run(xsl, "#unnamed")).Should().Contain("<ok-unnamed/>");
    }

    [Fact]
    public async Task Mode1705b_PrivateModeNotEligible()
    {
        var xsl = """
            <xsl:package package-version="1.0.0" version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" declared-modes="yes" default-mode="a">
              <xsl:mode name="a" />
              <xsl:mode/>
              <xsl:mode name="private-mode" />
              <xsl:template match="/"><ok/></xsl:template>
              <xsl:template match="/" mode="#unnamed"><ok-unnamed/></xsl:template>
            </xsl:package>
            """;
        var act = async () => await Run(xsl, "private-mode");
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTDE0045");
    }

    [Fact]
    public async Task Mode1714err_DefaultModeOnTemplateDoesNotMakeModeEligible()
    {
        // default-mode="a" is on a template (not the package root), so it does NOT make
        // mode "a" (private) eligible as an initial mode -> XTDE0045.
        var xsl = """
            <xsl:package package-version="1.0.0" version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:mode name="a"/>
              <xsl:mode/>
              <xsl:template match="/" default-mode="a"><out><xsl:apply-templates select="doc/a"/></out></xsl:template>
              <xsl:template match="a" mode="a"><xsl:text>mode-a:</xsl:text><xsl:value-of select="."/></xsl:template>
              <xsl:template match="a"><xsl:text>no-mode:</xsl:text><xsl:value-of select="."/></xsl:template>
            </xsl:package>
            """;
        var act = async () => await Run(xsl, "a");
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTDE0045");
    }
}
