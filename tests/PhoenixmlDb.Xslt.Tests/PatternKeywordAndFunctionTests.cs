using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

#pragma warning disable CA1849 // Call async methods in an async method

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// End-to-end regression tests for pattern parsing/matching cases from the W3C
/// attr/match conformance set: union keyword, document-node()/root() axis names,
/// variable-reference patterns with predicates, parenthesized positional patterns,
/// XPath comments in patterns, and intersect patterns.
/// </summary>
public class PatternKeywordAndFunctionTests
{
    private const string Doc1059 = """
        <doc>
          <foo att1="c">
            <foo att1="b">
              <foo att1="a">
                <baz att1="wrong"/>
              </foo>
            </foo>
          </foo>
        </doc>
        """;

    private const string Doc1052 = """
        <div>
          <and/>
          <or/>
          <div/>
        </div>
        """;

    private static async Task<string> RunAsync(string stylesheet, string input)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(input);
    }

    private static async Task<string> RunTemplateAsync(string stylesheet, string templateName, string? ns = null)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        t.SetInitialTemplate(templateName, ns);
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task Match038_UnionKeyword()
    {
        // "/ union /*" is a union of (/) and (/*). Applied to the document node, only
        // the branch "/" could match it — but per leading-lone-slash disambiguation the
        // pattern is (/union) union (/*): "/union" (child::union of root) and "/*"
        // (element children of root). Neither matches the DOCUMENT node, so the
        // low-priority match="/" template wins → OK.
        var xsl = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <xsl:template match="/ union /*" priority="5"><out>WRONG!</out></xsl:template>
              <xsl:template match="/"><out>OK!</out></xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(xsl, Doc1052);
        r.Should().Contain("OK!").And.NotContain("WRONG");
    }

    [Fact]
    public async Task Match048_ChildDocumentNode()
    {
        // child::document-node() can never match (a document node is never a child),
        // so the document node falls to the built-in template, recurses to the top
        // element, which matches "*" → OK.
        var xsl = """
            <xsl:stylesheet version="2.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="child::document-node()"><d>WRONG</d></xsl:template>
              <xsl:template match="*"><out>OK</out></xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(xsl, Doc1059);
        r.Should().Contain("<out>OK</out>").And.NotContain("WRONG");
    }

    [Fact]
    public async Task Match074_VariableRefWithPredicate()
    {
        var xsl = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" version="3.0">
              <xsl:variable name="v" select="//foo"/>
              <xsl:template match="/"><out><xsl:apply-templates select="//*"/></out></xsl:template>
              <xsl:template match="$v[@att1='a']"><ok/></xsl:template>
              <xsl:template match="*"/>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(xsl, Doc1059);
        r.Should().Be("<out><ok/></out>");
    }

    [Fact]
    public async Task Match076_ParenthesizedPositional()
    {
        var xsl = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" version="3.0">
              <xsl:template match="/"><out><xsl:apply-templates select="//*"/></out></xsl:template>
              <xsl:template match="(doc/descendant::foo)[2]"><ok att="{@att1}"/></xsl:template>
              <xsl:template match="*"/>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(xsl, Doc1059);
        r.Should().Be("<out><ok att=\"b\"/></out>");
    }

    [Fact]
    public async Task Match215_XPathComments()
    {
        var xsl = """
            <xsl:transform xmlns:my="http://www.example.com/ns/match/id-idref-notation" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" exclude-result-prefixes="my xs" version="3.0">
              <xsl:variable name="data" as="element()">
                <a nr="1"><a nr="2"/></a>
              </xsl:variable>
              <xsl:template match="/"><out><xsl:apply-templates select="$data"/></out></xsl:template>
              <xsl:template match="(: child-or-top:: :)a"><one nr="{@nr}"><xsl:apply-templates/></one></xsl:template>
              <xsl:template match="(: child-or-top:: :)a"><two nr="{@nr}"><xsl:next-match/></two></xsl:template>
              <xsl:template match="( */(: child:: :) a)"><three nr="{@nr}"><xsl:next-match/></three></xsl:template>
              <xsl:template match="//a"><wrong reason="//a should only match a node in a tree rooted at a document node"/></xsl:template>
            </xsl:transform>
            """;
        var r = await RunAsync(xsl, Doc1059);
        r.Should().Be("<out><two nr=\"1\"><one nr=\"1\"><three nr=\"2\"><two nr=\"2\"><one nr=\"2\"/></two></three></one></two></out>");
    }

    [Fact]
    public async Task Match233_RootFunction()
    {
        // $data is a parentless element(A); root(A) is A itself; [self::A] is true → ok.
        var xsl = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:my="my" exclude-result-prefixes="xs my" version="3.0">
              <xsl:variable name="data" as="element(A)"><A><B>treasure</B></A></xsl:variable>
              <xsl:template match="root()[self::A]"><ok/></xsl:template>
              <xsl:template name="main"><xsl:apply-templates select="$data"/></xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunTemplateAsync(xsl, "main");
        r.Should().Contain("<ok/>").And.NotContain("treasure");
    }

    [Fact]
    public async Task Match278_IntersectPattern()
    {
        // The intersect never matches anything (the two child::div branches must share
        // a parent, which never happens in the nested structure) → shallow-copy only.
        var xsl = """
            <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs" expand-text="yes">
              <xsl:mode on-no-match="shallow-copy"/>
              <xsl:variable name="nodes"><div id="a"><div id="b"><div id="c">text</div></div></div></xsl:variable>
              <xsl:template name="xsl:initial-template"><x><xsl:apply-templates select="$nodes"/></x></xsl:template>
              <xsl:template match="div[@id='a']//* intersect div[@id='b']//*" priority="20"><wrong/></xsl:template>
            </xsl:transform>
            """;
        var r = await RunTemplateAsync(xsl, "initial-template", "http://www.w3.org/1999/XSL/Transform");
        r.Should().NotContain("wrong");
    }
}
