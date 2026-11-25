using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Static expressions — <c>use-when</c>, the <c>select</c> of a static variable or parameter, a
/// shadow attribute — are evaluated by the engine's runtime evaluator only, and may call the
/// functions XSLT 3.0 §9.7 allows (F&amp;O, the xs constructors, five XSLT functions, the extension
/// functions). A hand-written evaluator did most of them before and the runtime only the rest, so
/// <c>math:pi()</c> and <c>xs:integer('2')</c> were XPST0017 in use-when, a call to <c>current()</c>
/// in a branch that was not taken went unnoticed, and a static variable that called a stylesheet
/// function was quietly computed at run time. Every expected result is Saxon-HE 10.9's.
/// </summary>
public sealed class StaticExpressionAllowListTests
{
    private const string Namespaces = """
        xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema"
        xmlns:math="http://www.w3.org/2005/xpath-functions/math" xmlns:map="http://www.w3.org/2005/xpath-functions/map"
        xmlns:array="http://www.w3.org/2005/xpath-functions/array" xmlns:xpath="http://www.w3.org/2005/xpath-functions"
        xmlns:f="urn:f" exclude-result-prefixes="#all"
        """;

    // "in" when the use-when expression is true, nothing when it is false; or the error code.
    private static async Task<string> UseWhenAsync(string expression)
        => await RunAsync($"""<xsl:sequence use-when="{expression}" select="'in'"/>""");

    private static async Task<string> RunAsync(string body, string declarations = "")
    {
        try
        {
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync($"""
                <xsl:stylesheet version="3.0" {Namespaces}>
                  <xsl:output method="text"/>
                  <xsl:function name="f:x" as="xs:string"><xsl:sequence select="'from-f'"/></xsl:function>
                  {declarations}
                  <xsl:template name="xsl:initial-template">{body}</xsl:template>
                </xsl:stylesheet>
                """);
            return await transformer.TransformAsync((string?)null);
        }
        catch (Engine.XsltException e) when (e.ErrorCode is { } code)
        {
            return code;
        }
    }

    [Theory]
    [InlineData("math:pi() gt 3")]
    [InlineData("xs:integer('2') eq 2")]
    [InlineData("map:size(map{1: 2}) eq 1 and array:size([1, 2]) eq 2")]
    [InlineData("function-available('math:pi')")]
    [InlineData("function-available('system-property', 1)")]
    public async Task A_use_when_may_call_the_functions_of_FO_and_the_xs_constructors(string expression)
        => (await UseWhenAsync(expression)).Should().Be("in");

    [Theory]
    [InlineData("function-available('f:x')")]
    [InlineData("function-available('key')")]
    [InlineData("function-available('current')")]
    public async Task In_a_use_when_a_stylesheet_function_or_a_function_that_needs_a_transformation_is_not_available(string expression)
        => (await UseWhenAsync(expression)).Should().BeEmpty();

    [Theory]
    [InlineData("f:x() eq 'from-f'", "XPST0017")]
    [InlineData("Q{urn:other}g()", "XPST0017")]
    [InlineData("if (true()) then true() else current()", "XPST0017")]
    [InlineData("1 div 0 eq 1", "FOAR0001")]
    [InlineData("/a", "XPDY0002")]
    [InlineData("function-available('test:foo')", "XTDE1400")]
    public async Task A_use_when_that_is_in_error_is_a_static_error_with_its_code(string expression, string code)
        => (await UseWhenAsync(expression)).Should().Be(code);

    [Fact]
    public async Task A_static_variable_may_not_call_a_stylesheet_function()
        => (await RunAsync("""<xsl:value-of select="$v"/>""", """<xsl:variable name="v" static="yes" select="f:x()"/>"""))
            .Should().Be("XPST0017");

