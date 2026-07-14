using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>cdata-section-elements</c> is a list of QNames resolved to expanded names using the
/// in-scope namespaces on <c>xsl:output</c> (default namespace applies to unprefixed names,
/// XSLT 2.0+). A literal result element whose expanded name matches a list entry — regardless
/// of the prefix actually used — must have its text content wrapped in a CDATA section.
/// Covers W3C XSLT decl/output output-0138: two prefixes (<c>my</c>, <c>one</c>) bound to the
/// same namespace both match the single <c>my:h3</c> list entry.
/// </summary>
public class CdataSectionElementNamespaceTests
{
    private static async System.Threading.Tasks.Task<string> Transform(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync("<in/>");
    }

    // cdata-section-elements="h1 my:h3 h5": h1/h5 resolve via the default (xhtml) namespace;
    // my:h3 resolves to {http://ns.example.com}h3. A second prefix `one` bound to the SAME
    // namespace URI must match the my:h3 entry by expanded name, and an unprefixed h3 in the
    // default namespace must NOT match.
    [Fact]
    public async System.Threading.Tasks.Task PrefixIndependentExpandedNameMatch_WrapsCdata()
    {
        var xsl = """
            <t:transform xmlns:my="http://ns.example.com"
                         xmlns:one="http://ns.example.com"
                         xmlns="http://www.w3.org/1999/xhtml"
                         xmlns:t="http://www.w3.org/1999/XSL/Transform" version="2.0">
              <t:output method="xhtml" cdata-section-elements="h1 my:h3 h5" indent="no" encoding="UTF-8"/>
              <t:template match="/">
                <html><body>
                  <h1>a &amp; b</h1>
                  <h2>a &amp; b</h2>
                  <h3>a &amp; b</h3>
                  <one:h3>a &amp; b</one:h3>
                  <my:h3>a &amp; b</my:h3>
                  <h3 xmlns="http://www.mytest.example.org">a &amp; b</h3>
                  <h4>a &amp; b</h4>
                  <h5>a &amp; b</h5>
                </body></html>
              </t:template>
            </t:transform>
            """;
        var result = await Transform(xsl);

        // h1/h5 (default xhtml ns) wrapped
        result.Should().MatchRegex(@"<h1><!\[CDATA\[a & b\]\]></h1>");
        result.Should().MatchRegex(@"<h5><!\[CDATA\[a & b\]\]></h5>");
        // one:h3 and my:h3 both bind to http://ns.example.com → match my:h3 entry by
        // expanded name regardless of the prefix actually used → content wrapped in CDATA
        result.Should().MatchRegex(@"<one:h3[^>]*><!\[CDATA\[a & b\]\]></one:h3>");
        result.Should().MatchRegex(@"<my:h3[^>]*><!\[CDATA\[a & b\]\]></my:h3>");
        // h2/h4 not listed → escaped, no CDATA
        result.Should().Contain("<h2>a &amp; b</h2>");
        result.Should().Contain("<h4>a &amp; b</h4>");
        // unprefixed h3 in xhtml default ns is NOT {http://ns.example.com}h3 → escaped
        result.Should().Contain("<h3>a &amp; b</h3>");
        // h3 in a different namespace → escaped
        result.Should().Contain("""<h3 xmlns="http://www.mytest.example.org">a &amp; b</h3>""");
    }
}
