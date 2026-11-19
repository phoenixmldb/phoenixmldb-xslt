using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The select value of a global variable or parameter is converted to its <c>as</c> type by the
/// function conversion rules (XSLT 3.0 §9.3, XPath 3.1 §3.1.5.2). An <c>xs:untypedAtomic</c> value or
/// an untyped node is cast, numeric values are promoted to <c>xs:double</c>/<c>xs:float</c>, and
/// <c>xs:anyURI</c> is promoted to <c>xs:string</c>. Any other value of another type is XTTE0570
/// (XTTE0590 for a parameter). Every shape here gives the same result as Saxon-HE 12.10. The engine
/// cast any atomic value: <c>as="xs:integer" select="'12'"</c> gave 12.
/// </summary>
public class GlobalSelectFunctionConversionTests
{
    private static async Task<string> Run(string declaration)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:output method="text"/>
              <xsl:variable name="d" select="parse-xml('&lt;r n=&quot;12&quot;&gt;3.5&lt;/r&gt;')"/>
              {declaration}
              <xsl:template name="xsl:initial-template"><xsl:value-of select="$v"/></xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync((string?)null);
    }

    private static string Var(string asType, string select) => $"""<xsl:variable name="v" as="{asType}" select="{select}"/>""";

    [Theory]
    [InlineData("xs:integer", "$d/r/@n", "12")]
    [InlineData("xs:double", "$d/r", "3.5")]
    [InlineData("xs:double", "12", "12")]
    [InlineData("xs:double", "1.5", "1.5")]
    [InlineData("xs:float", "12", "12")]
    [InlineData("xs:decimal", "12", "12")]
    [InlineData("xs:string", "xs:anyURI('http://a/')", "http://a/")]
    [InlineData("xs:string*", "('a', 'b')", "a b")]
    [InlineData("xs:integer", "xs:untypedAtomic('7')", "7")]
    [InlineData("xs:anyAtomicType", "'x'", "x")]
    [InlineData("xs:string", "xs:untypedAtomic('u')", "u")]
    [InlineData("xs:integer?", "()", "")]
    [InlineData("xs:double", "xs:float('1.5')", "1.5")]
    [InlineData("xs:double", "xs:int(3)", "3")]
    [InlineData("xs:float", "xs:short(3)", "3")]
    [InlineData("xs:decimal", "xs:int(3)", "3")]
    [InlineData("xs:integer", "xs:int(12)", "12")]
    public async Task A_value_the_rules_convert_is_converted(string asType, string select, string expected)
        => (await Run(Var(asType, select))).Should().Be(expected);

    [Theory]
    [InlineData("xs:date", "'2026-01-01'")]
    [InlineData("xs:boolean", "'true'")]
    [InlineData("xs:boolean", "1")]
    [InlineData("xs:integer", "1.0")]
    [InlineData("xs:decimal", "1e0")]
    [InlineData("xs:integer+", "(1, '2')")]
    [InlineData("xs:anyURI", "'http://a/'")]
    [InlineData("xs:integer", "'12'")]
    [InlineData("xs:string", "12")]
    public async Task A_value_of_another_type_is_XTTE0570(string asType, string select)
    {
        var act = () => Run(Var(asType, select));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTTE0570");
    }

    [Theory]
    [InlineData("xs:double", "xs:int(3)")]
    [InlineData("xs:float", "xs:short(3)")]
    public async Task A_promoted_derived_integer_is_of_the_declared_type(string asType, string select)
        => (await Run($"""<xsl:variable name="w" as="{asType}" select="{select}"/><xsl:variable name="v" select="$w instance of {asType}"/>"""))
            .Should().Be("true");

    [Fact]
    public async Task A_parameter_default_of_another_type_is_XTTE0590()
    {
        var act = () => Run("""<xsl:param name="v" as="xs:integer" select="'12'"/>""");

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTTE0590");
    }

    [Fact]
    public async Task A_supplied_string_parameter_is_untyped_and_is_cast()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:output method="text"/>
              <xsl:param name="n" as="xs:integer" select="0"/>
              <xsl:template name="xsl:initial-template"><xsl:value-of select="$n + 1"/></xsl:template>
            </xsl:stylesheet>
            """);
        t.SetParameter("n", "41");

        (await t.TransformAsync((string?)null)).Should().Be("42");
    }
}
