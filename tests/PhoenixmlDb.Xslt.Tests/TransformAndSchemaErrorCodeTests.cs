using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Error codes at three boundaries:
/// - fn:deep-equal on a function item is FOTY0015 (F&amp;O 3.1 §14.2.1). The XSLT implementation
///   atomized it and raised FOTY0013 (W3C higher-order-functions-054).
/// - fn:transform with a stylesheet-location that cannot be read is FOXT0002. A .NET
///   FileNotFoundException with no error code reached the caller (W3C transform-001).
/// - An inline xs:schema that does not compile is XTSE0220 (§3.15), not XQST0059 (W3C error-0220a).
/// </summary>
public sealed class TransformAndSchemaErrorCodeTests
{
    private static async Task<string> RunAsync(string declarations, string body)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            xmlns:f="urn:f" version="3.0">
              <xsl:function name="f:inc" as="xs:integer"><xsl:param name="x" as="xs:integer"/><xsl:sequence select="$x + 1"/></xsl:function>
              {declarations}
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(Path.GetTempPath(), "pxcodes", "main.xsl")));
        return await transformer.TransformAsync((string?)null);
    }

    private static async Task<string> CodeOf(Func<Task> act)
    {
        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        return thrown is PhoenixmlDb.XQuery.Execution.XQueryRuntimeException runtime ? runtime.ErrorCode : thrown.Message;
    }

    [Theory]
    [InlineData("deep-equal(f:inc#1, f:inc#1)")]
    [InlineData("deep-equal((1, abs#1), (1, abs#1))")]
    [InlineData("deep-equal([f:inc#1], [f:inc#1])")]
    public async Task Deep_equal_on_a_function_item_is_FOTY0015(string expression)
        => (await CodeOf(() => RunAsync("", $"<xsl:value-of select=\"{expression}\"/>"))).Should().Contain("FOTY0015");

    [Fact]
    public async Task Deep_equal_without_function_items_still_compares()
        => (await RunAsync("", "<xsl:value-of select=\"deep-equal(([1], map{'a':2}), ([1], map{'a':2}))\"/>")).Should().Be("true");

    [Fact]
    public async Task A_missing_stylesheet_location_is_FOXT0002()
        => (await CodeOf(() => RunAsync("",
            "<xsl:value-of select=\"transform(map{'stylesheet-location': 'no-such-stylesheet-0a1b.xsl'})?output\"/>")))
            .Should().Contain("FOXT0002");

    [Fact]
    public async Task An_inline_schema_that_does_not_compile_is_XTSE0220()
        => (await CodeOf(() => RunAsync("""
            <xsl:import-schema namespace="urn:dup">
              <xs:schema targetNamespace="urn:dup"><xs:element name="e"/><xs:element name="e"/></xs:schema>
            </xsl:import-schema>
            """, "x"))).Should().Contain("XTSE0220");
}
