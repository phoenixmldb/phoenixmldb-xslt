using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression coverage for the decl/output XHTML default-namespace (prefix-stripping) sub-slice:
/// under the <c>xhtml</c> output method, elements in the HTML5 content namespaces (XHTML, SVG,
/// MathML) that are bound via a prefix in the result tree are serialized in the conventional
/// default-namespace form — the element name is its local name and the namespace becomes a default
/// <c>xmlns</c> declaration — and the HTML5 DOCTYPE uses the local name. Mirrors the W3C
/// output-0211/0221/0225/0226 conformance cases. Foreign-namespace elements keep their prefixes
/// (XML rules), and the <c>xml</c> output method is unaffected.
/// </summary>
public class XhtmlPrefixRedefaultTests
{
    private static async Task<string> Transform(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync("<in/>");
    }

    [Fact]
    public async Task Xhtml_PrefixedHtml_Redefaulted_WithLocalDoctypeName()
    {
        // output-0211: prefixed <h:html> under the xhtml method must serialize with the XHTML
        // namespace as the default declaration and the DOCTYPE local name.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xhtml" html-version="5.0"/>
              <t:template match="/"><h:html xmlns:h="http://www.w3.org/1999/xhtml"><h:head><h:title>test</h:title></h:head><h:body><h:p>content</h:p></h:body></h:html></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        Regex.IsMatch(result, "<!DOCTYPE html>\\s*<html").Should().BeTrue(result);
        Regex.IsMatch(result, "xmlns=[\"']http://www.w3.org/1999/xhtml[\"']").Should().BeTrue(result);
        result.Should().NotContain("xmlns:h");
        result.Should().NotContain("h:");
    }

    [Fact]
    public async Task Xhtml_PrefixedEmptyElementsWithAttributes_Redefaulted()
    {
        // output-0221: prefixed empty XHTML elements with attributes serialize as local-named
        // elements with the XHTML namespace defaulted, expanded to start/end tag pairs.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xhtml" html-version="5.0"/>
              <t:template match="/"><h:html xmlns:h="http://www.w3.org/1999/xhtml"><h:head><h:title class="c"/></h:head><h:body><h:p class="c"/><h:i class="c"/><h:u class="c"/><h:div class="c"/><h:code class="c"/><h:strong class="c"/></h:body></h:html></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        foreach (var tag in new[] { "title", "p", "i", "u", "div", "code", "strong" })
            Regex.IsMatch(result, $"<{tag}\\s+class=\"c\"></{tag}>").Should().BeTrue($"{tag}: {result}");
        result.Should().NotContain("h:");
    }

    [Fact]
    public async Task Xhtml_WhitespacePaddedMethod_IsNormalized_AndRedefaulted()
    {
        // output-0221 uses method=" xhtml " and html-version=" 5.0 ": the padded token must be
        // whitespace-stripped so the xhtml method (and its prefix re-defaulting) actually applies.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method=" xhtml " html-version=" 5.0 "/>
              <t:template match="/"><h:html xmlns:h="http://www.w3.org/1999/xhtml"><h:head><h:title class="c"/></h:head></h:html></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        Regex.IsMatch(result, "<!DOCTYPE html>\\s*<html").Should().BeTrue(result);
        Regex.IsMatch(result, "<title\\s+class=\"c\"></title>").Should().BeTrue(result);
        result.Should().NotContain("h:");
    }

