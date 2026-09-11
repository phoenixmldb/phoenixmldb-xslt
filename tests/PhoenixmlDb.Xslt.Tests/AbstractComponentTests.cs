using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An abstract component of a used package exists — the name is declared — but has no body to
/// run, so invoking it is XTDE3052. Abstract functions and named templates were not merged at
/// all, so the call reported "not found", which is the answer for a name nothing declares.
/// Accepting a component AS abstract makes the using package abstract in turn: it cannot be
/// executed (XTSE3080). Mirrors W3C decl/accept accept-041b, 045b, 901..914.
/// </summary>
public sealed class AbstractComponentTests : IDisposable
{
    private readonly string _dir;

    public AbstractComponentTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pxabstract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A library package with an abstract function and an abstract named template, each reached
    /// through a public proxy — so the call comes from inside the package that declares them.
    /// </summary>
    private const string Library = """
        <xsl:package name="urn:lib" package-version="1.0.0" version="3.0"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:c="urn:c" exclude-result-prefixes="#all">
          <xsl:function name="c:abstract" visibility="abstract"><xsl:param name="p"/></xsl:function>
          <xsl:template name="t-abstract" visibility="abstract"/>
          <xsl:template name="call-function" visibility="public"><xsl:value-of select="c:abstract(1)"/></xsl:template>
          <xsl:template name="call-template" visibility="public"><xsl:call-template name="t-abstract"/></xsl:template>
        </xsl:package>
        """;

    private async Task<string> RunAsync(string principal, string initialTemplate = "main")
    {
        var libPath = Path.Combine(_dir, "lib.xsl");
        await File.WriteAllTextAsync(libPath, Library).ConfigureAwait(false);
        var catalog = new Dictionary<string, List<(string? Version, string FilePath)>>
        {
            ["urn:lib"] = new() { ("1.0.0", libPath) },
        };
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principal, new Uri(Path.Combine(_dir, "principal.xsl")), null, catalog);
        t.SetInitialTemplate(initialTemplate);
        return await t.TransformAsync("<in/>");
    }

    private static string Principal(string accepts, string body) => $"""
        <xsl:package name="urn:main" package-version="1.0.0" version="3.0"
          xmlns:xsl="http://www.w3.org/1999/XSL/Transform" exclude-result-prefixes="#all">
          <xsl:output method="text"/>
          <xsl:use-package name="urn:lib" package-version="1.0.0">{accepts}</xsl:use-package>
          <xsl:template name="main" visibility="public">{body}</xsl:template>
        </xsl:package>
        """;

    [Theory]
    [InlineData("call-function")]
    [InlineData("call-template")]
    public async Task InvokingAnAbstractComponent_FromItsOwnPackage_IsXTDE3052(string proxy)
    {
        var act = () => RunAsync(Principal("", $"""<xsl:call-template name="{proxy}"/>"""));
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE3052");
    }

    /// <summary>
    /// Accepting a component as hidden leaves the package executable — the component is simply
    /// not visible here, so calling that name stays the static XTSE0650 (W3C error-3052a).
    /// </summary>
    [Fact]
    public async Task CallingAHiddenName_FromTheUsingPackage_IsStillXTSE0650()
    {
        var act = () => RunAsync(Principal(
            """<xsl:accept component="template" names="t-abstract" visibility="hidden"/>""",
            """<xsl:call-template name="t-abstract"/>"""));
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTSE0650");
    }

    [Fact]
    public async Task AcceptingAComponentAsAbstract_MakesThePackageNonExecutable_XTSE3080()
    {
        var act = () => RunAsync(Principal(
            """<xsl:accept component="*" names="*" visibility="abstract"/>""", "ok"));
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTSE3080");
    }

    /// <summary>A package that merely uses a library with abstract internals still runs.</summary>
    [Fact]
    public async Task APackageUsingAbstractInternals_StillRuns()
        => (await RunAsync(Principal("", "ok"))).Trim().Should().Be("ok");
}
