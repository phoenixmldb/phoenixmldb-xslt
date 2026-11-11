using System.Text;
using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A failure deep in recursion costs time in proportion to the depth, not its square.
/// </summary>
/// <remarks>
/// Every async frame a failure leaves copies the stack trace it has so far. Reaching the depth
/// limit used to build a trace of 1 to 4 MB and spend up to 20 seconds on the way out, and a
/// cancellation at that depth took seconds to stop the work. Most of these tests assert the
/// trace length: it is what the time follows, and unlike the time it does not depend on the
/// machine. The class runs alone: the one test that measures time took 1.9 s in parallel with
/// the rest of the suite on a CI runner, against 47 to 87 ms alone.
/// </remarks>
[Collection(nameof(RecursionFailureTraceSerial))]
public sealed class RecursionFailureTraceTests
{
    private const int MaxTraceLength = 200_000;

    private const string Header = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                        xmlns:f="urn:f" xmlns:xs="http://www.w3.org/2001/XMLSchema">
        """;

    private const string CallTemplate = Header + """
        <xsl:template match="/"><xsl:call-template name="t"><xsl:with-param name="n" select="1"/></xsl:call-template></xsl:template>
        <xsl:template name="t"><xsl:param name="n"/><x><xsl:call-template name="t"><xsl:with-param name="n" select="$n + 1"/></xsl:call-template></x></xsl:template>
        </xsl:stylesheet>
        """;

    private const string ApplyTemplates = Header + """
        <xsl:template match="/"><a><xsl:apply-templates select="."/></a></xsl:template>
        </xsl:stylesheet>
        """;

    private const string Function = Header + """
        <xsl:function name="f:g"><xsl:param name="n"/><xsl:sequence select="1 + f:g($n + 1)"/></xsl:function>
        <xsl:template match="/"><xsl:value-of select="f:g(1)"/></xsl:template>
        </xsl:stylesheet>
        """;

    private const string BuiltIn = Header + """
        <xsl:template match="/"><xsl:apply-templates/></xsl:template>
        </xsl:stylesheet>
        """;

    private const string ErrorInDeepTemplate = Header + """
        <xsl:template match="/"><xsl:call-template name="t"><xsl:with-param name="n" select="1"/></xsl:call-template></xsl:template>
        <xsl:template name="t"><xsl:param name="n"/><x>
          <xsl:if test="$n eq 1100"><xsl:sequence select="error()"/></xsl:if>
          <xsl:call-template name="t"><xsl:with-param name="n" select="$n + 1"/></xsl:call-template>
        </x></xsl:template>
        </xsl:stylesheet>
        """;

    private const string SpinsDeep = Header + """
        <xsl:template match="/"><xsl:call-template name="t"><xsl:with-param name="n" select="1"/></xsl:call-template></xsl:template>
        <xsl:template name="t"><xsl:param name="n"/><x><xsl:choose>
          <xsl:when test="$n eq 1100"><xsl:for-each select="1 to 2000000000"><xsl:if test=". lt 0"><y/></xsl:if></xsl:for-each></xsl:when>
          <xsl:otherwise><xsl:call-template name="t"><xsl:with-param name="n" select="$n + 1"/></xsl:call-template></xsl:otherwise>
        </xsl:choose></x></xsl:template>
        </xsl:stylesheet>
        """;

    private static string DeepSource(int depth)
    {
        var source = new StringBuilder();
        for (var i = 0; i < depth; i++)
            source.Append("<a>");
        for (var i = 0; i < depth; i++)
            source.Append("</a>");
        return source.ToString();
    }

    private static async Task<Exception> FailureOfAsync(string stylesheet, string source = "<r/>", CancellationToken ct = default)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await Assert.ThrowsAnyAsync<Exception>(async () => await transformer.TransformAsync(source, ct));
    }

    [Theory]
    [InlineData(CallTemplate, "xsl:call-template")]
    [InlineData(ApplyTemplates, "xsl:apply-templates")]
    [InlineData(Function, "function")]
    public async Task The_depth_limit_fails_with_a_short_trace(string stylesheet, string reported)
    {
        var failure = await FailureOfAsync(stylesheet);

        failure.Should().BeOfType<XsltException>().Which.ErrorCode.Should().Be("XTDE0000");
        failure.Message.Should().Contain(reported);
        failure.StackTrace!.Length.Should().BeLessThan(MaxTraceLength);
    }

    [Fact]
    public async Task The_depth_limit_in_the_built_in_rules_fails_with_a_short_trace()
    {
        var failure = await FailureOfAsync(BuiltIn, DeepSource(1300));

        failure.Should().BeOfType<XsltException>().Which.ErrorCode.Should().Be("XTDE0000");
        failure.StackTrace!.Length.Should().BeLessThan(MaxTraceLength);
    }

    [Fact]
    public async Task An_error_raised_deep_in_recursion_fails_with_a_short_trace()
    {
        var failure = await FailureOfAsync(ErrorInDeepTemplate);

        failure.Message.Should().Contain("fn:error");
        failure.StackTrace!.Length.Should().BeLessThan(MaxTraceLength);
    }

    // The transform's cancellation is a new exception at the top, whose trace says nothing about
    // the unwinding below it, so this one measures the time. It took 3.4 to 4.7 seconds.
    [Fact]
    public async Task A_cancellation_deep_in_recursion_stops_the_transform_promptly()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var sinceCancel = new System.Diagnostics.Stopwatch();
        using var started = cancellation.Token.Register(sinceCancel.Start);

        var failure = await FailureOfAsync(SpinsDeep, ct: cancellation.Token);

        sinceCancel.Stop();
        failure.Should().BeAssignableTo<OperationCanceledException>();
        sinceCancel.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1.5));
    }
}

[CollectionDefinition(nameof(RecursionFailureTraceSerial), DisableParallelization = true)]
public sealed class RecursionFailureTraceSerial;
