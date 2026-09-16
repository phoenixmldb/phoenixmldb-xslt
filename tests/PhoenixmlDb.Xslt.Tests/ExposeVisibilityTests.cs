using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:expose must not change the visibility of a component declared abstract (XTSE3010), and a wildcard exposure
/// must not make a matching component abstract (XTSE3025). Both fell through to the blanket XTSE3080 the parser
/// raises for any abstract component in a top-level package (W3C expose-918, -919, -920, -922).
/// </summary>
public sealed class ExposeVisibilityTests
{
    private static async Task<string> ErrorOfAsync(string declaration, string expose)
    {
        var pkg = $$"""
            <xsl:package name="http://example.com/p" package-version="1.0" version="3.0"
                         xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:f="urn:f">
              {{declaration}}
              <xsl:template name="xsl:initial-template" visibility="public"><out/></xsl:template>
              {{expose}}
            </xsl:package>
            """;
        var t = new XsltTransformer();
        try
        {
            await t.LoadStylesheetAsync(pkg);
            return "ok:" + (await t.TransformAsync("<doc/>")).Trim();
        }
        catch (XsltException ex)
        {
            return ex.Message;
        }
    }

    private const string AbstractFunction = """<xsl:function name="f:abstract" visibility="abstract"/>""";
    private const string PlainFunction = """<xsl:function name="f:plain"><xsl:sequence select="1"/></xsl:function>""";

    [Theory]
    [InlineData("public")]
    [InlineData("private")]
    [InlineData("final")]
    public async Task Exposing_an_abstract_component_by_name_is_XTSE3010(string visibility)
        => (await ErrorOfAsync(AbstractFunction,
                $"""<xsl:expose visibility="{visibility}" component="function" names="f:abstract#0"/>"""))
            .Should().StartWith("XTSE3010");

    [Fact]
    public async Task A_wildcard_exposure_making_a_component_abstract_is_XTSE3025()
        => (await ErrorOfAsync(PlainFunction,
                """<xsl:expose visibility="abstract" component="function" names="*"/>"""))
            .Should().StartWith("XTSE3025");

    [Fact]
    public async Task A_normal_exposure_is_unaffected()
        => (await ErrorOfAsync(PlainFunction,
                """<xsl:expose visibility="public" component="function" names="f:plain#0"/>"""))
            .Should().StartWith("ok:");
}
