using System.Text.RegularExpressions;
using System.Xml.Linq;
using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xslt#628, BUGS.md #149: a namespace node in an element's content (xsl:namespace, or a copied
/// one: xsl:copy-of, xsl:copy) that binds a prefix the element has to another namespace. The two
/// kinds behave the same (XSLT 3.0 §11.7). Where the element's binding is one namespace fixup made
/// (xsl:element; an excluded prefix of a literal result element; xsl:copy with
/// copy-namespaces="no"), the element's name gets another prefix. Where it is a namespace node the
/// element copied (from the stylesheet, or from the source element), and between two namespace
/// nodes in the content, it is XTDE0430 (§5.7.1; W3C namespace-2618). Expected results: Saxon-HE
/// 10.9's for the same stylesheets, compared as XML (the new prefixes are the processor's choice),
/// except where noted.
/// </summary>
public partial class NamespaceNodeConflictTests
{
    private const string Source = """<doc xmlns:p="u2"><e/><p:f xmlns:p="u1"/></doc>""";

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

    private const string CopiedP = """<xsl:copy-of select="/doc/e/namespace::p"/>""";
    private const string NamespaceP = """<xsl:namespace name="p">u2</xsl:namespace>""";

    [Theory]
    // The issue's case: a copied namespace node. Main wrote <p:v xmlns:p="u1" xmlns:p="u2"/>.
    [InlineData("""<xsl:element name="p:v" namespace="u1">""" + CopiedP + "</xsl:element>",
        """<p_0:v xmlns:p="u2" xmlns:p_0="u1"/>""")]
    // xsl:namespace on xsl:element (already so on main).
    [InlineData("""<xsl:element name="p:v" namespace="u1">""" + NamespaceP + "</xsl:element>",
        """<p_0:v xmlns:p="u2" xmlns:p_0="u1"/>""")]
    // xsl:copy of a namespace node: it wrote the namespace URI as text.
    [InlineData("""<xsl:element name="p:v" namespace="u1"><xsl:for-each select="/doc/e/namespace::p"><xsl:copy/></xsl:for-each></xsl:element>""",
        """<p_0:v xmlns:p="u2" xmlns:p_0="u1"/>""")]
    // A literal result element whose prefix is excluded.
    [InlineData("""<p:v xmlns:p="u1" xsl:exclude-result-prefixes="p">""" + CopiedP + "</p:v>",
        """<p_0:v xmlns:p="u2" xmlns:p_0="u1"/>""")]
    // xsl:copy with copy-namespaces="no", a copied namespace node.
    [InlineData("""<xsl:for-each select="/doc/*[2]"><xsl:copy copy-namespaces="no">""" + CopiedP + "</xsl:copy></xsl:for-each>",
        """<p_0:f xmlns:p="u2" xmlns:p_0="u1"/>""")]
    // The same with xsl:namespace. Saxon-HE 10.9 raises XTDE0430 here; with copy-namespaces="no"
    // the binding of the name's prefix is one namespace fixup made (§11.9.1), as with xsl:element.
    [InlineData("""<xsl:for-each select="/doc/*[2]"><xsl:copy copy-namespaces="no">""" + NamespaceP + "</xsl:copy></xsl:for-each>",
        """<p_0:f xmlns:p="u2" xmlns:p_0="u1"/>""")]
    // The default namespace: the element gets a prefix. Main wrote <v xmlns="u1" xmlns="u2"/>.
    [InlineData("""<xsl:element name="v" namespace="u1"><xsl:namespace name="">u2</xsl:namespace></xsl:element>""",
        """<_0:v xmlns="u2" xmlns:_0="u1"/>""")]
    [InlineData("""<v xmlns="u1" xsl:exclude-result-prefixes="#default"><xsl:namespace name="">u2</xsl:namespace></v>""",
        """<_0:v xmlns="u2" xmlns:_0="u1"/>""")]
    // A child in the namespace the prefix had: it declares the prefix again. Main wrote <p:w/>,
    // which put the child into u2.
    [InlineData("""<xsl:element name="p:v" namespace="u1">""" + NamespaceP + """<xsl:element name="p:w" namespace="u1"/></xsl:element>""",
        """<p_0:v xmlns:p="u2" xmlns:p_0="u1"><p:w xmlns:p="u1"/></p_0:v>""")]
    // The element's new prefix is not one an attribute's declaration has taken already. Main
    // declared xmlns:p_0 twice.
    [InlineData("""<xsl:element name="p:v" namespace="u1"><xsl:attribute name="p:a" namespace="u3">1</xsl:attribute>""" + NamespaceP + "</xsl:element>",
        """<p_0:v xmlns:p="u2" xmlns:p_0="u1" xmlns:p_1="u3" p_1:a="1"/>""")]
    public async Task A_namespace_node_that_rebinds_the_prefix_of_the_name_gives_the_name_another_prefix(string body, string saxon)
    {
        var result = await Run(body);

        // Parsing fails for a result that is not well-formed (a prefix declared twice).
        var actual = XElement.Parse(result);
        var expected = XElement.Parse(saxon);
        Describe(actual).Should().Equal(Describe(expected), result);
    }

