using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Static errors reported under the code the XSLT 3.0 Recommendation gives them. A static variable
/// with a select attribute and content is XTSE0620, the rule for any variable-binding element
/// (W3C use-when-0431); without select it stays XTSE0010.
/// Static arithmetic has the XPath types: every operand was an xs:double, so 1 div 0 in a
/// shadow attribute gave "INF" instead of FOAR0001 (W3C package-version-909).
/// </summary>
public sealed class StaticErrorCodeTests
{
    private static async Task CompileAsync(string declarations, string rootAttributes = "")
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            version="3.0" {rootAttributes}>
              {declarations}
              <xsl:template name="xsl:initial-template"><out/></xsl:template>
            </xsl:stylesheet>
            """);
    }

    [Theory]
    [InlineData("""<xsl:variable name="x" static="yes" select="1"><e/></xsl:variable>""", "XTSE0620")]
    [InlineData("""<xsl:param name="x" static="yes" select="1"><e/></xsl:param>""", "XTSE0620")]
    [InlineData("""<xsl:param name="x" static="yes"><e/></xsl:param>""", "XTSE0010")]
    public async Task Static_variable_content_errors(string declaration, string code)
    {
        var act = () => CompileAsync(declaration);
        (await act.Should().ThrowAsync<XsltException>()).WithMessage($"*{code}*");
    }

    [Theory]
    [InlineData("""<xsl:variable name="v" static="yes" select="1 div 0"/>""")]
    [InlineData("""<xsl:variable name="v" static="yes" select="1"/><xsl:output _indent="{$v div 0}"/>""")]
    [InlineData("""<xsl:variable name="v" use-when="1 idiv 0 = 0" select="1"/>""")]
    public async Task Integer_division_by_zero_in_a_static_expression_is_FOAR0001(string declarations)
    {
        var act = () => CompileAsync(declarations);
        var thrown = (await act.Should().ThrowAsync<Exception>()).Which;
        var code = thrown is PhoenixmlDb.XQuery.Execution.XQueryRuntimeException runtime ? runtime.ErrorCode : thrown.Message;
        code.Should().Contain("FOAR0001");
    }

    [Fact]
    public async Task Static_arithmetic_keeps_the_integer_type()
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:variable name="n" static="yes" select="2 + 3"/>
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">
                <xsl:value-of select="$n instance of xs:integer" use-when="$n instance of xs:integer"/>
              </xsl:template>
            </xsl:stylesheet>
            """);
        (await transformer.TransformAsync((string?)null)).Should().Be("true");
    }
}
