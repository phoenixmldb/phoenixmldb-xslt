using FluentAssertions;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §3.5.2.5: xsl:expose DEFINES the visibility of a component declared without a
/// visibility attribute, and can only REDUCE it where the attribute is present. An inconsistent
/// combination is XTSE3010 when the component is named explicitly; by wildcard, the declared
/// visibility stands (W3C expose-909/-910/-911/-913). And redeclaring a used package's exposed
/// template outside xsl:override is XTSE3050 (override-t-008/-009), which the parser never checked
/// for templates.
/// </summary>
public sealed class PackageExposeVisibilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pxexpose-" + Guid.NewGuid().ToString("N"));

    public PackageExposeVisibilityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> RunAsync(string usedPackage, string principal)
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "used.xsl"), usedPackage);
        var principalPath = Path.Combine(_dir, "principal.xsl");
        await File.WriteAllTextAsync(principalPath, principal);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>>
        {
            ["urn:used"] = new() { ("1.0.0", Path.Combine(_dir, "used.xsl")) },
        };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principal, new Uri(principalPath), null, catalog);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    private static string Used(string expose, string templates) => $$"""
        <xsl:package name="urn:used" package-version="1.0.0" version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          {{expose}}
          {{templates}}
        </xsl:package>
        """;

    private static string Principal(string body) => $$"""
        <xsl:package name="urn:principal" package-version="1.0.0" version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:use-package name="urn:used" package-version="1.0.0"/>
          {{body}}
        </xsl:package>
        """;

    [Fact]
    public async Task A_wildcard_expose_does_not_raise_a_declared_private_template()
    {
        // accept-A's shape: main is declared private, expose names="*" says public. The declared
        // visibility stands, so the using package may declare its own main.
        var result = await RunAsync(
            Used("""<xsl:expose component="template" names="*" visibility="public"/>""",
                 """<xsl:template name="main" visibility="private"><used/></xsl:template>"""),
            Principal("""<xsl:template name="main" visibility="public"><own/></xsl:template>"""));
        result.Should().Contain("<own");
    }

    [Fact]
    public async Task An_explicit_expose_inconsistent_with_the_declaration_is_XTSE3010()
    {
        var act = () => RunAsync(
            Used("""<xsl:expose component="template" names="t" visibility="public"/>""",
                 """<xsl:template name="t" visibility="private"/>"""),
            Principal("""<xsl:template name="main" visibility="public"><own/></xsl:template>"""));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE3010");
    }

    [Fact]
    public async Task Expose_may_reduce_a_declared_visibility()
    {
        // public reduced to private by expose: the using package cannot see it, so its own
        // declaration of the same name does not conflict.
        var result = await RunAsync(
            Used("""<xsl:expose component="template" names="t" visibility="private"/>""",
                 """<xsl:template name="t" visibility="public"><used/></xsl:template>"""),
            Principal("""
                <xsl:template name="t" visibility="public"><own/></xsl:template>
                <xsl:template name="main" visibility="public"><xsl:call-template name="t"/></xsl:template>
                """));
        result.Should().Contain("<own");
    }

    [Fact]
    public async Task Redeclaring_a_used_public_template_outside_override_is_XTSE3050()
    {
        var act = () => RunAsync(
            Used("", """<xsl:template name="t" visibility="public"><used/></xsl:template>"""),
            Principal("""
                <xsl:template name="t" visibility="public"><own/></xsl:template>
                <xsl:template name="main" visibility="public"><xsl:call-template name="t"/></xsl:template>
                """));
        (await act.Should().ThrowAsync<XsltException>()).Which.Message.Should().Contain("XTSE3050");
    }

    [Fact]
    public async Task Overriding_it_inside_xsl_override_is_fine()
    {
        var result = await RunAsync(
            Used("", """<xsl:template name="t" visibility="public"><used/></xsl:template>"""),
            """
            <xsl:package name="urn:principal" package-version="1.0.0" version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:use-package name="urn:used" package-version="1.0.0">
                <xsl:override><xsl:template name="t" visibility="public"><own/></xsl:template></xsl:override>
              </xsl:use-package>
              <xsl:template name="main" visibility="public"><xsl:call-template name="t"/></xsl:template>
            </xsl:package>
            """);
        result.Should().Contain("<own");
    }
}
