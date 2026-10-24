using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Only a public or abstract component can be overridden (XSLT 3.0 §3.5.3.4, XTSE3060). Templates,
/// functions and attribute sets were checked; a final variable was overridden, and the error
/// surfaced later as an unrelated XPST0008 (W3C accept-007).
/// </summary>
public sealed class VariableOverrideVisibilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pxvarover-" + Guid.NewGuid().ToString("N"));

    public VariableOverrideVisibilityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> RunAsync(string visibility)
    {
        var libPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libPath, $"""
            <xsl:package name="urn:lib" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:expose component="variable" names="*" visibility="{visibility}"/>
              <xsl:variable name="v" select="1"/>
            </xsl:package>
            """);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>>
        {
            ["urn:lib"] = [("1.0.0", libPath)],
        };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:use-package name="urn:lib">
                <xsl:override><xsl:variable name="v" select="2"/></xsl:override>
              </xsl:use-package>
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template"><xsl:value-of select="$v"/></xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null, catalog);
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task Overriding_a_final_variable_is_XTSE3060()
    {
        var act = () => RunAsync("final");
        (await act.Should().ThrowAsync<XsltException>()).WithMessage("*XTSE3060*");
    }

    [Fact]
    public async Task Overriding_a_public_variable_is_allowed()
        => (await RunAsync("public")).Should().Be("2");
}
