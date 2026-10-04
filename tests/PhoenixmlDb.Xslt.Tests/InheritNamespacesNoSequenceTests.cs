using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An element built with inherit-namespaces="no" passes none of its namespaces to the nodes
/// added to it (XSLT 3.0 §11.2): a prefixed child with no default namespace of its own has none
/// in the result tree either.
/// </summary>
/// <remarks>
/// Only unprefixed no-namespace children were given the xmlns="" that keeps the parent's default
/// out, so a prefixed child added with xsl:sequence inherited it. XSpec builds its combined
/// document exactly this way (xsl:element name="{local-name()}" namespace="{namespace-uri()}"
/// inherit-namespaces="no"), and every compiled x:call / x:param gained a stray
/// &lt;xsl:namespace name=""&gt; for the XSpec namespace that Saxon does not produce.
/// </remarks>
public sealed class InheritNamespacesNoSequenceTests
{
    private const string Source = """<x:d xmlns:x="urn:x"><x:s><x:p n="1"><foo/></x:p></x:s></x:d>""";

    private static async Task<string> RunAsync(string inherit)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:x="urn:x" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="src" as="document-node()"><xsl:document><xsl:copy-of select="/x:d/node()"/></xsl:document></xsl:variable>
                <xsl:variable name="d" as="document-node()"><xsl:document>
                  <xsl:element name="root" namespace="urn:x" {inherit}><xsl:sequence select="$src/node()"/></xsl:element>
                </xsl:document></xsl:variable>
                <xsl:value-of select="$d//* ! (name() || '[' || string-join(sort(in-scope-prefixes(.)[. ne 'xml'] ! (if (. = '') then '#default' else .)), ',') || ']')"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync(Source);
    }

    [Fact]
    public async Task PrefixedChildren_DoNotInheritTheParentsDefaultNamespace()
        => (await RunAsync("""inherit-namespaces="no" """)).Should().Be("root[#default] x:s[x] x:p[x] foo[x]");

    /// <summary>With the default (yes) the children do inherit it, as Saxon also reports.</summary>
    [Fact]
    public async Task WithInheritance_PrefixedChildrenInheritIt()
        => (await RunAsync("")).Should().Be("root[#default] x:s[#default,x] x:p[#default,x] foo[x]");
}
