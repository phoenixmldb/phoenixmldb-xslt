using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An attribute made by xsl:attribute type="T" keeps its type annotation inside an element under
/// construction (xslt#439). The element was written as text and read back, and the text has no
/// annotations: $u/@id instance of attribute(*, xs:ID) was false. An attribute made with no
/// parent element kept its annotation.
/// </summary>
public sealed class AttributeTypeInElementTests
{
    private static async Task<string> RunAsync(string declarations, string body)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:output method="text"/>
              {declarations}
              <xsl:template name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return (await transformer.TransformAsync((string?)null)).Trim();
    }

    private const string Attr = """<xsl:attribute name="id" type="xs:ID">A</xsl:attribute>""";
    private const string Test = """<xsl:value-of select="$u//@id instance of attribute(*, xs:ID)"/>""";

    [Theory]
    // as="element()" with a literal result element
    [InlineData("", $"""<xsl:variable name="u" as="element()"><e1>{Attr}</e1></xsl:variable>{Test}""")]
    // a temporary tree
    [InlineData("", $"""<xsl:variable name="u"><e1>{Attr}</e1></xsl:variable>{Test}""")]
    // xsl:element
    [InlineData("", $"""<xsl:variable name="u" as="element()"><xsl:element name="e1">{Attr}</xsl:element></xsl:variable>{Test}""")]
    // nested
    [InlineData("", $"""<xsl:variable name="u"><a><b>{Attr}</b></a></xsl:variable>{Test}""")]
    // a global variable
    [InlineData($"""<xsl:variable name="u"><e1>{Attr}</e1></xsl:variable>""", Test)]
    // no parent element: kept already
    [InlineData("", $"""<xsl:variable name="u" as="attribute()">{Attr}</xsl:variable><xsl:value-of select="$u instance of attribute(*, xs:ID)"/>""")]
    public async Task The_annotation_is_kept(string declarations, string body)
        => (await RunAsync(declarations, body)).Should().Be("true");

    [Fact]
    public async Task The_typed_value_is_of_the_type()
        => (await RunAsync("", $"""<xsl:variable name="u"><e1><xsl:attribute name="n" type="xs:integer">007</xsl:attribute></e1></xsl:variable><xsl:value-of select="data($u/e1/@n) instance of xs:integer, data($u/e1/@n)"/>"""))
            .Should().Be("true 7");

    [Theory]
    [InlineData("""<xsl:variable name="v" as="attribute(*, xs:ID)" select="$u/e1/@id"/>""")]
    [InlineData("""<xsl:variable name="v" as="attribute(id, xs:ID)" select="$u/e1/@id"/>""")]
    public async Task As_attribute_of_the_type_accepts_it(string variable)
        => (await RunAsync("", $"""<xsl:variable name="u"><e1>{Attr}</e1></xsl:variable>{variable}<xsl:value-of select="string($v)"/>"""))
            .Should().Be("A");

    [Fact]
    public async Task As_attribute_of_another_type_is_XTTE0570()
    {
        var act = () => RunAsync("", $"""<xsl:variable name="u"><e1>{Attr}</e1></xsl:variable><xsl:variable name="v" as="attribute(*, xs:integer)" select="$u/e1/@id"/><xsl:value-of select="$v"/>""");
        (await act.Should().ThrowAsync<Engine.XsltException>()).Which.Message.Should().Contain("XTTE0570");
    }

    [Fact]
    public async Task An_untyped_attribute_stays_untyped()
        => (await RunAsync("", """<xsl:variable name="u"><e1 id="A"/></xsl:variable><xsl:value-of select="$u//@id instance of attribute(*, xs:untypedAtomic)"/>"""))
            .Should().Be("true");
}
