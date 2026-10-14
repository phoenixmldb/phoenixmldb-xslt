using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A variable in a streamable template may read the descendants when its value is grounded:
/// <c>count(//x)</c> binds a number, not a streamed node. It was refused with XTSE3430 for the
/// descendant axis alone. A variable that would hold streamed nodes is still refused.
/// </summary>
public class StreamedVariableCrawlingSelectTests
{
    private const string Input = "<root><item n='1'><foo>a</foo></item><item n='2'><foo>b</foo><foo>c</foo></item></root>";

    private static async Task<string> RunAsync(string variable, string use, bool tree)
    {
        var t = new XsltTransformer { DisableStreaming = tree };
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:mode streamable="yes"/>
              <xsl:template match="/"><out>{variable}{use}</out></xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync(Input);
    }

    [Theory]
    [InlineData("<xsl:variable name=\"v\" select=\"count(//foo)\"/>", "{$v}", "<out>3</out>")]
    [InlineData("<xsl:variable name=\"v\" select=\"string-join(//foo, ',')\"/>", "{$v}", "<out>a,b,c</out>")]
    [InlineData("<xsl:variable name=\"v\" select=\"sum(//item/@n)\"/>", "{$v}", "<out>3</out>")]
    [InlineData("<xsl:variable name=\"v\" select=\"exists(//foo)\"/>", "{$v}", "<out>true</out>")]
    [InlineData("<xsl:variable name=\"v\" select=\"count(//foo) + 1\"/>", "{$v}", "<out>4</out>")]
    [InlineData("<xsl:variable name=\"v\" select=\"//foo ! string()\"/>", "{$v}", "<out>a b c</out>")]
    public async Task A_variable_with_a_grounded_value_is_accepted(string variable, string use, string expected)
    {
        (await RunAsync(variable, use, tree: false)).Should().Be(expected);
        (await RunAsync(variable, use, tree: true)).Should().Be(expected);
    }

    [Theory]
    [InlineData("<xsl:variable name=\"v\" select=\"//foo\"/>")]
    [InlineData("<xsl:variable name=\"v\" select=\"//foo[1]\"/>")]
    [InlineData("<xsl:variable name=\"v\" select=\"(//foo, 1)\"/>")]
    public async Task A_variable_that_would_hold_streamed_nodes_is_still_XTSE3430(string variable)
    {
        var act = () => RunAsync(variable, "{count($v)}", tree: false);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTSE3430");
    }
}
