using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A call to a stylesheet function in tail position of a stylesheet function's body runs as the
/// caller's next iteration, not nested (xquery#296): tail recursion, direct or mutual, is not
/// limited by the recursion depth (XTDE0000 at 1,200). Tail position: the select of the
/// xsl:sequence the body evaluates last, also in the chosen xsl:choose / xsl:if branch, and there
/// the then/else of an if. Not tail calls: a call whose result is used, one inside xsl:try, a call
/// of or from a memoised function, and one to a function of another return type. A string walked
/// with substring($s, 2) is passed as a slice, so the walk is linear. Each iteration is reported
/// to the trace listener as a call.
/// </summary>
[Collection(nameof(SubstringRecursionTests))]
public sealed class FunctionTailCallTests
{
    private static async Task<string> Run(string functions, string select, Action<int, string, string>? trace = null)
    {
        var t = new XsltTransformer { TraceListener = trace };
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:f="urn:f" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {functions}
              <xsl:template name="main"><xsl:value-of select="{select}"/></xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    private const string Count = """
        <xsl:function name="f:n" as="xs:integer">
          <xsl:param name="k" as="xs:integer"/><xsl:param name="a" as="xs:integer"/>
          <xsl:sequence select="if ($k eq 0) then $a else f:n($k - 1, $a + 1)"/>
        </xsl:function>
        """;

    [Fact]
    public async Task Tail_recursion_is_not_limited_by_the_recursion_depth()
        => (await Run(Count, "f:n(100000, 0)")).Should().Be("100000");

    [Fact]
    public async Task Mutual_recursion_through_xsl_choose_is_a_tail_call()
        => (await Run("""
                <xsl:function name="f:e"><xsl:param name="n"/>
                  <xsl:choose><xsl:when test="$n eq 0"><xsl:sequence select="true()"/></xsl:when>
                    <xsl:otherwise><xsl:sequence select="f:o($n - 1)"/></xsl:otherwise></xsl:choose>
                </xsl:function>
                <xsl:function name="f:o"><xsl:param name="n"/>
                  <xsl:if test="$n eq 0"><xsl:sequence select="false()"/></xsl:if>
                  <xsl:if test="$n ne 0"><xsl:sequence select="f:e($n - 1)"/></xsl:if>
                </xsl:function>
                """, "f:e(10001)")).Should().Be("false");

    [Theory]
    [InlineData("""<xsl:function name="f:t"><xsl:param name="n"/><xsl:sequence select="if ($n eq 0) then 0 else 1 + f:t($n - 1)"/></xsl:function>""")]
    [InlineData("""<xsl:function name="f:t" cache="yes"><xsl:param name="n"/><xsl:sequence select="if ($n eq 0) then 0 else f:t($n - 1)"/></xsl:function>""")]
    [InlineData("""<xsl:function name="f:t" as="xs:integer"><xsl:param name="n"/><xsl:sequence select="if ($n eq 0) then 0 else f:u($n - 1)"/></xsl:function><xsl:function name="f:u"><xsl:param name="n"/><xsl:sequence select="f:t($n)"/></xsl:function>""")]
    public async Task A_call_that_is_not_a_tail_call_keeps_the_limit(string functions)
    {
        var act = () => Run(functions, "f:t(5000)");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE0000");
    }

    [Fact]
    public async Task A_call_inside_xsl_try_is_caught_there()
        => (await Run("""
                <xsl:function name="f:c"><xsl:param name="n"/>
                  <xsl:try><xsl:sequence select="if ($n eq 0) then 'bottom' else f:c($n - 1)"/>
                    <xsl:catch><xsl:sequence select="'caught'"/></xsl:catch></xsl:try>
                </xsl:function>
                """, "f:c(5000)")).Should().Be("caught");

    [Fact]
    public async Task Output_before_the_tail_call_is_kept()
        => (await Run("""
                <xsl:function name="f:w"><xsl:param name="n"/>
                  <xsl:sequence select="$n"/>
                  <xsl:sequence select="if ($n eq 0) then () else f:w($n - 1)"/>
                </xsl:function>
                """, "string-join(f:w(5) ! string(), ',')")).Should().Be("5,4,3,2,1,0");

    [Fact]
    public async Task A_string_walk_takes_a_slice_and_is_linear()
    {
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var count = await Run("""
                <xsl:function name="f:s" as="xs:integer">
                  <xsl:param name="s" as="xs:string"/><xsl:param name="k" as="xs:integer"/>
                  <xsl:sequence select="if (string-length($s) = 0) then $k else f:s(substring($s, 2), $k + 1)"/>
                </xsl:function>
                """, "f:s(string-join((1 to 50000) ! 'x'), 0)");
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        count.Should().Be("50000");
        // A copy of the rest at each call is 50,000² / 2 chars = 2.5 GB.
        allocated.Should().BeLessThan(500L * 1024 * 1024);
    }

    [Fact]
    public async Task Each_iteration_is_traced_as_a_call()
    {
        var calls = 0;
        (await Run(Count, "f:n(10, 0)", (_, kind, _) => { if (kind == "call-function") calls++; })).Should().Be("10");
        calls.Should().Be(11);
    }
}
