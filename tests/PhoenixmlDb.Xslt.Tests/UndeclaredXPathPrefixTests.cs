using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT predeclares no namespace prefixes for XPath: a prefixed function or variable name must use a prefix the
/// stylesheet declares, or it is a static error, XPST0081 (W3C namespace-6202, "Namespace fn is not predeclared").
/// 2.0.0 accepted fn:current-dateTime() with no xmlns:fn, because the namespace walker left XQuery's predeclared
/// prefixes to the XQuery layer, which resolved them (xslt#109). Before that, 1.8.0 raised XTSE0280, the code for a
/// QName in an XSLT attribute. The declared and unprefixed tests are guards.
/// </summary>
public sealed class UndeclaredXPathPrefixTests
{
    private static async Task<string> RunAsync(string body, string namespaces = "")
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" {{namespaces}} exclude-result-prefixes="#all">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/">{{body}}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return await t.TransformAsync("<doc/>");
    }

    private static async Task<string> ErrorOfAsync(string body, string namespaces = "")
    {
        var act = () => RunAsync(body, namespaces);
        return (await act.Should().ThrowAsync<Exception>()).Which.Message;
    }

    [Fact]
    public async Task An_undeclared_fn_prefix_in_an_attribute_value_template_is_XPST0081()
        => (await ErrorOfAsync("""<out at="{fn:current-dateTime()}"/>""")).Should().Contain("XPST0081");

    [Fact]
    public async Task An_undeclared_xs_prefix_inside_a_map_constructor_is_XPST0081()
        => (await ErrorOfAsync("""<xsl:value-of select="map { 'v': xs:double(1) }?v"/>""")).Should().Contain("XPST0081");

    [Fact]
    public async Task An_undeclared_prefix_on_a_variable_reference_is_XPST0081()
        => (await ErrorOfAsync("""<xsl:value-of select="$p:v"/>""")).Should().Contain("XPST0081");

    [Fact]
    public async Task A_declared_fn_prefix_resolves()
        => (await RunAsync("""<out><xsl:value-of select="fn:string-length('abc')"/></out>""",
                "xmlns:fn=\"http://www.w3.org/2005/xpath-functions\"")).Should().Contain("<out>3</out>");

    [Fact]
    public async Task A_declared_xs_prefix_inside_a_map_constructor_resolves()
        => (await RunAsync("""<out><xsl:value-of select="map { 'v': xs:double(1.5) }?v"/></out>""",
                "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"")).Should().Contain("<out>1.5</out>");

    [Fact]
    public async Task An_unprefixed_function_uses_the_default_function_namespace()
        => (await RunAsync("""<out><xsl:value-of select="string-length('abcd')"/></out>""")).Should().Contain("<out>4</out>");
}
