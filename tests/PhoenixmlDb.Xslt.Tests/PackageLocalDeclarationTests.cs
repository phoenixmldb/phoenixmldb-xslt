using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Per-package resolution of declarations that XSLT 3.0 defines as LOCAL to their
/// declaring package: global variables (diamond override / different versions),
/// named xsl:output + xsl:character-map, and xsl:namespace-alias. Mirrors W3C
/// decl/use-package cases 175/176 (globals), 108/108b (named output + char-map),
/// and 103/108b (namespace-alias). Non-package stylesheets must be unaffected.
/// </summary>
public sealed class PackageLocalDeclarationTests : IDisposable
{
    private readonly string _dir;

    public PackageLocalDeclarationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxpkgdecl-" + Guid.NewGuid().ToString("N"));
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
        params (string Name, string? Version, string Path)[] entries)
    {
        var cat = new Dictionary<string, List<(string?, string)>>();
        foreach (var (name, version, path) in entries)
        {
            if (!cat.TryGetValue(name, out var list))
                cat[name] = list = new List<(string?, string)>();
            list.Add((version, path));
        }
        return cat;
    }

    // ---------------------------------------------------------------------------
    // A: Per-package global variables — diamond override (use-package-175)
    // ---------------------------------------------------------------------------
    // Package D declares public $v='ddddd'. Package B uses D overriding $v='bbbbb';
    // Package C uses D overriding $v='ccccc'. Principal A uses B and C. Template b
    // (in B) reads $v -> must be 'bbbbb'; template c (in C) reads $v -> must be
    // 'ccccc'. A single QName-keyed global map wrongly collapses them.
    [Fact]
    public async Task GlobalVariable_DiamondOverride_ResolvesPerPackage()
    {
        var d = Write("d.xsl", """
            <xsl:package name="urn:d" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:variable name="v" select="'ddddd'" as="xs:string" visibility="public"/>
            </xsl:package>
            """);
        var b = Write("b.xsl", """
            <xsl:package name="urn:b" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:d" package-version="1.0.0">
                <xsl:override>
                  <xsl:variable name="v" as="xs:string" select="'bbbbb'" visibility="public"/>
                </xsl:override>
              </xsl:use-package>
              <xsl:template name="b" visibility="public"><b><xsl:value-of select="$v"/></b></xsl:template>
            </xsl:package>
            """);
        var c = Write("c.xsl", """
            <xsl:package name="urn:c" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:d" package-version="1.0.0">
                <xsl:override>
                  <xsl:variable name="v" as="xs:string" select="'ccccc'" visibility="private"/>
                </xsl:override>
              </xsl:use-package>
              <xsl:template name="c" visibility="public"><c><xsl:value-of select="$v"/></c></xsl:template>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:a" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:b" package-version="1.0.0"/>
              <xsl:use-package name="urn:c" package-version="1.0.0"/>
              <xsl:template name="main" visibility="public">
                <out><bb><xsl:call-template name="b"/></bb><cc><xsl:call-template name="c"/></cc></out>
              </xsl:template>
            </xsl:package>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principal, new Uri(Path.Combine(_dir, "a.xsl")),
            null, Cat(("urn:d", "1.0.0", d), ("urn:b", "1.0.0", b), ("urn:c", "1.0.0", c)));
        t.SetInitialTemplate("main");
        var result = await t.TransformAsync("<in/>");

        Assert.Contains("<b>bbbbb</b>", result, StringComparison.Ordinal);
        Assert.Contains("<c>ccccc</c>", result, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------
    // A: Per-package global variables — different versions (use-package-176)
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task GlobalVariable_DifferentVersions_ResolvesPerPackage()
    {
        var d1 = Write("d1.xsl", """
            <xsl:package name="urn:dv" package-version="1.0.1" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:variable name="v" select="'bbbbb'" as="xs:string" visibility="public"/>
            </xsl:package>
            """);
        var d2 = Write("d2.xsl", """
            <xsl:package name="urn:dv" package-version="2.0.1" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:variable name="v" select="'ccccc'" as="xs:string" visibility="public"/>
            </xsl:package>
            """);
        var b = Write("bv.xsl", """
            <xsl:package name="urn:bv" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:dv" package-version="1.0.*"/>
              <xsl:template name="b" visibility="public"><b><xsl:value-of select="$v"/></b></xsl:template>
            </xsl:package>
            """);
        var c = Write("cv.xsl", """
            <xsl:package name="urn:cv" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:dv" package-version="2.0.*"/>
              <xsl:template name="c" visibility="public"><c><xsl:value-of select="$v"/></c></xsl:template>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:av" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:bv" package-version="1.0.0"/>
              <xsl:use-package name="urn:cv" package-version="1.0.0"/>
              <xsl:template name="main" visibility="public">
                <out><bb><xsl:call-template name="b"/></bb><cc><xsl:call-template name="c"/></cc></out>
              </xsl:template>
            </xsl:package>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principal, new Uri(Path.Combine(_dir, "av.xsl")),
            null, Cat(("urn:dv", "1.0.1", d1), ("urn:dv", "2.0.1", d2),
                      ("urn:bv", "1.0.0", b), ("urn:cv", "1.0.0", c)));
        t.SetInitialTemplate("main");
        var result = await t.TransformAsync("<in/>");

        Assert.Contains("<b>bbbbb</b>", result, StringComparison.Ordinal);
        Assert.Contains("<c>ccccc</c>", result, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------
    // B: Per-package named xsl:output + xsl:character-map (use-package-108)
    // ---------------------------------------------------------------------------
    // The library package declares a NAMED output 'with-maps' referencing char-map
    // 'cm' (z -> ZZ) and a template 'go' that does result-document format='with-maps'.
    // The principal uses the library and calls go. @format must resolve against the
    // DECLARING package's outputs/char-maps: 'zzz' -> 'ZZZZZZ'.
    [Fact]
    public async Task NamedOutputAndCharacterMap_ResolveFromDeclaringPackage()
    {
        var lib = Write("outlib.xsl", """
            <xsl:package name="urn:outlib" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:output name="with-maps" use-character-maps="cm"/>
              <xsl:character-map name="cm">
                <xsl:output-character character="z" string="ZZ"/>
              </xsl:character-map>
              <xsl:template name="go" visibility="public">
                <xsl:result-document format="with-maps"><out>zzz</out></xsl:result-document>
              </xsl:template>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:outmain" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:use-package name="urn:outlib" package-version="1.0.0"/>
              <xsl:template name="main" visibility="public">
                <xsl:call-template name="go"/>
              </xsl:template>
            </xsl:package>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principal, new Uri(Path.Combine(_dir, "outmain.xsl")),
            null, Cat(("urn:outlib", "1.0.0", lib)));
        t.SetInitialTemplate("main");
        var result = await t.TransformAsync("<in/>");

        Assert.Contains("ZZZZZZ", result, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------
    // C: Per-package xsl:namespace-alias (use-package-103)
    // ---------------------------------------------------------------------------
    // base declares alias xs -> p (base ns); its function p:alias emits <xs:test/>.
    // principal declares alias xs -> q (principal ns); its function q:alias emits
    // <xs:test/>. Each function's literal must be aliased by ITS OWN package.
    [Fact]
    public async Task NamespaceAlias_ResolvesPerPackage()
    {
        var basePkg = Write("nsbase.xsl", """
            <xsl:package name="urn:nsbase" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:ns-base-result" exclude-result-prefixes="xs p">
              <xsl:namespace-alias stylesheet-prefix="xs" result-prefix="p"/>
              <xsl:function name="p:alias" as="element()" visibility="public"><xs:test/></xsl:function>
            </xsl:package>
            """);
        var principal = """
            <xsl:package name="urn:nsmain" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema"
              xmlns:p="urn:ns-base-result" xmlns:q="urn:ns-main-result"
              exclude-result-prefixes="xs p q">
              <xsl:use-package name="urn:nsbase" package-version="1.0.0"/>
              <xsl:namespace-alias stylesheet-prefix="xs" result-prefix="q"/>
              <xsl:function name="q:alias" as="element()" visibility="public"><xs:test/></xsl:function>
              <xsl:template name="main" visibility="public">
                <out><xsl:sequence select="q:alias(), p:alias()"/></out>
              </xsl:template>
            </xsl:package>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principal, new Uri(Path.Combine(_dir, "nsmain.xsl")),
            null, Cat(("urn:nsbase", "1.0.0", basePkg)));
        t.SetInitialTemplate("main");
        var result = await t.TransformAsync("<in/>");

        // q:alias() -> principal ns; p:alias() -> base ns
        Assert.Contains("urn:ns-main-result", result, StringComparison.Ordinal);
        Assert.Contains("urn:ns-base-result", result, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------
    // Control: a plain (non-package) stylesheet's namespace-alias still applies.
    // ---------------------------------------------------------------------------
    [Fact]
    public async Task NamespaceAlias_PlainStylesheet_StillApplies()
    {
        var sheet = """
            <xsl:stylesheet version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:out="urn:plain-alias-result" xmlns:in="urn:plain-alias-src"
              exclude-result-prefixes="in">
              <xsl:namespace-alias stylesheet-prefix="in" result-prefix="out"/>
              <xsl:template name="main"><in:e/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(sheet, new Uri(Path.Combine(_dir, "plain.xsl")));
        t.SetInitialTemplate("main");
        var result = await t.TransformAsync("<in/>");
        Assert.Contains("urn:plain-alias-result", result, StringComparison.Ordinal);
    }
}
