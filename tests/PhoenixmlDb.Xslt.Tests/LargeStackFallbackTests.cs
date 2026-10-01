using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Where no thread can be started, a transformation runs inline instead of failing (xslt#237).
/// </summary>
/// <remarks>
/// 2.5.1 started a large-stack thread for every transformation (xslt#197). Browser WebAssembly
/// cannot start threads, so every transform in Blazor WebAssembly threw
/// PlatformNotSupportedException. These tests force the two inline paths: a platform known to
/// have no threads, and a Thread.Start() that throws.
/// </remarks>
public sealed class LargeStackFallbackTests
{
    private const string Stylesheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          <xsl:template match="/">ok:<xsl:value-of select="count(//x)"/></xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<string> RunAsync()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet);
        return (await t.TransformAsync("<r><x/><x/></r>")).Trim();
    }

    [Fact]
    public async Task On_a_platform_without_threads_the_transform_runs_inline()
    {
        LargeStack.ForceInline.Value = true;
        // Fails the transformation if any thread start is attempted.
        LargeStack.StartOverride.Value = _ => throw new InvalidOperationException("a thread was started");
        (await RunAsync()).Should().Be("ok:2");
    }

    [Fact]
    public async Task A_thread_start_the_platform_refuses_falls_back_to_inline()
    {
        // What browser WebAssembly does when the platform check does not catch it.
        LargeStack.StartOverride.Value = _ => throw new PlatformNotSupportedException("no threads");
        (await RunAsync()).Should().Be("ok:2");
    }
}
