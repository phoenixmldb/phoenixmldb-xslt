using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Reported by Martin Honnen (2026-08-27) via sxq, an XQuery implementation of Schematron:
/// nodes handed back from <c>fn:transform</c> with <c>delivery-format='raw'</c> arrived
/// stripped of their ancestors. <c>fn:path()</c> returned <c>/Q{}book[1]</c> for BOTH results
/// where Saxon gives <c>/Q{}books[1]/Q{}book[1]</c> and <c>[2]</c>.
///
/// The transport wrapper carried only the node's OWN serialization, and re-anchoring reparsed
/// that into a fresh document — so a &lt;book&gt; became the root of a one-element tree by
/// construction. Ancestors cannot survive a round-trip that never carried them. It now carries
/// the serialized ROOT plus a child-index path down to the node.
///
/// Martin's report was against xsl:evaluate, but that was innocent: the same failure reproduces
/// with a stylesheet function that merely selects nodes, which is what these tests use.
///
/// No conformance suite covers this. QT3 and the XSLT suite each exercise ONE engine; nothing
/// in either has XQuery call XSLT and navigate what comes back.
/// </summary>
public class CrossStoreNodeAncestorTests
{
    private const string SelectBooks = """
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
            xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:mf="http://example.com/mf"
            exclude-result-prefixes="#all">
          <xsl:function name="mf:pick" as="item()*" visibility="public">
            <xsl:param name="ctx" as="item()"/>
            <xsl:sequence select="$ctx//book"/>
          </xsl:function>
        </xsl:stylesheet>
        """;

    private static async Task<string> RawTransform(string returnExpr)
    {
        PhoenixmlDb.XQuery.Functions.TransformFunction.Provider ??= new PhoenixmlDb.Xslt.XsltTransformProvider();
        var xq = $$"""
            let $doc := parse-xml('<books><book id="b1">one</book><book id="b2">two</book></books>')
            let $r := transform(map {
                'stylesheet-text'  : {{Quote(SelectBooks)}},
                'initial-function' : QName('http://example.com/mf', 'pick'),
                'function-params'  : [$doc],
                'delivery-format'  : 'raw'
              })?output
            return {{returnExpr}}
            """;
        return await new PhoenixmlDb.XQuery.XQueryFacade().EvaluateAsync(xq);
    }

    private static string Quote(string s) => "'" + s.Replace("'", "''", System.StringComparison.Ordinal) + "'";

    /// <summary>The reported symptom: both results claimed to be the root of their own tree.</summary>
    [Fact]
    public async Task Returned_nodes_keep_their_ancestor_path()
        => (await RawTransform("string-join($r/path(), '|')"))
            .Should().Be("/Q{}books[1]/Q{}book[1]|/Q{}books[1]/Q{}book[2]");

    /// <summary>
    /// Not in Martin's report, and broken in a second way: re-anchoring per item would put the
    /// two siblings into two separate copies of their document, so they would not share a
    /// parent. Each distinct root is now parsed once for the whole result.
    /// </summary>
    [Fact]
    public async Task Sibling_results_share_one_tree()
        => (await RawTransform("string($r[1]/.. is $r[2]/..)")).Should().Be("true");

    [Theory]
    [InlineData("name($r[1]/..)", "books")]
    [InlineData("count($r[1]/../book)", "2")]
    [InlineData("count($r[1]/ancestor::*)", "1")]
    [InlineData("string($r[2]/@id)", "b2")]
    [InlineData("string($r[1])", "one")]
    [InlineData("count($r)", "2")]
    public async Task Navigation_from_returned_nodes_works(string expr, string expected)
        => (await RawTransform(expr)).Should().Be(expected);

    /// <summary>
    /// A node that IS its tree's root has no path to record — the pre-existing case, kept so
    /// the empty-path branch does not rot.
    /// </summary>
    [Fact]
    public async Task Document_root_result_is_unaffected()
    {
        PhoenixmlDb.XQuery.Functions.TransformFunction.Provider ??= new PhoenixmlDb.Xslt.XsltTransformProvider();
        var sheet = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                xmlns:mf="http://example.com/mf" exclude-result-prefixes="#all">
              <xsl:function name="mf:whole" as="item()*" visibility="public">
                <xsl:param name="ctx" as="item()"/>
                <xsl:sequence select="$ctx"/>
              </xsl:function>
            </xsl:stylesheet>
            """;
        var xq = $$"""
            let $doc := parse-xml('<books><book id="b1"/></books>')
            let $r := transform(map {
                'stylesheet-text'  : {{Quote(sheet)}},
                'initial-function' : QName('http://example.com/mf', 'whole'),
                'function-params'  : [$doc],
                'delivery-format'  : 'raw'
              })?output
            return name($r/*)
            """;
        (await new PhoenixmlDb.XQuery.XQueryFacade().EvaluateAsync(xq)).Should().Be("books");
    }
}
