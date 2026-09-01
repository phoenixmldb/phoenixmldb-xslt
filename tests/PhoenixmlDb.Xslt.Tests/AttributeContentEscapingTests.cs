using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xsl:attribute</c> with a sequence-constructor body must produce the same attribute value
/// as the <c>select</c> and AVT forms.
/// </summary>
/// <remarks>
/// The body form runs its content into the ordinary output buffer and then takes the written
/// span as the attribute's value. Text was XML-escaped on the way into that buffer, and escaped
/// AGAIN when the finished value was serialized: <c>&amp;</c> came back as <c>&amp;amp;amp;</c>,
/// and a <c>&gt;</c> written by <c>xsl:text</c> came back as <c>&amp;amp;gt;</c>.
///
/// <para>
/// Nothing errors — the attribute is well-formed, just wrong — so this only surfaces when
/// something downstream reads the value back. XSpec's compiler builds a <c>select</c> attribute
/// exactly this way, wrapping an XPath that contains the arrow operator <c>=&gt;</c>; the
/// generated stylesheet then failed to parse, reporting "token recognition error at: '&amp;'"
/// because the XPath lexer was handed a literal <c>&amp;gt;</c>.
/// </para>
/// </remarks>
public class AttributeContentEscapingTests
{
    private static async Task<string> RunAsync(string body)
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\">"
            + "<xsl:template name=\"main\">" + body + "</xsl:template></xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    /// <summary>
    /// The four ways to build an attribute value must agree. They are asserted together because
    /// the bug was a divergence BETWEEN forms, not an absolute-value error — the select and AVT
    /// forms were always right, which is what made the body form's corruption easy to miss.
    /// </summary>
    [Fact]
    public async Task AllFormsOfAttributeConstruction_EscapeExactlyOnce()
    {
        var result = await RunAsync(
            "<r>"
            + "<a1 v=\"x &gt; y &amp; z\"/>"
            + "<a2><xsl:attribute name=\"v\"><xsl:text>x &gt; y &amp; z</xsl:text></xsl:attribute></a2>"
            + "<a3><xsl:attribute name=\"v\" select=\"'x &gt; y &amp; z'\"/></a3>"
            + "<a4 v=\"{'x &gt; y &amp; z'}\"/>"
            + "</r>");

        // One level of escaping, four times over.
        result.Should().Be(
            "<r><a1 v=\"x &gt; y &amp; z\"/><a2 v=\"x &gt; y &amp; z\"/>"
            + "<a3 v=\"x &gt; y &amp; z\"/><a4 v=\"x &gt; y &amp; z\"/></r>");
    }

    /// <summary>
    /// Ordinary element text must still be escaped exactly once. This is the OTHER arm of the
    /// branch the fix introduced, and it is here deliberately: the first version of the fix
    /// recursed infinitely on this path, and a test that built only attributes never ran it.
    /// </summary>
    [Fact]
    public async Task ElementTextContent_IsStillEscapedExactlyOnce()
    {
        var result = await RunAsync(
            "<r><t1>x &gt; y &amp; z</t1>"
            + "<t2><xsl:text>x &gt; y &amp; z</xsl:text></t2>"
            + "<t3><xsl:value-of select=\"'x &gt; y &amp; z'\"/></t3></r>");

        result.Should().Be(
            "<r><t1>x &gt; y &amp; z</t1><t2>x &gt; y &amp; z</t2><t3>x &gt; y &amp; z</t3></r>");
    }

    /// <summary>
    /// The shape XSpec's compiler emits: an XPath arrow operator built by an xsl:attribute body.
    /// Round-tripped through a second parse, because that is where the corruption showed up —
    /// the value looked plausible until something tried to read it as an expression.
    /// </summary>
    [Fact]
    public async Task AttributeBodyCarryingAnXPathArrow_SurvivesReparse()
    {
        var generated = await RunAsync(
            "<xsl:element name=\"xsl:variable\" namespace=\"http://www.w3.org/1999/XSL/Transform\">"
            + "<xsl:attribute name=\"name\">v</xsl:attribute>"
            + "<xsl:attribute name=\"select\"><xsl:text>'a' =&gt; upper-case()</xsl:text></xsl:attribute>"
            + "</xsl:element>");

        generated.Should().Contain("select=\"'a' =&gt; upper-case()\"");
        generated.Should().NotContain("&amp;gt;");

        // And the emitted markup must actually be usable as a stylesheet fragment.
        var stylesheet =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\">"
            + generated
            + "<xsl:template name=\"main\"><out><xsl:value-of select=\"$v\"/></out></xsl:template>"
            + "</xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        t.SetInitialTemplate("main");
        (await t.TransformAsync((string?)null)).Should().Be("<out>A</out>");
    }
}
