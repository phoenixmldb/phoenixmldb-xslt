using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Under a streamable mode, a <c>match="/"</c> template whose body aggregates over the stream —
/// <c>count(//x)</c>, <c>sum(//x)</c> — answers from the whole document. Its body was treated
/// as literal-only and run before the streaming pass, against watchers that had seen nothing:
/// <c>count(//PRICE)</c> was 0 and <c>sum(//PRICE)</c> empty, whatever the input held. The
/// W3C streaming sets did not notice because they aggregate inside xsl:source-document, a
/// different entry path; this is the plain one — the CLI streams a file this way by default.
/// </summary>
public sealed class StreamingDocumentAggregateTests
{
    private static async Task<string> RunAsync(string select, string source, bool streamed)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:mode streamable="yes"/>
              <xsl:template match="/"><xsl:value-of select="{select}"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var result = streamed
            ? await t.TransformAsync(new StringReader(source))
            : await t.TransformAsync(source);
        return result.Trim();
    }

    private const string Prices = "<doc><ITEM><PRICE>2</PRICE></ITEM><PRICE>3</PRICE></doc>";
    private const string NoPrices = "<doc><ITEM/></doc>";

    [Theory]
    [InlineData("count(//PRICE)", Prices, "2")]
    [InlineData("sum(//PRICE)", Prices, "5")]
    [InlineData("count(//*)", Prices, "4")]
    [InlineData("max(//PRICE)", Prices, "3")]
    [InlineData("sum(/doc/PRICE)", Prices, "3")]
    // fn:sum(()) is 0, not the empty sequence; the two-argument form keeps its default.
    [InlineData("sum(//PRICE)", NoPrices, "0")]
    [InlineData("sum(//PRICE, 7)", NoPrices, "7")]
    [InlineData("count(//PRICE)", NoPrices, "0")]
    public async Task ADocumentTemplateAggregate_SeesTheWholeStream(string select, string source, string expected)
    {
        (await RunAsync(select, source, streamed: true)).Should().Be(expected);
        (await RunAsync(select, source, streamed: false)).Should().Be(expected);
    }
}
