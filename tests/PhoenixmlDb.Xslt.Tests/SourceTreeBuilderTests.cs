using System.Xml;
using AwesomeAssertions;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The tree builder (<see cref="XsltTransformEngine.ConvertToXdm"/>) gives each element the in-scope
/// namespaces, string value and base URI that System.Xml reports for its node. It asked System.Xml
/// for them per element (<c>GetNamespacesInScope</c> and <c>BaseURI</c> walk the ancestors,
/// <c>InnerText</c> the descendants), so a tree of depth N took time about N squared to build
/// (#639); it now computes them from the parent's and the children's. The expected values here are
/// System.Xml's per-element answers, as the builder read them before.
/// </summary>
public sealed class SourceTreeBuilderTests
{
    private const string DocumentUri = "http://example.org/doc.xml";

    private static readonly string[] Documents =
    [
        // Default namespaces nested and undeclared, a prefix declared again, an element prefix
        // declared on an ancestor, two prefixes for one namespace.
        """<r xmlns="urn:d" xmlns:p="urn:p1"><p:a xmlns:q="urn:q"><b xmlns=""><c xmlns:p="urn:p2"><p:d/><q:e xmlns:z="urn:p1"/></c></b></p:a><e xmlns="urn:e2"><f/><g xmlns=""/></e></r>""",
        // Mixed content: text, CDATA, a comment and a processing instruction, whitespace, empty elements.
        "<r>t1<a>x<![CDATA[y]]><!--c--><?pi d?>z<b/></a>\n  <c> <d/> </c>t2<e><f><g>deep</g></f></e></r>",
        // xml:base on the way down (the builder keeps it as an attribute).
        """<r xml:base="http://example.org/a/"><s xml:base="b/"><t xml:base="c.xml"><?pi x?></t></s></r>""",
        // An internal entity: XmlDocument.LoadXml keeps an entity reference node, whose text is
        // part of the string value of the element that contains it.
        """<!DOCTYPE r [<!ENTITY e "<x>ent</x>tail">]><r><a>&e;</a>&e;<b>&e;<c/></b></r>""",
    ];

    public static IEnumerable<object[]> Loaded()
    {
        foreach (var xml in Documents)
        {
            yield return [xml, "LoadExternalXml"];
            yield return [xml, "LoadXml"];
        }
    }

    [Theory]
    [MemberData(nameof(Loaded))]
    public void Each_element_has_the_namespaces_string_value_and_base_URI_that_System_Xml_gives_it(string xml, string loader)
    {
        XmlDocument doc;
        if (loader == "LoadXml")
        {
            doc = new XmlDocument { PreserveWhitespace = true };
            doc.LoadXml(xml);
        }
        else
        {
            doc = XsltTransformEngine.LoadExternalXml(xml);
        }
        var store = new XdmInMemoryStore();
        var converted = XsltTransformEngine.ConvertToXdm(doc, store, DocumentUri);

        var expected = ConvertedElements(doc.DocumentElement!).ToList();
        var actual = Elements(store, store.GetNode(converted.DocumentElement!.Value)!).ToList();

        actual.Should().HaveCount(expected.Count);
        expected.Count.Should().BeGreaterThan(2);
        for (var i = 0; i < expected.Count; i++)
        {
            var (xmlElem, elem) = (expected[i], actual[i]);
            elem.LocalName.Should().Be(xmlElem.LocalName);
            var bindings = elem.NamespaceDeclarations
                .Select(b => (b.Prefix ?? "", b.Namespace == Core.NamespaceId.None ? "" : store.GetNamespaceUri(b.Namespace)))
                .ToList();
            bindings.Should().Equal(SystemXmlBindings(xmlElem), $"the in-scope namespaces of <{xmlElem.Name}>, in order");
            elem.StringValue.Should().Be(xmlElem.InnerText, $"the string value of <{xmlElem.Name}>");
            elem.BaseUri.Should().Be(EntityBaseUri(xmlElem.BaseURI), $"the base URI of <{xmlElem.Name}>");
            foreach (var pi in xmlElem.ChildNodes.OfType<XmlProcessingInstruction>())
            {
                var xdmPi = elem.Children.Select(id => store.GetNode(id)).OfType<XdmProcessingInstruction>().Single(p => p.Target == pi.Target);
                xdmPi.BaseUri.Should().Be(EntityBaseUri(pi.BaseURI));
            }
        }
    }

    [Fact]
    public void A_document_10000_elements_deep_is_built_with_each_elements_namespaces_and_string_value()
    {
        const int depth = 10_000;
        var xml = "<a xmlns='urn:d' xmlns:p='urn:p'>" + string.Concat(Enumerable.Repeat("<p:a>t", depth - 1))
            + string.Concat(Enumerable.Repeat("</p:a>", depth - 1)) + "</a>";
        var store = new XdmInMemoryStore();

        // The builder recurses once per level, as a transformation does: on a thread with a stack as
        // large as a transformation's, not on the test thread (1 MB on Windows).
        XdmDocument? converted = null;
        var builder = new Thread(() => converted = XsltTransformEngine.ConvertToXdm(XsltTransformEngine.LoadExternalXml(xml), store, DocumentUri), 256 * 1024 * 1024);
        builder.Start();
        builder.Join();
        converted.Should().NotBeNull();

        var elem = (XdmElement)store.GetNode(converted!.DocumentElement!.Value)!;
        var level = 1;
        while (elem.Children.Select(id => store.GetNode(id)).OfType<XdmElement>().FirstOrDefault() is { } child)
        {
            elem = child;
            level++;
        }
        level.Should().Be(depth);
        elem.StringValue.Should().Be("t");
        ((XdmElement)store.GetNode(converted.DocumentElement!.Value)!).StringValue.Should().Be(new string('t', depth - 1));
        // Inherited from the document element, in System.Xml's order (an element's own declarations
        // last to first).
        elem.NamespaceDeclarations.Select(b => (b.Prefix ?? "", store.GetNamespaceUri(b.Namespace)))
            .Should().Equal(("p", "urn:p"), ("", "urn:d"));
    }

    // The builder's namespace set before #639: GetNamespacesInScope(All) without "xml", then an
    // xmlns="" undeclaration on the element itself as ("", "").
    private static List<(string Prefix, string? Uri)> SystemXmlBindings(XmlElement elem)
    {
        var result = new List<(string, string?)>();
        foreach (var kvp in elem.CreateNavigator()!.GetNamespacesInScope(XmlNamespaceScope.All))
            if (kvp.Key != "xml")
                result.Add((kvp.Key, kvp.Value));
        if (!result.Any(b => b.Item1.Length == 0) && elem.HasAttribute("xmlns") && elem.GetAttribute("xmlns").Length == 0)
            result.Add(("", ""));
        return result;
    }

    private static string? EntityBaseUri(string systemXmlBaseUri)
        => !string.IsNullOrEmpty(systemXmlBaseUri) && systemXmlBaseUri != DocumentUri ? systemXmlBaseUri : null;

    // The elements the builder converts, in document order: those reached through elements (it does
    // not convert an entity reference node or what is inside one).
    private static IEnumerable<XmlElement> ConvertedElements(XmlElement elem)
    {
        yield return elem;
        foreach (var child in elem.ChildNodes.OfType<XmlElement>())
            foreach (var e in ConvertedElements(child))
                yield return e;
    }

    private static IEnumerable<XdmElement> Elements(XdmInMemoryStore store, XdmNode node)
    {
        if (node is not XdmElement elem)
            yield break;
        yield return elem;
        foreach (var id in elem.Children)
            foreach (var e in Elements(store, store.GetNode(id)!))
                yield return e;
    }
}
