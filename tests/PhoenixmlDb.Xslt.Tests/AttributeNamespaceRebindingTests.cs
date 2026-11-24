using System.Text.RegularExpressions;
using System.Xml.Linq;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// BUGS.md #148: an attribute written before a namespace node (xsl:namespace, a copied namespace
/// node) that binds the attribute's prefix to another namespace moved into that namespace. A
/// namespace node never changes the name of an attribute (XSLT 3.0 §11.7): the attribute keeps its
/// namespace, under another prefix (namespace fixup, §5.7.3). The expected results are Saxon-HE
/// 10.9's for the same stylesheets. The prefixes that fixup makes are the processor's choice (here
/// <c>p_0</c>, in Saxon <c>p_1</c>), so the serialized results are compared as XML: the element's
/// name, the attributes by expanded name and value, and the declarations of the other prefixes.
/// </summary>
public partial class AttributeNamespaceRebindingTests
{
    private const string Source = """<doc xmlns:p="u1" xmlns:q="u2" p:a="src1" q:a="src2"/>""";

    private static async Task<string> Run(string body)
    {
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(
            """<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">"""
            + """<xsl:output omit-xml-declaration="yes"/><xsl:template match="/">"""
            + body
            + "</xsl:template></xsl:stylesheet>");
        return await transformer.TransformAsync(Source);
    }

    [Theory]
    // The four forms: a literal result element, xsl:element, xsl:copy, a copied attribute.
    [InlineData("""<r><xsl:attribute name="p:a" namespace="u1">1</xsl:attribute><xsl:namespace name="p">u2</xsl:namespace></r>""",
        """<r xmlns:p="u2" xmlns:p_1="u1" p_1:a="1"/>""")]
    [InlineData("""<xsl:element name="r"><xsl:attribute name="p:a" namespace="u1">1</xsl:attribute><xsl:namespace name="p">u2</xsl:namespace></xsl:element>""",
        """<r xmlns:p="u2" xmlns:p_1="u1" p_1:a="1"/>""")]
    [InlineData("""<xsl:for-each select="/doc"><xsl:copy><xsl:attribute name="z:a" namespace="u5">1</xsl:attribute><xsl:namespace name="z">u6</xsl:namespace></xsl:copy></xsl:for-each>""",
        """<doc xmlns:p="u1" xmlns:q="u2" xmlns:z="u6" xmlns:z_1="u5" z_1:a="1"/>""")]
    [InlineData("""<r><xsl:copy-of select="/doc/@*[1]"/><xsl:namespace name="p">u7</xsl:namespace></r>""",
        """<r xmlns:p="u7" xmlns:p_1="u1" p_1:a="src1"/>""")]
    // The namespace node first: the attribute's prefix was rebound already when it was written.
    [InlineData("""<r><xsl:namespace name="p">u2</xsl:namespace><xsl:attribute name="p:a" namespace="u1">1</xsl:attribute></r>""",
        """<r xmlns:p="u2" xmlns:p_1="u1" p_1:a="1"/>""")]
    // Two attributes whose prefix the namespace node rebinds, in two namespaces.
    [InlineData("""<r><xsl:attribute name="p:a" namespace="u1">1</xsl:attribute><xsl:attribute name="p:b" namespace="u3">2</xsl:attribute><xsl:namespace name="p">u2</xsl:namespace></r>""",
        """<r xmlns:p="u2" xmlns:p_1="u1" xmlns:p_2="u3" p_1:a="1" p_2:b="2"/>""")]
    // The element's prefix is rebound too, and the element gets a new prefix: an attribute in the
    // element's namespace is declared once (with the first version of this change, the element and
    // the attribute both declared xmlns:p_0).
    [InlineData("""<xsl:element name="p:v" namespace="u1"><xsl:attribute name="p:a" namespace="u1">1</xsl:attribute><xsl:namespace name="p">u2</xsl:namespace></xsl:element>""",
        """<p_0:v xmlns:p="u2" xmlns:p_0="u1" xmlns:p_1="u1" p_1:a="1"/>""")]
    [InlineData("""<p:v xmlns:p="u1" xsl:exclude-result-prefixes="p"><xsl:attribute name="p:a" namespace="u1">1</xsl:attribute><xsl:namespace name="p">u2</xsl:namespace></p:v>""",
        """<p_0:v xmlns:p="u2" xmlns:p_0="u1" xmlns:p_1="u1" p_1:a="1"/>""")]
    [InlineData("""<p:v xmlns:p="u1" xsl:exclude-result-prefixes="p"><xsl:attribute name="p:a" namespace="u3">1</xsl:attribute><xsl:namespace name="p">u2</xsl:namespace></p:v>""",
        """<p_0:v xmlns:p="u2" xmlns:p_0="u1" xmlns:p_1="u3" p_1:a="1"/>""")]
    public async Task An_attribute_keeps_its_namespace_when_a_namespace_node_rebinds_its_prefix(string body, string saxon)
    {
        var result = await Run(body);

        // Parsing fails for a result that is not well-formed (a prefix declared twice).
        var actual = XElement.Parse(result);
        var expected = XElement.Parse(saxon);
        actual.Name.Should().Be(expected.Name, result);
        Attributes(actual).Should().BeEquivalentTo(Attributes(expected), result);
        Declarations(actual).Should().BeEquivalentTo(Declarations(expected), result);
    }

    /// <summary>The four forms in a temporary tree: the attribute's namespace and the bindings of the element.</summary>
    [Theory]
    [InlineData("""<r><xsl:attribute name="p:a" namespace="u1">1</xsl:attribute><xsl:namespace name="p">u2</xsl:namespace></r>""",
        "<o>u1=1|u2</o>")]
    [InlineData("""<xsl:element name="r"><xsl:attribute name="p:a" namespace="u1">1</xsl:attribute><xsl:namespace name="p">u2</xsl:namespace></xsl:element>""",
        "<o>u1=1|u2</o>")]
    [InlineData("""<xsl:for-each select="/doc"><xsl:copy><xsl:attribute name="z:a" namespace="u5">1</xsl:attribute><xsl:namespace name="z">u6</xsl:namespace></xsl:copy></xsl:for-each>""",
        "<o>u5=1|u1u6</o>")]
    [InlineData("""<r><xsl:copy-of select="/doc/@*[1]"/><xsl:namespace name="p">u7</xsl:namespace></r>""",
        "<o>u1=src1|u7</o>")]
    public async Task In_a_temporary_tree_the_attribute_keeps_its_namespace(string body, string saxon)
    {
        var result = await Run(
            """<xsl:variable name="t">""" + body + "</xsl:variable>"
            + """<o xsl:expand-text="yes">{string-join($t/*/@*!(namespace-uri(.)||'='||.), ' ')}|{namespace-uri-for-prefix('p',$t/*)}{namespace-uri-for-prefix('z',$t/*)}</o>""");

        result.Should().Be(saxon);
    }

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
