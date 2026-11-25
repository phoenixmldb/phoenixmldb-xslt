using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The base URI of what fn:copy-of and fn:snapshot return. XSLT 3.0 §11.9.2 (which fn:copy-of
/// follows): "The base URI of a node is copied, except in the case of an element node having an
/// xml:base attribute, in which case the base URI of the new node is taken as the value of the
/// xml:base attribute, resolved if it is relative against the base URI of the xsl:copy-of
/// instruction." §18.4: each ancestor of a snapshot has the base URI of the original. The copies
/// had none: base-uri() of a copy was empty, a relative xml:base stayed relative, and a snapshot
/// lost the base URI of its document. The instructions were right and are unchanged.
/// </summary>
public sealed class CopyFunctionBaseUriTests
{
    private const string SourceUri = "file:///data/in/doc.xml";
    private const string StylesheetUri = "file:///data/xsl/style.xsl";

    private const string Input = """<r><a><b xml:base="sub/"><c/></b></a><d xml:base="http://ex.org/d/"><e/></d></r>""";

    [Theory]
    // fn:copy-of: the base URI of the original.
    [InlineData("base-uri(copy-of(//a))", SourceUri)]
    [InlineData("base-uri(copy-of(//e))", "http://ex.org/d/")]
    [InlineData("base-uri(copy-of(/))", SourceUri)]
    [InlineData("string(document-uri(copy-of(/)))", "")]
    // An xml:base of the copied element is resolved against the static base URI of the call.
    [InlineData("base-uri(copy-of(//b))", "file:///data/xsl/sub/")]
    [InlineData("base-uri(copy-of(//b)/c)", "file:///data/xsl/sub/")]
    // fn:snapshot: the ancestors keep their base URIs, the document node too.
    [InlineData("base-uri(snapshot(//a))", SourceUri)]
    [InlineData("base-uri(snapshot(//a)/..)", SourceUri)]
    [InlineData("base-uri(root(snapshot(//a)))", SourceUri)]
    [InlineData("base-uri(snapshot(//c))", "file:///data/in/sub/")]
    [InlineData("base-uri(snapshot(//e))", "http://ex.org/d/")]
    [InlineData("base-uri(snapshot(/))", SourceUri)]
    // The forms with no argument copy the context item.
    [InlineData("string(//a/base-uri(copy-of()))", SourceUri)]
    [InlineData("string(//b/base-uri(copy-of()))", "file:///data/xsl/sub/")]
    [InlineData("string(//a/base-uri(snapshot()))", SourceUri)]
    public async Task The_base_uri_of_a_copy_or_a_snapshot_is_the_one_the_specification_gives(string expression, string expected)
        => (await EvaluateAsync(expression)).Should().Be(expected);

    [Theory]
    // A copy attached to a new parent inherits the parent's base URI (here the temporary tree's:
    // the stylesheet's), or its own xml:base resolved against it; one with no parent keeps the
    // original's. These were right before.
    [InlineData("""<xsl:variable name="v"><xsl:copy-of select="//a"/></xsl:variable><xsl:value-of select="base-uri($v/a)"/>""", StylesheetUri)]
    [InlineData("""<xsl:variable name="v"><xsl:copy-of select="//b"/></xsl:variable><xsl:value-of select="base-uri($v/b)"/>""", "file:///data/xsl/sub/")]
    [InlineData("""<xsl:variable name="v" as="element()"><xsl:copy-of select="//a"/></xsl:variable><xsl:value-of select="base-uri($v)"/>""", SourceUri)]
    public async Task The_copy_instructions_keep_their_base_uri(string body, string expected)
        => (await RunAsync(body)).Should().Be(expected);

    private Task<string> EvaluateAsync(string expression)
        => RunAsync($"""<xsl:value-of select="{expression}"/>""");

    private static async Task<string> RunAsync(string body)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">{{body}}</xsl:template>
            </xsl:stylesheet>
            """, new Uri(StylesheetUri));
        transformer.SetSourceDocumentUri(new Uri(SourceUri));
        return await transformer.TransformAsync(Input);
    }
}
