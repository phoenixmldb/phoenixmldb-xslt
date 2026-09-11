using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Element and document nodes the engine builds must report their string value. A node's string
/// value is its cached value, else its resolver's answer, else — silently — the empty string, so a
/// node built with children but neither a value nor a resolver read as empty: indistinguishable
/// from a genuinely empty node, and printed as nothing.
/// </summary>
/// <remarks>
/// This test project runs with <c>PhoenixmlDb.Xdm.StrictStringValue</c> on
/// (runtimeconfig.template.json), which makes that third case throw instead of returning "". So
/// any construction site added without <c>StringValueResolver = store.StringValueResolver</c>
/// fails the suite rather than printing nothing in production.
/// </remarks>
public sealed class NodeStringValueTests
{
    private static async Task<string> RunAsync(string body, string declarations = "")
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {{declarations}}
              <xsl:template match="/">{{body}}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc>source</doc>")).Trim();
    }

    [Theory]
    [InlineData("""<xsl:variable name="v" as="document-node()"><a>hello</a></xsl:variable><xsl:value-of select="$v"/>""", "hello")]
    [InlineData("""<xsl:variable name="v" as="document-node()"><a>he<b>ll</b>o</a></xsl:variable><xsl:value-of select="$v"/>""", "hello")]
    [InlineData("""<xsl:variable name="v" as="document-node()"><xsl:copy-of select="/"/></xsl:variable><xsl:value-of select="$v"/>""", "source")]
    [InlineData("""<xsl:value-of select="json-to-xml('{&quot;a&quot;:&quot;x&quot;}')"/>""", "x")]
    [InlineData("""<xsl:value-of select="json-to-xml('[&quot;p&quot;,&quot;q&quot;]')"/>""", "pq")]
    public async Task AConstructedNode_ReportsItsStringValue(string body, string expected)
        => (await RunAsync(body)).Should().Be(expected);

    [Fact]
    public async Task ATypedGlobalDocument_ReportsItsStringValue()
        => (await RunAsync("""<xsl:value-of select="$g"/>""",
                """<xsl:variable name="g" as="document-node()"><r>glo<s>bal</s></r></xsl:variable>"""))
            .Should().Be("global");
}
