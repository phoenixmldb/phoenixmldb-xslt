using System.Xml.Linq;
using AwesomeAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The in-scope namespaces of a stylesheet element are worked out once per element (#401), with
/// the result the ancestor walk gave: the nearest declaration of a prefix first, then the outer
/// ones. An element that declares nothing has its parent's set.
/// </summary>
public sealed class NamespaceScopesTests
{
    private static readonly XDocument Doc = XDocument.Parse(
        "<r xmlns='urn:d' xmlns:a='urn:a'><m xmlns:b='urn:b' xmlns:a='urn:a2'><n/></m><o/></r>");

    private static XElement E(string local) => Doc.Descendants().First(e => e.Name.LocalName == local);

    [Fact]
    public void The_nearest_declaration_comes_first()
    {
        NamespaceScopes.InScope(E("m")).Should().Equal(
            new KeyValuePair<string, string>("b", "urn:b"),
            new KeyValuePair<string, string>("a", "urn:a2"),
            new KeyValuePair<string, string>("", "urn:d"));
        NamespaceScopes.Prefixed(E("m")).Keys.Should().Equal("b", "a");
    }

    [Fact]
    public void An_element_that_declares_nothing_has_its_parents_set()
    {
        NamespaceScopes.InScope(E("n")).Should().BeSameAs(NamespaceScopes.InScope(E("m")));
        NamespaceScopes.InScope(E("o")).Should().BeSameAs(NamespaceScopes.InScope(Doc.Root!));
    }

    [Fact]
    public void Only_a_declaration_below_the_root_counts_as_declared_below_the_root()
    {
        NamespaceScopes.DeclaresBelowRoot(Doc.Root!).Should().BeFalse();
        NamespaceScopes.DeclaresBelowRoot(E("o")).Should().BeFalse();
        NamespaceScopes.DeclaresBelowRoot(E("m")).Should().BeTrue();
        NamespaceScopes.DeclaresBelowRoot(E("n")).Should().BeTrue();
    }
}
