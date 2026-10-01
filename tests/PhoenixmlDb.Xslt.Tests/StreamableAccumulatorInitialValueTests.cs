using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A streamable accumulator's initial value is computed before the stream starts, so it must
/// not navigate the input (W3C accumulator-019s: initial-value="//x" was accepted).
/// </summary>
public class StreamableAccumulatorInitialValueTests
{
    private static async Task<string?> CompileError(string initialValue, string streamable)
    {
        try
        {
            await new XsltTransformer().LoadStylesheetAsync($"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:accumulator name="n" initial-value="{initialValue}" streamable="{streamable}">
                    <xsl:accumulator-rule match="x" select="$value"/>
                  </xsl:accumulator>
                  <xsl:template match="/"><out/></xsl:template>
                </xsl:stylesheet>
                """);
            return null;
        }
        catch (XsltException e)
        {
            return e.Message;
        }
    }

    [Fact]
    public async Task A_navigating_initial_value_is_rejected_when_streamable() =>
        (await CompileError("count(//x)", "yes")).Should().StartWith("XTSE3430");

    [Theory]
    [InlineData("count(//x)", "no")]   // not streamable: anything goes
    [InlineData("0", "yes")]
    public async Task Other_initial_values_are_accepted(string initialValue, string streamable) =>
        (await CompileError(initialValue, streamable)).Should().BeNull();
}
