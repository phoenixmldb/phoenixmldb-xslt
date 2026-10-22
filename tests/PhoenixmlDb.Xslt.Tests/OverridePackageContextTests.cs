using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A function or template declared in xsl:override belongs to the using package (XSLT 3.0
/// §3.5.3.4), so key() in it finds the using package's keys, also when a component of the used
/// package calls it. It was given the used package's, so the key was not found (W3C override-f-026).
/// </summary>
public sealed class OverridePackageContextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pxovctx-" + Guid.NewGuid().ToString("N"));

    public OverridePackageContextTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private const string Library = """
        <xsl:package name="urn:lib" package-version="1.0.0" version="3.0"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:g="urn:g" exclude-result-prefixes="g">
          <xsl:function name="g:find" visibility="abstract" as="node()*">
            <xsl:param name="from" as="node()"/>
          </xsl:function>
          <xsl:function name="g:count" visibility="public">
            <xsl:param name="from" as="node()"/>
            <xsl:sequence select="count(g:find($from))"/>
          </xsl:function>
          <xsl:template name="g:show" visibility="public"><xsl:param name="from" as="node()"/><xsl:call-template name="g:ids"><xsl:with-param name="from" select="$from"/></xsl:call-template></xsl:template>
          <xsl:template name="g:ids" visibility="abstract"><xsl:param name="from" as="node()"/></xsl:template>
        </xsl:package>
        """;

    [Fact]
    public async Task Key_in_an_overriding_function_or_template_uses_the_using_package_keys()
    {
        var libraryPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libraryPath, Library);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:package name="urn:main" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:g="urn:g" exclude-result-prefixes="g">
              <xsl:key name="id" match="*" use="@id"/>
              <xsl:use-package name="urn:lib" package-version="1.0.0">
                <xsl:override>
                  <xsl:function name="g:find" visibility="public" as="node()*">
                    <xsl:param name="from" as="node()"/>
                    <xsl:sequence select="key('id', tokenize($from/@ref), root($from))"/>
                  </xsl:function>
                  <xsl:template name="g:ids" visibility="public"><xsl:param name="from" as="node()"/><xsl:value-of select="key('id', tokenize($from/@ref), root($from))/@id"/></xsl:template>
                </xsl:override>
              </xsl:use-package>
              <xsl:variable name="data"><n id="a" ref="b c"/><n id="b"/><n id="c"/></xsl:variable>
              <xsl:template name="main" visibility="public">
                <out count="{g:count($data/n[1])}"><xsl:call-template name="g:show"><xsl:with-param name="from" select="$data/n[1]"/></xsl:call-template></out>
              </xsl:template>
            </xsl:package>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null,
            new Dictionary<string, List<(string? Version, string FilePath)>> { ["urn:lib"] = [("1.0.0", libraryPath)] });
        t.SetInitialTemplate("main");
        (await t.TransformAsync("<in/>")).Should().Contain("<out count=\"2\">b c</out>");
    }
}
