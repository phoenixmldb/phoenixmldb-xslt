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

    [Fact]
    public void AddsAttributeCommentProcessingInstructionAndBaseUri()
    {
        var store = new XdmInMemoryStore();
        var tc = new TreeConstructor(store, 1);
        tc.StartElement(NamespaceId.None, "a", null);
        tc.AddAttribute(NamespaceId.None, "id", null, "42");
        tc.AppendComment("a comment");
        tc.AppendProcessingInstruction("pi-target", "pi-data");
        tc.SetBaseUri("http://example.com/base");
        tc.EndElement();
        var elem = (XdmElement)store.GetNode(tc.FinishFragment()[0])!;

        Assert.Single(elem.Attributes);
        var attr = (XdmAttribute)store.GetNode(elem.Attributes[0])!;
        Assert.Equal("id", attr.LocalName);
        Assert.Equal("42", attr.Value);

        Assert.Equal(2, elem.Children.Count);
        var comment = Assert.IsType<XdmComment>(store.GetNode(elem.Children[0]));
        Assert.Equal("a comment", comment.Value);
        var pi = Assert.IsType<XdmProcessingInstruction>(store.GetNode(elem.Children[1]));
        Assert.Equal("pi-target", pi.Target);
        Assert.Equal("pi-data", pi.Value);

        Assert.Equal("http://example.com/base", elem.BaseUri);
        Assert.Equal("http://example.com/base", elem.CopySourceBaseUri);
    }

    [Fact]
    public void AppendNodeAndText_PreserveDocumentOrder()
    {
        var store = new XdmInMemoryStore();
        // pre-build a standalone <x/> node to append
        var pre = new TreeConstructor(store, 1);
        pre.StartElement(NamespaceId.None, "x", null); pre.EndElement();
        var xId = pre.FinishFragment()[0];

        var tc = new TreeConstructor(store, 1);
        tc.StartElement(NamespaceId.None, "out", null);
        tc.AppendText("1");
        tc.AppendNode(xId);     // node between text
        tc.AppendText("2");
        tc.EndElement();
        var outElem = (XdmElement)store.GetNode(tc.FinishFragment()[0])!;
        // children in order: text "1", element x, text "2"
        Assert.Equal(3, outElem.Children.Count);
        Assert.IsType<XdmText>(store.GetNode(outElem.Children[0]));
        Assert.IsType<XdmElement>(store.GetNode(outElem.Children[1]));
        Assert.IsType<XdmText>(store.GetNode(outElem.Children[2]));
    }
}
