using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Whitespace-control declarations reached through <c>xsl:use-package</c> carry the import
/// precedence of the package that declared them. XSLT 1.0 §3.4, carried into 3.0 §4.4, resolves a
/// conflict by import precedence FIRST and only then by NameTest default priority, and XSLT 3.0
/// §3.5.1 puts components supplied by a used package below the using package's own.
///
/// #141 gave these declarations a precedence but stamped every used-package declaration with the
/// absolute level 1, so a package used by a package tied with a directly used one (#142). The fix
/// mirrors what template rules already do: shift the package's existing levels by one when it is
/// used, which composes across nesting.
///
/// These tests count text nodes rather than comparing serialized output, because the conformance
/// suite's <c>assert-xml</c> cannot see whitespace-only content at all (#140).
/// </summary>
public sealed class PackageWhitespacePrecedenceTests : IDisposable
{
    private readonly string _dir;

    public PackageWhitespacePrecedenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxpkgws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string Write(string fileName, string xml)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, xml);
        return path;
    }

    private static Dictionary<string, List<(string? Version, string FilePath)>> Cat(
        params (string Name, string Path)[] entries)
    {
        var cat = new Dictionary<string, List<(string?, string)>>();
        foreach (var (name, path) in entries)
            cat[name] = new List<(string?, string)> { ("1.0.0", path) };
        return cat;
    }

    private const string Source = """
        <doc xmlns="http://docbook.org/ns/docbook">
          <info>i</info>
          <section>s</section>
        </doc>
        """;

    /// <summary>Counts the section's preceding siblings: "1/0" when db:doc was stripped, "3/2" when not.</summary>
    private async Task<string> RunAsync(string principal,
        Dictionary<string, List<(string? Version, string FilePath)>> catalog)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principal, new Uri(Path.Combine(_dir, "main-ws.xsl")), null, catalog);
        t.SetInitialTemplate("main");
        return (await t.TransformAsync(Source)).Trim();
    }

    private const string CountingMain = """
          <xsl:template name="main" visibility="public">
            <xsl:variable name="sec" select="//db:section"/>
            <xsl:variable name="nodes" select="$sec/preceding-sibling::node()"/>
            <xsl:value-of select="count($nodes) || '/' || count($nodes[self::text()])"/>
          </xsl:template>
        """;

    /// <summary>
    /// The nested case #142 describes. The deeper package declares the MORE specific
    /// <c>preserve-space db:doc</c>; the nearer one declares the LESS specific
    /// <c>strip-space db:*</c>. Precedence decides before specificity, and the nearer package wins,
    /// so the whitespace is stripped.
    ///
    /// Specificity is deliberately set against depth. With both declarations equally specific the
    /// tie-break would strip anyway and the test would pass without the fix, gating nothing.
    /// </summary>
    [Fact]
    public async Task NearerPackage_OutranksDeeperPackage_EvenWhenDeeperIsMoreSpecific()
    {
        var deep = Write("ws-deep.xsl", """
            <xsl:package name="urn:ws-deep" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:db="http://docbook.org/ns/docbook">
              <xsl:preserve-space elements="db:doc"/>
            </xsl:package>
            """);
        var near = Write("ws-near.xsl", """
            <xsl:package name="urn:ws-near" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:db="http://docbook.org/ns/docbook">
              <xsl:use-package name="urn:ws-deep" package-version="1.0.0"/>
              <xsl:strip-space elements="db:*"/>
            </xsl:package>
            """);
        var principal = $$"""
            <xsl:package name="urn:ws-main" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:db="http://docbook.org/ns/docbook">
              <xsl:use-package name="urn:ws-near" package-version="1.0.0"/>
              <xsl:output method="text"/>
            {{CountingMain}}
            </xsl:package>
            """;
        (await RunAsync(principal, Cat(("urn:ws-deep", deep), ("urn:ws-near", near))))
            .Should().Be("1/0");
    }

    /// <summary>
    /// Guard: the using package's own declaration still outranks a used package's, even when the
    /// used one is more specific. This held before #142 and must keep holding.
    /// </summary>
    [Fact]
    public async Task UsingPackageOwnDeclaration_OutranksMoreSpecificUsedPackageDeclaration()
    {
        var used = Write("ws-used.xsl", """
            <xsl:package name="urn:ws-used" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:db="http://docbook.org/ns/docbook">
              <xsl:strip-space elements="db:doc"/>
            </xsl:package>
            """);
        var principal = $$"""
            <xsl:package name="urn:ws-main2" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:db="http://docbook.org/ns/docbook">
              <xsl:use-package name="urn:ws-used" package-version="1.0.0"/>
              <xsl:preserve-space elements="db:*"/>
              <xsl:output method="text"/>
            {{CountingMain}}
            </xsl:package>
            """;
        (await RunAsync(principal, Cat(("urn:ws-used", used)))).Should().Be("3/2");
    }

    /// <summary>Guard: a single used package's declaration still applies when nothing competes.</summary>
    [Fact]
    public async Task UsedPackageDeclaration_IsAppliedWhenUncontested()
    {
        var used = Write("ws-solo.xsl", """
            <xsl:package name="urn:ws-solo" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:db="http://docbook.org/ns/docbook">
              <xsl:strip-space elements="db:doc"/>
            </xsl:package>
            """);
        var principal = $$"""
            <xsl:package name="urn:ws-main3" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:db="http://docbook.org/ns/docbook">
              <xsl:use-package name="urn:ws-solo" package-version="1.0.0"/>
              <xsl:output method="text"/>
            {{CountingMain}}
            </xsl:package>
            """;
        (await RunAsync(principal, Cat(("urn:ws-solo", used)))).Should().Be("1/0");
    }
}

