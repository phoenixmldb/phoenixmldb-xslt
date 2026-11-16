using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:accept does not apply to a component the same xsl:use-package overrides: the override is
/// the using package's own component, with its own visibility (XSLT 3.0 §3.5.3.3). names="*"
/// visibility="hidden" hid the override too, so a package used twice, overriding t in one use
/// and u in the other, had neither: XTSE0650 (W3C override-t-014). Expected values are
/// Saxon-HE 12.10's.
/// </summary>
public sealed class AcceptSkipsOverriddenTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-accovr-" + Guid.NewGuid().ToString("N"));

    public AcceptSkipsOverriddenTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Library = """
        <xsl:package name="urn:lib" package-version="1.0" version="3.0"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="#all">
          <xsl:template name="t" visibility="public"><t/></xsl:template>
          <xsl:template name="u" visibility="public"><u/></xsl:template>
          <xsl:function name="f:g" visibility="public"><xsl:sequence select="'lib'"/></xsl:function>
        </xsl:package>
        """;

    private async Task<string> Run(string uses, string body)
    {
        var libPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libPath, Library, TestContext.Current.CancellationToken);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>> { ["urn:lib"] = new() { ("1.0", libPath) } };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:package name="urn:main" package-version="1.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="#all">
              {{uses}}
              <xsl:template name="main" visibility="public"><out>{{body}}</out></xsl:template>
            </xsl:package>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null, catalog);
        t.SetInitialTemplate("main");
        return (await t.TransformAsync("<in/>")).Trim();
    }

    [Fact]
    public async Task Each_use_keeps_its_override()
        => (await Run("""
                <xsl:use-package name="urn:lib"><xsl:accept component="*" names="*" visibility="hidden"/>
                  <xsl:override><xsl:template name="t" visibility="public"><t2/></xsl:template></xsl:override></xsl:use-package>
                <xsl:use-package name="urn:lib"><xsl:accept component="*" names="*" visibility="hidden"/>
                  <xsl:override><xsl:template name="u" visibility="public"><u2/></xsl:template></xsl:override></xsl:use-package>
                """, "<xsl:call-template name='t'/><xsl:call-template name='u'/>"))
            .Should().Be("<out><t2/><u2/></out>");

    [Fact]
    public async Task A_function_override_is_not_hidden_by_a_wildcard_accept()
        => (await Run("""
                <xsl:use-package name="urn:lib"><xsl:accept component="function" names="*" visibility="hidden"/>
                  <xsl:override><xsl:function name="f:g" visibility="public"><xsl:sequence select="'override'"/></xsl:function></xsl:override></xsl:use-package>
                """, "<xsl:value-of select='f:g()'/>"))
            .Should().Be("<out>override</out>");
}
