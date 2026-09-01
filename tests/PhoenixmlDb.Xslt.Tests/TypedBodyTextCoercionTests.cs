using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A typed variable whose sequence-constructor body produces text must accept that text.
/// </summary>
/// <remarks>
/// The body of <c>&lt;xsl:variable as="xs:string"&gt;</c> yields a text node, which the engine
/// represents with the lightweight <c>TextNodeItem</c> marker. The coercion check atomized
/// <c>string</c>, <c>XdmNode</c> and <c>xs:anyURI</c> but not that marker, so it fell through to
/// "no string value" and answered false. The variable then raised XTTE0570 against a value of
/// exactly the declared type.
///
/// <para>
/// XSpec's <c>report-sequence.xsl</c> names an XSD type this way — an <c>xsl:choose</c> whose
/// branches are literal text, assigned to an <c>as="xs:string"</c> variable.
/// </para>
/// </remarks>
public class TypedBodyTextCoercionTests
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

    /// <summary>The shape XSpec uses: a typed string variable wrapping an xsl:choose.</summary>
    [Fact]
    public async Task ATypedStringVariable_AcceptsTextFromAChooseBody()
    {
        var result = await RunAsync(
            "<xsl:variable name=\"v\" select=\"'abc'\"/>"
            + "<xsl:variable name=\"name\" as=\"xs:string\">"
            + "<xsl:choose>"
            + "<xsl:when test=\"$v instance of xs:integer\">integer</xsl:when>"
            + "<xsl:when test=\"$v instance of xs:string\">string</xsl:when>"
            + "<xsl:otherwise>anyAtomicType</xsl:otherwise>"
            + "</xsl:choose></xsl:variable>"
            + "<out><xsl:value-of select=\"$name\"/></out>");

        result.Should().Be("<out>string</out>");
    }

    /// <summary>Plain literal text in a typed body, and text produced by xsl:text.</summary>
    [Fact]
    public async Task ATypedStringVariable_AcceptsLiteralAndXslText()
    {
        var result = await RunAsync(
            "<xsl:variable name=\"a\" as=\"xs:string\">literal</xsl:variable>"
            + "<xsl:variable name=\"b\" as=\"xs:string\"><xsl:text>from-xsl-text</xsl:text></xsl:variable>"
            + "<out a=\"{$a}\" b=\"{$b}\"/>");

        result.Should().Be("<out a=\"literal\" b=\"from-xsl-text\"/>");
    }

    /// <summary>Text in a typed body coerces to a non-string type too.</summary>
    [Fact]
    public async Task ATypedIntegerVariable_AcceptsNumericTextFromABody()
    {
        var result = await RunAsync(
            "<xsl:variable name=\"n\" as=\"xs:integer\"><xsl:text>42</xsl:text></xsl:variable>"
            + "<out><xsl:value-of select=\"$n + 1\"/></out>");

        result.Should().Be("<out>43</out>");
    }
}
