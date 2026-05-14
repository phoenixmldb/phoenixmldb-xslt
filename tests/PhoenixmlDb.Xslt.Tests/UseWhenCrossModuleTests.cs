using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:use-when across module boundaries. Per XSLT 3.0 §3.10, a static
/// variable/parameter is in scope for use-when expressions in any module
/// that imports or includes the declaring module (subject to forward-reference
/// rules within a single module).
/// </summary>
public sealed class UseWhenCrossModuleTests : IDisposable
{
    private readonly string _tempDir;

    public UseWhenCrossModuleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "use-when-cross-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<string> Run(string mainPath, string xml)
    {
        var t = new XsltTransformer();
        var content = await File.ReadAllTextAsync(mainPath);
        await t.LoadStylesheetAsync(content, new Uri(mainPath));
        return await t.TransformAsync(xml);
    }

    [Fact]
    public async Task ImportingModule_StaticVar_VisibleToImported_Use_When()
    {
        // Module A defines a static var. Module B (imported by A) uses it in use-when.
        Write("imported.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:template match="/" use-when="$debug">
                    <out>imported-debug</out>
                </xsl:template>
                <xsl:template match="/" use-when="not($debug)">
                    <out>imported-quiet</out>
                </xsl:template>
            </xsl:stylesheet>
            """);
        var mainPath = Write("main.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
                <xsl:variable name="debug" as="xs:boolean" static="yes" select="true()"/>
                <xsl:import href="imported.xsl"/>
            </xsl:stylesheet>
            """);
        var r = await Run(mainPath, "<root/>");
        r.Should().Contain("imported-debug", $"actual={r}");
        r.Should().NotContain("imported-quiet", $"actual={r}");
    }

    [Fact]
    public async Task IncludingModule_StaticVar_VisibleToIncluded_Use_When()
    {
        // Same shape but via xsl:include rather than xsl:import.
        Write("included.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:template match="/" use-when="$verbose">
                    <out>included-verbose</out>
                </xsl:template>
                <xsl:template match="/" use-when="not($verbose)">
                    <out>included-quiet</out>
                </xsl:template>
            </xsl:stylesheet>
            """);
        var mainPath = Write("main2.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
                <xsl:variable name="verbose" as="xs:boolean" static="yes" select="false()"/>
                <xsl:include href="included.xsl"/>
            </xsl:stylesheet>
            """);
        var r = await Run(mainPath, "<root/>");
        r.Should().Contain("included-quiet", $"actual={r}");
        r.Should().NotContain("included-verbose", $"actual={r}");
    }

    [Fact]
    public async Task TransitiveImport_StaticVar_VisibleAcrossThreeModules()
    {
        // A imports B imports C. A's static var should be visible in C's use-when.
        Write("c.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:template match="/" use-when="$mode = 'prod'">
                    <out>c-prod</out>
                </xsl:template>
                <xsl:template match="/" use-when="$mode = 'dev'">
                    <out>c-dev</out>
                </xsl:template>
            </xsl:stylesheet>
            """);
        Write("b-trans.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:import href="c.xsl"/>
            </xsl:stylesheet>
            """);
        var mainPath = Write("a-trans.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
                <xsl:variable name="mode" as="xs:string" static="yes" select="'prod'"/>
                <xsl:import href="b-trans.xsl"/>
            </xsl:stylesheet>
            """);
        var r = await Run(mainPath, "<root/>");
        r.Should().Contain("c-prod", $"actual={r}");
    }

    [Fact]
    public async Task NamespacedStaticVar_VisibleInImported_UseWhen()
    {
        // Static var declared in a non-default namespace; importer uses prefixed reference.
        Write("ns-imp.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:cfg="http://example.com/cfg" version="3.0">
                <xsl:template match="/" use-when="$cfg:enabled">
                    <on>on</on>
                </xsl:template>
                <xsl:template match="/" use-when="not($cfg:enabled)">
                    <off>off</off>
                </xsl:template>
            </xsl:stylesheet>
            """);
        var mainPath = Write("ns-main.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:cfg="http://example.com/cfg"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
                <xsl:variable name="cfg:enabled" as="xs:boolean" static="yes" select="true()"/>
                <xsl:import href="ns-imp.xsl"/>
            </xsl:stylesheet>
            """);
        var r = await Run(mainPath, "<root/>");
        r.Should().Contain(">on</on>", $"actual={r}");
        r.Should().NotContain(">off</off>", $"actual={r}");
    }

    [Fact]
    public async Task ImportedModule_OwnStaticVar_VisibleInImporter_Use_When()
    {
        // Module B (imported) defines a static var. Module A uses it in use-when AFTER the import.
        Write("imp.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
                <xsl:variable name="featureX" as="xs:boolean" static="yes" select="true()"/>
            </xsl:stylesheet>
            """);
        var mainPath = Write("main3.xsl", """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
                <xsl:import href="imp.xsl"/>
                <xsl:template match="/" use-when="$featureX">
                    <out>featureX-on</out>
                </xsl:template>
                <xsl:template match="/" use-when="not($featureX)">
                    <out>featureX-off</out>
                </xsl:template>
            </xsl:stylesheet>
            """);
        var r = await Run(mainPath, "<root/>");
        r.Should().Contain("featureX-on", $"actual={r}");
    }
}
