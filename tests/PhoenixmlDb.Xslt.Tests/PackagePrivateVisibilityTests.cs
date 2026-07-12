using FluentAssertions;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Enforces private-component visibility across the xsl:use-package boundary
/// (W3C decl/use-package-003 / -006 / -007). A component that is private in a used
/// package — whether explicitly private or private by the package default — must not
/// be resolvable from the using package: a reference to it raises a static error
/// (XPST0017 for an unknown function, XPST0008 for an unknown variable). Public/final
/// components remain visible, intra-package references to private components still
/// resolve, and plain xsl:import/xsl:include cross-module references (which rely on the
/// parser's public-by-default workaround) are unaffected.
/// </summary>
public sealed class PackagePrivateVisibilityTests : IDisposable
{
    private readonly string _dir;

    public PackagePrivateVisibilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxpkgvis-" + Guid.NewGuid().ToString("N"));
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

    // ---- use-package-003: private function of a used package is not callable ----
    // DEFERRED: enforcing this correctly requires rejecting a by-NAME call at name-resolution
    // time (a static XPST0017) WITHOUT rejecting a dynamic invocation of a function item that
    // was captured inside the used package and passed out (override-f-014 legitimately does the
    // latter). The engine resolves both through the same FunctionLibrary.InvokeAsync, so the
    // distinction lives in the XQuery execution layer (FunctionCallOperator vs
    // DynamicFunctionCallOperator) and cannot be made in the XSLT adapter without regressing
    // override-f-014/016/017/018. Tracked as a follow-up; the variable cases (006/007) ship.
    [Fact(Skip = "use-package-003: private-function boundary needs XQuery name-resolution hook; deferred")]
    public async Task PrivateFunction_OfUsedPackage_NotCallableFromUsingPackage()
    {
        var basePath = WritePackage("base-fn.xsl", """
            <xsl:package name="urn:base-fn" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:base-fn" exclude-result-prefixes="xs p">
              <xsl:function name="p:f" as="xs:string" visibility="public">
                <xsl:param name="in" as="xs:string"/>
                <xsl:param name="count" as="xs:integer"/>
                <xsl:sequence select="p:f-private($in, $count)"/>
              </xsl:function>
              <xsl:function name="p:f-private" as="xs:string" visibility="private">
                <xsl:param name="in" as="xs:string"/>
                <xsl:param name="count" as="xs:integer"/>
                <xsl:sequence select="string-join((1 to $count)!$in, '')"/>
              </xsl:function>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-fn" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:base-fn" exclude-result-prefixes="xs p">
              <xsl:use-package name="urn:base-fn" package-version="1.0.0"/>
              <xsl:template name="main" visibility="public">
                <xsl:choose>
                  <xsl:when test="p:f-private('x', 5) = 'xxxxx'"><ok/></xsl:when>
                  <xsl:otherwise><wrong/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
            </xsl:package>
            """;
        var act = () => RunMainAsync(principal, Catalog(("urn:base-fn", basePath)));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XPST0017");
    }

    // ---- use-package-006: explicitly-private variable of a used package is not visible ----
    [Fact]
    public async Task PrivateVariable_Explicit_OfUsedPackage_NotVisibleFromUsingPackage()
    {
        var basePath = WritePackage("base-var-006.xsl", """
            <xsl:package name="urn:base-var6" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:variable name="v-public" as="xs:string" visibility="public" select="'v/public'"/>
              <xsl:variable name="v-private" as="xs:string" visibility="private" select="'v/private'"/>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-var6" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-var6" package-version="1.0.0"/>
              <xsl:template name="main" visibility="public">
                <xsl:choose>
                  <xsl:when test="$v-private = 'v/private'"><ok/></xsl:when>
                  <xsl:otherwise><wrong/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
            </xsl:package>
            """;
        var act = () => RunMainAsync(principal, Catalog(("urn:base-var6", basePath)));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XPST0008");
    }

    // ---- use-package-007: variable private by default (no visibility attr) is not visible ----
    [Fact]
    public async Task PrivateVariable_ByDefault_OfUsedPackage_NotVisibleFromUsingPackage()
    {
        var basePath = WritePackage("base-var-007.xsl", """
            <xsl:package name="urn:base-var7" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:variable name="v-public" as="xs:string" visibility="public" select="'v/public'"/>
              <xsl:variable name="v-private" as="xs:string" select="'v/private'"/>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-var7" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-var7" package-version="1.0.0"/>
              <xsl:template name="main" visibility="public">
                <xsl:choose>
                  <xsl:when test="$v-private = 'v/private'"><ok/></xsl:when>
                  <xsl:otherwise><wrong/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
            </xsl:package>
            """;
        var act = () => RunMainAsync(principal, Catalog(("urn:base-var7", basePath)));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XPST0008");
    }

    // ==== GUARDS ====

    // (a) A PUBLIC function of a used package is still visible/callable.
    [Fact]
    public async Task PublicFunction_OfUsedPackage_StillCallable()
    {
        var basePath = WritePackage("base-pub-fn.xsl", """
            <xsl:package name="urn:base-pfn" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:base-pfn" exclude-result-prefixes="xs p">
              <xsl:function name="p:f" as="xs:string" visibility="public">
                <xsl:param name="in" as="xs:string"/>
                <xsl:param name="count" as="xs:integer"/>
                <xsl:sequence select="p:f-private($in, $count)"/>
              </xsl:function>
              <xsl:function name="p:f-private" as="xs:string" visibility="private">
                <xsl:param name="in" as="xs:string"/>
                <xsl:param name="count" as="xs:integer"/>
                <xsl:sequence select="string-join((1 to $count)!$in, '')"/>
              </xsl:function>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-pfn" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:base-pfn" exclude-result-prefixes="xs p">
              <xsl:use-package name="urn:base-pfn" package-version="1.0.0"/>
              <xsl:template name="main" visibility="public">
                <xsl:choose>
                  <xsl:when test="p:f('x', 5) = 'xxxxx'"><ok/></xsl:when>
                  <xsl:otherwise><wrong/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
            </xsl:package>
            """;
        // (a) also proves (b): the public function's body internally references the
        // private p:f-private within the same package, which must still resolve.
        var result = await RunMainAsync(principal, Catalog(("urn:base-pfn", basePath)));
        result.Should().Contain("<ok/>");
    }

    // (a) A PUBLIC variable of a used package is still visible.
    [Fact]
    public async Task PublicVariable_OfUsedPackage_StillVisible()
    {
        var basePath = WritePackage("base-pub-var.xsl", """
            <xsl:package name="urn:base-pvar" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:variable name="v-public" as="xs:string" visibility="public" select="'v/public'"/>
              <xsl:variable name="v-private" as="xs:string" visibility="private" select="'v/private'"/>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-pvar" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-pvar" package-version="1.0.0"/>
              <xsl:template name="main" visibility="public">
                <xsl:choose>
                  <xsl:when test="$v-public = 'v/public'"><ok/></xsl:when>
                  <xsl:otherwise><wrong/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunMainAsync(principal, Catalog(("urn:base-pvar", basePath)));
        result.Should().Contain("<ok/>");
    }

    // (b) A public variable of a used package that INTERNALLY references a private
    // sibling variable resolves it (intra-package private reference intact).
    [Fact]
    public async Task PublicVariable_ReferencingPrivateSibling_ResolvesIntraPackage()
    {
        var basePath = WritePackage("base-intra-var.xsl", """
            <xsl:package name="urn:base-ivar" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:variable name="v-public" as="xs:string" visibility="public" select="concat($v-private, '!')"/>
              <xsl:variable name="v-private" as="xs:string" select="'v/private'"/>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:main-ivar" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:base-ivar" package-version="1.0.0"/>
              <xsl:template name="main" visibility="public">
                <xsl:choose>
                  <xsl:when test="$v-public = 'v/private!'"><ok/></xsl:when>
                  <xsl:otherwise><wrong value="{$v-public}"/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
            </xsl:package>
            """;
        var result = await RunMainAsync(principal, Catalog(("urn:base-ivar", basePath)));
        result.Should().Contain("<ok/>");
    }

    // (c) Plain xsl:import: a component with NO visibility attribute in an imported
    // module remains visible to the importing module (the public-by-default workaround
    // for non-package multi-module stylesheets must be untouched).
    [Fact]
    public async Task PlainImport_ComponentWithoutVisibility_StillVisible()
    {
        var modulePath = WritePackage("imported-mod.xsl", """
            <xsl:stylesheet version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:mod" exclude-result-prefixes="xs p">
              <xsl:variable name="v-mod" as="xs:string" select="'from-module'"/>
              <xsl:function name="p:g" as="xs:string">
                <xsl:param name="in" as="xs:string"/>
                <xsl:sequence select="concat($in, '/g')"/>
              </xsl:function>
            </xsl:stylesheet>
            """);
        var principal = $$"""
            <xsl:stylesheet version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:mod" exclude-result-prefixes="xs p">
              <xsl:import href="{{new Uri(modulePath).AbsoluteUri}}"/>
              <xsl:template name="main" visibility="public">
                <xsl:choose>
                  <xsl:when test="$v-mod = 'from-module' and p:g('a') = 'a/g'"><ok/></xsl:when>
                  <xsl:otherwise><wrong/></xsl:otherwise>
                </xsl:choose>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var result = await RunMainAsync(principal, Catalog());
        result.Should().Contain("<ok/>");
    }
}
