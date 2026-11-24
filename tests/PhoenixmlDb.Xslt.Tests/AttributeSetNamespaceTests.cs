using System.Text.RegularExpressions;
using System.Xml.Linq;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// BUGS.md #151 (the typed attribute pipeline, S3): an attribute from an attribute set kept its
/// prefix but not its namespace. The sets were written as text and read back without namespaces,
/// so an attribute whose prefix a namespace node in the element's content rebound moved into that
/// namespace, as BUGS.md #148 was for the content's own attributes. The expected results are
/// Saxon-HE 10.9's for the same stylesheets, compared as XML where namespace fixup chose a prefix
/// (here <c>p_0</c>, in Saxon <c>p_1</c>).
/// </summary>
public partial class AttributeSetNamespaceTests
{
    private const string Sets =
        """<xsl:attribute-set name="s"><xsl:attribute name="p:a" namespace="u1">1</xsl:attribute></xsl:attribute-set>"""
        + """<xsl:attribute-set name="t" use-attribute-sets="s"><xsl:attribute name="q:b" namespace="u3">2</xsl:attribute></xsl:attribute-set>"""
        + """<xsl:attribute-set name="pr"><xsl:attribute name="a">set</xsl:attribute><xsl:attribute name="b">set</xsl:attribute><xsl:attribute name="c">set</xsl:attribute></xsl:attribute-set>""";

    private static async Task<string> Run(string body)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(
            """<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"><xsl:output omit-xml-declaration="yes"/>"""
            + Sets + """<xsl:template match="/">""" + body + "</xsl:template></xsl:stylesheet>");
        return await transformer.TransformAsync("<x/>");
    }

    [Theory]
    [InlineData("""<r xsl:use-attribute-sets="s"><xsl:namespace name="p">u2</xsl:namespace></r>""",
        """<r xmlns:p="u2" xmlns:p_1="u1" p_1:a="1"/>""")]
    [InlineData("""<xsl:element name="r" use-attribute-sets="s"><xsl:namespace name="p">u2</xsl:namespace></xsl:element>""",
        """<r xmlns:p="u2" xmlns:p_1="u1" p_1:a="1"/>""")]
    [InlineData("""<xsl:for-each select="/x"><xsl:copy use-attribute-sets="s"><xsl:namespace name="p">u2</xsl:namespace></xsl:copy></xsl:for-each>""",
        """<x xmlns:p="u2" xmlns:p_1="u1" p_1:a="1"/>""")]
    // A set that uses another set; two prefixes rebound.
    [InlineData("""<r xsl:use-attribute-sets="t"><xsl:namespace name="p">u2</xsl:namespace><xsl:namespace name="q">u4</xsl:namespace></r>""",
        """<r xmlns:p="u2" xmlns:p_1="u1" xmlns:q="u4" xmlns:q_2="u3" p_1:a="1" q_2:b="2"/>""")]
    // The prefix bound on the literal result element itself: unchanged (it was right).
    [InlineData("""<r xsl:use-attribute-sets="s" xmlns:p="u2"/>""",
        """<r xmlns:p="u2" xmlns:p_1="u1" p_1:a="1"/>""")]
    public async Task An_attribute_from_an_attribute_set_keeps_its_namespace(string body, string saxon)
    {
        var result = await Run(body);

        var actual = XElement.Parse(result);
        var expected = XElement.Parse(saxon);
        actual.Name.Should().Be(expected.Name, result);
        Attributes(actual).Should().BeEquivalentTo(Attributes(expected), result);
        Declarations(actual).Should().BeEquivalentTo(Declarations(expected), result);
    }

    [Fact]
    public async Task In_a_temporary_tree_too()
        => (await Run("""<xsl:variable name="v"><r xsl:use-attribute-sets="t"><xsl:namespace name="p">u2</xsl:namespace><xsl:namespace name="q">u4</xsl:namespace></r></xsl:variable>"""
                + """<xsl:value-of select="$v/*/@*!(namespace-uri(.)||'='||.)"/>"""))
            .Should().Be("u1=1 u3=2");

    /// <summary>The attributes of the sets have the lowest priority: literal attributes, then the content's, over them.</summary>
    [Theory]
    [InlineData("""<r a="lit" b="lit" xsl:use-attribute-sets="pr"><xsl:attribute name="b">content</xsl:attribute></r>""",
        """<r a="lit" b="content" c="set"/>""")]
    [InlineData("""<xsl:element name="r" use-attribute-sets="pr"><xsl:attribute name="b">content</xsl:attribute></xsl:element>""",
        """<r a="set" b="content" c="set"/>""")]
    public async Task The_sets_are_overridden_as_before(string body, string saxon)
        => (await Run(body)).Should().Be(saxon);

    private static IEnumerable<string> Attributes(XElement element) =>
        element.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => $"{a.Name}={a.Value}");

    // The prefixes namespace fixup makes (name_0, name_1, ...) are the processor's choice.
    private static IEnumerable<string> Declarations(XElement element) =>
        element.Attributes()
            .Where(a => a.IsNamespaceDeclaration && !GeneratedPrefix().IsMatch(a.Name.LocalName))
            .Select(a => $"{a.Name.LocalName}={a.Value}");

    [GeneratedRegex(@"_[0-9]+$")]
    private static partial Regex GeneratedPrefix();
}
