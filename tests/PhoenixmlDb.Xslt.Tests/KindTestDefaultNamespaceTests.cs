using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An unprefixed element name in a kind test, element(elem), takes xpath-default-namespace as
/// the same name in a plain step does (XSLT 3.0 §5.1.2). It was matched against no-namespace
/// elements only, so with the default namespace set, element(elem) found nothing where elem
/// found the element (W3C xpath-default-namespace-0702).
/// </summary>
public sealed class KindTestDefaultNamespaceTests
{
    private const string Input = """<d xmlns="urn:d"><elem a="1">x</elem><elem>y</elem></d>""";

    private static async Task<string> Run(string select, string xdn = " xpath-default-namespace='urn:d'")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"{xdn}>
              <xsl:output method="text"/>
              <xsl:template match="/"><xsl:value-of select="{select}"/></xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync(Input)).Trim();
    }

    [Theory]
    [InlineData("count(d/element(elem))", "2")]
    [InlineData("count(d/element(elem)) = count(d/elem)", "true")]
    [InlineData("count(self::document-node(element(d)))", "1")]
    [InlineData("count(d/elem/attribute(a))", "1")]
    public async Task The_kind_test_name_takes_the_default_namespace(string select, string expected)
        => (await Run(select)).Should().Be(expected);

    [Fact]
    public async Task Without_the_default_namespace_it_matches_no_namespace_elements_only()
        => (await Run("count(*/element(elem))", xdn: "")).Should().Be("0");
}
