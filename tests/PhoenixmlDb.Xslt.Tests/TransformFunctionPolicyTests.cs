using FluentAssertions;
using PhoenixmlDb.XQuery.Security;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A host can turn <c>fn:transform</c> off for a stylesheet
/// (<see cref="ResourcePolicy.AllowTransformFunction"/>). Every way to call it then fails with
/// FOXT0001 before the nested stylesheet is read, and <c>function-available</c> and
/// <c>function-lookup</c> do not report the function, at run time or in a static expression.
/// It is on unless turned off.
/// </summary>
public class TransformFunctionPolicyTests
{
    private const string Nested =
        "&lt;xsl:stylesheet version=&quot;3.0&quot; xmlns:xsl=&quot;http://www.w3.org/1999/XSL/Transform&quot;&gt;&lt;xsl:template name=&quot;xsl:initial-template&quot;&gt;&lt;ran/&gt;&lt;/xsl:template&gt;&lt;/xsl:stylesheet&gt;";

    private static ResourcePolicy Off => ResourcePolicy.CreateBuilder().AllowScheme("*").AllowTransformFunction(false).Build();

    private static ResourcePolicy On => ResourcePolicy.CreateBuilder().AllowScheme("*").Build();

    private static async Task<string> RunAsync(string body, ResourcePolicy? policy, string declarations = "")
    {
        var t = new XsltTransformer { ResourcePolicy = policy };
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema"
                xmlns:map="http://www.w3.org/2005/xpath-functions/map" exclude-result-prefixes="#all" version="3.0" expand-text="yes">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:variable name="options" select="map { 'stylesheet-text': '{{Nested}}', 'delivery-format': 'serialized' }"/>
              {{declarations}}
              <xsl:template match="/"><out>{{body}}</out></xsl:template>
            </xsl:stylesheet>
            """);
        try
        {
            return await t.TransformAsync("<in/>");
        }
        catch (Exception e) when (e.Message.Contains("FOXT0001", StringComparison.Ordinal) || e.Message.Contains("resource policy", StringComparison.Ordinal))
        {
            return "REFUSED " + e.Message;
        }
    }

    public static TheoryData<string> CallForms => new()
    {
        "{transform($options)?output}",
        "<xsl:value-of select=\"transform($options)?output\"/>",
        "<xsl:variable name=\"f\" select=\"transform#1\"/>{$f($options)?output}",
        "{transform(?)($options)?output}",
        "{apply(transform#1, [$options])?output}",
        "{($options ! transform(.))?output}",
        "{($options => transform())?output}",
        "<xsl:evaluate xpath=\"'transform($o)?output'\"><xsl:with-param name=\"o\" select=\"$options\"/></xsl:evaluate>",
    };

    [Theory]
    [MemberData(nameof(CallForms))]
    public async Task Turned_off_every_call_form_is_refused(string body)
        => (await RunAsync(body, ResourcePolicy.CreateBuilder().AllowScheme("*").AllowXslEvaluate().AllowTransformFunction(false).Build()))
            .Should().StartWith("REFUSED").And.Contain("resource policy");

    [Theory]
    [MemberData(nameof(CallForms))]
    public async Task By_default_every_call_form_runs(string body)
        => (await RunAsync(body, ResourcePolicy.CreateBuilder().AllowScheme("*").AllowXslEvaluate().Build()))
            .Should().Contain("ran");

    [Fact]
    public async Task With_no_policy_it_runs()
        => (await RunAsync("{transform($options)?output}", null)).Should().Contain("ran");

    [Theory]
    [InlineData("{function-available('transform')}")]
    [InlineData("{function-available('transform', 1)}")]
    [InlineData("{function-available('fn:transform', 1)}")]
    [InlineData("{exists(function-lookup(xs:QName('fn:transform'), 1))}")]
    public async Task Turned_off_the_function_is_not_reported(string body)
    {
        (await RunAsync(body, Off)).Should().Be("<out>false</out>");
        (await RunAsync(body, On)).Should().Be("<out>true</out>");
    }

    [Fact]
    public async Task Turned_off_a_static_expression_does_not_see_the_function()
    {
        const string declarations = "<xsl:variable name=\"seen\" select=\"'yes'\" use-when=\"function-available('transform')\"/><xsl:variable name=\"seen\" select=\"'no'\" use-when=\"not(function-available('transform'))\"/>";
        (await RunAsync("{$seen}", Off, declarations)).Should().Be("<out>no</out>");
        (await RunAsync("{$seen}", On, declarations)).Should().Be("<out>yes</out>");
    }

    [Fact]
    public async Task Turned_off_a_static_expression_cannot_call_it()
    {
        var declarations = $"<xsl:variable name=\"s\" static=\"yes\" select=\"exists(transform(map {{ 'stylesheet-text': '{Nested}' }}))\"/>";
        (await RunAsync("{$s}", Off, declarations)).Should().StartWith("REFUSED").And.Contain("resource policy");
        (await RunAsync("{$s}", On, declarations)).Should().Be("<out>true</out>");
    }

    [Fact]
    public async Task Another_function_is_reported_as_before()
        => (await RunAsync("{function-available('abs')}{exists(function-lookup(xs:QName('fn:abs'), 1))}", Off)).Should().Be("<out>truetrue</out>");
}
