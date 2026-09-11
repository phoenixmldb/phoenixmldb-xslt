using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An xml:id value is whitespace-normalized as an ID, so <c>xml:id="id3 "</c> is the ID
/// <c>id3</c>; and fn:id tokenizes its argument on any XML whitespace, not only the space
/// character. id() compared the raw attribute value and split on ' ' alone (W3C key-076).
/// </summary>
public sealed class XmlIdNormalizationTests
{
    private const string Source = """
        <doc><div xml:id="id3 "><title>Expressions</title></div><div xml:id="id4"><title>Four</title></div></doc>
        """;

    private static async Task<string> RunAsync(string select)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:value-of select="{select}"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync(Source)).Trim();
    }

    [Theory]
    [InlineData("id('id3')/title", "Expressions")]
    [InlineData("id(' id3')/title", "Expressions")]
    [InlineData("count(id('id3&#9;id4'))", "2")]
    [InlineData("count(id('id3&#10;id4'))", "2")]
    [InlineData("count(id(xs:untypedAtomic('id3 id4')))", "2")]
    public async Task IdFindsAnXmlIdByItsNormalizedValue(string select, string expected)
        => (await RunAsync(select.Replace("xs:untypedAtomic", "Q{http://www.w3.org/2001/XMLSchema}untypedAtomic", StringComparison.Ordinal)))
            .Should().Be(expected);
}
