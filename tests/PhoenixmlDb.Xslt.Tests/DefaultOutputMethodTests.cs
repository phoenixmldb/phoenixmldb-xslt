using System;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Coverage for the Serialization 4.0 default-output-method resolution on the NON-STREAMING
/// primary result path: when <c>xsl:output</c> specifies no explicit <c>method</c>, a serialized
/// result whose document element is <c>html</c> selects the <c>html</c> method (no namespace) or
/// the <c>xhtml</c> method (XHTML namespace) instead of falling back to <c>xml</c>, which in turn
/// injects the Content-Type <c>&lt;meta&gt;</c>. Mirrors W3C output-0715 / output-0130.
///
/// The streaming-safety guard (a streamable transform whose result is html-rooted must NOT get the
/// html/xhtml treatment) lives here and in the unmodified StreamingForEachGroupTest.
/// </summary>
public class DefaultOutputMethodTests
{
    private static async Task<string> Transform(string stylesheet, string input = "<in/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(input);
    }

    [Fact]
    public async Task DefaultMethod_HtmlRoot_NoNamespace_ResolvesHtml_InsertsContentTypeMeta()
    {
        // output-0715: no xsl:output at all; the result's document element is <html> in no
        // namespace, so the default output method resolves to html and a Content-Type meta is
        // inserted as the first child of <head> (HTML method emits it unclosed).
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><html><head><title>A document</title></head><body><p>Some content</p></body></html></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().Contain("http-equiv=\"Content-Type\"", $"actual:\n{result}");
        result.Should().Contain("content=\"text/html; charset=UTF-8\"", $"actual:\n{result}");
        // meta precedes the title (first child of head).
        result.IndexOf("http-equiv=\"Content-Type\"", StringComparison.Ordinal)
            .Should().BeLessThan(result.IndexOf("<title>", StringComparison.Ordinal), $"actual:\n{result}");
    }

    [Fact]
    public async Task DefaultMethod_HtmlRoot_XhtmlNamespace_ResolvesXhtml_InsertsContentTypeMeta()
    {
        // output-0130: no explicit output method; the document element is <html> in the XHTML
        // namespace, so the default output method resolves to xhtml. The Content-Type meta is
        // inserted (self-closed under xhtml), and an XML declaration is emitted.
        const string ss = """
            <t:transform xmlns="http://www.w3.org/1999/xhtml"
                         xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:template match="/">
                <html><head><title>Default output method</title></head><body><p>x</p></body></html>
              </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().Contain("http-equiv=\"Content-Type\"", $"actual:\n{result}");
        result.Should().Contain("content=\"text/html; charset=UTF-8\"", $"actual:\n{result}");
        result.Should().MatchRegex("<\\?xml version=\"1.0\" encoding=\"UTF-8\"\\?>", $"actual:\n{result}");
    }

    [Fact]
    public async Task DefaultMethod_NonHtmlRoot_StaysXml_NoMeta()
    {
        // A non-html document element keeps the xml default: no Content-Type meta.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><doc><head><title>x</title></head></doc></xsl:template>
            </xsl:stylesheet>
            """;
        var result = await Transform(ss);
        result.Should().NotContain("http-equiv", $"actual:\n{result}");
    }

    [Fact]
    public async Task StreamingHtmlRootedResult_DoesNotResolveHtmlMethod_NoMeta()
    {
        // STREAMING-SAFETY GUARD: a streamable transform whose serialized result is <html>-rooted
        // in no namespace must NOT pick up the default html method — no Content-Type meta, no html
        // indentation. The default-method resolution is restricted to the non-streaming path.
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                xmlns:xs="http://www.w3.org/2001/XMLSchema"
                exclude-result-prefixes="#all">
                <xsl:mode on-no-match="shallow-copy" streamable="yes"/>
                <xsl:template match="body">
                    <xsl:copy>
                        <xsl:for-each-group select="*" group-starting-with="h1">
                            <section>
                                <xsl:apply-templates select="current-group()"/>
                            </section>
                        </xsl:for-each-group>
                    </xsl:copy>
                </xsl:template>
            </xsl:stylesheet>
            """);
        var input = """
            <html>
              <body>
                <h1>Section 1</h1>
                <p>p1</p>
                <h1>Section 2</h1>
                <p>p2</p>
              </body>
            </html>
            """;
        var result = await transformer.TransformAsync(input);
        result.Should().NotContain("http-equiv", $"streamed html-rooted result must not get a meta; actual:\n{result}");
        // h1 stays tight (no html-method indentation) exactly as before the change.
        result.Should().Contain("<h1>Section 1</h1>", $"actual:\n{result}");
    }
}
