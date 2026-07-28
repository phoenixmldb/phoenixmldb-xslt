using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 / XPath F&amp;O §5.3.4: with no <c>collation</c> and no <c>lang</c>, <c>xsl:sort</c>
/// uses the default collation, whose default is the Unicode codepoint collation — uppercase
/// (A = U+0041) sorts before lowercase (a = U+0061), NOT the locale-aware, effectively
/// case-insensitive order the engine used previously (`a B c Z`). Regression coverage for
/// strm/si-iterate si-iterate-135 (a case-sensitive sort over element names).
/// </summary>
public class DefaultSortCollationTests
{
    private static async Task<string> Transform(string stylesheet, string input = "<in/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(input);
    }

    [Fact]
    public async Task DefaultSort_UsesUnicodeCodepointOrder_UppercaseBeforeLowercase()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
                <out><xsl:for-each select="('a','Z','B','c')"><xsl:sort select="."/><i><xsl:value-of select="."/></i></xsl:for-each></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // Codepoint: B(0x42) Z(0x5A) a(0x61) c(0x63). (Was `a B c Z` under locale-aware compare.)
        (await Transform(ss)).Should().Be("<out><i>B</i><i>Z</i><i>a</i><i>c</i></out>");
    }

    [Fact]
    public async Task Sort_WithExplicitLang_KeepsLocaleAwareOrder()
    {
        // Guard against over-correction: a lang-qualified sort still uses locale-aware comparison
        // (case-insensitive alphabetical), NOT codepoint.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
                <out><xsl:for-each select="('a','Z','B','c')"><xsl:sort select="." lang="en"/><i><xsl:value-of select="."/></i></xsl:for-each></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        (await Transform(ss)).Should().Be("<out><i>a</i><i>B</i><i>c</i><i>Z</i></out>");
    }
}
