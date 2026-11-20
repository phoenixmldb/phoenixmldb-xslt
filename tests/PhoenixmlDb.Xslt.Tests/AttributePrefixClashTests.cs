using System.Xml.Linq;
using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// One element cannot bind a prefix to two namespaces, so when an attribute's prefix is already
/// bound to another namespace on the element being constructed (by an earlier attribute, a
/// namespace node, or the element's own name), namespace fixup gives the attribute another prefix
/// (XSLT 3.0 §5.7.3). Both attributes must survive, and the output must be well-formed. Which
/// prefix is chosen is not specified, so the tests compare expanded names and values.
/// </summary>
public class AttributePrefixClashTests
{
    private static async Task<XElement> TransformAsync(string body, string extraNamespaces = "")
    {
        var stylesheet = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" {extraNamespaces}>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheet);
        return XElement.Parse(await transformer.TransformAsync("<r/>")); // throws if not well-formed
    }

    private static string[] Attributes(XElement e) =>
        [.. e.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => $"{{{a.Name.NamespaceName}}}{a.Name.LocalName}={a.Value}").Order()];

    [Fact]
    public async Task OnePrefix_TwoNamespaces_BothAttributesKept()
    {
        var e = await TransformAsync("""<out><xsl:attribute name="p:att" namespace="urn:one">1</xsl:attribute><xsl:attribute name="p:att" namespace="urn:two">2</xsl:attribute></out>""");
        Attributes(e).Should().Equal("{urn:one}att=1", "{urn:two}att=2");
    }

    [Fact]
    public async Task StylesheetPrefixRenamedTwice_BothAttributesKept()
    {
        var e = await TransformAsync(
            """<out><xsl:attribute name="p:a" namespace="urn:one">1</xsl:attribute><xsl:attribute name="p:b" namespace="urn:two">2</xsl:attribute></out>""",
            """xmlns:p="urn:x" exclude-result-prefixes="p" """);
        Attributes(e).Should().Equal("{urn:one}a=1", "{urn:two}b=2");
    }

    [Fact]
    public async Task PrefixOfTheElementName_IsNotReboundByAnAttribute()
    {
        var e = await TransformAsync("""<xsl:element name="p:out" namespace="urn:e"><xsl:attribute name="p:att" namespace="urn:two">2</xsl:attribute></xsl:element>""");
        e.Name.NamespaceName.Should().Be("urn:e");
        Attributes(e).Should().Equal("{urn:two}att=2");
    }

    [Fact]
    public async Task PrefixOfANamespaceNode_IsNotReboundByAnAttribute()
    {
        var e = await TransformAsync("""<out><xsl:namespace name="p" select="'urn:one'"/><xsl:attribute name="p:att" namespace="urn:two">2</xsl:attribute><xsl:attribute name="q:z" namespace="urn:one">3</xsl:attribute></out>""");
        Attributes(e).Should().Equal("{urn:one}z=3", "{urn:two}att=2");
        e.GetNamespaceOfPrefix("p")!.NamespaceName.Should().Be("urn:one");
    }

    [Fact]
    public async Task SameExpandedName_DifferentPrefixes_LastWins()
    {
        var e = await TransformAsync("""<out><xsl:attribute name="a:att" namespace="urn:u">1</xsl:attribute><xsl:attribute name="b:att" namespace="urn:u">2</xsl:attribute></out>""");
        Attributes(e).Should().Equal("{urn:u}att=2");
    }
}
