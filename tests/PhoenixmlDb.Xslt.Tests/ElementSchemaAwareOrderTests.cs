using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An xsl:element carrying an attribute this processor must not accept at all is told about that
/// (XTSE1660) rather than about its missing name: the name is the mistake the author can fix by
/// typing more, the schema-aware attribute is the one they cannot (W3C error-1660b/c).
/// </summary>
public sealed class ElementSchemaAwareOrderTests
{
    private static async Task AssertErrorAsync(string body, string code)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var act = async () =>
        {
            // As a basic processor: a schema-aware one, the default, accepts these attributes.
            var t = new XsltTransformer { SchemaAware = false };
            await t.LoadStylesheetAsync(ss);
            return await t.TransformAsync("<doc/>");
        };
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain(code);
    }

    [Theory]
    [InlineData("""<xsl:element type="xs:untyped"><x/></xsl:element>""")]
    [InlineData("""<xsl:element validation="strict"><x/></xsl:element>""")]
    public Task AnElementWithASchemaAwareAttributeAndNoName_IsXTSE1660(string body)
        => AssertErrorAsync(body, "XTSE1660");

    [Theory]
    [InlineData("""<xsl:element name="e" type="xs:untyped"/>""")]
    [InlineData("""<xsl:element name="e" validation="strict"/>""")]
    public Task ANamedElementWithASchemaAwareAttribute_IsStillXTSE1660(string body)
        => AssertErrorAsync(body, "XTSE1660");

    [Fact]
    public Task AnElementWithNoNameAndNothingElse_IsStillXTSE0010()
        => AssertErrorAsync("<xsl:element><x/></xsl:element>", "XTSE0010");

    /// <summary>
    /// The default is a schema-aware processor: a type attribute and validation="strict" are
    /// accepted with no xsl:import-schema, and they validate. They were refused with XTSE1660
    /// unless the stylesheet had an xsl:import-schema, which a schema-aware processor does not
    /// require (the built-in types are always in scope).
    /// </summary>
    [Theory]
    [InlineData("<xsl:element name=\"n\" type=\"xs:integer\">12</xsl:element>", "<n>12</n>")]
    [InlineData("<n xsl:type=\"xs:integer\">12</n>", "<n>12</n>")]
    [InlineData("<e><xsl:attribute name=\"a\" type=\"xs:date\">2024-03-01</xsl:attribute></e>", "<e a=\"2024-03-01\"/>")]
    [InlineData("<xsl:value-of select=\"system-property('xsl:is-schema-aware')\"/>", "yes")]
    public async Task By_default_a_schema_aware_attribute_is_accepted(string body, string expected)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        (await t.TransformAsync("<doc/>")).Should().Be(expected);
    }

    [Theory]
    [InlineData("<xsl:element name=\"n\" type=\"xs:integer\">x</xsl:element>", "XTTE1540")]
    [InlineData("<n xsl:type=\"xs:integer\">x</n>", "XTTE1540")]
    [InlineData("<e><xsl:attribute name=\"a\" type=\"xs:date\">yesterday</xsl:attribute></e>", "XTTE1540")]
    // strict validation with no declaration in scope
    [InlineData("<n xsl:validation=\"strict\"/>", "XTTE1512")]
    public async Task By_default_it_validates(string body, string code)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        var run = () => t.TransformAsync("<doc/>");
        (await run.Should().ThrowAsync<Exception>()).Which.Message.Should().StartWith(code + ":");
    }

    private const string InlineSchema = """
        <xsl:import-schema>
          <xs:schema>
            <xs:element name="n" type="xs:integer"/>
          </xs:schema>
        </xsl:import-schema>
        """;

    private static async Task<string> RunWithInlineSchemaAsync(string declarations, string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output omit-xml-declaration="yes"/>
              {InlineSchema}
              {declarations}
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync("<doc/>");
    }

    /// <summary>
    /// An xs:schema child of xsl:import-schema with no target namespace is loaded. It was
    /// taken for the empty marker form of the declaration and skipped, so strict validation
    /// against it passed with nothing checked.
    /// </summary>
    [Fact]
    public async Task An_inline_schema_with_no_target_namespace_is_loaded()
    {
        (await RunWithInlineSchemaAsync("", "<n xsl:validation=\"strict\">12</n>")).Should().Be("<n>12</n>");
        var invalid = () => RunWithInlineSchemaAsync("", "<n xsl:validation=\"strict\">x</n>");
        (await invalid.Should().ThrowAsync<Exception>()).Which.Message.Should().StartWith("XTTE1510:");
        var undeclared = () => RunWithInlineSchemaAsync("", "<other xsl:validation=\"strict\"/>");
        (await undeclared.Should().ThrowAsync<Exception>()).Which.Message.Should().StartWith("XTTE1512:");
    }

    /// <summary>
    /// A global variable whose body fails, and that nothing uses, leaves nothing in the result.
    /// What its body had written before the error came out ahead of the result.
    /// </summary>
    [Fact]
    public async Task What_a_failing_global_variable_wrote_is_not_in_the_result()
        => (await RunWithInlineSchemaAsync(
                "<xsl:variable name=\"unused\"><before/><n xsl:validation=\"strict\">x</n></xsl:variable>",
                "<r/>"))
            .Should().Be("<r/>");
}
