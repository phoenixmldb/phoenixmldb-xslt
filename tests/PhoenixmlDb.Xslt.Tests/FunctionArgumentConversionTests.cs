using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An argument the function conversion rules cannot convert to a stylesheet function's declared
/// parameter type is XPTY0004 (XSLT 3.0 §5.4.1). XTTE0790 was the XSLT 2.0 spelling of the same
/// error and is not a 3.0 code (W3C error-0790a3, whose 2.0 twin error-0790a2 still expects the
/// old one and is skipped as a 3.0 processor).
/// </summary>
public sealed class FunctionArgumentConversionTests
{
    private static async Task<string> RunAsync(string body, string declarations)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:my="urn:my" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {declarations}
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc/>")).Trim();
    }

    private const string DateFunction = """<xsl:function name="my:f"><xsl:param name="x" as="xs:date"/><xsl:sequence select="string($x)"/></xsl:function>""";

    [Fact]
    public async Task AnArgumentThatCannotBeConverted_IsXPTY0004()
    {
        var act = () => RunAsync("""<xsl:value-of select="my:f(2)"/>""", DateFunction);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XPTY0004");
    }

    /// <summary>
    /// The conversion rules still apply: a typed value of the right type passes, and an UNTYPED
    /// one (a node's atomized value) is converted. A string is not — xs:string does not convert
    /// to xs:date — so that case is XPTY0004 too, which is why the control uses a node.
    /// </summary>
    [Theory]
    [InlineData("""my:f(xs:date('2026-09-11'))""")]
    [InlineData("""my:f(/doc/@d)""")]
    public async Task AnArgumentTheRulesCanConvert_StillWorks(string call)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:my="urn:my" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {DateFunction}
              <xsl:template match="/"><xsl:value-of select="{call}"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        (await t.TransformAsync("""<doc d="2026-09-11"/>""")).Trim().Should().Be("2026-09-11");
    }

    [Fact]
    public async Task AnIntegerParameterGivenAString_IsXPTY0004()
    {
        var act = () => RunAsync("""<xsl:value-of select="my:g('banana')"/>""",
            """<xsl:function name="my:g"><xsl:param name="n" as="xs:integer"/><xsl:sequence select="$n"/></xsl:function>""");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XPTY0004");
    }
}
