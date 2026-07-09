using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression tests for <c>fn:available-system-properties()</c>. Per XSLT 4.0 the
/// function returns <c>xs:QName*</c> — a SEQUENCE of xs:QName items. The engine
/// previously returned a <c>List&lt;object&gt;</c>, which the variable type-checker
/// treats as a single XDM array item (the "don't enumerate" pattern), so binding the
/// result to <c>as="xs:QName+"</c> saw one non-QName item and raised XTTE0570. Returning
/// an <c>object?[]</c> sequence lets each QName be matched individually.
/// </summary>
public class AvailableSystemPropertiesTypeTests
{
    private static async Task<string> TransformAsync(string stylesheet, string input)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return await transformer.TransformAsync(input);
    }

    [Fact]
    public async Task result_binds_to_as_xs_QName_plus_without_XTTE0570()
    {
        // Mirrors W3C available-system-properties-002.xsl: bind the result to
        // as="xs:QName+" and locate a specific property QName.
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            version="3.0">
              <xsl:template match="/">
                <xsl:variable name="props" select="available-system-properties()" as="xs:QName+"/>
                <xsl:variable name="q" select="QName('http://www.w3.org/1999/XSL/Transform', 'version')"/>
                <out>
                  <xsl:value-of select="$props[. eq $q] ! ('Q{', namespace-uri-from-QName(.), '}', local-name-from-QName(.))" separator=""/>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");
        result.Should().Contain("Q{http://www.w3.org/1999/XSL/Transform}version");
    }

    [Fact]
    public async Task result_items_are_QNames()
    {
        var stylesheet = """
            <?xml version="1.0" encoding="UTF-8"?>
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            version="3.0">
              <xsl:template match="/">
                <out><xsl:value-of select="available-system-properties()[1] instance of xs:QName"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var result = await TransformAsync(stylesheet, "<doc/>");
        result.Should().Contain("true");
    }
}
