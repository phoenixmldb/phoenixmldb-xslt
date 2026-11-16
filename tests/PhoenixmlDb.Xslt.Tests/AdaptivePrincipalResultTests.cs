using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The principal result of the adaptive output method (Serialization 3.1 §10), as Saxon-HE 12.10
/// writes it: the items in the order the sequence constructor gives them, separated by a newline;
/// a string quoted, a map as <c>map{...}</c>, a node by the xml method with its own XML declaration
/// unless omit-xml-declaration is yes. An element written after an atomic value was dropped,
/// strings were bare and maps were JSON objects (W3C character-map-026, output-0721).
/// </summary>
public sealed class AdaptivePrincipalResultTests
{
    private const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>";

    private static async Task<string> Run(string output, string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              {{output}}
              <xsl:template name="xsl:initial-template">{{body}}</xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    private const string Adaptive = "<xsl:output method='adaptive'/>";
    private const string AdaptiveNoDecl = "<xsl:output method='adaptive' omit-xml-declaration='yes'/>";

    [Fact]
    public async Task An_element_after_an_atomic_value_is_kept_in_its_place()
        => (await Run(AdaptiveNoDecl, "<xsl:sequence select='1'/><e>a</e><xsl:sequence select='2'/>"))
            .Should().Be("1\n<e>a</e>\n2");

    [Fact]
    public async Task A_string_is_quoted_and_a_map_is_written_as_a_map()
        => (await Run(AdaptiveNoDecl, "<xsl:sequence select=\"'s', map{'k': 'v'}\"/>"))
            .Should().Be("\"s\"\nmap{\"k\":\"v\"}");

    [Fact]
    public async Task Each_node_has_its_own_xml_declaration()
        => (await Run(Adaptive, "<xsl:sequence select='1'/><e/><xsl:comment>c</xsl:comment>"))
            .Should().Be($"1\n{Decl}<e/>\n{Decl}<!--c-->");

    [Fact]
    public async Task An_attribute_has_no_xml_declaration()
        => (await Run(Adaptive, "<xsl:attribute name='a' select=\"'v'\"/>")).Should().Be("a=\"v\"");

    [Fact]
    public async Task The_declaration_names_the_output_encoding()
        => (await Run("<xsl:output method='adaptive' encoding='iso-8859-1'/>", "<e/>"))
            .Should().Be("<?xml version=\"1.0\" encoding=\"iso-8859-1\"?><e/>");

    [Fact]
    public async Task The_item_separator_separates_the_items()
        => (await Run("<xsl:output method='adaptive' omit-xml-declaration='yes' item-separator='|'/>",
                "<xsl:sequence select='1'/><e/><xsl:sequence select=\"'x'\"/>"))
            .Should().Be("1|<e/>|\"x\"");

    [Fact]
    public async Task A_character_map_applies_to_strings_and_markup()
        => (await Run("<xsl:output method='adaptive' omit-xml-declaration='yes' use-character-maps='m'/>"
                + "<xsl:character-map name='m'><xsl:output-character character='q' string='Q'/></xsl:character-map>",
                "<e>q</e><xsl:sequence select=\"'q', map{'q': 'q'}\"/>"))
            .Should().Be("<e>Q</e>\n\"Q\"\nmap{\"Q\":\"Q\"}");
}
