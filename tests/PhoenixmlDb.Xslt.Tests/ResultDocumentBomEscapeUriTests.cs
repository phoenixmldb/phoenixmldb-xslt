using System;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Coverage for the <c>byte-order-mark</c> and <c>escape-uri-attributes</c> serialization
/// attributes on an href-less <c>xsl:result-document</c> (claiming the principal output,
/// routed through the shared <c>FinalizeOutput</c> pipeline).
///
/// Both attributes were validated at parse time but never captured into the
/// <c>XsltResultDocument</c> AST, so they were silently dropped: a truthy
/// <c>byte-order-mark</c> produced no U+FEFF prefix, and <c>escape-uri-attributes="no"</c>
/// still percent-encoded non-ASCII characters in URI-valued HTML attributes.
///
/// Mirrors W3C conformance cases insn/result-document/result-document-0256 / -0258 / -0260 /
/// -1203 (byte-order-mark, static values and AVT) and -0264 / -0266 / -0268
/// (escape-uri-attributes="no").
/// </summary>
public sealed class ResultDocumentBomEscapeUriTests
{
    private static async Task<string> Transform(string stylesheet, string input = "<doc><foo>true</foo></doc>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet, new Uri("file:///sheet.xsl"));
        return await t.TransformAsync(input);
    }

    [Fact]
    public async Task ByteOrderMark_Yes_PrependsBom()
    {
        // -0256: byte-order-mark="     yes   " (whitespace-padded) → leading U+FEFF.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
               <t:template match="/">
                  <t:result-document method="xhtml" indent="no" encoding="UTF-8" byte-order-mark="     yes   ">
                     <html xmlns="http://www.w3.org/1999/xhtml"><head><title/></head><body>hello</body></html>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().StartWith("﻿");
        result.Should().Contain("<body>hello</body>");
    }

    [Fact]
    public async Task ByteOrderMark_True_PrependsBom()
    {
        // -0258: byte-order-mark="true".
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
               <t:template match="/">
                  <t:result-document method="xhtml" indent="no" encoding="UTF-8" byte-order-mark="true">
                     <html xmlns="http://www.w3.org/1999/xhtml"><head><title/></head><body>hello</body></html>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        (await Transform(ss)).Should().StartWith("﻿");
    }

    [Fact]
    public async Task ByteOrderMark_One_PrependsBom()
    {
        // -0260: byte-order-mark="1".
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
               <t:template match="/">
                  <t:result-document method="xhtml" indent="no" encoding="UTF-8" byte-order-mark="1">
                     <html xmlns="http://www.w3.org/1999/xhtml"><head><title/></head><body>hello</body></html>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        (await Transform(ss)).Should().StartWith("﻿");
    }

    [Fact]
    public async Task ByteOrderMark_Avt_PrependsBom()
    {
        // -1203: byte-order-mark="{doc/foo}" evaluating to " true " (whitespace-padded).
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
               <t:template match="/">
                  <t:result-document method="xhtml" indent="no" encoding="UTF-8" byte-order-mark="{doc/foo}">
                     <html xmlns="http://www.w3.org/1999/xhtml"><head><title/></head><body>hello</body></html>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        (await Transform(ss, "<doc><foo> true </foo></doc>")).Should().StartWith("﻿");
    }

    [Fact]
    public async Task ByteOrderMark_No_DoesNotPrependBom()
    {
        // Guard: byte-order-mark="no" must not emit a BOM.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
               <t:template match="/">
                  <t:result-document method="xhtml" indent="no" encoding="UTF-8" byte-order-mark="no">
                     <html xmlns="http://www.w3.org/1999/xhtml"><head><title/></head><body>hello</body></html>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        (await Transform(ss)).Should().NotStartWith("﻿");
    }

    [Fact]
    public async Task EscapeUriAttributes_No_LeavesNonAsciiHrefUnescaped()
    {
        // -0264: method="html" escape-uri-attributes="no" → the non-ASCII href is left
        // untouched (no percent-encoding).
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
               <t:template match="/">
                  <t:result-document method="html" escape-uri-attributes="no" encoding="UTF-8" indent="no">
                     <html><body><div>This is <a href="http://iri.example.org/&#xFB4F;/&#xE5;rsrapport/&#xE5;r/2005?x=y">not escaped</a></div></body></html>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().Contain("href=\"http://iri.example.org/ﭏ/årsrapport/år/2005?x=y\"");
        result.Should().NotContain("%EF%AD%8F");
    }

    [Fact]
    public async Task EscapeUriAttributes_DefaultYes_PercentEncodesNonAsciiHref()
    {
        // Guard: without escape-uri-attributes="no", HTML output percent-encodes the non-ASCII
        // href (the default is "yes").
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
               <t:template match="/">
                  <t:result-document method="html" encoding="UTF-8" indent="no">
                     <html><body><div>This is <a href="http://iri.example.org/&#xFB4F;/&#xE5;rsrapport/&#xE5;r/2005?x=y">not escaped</a></div></body></html>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        (await Transform(ss)).Should().Contain("%EF%AD%8F");
    }
}
