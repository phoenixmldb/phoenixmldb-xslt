using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression pins for two XSLT-engine-local fn/base-uri conformance failures
/// (W3C xslt30-test fn/base-uri, Cluster B):
///   base-uri-046 — temp-tree ROOT base-uri stamp for a parentless deep xsl:copy-of
///                  whose source element carries a RELATIVE xml:base attribute.
///   base-uri-013 — arbitrary-scheme absolute-URI detection: xml:base="d://tests/"
///                  must be recognised as absolute and returned as-is.
/// </summary>
public class BaseUriConformanceTests
{
    private static async System.Threading.Tasks.Task<string> RunAsync(
        string stylesheet, string input, string stylesheetUri, string sourceUri)
    {
        var transformer = new XsltTransformer();
        transformer.SetSourceDocumentUri(new System.Uri(sourceUri));
        await transformer.LoadStylesheetAsync(stylesheet, new System.Uri(stylesheetUri));
        return (await transformer.TransformAsync(input)).Trim();
    }

    // base-uri-046: deep xsl:copy-of of /doc/str1 (which keeps xml:base="/xml/") into a
    // parentless as="element()" variable. The stylesheet static base is http://www.example.org/
    // (from xml:base on the transform root). base-uri() of the parentless copy must resolve the
    // relative /xml/ against that static base → http://www.example.org/xml/.
    [Fact]
    public async Task BaseUri046_parentless_copyof_root_resolves_relative_xmlbase_against_static_base()
    {
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform"
                         xml:base="http://www.example.org/" version="2.0">
              <t:variable name="elemcopy" as="element()">
                <t:copy-of select="/doc/str1"/>
              </t:variable>
              <t:template match="/doc">
                <out><t:value-of select="base-uri($elemcopy)"/></out>
              </t:template>
            </t:transform>
            """;
        const string input = """
            <doc xml:base="http://www.xmlexample.eu/"><str1 xml:base="/xml/">string1<substring1 attr="attribute1">substring</substring1></str1></doc>
            """;
        var result = await RunAsync(ss, input,
            stylesheetUri: "file:///tmp/baseuri-test/base-uri-046.xsl",
            sourceUri: "file:///tmp/baseuri-test/baseuri044.xml");
        result.Should().Be("<out>http://www.example.org/xml/</out>");
    }

    // base-uri-013: temp-tree xsl:variable with xml:base="d://tests/". The single-letter scheme
    // d: is a genuine absolute URI (RFC 3986 scheme = [A-Za-z][A-Za-z0-9+.-]*:) and must be
    // returned as-is, NOT resolved against the physical module file URI (would give file:///d://tests/).
    [Fact]
    public async Task BaseUri013_single_letter_scheme_xmlbase_is_absolute()
    {
        const string ss = """
            <t:transform xmlns:t="http://www.w3.org/1999/XSL/Transform"
                         xml:base="http://www.baseuri.exmpl/tests/" version="2.0">
              <t:output method="xml" encoding="UTF-8"/>
              <t:variable name="temptree" xml:base="d://tests/">
                <a><b>Text1</b><c>helloText2</c></a>
              </t:variable>
              <t:template match="/">
                <out><t:value-of select="base-uri($temptree)"/></out>
              </t:template>
            </t:transform>
            """;
        const string input = "<doc/>";
        var result = await RunAsync(ss, input,
            stylesheetUri: "file:///tmp/baseuri-test/base-uri-013.xsl",
            sourceUri: "file:///tmp/baseuri-test/in.xml");
        result.Should().Contain("<out>d://tests/</out>");
        result.Should().NotContain("file:///");
    }
}
