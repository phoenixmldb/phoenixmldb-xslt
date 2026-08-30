using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>fn:deep-equal</c> is defined for maps and arrays (XPath 3.1 F&amp;O 14.2.2); it does not
/// atomize its arguments. Under XSLT, the map case raised FOTY0013 "Atomization is not defined
/// for maps" while arrays and atomic values were fine, and the identical call through the
/// XQuery CLI answered correctly — so the defect is on the XSLT evaluation path, not in
/// fn:deep-equal itself.
///
/// Found through XSpec, whose x:deep-equal wrapper (src/common/deep-equal.xsl) compares
/// expected against actual; four suites in the FOTY0013 census bucket die there.
/// </summary>
public class DeepEqualMapTests
{
    private static async Task<string> Eval(string expression)
    {
        var xslt = $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:map="http://www.w3.org/2005/xpath-functions/map"
              xmlns:array="http://www.w3.org/2005/xpath-functions/array" exclude-result-prefixes="#all">
              <xsl:template name="main"><out><xsl:sequence select="{expression}"/></out></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xslt);
        t.SetInitialTemplate("main");
        return await t.TransformAsync("<dummy/>");
    }

    [Theory]
    // The controls: these always worked, and they place the failure precisely on maps.
    [InlineData("deep-equal(1, 1)", "true")]
    [InlineData("deep-equal(1, 2)", "false")]
    [InlineData("deep-equal([1,2], [1,2])", "true")]
    [InlineData("deep-equal([1,2], [1,3])", "false")]
    // The failing case.
    [InlineData("deep-equal(map{'a':1}, map{'a':1})", "true")]
    [InlineData("deep-equal(map{'a':1}, map{'a':2})", "false")]
    [InlineData("deep-equal(map{'a':1}, map{'b':1})", "false")]
    // Nested, because XSpec compares whole result trees rather than single entries.
    [InlineData("deep-equal(map{'a':map{'b':[1,2]}}, map{'a':map{'b':[1,2]}})", "true")]
    [InlineData("deep-equal(map{'a':map{'b':[1,2]}}, map{'a':map{'b':[1,3]}})", "false")]
    // An array is a single ITEM and is never equal to the sequence of its members. These
    // answered TRUE: ToList treated List<object?> - the engine's array representation, and an
    // IEnumerable<object?> - as a sequence and flattened it. A silent wrong answer, unlike the
    // map case which at least raised.
    [InlineData("deep-equal([1], 1)", "false")]
    [InlineData("deep-equal([1,2], (1,2))", "false")]
    [InlineData("deep-equal([1], [1])", "true")]
    // A map is likewise one item, not its entries.
    [InlineData("deep-equal(map{'a':1}, ('a', 1))", "false")]
    public async Task DeepEqual(string expression, string expected)
    {
        (await Eval(expression)).Should().Contain($"<out>{expected}</out>");
    }
}
