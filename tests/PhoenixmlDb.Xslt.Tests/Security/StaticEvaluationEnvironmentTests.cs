using FluentAssertions;
using Xunit;

#pragma warning disable CA2007 // xUnit runs tests without a synchronization context

namespace PhoenixmlDb.Xslt.Tests.Security;

/// <summary>
/// fn:environment-variable gives a stylesheet nothing of the process's environment at run time.
/// A static expression (use-when, a static variable, a shadow attribute) is evaluated while the
/// stylesheet compiles, by a different evaluator, and that one read the real environment.
/// </summary>
public sealed class StaticEvaluationEnvironmentTests
{
    private const string Name = "PHX_STATIC_ENV_TEST";

    private static async Task<string> RunAsync(string body)
    {
        Environment.SetEnvironmentVariable(Name, "process-secret");
        try
        {
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync($"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:output omit-xml-declaration="yes"/>
                  {body}
                </xsl:stylesheet>
                """, new Uri("urn:test:main.xsl"));
            return (await transformer.TransformAsync("<x/>")).Trim();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Name, null);
        }
    }

    [Fact]
    public async Task Use_when_cannot_test_the_environment()
        => (await RunAsync($"""
            <xsl:template match="/"><out>
              <xsl:if test="true()" use-when="starts-with(environment-variable('{Name}'), 'process')">leaked</xsl:if>
              <xsl:if test="true()" use-when="available-environment-variables() = '{Name}'">listed</xsl:if>
            </out></xsl:template>
            """)).Should().NotContain("leaked").And.NotContain("listed");

    [Fact]
    public async Task A_static_variable_cannot_read_the_environment()
        => (await RunAsync($$"""
            <xsl:variable name="v" static="yes" select="environment-variable('{{Name}}')"/>
            <xsl:template match="/"><out><xsl:value-of _select="'[{$v}]'"/></out></xsl:template>
            """)).Should().NotContain("process-secret");

    [Fact]
    public async Task A_shadow_attribute_cannot_read_the_environment()
        => (await RunAsync($$"""
            <xsl:template match="/"><out><xsl:value-of _select="'[{environment-variable('{{Name}}')}]'"/></out></xsl:template>
            """)).Should().NotContain("process-secret");

    [Fact]
    public async Task At_run_time_the_environment_is_empty_as_before()
        => (await RunAsync($"""
            <xsl:template match="/"><out><xsl:value-of select="count((environment-variable('{Name}'), available-environment-variables()))"/></out></xsl:template>
            """)).Should().Be("<out>0</out>");
}
