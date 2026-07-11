using FluentAssertions;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Per-package component scoping: a component of a used package must resolve its own
/// intra-package references (variables, attribute-sets, accumulators) within its OWN
/// package's scope — including that package's abstract/private components — not against
/// the merged global registry of the using package. Mirrors W3C decl/accept
/// (accept-042/043/046/047), decl/override (override-as-005) and the runtime tail of
/// override-misc-005.
/// </summary>
public sealed class PackageScopeResolutionTests : IDisposable
{
    private readonly string _dir;

    public PackageScopeResolutionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxpkgscope-" + Guid.NewGuid().ToString("N"));
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

    private async Task<string> RunMainAsync(
        string principalXsl,
        Dictionary<string, List<(string? Version, string FilePath)>> catalog,
        (string Name, string Value)[]? initialParams = null)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principalXsl, new Uri(Path.Combine(_dir, "principal.xsl")),
            null, catalog);
        t.SetInitialTemplate("main");
        if (initialParams != null)
            foreach (var (k, v) in initialParams)
                t.SetParameter(k, v);
        return await t.TransformAsync("<in/>");
    }

    private static Dictionary<string, List<(string? Version, string FilePath)>> Catalog(
        params (string Name, string Path)[] entries)
    {
        var cat = new Dictionary<string, List<(string? Version, string FilePath)>>();
        foreach (var (name, path) in entries)
            cat[name] = new List<(string?, string)> { ("1.0.0", path) };
        return cat;
    }

    // accept-C style base package: abstract variable + public proxy referencing it,
    // abstract attribute-set + public proxy referencing it.
    private const string BaseC = """
        <xsl:package name="urn:base-c" package-version="1.0.0" version="3.0"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
          <xsl:variable name="v1" as="xs:integer" visibility="abstract"/>
          <xsl:variable name="v1-proxy" as="xs:integer" visibility="public" select="$v1"/>
          <xsl:template name="t1" as="xs:integer" visibility="abstract">
            <xsl:param name="p1" as="xs:string"/>
          </xsl:template>
          <xsl:attribute-set name="a1" visibility="abstract"/>
          <xsl:attribute-set name="a1-proxy" use-attribute-sets="a1" visibility="public"/>
        </xsl:package>
        """;

    // accept-042: proxy component references an abstract intra-package dependency that
    // is accepted "hidden" (not overridden); the proxy is never referenced by main, so
    // no error must arise. a1 is overridden here (concrete) and used directly.
    [Fact]
    public async Task Accept042_VariableProxyReferencingAbstract_NotEvaluated_NoError()
    {
        var basePath = WritePackage("base-c-042.xsl", BaseC.Replace("urn:base-c", "urn:base-c042"));
        var principal = """
            <xsl:package name="urn:main-042" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-c042" package-version="1.0.0">
                <xsl:override>
                  <xsl:template name="t1" as="xs:integer">
                    <xsl:param name="p1" as="xs:string"/>
                    <xsl:sequence select="string-length($p1)"/>
                  </xsl:template>
                  <xsl:attribute-set name="a1">
                    <xsl:attribute name="a" select="22"/>
                  </xsl:attribute-set>
                </xsl:override>
                <xsl:accept component="variable" names="v1" visibility="hidden"/>
              </xsl:use-package>
              <xsl:template name="main" visibility="public">
                <out xsl:use-attribute-sets="a1">
                  <xsl:call-template name="t1">
                    <xsl:with-param name="p1" select="'London'"/>
                  </xsl:call-template>
                </out>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunMainAsync(principal, Catalog(("urn:base-c042", basePath)));
        result.Should().Contain("a=\"22\"").And.Contain(">6<");
    }

    // accept-046: proxy attribute-set references an abstract intra-package attribute-set
    // (accepted hidden, not overridden); proxy never used, so the static XTSE0710 check
    // must resolve a1 within the base package's own scope rather than the merged scope.
    [Fact]
    public async Task Accept046_AttributeSetProxyReferencingAbstract_NoStaticError()
    {
        var basePath = WritePackage("base-c-046.xsl", BaseC.Replace("urn:base-c", "urn:base-c046"));
        var principal = """
            <xsl:package name="urn:main-046" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-c046" package-version="1.0.0">
                <xsl:override>
                  <xsl:variable name="v1" as="xs:integer" select="22"/>
                  <xsl:template name="t1" as="xs:integer">
                    <xsl:param name="p1" as="xs:string"/>
                    <xsl:sequence select="string-length($p1)"/>
                  </xsl:template>
                </xsl:override>
                <xsl:accept component="attribute-set" names="a1" visibility="hidden"/>
              </xsl:use-package>
              <xsl:template name="main" visibility="public">
                <out>
                  <xsl:call-template name="t1">
                    <xsl:with-param name="p1" select="'AB'"/>
                  </xsl:call-template>
                </out>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunMainAsync(principal, Catalog(("urn:base-c046", basePath)));
        result.Should().Contain(">2<");
    }

    // override-as-005 shape: a used package's PRIVATE attribute-set must be resolvable by
    // another attribute-set of ITS OWN package, but NOT leak into the using package.
    [Fact]
    public async Task OverrideAs005_PrivateAttributeSet_ResolvesWithinOwnPackageOnly()
    {
        var basePath = WritePackage("base-as005.xsl", """
            <xsl:package name="urn:base-as005" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:attribute-set name="priv" visibility="private">
                <xsl:attribute name="p" select="'P'"/>
              </xsl:attribute-set>
              <xsl:attribute-set name="pub" use-attribute-sets="priv" visibility="public">
                <xsl:attribute name="q" select="'Q'"/>
              </xsl:attribute-set>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-as005" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-as005" package-version="1.0.0"/>
              <xsl:template name="main" visibility="public">
                <out><x xsl:use-attribute-sets="pub"/></out>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunMainAsync(principal, Catalog(("urn:base-as005", basePath)));
        // pub pulls in priv from its own package
        result.Should().Contain("p=\"P\"").And.Contain("q=\"Q\"");
    }

    // override-misc-005 runtime tail: a used package and the using package both declare an
    // accumulator named 'ac'; a component of each must resolve accumulator-after('ac') to its
    // OWN package's accumulator.
    [Fact]
    public async Task OverrideMisc005_AccumulatorSameName_ResolvesPerPackage()
    {
        var basePath = WritePackage("base-acc005.xsl", """
            <xsl:package name="urn:base-acc005" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:accumulator name="ac" initial-value="0">
                <xsl:accumulator-rule match="*" select="$value + 1"/>
              </xsl:accumulator>
              <xsl:template name="ac" visibility="public">
                <b><xsl:value-of select="accumulator-after('ac')"/></b>
              </xsl:template>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-acc005" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:variable name="data"><e>x</e><x>y</x><g>z</g></xsl:variable>
              <xsl:use-package name="urn:base-acc005" package-version="1.0.0"/>
              <xsl:accumulator name="ac" initial-value="0">
                <xsl:accumulator-rule match="*" select="$value - 1"/>
              </xsl:accumulator>
              <xsl:template name="main" visibility="public">
                <xsl:for-each select="$data">
                  <out>
                    <a><xsl:copy-of select="accumulator-after('ac')"/></a>
                    <xsl:call-template name="ac"/>
                  </out>
                </xsl:for-each>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunMainAsync(principal, Catalog(("urn:base-acc005", basePath)));
        result.Should().Contain("<a>-3</a>").And.Contain("<b>3</b>");
    }

    // The private attribute-set must NOT be resolvable from the using package directly.
    [Fact]
    public async Task OverrideAs005_PrivateAttributeSet_NotVisibleToUsingPackage()
    {
        var basePath = WritePackage("base-as005b.xsl", """
            <xsl:package name="urn:base-as005b" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:attribute-set name="priv" visibility="private">
                <xsl:attribute name="p" select="'P'"/>
              </xsl:attribute-set>
              <xsl:attribute-set name="pub" use-attribute-sets="priv" visibility="public">
                <xsl:attribute name="q" select="'Q'"/>
              </xsl:attribute-set>
            </xsl:package>
            """);
        // Using package references the private 'priv' directly — must be a static error.
        var principal = """
            <xsl:package name="urn:main-as005b" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-as005b" package-version="1.0.0"/>
              <xsl:template name="main" visibility="public">
                <out><x xsl:use-attribute-sets="priv"/></out>
              </xsl:template>
            </xsl:package>
            """;
        Func<Task> act = () => RunMainAsync(principal, Catalog(("urn:base-as005b", basePath)));
        await act.Should().ThrowAsync<Exception>();
    }
}
