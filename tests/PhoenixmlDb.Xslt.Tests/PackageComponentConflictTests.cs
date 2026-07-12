using FluentAssertions;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XTSE3050 cross-package component-conflict detection (XSLT 3.0 §3.6.2). A package must not
/// contain two distinct components of the same kind and name that are both accepted (visible)
/// from used packages, unless one overrides the other or one is not exposed. Two scenarios are
/// exercised, mirroring W3C decl/accept-020 (two independent used packages each exposing the
/// same unnamespaced symbol) and decl/package-022err (the "diamond": the same package reached
/// via an xsl:include chain plus another xsl:use-package, with an accept that leaves a symbol
/// visible on both routes). Guards verify legitimate configurations do NOT raise: a symbol
/// resolved by xsl:override, a symbol exposed by only one package, and the same package used
/// twice where the overlap is hidden in one route (decl/package-022 non-error variant).
/// </summary>
public sealed class PackageComponentConflictTests : IDisposable
{
    private readonly string _dir;

    public PackageComponentConflictTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxpkgconflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private void Write(string fileName, string xml) => File.WriteAllText(Path.Combine(_dir, fileName), xml);

    private static Dictionary<string, List<(string? Version, string FilePath)>> Catalog(
        params (string Name, string Path)[] entries)
    {
        var cat = new Dictionary<string, List<(string? Version, string FilePath)>>();
        foreach (var (name, path) in entries)
            cat[name] = new List<(string?, string)> { ("1.0.0", path) };
        return cat;
    }

