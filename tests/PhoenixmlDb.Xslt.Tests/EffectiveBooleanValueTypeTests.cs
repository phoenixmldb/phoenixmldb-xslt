using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Effective boolean value in XSLT's own evaluator, for the types its switch was missing.
///
/// A value of xs:string or a type DERIVED from it is true iff non-empty. The subtype case was
/// absent, and XSpec's compiler failed on 2.4.0 (xslt#191) once fn:prefix-from-QName returned the
/// xs:NCName the spec requires. The XQuery engine's copy of the same switch also handled float and
/// the typed integers (xs:short, xs:byte, …); this copy did not, so `test="xs:float(1)"` raised
/// FORG0006 here and nowhere else.
/// </summary>
public class EffectiveBooleanValueTypeTests
{
    private static async Task<string> TestAsync(string test)
    {
        var xsl = $$"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="xs">
              <xsl:template name="xsl:initial-template">
                <out><xsl:choose><xsl:when test="{{test}}">T</xsl:when><xsl:otherwise>F</xsl:otherwise></xsl:choose></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        t.SetInitialTemplate("xsl:initial-template");
        return await t.TransformAsync("<dummy/>");
    }

    [Theory]
    [InlineData("xs:NCName('a')", "T")]
    [InlineData("xs:token('')", "F")]
    [InlineData("xs:language('en')", "T")]
    [InlineData("xs:float(1)", "T")]
    [InlineData("xs:float(0)", "F")]
    [InlineData("xs:short(1)", "T")]
    [InlineData("xs:byte(0)", "F")]
    public async Task The_test_has_an_effective_boolean_value(string test, string expected)
        => (await TestAsync(test)).Should().Contain($"<out>{expected}</out>");
}
