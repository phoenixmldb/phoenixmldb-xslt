using System.Diagnostics;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Cancelling a transformation must stop work INSIDE an instruction, not only between
/// instructions: a long XPath expression, a regular-expression match, a sort. Asserted as
/// latency (stopped well before the work would have finished) with a watchdog, because "threw
/// OperationCanceledException eventually" is also true of work that ran to completion first.
/// </summary>
public class TransformCancellationTests
{
    private static string ValueOf(string xpath) => $"""
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
          <xsl:output method="text"/>
          <xsl:template match="/"><xsl:value-of select="{System.Security.SecurityElement.Escape(xpath)}"/></xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<(bool Cancelled, long Ms, Exception? Other)> RunCancelledAfter(
        XsltTransformer t, int cancelMs)
    {
        using var cts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();
        var run = Task.Run(async () =>
        {
            try { await t.TransformAsync("<r/>", cts.Token); return ((bool, Exception?))(false, null); }
            catch (OperationCanceledException) { return (true, null); }
            catch (Exception e) { return (false, e); }
        });
        cts.CancelAfter(cancelMs);
        var finished = await Task.WhenAny(run, Task.Delay(30_000));
        finished.Should().BeSameAs(run, "the transformation was still running 30 s after cancellation");
        var (cancelled, other) = await run;
        return (cancelled, sw.ElapsedMilliseconds, other);
    }

    // The transformation's token did not reach the XPath engine, so one long expression ran to
    // completion however long ago the transformation was cancelled. (2^27 calls: ~20 s here.)
    [Fact]
    public async Task Long_XPath_expression_stops_when_the_transformation_is_cancelled()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ValueOf(
            "let $f := function($f, $n) { if ($n lt 2) then $n else $f($f, $n - 1) + $f($f, $n - 2) } return $f($f, 32)"));

        var (cancelled, ms, other) = await RunCancelledAfter(t, 200);

        other.Should().BeNull();
        cancelled.Should().BeTrue();
        ms.Should().BeLessThan(5000, "cancellation was requested at 200 ms");
    }

    // A running .NET regex match cannot observe the token; the match timeout is how it stops,
    // and then it reports the cancellation rather than a timeout error.
    [Fact]
    public async Task Backtracking_analyze_string_instruction_stops_at_the_match_timeout_when_cancelled()
    {
        var t = new XsltTransformer { RegexMatchTimeout = TimeSpan.FromSeconds(1) };
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:analyze-string select="string-join((1 to 40) ! 'a', '')" regex="(a+)+b">
                  <xsl:matching-substring>m</xsl:matching-substring>
                </xsl:analyze-string>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var (cancelled, ms, other) = await RunCancelledAfter(t, 200);

        other.Should().BeNull();
        cancelled.Should().BeTrue();
        ms.Should().BeLessThan(5000, "the match timeout is 1 s");
    }

    [Fact]
    public async Task Backtracking_analyze_string_instruction_past_the_time_limit_is_FOER0000()
    {
        var t = new XsltTransformer { RegexMatchTimeout = TimeSpan.FromMilliseconds(500) };
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:analyze-string select="string-join((1 to 40) ! 'a', '')" regex="(a+)+b">
                  <xsl:matching-substring>m</xsl:matching-substring>
                </xsl:analyze-string>
              </xsl:template>
            </xsl:stylesheet>
            """);

        // Task.Run: TransformAsync runs synchronously into the match, and a match with no
        // timeout would then never hand control back to the watchdog below.
        var run = Task.Run(() => t.TransformAsync("<r/>"));
        var finished = await Task.WhenAny(run, Task.Delay(30_000));

        finished.Should().BeSameAs(run, "the match should stop at its 500 ms limit");
        var act = async () => await run;
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("FOER0000");
    }
}
