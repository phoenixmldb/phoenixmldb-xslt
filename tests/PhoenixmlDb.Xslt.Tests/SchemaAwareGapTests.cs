using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Three schema-aware gaps (xslt#316): a schema type named by prefix in XPath was XPST0081, an
/// <c>xs:schema</c> written inside <c>xsl:import-schema</c> was not read, and
/// <c>xsl:validation</c> on a literal result element was not applied.
/// </summary>
public sealed class SchemaAwareGapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-sag-" + Guid.NewGuid().ToString("N"));

    public SchemaAwareGapTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "t.xsd"), $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t" xmlns:t="urn:t" elementFormDefault="qualified">
              {SchemaBody}
            </xs:schema>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string SchemaBody = """
        <xs:simpleType name="size"><xs:restriction base="xs:integer"><xs:maxInclusive value="9"/></xs:restriction></xs:simpleType>
        <xs:element name="n" type="t:size"/>
        """;

    private const string ByLocation = """<xsl:import-schema namespace="urn:t" schema-location="t.xsd"/>""";

    // The schema's own QNames use t and xs as the stylesheet element declares them.
    private const string Inline = $"""
        <xsl:import-schema namespace="urn:t">
          <xs:schema targetNamespace="urn:t" elementFormDefault="qualified">
            {SchemaBody}
          </xs:schema>
        </xsl:import-schema>
        """;

    private async Task<string> RunAsync(string import, string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" xmlns:t="urn:t"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output omit-xml-declaration="yes"/>
              {import}
              <xsl:template name="xsl:initial-template"><out>{body}</out></xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    private async Task<string> ErrorAsync(string import, string body)
    {
        try
        {
            return "no error: " + await RunAsync(import, body);
        }
        catch (PhoenixmlDb.XQuery.Execution.XQueryRuntimeException ex)
        {
            return ex.ErrorCode + ": " + ex.Message;
        }
        catch (Exception ex) when (ex is XsltException or PhoenixmlDb.XQuery.Parser.XQueryParseException)
        {
            return ex.Message;
        }
    }

    public static TheoryData<string> Imports => new() { ByLocation, Inline };

    [Theory]
    [MemberData(nameof(Imports))]
    public async Task A_schema_type_is_named_by_a_prefix_the_stylesheet_declares(string import)
    {
        (await RunAsync(import, """<xsl:value-of select="'8' cast as t:size"/>""")).Should().Be("<out>8</out>");
        (await RunAsync(import, """<xsl:value-of select="'12' castable as t:size, '8' castable as t:size"/>"""))
            .Should().Be("<out>false true</out>");
        (await ErrorAsync(import, """<xsl:value-of select="'12' cast as t:size"/>""")).Should().Contain("FORG0001");
    }

    [Fact]
    public async Task A_prefix_the_stylesheet_does_not_declare_is_still_XPST0081()
        => (await ErrorAsync(ByLocation, """<xsl:value-of select="'8' cast as u:size"/>""")).Should().Contain("XPST0081");

    /// <summary>The prefix is taken where the expression stands, not from the stylesheet element alone.</summary>
    [Fact]
    public async Task The_prefix_may_be_declared_on_the_instruction_itself()
        => (await RunAsync(ByLocation, """<xsl:value-of xmlns:local="urn:t" select="'8' cast as local:size"/>"""))
            .Should().Be("<out>8</out>");

    [Theory]
    [MemberData(nameof(Imports))]
    public async Task Validation_on_a_literal_result_element_is_applied(string import)
    {
        (await RunAsync(import, """<t:n xsl:validation="strict">8</t:n>""")).Should().Be("""<out><t:n xmlns:t="urn:t">8</t:n></out>""");
        (await ErrorAsync(import, """<t:n xsl:validation="strict">12</t:n>""")).Should().Contain("XQDY0027");
    }

    /// <summary>The literal result element and xsl:element answer alike.</summary>
    [Theory]
    [InlineData("8")]
    [InlineData("12")]
    public async Task A_literal_result_element_validates_as_xsl_element_does(string value)
    {
        var literal = await ErrorAsync(ByLocation, $"""<t:n xsl:validation="strict">{value}</t:n>""");
        var instruction = await ErrorAsync(ByLocation, $"""<xsl:element name="t:n" validation="strict">{value}</xsl:element>""");
        literal.Contains("XQDY0027", StringComparison.Ordinal).Should().Be(instruction.Contains("XQDY0027", StringComparison.Ordinal));
        literal.StartsWith("no error", StringComparison.Ordinal).Should().Be(instruction.StartsWith("no error", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("strip")]
    [InlineData("preserve")]
    public async Task Validation_that_asks_for_no_check_makes_none(string mode)
        => (await RunAsync(ByLocation, $"""<t:n xsl:validation="{mode}">12</t:n>""")).Should().Be("""<out><t:n xmlns:t="urn:t">12</t:n></out>""");

    // The inline schema is part of the stylesheet, so a relative schemaLocation in it is
    // relative to the stylesheet. It resolved against the process's current directory.
    [Fact]
    public async Task An_inline_schema_includes_a_document_relative_to_the_stylesheet()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "part.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t">
              <xs:simpleType name="size"><xs:restriction base="xs:integer"><xs:maxInclusive value="9"/></xs:restriction></xs:simpleType>
            </xs:schema>
            """);
        const string import = """
            <xsl:import-schema namespace="urn:t">
              <xs:schema targetNamespace="urn:t" elementFormDefault="qualified">
                <xs:include schemaLocation="part.xsd"/>
                <xs:element name="n" type="t:size"/>
              </xs:schema>
            </xsl:import-schema>
            """;
        (await RunAsync(import, """<t:n xsl:validation="strict">7</t:n>""")).Should().Be("""<out><t:n xmlns:t="urn:t">7</t:n></out>""");
        (await ErrorAsync(import, """<t:n xsl:validation="strict">12</t:n>""")).Should().NotStartWith("no error");
    }

    [Fact]
    public async Task Two_inline_schemas_in_one_stylesheet_are_both_loaded()
    {
        const string import = """
            <xsl:import-schema namespace="urn:t">
              <xs:schema targetNamespace="urn:t" elementFormDefault="qualified">
                <xs:element name="n" type="xs:integer"/>
              </xs:schema>
            </xsl:import-schema>
            <xsl:import-schema namespace="urn:u">
              <xs:schema targetNamespace="urn:u" elementFormDefault="qualified">
                <xs:element name="m" type="xs:integer"/>
              </xs:schema>
            </xsl:import-schema>
            """;
        (await RunAsync(import, """<u:m xmlns:u="urn:u" xsl:validation="strict">7</u:m>""")).Should().Contain(">7</u:m>");
        (await ErrorAsync(import, """<u:m xmlns:u="urn:u" xsl:validation="strict">x</u:m>""")).Should().NotStartWith("no error");
    }

    [Fact]
    public async Task A_location_and_an_inline_schema_together_are_XTSE0215()
        => (await ErrorAsync("""
            <xsl:import-schema namespace="urn:t" schema-location="t.xsd">
              <xs:schema targetNamespace="urn:t"/>
            </xsl:import-schema>
            """, "x")).Should().Contain("XTSE0215");

    [Fact]
    public async Task An_inline_schema_for_another_namespace_is_XTSE0215()
        => (await ErrorAsync("""
            <xsl:import-schema namespace="urn:t">
              <xs:schema targetNamespace="urn:other"/>
            </xsl:import-schema>
            """, "x")).Should().Contain("XTSE0215");

    [Fact]
    public async Task An_inline_schema_that_does_not_compile_is_reported()
        => (await ErrorAsync("""
            <xsl:import-schema namespace="urn:t">
              <xs:schema targetNamespace="urn:t"><xs:element name="n" type="t:nosuch"/></xs:schema>
            </xsl:import-schema>
            """, "x")).Should().Contain("XQST0059");
}
