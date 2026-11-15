using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// SetInitialTemplate("my:temp", uri) names the template in that namespace. The prefix was
/// resolved through the stylesheet's own bindings instead, so a stylesheet whose "my" means
/// another namespace ran its my:temp; the right answer is XTDE0040 (W3C call-template-0105).
/// </summary>
public sealed class InitialTemplateNamespaceTests
{
    private const string Stylesheet = """
        <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
          <xsl:output method="text"/>
          <xsl:template name="my:temp" xmlns:my="urn:other">other</xsl:template>
          <xsl:template name="my:temp" xmlns:my="urn:wanted">wanted</xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<string> Run(string name, string? ns)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(Stylesheet);
        t.SetInitialTemplate(name, ns);
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("my:temp", "urn:wanted", "wanted")]
    [InlineData("x:temp", "urn:wanted", "wanted")]
    [InlineData("my:temp", "urn:other", "other")]
    [InlineData("Q{urn:wanted}temp", null, "wanted")]
    public async Task The_name_is_taken_in_the_namespace_given(string name, string? ns, string expected)
        => (await Run(name, ns)).Should().Be(expected);

    [Theory]
    [InlineData("my:temp", "urn:none")]
    [InlineData("temp", "urn:wanted-not")]
    public async Task A_name_no_template_has_is_XTDE0040(string name, string ns)
    {
        var act = () => Run(name, ns);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE0040");
    }
}
