using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An xs:anyURI passed where xs:string is declared is PROMOTED to xs:string (the function
/// conversion rules), as W3C function-1015 requires. XSLT kept it as an xs:anyURI because it
/// "already matched"; the case passed only while fn:namespace-uri wrongly returned a plain string.
/// </summary>
public class AnyUriPromotionTests
{
    private static async Task<string> RunAsync(string body)
    {
        var xsl = $$"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                xmlns:x="urn:x" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="x xs">
              <xsl:function name="x:is-uri" as="xs:boolean"><xsl:param name="n" as="item()"/><xsl:sequence select="$n instance of xs:anyURI"/></xsl:function>
              {{body}}
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        return await t.TransformAsync("<dummy/>");
    }

    [Fact]
    public async Task A_function_parameter_declared_string_receives_a_string()
        => (await RunAsync("""
            <xsl:function name="x:test" as="xs:boolean"><xsl:param name="in" as="xs:string"/><xsl:sequence select="x:is-uri($in)"/></xsl:function>
            <xsl:template match="/"><out><xsl:value-of select="x:test(xs:anyURI('http://a/'))"/></out></xsl:template>
            """)).Should().Contain("<out>false</out>");

    /// <summary>Guard: a variable declared xs:string already converted; it must keep doing so.</summary>
    [Fact]
    public async Task A_variable_declared_string_holds_a_string()
        => (await RunAsync("""
            <xsl:template match="/"><xsl:variable name="v" as="xs:string" select="xs:anyURI('http://a/')"/><out><xsl:value-of select="x:is-uri($v)"/></out></xsl:template>
            """)).Should().Contain("<out>false</out>");

    /// <summary>Guard: a parameter declared xs:anyURI keeps the anyURI.</summary>
    [Fact]
    public async Task A_parameter_declared_anyURI_keeps_it()
        => (await RunAsync("""
            <xsl:function name="x:test" as="xs:boolean"><xsl:param name="in" as="xs:anyURI"/><xsl:sequence select="x:is-uri($in)"/></xsl:function>
            <xsl:template match="/"><out><xsl:value-of select="x:test(xs:anyURI('http://a/'))"/></out></xsl:template>
            """)).Should().Contain("<out>true</out>");
}
