using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An abstract template or function that a package takes from a package it uses, and keeps
/// abstract, is a component of that package, so a package that uses it in turn can override it
/// (XSLT 3.0 §3.5.3.3). Only its name was kept when the packages were merged, and the override
/// matched nothing: XTSE3058 (W3C override-t-003a). Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class OverrideIndirectAbstractTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-ovrabs-" + Guid.NewGuid().ToString("N"));

    public OverrideIndirectAbstractTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Base = """
        <xsl:package name="urn:base" package-version="1.0" version="3.0"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="#all">
          <xsl:template name="t" visibility="public">[<xsl:call-template name="t-abstract"/>|<xsl:value-of select="f:abstract()"/>]</xsl:template>
          <xsl:template name="t-abstract" visibility="abstract"/>
          <xsl:function name="f:abstract" visibility="abstract"/>
        </xsl:package>
        """;

    private const string Middle = """
        <xsl:package name="urn:middle" package-version="1.0" version="3.0"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="#all">
          <xsl:use-package name="urn:base">
            <xsl:accept component="template" names="t" visibility="public"/>
            <xsl:accept component="template" names="t-abstract" visibility="abstract"/>
            <xsl:accept component="function" names="f:abstract#0" visibility="abstract"/>
          </xsl:use-package>
        </xsl:package>
        """;

    [Fact]
    public async Task A_package_overrides_an_abstract_component_of_the_package_its_used_package_uses()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "base.xsl"), Base, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_dir, "middle.xsl"), Middle, TestContext.Current.CancellationToken);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>>
        {
            ["urn:base"] = new() { ("1.0", Path.Combine(_dir, "base.xsl")) },
            ["urn:middle"] = new() { ("1.0", Path.Combine(_dir, "middle.xsl")) },
        };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:package name="urn:top" package-version="1.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:use-package name="urn:middle">
                <xsl:override>
                  <xsl:template name="t-abstract" visibility="public">template</xsl:template>
                  <xsl:function name="f:abstract" visibility="public"><xsl:sequence select="'function'"/></xsl:function>
                </xsl:override>
              </xsl:use-package>
              <xsl:template name="main" visibility="public"><out><xsl:call-template name="t"/></out></xsl:template>
            </xsl:package>
            """, new Uri(Path.Combine(_dir, "top.xsl")), null, catalog);
        t.SetInitialTemplate("main");
        (await t.TransformAsync("<in/>")).Trim().Should().Be("<out>[template|function]</out>");
    }
}
