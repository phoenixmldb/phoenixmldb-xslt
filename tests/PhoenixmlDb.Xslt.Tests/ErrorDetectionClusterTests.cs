using FluentAssertions;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 packages ERROR-DETECTION cluster: cases where the engine previously
/// silently accepted illegal input (or produced output) instead of raising the
/// required static/dynamic error. Mirrors W3C decl/package and decl/accept
/// conformance cases as self-contained unit tests.
/// <list type="bullet">
///   <item>XTDE0040 — an initial named template (or auto-detected xsl:initial-template)
///     invoked under xsl:package must be explicitly public or final; the package-default
///     private visibility makes it an ineligible entry point.</item>
///   <item>XTSE0500 — a visibility attribute on a template <em>rule</em> (a template with
///     a match pattern) is a static error.</item>
///   <item>XTSE0020 — the reserved token "#unnamed" is not a valid QName for xsl:mode/@name.</item>
///   <item>XTSE0165 — xsl:import may not target an xsl:package (only a stylesheet module).</item>
/// </list>
/// </summary>
public sealed class ErrorDetectionClusterTests : IDisposable
{
    private readonly string _dir;

    public ErrorDetectionClusterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxerrdet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string WritePackage(string fileName, string xml)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, xml);
        return path;
    }

    private static Dictionary<string, List<(string? Version, string FilePath)>> Catalog(
        params (string Name, string Path)[] entries)
    {
        var cat = new Dictionary<string, List<(string? Version, string FilePath)>>();
        foreach (var (name, path) in entries)
            cat[name] = new List<(string?, string)> { ("1.0.0", path) };
        return cat;
    }

    private async Task<XsltTransformer> LoadAsync(
        string principalXsl,
        Dictionary<string, List<(string? Version, string FilePath)>>? catalog = null)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principalXsl, new Uri(Path.Combine(_dir, "principal.xsl")),
            null, catalog);
        return t;
    }

    // ---- package-001a: named template "main" invoked as initial template, private in a package → XTDE0040 ----
    [Fact]
    public async Task InitialTemplate_PrivateNamedInPackage_XTDE0040()
    {
        var principal = """
            <xsl:package version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="main"><not-ok/></xsl:template>
            </xsl:package>
            """;
        Func<Task> act = async () =>
        {
            var t = await LoadAsync(principal);
            t.SetInitialTemplate("main");
            await t.TransformAsync("<in/>");
        };
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTDE0040");
    }

    // ---- package-001b: auto-detected xsl:initial-template private in a package → XTDE0040.
    // Exercised through the facade so the no-source invocation path (which auto-detects
    // xsl:initial-template) is taken, matching the W3C test that supplies no source. ----
    [Fact]
    public async Task InitialTemplate_PrivateXslInitialTemplate_XTDE0040()
    {
        var principal = """
            <xsl:package version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="xsl:initial-template"><not-ok/></xsl:template>
            </xsl:package>
            """;
        Func<Task> act = async () =>
        {
            var facade = new PhoenixmlDb.Xslt.XsltTransformer();
            await facade.LoadStylesheetAsync(principal);
            await facade.TransformAsync((string?)null);
        };
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTDE0040");
    }

    // ---- package-001a is fine when the template is explicitly public ----
    [Fact]
    public async Task InitialTemplate_PublicNamedInPackage_Succeeds()
    {
        var principal = """
            <xsl:package version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="main" visibility="public"><ok/></xsl:template>
            </xsl:package>
            """;
        var t = await LoadAsync(principal);
        t.SetInitialTemplate("main");
        var result = await t.TransformAsync("<in/>");
        result.Should().Contain("<ok");
    }

    // ---- package-001t: visibility on a template RULE (has match) → XTSE0500 ----
    [Fact]
    public async Task TemplateRule_WithVisibility_XTSE0500()
    {
        var principal = """
            <xsl:package version="3.0" declared-modes="false" default-mode="start"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode visibility="public" name="start"/>
              <xsl:template match="." mode="start" visibility="private"><ok/></xsl:template>
            </xsl:package>
            """;
        Func<Task> act = () => LoadAsync(principal);
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE0500");
    }

    // ---- package-909: xsl:mode name="#unnamed" is an invalid token → XTSE0020 ----
    [Fact]
    public async Task Mode_NameUnnamedToken_XTSE0020()
    {
        var principal = """
            <xsl:package package-version="1.0.0" version="3.0" declared-modes="true"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode name="#unnamed"/>
            </xsl:package>
            """;
        Func<Task> act = () => LoadAsync(principal);
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE0020");
    }

    // ---- package-914a: empty package invoked with no source, no initial template/
    // function/mode → nothing to apply templates to → XTDE0040 (any-of XTDE0040/0044) ----
    [Fact]
    public async Task EmptyPackage_NoSourceNoEntryPoint_XTDE0040()
    {
        var principal = """
            <xsl:package xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"/>
            """;
        Func<Task> act = async () =>
        {
            var facade = new PhoenixmlDb.Xslt.XsltTransformer();
            await facade.LoadStylesheetAsync(principal);
            await facade.TransformAsync((string?)null);
        };
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should()
            .Match(m => m.Contains("XTDE0040") || m.Contains("XTDE0044"));
    }

    // ---- package-001j: an implicit mode (declared-modes="false") explicitly exposed as
    // private is not eligible as an initial mode → XTDE0045 ----
    [Fact]
    public async Task InitialMode_ImplicitModeExposedPrivate_XTDE0045()
    {
        var principal = """
            <xsl:package version="3.0" declared-modes="false" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:expose component="mode" names="*" visibility="private"/>
              <xsl:template match="." mode="start"><not-ok/></xsl:template>
            </xsl:package>
            """;
        Func<Task> act = async () =>
        {
            var facade = new PhoenixmlDb.Xslt.XsltTransformer();
            await facade.LoadStylesheetAsync(principal);
            facade.SetInitialMode("start");
            await facade.TransformAsync("<in/>");
        };
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTDE0045");
    }

    // ---- Guard: an implicitly-private mode NOT named by any expose remains ELIGIBLE ----
    [Fact]
    public async Task InitialMode_ImplicitModeNotExposed_Succeeds()
    {
        var principal = """
            <xsl:package version="3.0" declared-modes="false" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="." mode="start"><ok/></xsl:template>
            </xsl:package>
            """;
        var facade = new PhoenixmlDb.Xslt.XsltTransformer();
        await facade.LoadStylesheetAsync(principal);
        facade.SetInitialMode("start");
        var result = await facade.TransformAsync("<in/>");
        result.Should().Contain("<ok");
    }

    // ---- package-910: xsl:import targeting an xsl:package → XTSE0165 ----
    [Fact]
    public async Task Import_TargetingPackage_XTSE0165()
    {
        var imported = WritePackage("imported-pkg.xsl", """
            <xsl:package xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                declared-modes="false" version="3.0"/>
            """);
        var principal = $"""
            <xsl:package xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                declared-modes="false" version="3.0">
              <xsl:import href="{new Uri(imported).AbsoluteUri}"/>
            </xsl:package>
            """;
        Func<Task> act = () => LoadAsync(principal);
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE0165");
    }
}
