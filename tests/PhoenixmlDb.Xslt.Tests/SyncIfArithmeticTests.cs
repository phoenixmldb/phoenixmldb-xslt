using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// if expressions, arithmetic and value comparisons are evaluated synchronously when their
/// operands allow it (xquery: IfOperator and BinaryOperatorNode.EvaluateSync), as an XSLT
/// instruction's XPath is. These pin the results of that path to those of the asynchronous one:
/// the empty sequence, more than one item, the first item deciding a condition, and XPath 1.0
/// backwards-compatible arithmetic.
/// </summary>
public sealed class SyncIfArithmeticTests
{
    private static async Task<string> Run(string select, string version = "3.0")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="{version}">
              <xsl:output method="text"/>
              <xsl:template name="main"><xsl:value-of select="{select}" separator="|"/></xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("for $i in 1 to 6 return if ($i mod 3 = 0) then 'x' else $i", "1|2|x|4|5|x")]
    [InlineData("(7 idiv 2, 7 mod 2, 7 div 2, 2 * 3, 5 - 8, 1 + 1)", "3|1|3.5|6|-3|2")]
    [InlineData("count(() + 1), count(() eq 1), count(1 lt ())", "0|0|0")]
    [InlineData("1 lt 2, 'a' eq 'a', 2 ge 3, 1 ne 1", "true|true|false|false")]
    [InlineData("if (()) then 'y' else 'n', if ('') then 'y' else 'n', if (0.5) then 'y' else 'n'", "n|n|y")]
    [InlineData("xs:untypedAtomic('3') + 1", "4")]
    public async Task The_values_are_those_of_the_expression(string select, string expected)
        => (await Run(select)).Should().Be(expected);

    [Theory]
    [InlineData("(1, 2) eq 1")]
    [InlineData("(1, 2) + 1")]
    public async Task More_than_one_item_is_XPTY0004(string select)
    {
        var act = () => Run(select);
        (await act.Should().ThrowAsync<PhoenixmlDb.XQuery.Execution.XQueryRuntimeException>()).Which.ErrorCode.Should().Be("XPTY0004");
    }

    [Fact]
    public async Task Backwards_compatible_arithmetic_takes_the_first_item_as_a_double()
        => (await Run("(1, 2) + '3', 'a' * 2", "1.0")).Should().Be("4|NaN");
}
