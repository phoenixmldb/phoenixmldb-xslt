using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Copying an element must not change its name, and the namespace URI is part of the name.
///
/// An unprefixed element in NO namespace can only exist where the default namespace is
/// undeclared (<c>xmlns=""</c>), so a faithful copy has to re-emit that undeclaration whenever a
/// default namespace would otherwise apply. Output is built as serialized text, so without it
/// the bare start tag re-parses into the enclosing default namespace and the copy silently comes
/// out with a different name:
///
///   &lt;outer xmlns="urn:o"&gt;&lt;undeclared xmlns=""/&gt;&lt;/outer&gt;
///   xsl:copy of `undeclared`  ->  namespace-uri() became "urn:o"
///
/// Two independent implementations had the bug — <c>xsl:copy</c> (which inherited the wrong
/// default from GatherSourceInScopeBindings and so needed an override, not a fill-in) and the
/// built-in shallow-copy rule (which replays only the declarations the node model recorded, and
/// an undeclaration is not among them). <c>xsl:copy-of</c> was correct throughout because it
/// copies the subtree wholesale, which is why only the identity-transform shape showed it.
///
/// Found via XSpec's undeclare-ns suites, whose combine step is an identity transform.
/// </summary>
public class CopyNamespaceUndeclarationTests
{
    private const string Doc = """
        <description xmlns="http://example.org/outer">
          <param><undeclared xmlns=""><kid/></undeclared></param>
        </description>
        """;

    private static async Task<string> Run(string body, string input)
    {
        var xslt = $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:o="http://example.org/outer" exclude-result-prefixes="#all">
              {body}
            </xsl:stylesheet>
            """;
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xslt);
        return await t.TransformAsync(input);
    }

    [Fact]
    public async Task ExplicitXslCopy_KeepsTheElementOutOfTheEnclosingDefaultNamespace()
    {
        var result = await Run("""
            <xsl:template match="/">
              <xsl:variable name="copied" as="document-node()">
                <xsl:document><xsl:apply-templates select="/" mode="ex"/></xsl:document>
              </xsl:variable>
              <out>[<xsl:value-of select="$copied//*[local-name()='undeclared']/namespace-uri()"/>]</out>
            </xsl:template>
            <xsl:mode name="ex" on-no-match="fail"/>
            <xsl:template match="element()|document-node()" mode="ex">
              <xsl:copy><xsl:apply-templates select="node()" mode="#current"/></xsl:copy>
            </xsl:template>
            <xsl:template match="text()" mode="ex"/>
            """, Doc);
        result.Should().Contain("<out>[]</out>");
    }

    [Fact]
    public async Task BuiltInShallowCopy_KeepsTheElementOutOfTheEnclosingDefaultNamespace()
    {
        var result = await Run("""
            <xsl:template match="/">
              <xsl:variable name="copied" as="document-node()">
                <xsl:document><xsl:apply-templates select="/" mode="sc"/></xsl:document>
              </xsl:variable>
              <out>[<xsl:value-of select="$copied//*[local-name()='undeclared']/namespace-uri()"/>]</out>
            </xsl:template>
            <xsl:mode name="sc" on-no-match="shallow-copy"/>
            """, Doc);
        result.Should().Contain("<out>[]</out>");
    }

    /// <summary>
    /// The control that the first attempt at this fix broke: in a document with no namespaces at
    /// all, nothing needs undeclaring and the copy must NOT gain a redundant xmlns="". Emitting
    /// it unconditionally turned every <c>&lt;doc&gt;</c> into <c>&lt;doc xmlns=""&gt;</c>.
    /// </summary>
    [Fact]
    public async Task NamespaceFreeDocument_GainsNoRedundantUndeclaration()
    {
        var result = await Run("""
            <xsl:template match="/"><xsl:apply-templates select="*" mode="sc"/></xsl:template>
            <xsl:mode name="sc" on-no-match="shallow-copy"/>
            """, "<doc><c>v</c></doc>");
        result.Should().Contain("<doc><c>v</c></doc>");
        result.Should().NotContain("xmlns=\"\"", "nothing needed undeclaring here");
    }

    /// <summary>
    /// An element that genuinely IS in the enclosing namespace must keep it — the fix must not
    /// strip namespaces, only decline to add one that was undeclared.
    /// </summary>
    [Fact]
    public async Task NamespacedElement_KeepsItsNamespaceThroughCopy()
    {
        var result = await Run("""
            <xsl:template match="/">
              <xsl:variable name="copied" as="document-node()">
                <xsl:document><xsl:apply-templates select="/" mode="sc"/></xsl:document>
              </xsl:variable>
              <out>[<xsl:value-of select="$copied//*[local-name()='param']/namespace-uri()"/>]</out>
            </xsl:template>
            <xsl:mode name="sc" on-no-match="shallow-copy"/>
            """, Doc);
        result.Should().Contain("<out>[http://example.org/outer]</out>");
    }
}
