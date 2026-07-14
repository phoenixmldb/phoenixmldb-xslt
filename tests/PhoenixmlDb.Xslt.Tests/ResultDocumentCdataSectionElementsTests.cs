using System;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Coverage for the <c>cdata-section-elements</c> serialization attribute on
/// <c>xsl:result-document</c>. The attribute was validated but never captured into the
/// <c>XsltResultDocument</c> AST, so the named elements' text was normally escaped instead of
/// wrapped in <c>&lt;![CDATA[…]]&gt;</c>.
///
/// Mirrors W3C conformance cases insn/result-document/result-document-0217 (whitespace-separated
/// QName list including a prefixed name), -0240 (union with a matched xsl:output declaration) and
/// -0401 (AVT-valued cdata-section-elements resolved against the source document).
/// </summary>
public sealed class ResultDocumentCdataSectionElementsTests
{
    private static async Task<string> Transform(string stylesheet, string input = "<doc/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet, new Uri("file:///sheet.xsl"));
        return await t.TransformAsync(input);
    }

    [Fact]
    public async Task Hrefless_SingleElement_WrapsTextInCdata()
    {
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
               <t:template match="/">
                  <t:result-document method="xml" cdata-section-elements="item1">
                     <out><item1>a &amp; b</item1><item2>a &amp; b</item2></out>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().Contain("<item1><![CDATA[a & b]]></item1>");
        result.Should().Contain("<item2>a &amp; b</item2>");
    }

    [Fact]
    public async Task Hrefless_QNameList_WithPrefixAndDefaultNamespace()
    {
        // Mirrors result-document-0217: "item1 my:item3 item5". Only the no-namespace item1/item5
        // and the mytest-namespace item3 are wrapped; the no-namespace item3 is not.
        const string ss = """
            <t:transform xmlns:my="http://www.mytest.example.org"
                         xmlns:t="http://www.w3.org/1999/XSL/Transform"
                         exclude-result-prefixes="my" version="3.0">
               <t:template match="/">
                  <t:result-document method="xml" cdata-section-elements="item1 my:item3 item5">
                     <out>
                        <item1>a &amp; b</item1>
                        <item2>a &amp; b</item2>
                        <item3>a &amp; b</item3>
                        <item3 xmlns="http://www.mytest.example.org">a &amp; b</item3>
                        <item4>a &amp; b</item4>
                        <item5>a &amp; b</item5>
                     </out>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().Contain("<item1><![CDATA[a & b]]></item1>");
        result.Should().Contain("<item2>a &amp; b</item2>");
        result.Should().Contain("<item3>a &amp; b</item3>");
        result.Should().MatchRegex(@"<item3 xmlns=[""']http://www\.mytest\.example\.org[""']><!\[CDATA\[a & b\]\]></item3>");
        result.Should().Contain("<item4>a &amp; b</item4>");
        result.Should().Contain("<item5><![CDATA[a & b]]></item5>");
    }

    [Fact]
    public async Task Secondary_UnionsWithMatchedOutputDeclaration()
    {
        // Mirrors result-document-0240: union of {item2,item3} (result-document) and
        // {item2,item5,item7} (xsl:output) → item2,item3,item5,item7 wrapped.
        const string ss = """
            <t:transform xmlns:my="http://example1.com"
                         xmlns:t="http://www.w3.org/1999/XSL/Transform"
                         version="3.0" exclude-result-prefixes="my">
               <t:output name="my:temp-output" method="xml" indent="no"
                         cdata-section-elements="item2 item5 item7"/>
               <t:template match="/">
                  <t:result-document format="my:temp-output" href="out.xml" cdata-section-elements="item2 item3">
                     <out>
                        <item1>x</item1>
                        <item2>x</item2>
                        <item3>x</item3>
                        <item4>x</item4>
                        <item5>x</item5>
                        <item6>x</item6>
                        <item7>x</item7>
                     </out>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss, new Uri("file:///sheet.xsl"));
        await t.TransformAsync("<doc/>");
        string? secondary = null;
        foreach (var kv in t.SecondaryResultDocuments)
            if (kv.Key.EndsWith("out.xml", StringComparison.Ordinal))
                secondary = kv.Value;
        secondary.Should().NotBeNull();
        secondary.Should().Contain("<item1>x</item1>");
        secondary.Should().Contain("<item2><![CDATA[x]]></item2>");
        secondary.Should().Contain("<item3><![CDATA[x]]></item3>");
        secondary.Should().Contain("<item4>x</item4>");
        secondary.Should().Contain("<item5><![CDATA[x]]></item5>");
        secondary.Should().Contain("<item6>x</item6>");
        secondary.Should().Contain("<item7><![CDATA[x]]></item7>");
    }

    [Fact]
    public async Task Hrefless_AvtValue_ResolvedAgainstSource()
    {
        // Mirrors result-document-0401: cdata-section-elements="{foo[1]} my:{elem} {item}".
        const string ss = """
            <t:transform xmlns:my="http://www.mytest.example.org"
                         xmlns:t="http://www.w3.org/1999/XSL/Transform"
                         exclude-result-prefixes="my" version="3.0">
               <t:template match="/doc">
                  <t:result-document method="xml" cdata-section-elements="{foo[1]} my:{elem} {item}">
                     <out>
                        <item1>a &amp; b</item1>
                        <item2>a &amp; b</item2>
                        <item3>a &amp; b</item3>
                        <item3 xmlns="http://www.mytest.example.org">a &amp; b</item3>
                        <item4>a &amp; b</item4>
                        <item5 xmlns="http://www.mytest.example.org">a &amp; b</item5>
                     </out>
                  </t:result-document>
               </t:template>
            </t:transform>
            """;
        const string input = "<doc><foo>item1</foo><foo>item</foo><elem>item3</elem><item>my:item5</item></doc>";
        var result = await Transform(ss, input);
        result.Should().Contain("<item1><![CDATA[a & b]]></item1>");
        result.Should().Contain("<item2>a &amp; b</item2>");
        result.Should().MatchRegex(@"<item3 xmlns=[""'][^""']*[""']><!\[CDATA\[a & b\]\]></item3>");
        result.Should().Contain("<item4>a &amp; b</item4>");
        result.Should().MatchRegex(@"<item5 xmlns=[""'][^""']*[""']><!\[CDATA\[a & b\]\]></item5>");
    }
}
