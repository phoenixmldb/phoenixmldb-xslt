using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

#pragma warning disable CA2007 // xUnit runs tests without a synchronization context

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Each simple type of an imported schema has a constructor function of its name (XSLT 3.0
/// §3.15, XPath 3.1 §3.1.5.1). A stylesheet could cast to such a type and could not call its
/// constructor: the function was "not found".
/// </summary>
public sealed class SchemaTypeConstructorFunctionTests
{
    private static async Task<string> RunAsync(string select)
    {
        try
        {
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync($$"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:t="urn:t"
                                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
                  <xsl:output omit-xml-declaration="yes"/>
                  <xsl:import-schema namespace="urn:t">
                    <xs:schema targetNamespace="urn:t" xmlns="urn:t">
                      <xs:simpleType name="size"><xs:restriction base="xs:integer"><xs:maxInclusive value="9"/></xs:restriction></xs:simpleType>
                      <xs:simpleType name="sizes"><xs:list itemType="size"/></xs:simpleType>
                    </xs:schema>
                  </xsl:import-schema>
                  <xsl:template match="/"><out><xsl:value-of select="{{select}}"/></out></xsl:template>
                </xsl:stylesheet>
                """, new Uri("urn:test:main.xsl"));
            return (await transformer.TransformAsync("<x/>")).Trim();
        }
        catch (Exception e) when (e is XsltException or PhoenixmlDb.XQuery.Execution.XQueryRuntimeException)
        {
            return "error: " + e.Message;
        }
    }

    [Theory]
    [InlineData("t:size('8') + 1", "9")]
    [InlineData("count(t:size(()))", "0")]
    [InlineData("count(t:sizes('1 2 3'))", "3")]
    [InlineData("t:size#1('7')", "7")]
    [InlineData("function-lookup(xs:QName('t:size'), 1)('6')", "6")]
    [InlineData("let $f := t:size(?) return $f('3')", "3")]
    [InlineData("('1', '2') ! t:size(.)", "1 2")]
    [InlineData("function-available('t:size')", "true")]
    [InlineData("function-available('t:size', 1)", "true")]
    [InlineData("function-available('t:size', 2)", "false")]
    [InlineData("function-available('t:nothing')", "false")]
    public async Task The_constructor_function_of_an_imported_simple_type_can_be_called(string select, string expected)
        => (await RunAsync(select)).Should().Be($"<out>{expected}</out>");

    /// <summary>The value is an instance of the type it was made as, and of what that restricts.</summary>
    [Theory]
    [InlineData("t:size('8') instance of t:size", "true")]
    [InlineData("('8' cast as t:size) instance of t:size", "true")]
    [InlineData("t:size('8') instance of xs:integer", "true")]
    [InlineData("8 instance of t:size", "false")]
    [InlineData("(t:size('8') + 0) instance of t:size", "false")]
    [InlineData("let $s := t:size('8') return $s instance of t:size", "true")]
    [InlineData("every $s in t:sizes('1 2 3') satisfies $s instance of t:size", "true")]
    public async Task A_value_made_as_a_schema_type_is_an_instance_of_it(string select, string expected)
        => (await RunAsync(select)).Should().Be($"<out>{expected}</out>");

    [Fact]
    public async Task A_value_the_type_does_not_allow_is_an_error()
        => (await RunAsync("t:size('80')")).Should().StartWith("error:").And.Contain("80");

    [Fact]
    public async Task A_name_that_is_no_type_of_the_schema_is_still_not_a_function()
        => (await RunAsync("t:nothing('1')")).Should().StartWith("error:").And.Contain("nothing");
}
