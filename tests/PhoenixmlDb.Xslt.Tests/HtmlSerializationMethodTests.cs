using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Focused regression coverage for the decl/output HTML/XHTML serializer sub-slice:
/// URI-attribute %-escaping (NFC before UTF-8 octet mapping), XHTML well-formed empty
/// (void) elements, and Content-Type meta replacement. These mirror the W3C
/// output-0101/0132/0143/0157 conformance cases and lock the behavior at the unit level.
/// </summary>
public class HtmlSerializationMethodTests
{
    private static async Task<string> Transform(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync("<in/>");
    }

    [Fact]
    public async Task HtmlUriAttribute_NormalizesToNfc_BeforePercentEncoding()
    {
        // href holds a decomposed "a" + U+030A (combining ring above); escape-uri-attributes
        // must compose to U+00E5 and emit %C3%A5, not the decomposed a%CC%8A (output-0101).
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="html" escape-uri-attributes="yes" indent="no"/>
              <xsl:template match="/"><a href="http://x.example/a&#x30A;r">t</a></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().Contain("%C3%A5r");
        result.Should().NotContain("a%CC%8A");
    }

    [Fact]
    public async Task XhtmlVoidElement_IsWellFormedEmpty_WithSpaceSlash()
    {
        // XHTML method: <br/> serializes as <br /> (well-formed empty), and a non-void empty
        // element expands to start+end tags (output-0132).
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns="http://www.w3.org/1999/xhtml">
              <xsl:output method="xhtml" indent="no"/>
              <xsl:template match="/"><body><br/><Option selected="selected"/></body></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().Contain("<br />");
        result.Should().Contain("<Option selected=\"selected\"></Option>");
    }

    [Fact]
    public async Task ContentTypeMeta_ReplacesExistingMeta_WithComputedCharset()
    {
        // include-content-type default: an existing http-equiv="Content-Type" meta (here with a
        // stale UTF-16 charset) is REPLACED by the serializer's computed value, leaving a single
        // correct meta (output-0157).
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="html" encoding="UTF-8"/>
              <xsl:template match="/"><html><head>
                <meta http-equiv="Content-Type" content="text/html; charset=UTF-16"/>
              </head><body>x</body></html></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().Contain("content=\"text/html; charset=UTF-8\"");
        result.Should().NotContain("charset=UTF-16");
        System.Text.RegularExpressions.Regex.Count(result, "http-equiv=\"Content-Type\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Should().Be(1);
    }

    [Fact]
    public async Task HtmlScript_ContentIsRawText_NotXmlEscaped()
    {
        // HTML method: script is a CDATA (raw-text) element — its content is emitted verbatim,
        // < > & are NOT escaped (output-0154). document.write("<EM>…</EM>") stays literal.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="html" indent="no"/>
              <xsl:template match="/"><html><head>
                <script type="text/javascript">document.write ("&lt;EM&gt;This will work&lt;\/EM&gt;")</script>
              </head><body/></html></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().Contain("document.write (\"<EM>This will work<\\/EM>\")");
        result.Should().NotContain("&lt;EM&gt;");
    }

    [Fact]
    public async Task HtmlStyle_ContentIsRawText_NotXmlEscaped()
    {
        // HTML method: style is likewise a raw-text (CDATA) element (output-0159).
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="html" indent="no"/>
              <xsl:template match="/"><html><head>
                <style>&lt; &gt; &amp;</style>
              </head><body/></html></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().Contain("<style>< > &</style>");
    }

    [Fact]
    public async Task HtmlScript_ElementNameMatchIsCaseInsensitive()
    {
        // HTML element-name matching is case-insensitive: SCRIPT is still a raw-text element.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="html" indent="no"/>
              <xsl:template match="/"><html><head>
                <SCRIPT>if (a &lt; b &amp;&amp; b &gt; c) {}</SCRIPT>
              </head><body/></html></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().Contain("if (a < b && b > c) {}");
        result.Should().NotContain("&lt;");
        result.Should().NotContain("&amp;&amp;");
    }

    [Fact]
    public async Task XhtmlScript_ContentStillXmlEscaped_NoOverreach()
    {
        // Guard: the raw-text exemption is HTML-only. The XHTML method serializes script
        // content by XML rules — < and & MUST still be escaped.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns="http://www.w3.org/1999/xhtml">
              <xsl:output method="xhtml" indent="no"/>
              <xsl:template match="/"><html><head>
                <script>if (a &lt; b) x = "&amp;";</script>
              </head><body/></html></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().Contain("a &lt; b");
        result.Should().Contain("\"&amp;\"");
    }

    [Fact]
    public async Task HtmlNormalElement_StillEscapesSpecialChars()
    {
        // Guard: a normal element (p) is NOT raw-text — < and & are escaped as usual.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="html" indent="no"/>
              <xsl:template match="/"><html><body><p>a &lt; b &amp; c</p></body></html></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().Contain("a &lt; b &amp; c");
    }
}
