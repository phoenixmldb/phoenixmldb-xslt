using AwesomeAssertions;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Foundational package component-resolution: xsl:original completeness across all
/// component kinds (variable, attribute-set, function named-reference / partial
/// application) and accepted-component visibility (xsl:accept name specificity).
/// Mirrors W3C decl/override and decl/accept conformance cases as self-contained
/// two-package unit tests using an on-disk package catalog.
/// </summary>
public sealed class PackageComponentResolutionTests : IDisposable
{
    private readonly string _dir;

    public PackageComponentResolutionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxpkgres-" + Guid.NewGuid().ToString("N"));
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
        Dictionary<string, List<(string? Version, string FilePath)>> catalog)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principalXsl, new Uri(Path.Combine(_dir, "principal.xsl")),
            null, catalog);
        t.SetInitialTemplate("main");
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

    // ---- xsl:original inside an overriding xsl:variable (override-v-003) ----
    [Fact]
    public async Task Override_Variable_XslOriginal_ResolvesToOverriddenValue()
    {
        var basePath = WritePackage("base-v.xsl", """
            <xsl:package name="urn:base-v" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:variable name="vp" as="xs:integer" visibility="public" select="$vb - 1"/>
              <xsl:variable name="vb" as="xs:integer" visibility="public" select="2"/>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-v" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-v" package-version="1.0.0">
                <xsl:override>
                  <xsl:variable name="vp" as="xs:integer" visibility="public" select="$xsl:original + 13"/>
                </xsl:override>
              </xsl:use-package>
              <xsl:template name="main" visibility="public">
                <xsl:choose>
                  <xsl:when test="$vp = 14"><ok/></xsl:when>
                  <xsl:otherwise><wrong value="{$vp}"/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunMainAsync(principal, Catalog(("urn:base-v", basePath)));
        result.Should().Contain("<ok/>");
    }

    // ---- xsl:original inside an overriding xsl:attribute-set (override-as-002 / package-101) ----
    [Fact]
    public async Task Override_AttributeSet_UseAttributeSetsXslOriginal_MergesOriginalAttributes()
    {
        var basePath = WritePackage("base-as.xsl", """
            <xsl:package name="urn:base-as" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:attribute-set name="asp" visibility="public">
                <xsl:attribute name="pub1" select="'pub1'"/>
                <xsl:attribute name="pub2" select="'pub2'"/>
              </xsl:attribute-set>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-as" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-as" package-version="1.0.0">
                <xsl:override>
                  <xsl:attribute-set name="asp" visibility="public" use-attribute-sets="xsl:original">
                    <xsl:attribute name="pub1" select="'pub1o'"/>
                    <xsl:attribute name="pub3" select="'pub3o'"/>
                  </xsl:attribute-set>
                </xsl:override>
              </xsl:use-package>
              <xsl:template name="main" visibility="public">
                <out><x xsl:use-attribute-sets="asp"/></out>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunMainAsync(principal, Catalog(("urn:base-as", basePath)));
        // original contributes pub2; override contributes pub1o (wins over original pub1) and pub3o
        result.Should().Contain("pub2=\"pub2\"");
        result.Should().Contain("pub1=\"pub1o\"");
        result.Should().Contain("pub3=\"pub3o\"");
    }

    // ---- xsl:original as a named function reference (override-f-017) ----
    [Fact]
    public async Task Override_Function_XslOriginalNamedReference_CapturesOriginal()
    {
        var basePath = WritePackage("base-f.xsl", """
            <xsl:package name="urn:base-f" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:base-f" exclude-result-prefixes="xs p">
              <xsl:function name="p:f" as="xs:string" visibility="public">
                <xsl:param name="in" as="xs:string"/>
                <xsl:param name="count" as="xs:integer"/>
                <xsl:sequence select="string-join((for $i in 1 to $count return $in))"/>
              </xsl:function>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-f" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:base-f" exclude-result-prefixes="xs p">
              <xsl:use-package name="urn:base-f" package-version="1.0.0">
                <xsl:override>
                  <xsl:function name="p:f" as="xs:string" visibility="public">
                    <xsl:param name="in" as="xs:string"/>
                    <xsl:param name="count" as="xs:integer"/>
                    <xsl:variable name="orig" as="function(*)" select="xsl:original#2"/>
                    <xsl:sequence select="p:action($orig, $in, $count)"/>
                  </xsl:function>
                </xsl:override>
              </xsl:use-package>
              <xsl:function name="p:action">
                <xsl:param name="f" as="function(*)"/>
                <xsl:param name="in" as="xs:string"/>
                <xsl:param name="count" as="xs:integer"/>
                <xsl:sequence select="'*' || $f($in, $count) || '*'"/>
              </xsl:function>
              <xsl:template name="main" visibility="public">
                <xsl:choose>
                  <xsl:when test="p:f('x', 5) = '*xxxxx*'"><ok/></xsl:when>
                  <xsl:otherwise><wrong value="{p:f('x', 5)}"/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunMainAsync(principal, Catalog(("urn:base-f", basePath)));
        result.Should().Contain("<ok/>");
    }

    // ---- xsl:original as a partial function application (override-f-018) ----
    [Fact]
    public async Task Override_Function_XslOriginalPartialApplication_CapturesOriginal()
    {
        var basePath = WritePackage("base-f2.xsl", """
            <xsl:package name="urn:base-f2" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:base-f2" exclude-result-prefixes="xs p">
              <xsl:function name="p:f" as="xs:string" visibility="public">
                <xsl:param name="in" as="xs:string"/>
                <xsl:param name="count" as="xs:integer"/>
                <xsl:sequence select="string-join((for $i in 1 to $count return $in))"/>
              </xsl:function>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-f2" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:base-f2" exclude-result-prefixes="xs p">
              <xsl:use-package name="urn:base-f2" package-version="1.0.0">
                <xsl:override>
                  <xsl:function name="p:f" as="xs:string" visibility="public">
                    <xsl:param name="in" as="xs:string"/>
                    <xsl:param name="count" as="xs:integer"/>
                    <xsl:variable name="orig" as="function(*)" select="xsl:original(?, $count)"/>
                    <xsl:sequence select="p:action($orig, $in)"/>
                  </xsl:function>
                </xsl:override>
              </xsl:use-package>
              <xsl:function name="p:action">
                <xsl:param name="f" as="function(*)"/>
                <xsl:param name="in" as="xs:string"/>
                <xsl:sequence select="'*' || $f($in) || '*'"/>
              </xsl:function>
              <xsl:template name="main" visibility="public">
                <xsl:choose>
                  <xsl:when test="p:f('x', 5) = '*xxxxx*'"><ok/></xsl:when>
                  <xsl:otherwise><wrong value="{p:f('x', 5)}"/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunMainAsync(principal, Catalog(("urn:base-f2", basePath)));
        result.Should().Contain("<ok/>");
    }

    // ---- xsl:accept explicit name beats wildcard (accept-005) ----
    [Fact]
    public async Task Accept_ExplicitNameBeatsWildcard_VariableStaysVisible()
    {
        var basePath = WritePackage("base-acc.xsl", """
            <xsl:package name="urn:base-acc" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:expose component="variable" names="*" visibility="public"/>
              <xsl:variable name="v1" select="1"/>
              <xsl:variable name="v2" select="2"/>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-acc" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-acc" package-version="1.0.0">
                <xsl:accept component="variable" names="v1" visibility="private"/>
                <xsl:accept component="variable" names="*" visibility="hidden"/>
              </xsl:use-package>
              <xsl:template name="main" visibility="public">
                <out><v1><xsl:value-of select="$v1"/></v1></out>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunMainAsync(principal, Catalog(("urn:base-acc", basePath)));
        result.Should().Contain("<v1>1</v1>");
    }

    private const string BaseWithVariable = """
        <xsl:package name="urn:base-ov" package-version="1.0.0" version="3.0"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
          <xsl:variable name="var" as="xs:string" select="'base'" visibility="public"/>
        </xsl:package>
        """;

    /// <summary>
    /// An xsl:param may override an xsl:variable of the used package (XSLT 3.0 §3.5.3.4), and its
    /// value is then the one the caller supplies. It was looked for among the parameters only and
    /// dropped, so the variable's value stayed (W3C override-v-008/009).
    /// </summary>
    [Fact]
    public async Task Override_Variable_With_Param_Takes_The_Supplied_Value()
    {
        var basePath = WritePackage("base-ov.xsl", BaseWithVariable);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:package name="urn:main-ov" version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-ov" package-version="*">
                <xsl:override><xsl:param name="var" as="xs:string" required="yes"/></xsl:override>
              </xsl:use-package>
              <xsl:template name="main" visibility="public"><out><xsl:value-of select="$var"/></out></xsl:template>
            </xsl:package>
            """, new Uri(Path.Combine(_dir, "principal.xsl")), null, Catalog(("urn:base-ov", basePath)));
        t.SetInitialTemplate("main");
        t.SetParameter("var", "supplied");
        (await t.TransformAsync("<in/>")).Should().Contain("<out>supplied</out>");
    }

    /// <summary>
    /// XTSE3070: an overriding variable or parameter declares the type of the component it
    /// overrides. A different type was accepted (W3C override-v-006/007).
    /// </summary>
    [Fact]
    public async Task Override_Variable_With_Another_Type_Is_XTSE3070()
    {
        var basePath = WritePackage("base-ov.xsl", BaseWithVariable);
        var act = () => RunMainAsync("""
            <xsl:package name="urn:main-ov" version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-ov" package-version="*">
                <xsl:override><xsl:variable name="var" as="xs:integer" select="1" visibility="public"/></xsl:override>
              </xsl:use-package>
              <xsl:template name="main" visibility="public"><out/></xsl:template>
            </xsl:package>
            """, Catalog(("urn:base-ov", basePath)));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE3070");
    }
}
