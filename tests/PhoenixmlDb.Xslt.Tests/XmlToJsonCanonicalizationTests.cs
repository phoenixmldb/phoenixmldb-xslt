using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// fn:xml-to-json conformance (W3C fn/xml-to-json D-series): a &lt;number&gt; must be
/// emitted in canonical JSON-number form (xs:double casting rules), and string output
/// must \u-escape DEL and the C1 controls (#x7F–#x9F) and escape the solidus as \/.
/// Mirrors conformance cases D201–D206 (numbers) and D014/D016/D019 (strings).
/// </summary>
public class XmlToJsonCanonicalizationTests
{
    private static async System.Threading.Tasks.Task<string> RunAsync(string jsonXmlBody)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0"
                xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns="http://www.w3.org/2005/xpath-functions"
                exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:template match="/" name="xsl:initial-template">
                <xsl:variable name="in">{{jsonXmlBody}}</xsl:variable>
                <xsl:value-of select="xml-to-json($in)"/>
              </xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync("<in/>"));
    }

    [Theory]
    [InlineData(" 007 ", "7")]            // D201: strip leading zeros
    [InlineData(" -0e0 ", "-0")]          // D202: negative zero preserved
    [InlineData(" 1E6 ", "1.0E6")]        // D203: scientific canonical form
    [InlineData(" -1E-6 ", "-0.000001")]  // D204: fixed notation at boundary
    [InlineData(" .001 ", "0.001")]       // D205: leading zero added
    [InlineData(" 23. ", "23")]           // D206: trailing dot dropped
    public async Task Number_is_canonicalized(string lexical, string expected)
    {
        var r = await RunAsync($"<number>{lexical}</number>");
        r.Should().Be(expected);
    }

    [Fact]
    public async Task Del_control_is_u_escaped()
    {
        // D014: U+007F DEL
        var r = await RunAsync("<string escaped=\"1\">-&#127;-</string>");
        r.Should().Be("\"-\\u007F-\"");
    }

    [Fact]
    public async Task C1_control_is_u_escaped()
    {
        // D016: U+0090
        var r = await RunAsync("<string escaped=\"1\">-&#144;-</string>");
        r.Should().Be("\"-\\u0090-\"");
    }

    [Fact]
    public async Task Solidus_is_escaped()
    {
        // D019: solidus is escaped (bug 29665)
        var r = await RunAsync("<string escaped=\"1\">-/-</string>");
        r.Should().Be("\"-\\/-\"");
    }
}
