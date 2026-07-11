using FluentAssertions;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Cross-package template/mode composition: template rules declared in a module that is
/// pulled into the principal package via xsl:include (possibly several levels deep, and
/// alongside xsl:use-package in the same module) must participate in the principal
/// package's unnamed-mode dispatch. Mirrors W3C decl/package cases package-019..022 and
/// the xsl:import-on-a-package precedence case package-015.
/// </summary>
public sealed class PackageIncludeModeCompositionTests : IDisposable
{
    private readonly string _dir;

    public PackageIncludeModeCompositionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxpkginc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void Write(string fileName, string xml) => File.WriteAllText(Path.Combine(_dir, fileName), xml);

    private const string Input = """
        <doc>
            <first-child att="some attribute" />
            <second-child>Some random input file</second-child>
        </doc>
        """;

    private const string UsedPackage = """
        <xsl:package package-version="0.1" name="urn:use-me"
            xmlns:me="urn:use-me" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
          <xsl:mode on-no-match="fail" />
          <xsl:function name="me:function1" visibility="public">You found me!</xsl:function>
          <xsl:function name="me:function2" visibility="public" />
        </xsl:package>
        """;

    private async Task<string> RunAsync(string principalFile)
    {
        var t = new XsltTransformer();
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>>
        {
            ["urn:use-me"] = new() { ("0.1", Path.Combine(_dir, "used.xsl")) }
        };
        await t.LoadStylesheetAsync(
            File.ReadAllText(Path.Combine(_dir, principalFile)),
            new Uri(Path.Combine(_dir, principalFile)),
            null, catalog);
        return await t.TransformAsync(Input);
    }

    // ---- package-019: use-package + template in a single included module ----
    [Fact]
    public async Task Package019_TemplateInIncludedModuleWithUsePackageParticipatesInMode()
    {
        Write("used.xsl", UsedPackage);
        Write("principal.xsl", """
            <xsl:package package-version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:pkg="urn:use-me" version="3.0">
              <xsl:mode on-no-match="text-only-copy" />
              <xsl:include href="include.xsl"/>
            </xsl:package>
            """);
        Write("include.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:pkg="urn:use-me" version="3.0">
              <xsl:use-package name="urn:use-me" package-version="*">
                <xsl:accept component="function" names="pkg:function1#0" visibility="public" />
              </xsl:use-package>
              <xsl:template match="second-child">
                <xsl:value-of select="pkg:function1()" />
              </xsl:template>
            </xsl:stylesheet>
            """);
        (await RunAsync("principal.xsl")).Should().Contain("You found me!");
    }

    // ---- package-020: 3-level-deep include chain, use-package + template in deepest ----
    [Fact]
    public async Task Package020_TemplateInDeeplyIncludedModuleParticipatesInMode()
    {
        Write("used.xsl", UsedPackage);
        Write("principal.xsl", """
            <xsl:package package-version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:pkg="urn:use-me" version="3.0">
              <xsl:mode on-no-match="text-only-copy" />
              <xsl:include href="includeA.xsl"/>
            </xsl:package>
            """);
        Write("includeA.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:pkg="urn:use-me" version="3.0">
              <xsl:include href="includeB.xsl"/>
            </xsl:stylesheet>
            """);
        Write("includeB.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:pkg="urn:use-me" version="3.0">
              <xsl:include href="includeC.xsl"/>
            </xsl:stylesheet>
            """);
        Write("includeC.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:pkg="urn:use-me" version="3.0">
              <xsl:use-package name="urn:use-me" package-version="*">
                <xsl:accept component="function" names="pkg:function1#0" visibility="public" />
              </xsl:use-package>
              <xsl:template match="second-child">
                <xsl:value-of select="pkg:function1()" />
              </xsl:template>
            </xsl:stylesheet>
            """);
        (await RunAsync("principal.xsl")).Should().Contain("You found me!");
    }

    // ---- package-001p: initial-mode #default resolves to a private @default-mode named mode ----
    [Fact]
    public async Task Package001p_DefaultInitialModeResolvesToNamedPrivateDefaultMode()
    {
        Write("principal.xsl", """
            <xsl:package version="3.0" declared-modes="false" default-mode="start"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode name="start" visibility="private" />
              <xsl:template match="." mode="start"><ok><xsl:value-of select="." /></ok></xsl:template>
            </xsl:package>
            """);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(File.ReadAllText(Path.Combine(_dir, "principal.xsl")),
            new Uri(Path.Combine(_dir, "principal.xsl")), null, null);
        t.SetInitialMode("#default");
        t.SetInitialModeSelect("42");
        (await t.TransformAsync("<dummy/>")).Should().Contain("<ok>42</ok>");
    }

    // ---- package-012 (package-011.xsl): #default -> named default-mode with shallow-skip ----
    [Fact]
    public async Task Package012_DefaultInitialModeUsesShallowSkipDefaultMode()
    {
        Write("principal.xsl", """
            <xsl:package package-version="1.0.0" version="3.0" default-mode="output-nothing"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode name="output-nothing" on-no-match="shallow-skip" visibility="public"/>
            </xsl:package>
            """);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(File.ReadAllText(Path.Combine(_dir, "principal.xsl")),
            new Uri(Path.Combine(_dir, "principal.xsl")), null, null);
        t.SetInitialMode("#default");
        (await t.TransformAsync(Input)).Trim().Should().BeEmpty();
    }

    // ---- #unnamed still selects the unnamed mode even when @default-mode names a mode ----
    [Fact]
    public async Task UnnamedInitialModeIgnoresNamedDefaultMode()
    {
        Write("principal.xsl", """
            <xsl:package version="3.0" declared-modes="false" default-mode="start"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode name="start" visibility="private" />
              <xsl:template match="second-child" mode="start"><named/></xsl:template>
              <xsl:template match="second-child" mode="#unnamed"><unnamed/></xsl:template>
            </xsl:package>
            """);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(File.ReadAllText(Path.Combine(_dir, "principal.xsl")),
            new Uri(Path.Combine(_dir, "principal.xsl")), null, null);
        t.SetInitialMode("#unnamed");
        (await t.TransformAsync(Input)).Should().Contain("<unnamed/>");
    }

    // ---- package-015: xsl:import on a package; package's own xsl:mode wins by precedence ----
    [Fact]
    public async Task Package015_PackageOwnUnnamedModeOverridesImportedFailMode()
    {
        Write("principal.xsl", """
            <xsl:package package-version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                declared-modes="false" version="3.0">
              <xsl:mode on-no-match="text-only-copy" />
              <xsl:import href="import.xsl" />
            </xsl:package>
            """);
        Write("import.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode on-no-match="fail" />
            </xsl:stylesheet>
            """);
        (await RunAsync("principal.xsl")).Should().Contain("Some random input file");
    }
}
