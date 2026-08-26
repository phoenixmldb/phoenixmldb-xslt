using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Reported by Martin Honnen (2026-08-26): <c>match="document-node(element(x:report))"</c>
/// matched nothing, so XSpec's report stylesheet fell through to the built-in rule and died
/// with XTDE0555.
///
/// A NameTest inside a pattern step gets its namespace URI resolved to a NamespaceId by
/// ResolveNamespacesInPattern. That pass only visited <c>step.NodeTest is NameTest</c> — but a
/// NameTest is also NESTED inside a KindTest: <c>element(x:report)</c> keeps it in
/// <c>KindTest.Name</c>, <c>document-node(element(x:report))</c> in
/// <c>KindTest.DocumentElementTest</c>. Those kept NamespaceUri with ResolvedNamespace null,
/// and NameTest.Matches ends on "this shouldn't happen … return false".
///
/// So <c>x:report</c> matched and <c>element(x:report)</c> did not — the same name, one nesting
/// level apart. The negative cases matter as much as the positive ones: a fix that resolved
/// nothing and matched everything would pass the positives alone.
/// </summary>
public class PatternNestedNameTestNamespaceTests
{
    private const string Ns = "http://www.jenitennison.com/xslt/xspec";
    private const string Input = $"<report xmlns=\"{Ns}\"><a/></report>";

    private static async Task<bool> Matches(string pattern, string driver)
    {
        var xslt = $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:x="{Ns}" xmlns:other="urn:other">
              {driver}
              <xsl:template match="{pattern}"><HIT/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xslt);
        try { return (await t.TransformAsync(Input)).Contains("<HIT", StringComparison.Ordinal); }
        // A non-matching pattern falls through to the built-in rule, which under
        // on-no-match="fail" raises XTDE0555 — that IS the "did not match" signal here.
        // Note it arrives as InvalidOperationException, not XsltException, so it carries no
        // ErrorCode; worth tidying separately.
        catch (Exception ex) when (ex.Message.Contains("XTDE0555", StringComparison.Ordinal))
        { return false; }
    }

    private const string DocDriver = """<xsl:mode on-no-match="fail"/>""";
    private const string ElemDriver = """<xsl:template match="/"><xsl:apply-templates select="/*"/></xsl:template>""";

    // ---- document-node(element(E)) — the reported case ----

    [Theory]
    [InlineData("document-node(element(x:report))", true)]
    [InlineData("document-node(element(*))", true)]
    [InlineData("document-node(element(other:report))", false)]  // wrong namespace
    [InlineData("document-node(element(x:nope))", false)]        // wrong local name
    [InlineData("document-node(element(report))", false)]        // unprefixed = no namespace
    public async Task Document_node_element_test_honours_the_namespace(string pattern, bool expected)
        => (await Matches(pattern, DocDriver)).Should().Be(expected);

    // ---- element(E) — broken the same way, not reported ----

    [Theory]
    [InlineData("element(x:report)", true)]
    [InlineData("element(*)", true)]
    [InlineData("element(other:report)", false)]
    [InlineData("element(x:nope)", false)]
    public async Task Element_kind_test_honours_the_namespace(string pattern, bool expected)
        => (await Matches(pattern, ElemDriver)).Should().Be(expected);

    /// <summary>The un-nested form always worked; kept so the pair cannot drift apart again.</summary>
    [Theory]
    [InlineData("x:report", true)]
    [InlineData("*:report", true)]
    [InlineData("other:report", false)]
    public async Task Bare_name_test_still_honours_the_namespace(string pattern, bool expected)
        => (await Matches(pattern, ElemDriver)).Should().Be(expected);
}
