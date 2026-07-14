using System;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Coverage for the "cluster D" href-less <c>xsl:result-document</c> serialization defects.
///
/// <para>result-document-0305: a named <c>xsl:output</c> carrying <c>item-separator="|"</c> is the
/// stylesheet's only output declaration. It is NOT the principal (unnamed) output, so its
/// item-separator must never seed the principal result sequence. The result-document overrides with
/// <c>item-separator="#absent"</c>, which resets the separator to the default single space. Expected
/// <c>&lt;!--begin--&gt;1 2&lt;!--end--&gt;</c>; the bug seeded the named output's <c>|</c> as the
/// principal separator, yielding <c>&lt;!--begin--&gt;|1|2|&lt;!--end--&gt;</c>.</para>
///
/// <para>result-document-0202: an href-less <c>xsl:result-document</c> with no <c>format</c> attribute
/// uses the unnamed (default) <c>xsl:output</c> declaration. Here that declaration has
/// <c>method="text"</c>, so the content must serialize as text (string value only). The bug ignored
/// the unnamed output and defaulted to <c>method="xml"</c>, emitting an XML declaration and element
/// markup.</para>
/// </summary>
public sealed class ResultDocumentClusterDTests
{
    private static async Task<string> Transform(string stylesheet, string input = "<doc/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet, new Uri("file:///sheet.xsl"));
        return await t.TransformAsync(input);
    }

    [Fact]
    public async Task NamedOutputItemSeparator_DoesNotSeedPrincipal_AbsentResetsToSpace()
    {
        // result-document-0305
        const string ss = """
            <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
               <xsl:output name="f" item-separator="|" omit-xml-declaration="yes"/>
               <xsl:template match="/">
                  <xsl:result-document build-tree="no" format="f" item-separator="#absent" method="xml">
                     <xsl:comment>begin</xsl:comment>
                     <xsl:sequence select="1 to count(//*)"/>
                     <xsl:comment>end</xsl:comment>
                  </xsl:result-document>
               </xsl:template>
            </xsl:transform>
            """;
        var result = await Transform(ss, "<doc><foo>text</foo></doc>");
        result.Should().Be("<!--begin-->1 2<!--end-->");
    }

    [Fact]
    public async Task HreflessNoFormat_UsesUnnamedTextOutput()
    {
        // result-document-0202
        const string ss = """
            <t:transform xmlns:my="http://example.com"
                         xmlns:t="http://www.w3.org/1999/XSL/Transform"
                         version="2.0">
               <t:output method="text" encoding="UTF-8"/>
               <t:output name="my:format2" method="xml" encoding="UTF-8"/>
               <t:template match="doc">
                  <t:result-document>
                     <out>text content only</out>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss, "<doc/>");
        result.Should().Be("text content only");
    }
}
