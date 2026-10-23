using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:expose component="*" selects every component kind, so its names must be wildcards
/// (XSLT 3.0 §3.6.3.1, XTSE3022). The check did not exist: an explicit name was accepted, and a
/// package that also had a visibility conflict reported XTSE3010 instead (W3C expose-914).
/// </summary>
public sealed class ExposeComponentWildcardTests
{
    private static Task CompileAsync(string expose)
        => new XsltTransformer().LoadStylesheetAsync($"""
            <xsl:package name="urn:p" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p">
              <xsl:function name="p:f1" visibility="public"><xsl:sequence select="0"/></xsl:function>
              {expose}
            </xsl:package>
            """);

    [Theory]
    [InlineData("p:f1")]
    [InlineData("p:* p:f1")]
    public async Task An_explicit_name_with_component_star_is_XTSE3022(string names)
    {
        var act = () => CompileAsync($"""<xsl:expose component="*" names="{names}" visibility="private"/>""");
        (await act.Should().ThrowAsync<XsltException>()).WithMessage("*XTSE3022*");
    }

    [Theory]
    [InlineData("*")]
    [InlineData("p:*")]
    [InlineData("*:f1")]
    public async Task A_wildcard_with_component_star_is_allowed(string names)
        => await CompileAsync($"""<xsl:expose component="*" names="{names}" visibility="private"/>""");
}
