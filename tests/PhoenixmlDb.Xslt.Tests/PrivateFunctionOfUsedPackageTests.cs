using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A function that is private in its own package, or hidden by xsl:accept, is not a component of
/// the using package (XSLT 3.0 §3.5.3.2): a call to it there is the static error XPST0017, also
/// where the call never runs. It ran (W3C use-package-003, expose-003). A function accepted as
/// private is the using package's to call. Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class PrivateFunctionOfUsedPackageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-privfn-" + Guid.NewGuid().ToString("N"));

    public PrivateFunctionOfUsedPackageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Library = """
        <xsl:package name="urn:lib" package-version="1.0" version="3.0"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p" exclude-result-prefixes="#all">
          <xsl:function name="p:pub" visibility="public"><xsl:sequence select="p:priv()"/></xsl:function>
          <xsl:function name="p:priv" visibility="private"><xsl:sequence select="'private'"/></xsl:function>
          <xsl:function name="p:def"><xsl:sequence select="'default'"/></xsl:function>
          <xsl:function name="p:pub2" visibility="public"><xsl:sequence select="'pub2'"/></xsl:function>
        </xsl:package>
        """;

    private async Task<string> Run(string use, string body, string extra = "")
    {
        var libPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libPath, Library, TestContext.Current.CancellationToken);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>> { ["urn:lib"] = new() { ("1.0", libPath) } };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:package name="urn:main" package-version="1.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p" exclude-result-prefixes="#all">
              {{use}}
              <xsl:template name="main" visibility="public"><out><xsl:value-of select="{{body}}"/></out></xsl:template>
              {{extra}}
            </xsl:package>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null, catalog);
        t.SetInitialTemplate("main");
        return (await t.TransformAsync("<in/>")).Trim();
    }

    private const string Use = "<xsl:use-package name='urn:lib'/>";

    [Fact]
    public async Task A_public_function_that_calls_a_private_one_is_callable()
        => (await Run(Use, "p:pub()")).Should().Be("<out>private</out>");

    [Theory]
    [InlineData("<xsl:use-package name='urn:lib'/>", "p:priv()", "")]
    [InlineData("<xsl:use-package name='urn:lib'/>", "p:def()", "")]
    // A call that never runs is still an error: it is static.
    [InlineData("<xsl:use-package name='urn:lib'/>", "'x'", "<xsl:template name='never'><xsl:sequence select='p:priv()'/></xsl:template>")]
    [InlineData("<xsl:use-package name='urn:lib'><xsl:accept component='function' names='p:pub2#0' visibility='hidden'/></xsl:use-package>", "p:pub2()", "")]
    public async Task A_call_to_a_function_the_package_cannot_see_is_XPST0017(string use, string body, string extra)
        => (await FluentActions.Awaiting(() => Run(use, body, extra)).Should().ThrowAsync<Exception>())
            .Which.Message.Should().Contain("XPST0017");

    [Fact]
    public async Task A_function_accepted_as_private_is_callable()
        => (await Run("<xsl:use-package name='urn:lib'><xsl:accept component='function' names='p:pub2#0' visibility='private'/></xsl:use-package>", "p:pub2()"))
            .Should().Be("<out>pub2</out>");
}
