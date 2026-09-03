using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xsl:include/@href</c> and <c>xsl:import/@href</c> resolve against the EFFECTIVE base URI of
/// the element carrying them, which an <c>xml:base</c> attribute overrides (XML Base; XSLT 3.0
/// §3.2).
/// </summary>
/// <remarks>
/// The parser used <c>XElement.BaseUri</c>, which reports the base URI of the DOCUMENT the
/// element was read from and knows nothing about xml:base attributes. So
/// <c>&lt;xsl:include href="demo.xsl" xml:base="../other/"/&gt;</c> resolved against the
/// including module's own directory and failed as XTSE0165, even though the target existed
/// exactly where xml:base pointed. The parser already had ResolveEffectiveBaseUri, which walks
/// ancestors collecting xml:base — it simply was not used on this path (xspec issue 1135).
/// </remarks>
public class IncludeXmlBaseTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "phx-xmlbase-" + Guid.NewGuid().ToString("N"));

    public IncludeXmlBaseTests()
    {
        // The included module lives in a SIBLING directory, reachable only via xml:base.
        Directory.CreateDirectory(Path.Combine(_root, "main"));
        Directory.CreateDirectory(Path.Combine(_root, "lib"));
        File.WriteAllText(Path.Combine(_root, "lib", "helper.xsl"),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<xsl:stylesheet version=\"3.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\"\n"
            + "  xmlns:h=\"urn:h\" xmlns:xs=\"http://www.w3.org/2001/XMLSchema\">\n"
            + "  <xsl:function name=\"h:greet\" as=\"xs:string\">"
            + "<xsl:sequence select=\"'from-lib'\"/></xsl:function>\n"
            + "</xsl:stylesheet>");
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<(Exception? Error, string Result)> RunAsync(string includeElement)
    {
        var path = Path.Combine(_root, "main", "principal.xsl");
        var xsl =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<xsl:stylesheet version=\"3.0\" xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\"\n"
            + "  xmlns:h=\"urn:h\" exclude-result-prefixes=\"#all\">\n"
            + includeElement + "\n"
            + "  <xsl:template name=\"main\"><r><xsl:value-of select=\"h:greet()\"/></r></xsl:template>\n"
            + "</xsl:stylesheet>";
        await File.WriteAllTextAsync(path, xsl);

        var result = "";
        var error = await Record.ExceptionAsync(async () =>
        {
            var t = new PhoenixmlDb.Xslt.XsltTransformer();
            await t.LoadStylesheetAsync(xsl, new Uri(path));
            t.SetInitialTemplate("main");
            result = await t.TransformAsync((string?)null);
        });
        return (error, result);
    }

    /// <summary>The defect: xml:base on xsl:include was ignored, so the href missed.</summary>
    [Fact]
    public async Task Include_HonoursXmlBaseOnTheElement()
    {
        var (error, result) = await RunAsync(
            "  <xsl:include href=\"helper.xsl\" xml:base=\"../lib/\"/>");
        error.Should().BeNull();
        result.Should().Be("<r>from-lib</r>");
    }

    /// <summary>The same rule applies to xsl:import.</summary>
    [Fact]
    public async Task Import_HonoursXmlBaseOnTheElement()
    {
        var (error, result) = await RunAsync(
            "  <xsl:import href=\"helper.xsl\" xml:base=\"../lib/\"/>");
        error.Should().BeNull();
        result.Should().Be("<r>from-lib</r>");
    }

    /// <summary>
    /// Without xml:base the href resolves against the module's own directory, where the target
    /// does not exist — the fix must not start searching elsewhere.
    /// </summary>
    [Fact]
    public async Task Include_WithoutXmlBase_StillResolvesAgainstTheModule()
    {
        var (error, _) = await RunAsync("  <xsl:include href=\"helper.xsl\"/>");
        error.Should().NotBeNull();
        error!.Message.Should().Contain("XTSE0165");
    }

    /// <summary>A relative href that IS reachable from the module keeps working.</summary>
    [Fact]
    public async Task Include_RelativePathFromTheModule_StillWorks()
    {
        var (error, result) = await RunAsync("  <xsl:include href=\"../lib/helper.xsl\"/>");
        error.Should().BeNull();
        result.Should().Be("<r>from-lib</r>");
    }
}