    /// <summary>
    /// The element's new prefix is not one of its own namespace nodes binds. Saxon-HE 10.9 gives
    /// the element <c>p_0</c> and drops the namespace node <c>p_0</c> = <c>u9</c>.
    /// </summary>
    [Fact]
    public async Task The_new_prefix_of_the_name_is_not_one_a_namespace_node_binds()
    {
        var result = await Run("""<xsl:element name="p:v" namespace="u1">""" + NamespaceP
            + """<xsl:namespace name="p_0">u9</xsl:namespace></xsl:element>""");

        var element = XElement.Parse(result);
        element.Name.Should().Be(XName.Get("v", "u1"), result);
        element.GetNamespaceOfPrefix("p")!.NamespaceName.Should().Be("u2");
        element.GetNamespaceOfPrefix("p_0")!.NamespaceName.Should().Be("u9");
    }

    /// <summary>The issue's case in a temporary tree: main gave XPTY0019 (the tree was not built).</summary>
    [Fact]
    public async Task In_a_temporary_tree_the_element_keeps_its_namespace()
    {
        var result = await Run("""<xsl:variable name="t"><xsl:element name="p:v" namespace="u1">""" + CopiedP
            + """</xsl:element></xsl:variable><o xsl:expand-text="yes">{namespace-uri($t/*)}|{namespace-uri-for-prefix('p', $t/*)}</o>""");

        result.Should().Be("<o>u1|u2</o>");
    }

    [Theory]
    // B1: a literal result element and a copied namespace node that binds one of its prefixes.
    // Saxon-HE 10.9 gives the name another prefix (the name's prefix) or drops the element's
    // namespace node (another prefix); the spec makes a copied namespace node behave as
    // xsl:namespace does (§11.7), which is XTDE0430 here (namespace-2618).
    [InlineData("""<p:v xmlns:p="u1">""" + CopiedP + "</p:v>", "the prefix 'p'", "'u2'", "'u1'")]
    [InlineData("""<v xmlns:p="u1">""" + CopiedP + "</v>", "the prefix 'p'", "'u2'", "'u1'")]
    // B2: xsl:namespace and a prefix of the literal result element other than its name's
    // (main wrote two declarations of the prefix; in a temporary tree, p = u1 won).
    [InlineData("""<v xmlns:p="u1">""" + NamespaceP + "</v>", "the prefix 'p'", "'u2'", "'u1'")]
    [InlineData("""<v xmlns="u1"><xsl:namespace name="">u2</xsl:namespace></v>""", "the default namespace", "'u2'", "'u1'")]
    // The name's prefix (already so on main).
    [InlineData("""<p:v xmlns:p="u1">""" + NamespaceP + "</p:v>", "the prefix 'p'", "'u2'", "'u1'")]
    // B3: xsl:copy with the source element's namespace nodes (copy-namespaces="yes"). For the
    // copied namespace node Saxon-HE 10.9 gives the name another prefix.
    [InlineData("""<xsl:for-each select="/doc/*[2]"><xsl:copy>""" + NamespaceP + "</xsl:copy></xsl:for-each>", "the prefix 'p'", "'u2'", "'u1'")]
    [InlineData("""<xsl:for-each select="/doc/*[2]"><xsl:copy>""" + CopiedP + "</xsl:copy></xsl:for-each>", "the prefix 'p'", "'u2'", "'u1'")]
    // B4: two namespace nodes in the content (main kept the last one).
    [InlineData("<r>" + CopiedP + """<xsl:namespace name="p">u3</xsl:namespace></r>""", "the prefix 'p'", "'u2'", "'u3'")]
    [InlineData("<r>" + CopiedP + """<xsl:copy-of select="/doc/*[2]/namespace::p"/></r>""", "the prefix 'p'", "'u2'", "'u1'")]
    [InlineData("<r>" + NamespaceP + """<xsl:namespace name="p">u3</xsl:namespace></r>""", "the prefix 'p'", "'u2'", "'u3'")]
    public async Task A_namespace_node_that_rebinds_a_namespace_node_of_the_element_is_XTDE0430(
        string body, string prefix, string first, string second)
    {
        var run = () => Run(body);

        var message = (await run.Should().ThrowAsync<XsltException>()).Which.Message;
        message.Should().Contain("XTDE0430").And.Contain(prefix).And.Contain(first).And.Contain(second);
    }

