using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A global variable is evaluated once. Globals are initialized in dependency order, and a
/// dependency the static analysis misses is initialized on demand when first read. The ordered
/// pass then evaluated that global a second time and rebound it: a node it built got a second
/// identity, and anything the evaluation did happened twice.
/// </summary>
public sealed class GlobalEvaluatedOnceTests
{
    // f:get reads $b from inside a local variable, which the dependency analysis does not follow,
    // so $a (evaluated first) initializes $b on demand.
    private const string Functions = """
        <xsl:function name="f:get"><xsl:variable name="t" select="$b"/><xsl:sequence select="$t"/></xsl:function>
        """;

    private static async Task<(string Output, List<string> Messages)> RunAsync(string declarations, string body)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {{declarations}}
              <xsl:template match="/">{{body}}</xsl:template>
            </xsl:stylesheet>
            """;
        var messages = new List<string>();
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        t.MessageListener = (text, _) => messages.Add(text);
        return ((await t.TransformAsync("<doc/>")).Trim(), messages);
    }

    [Fact]
    public async Task AGlobalInitializedOnDemand_KeepsItsNodeIdentity()
    {
        var (output, _) = await RunAsync(Functions + """
            <xsl:variable name="a" select="f:get()"/>
            <xsl:variable name="b" select="parse-xml('&lt;x/&gt;')"/>
            """, """<xsl:value-of select="$a is $b"/>""");
        output.Should().Be("true");
    }

    [Fact]
    public async Task AGlobalInitializedOnDemand_IsEvaluatedOnce()
    {
        var (_, messages) = await RunAsync(Functions + """
            <xsl:variable name="a" select="f:get()"/>
            <xsl:variable name="b"><xsl:message>evaluating b</xsl:message><x/></xsl:variable>
            """, """<xsl:value-of select="count(($a, $b))"/>""");
        messages.Should().ContainSingle(m => m.Contains("evaluating b"));
    }
}
