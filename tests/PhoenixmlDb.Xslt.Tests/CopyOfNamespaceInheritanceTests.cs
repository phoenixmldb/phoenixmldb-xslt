using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A node copied into a temporary tree inherits the namespaces in scope on the element it is
/// copied into, for the prefixes it does not bind itself, unless that element was built with
/// inherit-namespaces="no" (XSLT 3.0 §11.9.1). An unprefixed element never inherits a default.
/// </summary>
/// <remarks>
/// The copies were materialised with only their own (source) bindings, so
/// in-scope-prefixes() on a copied x:param under &lt;root xmlns="urn:x"&gt; reported no default,
/// where Saxon reports urn:x. Serialized output was unaffected; reading namespaces off the
/// copy was not.
/// </remarks>
public sealed class CopyOfNamespaceInheritanceTests
{
    private const string Source = """<x:d xmlns:x="urn:x"><x:p n="1"><foo/></x:p></x:d>""";

    private static async Task<string> InScopeAsync(string parentOpen, string parentClose, string copyNamespaces = "yes")
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:x="urn:x" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="v">{parentOpen}<xsl:copy-of select="//x:p" copy-namespaces="{copyNamespaces}"/>{parentClose}</xsl:variable>
                <xsl:value-of select="$v//* ! (name() || '[' || string-join(sort(in-scope-prefixes(.)[. ne 'xml'] ! (if (. = '') then '#default' else .)), ',') || ']')"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync(Source);
    }

    [Fact]
    public async Task UnderXslElement_ThePrefixedCopyInheritsTheDefault()
        => (await InScopeAsync("""<xsl:element name="root" namespace="urn:x">""", "</xsl:element>"))
            .Should().Be("root[#default] x:p[#default,x] foo[x]");

    [Fact]
    public async Task UnderALiteralResultElement_ThePrefixedCopyInheritsItsBindings()
        => (await InScopeAsync("""<r xmlns="urn:r" xmlns:q="urn:q">""", "</r>"))
            .Should().Be("r[#default,q] x:p[#default,q,x] foo[q,x]");

    [Fact]
    public async Task InheritNamespacesNo_NothingIsInherited()
        => (await InScopeAsync("""<xsl:element name="root" namespace="urn:x" inherit-namespaces="no">""", "</xsl:element>"))
            .Should().Be("root[#default] x:p[x] foo[x]");
}
