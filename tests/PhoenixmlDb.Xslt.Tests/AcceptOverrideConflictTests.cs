using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A non-wildcard name in xsl:accept that matches a component declared in an xsl:override child of
/// the same xsl:use-package is XTSE3051 (XSLT 3.0 §3.6.2.4). Templates were checked; functions were
/// not, so accepting and overriding local:foo#0 compiled and ran the override (W3C accept-916).
/// </summary>
public sealed class AcceptOverrideConflictTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pxaccept-" + Guid.NewGuid().ToString("N"));

    public AcceptOverrideConflictTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> RunAsync(string acceptNames)
    {
        var libPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libPath, """
            <xsl:package name="urn:lib" package-version="1.0.0" version="3.0"
              xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f">
              <xsl:function name="f:foo" visibility="public"><xsl:sequence select="'foo'"/></xsl:function>
              <xsl:function name="f:bar" visibility="public"><xsl:sequence select="'bar'"/></xsl:function>
            </xsl:package>
            """);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>>
        {
            ["urn:lib"] = [("1.0.0", libPath)],
        };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f">
              <xsl:use-package name="urn:lib">
                <xsl:accept component="function" names="{acceptNames}" visibility="private"/>
                <xsl:override>
                  <xsl:function name="f:foo"><xsl:sequence select="'foo2'"/></xsl:function>
                </xsl:override>
              </xsl:use-package>
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template"><xsl:value-of select="f:foo()"/></xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null, catalog);
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("f:foo#0")]
    [InlineData("f:foo")]
    public async Task Accepting_an_overridden_function_is_XTSE3051(string names)
    {
        var act = () => RunAsync(names);
        (await act.Should().ThrowAsync<XsltException>()).WithMessage("*XTSE3051*");
    }

    [Theory]
    [InlineData("f:bar")]
    [InlineData("f:*")]
    public async Task Accepting_another_function_or_a_wildcard_is_allowed(string names)
        => (await RunAsync(names)).Should().Be("foo2");
}
