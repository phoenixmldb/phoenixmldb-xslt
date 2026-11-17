using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:accept (XSLT 3.0 §3.5.3.2), xslt#543:
/// <list type="bullet">
/// <item>A function is named with its arity: <c>&lt;xsl:accept component="function"
/// names="my-public-func#1" visibility="public"/&gt;</c>. A name without an arity identifies no
/// function (§3.5.3.1 as draft erratum XT30.E36 states it, which the W3C suite follows), so it
/// matches no component: XTSE3030, and the message says to add the arity. It was taken as every
/// arity of the name.</item>
/// <item>A function name with an arity must match a function of the used package (XTSE3030), as
/// a template or variable name already had to. It was accepted silently.</item>
/// <item>component, names and visibility are required (XTSE0010). Without one, the accept was
/// ignored.</item>
/// </list>
/// Saxon-HE 12.10 reports XTSE3020 for a name without an arity and XTSE3030 for a missing
/// function; the code here is the one §3.5.3.2 gives for xsl:accept.
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
          <xsl:function name="p:my-public-func" visibility="public"><xsl:param name="x"/><xsl:sequence select="$x * 2"/></xsl:function>
          <xsl:function name="p:my-public-func" visibility="public"><xsl:sequence select="0"/></xsl:function>
        </xsl:package>
        """;

    private async Task<string> RunAsync(string accept)
    {
        var libPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libPath, Library, TestContext.Current.CancellationToken);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>> { ["urn:lib"] = new() { ("1.0", libPath) } };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p" exclude-result-prefixes="p">
              <xsl:use-package name="urn:lib" package-version="1.0">
                {{accept}}
              </xsl:use-package>
              <xsl:template name="main"><out><xsl:value-of select="p:my-public-func(21)"/></out></xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")), null, catalog);
        t.SetInitialTemplate("main");
        return (await t.TransformAsync("<in/>")).Trim();
    }

    private static string Accept(string names) => $"""<xsl:accept component="function" names="{names}" visibility="public"/>""";

    private async Task<string> ErrorAsync(string accept)
    {
        var run = () => RunAsync(accept);
        return (await run.Should().ThrowAsync<XsltException>()).Which.Message;
    }

    [Theory]
    [InlineData("p:my-public-func#1")]
    [InlineData("p:my-public-func#1 p:my-public-func#0")]
    [InlineData("p:*")]
    [InlineData("*")]
    public async Task A_function_named_with_its_arity_or_a_wildcard_is_accepted(string names)
        => (await RunAsync(Accept(names))).Should().Be("<out>42</out>");

    [Theory]
    [InlineData("p:my-public-func")]
    [InlineData("Q{urn:p}my-public-func")]
    public async Task A_function_name_without_an_arity_is_XTSE3030_and_the_message_says_to_add_it(string names)
    {
        var message = await ErrorAsync(Accept(names));
        message.Should().StartWith("XTSE3030");
        message.Should().Contain(names + "#1");
    }

    [Theory]
    [InlineData("p:my-public-func#5")]
    [InlineData("p:other#1")]
    public async Task A_function_the_used_package_does_not_have_is_XTSE3030(string names)
        => (await ErrorAsync(Accept(names))).Should().StartWith("XTSE3030");

    [Theory]
    [InlineData("""<xsl:accept names="p:my-public-func#1" visibility="public"/>""", "component")]
    [InlineData("""<xsl:accept component="function" visibility="public"/>""", "names")]
    [InlineData("""<xsl:accept component="function" names="p:my-public-func#1"/>""", "visibility")]
    public async Task An_accept_without_a_required_attribute_is_XTSE0010(string accept, string attribute)
    {
        var message = await ErrorAsync(accept);
        message.Should().StartWith("XTSE0010");
        message.Should().Contain(attribute);
    }
}
