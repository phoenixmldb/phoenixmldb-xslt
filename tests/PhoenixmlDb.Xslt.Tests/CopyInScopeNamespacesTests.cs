using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §11.9.1: a copied element carries a namespace node for EVERY namespace node of the
/// original — its complete IN-SCOPE set, not merely the xmlns declarations physically written
/// on it.
///
/// Emitting only the local declarations is usually indistinguishable, because the copy lands in
/// a result tree whose ancestors re-supply the rest. It is not indistinguishable when the copy
/// is detached or sits under <c>inherit-namespaces="no"</c>: nothing re-supplies them, the copy
/// keeps only the prefix of its own name, and a later <c>resolve-QName</c> against it fails with
/// <c>FONS0004</c> for a prefix the source plainly had.
///
/// XSpec's compiler is exactly this shape — <c>x:combine</c> wraps the combined document in
/// <c>&lt;xsl:element inherit-namespaces="no"&gt;</c>, then copies scenarios out of it and
/// resolves <c>@function</c> / <c>@template</c> / <c>@as</c> against the copies. This was
/// <c>FONS0004</c> in 60 of 162 XSpec suites, each failing before it could run a single test.
/// </summary>
public sealed class CopyInScopeNamespacesTests
{
    private const string Source = """
        <desc xmlns:mirror="x-urn:test:mirror" xmlns:x="urn:x">
          <scenario><call as="mirror:thing"/></scenario>
        </desc>
        """;

    private static async Task<string> RunAsync(string stylesheet)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(Source);
    }

    [Fact]
    public async Task CopyUnderInheritNamespacesNo_KeepsTheSourcesInScopeNamespaces()
    {
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="combined" as="document-node()">
                  <xsl:document>
                    <xsl:element name="desc" namespace="" inherit-namespaces="no">
                      <xsl:sequence select="/desc/node()"/>
                    </xsl:element>
                  </xsl:document>
                </xsl:variable>
                <xsl:for-each select="($combined//scenario)[1]">
                  <xsl:variable name="copied" as="element(call)?">
                    <xsl:copy select="call"><xsl:sequence select="call/attribute()"/></xsl:copy>
                  </xsl:variable>
                  <xsl:value-of select="string-join(sort(in-scope-prefixes($copied)), ',')"/>
                </xsl:for-each>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        r.Trim().Should().Be("mirror,x,xml",
            "the copy must carry the source element's complete in-scope set, not just 'x' from its own name");
    }

    [Fact]
    public async Task ResolveQNameAgainstSuchACopy_Succeeds()
    {
        // The failure this actually produced: FONS0004 on a prefix the source had.
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="combined" as="document-node()">
                  <xsl:document>
                    <xsl:element name="desc" namespace="" inherit-namespaces="no">
                      <xsl:sequence select="/desc/node()"/>
                    </xsl:element>
                  </xsl:document>
                </xsl:variable>
                <xsl:for-each select="($combined//scenario)[1]">
                  <xsl:variable name="copied" as="element(call)?">
                    <xsl:copy select="call"><xsl:sequence select="call/attribute()"/></xsl:copy>
                  </xsl:variable>
                  <xsl:value-of select="resolve-QName('mirror:thing', $copied)"/>
                </xsl:for-each>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        r.Trim().Should().Be("mirror:thing");
    }

    [Fact]
    public async Task CopyNamespacesNo_StillDropsInheritedBindings()
    {
        // The complement: copy-namespaces="no" is explicitly ALLOWED to drop the source's
        // additional bindings. Only the element's own name prefix must survive. This pins that
        // the fix widened the copy-namespaces="yes" path without weakening "no".
        const string ss = """
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:for-each select="/desc/scenario">
                  <xsl:variable name="copied" as="element(call)?">
                    <xsl:copy select="call" copy-namespaces="no"/>
                  </xsl:variable>
                  <xsl:value-of select="string-join(sort(in-scope-prefixes($copied)), ',')"/>
                </xsl:for-each>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var r = await RunAsync(ss);
        r.Trim().Should().NotContain("mirror");
    }
}
