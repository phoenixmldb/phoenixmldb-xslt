using AwesomeAssertions;
using PhoenixmlDb.XQuery.Execution;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xs</c> is a prefix like any other in a stylesheet: with <c>xmlns:xs="urn:x"</c>,
/// <c>as="xs:integer"</c> names <c>Q{urn:x}integer</c>, a type only an imported schema can declare
/// (XPST0051 otherwise). Type names were read as XML Schema types by their spelling, so the
/// stylesheet below compiled and gave 1, and <c>as="xs:int"</c> raised XTTE0570 instead of XPST0051
/// (BUGS.md #135; Lucas, 3.3.0).
/// </summary>
public sealed class XsPrefixReboundTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-xsrebound-" + Guid.NewGuid().ToString("N"));

    public XsPrefixReboundTests()
    {
        Directory.CreateDirectory(_dir);
        // A schema whose target namespace is the one xs is bound to, declaring a type "integer".
        File.WriteAllText(Path.Combine(_dir, "x.xsd"), """
            <s:schema xmlns:s="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:x">
              <s:simpleType name="integer"><s:restriction base="s:integer"><s:maxInclusive value="9"/></s:restriction></s:simpleType>
            </s:schema>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>The error code of a failed run: XQuery's runtime errors carry it apart from the message.</summary>
    private async Task<string> ErrorOf(string declarations, string body)
    {
        try
        {
            await Run(declarations, body);
            return "no error";
        }
#pragma warning disable CA1031 // the error is the result under test
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ex is XQueryRuntimeException rt ? rt.ErrorCode + " " + ex.Message : ex.Message;
        }
    }

    private async Task<string> Run(string declarations, string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                xmlns:xs="urn:x" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f"
                exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {{declarations}}
              <xsl:template name="xsl:initial-template">{{body}}</xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    [Theory]
    [InlineData("""<xsl:variable name="v" as="xs:integer" select="1"/>""", "<xsl:value-of select='$v'/>")]
    [InlineData("""<xsl:variable name="v" as="xs:int" select="1"/>""", "<xsl:value-of select='$v'/>")]
    [InlineData("""<xsl:variable name="v" as="xs:integer*" select="1"/>""", "<xsl:value-of select='$v'/>")]
    [InlineData("""<xsl:function name="f:g" as="xs:string"><xsl:sequence select="'a'"/></xsl:function>""", "<xsl:value-of select='f:g()'/>")]
    [InlineData("", "<xsl:value-of select='1 instance of xs:integer'/>")]
    public async Task A_type_name_with_xs_bound_to_another_namespace_is_XPST0051(string declarations, string body)
        => (await ErrorOf(declarations, body)).Should().Contain("XPST0051");

    [Fact]
    public async Task A_constructor_function_with_xs_bound_to_another_namespace_is_XPST0017()
        => (await ErrorOf("", "<xsl:value-of select=\"xs:integer('1')\"/>")).Should().Contain("XPST0017");

    [Fact]
    public async Task A_prefix_bound_to_xml_schema_still_names_the_built_in_type()
        => (await Run("""<xsl:variable name="v" as="xsd:integer" select="1"/>""",
            "<xsl:value-of select='$v instance of xsd:integer'/>")).Should().Be("true");

    [Fact]
    public async Task The_type_xs_integer_is_the_one_the_schema_for_its_namespace_declares()
    {
        const string import = """<xsl:import-schema namespace="urn:x" schema-location="x.xsd"/>""";
        // An untypedAtomic value is cast to the declared type (function conversion rules): to the
        // schema's integer, whose values stop at 9, not to XML Schema's.
        (await Run(import + """<xsl:variable name="v" as="xs:integer" select="xsd:untypedAtomic('5')"/>""",
            "<xsl:value-of select='$v'/>")).Should().Be("5");
        (await ErrorOf(import + """<xsl:variable name="v" as="xs:integer" select="xsd:untypedAtomic('12')"/>""",
            "<xsl:value-of select='$v'/>")).Should().NotBe("no error");
    }
}
