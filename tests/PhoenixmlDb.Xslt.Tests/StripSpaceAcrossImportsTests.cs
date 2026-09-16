using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xsl:strip-space</c> and <c>xsl:preserve-space</c> reached through <c>xsl:import</c> were
/// silently ignored. The import arm records the module in <c>Imports</c> rather than merging it —
/// correctly, since imported declarations hold lower precedence — but the whitespace lists were
/// never collected from that graph, so only the principal module's own declarations and those of
/// modules it <c>xsl:include</c>d ever reached the stripper.
///
/// The symptom appears far from the cause: whitespace-only text nodes survive in the source tree,
/// flow through built-in templates into output, and constructs that test for emptiness
/// (<c>xsl:where-populated</c>, <c>empty()</c>, <c>normalize-space()</c>) see content that should
/// not be there. DocBook xslTNG reaches <c>modules/space.xsl</c> exactly this way — docbook.xsl
/// imports main.xsl, which includes space.xsl — and emitted an empty <c>div class="db-bfs"</c>
/// where Saxon emits none (#130).
/// </summary>
public sealed class StripSpaceAcrossImportsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-strip-").FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    /// <summary>Writes a module into the temp dir and returns the name to use as an href.</summary>
    private string Write(string name, string content)
    {
        File.WriteAllText(Path.Combine(_dir, name), content);
        return name;
    }

    private const string Source = """
        <doc xmlns="http://docbook.org/ns/docbook">
          <info>i</info>
          <section>s</section>
        </doc>
        """;

    /// <summary>Counts preceding siblings of the section: 1 node / 0 text when stripping works.</summary>
    private async Task<string> RunAsync(string principal)
    {
        var path = Path.Combine(_dir, "principal.xsl");
        await File.WriteAllTextAsync(path, principal);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(principal, new Uri(path));
        return (await t.TransformAsync(Source)).Trim();
    }

    private static string Principal(string declarations) => $"""
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:db="http://docbook.org/ns/docbook" exclude-result-prefixes="#all">
          <xsl:output method="text"/>
        {declarations}
          <xsl:template match="/">
            <xsl:variable name="sec" select="//db:section"/>
            <xsl:variable name="nodes" select="$sec/preceding-sibling::node()"/>
            <xsl:value-of select="count($nodes) || '/' || count($nodes[self::text()])"/>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private string LeafUri() => Write("leaf.xsl", """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
          xmlns:db="http://docbook.org/ns/docbook" exclude-result-prefixes="#all">
          <xsl:preserve-space elements="*"/>
          <xsl:strip-space elements="db:doc"/>
        </xsl:stylesheet>
        """);

    /// <summary>The reported shape: declarations behind an import boundary.</summary>
    [Fact]
    public async Task StripSpace_ThroughImport_IsApplied() =>
        (await RunAsync(Principal($"  <xsl:import href=\"{LeafUri()}\"/>"))).Should().Be("1/0");

    /// <summary>xslTNG's actual chain: import a module that includes the declaring module.</summary>
    [Fact]
    public async Task StripSpace_ThroughImportOfAnIncludingModule_IsApplied()
    {
        var leaf = LeafUri();
        var mid = Write("mid.xsl", $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:include href="{leaf}"/>
            </xsl:stylesheet>
            """);
        (await RunAsync(Principal($"  <xsl:import href=\"{mid}\"/>"))).Should().Be("1/0");
    }

    /// <summary>Guard: xsl:include already worked and must keep working.</summary>
    [Fact]
    public async Task StripSpace_ThroughInclude_StillApplied() =>
        (await RunAsync(Principal($"  <xsl:include href=\"{LeafUri()}\"/>"))).Should().Be("1/0");

    /// <summary>Guard: a declaration in the principal module is unaffected.</summary>
    [Fact]
    public async Task StripSpace_InPrincipalModule_StillApplied() =>
        (await RunAsync(Principal("""
              <xsl:preserve-space elements="*"/>
              <xsl:strip-space elements="db:doc"/>
        """))).Should().Be("1/0");

    /// <summary>
    /// Guard: specificity still decides. An explicit NameTest outranks a blanket
    /// <c>preserve-space elements="*"</c> even when the blanket one is in the higher-precedence
    /// module — merging imported tests into the same list must not invert that.
    /// </summary>
    [Fact]
    public async Task ExplicitStrip_BeatsBlanketPreserveInTheImportingModule() =>
        (await RunAsync(Principal($"  <xsl:import href=\"{LeafUri()}\"/>\n  <xsl:preserve-space elements=\"*\"/>")))
            .Should().Be("1/0");

    /// <summary>Guard: with no declaration anywhere, whitespace is preserved (the default).</summary>
    [Fact]
    public async Task NoDeclaration_PreservesWhitespace() =>
        (await RunAsync(Principal(""))).Should().Be("3/2");

    /// <summary>
    /// A principal <c>strip-space</c> and an IMPORTED <c>preserve-space</c> naming the same element
    /// is legal — XSLT 3.0 §4.4 resolves it by import precedence, the principal winning. It must
    /// not raise XTSE0270, which is a conflict between declarations at the SAME precedence.
    ///
    /// Collecting whitespace declarations across imports made this combination reachable for the
    /// first time, and the conflict check ran after the merge, so it saw two precedence levels as
    /// one and rejected a valid stylesheet at compile time.
    /// </summary>
    [Fact]
    public async Task PrincipalStrip_AndImportedPreserveOfTheSameName_IsNotAConflict()
    {
        var lib = Write("conflict-lib.xsl", """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:preserve-space elements="b"/>
            </xsl:stylesheet>
            """);
        var xsl = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:import href="{lib}"/>
              <xsl:strip-space elements="b"/>
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:text>ok</xsl:text></xsl:template>
            </xsl:stylesheet>
            """;
        var path = Path.Combine(_dir, "conflict-main.xsl");
        await File.WriteAllTextAsync(path, xsl);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl, new Uri(path));
        (await t.TransformAsync("<doc><b>  </b></doc>")).Trim().Should().Be("ok");
    }

    /// <summary>
    /// The other half, and the guard that stops the fix from becoming "never check": at the SAME
    /// import precedence — principal plus a module it <c>xsl:include</c>s — naming one element in
    /// both declarations really is XTSE0270.
    /// </summary>
    [Fact]
    public async Task PrincipalStrip_AndIncludedPreserveOfTheSameName_StillRaises0270()
    {
        var lib = Write("same-prec-lib.xsl", """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:preserve-space elements="b"/>
            </xsl:stylesheet>
            """);
        var xsl = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:include href="{lib}"/>
              <xsl:strip-space elements="b"/>
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:text>ok</xsl:text></xsl:template>
            </xsl:stylesheet>
            """;
        var path = Path.Combine(_dir, "same-prec-main.xsl");
        await File.WriteAllTextAsync(path, xsl);
        var act = async () =>
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(xsl, new Uri(path));
            await t.TransformAsync("<doc><b>  </b></doc>");
        };
        (await act.Should().ThrowAsync<System.Exception>())
            .Which.Message.Should().Contain("XTSE0270");
    }
}
