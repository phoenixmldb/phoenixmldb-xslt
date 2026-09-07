using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xsl:namespace</c> producing a free-standing namespace node — a value returned from a
/// function declared <c>as="namespace-node()*"</c> — must not be validated as though it were
/// being attached to the enclosing element.
/// </summary>
/// <remarks>
/// <para>
/// XTDE0440 ("cannot define a default namespace when the element being constructed is in no
/// namespace") and XTDE0430 ("two namespace nodes with the same prefix have different URIs")
/// both describe an element under construction. They ran before the branch that emits a
/// free-standing node, and the stacks they consult still held the ENCLOSING element's entries —
/// so a namespace node that was merely being returned as a value was judged against an element
/// it would never be attached to.
/// </para>
/// <para>
/// XSpec's x:copy-of-namespaces has exactly this shape. Called once per source element, two
/// calls covering elements with different default namespaces produced two empty-prefix nodes,
/// and the second was rejected as a duplicate declaration — even though each belonged to a
/// different element. That failed the namespaces tutorial suite at Compile with
/// XTDE0430 naming DocBook and XHTML.
/// </para>
/// </remarks>
public class FreeStandingNamespaceNodeTests
{
    private static async Task<string> RunAsync(string body, string sourceXml)
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" "
            + "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:f=\"urn:f\" version=\"3.0\" "
            + "exclude-result-prefixes=\"#all\">"
            + "<xsl:function name=\"f:nscopy\" as=\"namespace-node()*\">"
            + "<xsl:param name=\"element\" as=\"element()\"/>"
            + "<xsl:for-each select=\"in-scope-prefixes($element)[. ne 'xml']\">"
            + "<xsl:namespace name=\"{.}\" select=\"namespace-uri-for-prefix(., $element)\"/>"
            + "</xsl:for-each></xsl:function>"
            + "<xsl:template match=\"/\">" + body + "</xsl:template></xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        return await t.TransformAsync(sourceXml);
    }

    private const string TwoDefaults =
        "<r xmlns=\"urn:docbook\"><first/><second xmlns=\"urn:xhtml\"/></r>";

    /// <summary>
    /// Two calls covering elements with DIFFERENT default namespaces. Previously the second
    /// raised a false XTDE0430.
    /// </summary>
    [Fact]
    public async Task TwoCalls_WithDifferentDefaultNamespaces_DoNotConflict()
    {
        var result = await RunAsync(
            "<wrapper>"
            + "<xsl:variable name=\"a\" as=\"namespace-node()*\" select=\"f:nscopy(/*/*:first)\"/>"
            + "<xsl:variable name=\"b\" as=\"namespace-node()*\" select=\"f:nscopy(/*/*:second)\"/>"
            + "<got a=\"{count($a)}\" b=\"{count($b)}\"/></wrapper>",
            TwoDefaults);
        result.Should().Be("<wrapper><got a=\"1\" b=\"1\"/></wrapper>");
    }

    /// <summary>
    /// A single call inside an element that is itself in NO namespace. Previously this raised a
    /// false XTDE0440, because the free node was checked against the enclosing element.
    /// </summary>
    [Fact]
    public async Task DefaultNamespaceNode_InsideNoNamespaceElement_IsAllowed()
    {
        var result = await RunAsync(
            "<wrapper>"
            + "<xsl:variable name=\"a\" as=\"namespace-node()*\" select=\"f:nscopy(/*/*:first)\"/>"
            + "<got a=\"{count($a)}\"/></wrapper>",
            TwoDefaults);
        result.Should().Be("<wrapper><got a=\"1\"/></wrapper>");
    }

    /// <summary>
    /// The returned node carries the right URI — the fix must not blank the value out.
    /// </summary>
    [Fact]
    public async Task FreeStandingNamespaceNode_KeepsItsUri()
    {
        var result = await RunAsync(
            "<wrapper><xsl:variable name=\"a\" as=\"namespace-node()*\" "
            + "select=\"f:nscopy(/*/*:second)\"/><got uri=\"{$a[1]}\"/></wrapper>",
            TwoDefaults);
        result.Should().Be("<wrapper><got uri=\"urn:xhtml\"/></wrapper>");
    }

    /// <summary>
    /// A genuine conflict on one element under construction must STILL raise XTDE0430 — the
    /// hoist must not disable the real check.
    /// </summary>
    [Fact]
    public async Task GenuineConflictOnOneConstructedElement_StillRaisesXTDE0430()
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\">"
            + "<xsl:template match=\"/\"><out xmlns:p=\"urn:one\">"
            + "<xsl:namespace name=\"q\" select=\"'urn:a'\"/>"
            + "<xsl:namespace name=\"q\" select=\"'urn:b'\"/>"
            + "</out></xsl:template></xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        var ex = await Record.ExceptionAsync(async () => await t.TransformAsync("<r/>"));
        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("XTDE0430");
    }
}
