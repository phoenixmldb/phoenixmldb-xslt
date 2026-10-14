using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A type name with no prefix names a type in the default namespace for elements and types:
/// <c>xpath-default-namespace</c>, or no namespace. A type of an imported schema with no target
/// namespace could not be named at all: <c>'7' castable as small</c> was XPST0051 (xslt#359).
/// </summary>
public sealed class UnprefixedSchemaTypeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phx-unprefixed-type").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> RunAsync(string body, string? targetNamespace = null, string stylesheetAttributes = "")
    {
        var xsd = Path.Combine(_dir, "small.xsd");
        await File.WriteAllTextAsync(xsd, $"""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema"{(targetNamespace is null ? "" : $" targetNamespace=\"{targetNamespace}\"")}>
              <xs:simpleType name="small"><xs:restriction base="xs:integer"><xs:maxInclusive value="9"/></xs:restriction></xs:simpleType>
            </xs:schema>
            """, TestContext.Current.CancellationToken);
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            version="3.0" expand-text="yes" exclude-result-prefixes="#all" {stylesheetAttributes}>
              <xsl:output omit-xml-declaration="yes"/>
              <xsl:import-schema{(targetNamespace is null ? "" : $" namespace=\"{targetNamespace}\"")} schema-location="{xsd.Replace("\\", "/", StringComparison.Ordinal)}"/>
              <xsl:template match="/"><r>{body}</r></xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync("<in/>");
    }

    [Theory]
    [InlineData("{'7' castable as small} {'70' castable as small}", "true false")]
    [InlineData("{('7' cast as small) + 1}", "8")]
    [InlineData("{('7' cast as small) instance of small} {7 instance of small}", "true false")]
    [InlineData("{('7' cast as small) treat as small}", "7")]
    [InlineData("<xsl:variable name=\"v\" as=\"small\" select=\"'7' cast as small\"/>{$v + 1}", "8")]
    public async Task A_type_of_a_schema_with_no_target_namespace_is_named_with_no_prefix(string body, string expected)
        => (await RunAsync(body)).Should().Be($"<r>{expected}</r>");

    [Fact]
    public async Task With_xpath_default_namespace_the_name_is_in_that_namespace()
        => (await RunAsync("{'7' castable as small} {'70' castable as small}", "urn:x", "xpath-default-namespace=\"urn:x\""))
            .Should().Be("<r>true false</r>");

    [Fact]
    public async Task A_name_no_schema_declares_is_an_error()
    {
        var run = () => RunAsync("{'7' castable as nope}");
        (await run.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("nope");
    }

    [Fact]
    public async Task The_type_of_another_namespace_is_not_found_by_the_bare_name()
    {
        // The schema's type is in urn:x; with no xpath-default-namespace `small` is in no namespace.
        var run = () => RunAsync("{'7' castable as small}", "urn:x");
        (await run.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("small");
    }

    [Fact]
    public async Task With_the_schema_namespace_as_default_a_bare_name_is_a_built_in_type()
        => (await RunAsync("{'7' castable as integer}", null, "xpath-default-namespace=\"http://www.w3.org/2001/XMLSchema\""))
            .Should().Be("<r>true</r>");
}
