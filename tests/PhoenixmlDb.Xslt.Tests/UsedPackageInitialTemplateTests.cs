using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A public template of a used package is private in the using package unless an xsl:accept rule
/// raises it (XSLT 3.0 §3.6.2.4), so it cannot be the principal package's initial template
/// (XTDE0040). The check read the template's own visibility="public" and ran it (W3C accept-913).
/// </summary>
public sealed class UsedPackageInitialTemplateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pxinit-" + Guid.NewGuid().ToString("N"));

    public UsedPackageInitialTemplateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> RunAsync(string accept)
    {
        var libPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libPath, """
            <xsl:package name="urn:lib" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="xsl:initial-template" visibility="public"><out/></xsl:template>
            </xsl:package>
            """);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>>
        {
            ["urn:lib"] = [("1.0.0", libPath)],
        };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:package name="urn:main" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:use-package name="urn:lib">{accept}</xsl:use-package>
            </xsl:package>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null, catalog);
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task An_unaccepted_template_of_a_used_package_is_not_an_entry_point()
    {
        var act = () => RunAsync("");
        (await act.Should().ThrowAsync<XsltException>()).WithMessage("*XTDE0040*");
    }

    [Fact]
    public async Task A_template_accepted_as_public_is_an_entry_point()
        => (await RunAsync("""<xsl:accept component="template" names="xsl:initial-template" visibility="public"/>"""))
            .Should().Contain("<out");
}
