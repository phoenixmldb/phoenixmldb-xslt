using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// How xsl:mode declarations for one mode combine across modules (XSLT 3.0 §6.6.1). At the same
/// import precedence they combine, and two that state different values for an attribute are
/// XTSE0545 unless a declaration at higher import precedence states that attribute. A
/// higher-precedence declaration overrides exactly the attributes it states.
/// </summary>
/// <remarks>
/// What these pin down: a conflict between included modules was never detected (W3C
/// mode-1506), and a conflict inside an imported module could not survive the import, because
/// the check for a resolving declaration ran after the imported declarations had merged in and
/// so found them. A conflict within one module threw at once, before a higher declaration could
/// resolve it. An included module's declaration was dropped when the includer already declared
/// the mode. The import merge OR-ed streamable and ignored an imported on-multiple-match.
/// </remarks>
public sealed class ModeDeclarationCombinationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-mode-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Module(string name, string body)
    {
        File.WriteAllText(Path.Combine(_dir, name), $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
            {body}
            </xsl:stylesheet>
            """);
        return name;
    }

    private async Task<string> RunAsync(string principalBody, string source = "<doc><a>x</a></doc>")
    {
        var principal = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
            {principalBody}
            </xsl:stylesheet>
            """;
        var path = Path.Combine(_dir, "principal.xsl");
        await File.WriteAllTextAsync(path, principal);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principal, new Uri(path));
        return (await t.TransformAsync(source)).Trim();
    }

    private void StreamableYesAndNoIncluded()
    {
        Module("yes.xsl", """<xsl:mode streamable="yes"/>""");
        Module("no.xsl", """<xsl:mode streamable="no"/>""");
        Module("both.xsl", """<xsl:include href="yes.xsl"/><xsl:include href="no.xsl"/>""");
    }

    [Fact]
    public async Task ConflictBetweenIncludedModules_IsXTSE0545()
    {
        StreamableYesAndNoIncluded();
        var act = () => RunAsync("""<xsl:include href="both.xsl"/>""");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTSE0545").And.Contain("streamable");
    }

    [Fact]
    public async Task ConflictInsideAnImport_UnresolvedByTheImporter_IsXTSE0545()
    {
        StreamableYesAndNoIncluded();
        var act = () => RunAsync("""
            <xsl:import href="both.xsl"/>
            <xsl:mode on-no-match="deep-copy"/>
            """);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTSE0545");
    }

    [Fact]
    public async Task ConflictInsideAnImport_IsResolvedByTheImporterStatingTheAttribute()
    {
        StreamableYesAndNoIncluded();
        (await RunAsync("""
            <xsl:import href="both.xsl"/>
            <xsl:mode streamable="no" on-no-match="deep-copy"/>
            """)).Should().Contain("<a>x</a>");
    }

    /// <summary>Two declarations in ONE module conflict too, and a higher one still resolves it.</summary>
    [Fact]
    public async Task ConflictWithinOneImportedModule_IsResolvedByTheImporter()
    {
        Module("one.xsl", """
            <xsl:mode on-no-match="deep-skip"/>
            <xsl:mode on-no-match="text-only-copy"/>
            """);
        (await RunAsync("""
            <xsl:import href="one.xsl"/>
            <xsl:mode on-no-match="deep-copy"/>
            """)).Should().Contain("<a>x</a>");
    }

    /// <summary>Values compare as parsed: streamable="yes" and streamable="true" agree.</summary>
    [Fact]
    public async Task EquivalentSpellings_AreNotAConflict()
    {
        Module("yes.xsl", """<xsl:mode streamable="yes" on-no-match="deep-copy"/>""");
        Module("true.xsl", """<xsl:mode streamable="true"/>""");
        (await RunAsync("""<xsl:include href="yes.xsl"/><xsl:include href="true.xsl"/>"""))
            .Should().Contain("<a>x</a>");
    }

    /// <summary>An included module's declaration adds what it states to the includer's.</summary>
    [Fact]
    public async Task IncludedDeclaration_AddsItsAttributes()
    {
        Module("inc.xsl", """<xsl:mode name="m" on-no-match="deep-copy"/>""");
        (await RunAsync("""
            <xsl:mode name="m"/>
            <xsl:include href="inc.xsl"/>
            <xsl:template match="/"><out><xsl:apply-templates select="doc" mode="m"/></out></xsl:template>
            """)).Should().Contain("<out><doc><a>x</a></doc></out>");
    }

    /// <summary>An imported on-multiple-match="fail" holds unless the importer states otherwise.</summary>
    [Fact]
    public async Task ImportedOnMultipleMatchFail_HoldsWhenTheImporterDoesNotStateIt()
    {
        Module("fail.xsl", """<xsl:mode on-multiple-match="fail"/>""");
        var act = () => RunAsync("""
            <xsl:import href="fail.xsl"/>
            <xsl:mode on-no-match="shallow-copy"/>
            <xsl:template match="doc/*">1</xsl:template>
            <xsl:template match="doc/a">2</xsl:template>
            """);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE0540");
    }

    [Fact]
    public async Task AbstractVisibilityOnAMode_IsXTSE0020()
    {
        var act = () => RunAsync("""<xsl:mode name="m" visibility="abstract"/>""");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTSE0020");
    }
}