    // It loaded before, the value computed at run time; the message says how to change it.
    [Theory]
    [InlineData("""<xsl:variable name="v" static="yes" select="f:x()"/>""", true)]
    [InlineData("""<xsl:param name="v" static="yes" select="f:x()"/>""", true)]
    [InlineData("""<xsl:variable name="v" select="'x'" use-when="f:x() = 'from-f'"/>""", false)]
    public async Task The_message_of_a_static_select_that_calls_a_stylesheet_function_says_what_to_change(string declaration, bool static_)
    {
        var transformer = new XsltTransformer();
        var load = () => transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" {Namespaces}>
              <xsl:function name="f:x" as="xs:string"><xsl:sequence select="'from-f'"/></xsl:function>
              {declaration}
              <xsl:template name="xsl:initial-template"><xsl:value-of select="$v"/></xsl:template>
            </xsl:stylesheet>
            """);
        var error = (await load.Should().ThrowAsync<Engine.XsltException>()).Which;
        error.ErrorCode.Should().Be("XPST0017");
        if (static_)
            error.Message.Should().Contain("remove static=\"yes\" from the declaration, or do not call the function");
        else
            error.Message.Should().NotContain("static=\"yes\"", "a use-when is not the select of a static declaration");
    }

    // The prefix of a named function reference is bound like that of a call; only predeclared
    // prefixes were known to a static expression (W3C system-property-101*-qname).
    [Theory]
    [InlineData("xpath:abs#1(-2)", "2")]
    [InlineData("xpath:system-property#1('xsl:version')", "3.0")]
    [InlineData("count(xpath:available-system-properties#0()) gt 0", "true")]
    public async Task A_named_function_reference_with_any_prefix_of_its_namespace_works_in_a_static_variable(string select, string expected)
        => (await RunAsync("""<xsl:value-of select="$v"/>""", $"""<xsl:variable name="v" static="yes" select="{select}"/>"""))
            .Should().Be(expected);

    // One evaluation context serves the static expressions of all the modules; each expression has
    // the base URI of its own element: its module's location, or an xml:base on it.
    [Fact]
    public async Task A_static_expression_has_the_base_URI_of_its_own_module()
    {
        var dir = Directory.CreateTempSubdirectory("static-base-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "sub"));
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "sub", "data.xml"), "<d/>");
            await File.WriteAllTextAsync(Path.Combine(dir.FullName, "sub", "lib.xsl"), """
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:variable name="lib-base" static="yes" select="static-base-uri()"/>
                  <xsl:template name="lib-doc" use-when="doc-available('data.xml')">lib-doc </xsl:template>
                  <xsl:template name="lib-doc" use-when="not(doc-available('data.xml'))">no-lib-doc </xsl:template>
                </xsl:stylesheet>
                """);
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync("""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:output method="text"/>
                  <xsl:include href="sub/lib.xsl"/>
                  <xsl:variable name="main-base" static="yes" select="static-base-uri()"/>
                  <xsl:template name="xsl:initial-template">
                    <xsl:value-of select="ends-with($lib-base, '/sub/lib.xsl'), ends-with($main-base, '/main.xsl')"/>
                    <xsl:text> </xsl:text>
                    <xsl:call-template name="lib-doc"/>
                    <xsl:call-template name="main-doc"/>
                    <xsl:call-template name="other"/>
                  </xsl:template>
                  <xsl:template name="main-doc" use-when="doc-available('sub/data.xml')">main-doc </xsl:template>
                  <xsl:template name="main-doc" use-when="not(doc-available('sub/data.xml'))">no-main-doc </xsl:template>
                  <xsl:template name="other" xml:base="http://example.com/other/" use-when="static-base-uri() eq 'http://example.com/other/'">xml-base</xsl:template>
                  <xsl:template name="other" xml:base="http://example.com/other/" use-when="static-base-uri() ne 'http://example.com/other/'">no-xml-base</xsl:template>
                </xsl:stylesheet>
                """, new Uri(Path.Combine(dir.FullName, "main.xsl")));
            (await transformer.TransformAsync((string?)null)).Should().Be("true true lib-doc main-doc xml-base");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
