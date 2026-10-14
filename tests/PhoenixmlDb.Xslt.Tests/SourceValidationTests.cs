using AwesomeAssertions;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <see cref="XsltTransformer.SourceValidation"/>: the source document is validated against
/// the schemas in scope before the transformation, and the stylesheet sees typed nodes. There
/// was no way to give a transformation a typed source from XML text.
/// </summary>
public sealed class SourceValidationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-source-validation").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string Valid = "<o:order xmlns:o=\"urn:o\" id=\"a1\"><o:qty>12</o:qty></o:order>";
    private const string Invalid = "<o:order xmlns:o=\"urn:o\"><o:qty>many</o:qty></o:order>";

    private async Task<XsltTransformer> LoadAsync(string body, string modeAttributes = "")
    {
        var xsd = Path.Combine(_dir, "order.xsd");
        await File.WriteAllTextAsync(xsd, """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:o" xmlns:o="urn:o" elementFormDefault="qualified">
              <xs:element name="order" type="o:orderType"/>
              <xs:complexType name="orderType">
                <xs:sequence><xs:element name="qty" type="xs:integer"/></xs:sequence>
                <xs:attribute name="id" type="xs:ID"/>
              </xs:complexType>
            </xs:schema>
            """, TestContext.Current.CancellationToken);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:o="urn:o"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0" expand-text="yes" exclude-result-prefixes="#all">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:import-schema namespace="urn:o" schema-location="{xsd.Replace("\\", "/", StringComparison.Ordinal)}"/>
              <xsl:mode {modeAttributes}/>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return t;
    }

    private const string Types = "<r>{o:order instance of schema-element(o:order)} {o:order instance of element(*, o:orderType)} "
        + "{o:order/o:qty instance of element(*, xs:integer)} {data(o:order/o:qty) instance of xs:integer} "
        + "{o:order/@id instance of attribute(*, xs:ID)} {o:order/o:qty + 1}</r>";

    [Theory]
    [InlineData(ValidationMode.Strict)]
    [InlineData(ValidationMode.Lax)]
    public async Task A_validated_source_has_typed_nodes(ValidationMode mode)
    {
        var t = await LoadAsync(Types);
        t.SourceValidation = mode;
        (await t.TransformAsync(Valid)).Should().Be("<r>true true true true true 13</r>");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ValidationMode.Strip)]
    [InlineData(ValidationMode.Preserve)]
    public async Task A_source_that_is_not_validated_is_untyped_as_before(ValidationMode? mode)
    {
        var t = await LoadAsync(Types);
        t.SourceValidation = mode;
        (await t.TransformAsync(Valid)).Should().Be("<r>false false false false false 13</r>");
    }

    [Theory]
    [InlineData(ValidationMode.Strict, Invalid, "XTTE1510")]
    [InlineData(ValidationMode.Lax, Invalid, "XTTE1515")]
    // strict: the outermost element must be declared
    [InlineData(ValidationMode.Strict, "<other/>", "XTTE1510")]
    public async Task A_source_that_is_not_valid_fails_the_transformation(ValidationMode mode, string source, string code)
    {
        var t = await LoadAsync("<r/>");
        t.SourceValidation = mode;
        var run = () => t.TransformAsync(source);
        (await run.Should().ThrowAsync<XsltException>()).Which.Message.Should().StartWith(code + ":");
    }

    [Fact]
    public async Task Lax_validation_accepts_a_source_the_schema_does_not_declare()
    {
        var t = await LoadAsync("<r>{name(*)}</r>");
        t.SourceValidation = ValidationMode.Lax;
        (await t.TransformAsync("<other/>")).Should().Be("<r>other</r>");
    }

    [Fact]
    public async Task The_source_is_what_was_written_with_nothing_added()
    {
        var t = await LoadAsync("<xsl:copy-of select=\".\"/>");
        t.SourceValidation = ValidationMode.Strict;
        (await t.TransformAsync("<!--c-->" + Valid + "<?p q?>")).Should().Be("<!--c-->" + Valid.Replace("\"", "\"", StringComparison.Ordinal) + "<?p q?>");
    }

    [Fact]
    public async Task A_validated_source_is_not_streamed_and_gives_the_same_result()
    {
        var t = await LoadAsync("<r>{o:order/o:qty instance of element(*, xs:integer)} {sum(o:order/o:qty)}</r>", "streamable=\"yes\"");
        t.SourceValidation = ValidationMode.Strict;
        (await t.TransformAsync(Valid)).Should().Be("<r>true 12</r>");
    }

    [Fact]
    public async Task A_source_with_a_document_type_declaration_is_validated()
    {
        var t = await LoadAsync("<r>{o:order/o:qty + 1}</r>");
        t.AllowDtdProcessing = true;
        t.SourceValidation = ValidationMode.Strict;
        (await t.TransformAsync("<!DOCTYPE o:order [<!ENTITY n '12'>]><o:order xmlns:o=\"urn:o\"><o:qty>&n;</o:qty></o:order>"))
            .Should().Be("<r>13</r>");
    }

    /// <summary>
    /// A validated node that is bound to a variable or a parameter with an atomic <c>as</c> type
    /// is atomized to its typed value. Its string value was converted, so an element of type
    /// xs:int became a plain xs:integer and <c>$v instance of xs:int</c> was false (W3C as-17xx).
    /// </summary>
    [Fact]
    public async Task A_typed_node_bound_with_an_atomic_as_type_keeps_its_type()
    {
        var xsd = Path.Combine(_dir, "types.xsd");
        await File.WriteAllTextAsync(xsd, """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="doc"><xs:complexType><xs:sequence>
                <xs:element name="i" type="xs:int"/><xs:element name="n" type="xs:NMTOKEN"/>
                <xs:element name="ub" type="xs:unsignedByte"/><xs:element name="d" type="xs:decimal"/>
              </xs:sequence><xs:attribute name="id" type="xs:ID"/></xs:complexType></xs:element>
            </xs:schema>
            """, TestContext.Current.CancellationToken);
        var t = new XsltTransformer { SourceValidation = ValidationMode.Strict };
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            version="3.0" expand-text="yes" exclude-result-prefixes="#all">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:import-schema schema-location="{{xsd.Replace("\\", "/", StringComparison.Ordinal)}}"/>
              <xsl:template match="/">
                <xsl:variable name="i" select="doc/i" as="xs:int"/>
                <xsl:variable name="n" select="doc/n" as="xs:NMTOKEN"/>
                <xsl:variable name="ub" select="doc/ub" as="xs:unsignedByte"/>
                <xsl:variable name="id" select="doc/@id" as="xs:ID"/>
                <xsl:variable name="wide" select="doc/i" as="xs:integer"/>
                <xsl:variable name="all" select="doc/(i, ub)" as="xs:integer*"/>
                <r>{$i instance of xs:int} {$n instance of xs:NMTOKEN} {$ub instance of xs:unsignedByte} {$id instance of xs:ID} {$wide instance of xs:int} {$all[2] instance of xs:unsignedByte} {$i instance of xs:untypedAtomic} {$i + $ub}</r>
              </xsl:template>
            </xsl:stylesheet>
            """);
        (await t.TransformAsync("<doc id=\"a1\"><i>5</i><n>tok</n><ub>3</ub><d>1.5</d></doc>"))
            .Should().Be("<r>true true true true true true false 8</r>");
    }

    [Fact]
    public async Task An_untyped_node_bound_with_an_atomic_as_type_is_cast_as_before()
    {
        var t = await LoadAsync("<xsl:variable name=\"q\" select=\"o:order/o:qty\" as=\"xs:integer\"/><r>{$q instance of xs:integer} {$q + 1}</r>");
        (await t.TransformAsync(Valid)).Should().Be("<r>true 13</r>");
    }

    /// <summary>
    /// A mode with <c>typed="yes"</c> takes no untyped element or attribute (XTTE3100, XSLT 3.0
    /// §6.6.1). The check was made in the built-in rule only and for every element, typed or
    /// not: a typed source could not be processed in such a mode, and an untyped element that
    /// a rule matched was accepted.
    /// </summary>
    [Fact]
    public async Task A_typed_mode_processes_a_validated_source()
    {
        var t = await LoadAsync("<r><xsl:apply-templates/></r>", "typed=\"yes\"");
        t.SourceValidation = ValidationMode.Strict;
        (await t.TransformAsync(Valid)).Should().Be("<r>12</r>");
    }

    [Theory]
    // the built-in rule, and a rule that matches
    [InlineData("<r><xsl:apply-templates/></r>")]
    [InlineData("<r><xsl:apply-templates select=\"*\"/></r>")]
    public async Task A_typed_mode_refuses_an_untyped_source(string body)
    {
        var t = await LoadAsync(body, "typed=\"yes\"");
        var run = () => t.TransformAsync(Valid);
        (await run.Should().ThrowAsync<XsltException>()).Which.Message.Should().StartWith("XTTE3100:");
    }

    [Fact]
    public async Task A_mode_that_is_not_typed_takes_an_untyped_source_as_before()
    {
        var t = await LoadAsync("<r><xsl:apply-templates/></r>");
        (await t.TransformAsync(Valid)).Should().Be("<r>12</r>");
    }
}
