using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The initial function must be public or final (XTDE0041). An xsl:function with no visibility
/// attribute is private, in a stylesheet as in a package (XSLT 3.0 §3.5.3.1); Saxon-HE 12.10 refuses
/// it too ("Cannot invoke function private#2 externally, because it is not public"). It was
/// invoked (W3C initial-function-905).
/// </summary>
public sealed class InitialFunctionVisibilityTests
{
    private static async Task<string> Call(string visibility)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:my="http://my.net/" exclude-result-prefixes="my">
              <xsl:function name="my:f" {visibility}>
                <xsl:param name="a"/>
                <r><xsl:value-of select="$a"/></r>
              </xsl:function>
            </xsl:stylesheet>
            """);
        t.SetInitialFunction("f", "http://my.net/");
        t.AddInitialFunctionArgument(7);
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("""visibility="public" """)]
    [InlineData("""visibility="final" """)]
    public async Task A_public_or_final_function_can_be_the_initial_function(string visibility)
        => (await Call(visibility)).Should().Contain("<r>7</r>");

    [Theory]
    [InlineData("")]
    [InlineData("""visibility="private" """)]
    public async Task A_function_with_no_visibility_or_private_visibility_is_XTDE0041(string visibility)
    {
        var act = () => Call(visibility);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE0041");
    }
}
