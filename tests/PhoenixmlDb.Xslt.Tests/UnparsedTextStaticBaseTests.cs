using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A relative URI passed to <c>fn:unparsed-text</c> and its siblings resolves against the static
/// base URI of the module that CONTAINS the call, and a resource that cannot be read raises
/// FOUT1170 (xslt#195).
/// </summary>
/// <remarks>
/// The functions resolved against the principal stylesheet's base URI, so a call in an included
/// module in another directory read the principal's neighbour instead of its own (XSpec's
/// <c>unparsed-text('VERSION')</c> in common/ printed the compiler's version). They also caught
/// every failure and returned the empty sequence, so a missing file read as "" instead of an
/// error. Both directories hold a VERSION file with different content, so the old and new rules
/// predict different outputs.
/// </remarks>
public sealed class UnparsedTextStaticBaseTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "phx-utbase-" + Guid.NewGuid().ToString("N"));

    public UnparsedTextStaticBaseTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "main"));
        Directory.CreateDirectory(Path.Combine(_root, "lib"));
        File.WriteAllText(Path.Combine(_root, "main", "VERSION"), "MAIN");
        File.WriteAllText(Path.Combine(_root, "lib", "VERSION"), "LIB");
        File.WriteAllText(Path.Combine(_root, "lib", "lib.xsl"), """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:err="http://www.w3.org/2005/xqt-errors" exclude-result-prefixes="#all">
              <xsl:variable name="global" select="unparsed-text('VERSION')"/>
              <xsl:template name="probe">
                <global><xsl:value-of select="$global"/></global>
                <local><xsl:value-of select="unparsed-text('VERSION', 'utf-8')"/></local>
                <lines><xsl:value-of select="unparsed-text-lines('VERSION')"/></lines>
                <lines2><xsl:value-of select="unparsed-text-lines('VERSION', 'utf-8')"/></lines2>
                <avail><xsl:value-of select="unparsed-text-available('VERSION')"/></avail>
                <avail-lib-only><xsl:value-of select="unparsed-text-available('lib.xsl')"/></avail-lib-only>
                <missing><xsl:try><xsl:value-of select="'[' || unparsed-text('nope.txt') || ']'"/><xsl:catch><xsl:value-of select="$err:code"/></xsl:catch></xsl:try></missing>
                <missing-lines><xsl:try><xsl:value-of select="'[' || string-join(unparsed-text-lines('nope.txt')) || ']'"/><xsl:catch><xsl:value-of select="$err:code"/></xsl:catch></xsl:try></missing-lines>
                <avail-missing><xsl:value-of select="unparsed-text-available('nope.txt')"/></avail-missing>
              </xsl:template>
            </xsl:stylesheet>
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<string> RunAsync()
    {
        var path = Path.Combine(_root, "main", "main.xsl");
        const string xsl = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:include href="../lib/lib.xsl"/>
              <xsl:template name="main"><r><xsl:call-template name="probe"/></r></xsl:template>
            </xsl:stylesheet>
            """;
        await File.WriteAllTextAsync(path, xsl);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl, new Uri(path));
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task RelativeUri_ResolvesAgainstTheCallingModule()
    {
        var result = await RunAsync();
        result.Should().Contain("<global>LIB</global>")
            .And.Contain("<local>LIB</local>")
            .And.Contain("<lines>LIB</lines>")
            .And.Contain("<lines2>LIB</lines2>")
            .And.Contain("<avail>true</avail>")
            .And.Contain("<avail-lib-only>true</avail-lib-only>");
    }

    [Fact]
    public async Task MissingResource_RaisesFOUT1170()
    {
        var result = await RunAsync();
        result.Should().Contain("<missing>err:FOUT1170</missing>")
            .And.Contain("<missing-lines>err:FOUT1170</missing-lines>")
            .And.Contain("<avail-missing>false</avail-missing>");
    }
}
