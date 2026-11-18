using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A type name in an <c>as</c> SequenceType must be a built-in type or an atomic or union type
/// of the schemas in scope: XPST0051 otherwise, when the stylesheet is compiled (XPath 3.1
/// §2.5.4; Saxon-HE 12.10 "Unknown simple type"). It was accepted, and the value was kept as it
/// was: as="nosuch" select="1" gave 1 (xslt#550).
/// </summary>
public sealed class UnknownAsTypeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-astype-" + Guid.NewGuid().ToString("N"));

    public UnknownAsTypeTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "my.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:my">
              <xs:simpleType name="t"><xs:restriction base="xs:integer"/></xs:simpleType>
            </xs:schema>
            """);
        File.WriteAllText(Path.Combine(_dir, "none.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xs:simpleType name="small"><xs:restriction base="xs:integer"/></xs:simpleType>
            </xs:schema>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string ImportMy = """<xsl:import-schema namespace="urn:my" schema-location="my.xsd"/>""";
    private const string ImportNone = """<xsl:import-schema schema-location="none.xsd"/>""";

    private async Task<string> Run(string declarations, string body = "<xsl:text>ok</xsl:text>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:my="urn:my" xmlns:f="urn:f"
                exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {{declarations}}
              <xsl:template name="xsl:initial-template">{{body}}</xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    private static string Variable(string asType, string select = "1")
        => $"""<xsl:variable name="v" as="{asType}" select="{select}"/>""";

    [Theory]
    [InlineData("", "nosuch")]
    [InlineData("", "nosuch?")]
    [InlineData("", "nosuch*")]
    [InlineData("", "my:nosuch")]
    [InlineData(ImportMy, "my:nosuch")]
    [InlineData(ImportNone, "nosuch")]
    [InlineData("", "xs:nosuch")]
    [InlineData("", "array(nosuch)")]
    [InlineData("", "array(array(nosuch))")]
    [InlineData("", "map(xs:string, nosuch)")]
    [InlineData("", "function(nosuch) as item()")]
    public async Task An_unknown_type_in_a_variable_is_XPST0051(string import, string asType)
    {
        var act = () => Run(import + Variable(asType));

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XPST0051");
    }

    [Fact]
    public async Task An_unknown_type_of_a_function_parameter_is_XPST0051()
    {
        var act = () => Run("""
            <xsl:function name="f:g" as="item()"><xsl:param name="p" as="nosuch"/><xsl:sequence select="$p"/></xsl:function>
            """);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XPST0051");
    }

    [Fact]
    public async Task An_unknown_type_of_a_template_parameter_is_XPST0051()
    {
        var act = () => Run("""
            <xsl:template name="t"><xsl:param name="p" as="my:nosuch"/><xsl:value-of select="$p"/></xsl:template>
            """);

        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XPST0051");
    }

    [Theory]
    [InlineData(ImportMy, "my:t", "1")]
    [InlineData(ImportMy, "my:t*", "(1, 2)")]
    [InlineData(ImportNone, "small", "1")]
    [InlineData("", "xs:integer", "1")]
    [InlineData("", "xs:int", "1")]
    [InlineData("", "xs:numeric", "1")]
    [InlineData("", "xs:dateTimeStamp", "current-dateTime()")]
    [InlineData("", "array(xs:integer)", "[1]")]
    [InlineData("", "map(xs:string, xs:integer)", "map{'a':1}")]
    [InlineData("", "function(xs:string) as xs:integer", "string-length#1")]
    [InlineData("", "element()?", "()")]
    public async Task A_known_type_is_accepted(string import, string asType, string select)
        => (await Run(import + Variable(asType, select))).Should().Be("ok");
}
