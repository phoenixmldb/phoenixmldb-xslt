using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// In an xsl:package a template or function with no visibility attribute is private (XSLT 3.0
/// §3.5), and a private component cannot be overridden: XTSE3060. An unstated visibility reads as
/// public, the default outside a package, so the override was accepted (W3C override-t-010).
/// Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class OverrideDefaultPrivateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-ovrpriv-" + Guid.NewGuid().ToString("N"));

    public OverrideDefaultPrivateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> Run(string libraryComponents, string overrides, string body)
    {
        var libPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libPath, $$"""
            <xsl:package name="urn:lib" package-version="1.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="#all">
              {{libraryComponents}}
            </xsl:package>
            """, TestContext.Current.CancellationToken);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>> { ["urn:lib"] = new() { ("1.0", libPath) } };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:use-package name="urn:lib"><xsl:override>{{overrides}}</xsl:override></xsl:use-package>
              <xsl:template name="xsl:initial-template"><out>{{body}}</out></xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null, catalog);
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("<xsl:template name='a'>lib</xsl:template>", "<xsl:template name='a'>mine</xsl:template>")]
    [InlineData("<xsl:function name='f:g'><xsl:sequence select='1'/></xsl:function>", "<xsl:function name='f:g'><xsl:sequence select='2'/></xsl:function>")]
    public async Task A_component_with_no_visibility_in_a_package_is_private_and_cannot_be_overridden(string library, string overrides)
        => (await FluentActions.Awaiting(() => Run(library, overrides, "x")).Should().ThrowAsync<Exception>())
            .Which.Message.Should().Contain("XTSE3060");

    [Theory]
    [InlineData("<xsl:template name='a' visibility='public'>lib</xsl:template>", "<xsl:template name='a'>mine</xsl:template>", "<xsl:call-template name='a'/>", "<out>mine</out>")]
    [InlineData("<xsl:function name='f:g' visibility='public'><xsl:sequence select='1'/></xsl:function>", "<xsl:function name='f:g'><xsl:sequence select='2'/></xsl:function>", "<xsl:value-of select='f:g()'/>", "<out>2</out>")]
    public async Task A_public_component_is_overridden(string library, string overrides, string body, string expected)
        => (await Run(library, overrides, body)).Should().Be(expected);
}
