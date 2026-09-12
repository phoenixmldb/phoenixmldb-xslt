using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// fn:transform with delivery-format="raw" hands back the nodes the inner transform produced.
/// A template that CONSTRUCTS nodes writes them to the output buffer rather than the sequence
/// collector, so the raw path had nothing typed to return and fell back to the serialized text —
/// making ?output a string, which copy-of then emitted as escaped markup (W3C transform-005/006).
/// </summary>
public sealed class TransformRawDeliveryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phoenixml-transform-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<string> RunAsync(string deliveryFormat, string innerBody)
    {
        var inner = Path.Combine(_dir, "inner.xsl");
        await File.WriteAllTextAsync(inner, $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="main">{innerBody}</xsl:template>
            </xsl:stylesheet>
            """).ConfigureAwait(true);
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/"><out><xsl:copy-of select="transform(map {
                'stylesheet-location':'{{new Uri(inner).AbsoluteUri}}',
                'initial-template':QName('','main'),
                'delivery-format':'{{deliveryFormat}}' })?output"/></out></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc/>")).Trim();
    }

    [Fact]
    public async Task RawDelivery_OfAConstructedElement_IsANode()
        => (await RunAsync("raw", "<in/>")).Should().Be("<out><in/></out>",
            "raw delivery returned the markup as a string, so copy-of escaped it");

    [Fact]
    public async Task RawDelivery_MatchesDocumentDelivery_ForTheSameTransform()
        => (await RunAsync("raw", "<in><a>1</a></in>")).Should().Be(await RunAsync("document", "<in><a>1</a></in>"));

    /// <summary>Text with no markup has no node to recover — the string is the result.</summary>
    [Fact]
    public async Task RawDelivery_OfText_IsStillText()
        => (await RunAsync("raw", "hello")).Should().Be("<out>hello</out>");

    /// <summary>
    /// A secondary result document comes back as its own nodes, and as ITS OWN content. Two
    /// defects sat on top of each other here: the serialized document carries an XML declaration
    /// that the parse wrapper cannot contain, so the parse failed and returned the markup as a
    /// string; and once it parsed, it was parsed into a fresh node store the surrounding
    /// evaluation does not consult, so the node resolved its children against the caller's store
    /// and the secondary result reported the PRIMARY result's content (W3C transform-008).
    /// </summary>
    [Fact]
    public async Task RawDelivery_SecondaryResult_KeepsItsOwnContent()
    {
        var inner = Path.Combine(_dir, "secondary.xsl");
        await File.WriteAllTextAsync(inner, """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template name="main">
                <xsl:result-document href="http://www.example.com/out.xml"><out>479</out></xsl:result-document>
                <a>892</a>
              </xsl:template>
            </xsl:stylesheet>
            """).ConfigureAwait(true);
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template match="/">
                <xsl:variable name="r" select="transform(map {
                  'stylesheet-location':'{{new Uri(inner).AbsoluteUri}}',
                  'initial-template':QName('','main'),
                  'delivery-format':'raw' })"/>
                <result><primary><xsl:sequence select="$r?output"/></primary
                  ><secondary><xsl:sequence select="$r('http://www.example.com/out.xml')"/></secondary></result>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        (await t.TransformAsync("<doc/>")).Trim()
            .Should().Be("<result><primary><a>892</a></primary><secondary><out>479</out></secondary></result>");
    }

    [Fact]
    public async Task SerializedDelivery_IsStillAString()
        => (await RunAsync("serialized", "<in/>")).Should().Contain("&lt;in/&gt;");
}
