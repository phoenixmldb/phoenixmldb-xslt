using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Cross-package template-rule import precedence (XSLT 3.0 §6.6.2 conflict
/// resolution): a template rule supplied by a using package (including one inside
/// xsl:override) has HIGHER import precedence than a rule brought in via
/// xsl:use-package. Import precedence dominates priority, so an override rule wins
/// over a used-package rule even when the used-package rule has higher priority.
/// Mirrors W3C decl/use-package cases 170-173.
/// </summary>
public sealed class PackageTemplateImportPrecedenceTests : IDisposable
{
    private readonly string _dir;

    public PackageTemplateImportPrecedenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxpkgprec-" + Guid.NewGuid().ToString("N"));
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

    private async Task<string> RunAsync(
        string principalXsl,
        string input,
        Dictionary<string, List<(string? Version, string FilePath)>> catalog)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principalXsl, new Uri(Path.Combine(_dir, "principal.xsl")),
            null, catalog);
        return await t.TransformAsync(input);
    }

    // Override rule (default priority 0.5) must beat a used-package rule with an
    // explicit higher priority (2) — import precedence dominates priority.
    [Fact]
    public async Task Override_Rule_Beats_HigherPriority_Used_Rule()
    {
        var used = WritePackage("used.xsl", """
            <xsl:package name="urn:used" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode name="m" visibility="public"/>
              <xsl:template match="a" mode="m" priority="2">
                <used/>
              </xsl:template>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:use-package name="urn:used" package-version="1.0.0">
                <xsl:accept component="mode" names="m" visibility="public"/>
                <xsl:override>
                  <xsl:template match="a" mode="m">
                    <over/>
                  </xsl:template>
                </xsl:override>
              </xsl:use-package>
              <xsl:mode/>
              <xsl:template match="/*">
                <out><xsl:apply-templates mode="m"/></out>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunAsync(principal, "<root><a/></root>",
            Catalog(("urn:used", used)));
        result.Should().Contain("<over/>");
        result.Should().NotContain("<used/>");
    }

    // Three-level hierarchy mirroring use-package-170: an override at the MIDDLE
    // package must beat a used-package rule of higher priority two levels down,
    // proving the precedence shift propagates across nested use-package merges.
    [Fact]
    public async Task ThreeLevel_MiddleOverride_Beats_BottomHigherPriority()
    {
        var bottom = WritePackage("bottom.xsl", """
            <xsl:package name="urn:bottom" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode name="m" visibility="public"/>
              <xsl:template match="a" mode="m" priority="2">
                <bottom/>
              </xsl:template>
            </xsl:package>
            """);
        var middle = WritePackage("middle.xsl", """
            <xsl:package name="urn:middle" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:use-package name="urn:bottom" package-version="1.0.0">
                <xsl:accept component="mode" names="m" visibility="public"/>
                <xsl:override>
                  <xsl:template match="a" mode="m">
                    <middle/>
                  </xsl:template>
                </xsl:override>
              </xsl:use-package>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:top" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:use-package name="urn:middle" package-version="1.0.0">
                <xsl:accept component="mode" names="m" visibility="public"/>
              </xsl:use-package>
              <xsl:mode/>
              <xsl:template match="/*">
                <out><xsl:apply-templates mode="m"/></out>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunAsync(principal, "<root><a/></root>",
            Catalog(("urn:bottom", bottom), ("urn:middle", middle)));
        result.Should().Contain("<middle/>");
        result.Should().NotContain("<bottom/>");
    }

    // Top-level override must beat a middle-level override which must beat the
    // bottom used-package rule — full precedence ladder (mirrors 170/172).
    [Fact]
    public async Task ThreeLevel_TopOverride_Beats_MiddleOverride_And_Bottom()
    {
        var bottom = WritePackage("bottom2.xsl", """
            <xsl:package name="urn:bottom2" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode name="m" visibility="public"/>
              <xsl:template match="a" mode="m" priority="5">
                <bottom/>
              </xsl:template>
            </xsl:package>
            """);
        var middle = WritePackage("middle2.xsl", """
            <xsl:package name="urn:middle2" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:use-package name="urn:bottom2" package-version="1.0.0">
                <xsl:accept component="mode" names="m" visibility="public"/>
                <xsl:override>
                  <xsl:template match="a" mode="m" priority="3">
                    <middle/>
                  </xsl:template>
                </xsl:override>
              </xsl:use-package>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:top2" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:use-package name="urn:middle2" package-version="1.0.0">
                <xsl:accept component="mode" names="m" visibility="public"/>
                <xsl:override>
                  <xsl:template match="a" mode="m">
                    <top/>
                  </xsl:template>
                </xsl:override>
              </xsl:use-package>
              <xsl:mode/>
              <xsl:template match="/*">
                <out><xsl:apply-templates mode="m"/></out>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunAsync(principal, "<root><a/></root>",
            Catalog(("urn:bottom2", bottom), ("urn:middle2", middle)));
        result.Should().Contain("<top/>");
        result.Should().NotContain("<middle/>");
        result.Should().NotContain("<bottom/>");
    }

    // GUARD against over-correction: within the SAME import precedence (two rules
    // in one package), priority must still decide the winner.
    [Fact]
    public async Task SamePrecedence_FallsBackTo_Priority()
    {
        var used = WritePackage("usedp.xsl", """
            <xsl:package name="urn:usedp" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode name="m" visibility="public"/>
              <xsl:template match="a" mode="m" priority="1">
                <low/>
              </xsl:template>
              <xsl:template match="a" mode="m" priority="9">
                <high/>
              </xsl:template>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:mainp" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:use-package name="urn:usedp" package-version="1.0.0">
                <xsl:accept component="mode" names="m" visibility="public"/>
              </xsl:use-package>
              <xsl:mode/>
              <xsl:template match="/*">
                <out><xsl:apply-templates mode="m"/></out>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunAsync(principal, "<root><a/></root>",
            Catalog(("urn:usedp", used)));
        result.Should().Contain("<high/>");
        result.Should().NotContain("<low/>");
    }
}
