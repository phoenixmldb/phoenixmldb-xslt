using System;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Coverage for the HTML output method on href-less <c>xsl:result-document</c> primary output
/// (claiming the principal output, routed through the shared <c>FinalizeOutput</c> pipeline).
/// Mirrors W3C conformance cases insn/result-document/result-document-0209 (defaulted html),
/// -0214 (method="html"), -0223 (media-type drives the Content-Type meta) and -0224 (an
/// existing head meta is replaced by the computed media-type + charset). The result-document's
/// own serialization attributes (method / media-type / include-content-type) must reach the
/// HTML post-processing, and an empty non-void element such as <c>&lt;title/&gt;</c> must
/// serialize as <c>&lt;title&gt;&lt;/title&gt;</c> without indentation whitespace inserted
/// between the start and end tags.
/// </summary>
public sealed class ResultDocumentHtmlSerializationTests
{
    private static async Task<string> Transform(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet, new Uri("file:///sheet.xsl"));
        return await t.TransformAsync("<doc/>");
    }

    [Fact]
    public async Task DefaultedHtmlMethod_InsertsContentTypeMeta_AndKeepsEmptyTitleInline()
    {
        // -0209: no method → default output method resolves html from the <html> root; a
        // Content-Type meta is injected and <title/> serializes as <title></title>.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
               <t:template match="/">
                  <t:result-document>
                     <html><head><title/></head><body>hello</body></html>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().MatchRegex(
            @"<[Mm][Ee][Tt][Aa]\s+http-equiv=[""']Content-Type[""']\s+content=[""']text/html;\s+charset=UTF-8[""']\s*>");
        result.Should().Contain("<title></title>");
    }

    [Fact]
    public async Task ExplicitHtmlMethod_InsertsContentTypeMeta_AndKeepsEmptyTitleInline()
    {
        // -0214: method="html" → Content-Type meta injected, <title></title> kept inline.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
               <t:template match="/">
                  <t:result-document method="html">
                     <html><head><title/></head><body>hello</body></html>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().MatchRegex(@"<meta\s+http-equiv=""Content-Type""");
        result.Should().Contain("<title></title>");
    }

    [Fact]
    public async Task HtmlMethod_MediaTypeDrivesContentTypeMeta()
    {
        // -0223: media-type on the result-document must drive the injected Content-Type meta.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
               <t:template match="/">
                  <t:result-document method="html" include-content-type="yes" media-type="application/xhtml-xml">
                     <html><head><title/></head><body>hello</body></html>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().Contain(
            "<meta http-equiv=\"Content-Type\" content=\"application/xhtml-xml; charset=UTF-8\">");
        result.Should().Contain("<title></title>");
    }

    [Fact]
    public async Task HtmlMethod_ExistingContentTypeMetaReplacedWithMediaType()
    {
        // -0224: an existing head Content-Type meta is REPLACED by the computed media-type + charset.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
               <t:template match="/">
                  <t:result-document method="html" include-content-type="yes" media-type="application/xhtml-xml">
                     <html>
                        <head>
                           <meta http-equiv="Content-Type" content="text/html;version='3.0'"/>
                           <title/>
                        </head>
                        <body>hello</body>
                     </html>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().Contain(
            "<meta http-equiv=\"Content-Type\" content=\"application/xhtml-xml; charset=UTF-8\">");
        result.Should().NotContain("text/html;version='3.0'");
        result.Should().Contain("<title></title>");
    }
}
