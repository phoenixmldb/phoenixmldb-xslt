using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Streamability analysis of calls to, and bodies of, streamable stylesheet functions
/// (XSLT 3.0 §19.8.5). Each of the first three was rejected as XTSE3430 though the spec
/// makes it guaranteed-streamable (W3C function-5007/5013, use-package-150..152).
/// </summary>
public class StreamableFunctionUsageTests
{
    private const string Head = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f">
          <xsl:mode streamable="yes"/>
        """;

    private static async Task<string?> CompileError(string body)
    {
        try
        {
            await new XsltTransformer().LoadStylesheetAsync(Head + body + "</xsl:stylesheet>");
            return null;
        }
        catch (XsltException e)
        {
            return e.Message;
        }
    }

    [Fact]
    public async Task Passing_dot_to_an_ascent_function_does_not_atomize_it() =>
        (await CompileError("""
            <xsl:template match="/"><xsl:copy-of select="*/section[f:up(.)/local-name() = 'doc']"/></xsl:template>
            <xsl:function name="f:up" streamability="ascent" as="node()*">
              <xsl:param name="n" as="node()"/><xsl:sequence select="$n/ancestor-or-self::*[last()]"/>
            </xsl:function>
            """)).Should().BeNull();

    [Fact]
    public async Task Calling_another_arity_of_the_same_name_is_not_recursion() =>
        (await CompileError("""
            <xsl:template match="/"><xsl:value-of select="f:j(.)"/></xsl:template>
            <xsl:function name="f:j" streamability="absorbing" as="xs:string" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:param name="in" as="node()"/><xsl:sequence select="f:j($in, 1)"/>
            </xsl:function>
            <xsl:function name="f:j" streamability="absorbing" as="xs:string" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:param name="in" as="node()"/><xsl:param name="x"/><xsl:sequence select="string($in)"/>
            </xsl:function>
            """)).Should().BeNull();

    [Fact]
    public async Task Choose_branches_each_consuming_once_are_alternatives() =>
        (await CompileError("""
            <xsl:template match="/"><xsl:value-of select="f:k(., true())"/></xsl:template>
            <xsl:function name="f:k" streamability="absorbing" as="xs:string" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:param name="in" as="node()"/><xsl:param name="b" as="xs:boolean"/>
              <xsl:choose>
                <xsl:when test="$b"><xsl:sequence select="string($in)"/></xsl:when>
                <xsl:otherwise><xsl:sequence select="upper-case(string($in))"/></xsl:otherwise>
              </xsl:choose>
            </xsl:function>
            """)).Should().BeNull();

    [Fact]
    public async Task Consuming_twice_in_sequence_is_still_rejected() =>
        (await CompileError("""
            <xsl:template match="/"><xsl:value-of select="f:m(.)"/></xsl:template>
            <xsl:function name="f:m" streamability="absorbing" as="xs:string" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:param name="in" as="node()"/>
              <xsl:sequence select="string($in) || string($in)"/>
            </xsl:function>
            """)).Should().Contain("XTSE3430");
}
