using System;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Coverage for the XML declaration emitted on <c>xsl:result-document</c> primary output
/// (href-less, claiming the principal output). Mirrors W3C conformance cases
/// insn/result-document/result-document-0229 (standalone="yes"), -0230 (standalone="no"),
/// -0231 (standalone="omit" → no standalone in decl), -0234 (output-version, decl present) and
/// -0206 (empty content, decl still present). The result-document's own serialization
/// attributes must drive the declaration, not the (absent) stylesheet-level xsl:output.
/// </summary>
public sealed class ResultDocumentXmlDeclarationTests
{
    private static async Task<string> Transform(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet, new Uri("file:///sheet.xsl"));
        return await t.TransformAsync("<!-- useless input --><doc/>");
    }

    [Fact]
    public async Task StandaloneYes_IsEmittedInDeclaration()
    {
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
               <t:template match="/">
                  <t:result-document method="xml" standalone="yes"><out>hello</out></t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().MatchRegex(@"<\?xml\s+version=""1.0""\s+encoding=""UTF-8""\s+standalone=""yes""\?>\s*<out>hello</out>");
    }

    [Fact]
    public async Task StandaloneNo_IsEmittedInDeclaration()
    {
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
               <t:template match="/">
                  <t:result-document method="xml" standalone="no"><out>hello</out></t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().MatchRegex(@"<\?xml\s+version=""1.0""\s+encoding=""UTF-8""\s+standalone=""no""\?>\s*<out>hello</out>");
    }

    [Fact]
    public async Task StandaloneOmit_HasNoStandaloneAttribute()
    {
        // -0231: standalone="omit" → declaration present but WITHOUT a standalone attribute.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
               <t:template match="/">
                  <t:result-document method="xml" standalone="omit"><out>hello</out></t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().MatchRegex(@"<\?xml\s+version=""1.0""\s+encoding=""UTF-8""\?>\s*<out>hello</out>");
        result.Should().NotContain("standalone");
    }

    [Fact]
    public async Task NoStandalone_HasNoStandaloneAttribute()
    {
        // Guard: no standalone param at all → declaration present but WITHOUT a standalone attribute.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
               <t:template match="/">
                  <t:result-document method="xml"><out>hello</out></t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().NotContain("standalone");
        result.Should().Contain("<out>hello</out>");
    }

    [Fact]
    public async Task OutputVersion_DeclarationIsPresent()
    {
        // -0234: output-version="1.0" and no method → XML declaration must still be emitted.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
               <t:template match="/">
                  <t:result-document output-version="1.0"><out>hello</out></t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().MatchRegex(@"<\?xml\s+version=['""]1.0['""].*>\s*<out>hello</out>");
    }

    [Fact]
    public async Task EmptyContentNoAttributes_DeclarationIsPresent()
    {
        // -0206: empty content, no attributes → output is exactly the XML declaration.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
               <t:template match="/">
                  <t:result-document/>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().MatchRegex(@"^<\?xml\s+version=""1.0""\s+encoding=""UTF-8""\?>\s*$");
    }

    [Fact]
    public async Task OmitXmlDeclarationYes_SuppressesDeclaration()
    {
        // Guard: omit-xml-declaration="yes" → no declaration at all.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
               <t:template match="/">
                  <t:result-document method="xml" omit-xml-declaration="yes"><out>hello</out></t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().NotContain("<?xml");
        result.Should().Contain("<out>hello</out>");
    }

    [Fact]
    public async Task TextMethod_OmitDeclWithStandalone_DoesNotRaiseSepm0009()
    {
        // insn/result-document-0239: a named xsl:output with method="text" carrying
        // omit-xml-declaration="yes" and standalone="no" (the latter surviving an import
        // override) must serialize as text without raising SEPM0009 — the omit-xml-declaration
        // and standalone parameters do not apply to the text output method.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0"
              xmlns:my="http://example1.com">
               <t:output name="my:temp" method="text" omit-xml-declaration="yes" standalone="no"/>
               <t:template match="/">
                  <t:result-document format="my:temp"><out>plain text</out></t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().Contain("plain text");
        result.Should().NotContain("<out>");
        result.Should().NotContain("SEPM0009");
    }
}