    private const string DefaultU2 = """<xsl:variable name="d"><d xmlns="u2"/></xsl:variable>""";
    private const string CopiedDefault = """<xsl:copy-of select="$d/*/namespace::*[name()='']"/>""";

    /// <summary>
    /// A copied namespace node for the default namespace, on an element in no namespace: XTDE0440
    /// (§5.7.1), as for xsl:namespace (Lucas, 2026-11-23). Main wrote <c>&lt;v xmlns="u2"/&gt;</c>,
    /// which moved the element into that namespace, in a temporary tree too. Saxon-HE 10.9:
    /// XTDE0440.
    /// </summary>
    [Theory]
    [InlineData(DefaultU2 + "<v>" + CopiedDefault + "</v>")]
    [InlineData(DefaultU2 + """<xsl:element name="v">""" + CopiedDefault + "</xsl:element>")]
    [InlineData(DefaultU2 + """<xsl:for-each select="/doc"><xsl:copy>""" + CopiedDefault + "</xsl:copy></xsl:for-each>")]
    [InlineData(DefaultU2 + """<xsl:variable name="t"><v>""" + CopiedDefault + """</v></xsl:variable><o xsl:expand-text="yes">{namespace-uri($t/*)}</o>""")]
    public async Task A_copied_default_namespace_node_on_an_element_in_no_namespace_is_XTDE0440(string body)
    {
        var run = () => Run(body);

        var message = (await run.Should().ThrowAsync<XsltException>()).Which.Message;
        message.Should().Contain("XTDE0440").And.Contain("'u2'");
    }

    /// <summary>On an element in that namespace, the copied node is the element's own binding.</summary>
    [Theory]
    [InlineData(DefaultU2 + """<xsl:element name="v" namespace="u2">""" + CopiedDefault + "</xsl:element>")]
    [InlineData(DefaultU2 + """<v xmlns="u2">""" + CopiedDefault + "</v>")]
    public async Task A_copied_default_namespace_node_on_an_element_in_that_namespace_is_its_binding(string body)
        => (await Run(body)).Should().Be("""<v xmlns="u2"/>""");

    // Each element: its expanded name, its attributes by expanded name and value, and the
    // declarations of the prefixes other than those namespace fixup makes (name_0, _0, ...).
    private static List<string> Describe(XElement root) =>
        root.DescendantsAndSelf().Select(e =>
            e.Name + " "
            + string.Join(" ", e.Attributes().Where(a => !a.IsNamespaceDeclaration)
                .Select(a => $"{a.Name}={a.Value}").Order(StringComparer.Ordinal))
            + " | "
            + string.Join(" ", e.Attributes()
                .Where(a => a.IsNamespaceDeclaration && !GeneratedPrefix().IsMatch(a.Name.LocalName))
                .Select(a => $"{a.Name.LocalName}={a.Value}").Order(StringComparer.Ordinal)))
        .ToList();

    [GeneratedRegex(@"_[0-9]+$")]
    private static partial Regex GeneratedPrefix();
}
