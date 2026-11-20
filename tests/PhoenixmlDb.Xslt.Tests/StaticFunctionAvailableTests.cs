using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// function-available() in a static expression (use-when) sees the XSLT functions that a static
/// expression may call: system-property, function-available, element-available and type-available
/// (XSLT 3.0 §3.13.1). It looked only in the XPath library, so function-available('system-property')
/// was false in use-when (W3C use-when-0407a). XSLT functions that need a dynamic context (current,
/// key, document, regex-group) are not available there. Every expected value is Saxon-HE 12.10's.
/// </summary>
public class StaticFunctionAvailableTests
{
    private static async Task<string> Run(string arguments)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:fn="http://www.w3.org/2005/xpath-functions" version="3.0">
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template"><a xsl:use-when="function-available({arguments})">T</a><a xsl:use-when="not(function-available({arguments}))">F</a></xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("'system-property'", "T")]
    [InlineData("'system-property', 1", "T")]
    [InlineData("'system-property', 2", "F")]
    [InlineData("'element-available'", "T")]
    [InlineData("'type-available', 1", "T")]
    [InlineData("'function-available', 2", "T")]
    [InlineData("'function-available', 3", "F")]
    [InlineData("'fn:system-property'", "T")]
    [InlineData("'key'", "F")]
    [InlineData("'current'", "F")]
    [InlineData("'document'", "F")]
    [InlineData("'regex-group'", "F")]
    [InlineData("'generate-id'", "T")]
    public async Task A_static_expression_sees_the_XSLT_functions_it_may_call(string arguments, string expected)
        => (await Run(arguments)).Should().Be(expected);
}
