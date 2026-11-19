using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Every element json-to-xml makes has the default namespace http://www.w3.org/2005/xpath-functions
/// in scope, so namespace::* on a child gives it and "xml" (Saxon-HE 12.10). The elements are
/// constructed, the in-scope set is read from each element's own declarations, and only the root
/// had one: a child gave "xml" alone (W3C static-030).
/// </summary>
public sealed class JsonToXmlInScopeNamespaceTests
{
    private static async Task<string> Run(string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:template name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return await t.TransformAsync((string?)null);
    }

    private const string Json = "json-to-xml('{&quot;a&quot;:1, &quot;b&quot;:[true]}')";

    [Fact]
    public async Task Every_element_has_the_default_namespace_and_xml_in_scope()
        => (await Run($"""<xsl:value-of select="for $e in {Json}//* return local-name($e) || count($e/namespace::*)"/>"""))
            .Should().Be("map2 number2 array2 boolean2");

    [Fact]
    public async Task The_namespace_is_declared_once_when_copied_to_the_result()
        => (await Run($"""<xsl:copy-of select="{Json}"/>"""))
            .Should().Be("""<map xmlns="http://www.w3.org/2005/xpath-functions"><number key="a">1</number><array key="b"><boolean>true</boolean></array></map>""");
}
