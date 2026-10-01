using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:expose/@names: an undeclared prefix is XTSE0020 (W3C expose-927), and with
/// component="function" a non-wildcard name needs its arity, name#N (XTSE3020, erratum E36,
/// expose-926).
/// </summary>
public class ExposeNamesValidationTests
{
    private static async Task<string?> CompileError(string expose)
    {
        try
        {
            await new XsltTransformer().LoadStylesheetAsync($"""
                <xsl:package name="urn:p" package-version="1.0.0" version="3.0"
                    xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:p="urn:p">
                  <xsl:function name="p:f"><xsl:sequence select="1"/></xsl:function>
                  <xsl:template name="main" visibility="public"><ok/></xsl:template>
                  {expose}
                </xsl:package>
                """);
            return null;
        }
        catch (XsltException e)
        {
            return e.Message[..8];
        }
    }

    [Fact]
    public async Task A_function_name_without_arity_is_XTSE3020() =>
        (await CompileError("<xsl:expose visibility='public' component='function' names='p:f'/>")).Should().Be("XTSE3020");

    [Fact]
    public async Task An_undeclared_prefix_is_XTSE0020() =>
        (await CompileError("<xsl:expose visibility='public' component='function' names='q:*'/>")).Should().Be("XTSE0020");

    [Theory]
    [InlineData("p:f#0")]
    [InlineData("p:*")]
    [InlineData("*")]
    public async Task Arity_qualified_and_wildcard_names_are_accepted(string names) =>
        (await CompileError($"<xsl:expose visibility='public' component='function' names='{names}'/>")).Should().BeNull();
}
