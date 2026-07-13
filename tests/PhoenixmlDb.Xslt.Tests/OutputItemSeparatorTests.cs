using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// §5.7.2 sequence normalization honours the <c>item-separator</c> serialization
/// parameter declared on the principal <c>xsl:output</c>. When a sequence of atomic
/// values is serialized, the specified (non-absent) <c>item-separator</c> string is
/// inserted between every pair of adjacent items with NO extra whitespace; when it is
/// absent, adjacent atomic values fall back to the legacy single space.
///
/// Mirrors W3C decl/output cases output-0703 / output-0709 / output-0718 / output-0719.
/// </summary>
public class OutputItemSeparatorTests
{
    private static async Task<string> Transform(string stylesheet)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet, new Uri(Path.GetTempPath() + "/"));
        transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return await transformer.TransformAsync((string?)null);
    }

    private static string Sheet(string outputAttrs) => $$"""
        <xsl:transform xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
          <xsl:output {{outputAttrs}}/>
          <xsl:template name="xsl:initial-template">
            <xsl:sequence select="11 to 20"/>
          </xsl:template>
        </xsl:transform>
        """;

    [Fact]
    public async Task ItemSeparator_Tilde_MethodText_BuildTreeNo()
    {
        // output-0703
        var result = await Transform(Sheet("method=\"text\" build-tree=\"no\" indent=\"no\" item-separator=\"~\""));
        result.Should().Be("11~12~13~14~15~16~17~18~19~20");
    }

    [Fact]
    public async Task ItemSeparator_Tilde_MethodText_BuildTreeYes()
    {
        // output-0709
        var result = await Transform(Sheet("method=\"text\" build-tree=\"yes\" indent=\"no\" item-separator=\"~\""));
        result.Should().Be("11~12~13~14~15~16~17~18~19~20");
    }

    [Fact]
    public async Task ItemSeparator_ThreeSpaces_MethodText()
    {
        // output-0718 — item-separator set to three spaces (whitespace)
        var result = await Transform(Sheet("method=\"text\" build-tree=\"yes\" indent=\"no\" item-separator=\"   \""));
        result.Should().Be("11   12   13   14   15   16   17   18   19   20");
    }

    [Fact]
    public async Task ItemSeparator_Newline_MethodText()
    {
        // output-0719 — item-separator set to a newline
        var result = await Transform(Sheet("method=\"text\" build-tree=\"yes\" indent=\"no\" item-separator=\"&#x0a;\""));
        result.Should().Be("11\n12\n13\n14\n15\n16\n17\n18\n19\n20");
    }

    [Fact]
    public async Task ItemSeparator_Absent_DefaultsToSingleSpace()
    {
        // Guard against over-correction: with NO item-separator, adjacent atomic
        // values fall back to the legacy single space.
        var result = await Transform(Sheet("method=\"text\" indent=\"no\""));
        result.Should().Be("11 12 13 14 15 16 17 18 19 20");
    }
}
