using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A global xsl:variable bound from text content kept the references of its serialized form:
/// as="xs:string" with ^[^&amp;amp;] held the 9 characters "^[^&amp;amp;]" (a local variable held
/// "^[^&amp;]"), and as="text()" held "a&amp;lt;b". Expected values are Saxon-HE 12.10's.
/// </summary>
public sealed class GlobalVariableTextContentTests
{
    private static async Task<string> RunAsync(string declarations, string select)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="text"/>
              {declarations}
              <xsl:template match="/"><xsl:value-of select="{select}" separator="|"/></xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync("<r/>")).Trim();
    }

    [Theory]
    [InlineData("""<xsl:variable name="g" as="xs:string">^[^&amp;]</xsl:variable>""", "$g, string-length($g)", "^[^&]|5")]
    [InlineData("""<xsl:variable name="g" as="xs:string">^[^&amp;]</xsl:variable>""", "matches('a', $g)", "true")]
    [InlineData("""<xsl:variable name="t" as="text()">a&lt;b&amp;c&gt;</xsl:variable>""", "string($t)", "a<b&c>")]
    [InlineData("""<xsl:variable name="n" as="xs:integer">&#x31;2</xsl:variable>""", "$n + 1", "13")]
    [InlineData("""<xsl:variable name="u">x &amp; y</xsl:variable>""", "string($u), string-length($u)", "x & y|5")]
    [InlineData("""<xsl:variable name="e" as="xs:string"><xsl:text>p&amp;q</xsl:text></xsl:variable>""", "$e", "p&q")]
    public async Task Binds_the_decoded_text(string declarations, string select, string expected)
        => (await RunAsync(declarations, select)).Should().Be(expected);
}