    [Fact]
    public async Task Xhtml_PrefixedSvgAndMathml_Redefaulted()
    {
        // output-0225: SVG and MathML elements bound via prefixes must each be re-defaulted to
        // their own namespace at the subtree boundary.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xhtml" html-version="5.0"/>
              <t:template match="/"><h:html xmlns:h="http://www.w3.org/1999/xhtml"><h:body><h:div><s:svg xmlns:s="http://www.w3.org/2000/svg" version="1.1"><s:circle r="40"/></s:svg></h:div><h:div><m:math display="block" xmlns:m="http://www.w3.org/1998/Math/MathML"><m:mi>cos</m:mi></m:math></h:div></h:body></h:html></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        Regex.IsMatch(result, "<html\\s+xmlns=['\"]http://www.w3.org/1999/xhtml['\"]\\s*>").Should().BeTrue(result);
        Regex.IsMatch(result, "<svg\\s+(version=['\"]1.1['\"]\\s+)?xmlns=['\"]http://www.w3.org/2000/svg['\"]").Should().BeTrue(result);
        Regex.IsMatch(result, "<math\\s+(display=['\"]block['\"]\\s+)?xmlns=['\"]http://www.w3.org/1998/Math/MathML['\"]").Should().BeTrue(result);
        result.Should().NotContain("h:");
        result.Should().NotContain("s:");
        result.Should().NotContain("m:");
    }

    [Fact]
    public async Task Xhtml_MixedDefaultAndPrefixedSvg_Redefaulted()
    {
        // output-0226: the html element already uses the default namespace; a prefixed s:svg
        // subtree must be re-defaulted to the SVG namespace.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xhtml" html-version="5.0"/>
              <t:template match="/"><html xmlns="http://www.w3.org/1999/xhtml"><body><div><s:svg xmlns:s="http://www.w3.org/2000/svg" version="1.1"><s:circle r="40"/></s:svg></div></body></html></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        Regex.IsMatch(result, "<html\\s+xmlns=['\"]http://www.w3.org/1999/xhtml['\"]\\s*>").Should().BeTrue(result);
        Regex.IsMatch(result, "<svg\\s+(version=['\"]1.1['\"]\\s+)?xmlns=['\"]http://www.w3.org/2000/svg['\"]").Should().BeTrue(result);
        result.Should().NotContain("s:");
    }

    [Fact]
    public async Task Xhtml_AlreadyDefaultNamespace_SerializesUnchanged()
    {
        // Guard (a): an XHTML source already using the default namespace is untouched — no
        // duplicated or altered xmlns declaration.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xhtml" html-version="5.0"/>
              <t:template match="/"><html xmlns="http://www.w3.org/1999/xhtml"><head><title>test</title></head><body><p>content</p></body></html></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        Regex.IsMatch(result, "<!DOCTYPE html>\\s*<html\\s+xmlns=\"http://www.w3.org/1999/xhtml\">").Should().BeTrue(result);
        // Exactly one xmlns declaration on the html element (no duplication).
        Regex.Count(result, "xmlns=\"http://www.w3.org/1999/xhtml\"").Should().Be(1, result);
        result.Should().Contain("<title>test</title>");
    }

    [Fact]
    public async Task Xhtml_ForeignNamespaceElement_KeepsPrefix()
    {
        // Guard (b): an element in a non-HTML5 (foreign) namespace keeps its prefix and
        // xmlns:prefix declaration — foreign content follows XML rules, not default-namespace
        // stripping.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xhtml" html-version="5.0"/>
              <t:template match="/"><html xmlns="http://www.w3.org/1999/xhtml"><body><ex:foo xmlns:ex="http://example.com/ns"><ex:bar/></ex:foo></body></html></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().Contain("xmlns:ex=\"http://example.com/ns\"");
        result.Should().Contain("<ex:foo");
        result.Should().Contain("ex:bar");
    }

    [Fact]
    public async Task XmlMethod_PrefixedXhtml_PreservesPrefix()
    {
        // Guard (c): the xml output method is entirely unaffected — prefixes are preserved.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="xml" indent="no"/>
              <t:template match="/"><h:html xmlns:h="http://www.w3.org/1999/xhtml"><h:head><h:title>test</h:title></h:head></h:html></t:template>
            </t:transform>
            """;
        var result = await Transform(ss);
        result.Should().Contain("<h:html");
        result.Should().Contain("xmlns:h=\"http://www.w3.org/1999/xhtml\"");
        result.Should().Contain("<h:title>test</h:title>");
    }
}
