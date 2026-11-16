using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// key#3 with a name no xsl:key in the calling package declares is XTDE1260, as key#2 is. It
/// gave the empty sequence, so a key declared in a used package, which is local to that package
/// (XSLT 3.0 §3.6.2), seemed to be found and to match nothing (W3C use-package-105). Expected
/// values are Saxon-HE 12.10's.
/// </summary>
public sealed class Key3UndeclaredTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-key3-" + Guid.NewGuid().ToString("N"));

    public Key3UndeclaredTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Library = """
        <xsl:package name="urn:lib" package-version="1.0" version="3.0"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p" exclude-result-prefixes="#all">
          <xsl:key name="k" match="row" use="@id"/>
          <xsl:function name="p:find" visibility="public"><xsl:param name="d"/><xsl:sequence select="key('k', 'a', $d)"/></xsl:function>
        </xsl:package>
        """;

    private async Task<string> Run(string body, string declarations = "")
    {
        var libPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libPath, Library, TestContext.Current.CancellationToken);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>> { ["urn:lib"] = new() { ("1.0", libPath) } };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:package name="urn:main" package-version="1.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p" exclude-result-prefixes="#all">
              <xsl:use-package name="urn:lib"/>
              {{declarations}}
              <xsl:variable name="d"><row id="a">one</row><row id="b">two</row></xsl:variable>
              <xsl:template name="main" visibility="public"><out>{{body}}</out></xsl:template>
            </xsl:package>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null, catalog);
        t.SetInitialTemplate("main");
        return (await t.TransformAsync("<in/>")).Trim();
    }

    [Fact]
    public async Task The_used_package_finds_its_own_key()
        => (await Run("<xsl:value-of select='p:find($d)'/>")).Should().Be("<out>one</out>");

    [Fact]
    public async Task The_using_package_does_not_see_the_used_package_s_key()
        => (await FluentActions.Awaiting(() => Run("<xsl:value-of select=\"key('k', 'a', $d)\"/>")).Should().ThrowAsync<Exception>())
            .Which.Message.Should().Contain("XTDE1260");

    [Fact]
    public async Task A_key_of_its_own_with_the_same_name_is_its_own()
        => (await Run("<xsl:value-of select=\"key('k', 'two', $d)/@id\"/>", "<xsl:key name='k' match='row' use='.'/>"))
            .Should().Be("<out>b</out>");
}
