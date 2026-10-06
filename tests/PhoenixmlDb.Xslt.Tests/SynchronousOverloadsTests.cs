#pragma warning disable CA1849 // the synchronous overloads are what this file tests
using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The synchronous LoadStylesheet / Transform overloads (xslt#309). The async methods complete
/// off the calling thread, so a host with no SynchronizationContext is on a thread-pool thread
/// after the await. These keep the caller on its thread: the work runs on the engine's
/// large-stack thread and the calling thread waits for it.
/// </summary>
public sealed class SynchronousOverloadsTests
{
    private const string Stylesheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          <xsl:template match="/"><xsl:message>working</xsl:message>ok:<xsl:value-of select="count(//x)"/></xsl:template>
        </xsl:stylesheet>
        """;

    [Fact]
    public async Task Synchronous_and_async_give_the_same_result()
    {
        var sync = new XsltTransformer();
        sync.LoadStylesheet(Stylesheet);
        var viaAsync = new XsltTransformer();
        await viaAsync.LoadStylesheetAsync(Stylesheet);

        sync.Transform("<r><x/><x/></r>").Should().Be(await viaAsync.TransformAsync("<r><x/><x/></r>"));
        sync.Transform("<r><x/><x/></r>").Trim().Should().Be("ok:2");
    }

    [Fact]
    public void The_work_runs_on_the_engines_thread_and_the_caller_stays_on_its_own()
    {
        // Run from a thread of our own, as a plugin's UI thread would be: no SynchronizationContext.
        string? workThreadName = null;
        var workThreadId = -1;
        int callerBefore = -1, callerAfterLoad = -1, callerAfterTransform = -1;
        string? result = null;
        var caller = new Thread(() =>
        {
            callerBefore = Environment.CurrentManagedThreadId;
            var t = new XsltTransformer
            {
                MessageListener = (_, _) =>
                {
                    workThreadName = Thread.CurrentThread.Name;
                    workThreadId = Environment.CurrentManagedThreadId;
                },
            };
            t.LoadStylesheet(Stylesheet);
            callerAfterLoad = Environment.CurrentManagedThreadId;
            result = t.Transform("<r><x/></r>");
            callerAfterTransform = Environment.CurrentManagedThreadId;
        });
        caller.Start();
        caller.Join();

        result!.Trim().Should().Be("ok:1");
        callerAfterLoad.Should().Be(callerBefore);
        callerAfterTransform.Should().Be(callerBefore);
        workThreadName.Should().Be("PhoenixmlDb.Xslt transform");
        workThreadId.Should().NotBe(callerBefore);
    }

    [Fact]
    public void Deep_recursion_is_as_safe_as_with_the_async_method()
    {
        // 1000 levels need far more stack than a default thread has; the large-stack thread holds it.
        var t = new XsltTransformer();
        t.LoadStylesheet("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="f:sum" as="xs:integer">
                <xsl:param name="n" as="xs:integer"/>
                <xsl:sequence select="if ($n = 0) then 0 else $n + f:sum($n - 1)"/>
              </xsl:function>
              <xsl:template match="/"><xsl:value-of select="f:sum(1000)"/></xsl:template>
            </xsl:stylesheet>
            """);
        t.Transform("<r/>").Trim().Should().Be("500500");
    }

    [Fact]
    public void An_error_surfaces_as_itself_not_wrapped()
    {
        var t = new XsltTransformer();
        var load = () => t.LoadStylesheet("<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'><xsl:nonsense/></xsl:stylesheet>");
        load.Should().Throw<XsltException>();

        t.LoadStylesheet("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><xsl:sequence select="error(xs:QName('boom'), 'bad')" xmlns:xs="http://www.w3.org/2001/XMLSchema"/></xsl:template>
            </xsl:stylesheet>
            """);
        var run = () => t.Transform("<r/>");
        run.Should().Throw<Exception>().Which.Should().NotBeOfType<AggregateException>();
    }

    [Fact]
    public void A_cancelled_token_cancels()
    {
        var t = new XsltTransformer();
        t.LoadStylesheet(Stylesheet);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var run = () => t.Transform("<r/>", cts.Token);
        run.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void Where_no_thread_can_be_started_it_runs_on_the_callers_stack()
    {
        LargeStack.StartOverride.Value = _ => throw new PlatformNotSupportedException("no threads");
        var t = new XsltTransformer();
        t.LoadStylesheet(Stylesheet);
        t.Transform("<r><x/></r>").Trim().Should().Be("ok:1");
    }
}
