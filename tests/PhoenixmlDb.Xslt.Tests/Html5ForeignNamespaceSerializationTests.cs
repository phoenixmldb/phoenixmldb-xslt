using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression coverage for the decl/output HTML5-method foreign-namespace serialization sub-slice
/// (W3C output-0602a/0602b/0603a). Under the <c>html</c> output method with <c>html-version="5.0"</c>,
/// the serializer performs the same "prefix normalization" as the XHTML method (MHK decl/output
/// ruling 2019-04-11): elements in the XHTML/SVG/MathML namespaces bound via a prefix are re-expressed
/// with a default <c>xmlns</c> declaration, namespace declarations for those three namespaces are
/// removed wherever they appear (including on ancestor elements such as <c>body</c>), and a prefixed
/// attribute in one of those namespaces keeps its prefix with its <c>xmlns:prefix</c> declaration
/// re-established on the owning element. Foreign (non-HTML5) namespaces keep their prefixes.
/// </summary>
public class Html5ForeignNamespaceSerializationTests
{
    private const string Html5Copy = """
        <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
          <t:output method="html" version="5.0" html-version="5.0" escape-uri-attributes="yes" encoding="UTF-8" indent="yes"/>
          <t:strip-space elements="*"/>
          <t:template match="/"><t:copy-of select="*"/></t:template>
        </t:transform>
        """;

    // Mirrors decl/output environment output-06a: an XML/HTML tree with prefixed elements in SVG,
    // MathML and a foreign namespace.
    private const string Input06a = """
        <html xmlns:n="NamespaceN">
          <head><title>OUTPUT-METHOD</title></head>
          <body xmlns:svg="http://www.w3.org/2000/svg">
            <p>This stylesheet generates<br/>some output</p>
            <n:zzz>Foreign namespace</n:zzz>
            <svg:svg width="100" height="100">
              <rect xmlns="http://www.w3.org/2000/svg" fill="yellow" width="100" height="100"></rect>
              <svg:circle fill="red" cx="30" cy="30" r="20"></svg:circle>
            </svg:svg>
            <mathML:math xmlns:mathML="http://www.w3.org/1998/Math/MathML">
              <mrow xmlns="http://www.w3.org/1998/Math/MathML"><mi>a</mi><mo>+</mo><mi>b</mi></mrow>
            </mathML:math>
          </body>
        </html>
        """;

    // Mirrors decl/output environment output-06b: prefixed attributes in SVG, MathML and a foreign
    // namespace.
    private const string Input06b = """
        <html xmlns:n="NamespaceN" xmlns:m="NamespaceM">
          <head><title>OUTPUT-METHOD</title></head>
          <body xmlns:svg="http://www.w3.org/2000/svg">
            <p>text<br/>output</p>
            <n:zzz>Foreign namespace</n:zzz>
            <p m:zzz="value">Foreign attribute</p>
            <mathML:math xmlns:mathML="http://www.w3.org/1998/Math/MathML">
              <mrow xmlns="http://www.w3.org/1998/Math/MathML">
                <mi svg:att="34" z="5" mathML:att="123" svg:atZZZ="99">a</mi>
              </mrow>
            </mathML:math>
          </body>
        </html>
        """;

    private static async Task<string> Transform(string stylesheet, string input)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(input);
    }

    [Fact]
    public async Task Output0602a_Svg_UnprefixedWithDefaultXmlns_NoSvgDeclOnBody()
    {
        // output-0602a: the svg namespace declaration must not survive on the body element (neither
        // as a default nor as xmlns:svg), and the svg element itself carries the default xmlns.
        var result = await Transform(Html5Copy, Input06a);
        Regex.IsMatch(result, "<body\\s+xmlns=[\"']http://www.w3.org/2000/svg[\"']").Should().BeFalse(result);
        Regex.IsMatch(result, "xmlns:svg=[\"']http://www.w3.org/2000/svg[\"']").Should().BeFalse(result);
        Regex.IsMatch(result, "<svg[^<>]+xmlns=[\"']http://www.w3.org/2000/svg[\"']").Should().BeTrue(result);
    }

    [Fact]
    public async Task Output0602b_MathMl_UnprefixedWithDefaultXmlns_NoMathMlPrefixDecl()
    {
        // output-0602b: the MathML element serializes with a default xmlns and no xmlns:mathML.
        var result = await Transform(Html5Copy, Input06a);
        Regex.IsMatch(result, "xmlns:mathML=[\"']http://www.w3.org/1998/Math/MathML[\"']").Should().BeFalse(result);
        Regex.IsMatch(result, "<math\\s+xmlns=[\"']http://www.w3.org/1998/Math/MathML[\"']").Should().BeTrue(result);
    }

    [Fact]
    public async Task Output0603a_SvgPrefixedAttribute_KeepsPrefixWithReDeclaredXmlns()
    {
        // output-0603a: a prefixed svg attribute keeps its prefix and its xmlns:svg declaration is
        // re-established on the owning element (the ancestor declaration having been normalized away).
        var result = await Transform(Html5Copy, Input06b);
        Regex.IsMatch(result, "mi[^<>]+xmlns:svg=[\"']http://www.w3.org/2000/svg[\"']").Should().BeTrue(result);
        result.Should().Contain("svg:att=");
    }

    [Fact]
    public async Task Html5_PlainHtmlWithoutForeignNamespaces_Unaffected()
    {
        // Guard: ordinary HTML output with no foreign/prefixed namespaces is untouched by prefix
        // normalization — no spurious xmlns declarations appear.
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <t:output method="html" version="5.0" html-version="5.0" indent="no"/>
              <t:template match="/"><html><head><title>t</title></head><body><p class="c">hello</p></body></html></t:template>
            </t:transform>
            """;
        var result = await Transform(ss, "<in/>");
        result.Should().NotContain("xmlns");
        result.Should().Contain("<!DOCTYPE html>");
        result.Should().Contain("<p class=\"c\">hello</p>");
        result.Should().Contain("<title>t</title>");
    }
}
