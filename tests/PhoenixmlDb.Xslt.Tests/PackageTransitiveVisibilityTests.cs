using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §3.5.3.2: a component of a used package that no xsl:accept matches is private in the
/// using package when it is public or final in the used package, and hidden otherwise. So a
/// public component of C, used by B without xsl:accept, is private in B and hidden in A, which
/// uses B: A cannot reference it (xslt#544). A could call it, and could call B's private
/// templates. Expected results are Saxon-HE 12.10's for the same packages.
/// </summary>
public sealed class PackageTransitiveVisibilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-transvis-" + Guid.NewGuid().ToString("N"));

    public PackageTransitiveVisibilityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string C = """
        <xsl:package name="urn:c" package-version="1.0" version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:c="urn:c">
          <xsl:template name="c:t" visibility="public"><ct/></xsl:template>
          <xsl:function name="c:f" visibility="public"><xsl:sequence select="'cf'"/></xsl:function>
          <xsl:variable name="c:v" visibility="public" select="'cv'"/>
          <xsl:attribute-set name="c:as" visibility="public"><xsl:attribute name="x" select="'cas'"/></xsl:attribute-set>
        </xsl:package>
        """;

    private static string B(string accept) => $$"""
        <xsl:package name="urn:b" package-version="1.0" version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:c="urn:c">
          <xsl:use-package name="urn:c" package-version="1.0">{{accept}}</xsl:use-package>
          <xsl:template name="b" visibility="public"><b><xsl:call-template name="c:t"/><xsl:value-of select="c:f(), $c:v"/><e xsl:use-attribute-sets="c:as"/></b></xsl:template>
          <xsl:template name="c:pt" visibility="private"><pt/></xsl:template>
        </xsl:package>
        """;

    private const string AcceptAll = """<xsl:accept component="*" names="*" visibility="public"/>""";

    private async Task<string> RunAsync(string bAccept, string aAccept, string body)
    {
        var cPath = Path.Combine(_dir, "c.xsl");
        var bPath = Path.Combine(_dir, "b.xsl");
        await File.WriteAllTextAsync(cPath, C, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(bPath, B(bAccept), TestContext.Current.CancellationToken);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>>
        {
            ["urn:c"] = new() { ("1.0", cPath) },
            ["urn:b"] = new() { ("1.0", bPath) },
        };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:c="urn:c" exclude-result-prefixes="c">
              <xsl:use-package name="urn:b" package-version="1.0">{{aAccept}}</xsl:use-package>
              <xsl:template name="main"><out>{{body}}</out></xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "a.xsl")), null, catalog);
        t.SetInitialTemplate("main");
        return (await t.TransformAsync("<in/>")).Trim();
    }

    private async Task<string> ErrorAsync(string bAccept, string aAccept, string body)
    {
        var run = () => RunAsync(bAccept, aAccept, body);
        return (await run.Should().ThrowAsync<XsltException>()).Which.Message;
    }

    /// <summary>B's own components still reference what B took from C.</summary>
    [Fact]
    public async Task The_package_between_references_what_it_took()
        => (await RunAsync("", "", """<xsl:call-template name="b"/>""")).Should().Be("""<out><b xmlns:c="urn:c"><ct/>cf cv<e x="cas"/></b></out>""");

    [Theory]
    [InlineData("""<xsl:call-template name="c:t"/>""", "XTSE0650")]
    [InlineData("""<xsl:value-of select="c:f()"/>""", "XPST0017")]
    [InlineData("""<xsl:value-of select="$c:v"/>""", "XPST0008")]
    [InlineData("""<e xsl:use-attribute-sets="c:as"/>""", "XTSE0710")]
    public async Task A_component_the_package_between_did_not_accept_is_not_visible_two_levels_up(string body, string code)
        => (await ErrorAsync("", "", body)).Should().StartWith(code);

    [Fact]
    public async Task Accepted_as_public_by_the_package_between_it_is_visible()
        => (await RunAsync(AcceptAll, "", """<xsl:call-template name="c:t"/><xsl:value-of select="c:f(), $c:v"/><e xsl:use-attribute-sets="c:as"/>"""))
            .Should().Be("""<out><ct xmlns:c="urn:c"/>cf cv<e x="cas"/></out>""");

    /// <summary>A private template of a used package was callable from the using package.</summary>
    [Fact]
    public async Task A_private_template_of_a_used_package_is_not_visible()
        => (await ErrorAsync(AcceptAll, "", """<xsl:call-template name="c:pt"/>""")).Should().StartWith("XTSE0650");

    /// <summary>
    /// A private component cannot be accepted (§3.5.3.2, the table): a wildcard does not match it,
    /// and naming it is XTSE3040.
    /// </summary>
    [Fact]
    public async Task A_wildcard_accept_does_not_reach_a_component_private_in_the_used_package()
        => (await ErrorAsync("", AcceptAll, """<xsl:call-template name="c:t"/>""")).Should().StartWith("XTSE0650");

    [Fact]
    public async Task Accepting_a_component_private_in_the_used_package_by_name_is_XTSE3040()
        => (await ErrorAsync("", """<xsl:accept component="template" names="c:t" visibility="public"/>""", """<xsl:call-template name="c:t"/>"""))
            .Should().StartWith("XTSE3040");

    [Fact]
    public async Task Accepted_twice_as_public_a_component_is_visible_two_levels_up()
        => (await RunAsync(AcceptAll, AcceptAll, """<xsl:call-template name="c:t"/>""")).Should().Be("""<out><ct xmlns:c="urn:c"/></out>""");
}
