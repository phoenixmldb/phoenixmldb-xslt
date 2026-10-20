using AwesomeAssertions;
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
              <xs:element name="head" type="xs:string"/>
              <xs:element name="member" type="xs:string" substitutionGroup="t:head"/>
              <xs:element name="heads"><xs:complexType><xs:sequence><xs:element ref="t:head" maxOccurs="unbounded"/></xs:sequence></xs:complexType></xs:element>
              <xs:element name="table"><xs:complexType><xs:sequence><xs:element name="row" maxOccurs="unbounded"><xs:complexType><xs:sequence>
                <xs:element name="key" type="xs:ID"/><xs:element name="value" type="xs:string"/>
              </xs:sequence></xs:complexType></xs:element></xs:sequence></xs:complexType></xs:element>
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

    /// <summary>
    /// schema-element(N) matches the elements of N's substitution group, and its default
    /// priority is 0.25, above the 0 of a name (XSLT 3.0 §6.5). It was matched by the name N
    /// only, with priority 0, so t:member took the later rule by name.
    /// </summary>
    [Fact]
    public async Task Schema_element_matches_the_substitution_group_with_priority_a_quarter()
        => (await RunAsync("""
                <xsl:template match="/"><r><xsl:apply-templates select="t:heads/*"/></r></xsl:template>
                <xsl:template match="schema-element(t:head)">[group {local-name()}]</xsl:template>
                <xsl:template match="t:member">[by name]</xsl:template>
                """, "<t:heads xmlns:t=\"urn:t\"><t:head>a</t:head><t:member>b</t:member></t:heads>"))
            .Should().Be("<r>[group head][group member]</r>");

    /// <summary>
    /// document-node(schema-element(N)) has the priority of schema-element(N), above the −0.5
    /// of "/", and matches only a document whose element is N or of its group.
    /// </summary>
    [Fact]
    public async Task Document_node_schema_element_matches_only_its_documents()
    {
        const string rules = """
            <xsl:template match="document-node(schema-element(t:heads))">[heads]</xsl:template>
            <xsl:template match="/">[root]</xsl:template>
            """;
        (await RunAsync(rules, "<t:heads xmlns:t=\"urn:t\"><t:head>a</t:head></t:heads>")).Should().Be("[heads]");
        (await RunAsync(rules, Source)).Should().Be("[root]");
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

    /// <summary>
    /// xsl:with-param select= with an atomic as= type atomizes the nodes it selects to their
    /// typed values; the nodes were passed on.
    /// </summary>
    [Fact]
    public async Task A_with_param_with_an_atomic_type_atomizes_what_it_selects()
        => (await RunAsync("""
                <xsl:template match="/"><xsl:apply-templates select="t:doc"><xsl:with-param name="p" select="t:doc/t:d" as="xs:decimal"/></xsl:apply-templates></xsl:template>
                <xsl:template match="t:doc"><xsl:param name="p"/>{$p instance of xs:decimal} {$p instance of node()} {$p}</xsl:template>
                """, Source)).Should().Be("true false 1.1");

    /// <summary>
    /// as= may name an atomic type that a schema defines; it was taken for item(), and the
    /// value kept the node it was selected as.
    /// </summary>
    [Fact]
    public async Task An_as_type_may_be_a_type_that_a_schema_defines()
        => (await RunAsync("""
                <xsl:template match="/">
                  <xsl:variable name="v" select="t:doc/@code" as="t:part"/>
                  <xsl:value-of select="$v instance of t:part, $v instance of node()"/>
                </xsl:template>
                """, Source)).Should().Be("true false");

    /// <summary>
    /// xsl:copy-of validation="strict" of an element whose base URI differs from the
    /// stylesheet's, in a variable: the copy is validated. Such a copy goes into the sequence
    /// as a node, and was kept untyped.
    /// </summary>
    [Fact]
    public async Task A_copy_from_a_document_with_its_own_base_uri_is_validated()
    {
        var doc = Path.Combine(_dir, "in.xml");
        await File.WriteAllTextAsync(doc, Source, TestContext.Current.CancellationToken);
        var uri = new Uri(doc).AbsoluteUri;
        (await RunAsync($$"""
                <xsl:template match="/">
                  <xsl:variable name="e" as="element()"><xsl:copy-of select="doc('{{uri}}')/t:doc" validation="strict"/></xsl:variable>
                  <xsl:value-of select="$e/t:d instance of element(*, xs:decimal)"/>
                </xsl:template>
                """, "<x/>", validation: null)).Should().Be("true");
    }

    private const string Table = """
        <xsl:variable name="x" as="document-node()">
          <xsl:document validation="strict"><t:table><t:row><t:key>A</t:key><t:value>alpha</t:value></t:row><t:row><t:key>C</t:key><t:value>gamma</t:value></t:row></t:table></xsl:document>
        </xsl:variable>
        """;

    /// <summary>
    /// element-with-id() and id() with a second argument may start a pattern (XSLT 3.0
    /// §5.5.2); element-with-id was XTSE0340. A global document-node() made by a validated
    /// xsl:document keeps its annotations, so the element of type xs:ID is found.
    /// </summary>
    [Fact]
    public async Task A_pattern_may_start_with_element_with_id()
        => (await RunAsync("""
                <xsl:template match="/"><r><xsl:apply-templates select="$x//t:row"/></r></xsl:template>
                <xsl:template match="element-with-id('C', $x)">[{t:value}]</xsl:template>
                <xsl:template match="*"/>
                """, "<x/>", validation: null, declarations: Table)).Should().Be("<r>[gamma]</r>");

    /// <summary>id() finds an element of type xs:ID by the ID it holds; only ID attributes were looked at.</summary>
    [Fact]
    public async Task Id_finds_an_element_whose_type_is_xs_ID()
        => (await RunAsync("""
                <xsl:template match="/"><r><xsl:apply-templates select="$x//t:key"/>{count($x/id('C'))} {$x/element-with-id('C')/t:value}</r></xsl:template>
                <xsl:template match="id('A', $x)">[{..//t:value}]</xsl:template>
                <xsl:template match="*"/>
                """, "<x/>", validation: null, declarations: Table)).Should().Be("<r>[alpha]1 gamma</r>");

    private const string Nilled = "<t:n xmlns:t=\"urn:t\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:nil=\"true\"/>";

    /// <summary>
    /// fn:copy-of and fn:snapshot keep the type annotations of what they copy, as
    /// validation="preserve" does; the copies were untyped, and a copy of a nilled element
    /// was not nilled.
    /// </summary>
    [Fact]
    public async Task Copy_of_and_snapshot_keep_type_annotations()
        => (await RunAsync("""
                <xsl:template match="/">{nilled(copy-of(t:n))} {nilled(snapshot(t:n))} {copy-of(t:n) instance of element(*, xs:integer?)}</xsl:template>
                """, Nilled)).Should().Be("true true true");

    /// <summary>
    /// xsl:copy-of validation="preserve" keeps the annotations, and "strip" drops them; both
    /// were dropped.
    /// </summary>
    [Fact]
    public async Task Copy_of_with_validation_preserve_keeps_type_annotations()
        => (await RunAsync("""
                <xsl:template match="/">
                  <xsl:variable name="p" as="document-node()"><xsl:copy-of select="." validation="preserve"/></xsl:variable>
                  <xsl:variable name="s" as="document-node()"><xsl:copy-of select="." validation="strip"/></xsl:variable>
                  <xsl:value-of select="$p/t:doc/t:d instance of element(*, xs:decimal), $s/t:doc/t:d instance of element(*, xs:decimal)"/>
                </xsl:template>
                """, Source)).Should().Be("true false");

    /// <summary>
    /// An element validated as it is built in a typed variable keeps its annotations when
    /// its content was copied in (xsl:copy-of select="@*"): the variable was read again
    /// from its text, untyped, so the copy of a nilled element was not nilled.
    /// </summary>
    [Fact]
    public async Task An_element_validated_in_a_variable_keeps_its_annotations_when_content_is_copied_in()
        => (await RunAsync("""
                <xsl:template match="/">
                  <xsl:variable name="v" as="element()"><xsl:copy select="t:n" validation="strict"><xsl:copy-of select="@*"/></xsl:copy></xsl:variable>
                  <xsl:value-of select="nilled($v), $v instance of element(*, xs:untyped)"/>
                </xsl:template>
                """, Nilled, validation: null)).Should().Be("true false");

    /// <summary>
    /// xsl:result-document validation="strict" with no href validates the principal result
    /// as a document: two root elements are XTTE1550. Only a secondary result was validated.
    /// </summary>
    [Fact]
    public async Task A_principal_result_document_is_validated_as_a_document()
    {
        var act = () => RunAsync("""
                <xsl:template match="/"><xsl:result-document validation="strict"><t:n>1</t:n><t:n>2</t:n></xsl:result-document></xsl:template>
                """, "<x/>", validation: null);
        (await act.Should().ThrowAsync<PhoenixmlDb.Xslt.Engine.XsltException>()).Which.Message.Should().Contain("XTTE1550");
    }

    /// <summary>
    /// json-to-xml with validate: true() gives a tree typed by the schema for the XML
    /// representation of JSON (F&amp;O 3.1 §17.1.4); it was FOJS0004, as for a processor
    /// that is not schema-aware.
    /// </summary>
    [Fact]
    public async Task Json_to_xml_validates_when_asked()
        => (await RunAsync("""
                <xsl:template match="/" xmlns:j="http://www.w3.org/2005/xpath-functions">
                  <xsl:variable name="j" select="json-to-xml('[1, true]', map{'validate': true()})"/>
                  <xsl:value-of select="$j/* instance of element(j:array, j:arrayType), data($j//j:number) instance of xs:double, data($j//j:boolean)"/>
                </xsl:template>
                """, "<x/>", validation: null, declarations: "<xsl:import-schema namespace='http://www.w3.org/2005/xpath-functions'/>")).Should().Be("true true true");

    /// <summary>
    /// xsl:attribute name="xml:lang" validation="strict" is validated against the schema of the
    /// XML namespace; the xml prefix was not resolved, and the attribute was looked for in no
    /// namespace (XTTE1512). xml:id is then an ID that id() finds in a temporary tree.
    /// </summary>
    [Fact]
    public async Task An_xml_attribute_is_validated_and_xml_id_is_an_id()
        => (await RunAsync("""
                <xsl:template match="/">
                  <xsl:variable name="t"><e><xsl:attribute name="xml:lang" select="'en'" validation="strict"/><xsl:attribute name="xml:id" select="'k1'" validation="strict"/></e></xsl:variable>
                  <xsl:value-of select="string($t/e/@xml:lang), exists(id('k1', $t))"/>
                </xsl:template>
                """, "<x/>", validation: null)).Should().Be("en true");

    /// <summary>A strictly validated element with no declaration but an xsi:type is validated against that type; it was XTTE1512.</summary>
    [Fact]
    public async Task An_undeclared_element_with_xsi_type_is_validated_against_the_type()
        => (await RunAsync("""
                <xsl:template match="/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <xsl:variable name="d" as="document-node()"><xsl:document validation="strict"><free><xsl:namespace name="xs" select="'http://www.w3.org/2001/XMLSchema'"/><xsl:attribute name="xsi:type">xs:anyType</xsl:attribute><x/></free></xsl:document></xsl:variable>
                  <xsl:value-of select="count($d/free/x)"/>
                </xsl:template>
                """, "<x/>", validation: null)).Should().Be("1");

    /// <summary>
    /// A source whose DTD declares the entities its xs:ENTITY attributes name is valid; the
    /// validating parse reads no DTD, so each value was "Reference to an undeclared entity".
    /// </summary>
    [Fact]
    public async Task A_source_entity_attribute_names_an_entity_its_dtd_declares()
    {
        var xsd = Path.Combine(_dir, "e.xsd");
        await File.WriteAllTextAsync(xsd, """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:element name="e"><xs:complexType><xs:attribute name="ent" type="xs:ENTITY"/></xs:complexType></xs:element>
            </xs:schema>
            """, TestContext.Current.CancellationToken);
        var t = new XsltTransformer { AllowDtdProcessing = true, SourceValidation = PhoenixmlDb.Xslt.Ast.ValidationMode.Strict };
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" expand-text="yes">
              <xsl:import-schema schema-location="{{xsd.Replace("\\", "/", StringComparison.Ordinal)}}"/>
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template match="/">{e/@ent}</xsl:template>
            </xsl:stylesheet>
            """);
        (await t.TransformAsync("<!DOCTYPE e [<!ENTITY pic 'P'>]><e ent='pic'/>")).Should().Be("pic");
    }

    /// <summary>
    /// xsl:copy-of of a document node with validation="strict" validates it as a document, so a
    /// duplicate ID is XTTE1555; its content was validated as a fragment, which allows that.
    /// </summary>
    [Fact]
    public async Task A_copied_document_is_validated_as_a_document()
    {
        var act = () => RunAsync("""
                <xsl:template match="/"><xsl:copy-of select="$t" validation="strict"/></xsl:template>
                """, "<x/>", validation: null, declarations: """
                <xsl:variable name="t"><t:table><t:row><t:key>A</t:key><t:value>1</t:value></t:row><t:row><t:key>A</t:key><t:value>2</t:value></t:row></t:table></xsl:variable>
                """);
        (await act.Should().ThrowAsync<PhoenixmlDb.Xslt.Engine.XsltException>()).Which.Message.Should().Contain("XTTE1555");
    }

    /// <summary>
    /// lang() in a template of a streamable mode is motionless (it reads the xml:lang of the
    /// ancestors, which streaming keeps); under strict streamability it was XTSE3430.
    /// </summary>
    [Fact]
    public async Task Lang_is_streamable()
    {
        var t = new XsltTransformer { StrictStreamability = true };
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0">
              <xsl:mode streamable="yes"/>
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template match="p"><r en="{lang('en')}"/></xsl:template>
            </xsl:stylesheet>
            """);
        using var input = new StringReader("<doc xml:lang='en'><p/></doc>");
        (await t.TransformAsync(input)).Should().Be("<r en=\"true\"/>");
    }

    /// <summary>
    /// xsl:evaluate schema-aware="yes" may name a type of an imported schema with a prefix of
    /// the stylesheet: the prefix was not bound when the expression was parsed (XPST0081).
    /// Without schema-aware the type is not available: XTDE3160.
    /// </summary>
    [Fact]
    public async Task Evaluate_may_name_a_schema_type_when_schema_aware()
    {
        (await RunAsync("""
                <xsl:template match="/"><xsl:evaluate xpath="'t:doc/t:p instance of element(*, t:part)'" context-item="." schema-aware="yes"/></xsl:template>
                """, Source)).Should().Be("true");
        var notAware = () => RunAsync("""
                <xsl:template match="/"><xsl:evaluate xpath="'t:doc/t:p instance of element(*, t:part)'" context-item="."/></xsl:template>
                """, Source);
        (await notAware.Should().ThrowAsync<PhoenixmlDb.Xslt.Engine.XsltException>()).Which.Message.Should().Contain("XTDE3160");
    }

    /// <summary>The static and type errors of the type attribute (XSLT 3.0 §25.4, §11.3).</summary>
    [Theory]
    // type and validation together.
    [InlineData("<e><xsl:attribute name='a' type='xs:integer' validation='strict'>1</xsl:attribute></e>", "XTSE1505")]
    // A prefix that is not declared; was XTSE0280.
    [InlineData("<e><xsl:attribute name='a' type='nope:x'>1</xsl:attribute></e>", "XTSE1520")]
    // Not a QName.
    [InlineData("<e><xsl:attribute name='a' type='t::x'>1</xsl:attribute></e>", "XTSE1520")]
    // A new attribute validated against xs:QName.
    [InlineData("<e><xsl:attribute name='a' type='xs:QName'>t:x</xsl:attribute></e>", "XTTE1545")]
    public async Task Type_attribute_errors(string body, string code)
    {
        var act = () => RunAsync($"<xsl:template match='/'>{body}</xsl:template>", "<x/>", validation: null);
        (await act.Should().ThrowAsync<PhoenixmlDb.Xslt.Engine.XsltException>()).Which.Message.Should().Contain(code);
    }

    /// <summary>A mode with typed="no" refuses a typed element: XTTE3110 (§6.6.1).</summary>
    [Fact]
    public async Task A_mode_with_typed_no_refuses_a_typed_element()
    {
        var act = () => RunAsync("""
                <xsl:mode name="u" typed="no"/>
                <xsl:template match="/"><xsl:apply-templates select="t:doc" mode="u"/></xsl:template>
                """, Source);
        (await act.Should().ThrowAsync<PhoenixmlDb.Xslt.Engine.XsltException>()).Which.Message.Should().Contain("XTTE3110");
    }

    /// <summary>
    /// A mode with typed="strict" may not have a rule whose pattern names an element the schemas
    /// do not declare: XTSE3105 (§6.6.1).
    /// </summary>
    [Fact]
    public async Task A_strict_typed_mode_requires_declared_names_in_its_patterns()
    {
        var act = () => RunAsync("""
                <xsl:mode typed="strict"/>
                <xsl:template match="t:undeclared">x</xsl:template>
                """, Source);
        (await act.Should().ThrowAsync<PhoenixmlDb.Xslt.Engine.XsltException>()).Which.Message.Should().Contain("XTSE3105");
    }

    /// <summary>
    /// The name in attribute(name, T) of a pattern is not in xpath-default-namespace (XSLT 3.0
    /// §5.1.2); it was put there, so the rule matched the attribute of that namespace.
    /// </summary>
    [Fact]
    public async Task An_attribute_kind_test_name_is_not_in_the_default_namespace()
        => (await RunAsync("""
                <xsl:template match="/"><r><xsl:apply-templates select="//@*"/></r></xsl:template>
                <xsl:template match="attribute(code, t:part)" xpath-default-namespace="urn:t">[{.}]</xsl:template>
                <xsl:template match="@*"/>
                """, "<t:doc xmlns:t='urn:t' code='123-AB'><t:p>456-CD</t:p><t:d>1</t:d><t:u>1</t:u></t:doc>")).Should().Be("<r>[123-AB]</r>");

    /// <summary>
    /// A group-by key that is a validated node is its typed value: 1.0 and 1 of type xs:decimal
    /// are one group. Its string value was the key.
    /// </summary>
    [Fact]
    public async Task A_group_by_key_is_the_typed_value_of_a_validated_node()
        => (await RunAsync("""
                <xsl:template match="/"><r><xsl:for-each-group select="$d/t:doc/*" group-by="."><g n="{count(current-group())}"/></xsl:for-each-group></r></xsl:template>
                """, "<x/>", validation: null, declarations: """
                <xsl:variable name="d" as="document-node()"><xsl:document validation="strict"><t:doc><t:p>123-AB</t:p><t:p>123-AB</t:p><t:d>1.0</t:d><t:u>1</t:u></t:doc></xsl:document></xsl:variable>
                """)).Should().Be("<r><g n=\"2\"/><g n=\"2\"/></r>");

    /// <summary>
    /// type="xs:untyped" asks for no validation (the element is untyped); it was validated
    /// against xs:untyped and refused. An element typed xs:untypedAtomic may not have element
    /// children (XTTE1540).
    /// </summary>
    [Fact]
    public async Task Type_untyped_means_no_validation()
    {
        (await RunAsync("""
                <xsl:template match="/"><xsl:variable name="v" as="element()"><e xsl:type="xs:untyped"><f/></e></xsl:variable><xsl:value-of select="$v instance of element(*, xs:untyped)"/></xsl:template>
                """, "<x/>", validation: null)).Should().Be("true");
        var withChild = () => RunAsync("""
                <xsl:template match="/"><e xsl:type="xs:untypedAtomic">a<f/>b</e></xsl:template>
                """, "<x/>", validation: null);
        (await withChild.Should().ThrowAsync<PhoenixmlDb.Xslt.Engine.XsltException>()).Which.Message.Should().Contain("XTTE1540");
    }

    /// <summary>default-validation is preserve or strip; strict is XTSE0020 (it was XTSE1660).</summary>
    [Fact]
    public async Task Default_validation_strict_is_XTSE0020()
    {
        var t = new XsltTransformer();
        var act = () => t.LoadStylesheetAsync("""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0" default-validation="strict">
              <xsl:template match="/"/>
            </xsl:stylesheet>
            """);
        (await act.Should().ThrowAsync<PhoenixmlDb.Xslt.Engine.XsltException>()).Which.Message.Should().Contain("XTSE0020");
    }

    /// <summary>
    /// A template as="schema-attribute(N)" or as="schema-element(N)*" returns the nodes. Its
    /// type was taken for an atomic one, so the attribute or element was atomized, and its
    /// value was written as text (W3C as-1812, as-3504, as-3601).
    /// </summary>
    [Fact]
    public async Task A_template_with_a_schema_node_type_returns_the_nodes()
        => (await RunAsync("""
                <xsl:template match="/"><r><a><xsl:call-template name="attr"/></a><xsl:call-template name="elems"/></r></xsl:template>
                <xsl:template name="attr" as="schema-attribute(t:special)"><xsl:attribute name="t:special" validation="strict">456-CD</xsl:attribute></xsl:template>
                <xsl:template name="elems" as="schema-element(t:doc)*"><xsl:copy-of select="t:doc" validation="preserve"/></xsl:template>
                """, Source)).Should().Be("<r><a xmlns:t=\"urn:t\" t:special=\"456-CD\"/><t:doc xmlns:t=\"urn:t\" code=\"123-AB\"><t:p>123-AB</t:p><t:d>  1.10  </t:d><t:u>100</t:u></t:doc></r>");

    /// <summary>
    /// A parameter or variable as="schema-attribute(N)" built from xsl:attribute holds the
    /// attribute; it was built as a temporary tree, and a document node cannot hold an
    /// attribute (XTDE0420).
    /// </summary>
    [Fact]
    public async Task A_schema_attribute_variable_holds_its_attribute()
        => (await RunAsync("""
                <xsl:template match="/">
                  <xsl:variable name="a" as="schema-attribute(t:special)"><xsl:attribute name="t:special" validation="strict">456-CD</xsl:attribute></xsl:variable>
                  <xsl:value-of select="$a instance of schema-attribute(t:special), string($a)"/>
                </xsl:template>
                """, "<x/>", validation: null)).Should().Be("true 456-CD");

    /// <summary>
    /// A global variable as="t:part?" over an untyped node casts its value to t:part (the
    /// function conversion rules); it held the string.
    /// </summary>
    [Fact]
    public async Task An_untyped_value_is_cast_to_the_declared_schema_type()
        => (await RunAsync("""
                <xsl:template match="/"><xsl:value-of select="$v instance of t:part, $v instance of xs:untypedAtomic"/></xsl:template>
                """, "<e>123-AB</e>", validation: null, declarations: """
                <xsl:variable name="v" select="/e" as="t:part?"/>
                """)).Should().Be("true false");

    /// <summary>
    /// xsl:copy-of validation="preserve" of an element into a variable as="element(...)" keeps
    /// its annotation; the copy made into the tree was untyped.
    /// </summary>
    [Fact]
    public async Task A_preserved_element_copy_keeps_its_annotation_in_a_variable()
        => (await RunAsync("""
                <xsl:template match="/">
                  <xsl:variable name="e" as="element(t:d, xs:decimal)"><xsl:copy-of select="t:doc/t:d" validation="preserve"/></xsl:variable>
                  <xsl:value-of select="data($e) instance of xs:decimal"/>
                </xsl:template>
                """, Source)).Should().Be("true");
}
