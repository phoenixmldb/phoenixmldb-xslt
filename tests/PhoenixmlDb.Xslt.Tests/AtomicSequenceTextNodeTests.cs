using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A variable whose type is a sequence of atomic values gets one value for each text node its
/// content makes (XSLT 3.0 §9.4: the sequence is atomized). The text of the content was written
/// into one buffer, so two <c>xsl:value-of</c> gave one value of the two strings joined
/// (W3C as-1801). The expected values are Saxon-HE 12's.
/// </summary>
public class AtomicSequenceTextNodeTests
{
    private static async Task<string> Run(string declarations, string body)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output omit-xml-declaration="yes"/>
              {declarations}
              <xsl:template name="main">{body}</xsl:template>
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("xs:anyAtomicType*")]
    [InlineData("xs:string*")]
    [InlineData("xs:untypedAtomic+")]
    public async Task Each_value_of_is_one_value(string type)
        => (await Run(
                $"""<xsl:variable name="g" as="{type}"><xsl:value-of select="'a'"/><xsl:value-of select="'b'"/></xsl:variable>""",
                $"""
                <xsl:variable name="l" as="{type}"><xsl:value-of select="'a'"/><xsl:value-of select="'b'"/></xsl:variable>
                <r><xsl:value-of select="count($g), count($l), string-join($l, '|')"/></r>
                """))
            .Should().Be("<r>2 2 a|b</r>");

    /// <summary>
    /// A template with an atomic sequence type returns one value for each text node, and the
    /// values are written separated by a space (W3C as-1803). Called with xsl:call-template its
    /// text nodes were not kept apart, and from apply-templates they were kept as text nodes,
    /// which merged where they were written.
    /// </summary>
    [Fact]
    public async Task A_template_returns_one_value_for_each_text_node()
        => (await Run("""
                <xsl:template name="t" as="xs:anyAtomicType+"><xsl:value-of select="'a'"/><xsl:text> * </xsl:text><xsl:value-of select="1"/></xsl:template>
                <xsl:template match="." mode="m" as="xs:string*"><xsl:value-of select="'p'"/><xsl:value-of select="'q'"/></xsl:template>
                <xsl:function name="Q{urn:f}f" as="xs:string*"><xsl:value-of select="'a'"/><xsl:text> * </xsl:text></xsl:function>
                """, """
                <r><a><xsl:call-template name="t"/></a><b><xsl:apply-templates select="1" mode="m"/></b><c><xsl:value-of select="count(Q{urn:f}f())"/></c></r>
                """))
            .Should().Be("<r><a>a  *  1</a><b>p q</b><c>2</c></r>");

    [Fact]
    public async Task Literal_text_sequence_items_and_casts_keep_their_own_values()
        => (await Run("", """
                <xsl:variable name="a" as="xs:string*">abc<xsl:value-of select="'d'"/></xsl:variable>
                <xsl:variable name="b" as="xs:integer*"><xsl:value-of select="1"/><xsl:value-of select="2"/></xsl:variable>
                <xsl:variable name="c" as="xs:string+"><xsl:value-of select="()"/></xsl:variable>
                <xsl:variable name="d" as="xs:string*"><xsl:value-of select="'a', 'b'"/></xsl:variable>
                <xsl:variable name="e" as="xs:string*"><xsl:sequence select="'a'"/><xsl:value-of select="'b'"/><xsl:text>c</xsl:text></xsl:variable>
                <r><xsl:value-of select="count($a), sum($b), count($b), count($c), count($d), string-join($e, '|')"/></r>
                """))
            .Should().Be("<r>2 3 2 1 1 a|b|c</r>");
}
