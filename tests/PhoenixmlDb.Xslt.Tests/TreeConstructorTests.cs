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
}
