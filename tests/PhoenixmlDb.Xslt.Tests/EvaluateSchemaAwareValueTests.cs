using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:evaluate/@schema-aware is a boolean (yes|no|true|false|1|0). A literal value outside that set
/// is XTSE0020 (W3C evaluate-038, "TRUE"); one an AVT computes is XTDE0030 (W3C evaluate-014). Both
/// were read as "no".
/// </summary>
public sealed class EvaluateSchemaAwareValueTests
{
    private static async Task<string> RunAsync(string schemaAware)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:param name="p" select="'maybe'"/>
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">
                <xsl:evaluate xpath="'1 + 1'" schema-aware="{schemaAware}"/>
              </xsl:template>
            </xsl:stylesheet>
            """);
        return await transformer.TransformAsync((string?)null);
    }

    [Fact]
    public async Task An_invalid_literal_value_is_XTSE0020()
    {
        var act = () => RunAsync("TRUE");
        (await act.Should().ThrowAsync<XsltException>()).WithMessage("*XTSE0020*");
    }

    [Fact]
    public async Task An_invalid_computed_value_is_XTDE0030()
    {
        var act = () => RunAsync("{$p}");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE0030");
    }

    [Theory]
    [InlineData("no")]
    [InlineData(" false ")]
    [InlineData("{'0'}")]
    public async Task A_valid_value_evaluates(string value)
        => (await RunAsync(value)).Should().Be("2");
}
