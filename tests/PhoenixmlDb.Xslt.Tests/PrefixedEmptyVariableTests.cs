using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A prefixed variable whose value is the empty sequence is found. Where the prefix reached
/// evaluation unresolved (inside map and array constructors and quantified expressions), the
/// variable is looked up by prefix, and that lookup answered null for both "no such variable"
/// and "found, value ()" — so an empty $p:x was "XPST0008: not bound" while a non-empty one worked.
/// </summary>
public sealed class PrefixedEmptyVariableTests
{
    private static async Task<string> RunAsync(string select)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:p="urn:p" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:variable name="p:x" select="()"/>
              <xsl:template match="/"><xsl:variable name="p:local" select="()"/><xsl:value-of select="{select}"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc/>")).Trim();
    }

    [Theory]
    [InlineData("count(map{'k': $p:x}?k)", "0")]
    [InlineData("some $i in 1 satisfies empty($p:x)", "true")]
    [InlineData("count(map{'k': $p:local}?k)", "0")]
    public async Task AnEmptyPrefixedVariable_IsFound(string select, string expected)
        => (await RunAsync(select)).Should().Be(expected);

    [Fact]
    public async Task AnUndefinedPrefixedVariable_IsStillXPST0008()
    {
        var act = () => RunAsync("count(map{'k': $p:nope}?k)");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("$p:nope");
    }
}
