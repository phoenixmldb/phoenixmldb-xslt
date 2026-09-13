using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A name test carries the namespace URI resolved from its prefix at parse time; before
/// execution the XSLT engine walks the expression tree interning each URI into the node
/// store's namespace id. An unresolved prefixed name test matches only no-namespace nodes.
///
/// The walk descended into every expression shape EXCEPT an inline function body and a
/// dynamic function call, so <c>function($m) { $m/p:i }</c> selected nothing — no error, no
/// warning, just the empty sequence. Anything built on higher-order functions over namespaced
/// input silently produced empty results: fold-left over a parser's member elements returned
/// an empty array, and the failure surfaced far from its cause.
///
/// Reported by Martin Honnen against the xdm-persistence library, whose parser is written as
/// <c>fold-left($el/xdm:member, array{}, function($acc, $m) { array:append($acc, ... $m/xdm:item ...) })</c>.
/// </summary>
public class InlineFunctionNamespaceTests
{
    // A one-element document in urn:p, built at runtime so the nodes come from the same
    // node store the name tests are interned against.
    private const string Doc =
        "parse-xml('&lt;p:r xmlns:p=&quot;urn:p&quot;&gt;&lt;p:m&gt;&lt;p:i&gt;A&lt;/p:i&gt;&lt;/p:m&gt;&lt;/p:r&gt;')";

    private static async Task<string> Transform(string body)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:p="urn:p"
                            exclude-result-prefixes="#all">
              <xsl:variable name="d" as="document-node()" select="{Doc}"/>
              <xsl:template match="/" name="xsl:initial-template">
                <xsl:variable name="m" select="$d/p:r/p:m"/>
                <out>{body}</out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        return await t.TransformAsync("<in/>").ConfigureAwait(true);
    }

    [Fact]
    public async Task PrefixedNameTest_InsideInlineFunction_SelectsTheNamespacedChild()
    {
        var r = await Transform("""<xsl:value-of select="count((function($x){ $x/p:i })($m))"/>""")
            .ConfigureAwait(true);
        r.Should().Be("<out>1</out>");
    }

    [Fact]
    public async Task PrefixedNameTest_InsideFoldLeftCallback_SelectsTheNamespacedChild()
    {
        var r = await Transform(
            """<xsl:value-of select="count(fold-left($m, (), function($acc,$x){ ($acc, $x/p:i) }))"/>""")
            .ConfigureAwait(true);
        r.Should().Be("<out>1</out>");
    }

    [Fact]
    public async Task PrefixedNameTest_InsideInlineFunction_AgreesWithTheSamePathOutside()
    {
        // The whole defect was a disagreement between these two: outside=1, inline=0.
        var r = await Transform(
            """<xsl:value-of select="count($m/p:i) = count((function($x){ $x/p:i })($m))"/>""")
            .ConfigureAwait(true);
        r.Should().Be("<out>true</out>");
    }

    [Fact]
    public async Task PrefixedNameTest_InsideInlineFunction_StillRejectsAForeignNamespace()
    {
        // Guard against "fixing" this by making prefixed tests match anything: a name test in
        // a different namespace must still select nothing.
        var ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:p="urn:p" xmlns:q="urn:q" exclude-result-prefixes="#all">
              <xsl:variable name="d" as="document-node()"
                select="parse-xml('&lt;p:r xmlns:p=&quot;urn:p&quot;&gt;&lt;p:m&gt;&lt;p:i&gt;A&lt;/p:i&gt;&lt;/p:m&gt;&lt;/p:r&gt;')"/>
              <xsl:template match="/" name="xsl:initial-template">
                <xsl:variable name="m" select="$d/p:r/p:m"/>
                <out><xsl:value-of select="count((function($x){ $x/q:i })($m))"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        (await t.TransformAsync("<in/>").ConfigureAwait(true)).Should().Be("<out>0</out>");
    }

    [Fact]
    public async Task PrefixedNameTest_InsideDynamicFunctionCallArgument_IsResolved()
    {
        // The dynamic-call case the same walk was missing: the callee expression and the
        // arguments are ordinary expressions and their name tests need interning too.
        var r = await Transform(
            """<xsl:value-of select="count((function($x){ $x })($m/p:i))"/>""")
            .ConfigureAwait(true);
        r.Should().Be("<out>1</out>");
    }
}
