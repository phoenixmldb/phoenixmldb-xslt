using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// In a streamable template, accumulator-after() of the processed node needs its whole
/// subtree. Called before the template descends, it is the one consumption, so a later
/// descent is not streamable (W3C accumulator-059), and several calls in one start tag's
/// attributes are not either (accumulator-009). After the descent, or in xsl:attribute
/// instructions taken in order (accumulator-008), it is fine.
/// </summary>
public class AccumulatorAfterPhaseTests
{
    private static async Task<string?> CompileError(string templateBody)
    {
        try
        {
            await new XsltTransformer().LoadStylesheetAsync($"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:accumulator name="n" initial-value="0" streamable="yes"><xsl:accumulator-rule match="x" select="$value + 1"/></xsl:accumulator>
                  <xsl:mode streamable="yes" use-accumulators="n"/>
                  <xsl:template match="r">{templateBody}</xsl:template>
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
    public async Task Before_the_descent_then_descending_is_rejected() =>
        (await CompileError("<out><p><xsl:value-of select=\"accumulator-after('n')\"/></p><xsl:apply-templates/></out>"))
            .Should().StartWith("XTSE3430");

    [Fact]
    public async Task Several_in_one_start_tag_are_rejected() =>
        (await CompileError("<out a=\"{accumulator-after('n')}\" b=\"{accumulator-after('n')}\"/>"))
            .Should().StartWith("XTSE3430");

    [Fact]
    public async Task After_the_descent_is_accepted() =>
        (await CompileError("<out><xsl:apply-templates/><n><xsl:value-of select=\"accumulator-after('n')\"/></n></out>"))
            .Should().BeNull();

    [Fact]
    public async Task In_xsl_attribute_instructions_taken_in_order_is_accepted() =>
        (await CompileError("<out><xsl:attribute name=\"a\" select=\"accumulator-after('n')\"/><xsl:attribute name=\"b\" select=\"accumulator-after('n')\"/></out>"))
            .Should().BeNull();
}
