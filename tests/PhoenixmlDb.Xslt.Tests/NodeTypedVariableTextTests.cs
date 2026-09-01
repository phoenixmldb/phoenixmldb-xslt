using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A variable declared with a NODE type must hold a real node, not the internal text marker.
/// </summary>
/// <remarks>
/// <c>TextNodeItem</c> lets the sequence accumulator tell a text NODE from an atomic string
/// (XSLT 3.0 §5.7.2). It has no identity, parent or store, so it is not a node to any operation
/// that needs one. <c>&lt;xsl:variable as="text()"&gt;&lt;xsl:text&gt;t&lt;/xsl:text&gt;&lt;/xsl:variable&gt;</c>
/// therefore held something that failed node operations — <c>except</c> reported
/// "An operand of the except operator is not a node".
///
/// <para>
/// Binding a variable is where a sequence stops being under construction and becomes a value,
/// so it is where the marker is made real. Found via XSpec's <c>wrap_stylesheet</c> suite, whose
/// fixture builds exactly this variable and then writes <c>$all-nodes except $document-text</c>.
/// </para>
/// </remarks>
public class NodeTypedVariableTextTests
{
    private static async Task<string> RunAsync(string body)
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" "
            + "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" version=\"3.0\" "
            + "exclude-result-prefixes=\"#all\">"
            + "<xsl:template name=\"main\">" + body + "</xsl:template></xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    /// <summary>The shape from XSpec's fixture: `except` over a sequence holding such a variable.</summary>
    [Fact]
    public async Task ATextTypedVariable_IsANodeForExcept()
    {
        var result = await RunAsync(
            "<xsl:variable name=\"t\" as=\"text()\"><xsl:text>text</xsl:text></xsl:variable>"
            + "<xsl:variable name=\"e\" as=\"element()\"><a/></xsl:variable>"
            + "<xsl:variable name=\"all\" as=\"node()+\" select=\"$t, $e\"/>"
            + "<out n=\"{count($all except $t)}\"/>");

        result.Should().Be("<out n=\"1\"/>");
    }

    /// <summary>It is a text node by every other measure too.</summary>
    [Fact]
    public async Task ATextTypedVariable_IsATextNode()
    {
        var result = await RunAsync(
            "<xsl:variable name=\"t\" as=\"text()\"><xsl:text>abc</xsl:text></xsl:variable>"
            + "<out isnode=\"{$t instance of node()}\" istext=\"{$t instance of text()}\""
            + " str=\"{string($t)}\"/>");

        result.Should().Be("<out isnode=\"true\" istext=\"true\" str=\"abc\"/>");
    }

    /// <summary>The same for a node()-typed variable, and for union/intersect.</summary>
    [Fact]
    public async Task ANodeTypedVariable_WorksWithUnionAndIntersect()
    {
        var result = await RunAsync(
            "<xsl:variable name=\"t\" as=\"node()\"><xsl:text>x</xsl:text></xsl:variable>"
            + "<out u=\"{count($t | $t)}\" i=\"{count($t intersect $t)}\"/>");

        result.Should().Be("<out u=\"1\" i=\"1\"/>");
    }

    /// <summary>
    /// The same thing for a GLOBAL variable. Global and local binding are separate code paths,
    /// and only the local one was fixed first — the local test passed while the corpus did not
    /// move, because XSpec's fixture declares these globally.
    /// </summary>
    /// <remarks>
    /// The global path built an XdmText but never registered it in the node store, having taken
    /// an id from the store. Node identity resolves through the store, so set operations could
    /// not see it. The document-node() branch beside it had always registered.
    /// </remarks>
    [Fact]
    public async Task AGlobalTextTypedVariable_IsANodeForSetOperations()
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" "
            + "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" version=\"3.0\" "
            + "exclude-result-prefixes=\"#all\">"
            + "<xsl:variable name=\"t\" as=\"text()\"><xsl:text>text</xsl:text></xsl:variable>"
            + "<xsl:variable name=\"e\" as=\"element()\"><a/></xsl:variable>"
            + "<xsl:variable name=\"all\" as=\"node()+\" select=\"$t, $e\"/>"
            + "<xsl:template name=\"main\">"
            + "<out except=\"{count($all except $t)}\" union=\"{count($t | $t)}\""
            + " istext=\"{$t instance of text()}\"/>"
            + "</xsl:template></xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        t.SetInitialTemplate("main");
        var result = await t.TransformAsync((string?)null);

        result.Should().Be("<out except=\"1\" union=\"1\" istext=\"true\"/>");
    }
}