    private async Task LoadPrincipalAsync(
        string principalFile,
        Dictionary<string, List<(string? Version, string FilePath)>>? catalog)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(
            File.ReadAllText(Path.Combine(_dir, principalFile)),
            new Uri(Path.Combine(_dir, principalFile)),
            null, catalog);
    }

    // ---- accept-020: two used packages each expose the same unnamespaced symbol -> XTSE3050 ----
    [Fact]
    public async Task TwoUsedPackages_ExposeSameUnnamespacedVariable_XTSE3050()
    {
        Write("pkgA.xsl", """
            <xsl:package name="urn:conf-a" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="dup" select="1" visibility="public"/>
            </xsl:package>
            """);
        Write("pkgB.xsl", """
            <xsl:package name="urn:conf-b" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="dup" select="2" visibility="public"/>
            </xsl:package>
            """);
        Write("principal.xsl", """
            <xsl:package name="urn:principal" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:use-package name="urn:conf-a"/>
              <xsl:use-package name="urn:conf-b"/>
              <xsl:template name="main" visibility="public"><out/></xsl:template>
            </xsl:package>
            """);

        var catalog = Catalog(
            ("urn:conf-a", Path.Combine(_dir, "pkgA.xsl")),
            ("urn:conf-b", Path.Combine(_dir, "pkgB.xsl")));

        Func<Task> act = () => LoadPrincipalAsync("principal.xsl", catalog);
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE3050");
    }

    // ---- accept-020 variant: exposure via xsl:expose names="*" (VisibilityAttr not propagated) ----
    [Fact]
    public async Task TwoUsedPackages_ExposeViaWildcardExpose_XTSE3050()
    {
        Write("pkgA.xsl", """
            <xsl:package name="urn:conf-a" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:expose component="variable" names="*" visibility="public"/>
              <xsl:variable name="dup" select="1"/>
            </xsl:package>
            """);
        Write("pkgB.xsl", """
            <xsl:package name="urn:conf-b" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:expose component="variable" names="*" visibility="public"/>
              <xsl:variable name="dup" select="2"/>
            </xsl:package>
            """);
        Write("principal.xsl", """
            <xsl:package name="urn:principal" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:use-package name="urn:conf-a"/>
              <xsl:use-package name="urn:conf-b"/>
              <xsl:template name="main" visibility="public"><out/></xsl:template>
            </xsl:package>
            """);

        var catalog = Catalog(
            ("urn:conf-a", Path.Combine(_dir, "pkgA.xsl")),
            ("urn:conf-b", Path.Combine(_dir, "pkgB.xsl")));

        Func<Task> act = () => LoadPrincipalAsync("principal.xsl", catalog);
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE3050");
    }

    // ---- package-022err: diamond via xsl:include + a second use-package, function visible on both ----
    [Fact]
    public async Task DiamondViaInclude_FunctionVisibleOnBothRoutes_XTSE3050()
    {
        Write("used.xsl", """
            <xsl:package name="urn:use-me" package-version="0.1" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:me="urn:use-me">
              <xsl:function name="me:function1" visibility="public">You found me!</xsl:function>
              <xsl:function name="me:function2" visibility="public"/>
            </xsl:package>
            """);
        // includeB accepts function1 as private (still a component) then includes includeC
        Write("includeB.xsl", """
            <xsl:stylesheet version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:pkg="urn:use-me">
              <xsl:use-package name="urn:use-me" package-version="*">
                <xsl:accept component="function" names="pkg:function1#0" visibility="private"/>
                <xsl:accept component="function" names="pkg:function2#0" visibility="private"/>
              </xsl:use-package>
              <xsl:include href="includeC.xsl"/>
            </xsl:stylesheet>
            """);
        // includeC: first use-package hides both; second accepts function1 as public -> conflict with includeB
        Write("includeC.xsl", """
            <xsl:stylesheet version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:pkg="urn:use-me">
              <xsl:use-package name="urn:use-me" package-version="*">
                <xsl:accept component="function" names="pkg:function1#0" visibility="hidden"/>
                <xsl:accept component="function" names="pkg:function2#0" visibility="hidden"/>
              </xsl:use-package>
              <xsl:use-package name="urn:use-me" package-version="*">
                <xsl:accept component="function" names="pkg:function1#0" visibility="public"/>
                <xsl:accept component="function" names="pkg:function2#0" visibility="hidden"/>
              </xsl:use-package>
            </xsl:stylesheet>
            """);
        Write("principal.xsl", """
            <xsl:package package-version="1.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:pkg="urn:use-me">
              <xsl:include href="includeB.xsl"/>
            </xsl:package>
            """);

        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>>
        {
            ["urn:use-me"] = new() { ("0.1", Path.Combine(_dir, "used.xsl")) }
        };

        Func<Task> act = () => LoadPrincipalAsync("principal.xsl", catalog);
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE3050");
    }

    // ---- GUARD: package-022 non-error variant — overlap hidden in one route -> NO conflict ----
    [Fact]
    public async Task DiamondViaInclude_OverlapHiddenInOneRoute_NoConflict()
    {
        Write("used.xsl", """
            <xsl:package name="urn:use-me" package-version="0.1" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:me="urn:use-me">
              <xsl:function name="me:function1" visibility="public">You found me!</xsl:function>
              <xsl:function name="me:function2" visibility="public"/>
            </xsl:package>
            """);
        Write("includeB.xsl", """
            <xsl:stylesheet version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:pkg="urn:use-me">
              <xsl:use-package name="urn:use-me" package-version="*">
                <xsl:accept component="function" names="pkg:function1#0" visibility="hidden"/>
              </xsl:use-package>
              <xsl:include href="includeC.xsl"/>
            </xsl:stylesheet>
            """);
        Write("includeC.xsl", """
            <xsl:stylesheet version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:pkg="urn:use-me">
              <xsl:use-package name="urn:use-me" package-version="*">
                <xsl:accept component="function" names="pkg:function1#0" visibility="hidden"/>
                <xsl:accept component="function" names="pkg:function2#0" visibility="hidden"/>
              </xsl:use-package>
              <xsl:use-package name="urn:use-me" package-version="*">
                <xsl:accept component="function" names="pkg:function1#0" visibility="public"/>
                <xsl:accept component="function" names="*" visibility="hidden"/>
              </xsl:use-package>
            </xsl:stylesheet>
            """);
        Write("principal.xsl", """
            <xsl:package package-version="1.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:pkg="urn:use-me">
              <xsl:include href="includeB.xsl"/>
            </xsl:package>
            """);

        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>>
        {
            ["urn:use-me"] = new() { ("0.1", Path.Combine(_dir, "used.xsl")) }
        };

        Func<Task> act = () => LoadPrincipalAsync("principal.xsl", catalog);
        await act.Should().NotThrowAsync();
    }

    // ---- GUARD: same-named symbol exposed by only ONE package -> NO conflict ----
    [Fact]
    public async Task SameName_ExposedByOnlyOnePackage_NoConflict()
    {
        Write("pkgA.xsl", """
            <xsl:package name="urn:conf-a" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="dup" select="1" visibility="public"/>
            </xsl:package>
            """);
        // pkgB declares dup private (the package default is private too) -> not exposed
        Write("pkgB.xsl", """
            <xsl:package name="urn:conf-b" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="dup" select="2" visibility="private"/>
              <xsl:variable name="onlyb" select="3" visibility="public"/>
            </xsl:package>
            """);
        Write("principal.xsl", """
            <xsl:package name="urn:principal" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:use-package name="urn:conf-a"/>
              <xsl:use-package name="urn:conf-b"/>
              <xsl:template name="main" visibility="public"><out/></xsl:template>
            </xsl:package>
            """);

        var catalog = Catalog(
            ("urn:conf-a", Path.Combine(_dir, "pkgA.xsl")),
            ("urn:conf-b", Path.Combine(_dir, "pkgB.xsl")));

        Func<Task> act = () => LoadPrincipalAsync("principal.xsl", catalog);
        await act.Should().NotThrowAsync();
    }

    // ---- GUARD: conflict resolved by xsl:override in one use-package -> NO conflict ----
    [Fact]
    public async Task SameName_ResolvedByOverride_NoConflict()
    {
        Write("pkgA.xsl", """
            <xsl:package name="urn:conf-a" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:conf-a" exclude-result-prefixes="xs p">
              <xsl:function name="p:dup" as="xs:integer" visibility="abstract">
                <xsl:param name="x" as="xs:integer"/>
              </xsl:function>
            </xsl:package>
            """);
        // Principal overrides conf-a's abstract p:dup. The override supplies the component;
        // the new cross-package conflict detection must not treat the overridden symbol as a
        // second contribution (override is the XTSE3050 exception).
        Write("principal.xsl", """
            <xsl:package name="urn:principal" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:conf-a" exclude-result-prefixes="xs p">
              <xsl:use-package name="urn:conf-a">
                <xsl:override>
                  <xsl:function name="p:dup" as="xs:integer" visibility="public">
                    <xsl:param name="x" as="xs:integer"/>
                    <xsl:sequence select="$x + 1"/>
                  </xsl:function>
                </xsl:override>
              </xsl:use-package>
              <xsl:template name="main" visibility="public"><out/></xsl:template>
            </xsl:package>
            """);

        var catalog = Catalog(
            ("urn:conf-a", Path.Combine(_dir, "pkgA.xsl")));

        Func<Task> act = () => LoadPrincipalAsync("principal.xsl", catalog);
        await act.Should().NotThrowAsync();
    }
}
