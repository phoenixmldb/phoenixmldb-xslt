using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression coverage for the decl/output XHTML/HTML5 serialization sub-slice: HTML5 DOCTYPE
/// emission (html-version &gt;= 5), case-preserving DOCTYPE names, and foreign-namespace document
/// elements being serialized by XML rules (no DOCTYPE, no Content-Type meta). Mirrors the W3C
/// output-0208/0209/0210/0212/0214/0229/0233 conformance cases.
/// </summary>
public class XhtmlDoctypeForeignNamespaceTests
{
    private static async Task<string> Transform(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync("<in/>");
    }

    [Fact]
    public async Task Xhtml5_EmitsHtml5Doctype_AndContentTypeMeta()
    {
        // output-0208: XHTML method, html-version 5.0, XHTML-namespace root.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xhtml" html-version="5.0"/>
              <t:template match="/"><html xmlns="http://www.w3.org/1999/xhtml"><head><title>test</title></head><body><p>content</p></body></html></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        Regex.IsMatch(result, "<!DOCTYPE html>\\s*<html").Should().BeTrue(result);
        Regex.IsMatch(result, "<meta\\s+").Should().BeTrue(result);
    }

    [Fact]
    public async Task Xhtml5_DoctypeNameIsCaseSensitive_UpperCaseHtml()
    {
        // output-0209: <HTML> is NOT the html element; the DOCTYPE name preserves the element's
        // exact casing -> <!DOCTYPE HTML>.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xhtml" html-version="5.0"/>
              <t:template match="/"><HTML xmlns="http://www.w3.org/1999/xhtml"><HEAD><title>test</title></HEAD><BODY><p>content</p></BODY></HTML></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        Regex.IsMatch(result, "<!DOCTYPE HTML>\\s*<HTML").Should().BeTrue(result);
    }

    [Fact]
    public async Task Xhtml5_DoctypeNameIsCaseSensitive_MixedCaseHtml()
    {
        // output-0210: mixed-case <HtMl> -> <!DOCTYPE HtMl>.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xhtml" html-version="5"/>
              <t:template match="/"><HtMl xmlns="http://www.w3.org/1999/xhtml"><HeAd><Title>test</Title></HeAd><BoDy><P>content</P></BoDy></HtMl></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        Regex.IsMatch(result, "<!DOCTYPE HtMl>\\s*<HtMl").Should().BeTrue(result);
    }

    [Fact]
    public async Task Xhtml5_DecimalVersion500_EmitsDoctype_WithLeadingWhitespaceText()
    {
        // output-0212: html-version "5.00" (still >= 5) with leading whitespace text before <html>.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xhtml" html-version="5.00"/>
              <t:template match="/"><t:text>   </t:text><html xmlns="http://www.w3.org/1999/xhtml"><head><title>test</title></head><body><p>content</p></body></html></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        Regex.IsMatch(result, "<!DOCTYPE html>\\s*<html").Should().BeTrue(result);
    }

    [Fact]
    public async Task Xhtml5_ForeignNamespaceRoot_SerializedByXmlRules_NoDoctypeNoMeta()
    {
        // output-0214: a document element in a non-XHTML namespace is foreign: no DOCTYPE, no
        // Content-Type meta injected into its <head>.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xhtml" html-version="5.0"/>
              <t:template match="/"><html xmlns="http://www.example.com/not-xhtml"><head><title>Not XHTML!</title></head><body><p>But what is it then?</p></body></html></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        Regex.IsMatch(result, "<html\\s+xmlns=\"http://www.example.com/not-xhtml\">").Should().BeTrue(result);
        result.Should().NotContain("DOCTYPE");
        result.Should().NotContain("meta");
    }

    [Fact]
    public async Task Xhtml5_DoctypePublicOnly_StillEmitsHtml5Doctype()
    {
        // output-0229: doctype-public set, no doctype-system, html-version 5 -> HTML5 DOCTYPE
        // (W3C bug 20264 ruling).
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xhtml" html-version="5.0" doctype-public="-//W3C//DTD XHTML 1.0 Strict//EN"/>
              <t:template match="/"><html xmlns="http://www.w3.org/1999/xhtml"><head><title>Heading</title></head><body><p>Hello, world!</p></body></html></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        Regex.IsMatch(result, "<!DOCTYPE\\s+html\\s*>").Should().BeTrue(result);
    }

    [Fact]
    public async Task Html5_DoctypeComesAfterLeadingComment_BeforeRootElement()
    {
        // output-0233: with HTML output, the DOCTYPE must come immediately before the first
        // element, after a preceding comment.
        const string ss = """
            <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="html" html-version="5" indent="no"/>
              <xsl:template match="/"><xsl:comment>This should precede the DOCTYPE declaration</xsl:comment><html><head><title>Title</title></head><body><p>Content</p></body></html></xsl:template>
            </xsl:transform>
            """;
        var result = await Transform(ss);
        Regex.IsMatch(result, "This should precede the DOCTYPE declaration-->\\s*<!DOCTYPE (html|HTML)>\\s*<html",
            RegexOptions.Singleline).Should().BeTrue(result);
    }

    [Fact]
    public async Task Xhtml5_NonHtmlRootElement_EmitsNoDoctype()
    {
        // output-0213: XHTML method, html-version 5, but the document element is <body>, not
        // <html>: no DOCTYPE is emitted.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xhtml" html-version="5.0"/>
              <t:template match="/"><body xmlns="http://www.w3.org/1999/xhtml"><p>content</p></body></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().NotContain("DOCTYPE");
    }

    [Fact]
    public async Task Html5_NonHtmlRootElement_EmitsNoDoctype()
    {
        // output-0724: HTML method, html-version 5, document element is <input> (void): no DOCTYPE,
        // and the void element is serialized without a trailing slash.
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="html" html-version="5"/>
              <xsl:template match="/"><input type="text" value="x"/></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().NotContain("DOCTYPE");
        result.Should().Contain("<input");
        result.Should().NotContain("/>");
    }

    [Fact]
    public async Task HtmlOrXhtml_WithoutHtml5Version_EmitsNoDoctype()
    {
        // Guard: html/xhtml output with no html-version (or a version < 5) must NOT gain a DOCTYPE.
        const string ss = """
            <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="html" html-version="4.0" indent="no"/>
              <xsl:template match="/"><html><head><title>t</title></head><body><p>x</p></body></html></xsl:template>
            </xsl:transform>
            """;
        var result = await Transform(ss);
        result.Should().NotContain("DOCTYPE");
    }
}
