using PhoenixmlDb.Core;
using PhoenixmlDb.Xslt;
using PhoenixmlDb.Xslt.Engine;
using PhoenixmlDb.Xdm.Nodes;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

public class TreeConstructorTests
{
    [Fact]
    public void BuildsElementWithTextChild()
    {
        var store = new XdmInMemoryStore();
        var tc = new TreeConstructor(store, documentId: 1);
        tc.StartElement(NamespaceId.None, "a", null);
        tc.AppendText("hi");
        tc.EndElement();
        var roots = tc.FinishFragment();

        Assert.Single(roots);
        var elem = Assert.IsType<XdmElement>(store.GetNode(roots[0]));
        Assert.Equal("a", elem.LocalName);
        Assert.Equal("hi", elem.StringValue);
        Assert.Single(elem.Children);
    }

    [Fact]
    public void InheritNamespacesNo_SuppressesInheritedPrefixedNamespace()
    {
        var store = new XdmInMemoryStore();
        var tc = new TreeConstructor(store, 1);
        var uriB = store.InternNamespace("uri:b");
        // parent declares b; child with inherit-namespaces="no" must NOT see b
        tc.StartElement(NamespaceId.None, "parent", null);
        tc.AddNamespace("b", uriB);
        tc.StartElement(NamespaceId.None, "child", null,
            inScope: new Dictionary<string, NamespaceId> { ["b"] = uriB },
            inheritNamespaces: false);
        tc.EndElement();
        tc.EndElement();
        var roots = tc.FinishFragment();
        var parent = (XdmElement)store.GetNode(roots[0])!;
        var childId = parent.Children[0];
        var inScope = tc.InScopeOf(childId);
        Assert.False(inScope.ContainsKey("b")); // suppressed — the text boundary could not do this
    }

    [Fact]
    public void DefaultNamespaceUndeclaration_CopyElementUnderDefaultNsParent()
    {
        // copy-1221 shape: no-namespace child under a default-namespace parent
        var store = new XdmInMemoryStore();
        var tc = new TreeConstructor(store, 1);
        var def = store.InternNamespace("uri:d");
        tc.StartElement(def, "parent", null);
        tc.AddNamespace("", def);
        tc.StartElement(NamespaceId.None, "child", null); // no namespace
        tc.AddNamespace("", NamespaceId.None);            // xmlns="" undeclaration
        tc.EndElement();
        tc.EndElement();
        var roots = tc.FinishFragment();
        var parent = (XdmElement)store.GetNode(roots[0])!;
        var child = (XdmElement)store.GetNode(parent.Children[0])!;
        Assert.Contains(child.NamespaceDeclarations, nb => nb.Prefix.Length == 0 && nb.Namespace == NamespaceId.None);
    }
}
