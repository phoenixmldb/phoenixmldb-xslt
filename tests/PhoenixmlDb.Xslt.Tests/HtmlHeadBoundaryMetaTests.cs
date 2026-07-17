using System;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The html output method (resolved by default from a lowercase &lt;html&gt; result document
/// element when no <c>xsl:output method</c> is declared, commit fffd4be) injects a Content-Type
/// <c>&lt;meta&gt;</c> as the first child of <c>&lt;head&gt;</c> and serializes it as an HTML void
/// element. This test pins that the injected meta is a genuine VOID element and that the head/body
/// boundary stays well-formed: <c>&lt;/head&gt;</c> is emitted before <c>&lt;body&gt;</c>, the meta
/// precedes the pre-existing <c>&lt;title&gt;</c>, and <c>&lt;body&gt;</c> is a sibling of
/// <c>&lt;head&gt;</c> (never nested inside the void meta).
///
/// Regression anchor: attr/select select-6201 (a no-<c>xsl:output</c> lowercase-html stylesheet)
/// asserts on the re-parsed result tree <c>/html/body/table/tbody/tr[…]</c>. The serialization
/// itself is correct HTML (this test proves it); the select-6201 fix was in the conformance harness
/// re-parse (self-closing HTML void elements before the XML re-parse, HTML-parser semantics), not
/// here.
/// </summary>
public class HtmlHeadBoundaryMetaTests
{
    // select-6201's shape: a lowercase-html document with a title-only head, no xsl:output.
    private const string Sheet = """
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="2.0">
          <xsl:template match="/">
            <html>
              <head>
                <title>Customers</title>
              </head>
              <body>
                <table><tbody>
                  <xsl:for-each select="customers/customer">
                    <tr>
                      <th><xsl:apply-templates select="name"/></th>
                      <xsl:for-each select="order"><td><xsl:apply-templates/></td></xsl:for-each>
                    </tr>
                  </xsl:for-each>
                </tbody></table>
              </body>
            </html>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private const string Source =
        "<customers><customer><name>William</name><order>pc10</order><order>pc20</order></customer>" +
        "<customer><name>Harry</name><order>pc21</order><order>pc22</order></customer></customers>";

    [Fact]
    public async Task HtmlMethod_InjectedMeta_IsVoid_AndHeadBodyBoundaryWellFormed()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Sheet, new Uri(Path.GetTempPath() + "/"));
        var result = await t.TransformAsync(Source);

        // The injected Content-Type meta is a VOID element: <meta …> with no self-closing slash.
        result.Should().Contain("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=UTF-8\">");
        result.Should().NotContain("charset=UTF-8\" />"); // not XHTML self-closed under html method

        var metaIdx = result.IndexOf("<meta", StringComparison.Ordinal);
        var titleIdx = result.IndexOf("<title>", StringComparison.Ordinal);
        var headCloseIdx = result.IndexOf("</head>", StringComparison.Ordinal);
        var bodyOpenIdx = result.IndexOf("<body>", StringComparison.Ordinal);

        // meta precedes the pre-existing title inside head.
        metaIdx.Should().BeGreaterThan(0);
        metaIdx.Should().BeLessThan(titleIdx);
        // </head> is present and closes BEFORE <body> opens: body is a sibling, not nested in head.
        headCloseIdx.Should().BeGreaterThan(titleIdx);
        bodyOpenIdx.Should().BeGreaterThan(headCloseIdx);
    }
}
