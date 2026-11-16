using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// fn:function-lookup() sees the stylesheet functions as the package that calls it sees them
/// (W3C function-lookup-004/005). It found every function of every package: a private function
/// of a used package, a function of the using package from inside the used one, an abstract
/// function, and, for an overridden function, the override. Inside a used package it finds the
/// package's own declaration, as Saxon-HE 12.10 does; the expected values are Saxon's.
/// </summary>
public sealed class FunctionLookupPackageScopeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-fnlookup-" + Guid.NewGuid().ToString("N"));

    public FunctionLookupPackageScopeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Library = """
        <xsl:package name="urn:lib" package-version="1.0" version="3.0" expand-text="yes"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="#all">
          <xsl:template name="sub" visibility="public">
            <sub mul="{exists(function-lookup(QName('urn:f', 'mul'), 2))}"
                 div="{exists(function-lookup(QName('urn:f', 'div'), 2))}"
                 add="{function-lookup(QName('urn:f', 'add'), 2)(3, 4)}"
                 sub="{exists(function-lookup(QName('urn:f', 'sub'), 2))}"/>
          </xsl:template>
          <xsl:function name="f:mul" visibility="private"><xsl:param name="x"/><xsl:param name="y"/><xsl:sequence select="$x * $y"/></xsl:function>
          <xsl:function name="f:add" visibility="public"><xsl:param name="x"/><xsl:param name="y"/><xsl:sequence select="$x + $y"/></xsl:function>
          <xsl:function name="f:sub" visibility="abstract"><xsl:param name="x"/><xsl:param name="y"/></xsl:function>
        </xsl:package>
        """;

    private async Task<string> Run(string body)
    {
        var libPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libPath, Library, TestContext.Current.CancellationToken);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>> { ["urn:lib"] = new() { ("1.0", libPath) } };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:package name="urn:main" package-version="1.0" version="3.0" expand-text="yes"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:use-package name="urn:lib">
                <xsl:override>
                  <xsl:function name="f:add" visibility="public"><xsl:param name="x"/><xsl:param name="y"/><xsl:sequence select="$x + $y + 1"/></xsl:function>
                  <xsl:function name="f:sub" visibility="public"><xsl:param name="x"/><xsl:param name="y"/><xsl:sequence select="$x - $y"/></xsl:function>
                </xsl:override>
              </xsl:use-package>
              <xsl:function name="f:div" visibility="public"><xsl:param name="x"/><xsl:param name="y"/><xsl:sequence select="$x div $y"/></xsl:function>
              <xsl:template name="main" visibility="public">{{body}}</xsl:template>
            </xsl:package>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null, catalog);
        t.SetInitialTemplate("main");
        return (await t.TransformAsync("<in/>")).Trim();
    }

    [Fact]
    public async Task Inside_the_used_package_it_sees_that_package_s_own_functions()
        => (await Run("<xsl:call-template name='sub'/>"))
            .Should().Be("<sub mul=\"true\" div=\"false\" add=\"7\" sub=\"false\"/>");

    [Fact]
    public async Task The_using_package_sees_its_own_functions_and_the_overrides_but_not_a_private_one()
        => (await Run("""<out mul="{exists(function-lookup(QName('urn:f', 'mul'), 2))}" div="{function-lookup(QName('urn:f', 'div'), 2)(8, 2)}" add="{function-lookup(QName('urn:f', 'add'), 2)(3, 4)}" sub="{function-lookup(QName('urn:f', 'sub'), 2)(3, 4)}"/>"""))
            .Should().Be("<out mul=\"false\" div=\"4\" add=\"8\" sub=\"-1\"/>");
}
