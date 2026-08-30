using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>fn:transform</c> with <c>delivery-format: 'raw'</c> hands back the typed XDM value and the
/// caller discards the serialized buffer — that is the whole point of raw delivery.
/// <c>XsltTransformFunction</c>, the implementation used when <c>fn:transform</c> is called from
/// inside a stylesheet, built its options without <c>ReturnRawXdm</c>. TransformRawAsync
/// therefore still serialized the result, and serializing means taking its string value, which
/// for a map is FOTY0013 "Atomization is not defined for maps".
///
/// So a function returning a map died on a serialization nobody asked for, while the raw value
/// sitting beside it was correct. <c>XsltTransformProvider</c> — the XQuery-side twin, reached
/// when fn:transform is called from a query — has always set the equivalent flag.
///
/// Found via XSpec's external_xslt-package_arith suites, whose SUT function returns
/// <c>map { 0: 2.0e0, 1: 5.0e0 }</c>.
/// </summary>
public class TransformRawDeliveryTests
{
    /// <summary>
    /// Calls fn:transform from inside a stylesheet, which is what routes through
    /// XsltTransformFunction rather than XsltTransformProvider. stylesheet-text keeps the
    /// target stylesheet inline so the test needs nothing on disk.
    /// </summary>
    private static async Task<string> RunTransform(string targetFunctionBody, string lookup)
    {
        // The target stylesheet is passed as stylesheet-text, so it is XML-escaped inside the
        // driver's XPath string literal. Built by concatenation rather than an interpolated raw
        // string: the driver is mostly braces, and escaping them fights the syntax.
        var target =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\" "
            + "xmlns:f=\"urn:test:f\" exclude-result-prefixes=\"#all\">"
            + "<xsl:function name=\"f:go\" as=\"item()*\" visibility=\"public\">"
            + targetFunctionBody
            + "</xsl:function></xsl:stylesheet>";

        // Single quotes would end the XPath string literal, so double them; angle brackets and
        // ampersands must survive as XML text inside the attribute.
        var escaped = System.Security.SecurityElement.Escape(target)!.Replace("'", "''", StringComparison.Ordinal);

        var driver =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\" "
            + "xmlns:f=\"urn:test:f\" exclude-result-prefixes=\"#all\">"
            + "<xsl:template name=\"main\">"
            + "<xsl:variable name=\"t\" select=\"transform(map { "
            + "'stylesheet-text': '" + escaped + "', "
            + "'delivery-format': 'raw', "
            + "'initial-function': QName('urn:test:f', 'f:go'), "
            + "'function-params': [] })\"/>"
            + "<out><xsl:sequence select=\"" + lookup + "\"/></out>"
            + "</xsl:template></xsl:stylesheet>";

        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(driver);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    [Fact]
    public async Task RawDelivery_OfAFunctionReturningAMap_DoesNotAtomizeIt()
    {
        var result = await RunTransform(
            // A numeric key, so the target stylesheet body carries no quotes of its own -
            // it is embedded inside an XPath string literal inside an XML attribute, and each
            // extra quoting layer is one more chance to get the escaping wrong rather than
            // to test the engine.
            "<xsl:sequence select=\"map { 7: 99 }\"/>",
            "$t?output?7");
        result.Should().Contain("<out>99</out>");
    }

    [Fact]
    public async Task RawDelivery_OfAFunctionReturningAnArray_KeepsTheArray()
    {
        var result = await RunTransform(
            "<xsl:sequence select=\"[10, 20]\"/>",
            "$t?output?2");
        result.Should().Contain("<out>20</out>");
    }

    /// <summary>
    /// The control: raw delivery of an ordinary atomic result kept working throughout, which is
    /// why the missing flag went unnoticed — only map and array results reach the atomization
    /// that raises.
    /// </summary>
    [Fact]
    public async Task RawDelivery_OfAnAtomicResult_StillWorks()
    {
        var result = await RunTransform(
            "<xsl:sequence select=\"42\"/>",
            "$t?output");
        result.Should().Contain("<out>42</out>");
    }
}
