using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The body of a function declared streamability="shallow-descent" must be striding, and
/// motionless or consuming, with a reference to its first parameter striding and motionless
/// (XSLT 3.0 §19.8.5.5); otherwise the function is not guaranteed-streamable, XTSE3430. A body
/// such as sum($n), which is grounded, was accepted (W3C su-shallow-descent-902).
/// </summary>
public sealed class ShallowDescentBodyTests
{
    private static async Task<string> Load(string function, string asAttribute = "")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f" exclude-result-prefixes="#all">
              <xsl:function name="f:g" streamability="shallow-descent" {{asAttribute}}>
                <xsl:param name="n" as="node()"/>
                <xsl:param name="k" as="xs:integer"/>
                {{function}}
              </xsl:function>
              <xsl:template match="/"><out><xsl:copy-of select="f:g(*, 2)"/></out></xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync("<r><a><b/></a><a/></r>")).Trim();
    }

    [Theory]
    [InlineData("<xsl:sequence select='$n/*'/>", "")]
    [InlineData("<xsl:sequence select='$n'/>", "")]
    [InlineData("<xsl:sequence select='if ($k gt 1) then $n/a else ()'/>", "")]
    [InlineData("<xsl:sequence select='$n/*/*'/>", "as='element()*'")]
    // Recursion: the call is classified by the declaration of the function itself.
    [InlineData("<xsl:sequence select='if ($k gt 0) then f:g($n, $k - 1) else $n/*'/>", "")]
    public async Task A_striding_body_is_accepted(string function, string asAttribute)
        => await FluentActions.Awaiting(() => Load(function, asAttribute)).Should().NotThrowAsync();

    [Theory]
    // Grounded.
    [InlineData("<xsl:sequence select='sum($n)'/>", "")]
    [InlineData("<xsl:sequence select='string($n)'/>", "")]
    // Grounded by the type adjustment: an atomic return type absorbs the result.
    [InlineData("<xsl:sequence select='$n/*'/>", "as='xs:string*'")]
    public async Task A_body_that_is_not_striding_is_XTSE3430(string function, string asAttribute)
        => (await FluentActions.Awaiting(() => Load(function, asAttribute)).Should().ThrowAsync<Exception>())
            .Which.Message.Should().Contain("XTSE3430");
}
