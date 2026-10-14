using FluentAssertions;
using PhoenixmlDb.Xslt.Ast;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Template rules whose pattern names a type (<c>element(*, T)</c>, <c>attribute(n, T)</c>,
/// <c>schema-element(N)</c>, <c>schema-attribute(N)</c>), and the typed values and
/// annotations they see. The type in a pattern was parsed and not used, so such a rule matched
/// every node of the name.
/// </summary>
public sealed class TypedPatternTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-typed-patterns").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> RunAsync(string rules, string source, ValidationMode? validation = ValidationMode.Strict,
        string stylesheetAttributes = "", string declarations = "")
    {
        var xsd = Path.Combine(_dir, "t.xsd");
        await File.WriteAllTextAsync(xsd, """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:t" xmlns:t="urn:t" elementFormDefault="qualified">
              <xs:simpleType name="part"><xs:restriction base="xs:string"><xs:pattern value="\d{3}-[A-Z]{2}"/></xs:restriction></xs:simpleType>
              <xs:simpleType name="partOrInt"><xs:union memberTypes="t:part xs:integer"/></xs:simpleType>
              <xs:element name="doc"><xs:complexType><xs:sequence>
                <xs:element name="p" type="t:part" maxOccurs="unbounded"/>
                <xs:element name="d" type="xs:decimal"/>
                <xs:element name="u" type="t:partOrInt"/>
              </xs:sequence><xs:attribute name="code" type="t:part"/></xs:complexType></xs:element>
              <xs:attribute name="special" type="t:part"/>
              <xs:element name="n" type="xs:integer" nillable="true"/>
            </xs:schema>
            """, TestContext.Current.CancellationToken);
        var t = new XsltTransformer { SourceValidation = validation };
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:t="urn:t" xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            version="3.0" expand-text="yes" exclude-result-prefixes="#all" {stylesheetAttributes}>
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:import-schema namespace="urn:t" schema-location="{xsd.Replace("\\", "/", StringComparison.Ordinal)}"/>
              {declarations}
              {rules}
            </xsl:stylesheet>
            """);
        return await t.TransformAsync(source);
    }

    private const string Source = "<t:doc xmlns:t=\"urn:t\" code=\"123-AB\"><t:p>123-AB</t:p><t:d>  1.10  </t:d><t:u>100</t:u></t:doc>";

    private const string Rules = """
        <xsl:template match="/"><r><xsl:apply-templates select="t:doc/(@code, *)"/></r></xsl:template>
        <xsl:template match="element(*, t:partOrInt)">[union {.}]</xsl:template>
        <xsl:template match="element(*, t:part)">[part {.}]</xsl:template>
        <xsl:template match="element(*, xs:decimal)">[decimal <xsl:value-of select="."/>]</xsl:template>
        <xsl:template match="attribute(code, t:part)">[code {.}]</xsl:template>
        <xsl:template match="element(*) | attribute(*)">[other {name()}]</xsl:template>
        """;

    [Fact]
    public async Task A_pattern_with_a_type_matches_the_nodes_of_that_type()
        // t:u holds 100, so its annotation is the member xs:integer: it matches the union and
        // xs:decimal, and the later of the two rules is chosen.
        => (await RunAsync(Rules, Source)).Should().Be("<r>[code 123-AB][part 123-AB][decimal 1.1][decimal 100]</r>");

    /// <summary>An element of a member type of a union matches element(*, U) as well (XPath 3.0).</summary>
    [Fact]
    public async Task An_element_of_a_member_type_matches_the_union_too()
        => (await RunAsync("""
                <xsl:template match="/"><xsl:apply-templates select="t:doc/t:p"/></xsl:template>
                <xsl:template match="element(*, t:partOrInt)">[union]</xsl:template>
                """, Source)).Should().Be("[union]");

    [Fact]
    public async Task With_an_untyped_source_it_matches_none_of_them()
        => (await RunAsync(Rules, Source, validation: null)).Should().Be("<r>[other code][other t:p][other t:d][other t:u]</r>");

    [Fact]
    public async Task Schema_element_and_schema_attribute_match_validated_nodes_only()
    {
        const string rules = """
            <xsl:template match="/"><r><xsl:apply-templates select="t:doc"/><xsl:apply-templates select="$v/*, $a"/></r></xsl:template>
            <xsl:template match="schema-element(t:doc)">[doc]</xsl:template>
            <xsl:template match="schema-attribute(t:special)">[special {.}]</xsl:template>
            <xsl:template match="element(*) | attribute(*)">[other {name()}]</xsl:template>
            """;
        const string declarations = """
            <xsl:variable name="v"><t:doc/></xsl:variable>
            <xsl:variable name="a" as="attribute()"><xsl:attribute name="t:special" validation="strict">456-CD</xsl:attribute></xsl:variable>
            """;
        (await RunAsync(rules, Source, declarations: declarations)).Should().Be("<r>[doc][other t:doc][special 456-CD]</r>");
    }

    [Fact]
    public async Task A_type_test_does_not_raise_the_priority_of_a_pattern()
        // element(*, T) has priority 0 or -0.25, not the 0.5 of a predicate: the rule for t:p by
        // name, priority 0, is chosen over it only by import precedence order; here it comes later.
        => (await RunAsync("""
                <xsl:template match="/"><xsl:apply-templates select="t:doc/t:p"/></xsl:template>
                <xsl:template match="t:p[true()]">[predicate]</xsl:template>
                <xsl:template match="element(*, t:part)">[type]</xsl:template>
                """, Source)).Should().Be("[predicate]");

    [Fact]
    public async Task Input_type_annotations_strip_takes_the_annotations_away()
        => (await RunAsync(Rules, Source, stylesheetAttributes: "input-type-annotations=\"strip\""))
            .Should().Be("<r>[other code][other t:p][other t:d][other t:u]</r>");

    [Fact]
    public async Task A_global_temporary_tree_keeps_the_annotations_of_what_was_validated_in_it()
        => (await RunAsync("""
                <xsl:template match="/"><r><xsl:apply-templates select="$tree/t:doc/*"/></r></xsl:template>
                <xsl:template match="*">[other]</xsl:template>
                <xsl:template match="element(*, t:part)">[part {.}]</xsl:template>
                """, "<x/>", validation: null, declarations: """
                <xsl:variable name="tree"><t:doc xsl:validation="strict" code="123-AB"><t:p>456-CD</t:p><t:d>1</t:d><t:u>1</t:u></t:doc></xsl:variable>
                """)).Should().Be("<r>[part 456-CD][other][other]</r>");

    [Fact]
    public async Task A_pattern_may_name_an_element_called_union()
        => (await RunAsync("""
                <xsl:template match="/"><r><xsl:apply-templates select="//t:union"/></r></xsl:template>
                <xsl:template match="t:union">[named union]</xsl:template>
                """, "<x xmlns:t=\"urn:t\"><t:union/></x>", validation: null)).Should().Be("<r>[named union]</r>");

    /// <summary>A parameter as="xs:int" holds an xs:int, not the xs:integer it was reduced to.</summary>
    [Fact]
    public async Task A_derived_built_in_as_type_gives_a_value_of_that_type()
        => (await RunAsync("""
                <xsl:template match="/">{$p instance of xs:int} {$p instance of xs:short} {$n instance of xs:NMTOKEN}</xsl:template>
                """, "<x/>", validation: null, declarations: """
                <xsl:param name="p" as="xs:int"><xsl:value-of select="'42'"/></xsl:param>
                <xsl:param name="n" as="xs:NMTOKEN"><xsl:value-of select="'a-b'"/></xsl:param>
                """)).Should().Be("true false true");

    /// <summary>xsl:source-document validation="strict" gives a typed document; the attribute was ignored.</summary>
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task Source_document_validation_annotates_what_it_reads(string streamable)
    {
        var doc = Path.Combine(_dir, "in.xml");
        await File.WriteAllTextAsync(doc, Source, TestContext.Current.CancellationToken);
        var uri = new Uri(doc).AbsoluteUri;
        (await RunAsync($$"""
                <xsl:mode streamable="{{streamable}}"/>
                <xsl:template name="xsl:initial-template" match="/">
                  <r><xsl:source-document href="{{uri}}" streamable="{{streamable}}" validation="strict"><xsl:apply-templates select="t:doc/t:d"/></xsl:source-document></r>
                </xsl:template>
                <xsl:template match="element(*, xs:decimal)">[decimal {data(.)}]</xsl:template>
                <xsl:template match="*">[other]</xsl:template>
                """, "<x/>", validation: null)).Should().Be("<r>[decimal 1.1]</r>");
    }

    /// <summary>fn:nilled of a validated element with xsi:nil="true" is true; it was always false.</summary>
    [Fact]
    public async Task Nilled_is_true_for_a_validated_nilled_element()
        => (await RunAsync("""
                <xsl:template match="/">{nilled(t:n)} {t:n/nilled()}</xsl:template>
                """, "<t:n xmlns:t=\"urn:t\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:nil=\"true\"/>")).Should().Be("true true");

    /// <summary>
    /// xsl:copy-of of a document node with validation="strict", in a variable, gives a validated
    /// document; the copy was untyped.
    /// </summary>
    [Fact]
    public async Task A_copy_of_a_document_node_is_validated_in_a_variable()
        => (await RunAsync("""
                <xsl:template match="/">
                  <xsl:variable name="d" as="document-node()"><xsl:copy-of select="." validation="strict"/></xsl:variable>
                  <xsl:variable name="e" as="document-node(element(t:doc))"><xsl:copy-of select="." validation="strict"/></xsl:variable>
                  <xsl:value-of select="$d/t:doc/t:d instance of element(*, xs:decimal), nilled($d/t:doc), $e/t:doc/@code instance of attribute(*, t:part)"/>
                </xsl:template>
                """, Source, validation: null)).Should().Be("true false true");
}
