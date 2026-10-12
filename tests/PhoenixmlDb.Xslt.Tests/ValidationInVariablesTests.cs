using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An element with <c>[xsl:]validation</c> that is built in a variable, a parameter or a
/// function is validated there, and the element the variable holds has the type annotations of
/// the schema (XSLT 3.0 §25.4.1). It was validated only on its way to the principal result:
/// in a variable invalid content passed, and <c>instance of schema-element(N)</c> was false
/// for valid content (xslt#353).
/// </summary>
public sealed class ValidationInVariablesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-validation-variables").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> RunAsync(string body, string declarations = "")
    {
        var xsd = Path.Combine(_dir, "order.xsd");
        await File.WriteAllTextAsync(xsd, """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:o" xmlns:o="urn:o" elementFormDefault="qualified">
              <xs:element name="order" type="o:orderType"/>
              <xs:complexType name="orderType"><xs:sequence><xs:element name="qty" type="xs:integer"/></xs:sequence></xs:complexType>
              <xs:element name="note"><xs:complexType><xs:sequence><xs:element name="n" type="xs:integer"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """, TestContext.Current.CancellationToken);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:o="urn:o" xmlns:f="urn:f"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0" expand-text="yes" exclude-result-prefixes="#all">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:import-schema namespace="urn:o" schema-location="{xsd.Replace("\\", "/", StringComparison.Ordinal)}"/>
              {declarations}
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync("<in/>");
    }

    private const string Valid = "<o:order xsl:validation=\"strict\"><o:qty>1</o:qty></o:order>";
    private const string Invalid = "<o:order xsl:validation=\"strict\"><o:qty>x</o:qty></o:order>";

    public static TheoryData<string, string> Refused => new()
    {
        // a variable with a declared type, and a temporary tree
        { $"<xsl:variable name=\"v\" as=\"element()\">{Invalid}</xsl:variable><r>{{name($v)}}</r>", "XTTE1510" },
        { $"<xsl:variable name=\"v\">{Invalid}</xsl:variable><r>{{name($v/*)}}</r>", "XTTE1510" },
        // xsl:element
        { "<xsl:variable name=\"v\" as=\"element()\"><xsl:element name=\"o:order\" validation=\"strict\"><o:qty>x</o:qty></xsl:element></xsl:variable><r>{name($v)}</r>", "XTTE1510" },
        // inside another element of the variable
        { $"<xsl:variable name=\"v\" as=\"element()\"><w>{Invalid}</w></xsl:variable><r>{{name($v)}}</r>", "XTTE1510" },
        // no declaration, and lax
        { "<xsl:variable name=\"v\" as=\"element()\"><o:nope xsl:validation=\"strict\"/></xsl:variable><r>{name($v)}</r>", "XTTE1512" },
        { "<xsl:variable name=\"v\"><o:order xsl:validation=\"lax\"><o:qty>x</o:qty></o:order></xsl:variable><r>{name($v/*)}</r>", "XTTE1515" },
        // a parameter of a called template
        { $"<xsl:call-template name=\"t\"><xsl:with-param name=\"p\" as=\"element()\">{Invalid}</xsl:with-param></xsl:call-template>", "XTTE1510" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task Invalid_content_in_a_variable_is_refused(string body, string code)
    {
        var run = () => RunAsync(body, "<xsl:template name=\"t\"><xsl:param name=\"p\"/><r>{name($p)}</r></xsl:template>");
        (await run.Should().ThrowAsync<XsltException>()).Which.Message.Should().StartWith(code + ":");
    }

    [Theory]
    // a variable with a declared type
    [InlineData("as=\"element()\"", "$v")]
    // a temporary tree
    [InlineData("", "$v/*")]
    public async Task A_validated_element_in_a_variable_has_its_type_annotations(string asAttribute, string element)
    {
        var body = $"<xsl:variable name=\"v\" {asAttribute}>{Valid}</xsl:variable>"
            + $"<r>{{{element} instance of schema-element(o:order)}} {{{element} instance of element(*, o:orderType)}} "
            + $"{{{element}/o:qty instance of element(*, xs:integer)}} {{data({element}/o:qty) instance of xs:integer}} {{{element}/o:qty + 1}}</r>";
        (await RunAsync(body)).Should().Be("<r>true true true true 2</r>");
    }

    [Fact]
    public async Task An_element_with_an_anonymous_type_matches_its_declaration()
        => (await RunAsync("<xsl:variable name=\"v\" as=\"element()\"><o:note xsl:validation=\"strict\"><o:n>1</o:n></o:note></xsl:variable>"
            + "<r>{$v instance of schema-element(o:note)} {$v instance of schema-element(o:order)}</r>"))
            .Should().Be("<r>true false</r>");

    [Fact]
    public async Task The_result_of_a_function_is_validated_too()
        => (await RunAsync("<r>{f:order() instance of schema-element(o:order)} {f:order()/o:qty * 2}</r>",
            $"<xsl:function name=\"f:order\" as=\"element()\">{Valid}</xsl:function>"))
            .Should().Be("<r>true 2</r>");

    [Fact]
    public async Task An_element_that_is_not_validated_has_no_annotation()
        => (await RunAsync("<xsl:variable name=\"v\" as=\"element()\"><o:order><o:qty>1</o:qty></o:order></xsl:variable>"
            + "<r>{$v instance of schema-element(o:order)} {$v/o:qty instance of element(*, xs:integer)} {name($v)}</r>"))
            .Should().Be("<r>false false o:order</r>");

    [Theory]
    // the validated element is in its place among what is around it, and copies out as it was
    [InlineData("<xsl:variable name=\"v\" as=\"element()\"><w a=\"1\">x" + Valid + "y<z/></w></xsl:variable><xsl:copy-of select=\"$v\"/>",
        "<w a=\"1\">x<o:order xmlns:o=\"urn:o\"><o:qty>1</o:qty></o:order>y<z/></w>")]
    [InlineData("<xsl:variable name=\"v\">a" + Valid + "b" + Valid + "</xsl:variable><r>{count($v/node())} {count($v/o:order)} {$v/o:order[2]/.. instance of document-node()}</r>",
        "<r>4 2 true</r>")]
    [InlineData("<xsl:variable name=\"v\" as=\"element()\">" + Valid + "</xsl:variable><r>{empty($v/..)}</r>", "<r>true</r>")]
    public async Task The_validated_element_stands_where_the_element_was_built(string body, string expected)
        => (await RunAsync(body)).Should().Be(expected);
}
