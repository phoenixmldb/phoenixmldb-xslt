using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// fn:generate-id gives the same NCName for the same node and different ones for different nodes
/// (XPath F&amp;O 3.1 §14.6.4). It was the hash code of the node object, so different nodes could
/// share an id: with many nodes some always did, and W3C streamable-007 (929 chapters, all ids
/// distinct) failed now and then.
/// </summary>
public sealed class GenerateIdIdentityTests
{
    private static async Task<string> Run(string body, string source)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">{{body}}</xsl:template>
            </xsl:stylesheet>
            """);
        return (await t.TransformAsync(source)).Trim();
    }

    private static readonly string ManyElements =
        "<r>" + string.Concat(Enumerable.Repeat("<e a='1'/>", 100_000)) + "</r>";

    [Fact]
    public async Task Different_nodes_have_different_ids()
        => (await Run("<xsl:value-of select='count(distinct-values((//e, //@a) ! generate-id(.))), count((//e, //@a))'/>", ManyElements))
            .Should().Be("200000 200000");

    [Fact]
    public async Task The_same_node_has_the_same_id_by_any_path()
        => (await Run("<xsl:value-of select='generate-id(/r/e[2]) eq generate-id((//e)[2]), generate-id(/r/e[2]/@a) eq generate-id((//@a)[2]), generate-id(/r/e[2]) eq generate-id(/r/e[3])'/>",
                "<r><e a='1'/><e a='2'/><e a='3'/></r>"))
            .Should().Be("true true false");

    [Fact]
    public async Task The_id_is_an_NCName()
        => (await Run("<xsl:value-of select='every $n in (/, //node(), //@*) satisfies generate-id($n) castable as xs:NCName' xmlns:xs='http://www.w3.org/2001/XMLSchema'/>",
                "<r x='1'><e>t</e><!--c--></r>"))
            .Should().Be("true");

    [Fact]
    public async Task The_context_item_form_agrees()
        => (await Run("<xsl:value-of select='(/r/e[2] ! generate-id()) eq generate-id(/r/e[2])'/>", "<r><e/><e/></r>"))
            .Should().Be("true");
}
