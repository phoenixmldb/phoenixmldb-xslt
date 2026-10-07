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

    // Static expressions run while the stylesheet LOADS, on an evaluator of their own. It was
    // built without the match timeout, so a regex in use-when, a static variable or a shadow
    // attribute ran unbounded whatever RegexMatchTimeout said. 40 characters against (a+)+b
    // does not finish in any useful time; with the limit, the load fails at 500 ms.
    [Theory]
    [InlineData("<out xsl:use-when=\"matches(string-join((1 to 40) ! 'a', ''), '(a+)+b')\"/>", "use-when")]
    [InlineData("<xsl:value-of _select=\"'{matches(string-join((1 to 40) ! 'a', ''), '(a+)+b')}'\"/>", "shadow attribute")]
    public async Task Backtracking_regex_in_a_static_expression_stops_at_the_match_timeout(string instruction, string where)
    {
        var t = new XsltTransformer { RegexMatchTimeout = TimeSpan.FromMilliseconds(500) };
        var load = Task.Run(() => t.LoadStylesheetAsync(
            "<xsl:stylesheet xmlns:xsl='http://www.w3.org/1999/XSL/Transform' version='3.0'>"
            + "<xsl:template match='/'>" + instruction + "</xsl:template></xsl:stylesheet>"));

        var finished = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(20)));

        finished.Should().BeSameAs(load, $"the match in the {where} should stop at its 500 ms limit");
        var act = async () => await load;
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("time limit");
    }

    [Fact]
    public async Task Backtracking_regex_in_a_static_variable_stops_at_the_match_timeout()
    {
        var t = new XsltTransformer { RegexMatchTimeout = TimeSpan.FromMilliseconds(500) };
        var load = Task.Run(() => t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:variable name="v" static="yes" select="matches(string-join((1 to 40) ! 'a', ''), '(a+)+b')"/>
              <xsl:template match="/"><out/></xsl:template>
            </xsl:stylesheet>
            """));

        var finished = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(20)));

        finished.Should().BeSameAs(load, "the match should stop at its 500 ms limit");
        var act = async () => await load;
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("time limit");
    }

    private const string BacktrackingStylesheet =
        "<xsl:stylesheet xmlns:xsl='http://www.w3.org/1999/XSL/Transform' version='3.0'>"
        + "<xsl:template name='main'><out><xsl:value-of select=\"matches(string-join((1 to 40) ! 'a', ''), '(a+)+b')\"/></out></xsl:template>"
        + "</xsl:stylesheet>";

    // fn:transform called from a query: the transformer was built with the resource policy
    // alone, so the query's RegexMatchTimeout and cancellation token never reached it.
    [Fact]
    public async Task Transform_called_from_a_query_obeys_the_querys_regex_match_timeout()
    {
        var provider = new XsltTransformProvider();
        var nodeStore = new XdmInMemoryStore();
        using var qec = new PhoenixmlDb.XQuery.Execution.QueryExecutionContext(
            new PhoenixmlDb.Core.ContainerId(1),
            nodeProvider: nodeStore,
            limits: new PhoenixmlDb.XQuery.Execution.QueryExecutionLimits { RegexMatchTimeout = TimeSpan.FromMilliseconds(500) });
        var options = new Dictionary<object, object?>
        {
            ["stylesheet-text"] = BacktrackingStylesheet,
            ["initial-template"] = new PhoenixmlDb.Core.QName(PhoenixmlDb.Core.NamespaceId.None, "main"),
        };

        var run = Task.Run(async () => await ((PhoenixmlDb.XQuery.Functions.ITransformProvider)provider).TransformAsync(options, qec));
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));

        finished.Should().BeSameAs(run, "the match should stop at the query's 500 ms limit");
        var act = async () => await run;
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("time limit");
    }

    // fn:transform called from a stylesheet: the nested transformation's options carried the
    // policy but not the outer transformation's match timeout.
    [Fact]
    public async Task Nested_transform_obeys_the_outer_regex_match_timeout()
    {
        var t = new XsltTransformer { RegexMatchTimeout = TimeSpan.FromMilliseconds(500) };
        await t.LoadStylesheetAsync(
            "<xsl:stylesheet xmlns:xsl='http://www.w3.org/1999/XSL/Transform' xmlns:xs='http://www.w3.org/2001/XMLSchema' version='3.0'>"
            + "<xsl:param name='inner' as='xs:string'/>"
            + "<xsl:template match='/'><xsl:sequence select=\"transform(map { 'stylesheet-text': $inner, 'initial-template': xs:QName('main') })?output\"/></xsl:template>"
            + "</xsl:stylesheet>");
        t.SetParameter("inner", BacktrackingStylesheet);

        var run = Task.Run(() => t.TransformAsync("<r/>"));
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));

        finished.Should().BeSameAs(run, "the nested match should stop at the outer 500 ms limit");
        var act = async () => await run;
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("time limit");
    }

    // A dynamic error in a pattern makes the pattern not match (XSLT 3.0 §5.5.4). A match that
    // was stopped at its time limit is not such an error: treating it as "no match" let the
    // transformation succeed with output that silently depends on how slow the machine was.
    [Fact]
    public async Task Backtracking_regex_in_a_match_pattern_is_an_error_not_a_non_match()
    {
        var t = new XsltTransformer { RegexMatchTimeout = TimeSpan.FromMilliseconds(500) };
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:template match="r[matches(string-join((1 to 40) ! 'a', ''), '(a+)+b')]">slow</xsl:template>
              <xsl:template match="r">fallback</xsl:template>
            </xsl:stylesheet>
            """);

        var run = Task.Run(() => t.TransformAsync("<r/>"));
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));

        finished.Should().BeSameAs(run, "the match should stop at its 500 ms limit");
        var act = async () => await run;
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("time limit");
    }

    // An imported or included module is parsed as part of the same stylesheet; its static
    // expressions must run under the same limit, whichever evaluator they get.
    [Theory]
    [InlineData("import", "variable")]
    [InlineData("include", "variable")]
    [InlineData("import", "param")]
    [InlineData("include", "use-when")]
    public async Task Backtracking_regex_in_an_imported_modules_static_expression_stops_at_the_match_timeout(
        string how, string where)
    {
        const string call = "matches(string-join((1 to 40) ! 'a', ''), '(a+)+b')";
        var dir = Path.Combine(Path.GetTempPath(), $"phoenixmldb-static-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var declaration = where switch
            {
                "variable" => $"<xsl:variable name='v' static='yes' select=\"{call}\"/>",
                "param" => $"<xsl:param name='p' static='yes' select=\"{call}\"/>",
                _ => $"<xsl:template name='t' use-when=\"{call}\"/>",
            };
            await File.WriteAllTextAsync(Path.Combine(dir, "module.xsl"),
                "<xsl:stylesheet xmlns:xsl='http://www.w3.org/1999/XSL/Transform' version='3.0'>" + declaration + "</xsl:stylesheet>");
            var main = "<xsl:stylesheet xmlns:xsl='http://www.w3.org/1999/XSL/Transform' version='3.0'>"
                + $"<xsl:{how} href='module.xsl'/><xsl:template match='/'><out/></xsl:template></xsl:stylesheet>";

            var t = new XsltTransformer { RegexMatchTimeout = TimeSpan.FromMilliseconds(500) };
            var load = Task.Run(() => t.LoadStylesheetAsync(main, new Uri(Path.Combine(dir, "main.xsl"))));
            var finished = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(20)));

            finished.Should().BeSameAs(load, $"the match in the {how}ed module's static {where} should stop at its 500 ms limit");
            var act = async () => await load;
            (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("time limit");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    // xsl:evaluate compiles and runs its expression at run time, on the transformation's own
    // context; pinned here so it stays inside the limit.
    [Fact]
    public async Task Backtracking_regex_in_xsl_evaluate_stops_at_the_match_timeout()
    {
        var t = new XsltTransformer { RegexMatchTimeout = TimeSpan.FromMilliseconds(500) };
        await t.LoadStylesheetAsync(
            "<xsl:stylesheet xmlns:xsl='http://www.w3.org/1999/XSL/Transform' version='3.0'>"
            + "<xsl:variable name='expr' select=\"&quot;matches(string-join((1 to 40) ! 'a', ''), '(a+)+b')&quot;\"/>"
            + "<xsl:template match='/'><out><xsl:evaluate xpath='$expr'/></out></xsl:template>"
            + "</xsl:stylesheet>");

        var run = Task.Run(() => t.TransformAsync("<r/>"));
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));

        finished.Should().BeSameAs(run, "the match should stop at its 500 ms limit");
        var act = async () => await run;
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("time limit");
    }
}
