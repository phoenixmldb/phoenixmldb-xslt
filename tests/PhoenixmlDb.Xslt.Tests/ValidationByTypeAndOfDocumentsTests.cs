using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The <c>type</c> attribute of <c>xsl:element</c>, <c>xsl:document</c> and
/// <c>xsl:result-document</c>, and <c>xsl:type</c> on a literal result element, validate the
/// node against that type (XTTE1540). A document node that is validated has one element child
/// (XTTE1550) and satisfies the constraints on ID and IDREF (XTTE1555). XSLT 3.0 §25.4. The
/// type attribute was read and dropped, and the document rules were not applied (xslt#354).
/// </summary>
public sealed class ValidationByTypeAndOfDocumentsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-validation-type").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> RunAsync(string body)
    {
        var xsd = Path.Combine(_dir, "order.xsd");
        await File.WriteAllTextAsync(xsd, """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:o" xmlns:o="urn:o" elementFormDefault="qualified">
              <xs:element name="order" type="o:orderType"/>
              <xs:complexType name="orderType"><xs:sequence><xs:element name="qty" type="xs:integer"/></xs:sequence></xs:complexType>
              <xs:element name="list"><xs:complexType><xs:sequence>
                <xs:element name="item" maxOccurs="unbounded"><xs:complexType>
                  <xs:attribute name="id" type="xs:ID"/><xs:attribute name="ref" type="xs:IDREF"/></xs:complexType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """, TestContext.Current.CancellationToken);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:o="urn:o" xmlns:err="http://www.w3.org/2005/xqt-errors"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0" expand-text="yes" exclude-result-prefixes="#all">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:import-schema namespace="urn:o" schema-location="{xsd.Replace("\\", "/", StringComparison.Ordinal)}"/>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync("<in/>");
    }

    private async Task<string> CodeAsync(string body)
    {
        var run = () => RunAsync(body);
        var message = (await run.Should().ThrowAsync<XsltException>()).Which.Message;
        return message[..message.IndexOf(':', StringComparison.Ordinal)];
    }

    [Theory]
    // not valid against the type: a literal result element, xsl:element, in the result and in a variable
    [InlineData("<any xsl:type=\"o:orderType\"><o:qty>x</o:qty></any>")]
    [InlineData("<any xsl:type=\"o:orderType\"/>")]
    [InlineData("<xsl:element name=\"any\" type=\"o:orderType\"><o:qty>x</o:qty></xsl:element>")]
    [InlineData("<xsl:element name=\"n\" type=\"xs:integer\">x</xsl:element>")]
    [InlineData("<w><any xsl:type=\"o:orderType\"><o:qty>x</o:qty></any></w>")]
    [InlineData("<xsl:variable name=\"v\" as=\"element()\"><any xsl:type=\"o:orderType\"><o:qty>x</o:qty></any></xsl:variable><r>{name($v)}</r>")]
    [InlineData("<xsl:variable name=\"v\"><any xsl:type=\"o:orderType\"><o:qty>x</o:qty></any></xsl:variable><r>{name($v/*)}</r>")]
    // the declared element is valid by its declaration, and not by the type asked for
    [InlineData("<o:order xsl:type=\"xs:integer\"><o:qty>1</o:qty></o:order>")]
    // xsl:document
    [InlineData("<xsl:variable name=\"d\"><xsl:document type=\"o:orderType\"><any><o:qty>x</o:qty></any></xsl:document></xsl:variable><r/>")]
    public async Task A_node_that_is_not_valid_against_the_type_is_XTTE1540(string body)
        => (await CodeAsync(body)).Should().Be("XTTE1540");

    [Fact]
    public async Task A_stylesheet_catches_it_by_that_code()
        => (await RunAsync("<xsl:try><any xsl:type=\"o:orderType\"><o:qty>x</o:qty></any><xsl:catch errors=\"err:XTTE1540\">caught</xsl:catch></xsl:try>"))
            .Should().Be("caught");

    [Theory]
    [InlineData("<any xsl:type=\"o:orderType\"><o:qty>1</o:qty></any>", "<any><o:qty xmlns:o=\"urn:o\">1</o:qty></any>")]
    [InlineData("<xsl:element name=\"n\" type=\"xs:integer\">12</xsl:element>", "<n>12</n>")]
    // the declaration of the element is not asked: its content is valid for the type
    [InlineData("<o:order xsl:type=\"xs:integer\">12</o:order>", "<o:order xmlns:o=\"urn:o\">12</o:order>")]
    public async Task A_node_that_is_valid_against_the_type_is_written_as_it_is(string body, string expected)
        => (await RunAsync(body)).Should().Be(expected);

    [Theory]
    [InlineData("as=\"element()\"", "$v")]
    [InlineData("", "$v/*")]
    public async Task In_a_variable_the_element_has_the_type_and_its_content_is_typed(string asAttribute, string element)
        => (await RunAsync($"<xsl:variable name=\"v\" {asAttribute}><any xsl:type=\"o:orderType\"><o:qty>1</o:qty></any></xsl:variable>"
            + $"<r>{{{element} instance of element(*, o:orderType)}} {{{element}/o:qty instance of element(*, xs:integer)}} {{{element}/o:qty + 1}} {{count({element}/@*)}}</r>"
            + $"<xsl:copy-of select=\"{element}\"/>"))
            .Should().Be("<r>true true 2 0</r><any><o:qty xmlns:o=\"urn:o\">1</o:qty></any>");

    [Fact]
    public async Task A_type_that_no_schema_declares_is_an_error()
        => (await CodeAsync("<any xsl:type=\"o:nope\"/>")).Should().Be("XTSE1520");

    [Theory]
    // two elements, no element, and text beside the element
    [InlineData("<o:order><o:qty>1</o:qty></o:order><o:order><o:qty>2</o:qty></o:order>")]
    [InlineData("<xsl:comment>none</xsl:comment>")]
    [InlineData("text<o:order><o:qty>1</o:qty></o:order>")]
    public async Task A_validated_document_without_exactly_one_element_child_is_XTTE1550(string content)
        => (await CodeAsync($"<xsl:variable name=\"d\"><xsl:document validation=\"strict\">{content}</xsl:document></xsl:variable><r/>"))
            .Should().Be("XTTE1550");

    [Fact]
    public async Task Comments_and_processing_instructions_beside_the_element_are_allowed()
        => (await RunAsync("<xsl:variable name=\"d\"><xsl:document validation=\"strict\"><xsl:comment>c</xsl:comment><o:order><o:qty>1</o:qty></o:order><xsl:processing-instruction name=\"p\"/></xsl:document></xsl:variable><r>{count($d//o:qty)}</r>"))
            .Should().Be("<r>1</r>");

    [Fact]
    public async Task A_document_that_is_not_validated_may_have_any_children()
        => (await RunAsync("<xsl:variable name=\"d\"><xsl:document>a<x/><y/></xsl:document></xsl:variable><r>{count($d/*)}</r>"))
            .Should().Be("<r>2</r>");

    [Theory]
    // an IDREF to no ID, and the same ID twice
    [InlineData("<o:item ref=\"nowhere\"/>")]
    [InlineData("<o:item id=\"a\"/><o:item id=\"a\"/>")]
    public async Task A_validated_document_that_breaks_an_ID_constraint_is_XTTE1555(string items)
        => (await CodeAsync($"<xsl:variable name=\"d\"><xsl:document validation=\"strict\"><o:list>{items}</o:list></xsl:document></xsl:variable><r/>"))
            .Should().Be("XTTE1555");

    [Fact]
    public async Task A_validated_document_with_its_IDs_in_order_passes()
        => (await RunAsync("<xsl:variable name=\"d\"><xsl:document validation=\"strict\"><o:list><o:item id=\"a\"/><o:item ref=\"a\"/></o:list></xsl:document></xsl:variable><r>{count($d//o:item)}</r>"))
            .Should().Be("<r>2</r>");

    /// <summary>
    /// The constraints on ID and IDREF are for a document. An element that is validated is not
    /// held to them (§25.4.1): the same ID twice in it was "already used as an ID".
    /// </summary>
    [Theory]
    [InlineData("<o:list xsl:validation=\"strict\"><o:item id=\"a\"/><o:item id=\"a\"/></o:list>")]
    [InlineData("<o:list xsl:validation=\"strict\"><o:item ref=\"nowhere\"/></o:list>")]
    [InlineData("<xsl:variable name=\"v\" as=\"element()\"><o:list xsl:validation=\"strict\"><o:item id=\"a\"/><o:item id=\"a\"/></o:list></xsl:variable><xsl:copy-of select=\"$v\"/>")]
    [InlineData("<xsl:variable name=\"v\"><o:list><o:item id=\"a\"/><o:item id=\"a\"/></o:list></xsl:variable><xsl:copy-of select=\"$v/*\" validation=\"strict\"/>")]
    public async Task A_validated_element_is_not_held_to_the_ID_constraints_of_a_document(string body)
        => (await RunAsync(body)).Should().StartWith("<o:list");
}
