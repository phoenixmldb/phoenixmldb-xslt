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

    // Martin Honnen's workbench example (memo-function-fibonacci1.xsl). Recursion 600 deep needs
    // far more than a small stack holds. On browser-wasm, which runs inline with no large-stack
    // thread, it failed with XTDE0000 from n=150 on. Running inline, a recursion level that finds
    // the stack low now yields, and the event loop (here, the thread pool) resumes it on a
    // fresh stack.
    private const string MemoFibonacci = """
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
            xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="http://example.com/functions"
            exclude-result-prefixes="#all">
          <xsl:output method="text"/>
          <xsl:function name="f:fib" as="xs:integer" cache="yes">
            <xsl:param name="num" as="xs:integer"/>
            <xsl:sequence select="if ($num = 0) then 0 else if ($num = 1) then 1 else f:fib($num - 2) + f:fib($num - 1)"/>
          </xsl:function>
          <xsl:template match="/"><xsl:value-of select="f:fib(xs:integer(/*))"/></xsl:template>
        </xsl:stylesheet>
        """;

    // Yielding only once the stack was already low raced the per-instruction stack check, and this
    // failed intermittently at 512 KB on Windows and Linux CI. (Smaller stacks are not meaningful
    // here: on a cold Windows CI runner the test host and transformation setup alone use most of
    // 256 KB before the recursion starts.)
    [Theory]
    [InlineData(512 * 1024)]
    public void Deep_recursion_running_inline_on_a_small_stack_yields_instead_of_exhausting_it(int stackSize)
    {
        string? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            LargeStack.ForceInline.Value = true;
            LargeStack.StartOverride.Value = _ => throw new InvalidOperationException("a thread was started");
            try
            {
                var t = new XsltTransformer();
                t.LoadStylesheetAsync(MemoFibonacci).GetAwaiter().GetResult();
                result = t.TransformAsync("<data>600</data>").GetAwaiter().GetResult().Trim();
            }
#pragma warning disable CA1031 // reported on the test thread below
            catch (Exception ex) { failure = ex; }
#pragma warning restore CA1031
        }, stackSize);
        thread.Start();
        thread.Join();

        failure.Should().BeNull();
        result.Should().Be("110433070572952242346432246767718285942590237357555606380008891875277701705731473925618404421867819924194229142447517901959200");
    }

    // The caller is not held while a transformation runs on the large-stack thread. Joining that
    // thread held the caller (in a server, a pool thread) for the whole transformation, and a few
    // long transformations starved the host. Right after the call returns, the work must still be
    // running; under the blocking implementation it had always finished by then.
    [Fact]
    public async Task TransformAsync_returns_to_its_caller_while_the_transformation_runs()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f">
              <xsl:output method="text"/>
              <xsl:function name="f:fib" as="xs:integer"><xsl:param name="n" as="xs:integer"/>
                <xsl:sequence select="if ($n lt 2) then $n else f:fib($n - 1) + f:fib($n - 2)"/></xsl:function>
              <xsl:template match="/"><xsl:value-of select="f:fib(22)"/></xsl:template>
            </xsl:stylesheet>
            """);

        var running = t.TransformAsync("<r/>");

        running.IsCompleted.Should().BeFalse("the transformation (hundreds of ms) should still be running when the call returns");
        (await running).Trim().Should().Be("17711");
    }

    // Likewise the parse, whose static expressions (use-when) can be slow. Run on the caller's
    // thread, it held, after an HTTP import fetch, whichever pool thread completed the fetch.
    [Fact]
    public async Task LoadStylesheetAsync_returns_to_its_caller_while_the_parse_runs()
    {
        var t = new XsltTransformer();

        var loading = t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="text"/>
              <xsl:template match="/" use-when="count((1 to 1500000)[. mod 7 = 0]) gt 0">loaded</xsl:template>
            </xsl:stylesheet>
            """);

        loading.IsCompleted.Should().BeFalse("the parse (a slow use-when) should still be running when the call returns");
        await loading;
        (await t.TransformAsync("<r/>")).Trim().Should().Be("loaded");
    }
}
