using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A function whose declared return type is a NODE type must hand back a node, including when
/// the body produced a temporary tree.
///
/// The return-type handling only coerced ATOMIC declared types, so a ResultTreeFragment was
/// returned raw:
///
///   &lt;xsl:function name="f:wrap" as="document-node()"&gt;
///     &lt;xsl:variable name="w"&gt;&lt;xsl:sequence select="$nodes"/&gt;&lt;/xsl:variable&gt;
///     &lt;xsl:sequence select="$w"/&gt;
///   &lt;/xsl:function&gt;
///   f:wrap($e)/node()   ->  XPTY0020 "context item is not a node (got ResultTreeFragment)"
///
/// Assigning the call to a variable DID convert it, so the defect only showed on a direct path
/// step off the call, or when the fragment flowed into an as="item()*" variable. That untyped
/// variable body is the spec's implicit-document-node idiom, and it is what XSpec's
/// wrap:wrap-nodes uses deliberately (its comment cites xspec/xspec#47). Its wrapper document
/// came back with no children, so every x:expect whose @test navigates from the context item
/// saw an empty sequence.
/// </summary>
public class FunctionNodeReturnMaterialisationTests
{
    private static async Task<string> Run(string body)
    {
        var xslt = $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f" exclude-result-prefixes="#all">
              {body}
            </xsl:stylesheet>
            """;
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xslt);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    private const string WrapFunction = """
        <xsl:function name="f:wrap" as="document-node()">
          <xsl:param name="nodes" as="node()*"/>
          <xsl:variable name="w"><xsl:sequence select="$nodes"/></xsl:variable>
          <xsl:sequence select="$w"/>
        </xsl:function>
        """;

    /// <summary>The case that failed: a path step taken directly off the call.</summary>
    [Fact]
    public async Task DocumentNodeReturn_IsNavigableDirectlyOffTheCall()
    {
        var result = await Run("""
            WRAPFN
            <xsl:template name="main">
              <xsl:variable name="d" as="document-node()"><xsl:document><span/></xsl:document></xsl:variable>
              <out inline="{count(f:wrap($d/span)/node())}"
                   name="{f:wrap($d/span)/node()/name()}"/>
            </xsl:template>
            """.Replace("WRAPFN", WrapFunction, StringComparison.Ordinal));
        result.Should().Contain("inline=\"1\"").And.Contain("name=\"span\"");
    }

    /// <summary>The route that always worked, kept so a regression is attributable.</summary>
    [Fact]
    public async Task DocumentNodeReturn_StillWorksViaAVariable()
    {
        var result = await Run("""
            WRAPFN
            <xsl:template name="main">
              <xsl:variable name="d" as="document-node()"><xsl:document><span/></xsl:document></xsl:variable>
              <xsl:variable name="w" select="f:wrap($d/span)"/>
              <out kids="{count($w/node())}" isDoc="{$w instance of document-node()}"/>
            </xsl:template>
            """.Replace("WRAPFN", WrapFunction, StringComparison.Ordinal));
        result.Should().Contain("kids=\"1\"").And.Contain("isDoc=\"true\"");
    }

    /// <summary>
    /// The XSpec shape: the wrapper flows into an as="item()*" variable and is then used as a
    /// context item. It has to arrive as a document node with its children intact.
    /// </summary>
    [Fact]
    public async Task DocumentNodeReturn_SurvivesIntoAnItemStarVariable()
    {
        var result = await Run("""
            WRAPFN
            <xsl:template name="main">
              <xsl:variable name="d" as="document-node()"><xsl:document><span/></xsl:document></xsl:variable>
              <xsl:variable name="items" as="item()*"><xsl:sequence select="f:wrap($d/span)"/></xsl:variable>
              <out isDoc="{$items[1] instance of document-node()}" kids="{count($items[1]/node())}"/>
            </xsl:template>
            """.Replace("WRAPFN", WrapFunction, StringComparison.Ordinal));
        result.Should().Contain("isDoc=\"true\"").And.Contain("kids=\"1\"");
    }

    /// <summary>
    /// An atomic declared return type must still be coerced — that path was the only one working
    /// before, and the fix adds a node branch ahead of it rather than replacing it.
    ///
    /// Uses numeric promotion (integer -> double), which the function conversion rules DO
    /// perform. A string is deliberately not used: the rules cast xs:untypedAtomic to the
    /// required type and promote between numerics, but do not cast xs:string, so
    /// as="xs:double" returning '42' correctly raises XTTE0780.
    /// </summary>
    [Fact]
    public async Task AtomicReturnTypeIsStillCoerced()
    {
        var result = await Run("""
            <xsl:function name="f:n" as="xs:double"><xsl:sequence select="42"/></xsl:function>
            <xsl:template name="main">
              <out isDouble="{f:n() instance of xs:double}" isInt="{f:n() instance of xs:integer}"/>
            </xsl:template>
            """);
        result.Should().Contain("isDouble=\"true\"", "an integer is promoted to the declared xs:double");
        result.Should().Contain("isInt=\"false\"", "and is no longer an integer after promotion");
    }
}
