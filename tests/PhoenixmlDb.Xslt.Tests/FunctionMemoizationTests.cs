using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Memoized stylesheet functions: <c>new-each-time="no"</c> declares a function deterministic,
/// so identical calls return identical nodes (XSLT 3.0 §10.3.2, W3C function-1025/1026), and
/// <c>cache="yes"</c> must never hand one call another call's result.
/// </summary>
public sealed class FunctionMemoizationTests
{
    private static async Task<string> RunAsync(string function, string body)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:f="urn:f" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {{function}}
              <xsl:template match="/">{{body}}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc/>")).Trim();
    }

    private const string MakeNode = """<xsl:param name="n"/><e><xsl:value-of select="$n"/></e>""";

    [Theory]
    [InlineData("no", "7")]
    [InlineData("false", "7")]
    [InlineData("0", "7")]
    [InlineData("yes", "10")]
    [InlineData("true", "10")]
    public async Task NewEachTime_DecidesWhetherIdenticalCallsShareANode(string newEachTime, string expected)
        => (await RunAsync(
                $"""<xsl:function name="f:make" as="element()" new-each-time="{newEachTime}">{MakeNode}</xsl:function>""",
                """<xsl:value-of select="count((1,4,6,8,3,5,6,2,1,3) ! f:make(.) | ())"/>"""))
            .Should().Be(expected, "ten calls over seven distinct arguments");

    [Fact]
    public async Task NewEachTimeMaybe_IsAccepted()
        => (await RunAsync(
                $"""<xsl:function name="f:make" as="element()" new-each-time="maybe">{MakeNode}</xsl:function>""",
                """<xsl:value-of select="count((1,1) ! f:make(.) | ())"/>"""))
            .Should().BeOneOf("1", "2");

    [Fact]
    public async Task NewEachTime_RejectsANonBooleanValue()
    {
        var act = () => RunAsync(
            $"""<xsl:function name="f:make" new-each-time="sometimes">{MakeNode}</xsl:function>""", "x");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTSE0020");
    }

    /// <summary>
    /// Every pair here shared one cache entry under the old string key: a sequence argument
    /// rendered as "System.Object[]", a string and an integer rendered alike, and a comma inside
    /// a string was indistinguishable from the argument separator.
    /// </summary>
    [Theory]
    [InlineData("f:echo((1, 2))", "f:echo((3, 4))", "1 2", "3 4")]
    [InlineData("f:echo('1')", "f:echo(1)", "xs:string", "xs:integer")]
    [InlineData("f:echo2('a,b', 'c')", "f:echo2('a', 'b,c')", "a,b|c", "a|b,c")]
    [InlineData("f:echo(map{'k': 1})", "f:echo(map{'k': 2})", "1", "2")]
    [InlineData("f:echo([1])", "f:echo([2])", "1", "2")]
    public async Task CacheYes_NeverReturnsAnotherCallsResult(string first, string second, string firstResult, string secondResult)
    {
        const string functions = """
            <xsl:function name="f:echo" cache="yes">
              <xsl:param name="v"/>
              <xsl:sequence select="if ($v instance of map(*)) then string($v?k)
                                    else if ($v instance of array(*)) then string($v(1))
                                    else if (count($v) = 1 and not($v instance of xs:integer)) then 'xs:string'
                                    else if ($v instance of xs:integer) then 'xs:integer'
                                    else string-join($v ! string(.), ' ')"/>
            </xsl:function>
            <xsl:function name="f:echo2" cache="yes">
              <xsl:param name="a"/><xsl:param name="b"/>
              <xsl:sequence select="$a || '|' || $b"/>
            </xsl:function>
            """;
        (await RunAsync(functions, $"""<xsl:value-of select="{first}, '#', {second}" separator=""/>"""))
            .Should().Be($"{firstResult}#{secondResult}");
    }

    [Fact]
    public async Task CacheYes_StillHitsForEqualArguments()
    {
        // The memo is keyed by content for maps: two separately built but equal maps are the
        // same call, so a node-returning function hands back the same node.
        var result = await RunAsync(
            """<xsl:function name="f:make" cache="yes"><xsl:param name="m"/><e><xsl:value-of select="$m?k"/></e></xsl:function>""",
            """<xsl:value-of select="count((f:make(map{'k': 1}), f:make(map{'k': 1})) | ())"/>""");
        result.Should().Be("1");
    }
}
