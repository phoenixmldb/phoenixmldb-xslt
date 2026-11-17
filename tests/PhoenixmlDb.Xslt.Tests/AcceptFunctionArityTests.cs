using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:accept with component="function": a name must match a function of the used package
/// (XTSE3030, §3.5.3.2), as a template or variable name already had to. A name with an arity
/// that no function has, or a name no function has, was accepted silently (xslt#543).
/// Saxon-HE 12.10 reports XTSE3030 for these too. A name without an arity still matches the
/// function of every arity; whether it should be an error (draft erratum XT30.E36) is open.
/// </summary>
public sealed class AcceptFunctionArityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-accarity-" + Guid.NewGuid().ToString("N"));

    public AcceptFunctionArityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Library = """
        <xsl:package name="urn:lib" package-version="1.0" version="3.0"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p">
          <xsl:function name="p:f" visibility="public"><xsl:param name="x"/><xsl:sequence select="$x * 2"/></xsl:function>
          <xsl:function name="p:f" visibility="public"><xsl:sequence select="0"/></xsl:function>
        </xsl:package>
        """;

    private async Task<string> Run(string names)
    {
        var libPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libPath, Library, TestContext.Current.CancellationToken);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>> { ["urn:lib"] = new() { ("1.0", libPath) } };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p" exclude-result-prefixes="p">
              <xsl:use-package name="urn:lib" package-version="1.0">
                <xsl:accept component="function" names="{{names}}" visibility="public"/>
              </xsl:use-package>
              <xsl:template name="main"><out><xsl:value-of select="p:f(21)"/></out></xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null, catalog);
        t.SetInitialTemplate("main");
        return (await t.TransformAsync("<in/>")).Trim();
    }

    [Theory]
    [InlineData("p:g")]
    [InlineData("Q{urn:p}g")]
    [InlineData("p:f#5")]
    [InlineData("p:g#1")]
    public async Task A_name_that_identifies_no_function_is_XTSE3030(string names)
    {
        var run = () => Run(names);
        (await run.Should().ThrowAsync<XsltException>()).Which.Message.Should().StartWith("XTSE3030");
    }

    [Theory]
    [InlineData("p:f#1")]
    [InlineData("p:f#1 p:f#0")]
    [InlineData("p:f")]
    [InlineData("p:*")]
    [InlineData("*")]
    public async Task A_name_with_its_arity_or_a_wildcard_is_accepted(string names)
        => (await Run(names)).Should().Contain("<out>42</out>");
}
