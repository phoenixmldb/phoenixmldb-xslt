using AwesomeAssertions;
using PhoenixmlDb.Xdm.Nodes;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <see cref="XsltTransformer.PrincipalResultTree"/>: the principal result as a tree with the
/// type annotations that validation gave it. The result was available only as text, and the
/// text read again is untyped.
/// </summary>
public sealed class PrincipalResultTreeTests
{
    private static async Task<XsltTransformer> RunAsync(string template)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema"
                            xmlns:r="urn:r" version="3.0">
              <xsl:import-schema namespace="urn:r">
                <xs:schema targetNamespace="urn:r" elementFormDefault="qualified">
                  <xs:element name="doc"><xs:complexType><xs:sequence>
                    <xs:element name="n" type="xs:integer" maxOccurs="unbounded"/>
                  </xs:sequence><xs:attribute name="id" type="xs:ID"/></xs:complexType></xs:element>
                </xs:schema>
              </xsl:import-schema>
              {{template}}
            </xsl:stylesheet>
            """);
        await t.TransformAsync("<x/>");
        return t;
    }

    [Fact]
    public async Task A_result_validated_as_it_is_built_is_kept_as_a_typed_tree()
    {
        var t = await RunAsync("""<xsl:template match="/"><r:doc xsl:validation="strict" id="a"><r:n>1</r:n><r:n>2</r:n></r:doc></xsl:template>""");
        var tree = t.PrincipalResultTree;
        tree.Should().NotBeNull();
        var root = (XdmElement)tree!.Nodes.GetNode(tree.Document.DocumentElement!.Value)!;
        root.LocalName.Should().Be("doc");
        root.TypeAnnotation.Should().NotBe(PhoenixmlDb.Xdm.XdmTypeName.Untyped);
        var first = (XdmElement)tree.Nodes.GetNode(root.Children[0])!;
        first.TypeAnnotation.LocalName.Should().Be("integer");
    }

    [Fact]
    public async Task A_result_that_was_not_validated_has_no_tree()
        => (await RunAsync("""<xsl:template match="/"><r:doc><r:n>1</r:n></r:doc></xsl:template>""")).PrincipalResultTree.Should().BeNull();

    [Fact]
    public async Task A_result_with_more_than_the_validated_element_has_no_tree()
        => (await RunAsync("""<xsl:template match="/"><r:doc xsl:validation="strict"><r:n>1</r:n></r:doc><extra/></xsl:template>""")).PrincipalResultTree.Should().BeNull();
}
