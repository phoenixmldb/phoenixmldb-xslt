using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// fn:current-output-uri() reports the base output URI while the principal result is produced, and is absent in
/// temporary output state: inside a stylesheet function or a variable body (XSLT 3.0 §20.3.7; W3C
/// current-output-uri-005, -016, -017). The engine returned the base output URI there. The direct test is a guard.
/// </summary>
public sealed class CurrentOutputUriTemporaryStateTests
{
    private const string Base = "file:///results/out.xml";

    private static async Task<string> RunAsync(string body)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:function name="f:uri"><xsl:sequence select="current-output-uri()"/></xsl:function>
              <xsl:template match="/">{{body}}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        t.SetBaseOutputUri(new Uri(Base));
        return (await t.TransformAsync("<doc/>")).Trim();
    }

    [Fact]
    public async Task While_producing_the_principal_result_it_is_the_base_output_uri()
        => (await RunAsync("""<xsl:value-of select="current-output-uri()"/>""")).Should().Be(Base);

    [Fact]
    public async Task Inside_a_stylesheet_function_it_is_absent()
        => (await RunAsync("""<xsl:value-of select="empty(f:uri())"/>""")).Should().Be("true");

    [Fact]
    public async Task Inside_a_variable_body_it_is_absent()
        => (await RunAsync("""<xsl:variable name="v"><xsl:value-of select="empty(current-output-uri())"/></xsl:variable><xsl:value-of select="$v"/>"""))
            .Should().Be("true");
}
