using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Function coercion (XPath 3.1 §3.1.5.3): a function item bound to a typed function type is
/// wrapped, and the wrapper converts each argument to the required parameter type before the
/// function gets it. Bound to function(xs:double), function($x as xs:float) gets an xs:double and
/// cannot take it: XPTY0004, as in Saxon-HE 12.10 (W3C higher-order-functions-066). The wrapper
/// passed the argument as it was, a typed xsl:variable did not wrap at all, and an as type with
/// a trailing space ("(function(xs:double) as xs:double)* ") lost its signature.
/// </summary>
public sealed class FunctionCoercionTests
{
    private static async Task<string> Run(string declarations, string select)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:l="l" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {declarations}
              <xsl:template name="xsl:initial-template"><xsl:value-of select="{select}"/></xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return await t.TransformAsync((string?)null);
    }


    [Theory]
    [InlineData("""<xsl:variable name="f" as="function(xs:double) as xs:double" select="function($x as xs:float){$x + 1}"/>""", "$f(xs:decimal('1.5'))")]
    [InlineData("""<xsl:function name="l:f" as="function(xs:double) as xs:double"><xsl:sequence select="function($x as xs:float){$x + 1}"/></xsl:function>""", "l:f()(xs:decimal('1.5'))")]
    [InlineData("""<xsl:function name="l:f" as="(function(xs:double) as xs:double)* "><xsl:sequence select="(abs#1, function($x as xs:float){$x + 1})"/></xsl:function>""", "for $g in l:f() return $g(xs:decimal('1.5'))")]
    [InlineData("""<xsl:function name="l:apply"><xsl:param name="g" as="function(xs:double) as xs:double"/><xsl:sequence select="$g(xs:decimal('1.5'))"/></xsl:function>""", "l:apply(function($x as xs:float){$x})")]
    public async Task A_function_that_cannot_take_the_required_argument_type_fails_when_called(string declarations, string select)
    {
        var act = () => Run(declarations, select);

        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        (ex is PhoenixmlDb.XQuery.Execution.XQueryRuntimeException rt ? rt.ErrorCode : ex.Message)
            .Should().Contain("XPTY0004");
    }

    [Fact]
    public async Task The_wrapper_promotes_and_casts_arguments_to_the_required_types()
        => (await Run("""
                <xsl:variable name="ok" as="function(xs:decimal) as xs:double" select="function($x as xs:double){$x * 2}"/>
                <xsl:variable name="u" as="function(xs:integer) as xs:integer" select="function($x){$x + 1}"/>
                <xsl:variable name="d" as="function(xs:double) as xs:double" select="function($x as xs:double){$x * 2}"/>
                """, "$ok(xs:decimal('1.5')), $u(xs:untypedAtomic('4')), $d(1)"))
            .Should().Be("3 5 2");
}
