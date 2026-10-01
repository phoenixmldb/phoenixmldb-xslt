using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// accumulator-before/after called with no context item is XTDE3350 (W3C accumulator-061:
/// accumulator-after#1 held in a global parameter has an absent focus), not a bare XPDY0002.
/// </summary>
public class AccumulatorAbsentFocusTests
{
    [Fact]
    public async Task An_accumulator_function_with_no_context_item_is_XTDE3350()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:accumulator name="n" initial-value="0"><xsl:accumulator-rule match="x" select="$value + 1"/></xsl:accumulator>
              <xsl:mode use-accumulators="n"/>
              <xsl:function name="f:a" xmlns:f="urn:f"><xsl:sequence select="accumulator-after('n')"/></xsl:function>
              <xsl:template match="/"><out><xsl:value-of select="f:a()" xmlns:f="urn:f"/></out></xsl:template>
            </xsl:stylesheet>
            """);
        var act = () => t.TransformAsync("<r><x/></r>");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().StartWith("XTDE3350");
    }
}
