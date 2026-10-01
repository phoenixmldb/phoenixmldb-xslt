using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A private global of a used package is invisible to the using package, so a global of the same
/// name there is not a duplicate (W3C expose-008/009). Each package keeps its own binding.
/// </summary>
public sealed class PackagePrivateVariableNameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-ppv-" + Guid.NewGuid().ToString("N"));

    public PackagePrivateVariableNameTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Each_package_resolves_its_own_same_named_global()
    {
        var used = Path.Combine(_dir, "used.xsl");
        await File.WriteAllTextAsync(used, """
            <xsl:package name="urn:used" package-version="1.0.0" version="3.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:u="urn:used">
              <xsl:variable name="test" select="'used'"/>
              <xsl:function name="u:which" visibility="public"><xsl:sequence select="$test"/></xsl:function>
            </xsl:package>
            """);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:package name="urn:main" package-version="1.0.0" version="3.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:u="urn:used">
              <xsl:use-package name="urn:used" package-version="1.0.0"/>
              <xsl:variable name="test" select="'main'"/>
              <xsl:template name="main" visibility="public"><out main="{$test}" used="{u:which()}"/></xsl:template>
            </xsl:package>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null,
            new Dictionary<string, List<(string? Version, string FilePath)>> { ["urn:used"] = [("1.0.0", used)] });
        t.SetInitialTemplate("main");
        (await t.TransformAsync((string?)null)).Should().Contain("main=\"main\" used=\"used\"");
    }
}
