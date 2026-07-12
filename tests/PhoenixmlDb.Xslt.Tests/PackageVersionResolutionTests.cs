using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 <c>xsl:use-package/@package-version</c> version-range resolution: when several
/// available package versions satisfy the requested range, the engine must select per the
/// configured <see cref="PackageVersionResolution"/> policy (highest / lowest) using the
/// spec's component-wise numeric ordering with pre-release qualifiers ordered BEFORE the
/// same version without a qualifier (<c>2.0.0-alpha</c> &lt; <c>2.0.0-beta</c> &lt; <c>2.0.0</c>).
/// Mirrors W3C decl/use-package cases 201-210 as self-contained unit tests. Base package
/// available at versions 1.0.0, 2.0.0, 3.5.4, 2.0.0-alpha, 2.0.0-beta (as in use-package-env-004).
/// </summary>
public sealed class PackageVersionResolutionTests : IDisposable
{
    private const string BaseName = "urn:pkgver-base";
    private readonly string _dir;

    public PackageVersionResolutionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxpkgver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private string WriteBase(string version)
    {
        var xml = $"""
            <xsl:package name="{BaseName}" package-version="{version}" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:variable name="package-version" as="xs:string" visibility="public" select="'{version}'"/>
            </xsl:package>
            """;
        var path = Path.Combine(_dir, "base-" + version.Replace('.', '_') + ".xsl");
        File.WriteAllText(path, xml);
        return path;
    }

    /// <summary>Builds a catalog mirroring use-package-env-004 (five versions of the base package).</summary>
    private Dictionary<string, List<(string? Version, string FilePath)>> Env004Catalog()
    {
        var entries = new List<(string?, string)>
        {
            ("1.0.0", WriteBase("1.0.0")),
            ("2.0.0", WriteBase("2.0.0")),
            ("3.5.4", WriteBase("3.5.4")),
            ("2.0.0-alpha", WriteBase("2.0.0-alpha")),
            ("2.0.0-beta", WriteBase("2.0.0-beta")),
        };
        return new Dictionary<string, List<(string? Version, string FilePath)>> { [BaseName] = entries };
    }

    private async Task<string> ResolveAsync(string requestedRange, PackageVersionResolution resolution)
    {
        var principal = $"""
            <xsl:package name="urn:pkgver-main" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="{BaseName}" package-version="{requestedRange}"/>
              <xsl:template name="main" visibility="public">
                <xsl:value-of select="$package-version"/>
              </xsl:template>
            </xsl:package>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principal, new Uri(Path.Combine(_dir, "principal.xsl")),
            null, Env004Catalog(), resolution);
        t.SetInitialTemplate("main");
        return (await t.TransformAsync("<in/>")).Trim();
    }

    // ---- Version list "1.0.0, 2.0" (use-package-203a / 203b) ----
    [Fact]
    public async Task List_Highest_PicksTwoZero()
        => (await ResolveAsync("1.0.0, 2.0", PackageVersionResolution.Highest)).Should().Contain("2.0.0");

    [Fact]
    public async Task List_Lowest_PicksOneZero()
        => (await ResolveAsync("1.0.0, 2.0", PackageVersionResolution.Lowest)).Should().Contain("1.0.0");

    // ---- Range "1.5 to 2.5" (use-package-204b) ----
    [Fact]
    public async Task ToRange_Lowest_PicksPreReleaseAlpha()
        => (await ResolveAsync("1.5 to 2.5", PackageVersionResolution.Lowest)).Should().Contain("2.0.0-alpha");

    [Fact]
    public async Task ToRange_Highest_PicksReleaseOverPreRelease()
        => (await ResolveAsync("1.5 to 2.5", PackageVersionResolution.Highest)).Should().Be("2.0.0");

    // ---- Minimum "1.5+" (use-package-206a / 206b) ----
    [Fact]
    public async Task Minimum_Highest_PicksThreeFiveFour()
        => (await ResolveAsync("1.5+", PackageVersionResolution.Highest)).Should().Contain("3.5.4");

    [Fact]
    public async Task Minimum_Lowest_PicksPreReleaseAlpha()
        => (await ResolveAsync("1.5+", PackageVersionResolution.Lowest)).Should().Contain("2.0.0-alpha");

    // ---- Pre-release range "2.0.0-a to 2.0.0-gamma" (use-package-210a) ----
    [Fact]
    public async Task PreReleaseRange_Highest_PicksBetaNotAlphaNotRelease()
        => (await ResolveAsync("2.0.0-a to 2.0.0-gamma", PackageVersionResolution.Highest)).Should().Contain("2.0.0-beta");

    [Fact]
    public async Task PreReleaseRange_Lowest_PicksAlpha()
        => (await ResolveAsync("2.0.0-a to 2.0.0-gamma", PackageVersionResolution.Lowest)).Should().Contain("2.0.0-alpha");

    // ---- Default (no requested version = '*') picks highest overall ----
    [Fact]
    public async Task Wildcard_Highest_PicksHighestOverall()
        => (await ResolveAsync("*", PackageVersionResolution.Highest)).Should().Contain("3.5.4");

    [Fact]
    public async Task Wildcard_Lowest_PicksLowestOverall()
        => (await ResolveAsync("*", PackageVersionResolution.Lowest)).Should().Contain("1.0.0");

    // ---- Unspecified resolves deterministically to the highest match ----
    [Fact]
    public async Task Unspecified_PicksHighestMatch()
        => (await ResolveAsync("1.5+", PackageVersionResolution.Unspecified)).Should().Contain("3.5.4");

    // ---- Exact pre-release match (use-package-208) ----
    [Fact]
    public async Task Exact_PreRelease_PicksThatVersion()
        => (await ResolveAsync("2.0.0-alpha", PackageVersionResolution.Highest)).Should().Contain("2.0.0-alpha");
}
