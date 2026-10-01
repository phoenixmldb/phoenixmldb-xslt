using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A map in the principal result reaches the serializer, which cannot represent it with the xml
/// method: SENR0001 (W3C output-0710..0712). Inside a constructed element it is still the tree
/// construction error XTDE0450.
/// </summary>
public class TopLevelMapSerializationTests
{
    private static async Task<string> ErrorOf(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml"/>
              <xsl:template name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        var act = () => t.TransformAsync((string?)null);
        return (await act.Should().ThrowAsync<Exception>()).Which.Message[..8];
    }

    [Fact]
    public async Task A_map_in_the_principal_result_is_a_serialization_error() =>
        (await ErrorOf("<xsl:sequence select=\"map{'a': 1}\"/>")).Should().Be("SENR0001");

    [Fact]
    public async Task A_map_in_element_content_is_a_tree_construction_error() =>
        (await ErrorOf("<out><xsl:sequence select=\"map{'a': 1}\"/></out>")).Should().Be("XTDE0450");
}
