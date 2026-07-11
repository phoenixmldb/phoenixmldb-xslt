using FluentAssertions;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Static-validation error codes for xsl:override / xsl:use-package, mirroring the W3C
/// decl/override conformance cases as self-contained two-package unit tests using an
/// on-disk package catalog. All are parse-time (static) checks:
/// <list type="bullet">
///   <item>XTSE0010 — a disallowed child of xsl:override (text, LRE, nested xsl:override).</item>
///   <item>XTSE3050 — a component declared outside xsl:override clashes with a public
///     component of a used package (variable, function, mode, or added template rule).</item>
///   <item>XTSE3060 — overriding a private/final attribute-set.</item>
///   <item>Accumulators in different packages may share a name (no XTSE3350).</item>
/// </list>
/// </summary>
public sealed class OverrideStaticErrorTests : IDisposable
{
    private readonly string _dir;

    public OverrideStaticErrorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxovr-" + Guid.NewGuid().ToString("N"));
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

    private async Task<string> RunMainAsync(
        string principalXsl,
        Dictionary<string, List<(string? Version, string FilePath)>> catalog)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principalXsl, new Uri(Path.Combine(_dir, "principal.xsl")),
            null, catalog);
        t.SetInitialTemplate("main");
        return await t.TransformAsync("<in/>");
    }

    private const string BaseFunc = """
        <xsl:package name="urn:base-f" package-version="1.0.0" version="3.0"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:xs="http://www.w3.org/2001/XMLSchema"
          xmlns:p="urn:base-f" exclude-result-prefixes="xs p">
          <xsl:function name="p:f" as="xs:string" visibility="public">
            <xsl:param name="in" as="xs:string"/>
            <xsl:sequence select="'*' || $in || '*'"/>
          </xsl:function>
        </xsl:package>
        """;

    // ---- override-f-005: text node child of xsl:override → XTSE0010 ----
    [Fact]
    public async Task OverrideChild_Text_IsDisallowed_XTSE0010()
    {
        var basePath = WritePackage("base-f.xsl", BaseFunc);
        var principal = """
            <xsl:package name="urn:main-f" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:base-f" exclude-result-prefixes="xs p">
              <xsl:use-package name="urn:base-f" package-version="1.0.0">
                <xsl:override>
                  <xsl:function name="p:f" as="xs:string" visibility="public">
                    <xsl:param name="in" as="xs:string"/>
                    <xsl:sequence select="$in"/>
                  </xsl:function>
                  Gotcha!
                </xsl:override>
              </xsl:use-package>
              <xsl:template name="main" visibility="public"><out/></xsl:template>
            </xsl:package>
            """;
        Func<Task> act = () => RunMainAsync(principal, Catalog(("urn:base-f", basePath)));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE0010");
    }

    // ---- override-f-006: literal result element child of xsl:override → XTSE0010 ----
    [Fact]
    public async Task OverrideChild_LiteralResultElement_IsDisallowed_XTSE0010()
    {
        var basePath = WritePackage("base-f.xsl", BaseFunc);
        var principal = """
            <xsl:package name="urn:main-f" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:base-f" exclude-result-prefixes="xs p">
              <xsl:use-package name="urn:base-f" package-version="1.0.0">
                <xsl:override>
                  <xsl:function name="p:f" as="xs:string" visibility="public">
                    <xsl:param name="in" as="xs:string"/>
                    <xsl:sequence select="$in"/>
                  </xsl:function>
                  <p:out/>
                </xsl:override>
              </xsl:use-package>
              <xsl:template name="main" visibility="public"><out/></xsl:template>
            </xsl:package>
            """;
        Func<Task> act = () => RunMainAsync(principal, Catalog(("urn:base-f", basePath)));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE0010");
    }

    // ---- override-f-007: xsl:override child of xsl:override → XTSE0010 ----
    [Fact]
    public async Task OverrideChild_NestedOverride_IsDisallowed_XTSE0010()
    {
        var basePath = WritePackage("base-f.xsl", BaseFunc);
        var principal = """
            <xsl:package name="urn:main-f" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:base-f" exclude-result-prefixes="xs p">
              <xsl:use-package name="urn:base-f" package-version="1.0.0">
                <xsl:override>
                  <xsl:function name="p:f" as="xs:string" visibility="public">
                    <xsl:param name="in" as="xs:string"/>
                    <xsl:sequence select="$in"/>
                  </xsl:function>
                  <xsl:override/>
                </xsl:override>
              </xsl:use-package>
              <xsl:template name="main" visibility="public"><out/></xsl:template>
            </xsl:package>
            """;
        Func<Task> act = () => RunMainAsync(principal, Catalog(("urn:base-f", basePath)));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE0010");
    }

    // ---- override-v-012: overriding a variable outside xsl:override → XTSE3050 ----
    [Fact]
    public async Task OverridingVariable_OutsideOverride_XTSE3050()
    {
        var basePath = WritePackage("base-v.xsl", """
            <xsl:package name="urn:base-v" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:variable name="var" as="xs:int" select="xs:int(2)" visibility="public"/>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-v" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:variable name="var" as="xs:decimal" select="12.2"/>
              <xsl:use-package name="urn:base-v" package-version="1.0.0"/>
              <xsl:template name="main" visibility="public"><out/></xsl:template>
            </xsl:package>
            """;
        Func<Task> act = () => RunMainAsync(principal, Catalog(("urn:base-v", basePath)));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE3050");
    }

    // ---- override-f-023: overriding a function outside xsl:override → XTSE3050 ----
    [Fact]
    public async Task OverridingFunction_OutsideOverride_XTSE3050()
    {
        var basePath = WritePackage("base-f.xsl", BaseFunc);
        var principal = """
            <xsl:package name="urn:main-f" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:base-f" exclude-result-prefixes="xs p">
              <xsl:function name="p:f" as="xs:string" visibility="private">
                <xsl:param name="in" as="xs:string"/>
                <xsl:sequence select="$in"/>
              </xsl:function>
              <xsl:use-package name="urn:base-f" package-version="1.0.0"/>
              <xsl:template name="main" visibility="public"><out/></xsl:template>
            </xsl:package>
            """;
        Func<Task> act = () => RunMainAsync(principal, Catalog(("urn:base-f", basePath)));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE3050");
    }

    private const string BaseMode = """
        <xsl:package name="urn:base-m" package-version="1.0.0" version="3.0"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
          <xsl:mode name="m3" visibility="public"/>
          <xsl:template match="A" mode="m3"><a/></xsl:template>
        </xsl:package>
        """;

    // ---- override-m-017: redeclare a used-package mode → XTSE3050 ----
    [Fact]
    public async Task RedeclaringUsedMode_XTSE3050()
    {
        var basePath = WritePackage("base-m.xsl", BaseMode);
        var principal = """
            <xsl:package name="urn:main-m" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" declared-modes="no" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-m" package-version="1.0.0"/>
              <xsl:mode name="m3"/>
              <xsl:template name="main" visibility="public"><out>12</out></xsl:template>
            </xsl:package>
            """;
        Func<Task> act = () => RunMainAsync(principal, Catalog(("urn:base-m", basePath)));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE3050");
    }

    // ---- override-m-018: add a template rule to a used-package mode outside override → XTSE3050 ----
    [Fact]
    public async Task AddingRuleToUsedMode_OutsideOverride_XTSE3050()
    {
        var basePath = WritePackage("base-m.xsl", BaseMode);
        var principal = """
            <xsl:package name="urn:main-m" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" declared-modes="no" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-m" package-version="1.0.0"/>
              <xsl:template match="*" mode="m3"><wrong/></xsl:template>
              <xsl:template name="main" visibility="public"><out>12</out></xsl:template>
            </xsl:package>
            """;
        Func<Task> act = () => RunMainAsync(principal, Catalog(("urn:base-m", basePath)));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE3050");
    }

    // ---- override-as-004: override a private attribute-set → XTSE3060 ----
    [Fact]
    public async Task OverridingPrivateAttributeSet_XTSE3060()
    {
        var basePath = WritePackage("base-as.xsl", """
            <xsl:package name="urn:base-as" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:attribute-set name="as-private" visibility="private">
                <xsl:attribute name="priv1" select="'priv1'"/>
              </xsl:attribute-set>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-as" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-as" package-version="1.0.0">
                <xsl:override>
                  <xsl:attribute-set name="as-private">
                    <xsl:attribute name="priv8" select="'base1o'"/>
                  </xsl:attribute-set>
                </xsl:override>
              </xsl:use-package>
              <xsl:template name="main" visibility="public"><out/></xsl:template>
            </xsl:package>
            """;
        Func<Task> act = () => RunMainAsync(principal, Catalog(("urn:base-as", basePath)));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE3060");
    }

    // ---- override-misc-005: same accumulator name in different packages is allowed ----
    [Fact]
    public async Task AccumulatorSameNameDifferentPackages_IsAllowed()
    {
        var basePath = WritePackage("base-acc.xsl", """
            <xsl:package name="urn:base-acc" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:accumulator name="ac" initial-value="0">
                <xsl:accumulator-rule match="*" select="$value+1"/>
              </xsl:accumulator>
              <xsl:template name="ac" visibility="public">
                <b><xsl:value-of select="accumulator-after('ac')"/></b>
              </xsl:template>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-acc" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-acc" package-version="1.0.0"/>
              <xsl:accumulator name="ac" initial-value="0">
                <xsl:accumulator-rule match="*" select="$value - 1"/>
              </xsl:accumulator>
              <xsl:template name="main" visibility="public"><out/></xsl:template>
            </xsl:package>
            """;
        // Should compile and run without XTSE3350 (accumulators are package-local).
        var result = await RunMainAsync(principal, Catalog(("urn:base-acc", basePath)));
        result.Should().Contain("<out/>");
    }
}
