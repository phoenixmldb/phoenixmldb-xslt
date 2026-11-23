using System.Runtime.CompilerServices;
using AwesomeAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A temporary tree has a store of its own and is freed when nothing refers to it (xslt#516). The
/// transform's store kept every temporary tree until the transform ended, so a long transform grew
/// with the number of trees it made, not with the number alive.
/// </summary>
public class TemporaryTreeStoreTests
{
    // A document node with one text child, as a text-only variable body makes.
    private static XdmDocument BuildTextDocument(XdmInMemoryStore store, string value)
    {
        var docId = store.NextId();
        var text = new XdmText { Id = store.NextId(), Document = new DocumentId(1), Parent = docId, Value = value };
        store.Register(text);
        var doc = new XdmDocument
        {
            Id = docId,
            Document = new DocumentId(1),
            Parent = NodeId.None,
            DocumentElement = NodeId.None,
            Children = [text.Id],
            StringValueResolver = store.StringValueResolver,
        };
        store.Register(doc);
        return doc;
    }

    [Fact]
    public void ANodeOfATemporaryTreeNamesItsOwnStoreAndIsFoundFromTheMainStore()
    {
        var store = new XdmInMemoryStore();
        XdmDocument doc;
        using (store.TemporaryTree())
            doc = BuildTextDocument(store, "v");

        doc.Store.Should().BeOfType<TemporaryTreeStore>();
        store.GetNode(doc.Id).Should().BeSameAs(doc);
        var text = store.GetNode(doc.Children[0]);
        text.Should().BeOfType<XdmText>().Which.Store.Should().BeSameAs(doc.Store);
        doc.Store!.GetNode(doc.Children[0]).Should().BeSameAs(text);
        doc.StringValue.Should().Be("v");
        store.GetAllNodes().Should().BeEmpty("the tree's nodes are in the tree's store");
    }

    [Fact]
    public void ANodeBuiltOutsideAScopeStaysInTheMainStore()
    {
        var store = new XdmInMemoryStore();
        var doc = BuildTextDocument(store, "v");

        doc.Store.Should().BeSameAs(store);
        store.GetAllNodes().Should().HaveCount(2);
    }

    [Fact]
    public void ATreeBuiltWhileAnotherIsBuiltHasAStoreOfItsOwn()
    {
        var store = new XdmInMemoryStore();
        XdmDocument outerFirst, inner, outerLast;
        using (store.TemporaryTree())
        {
            outerFirst = BuildTextDocument(store, "a");
            using (store.TemporaryTree())
                inner = BuildTextDocument(store, "b");
            outerLast = BuildTextDocument(store, "c");
        }

        inner.Store.Should().NotBeSameAs(outerFirst.Store);
        outerLast.Store.Should().BeSameAs(outerFirst.Store);
        foreach (var doc in new[] { outerFirst, inner, outerLast })
        {
            store.GetNode(doc.Id).Should().BeSameAs(doc);
            store.GetNode(doc.Children[0]).Should().BeOfType<XdmText>();
        }
    }

    [Fact]
    public void IdsAndDocumentOrderAreAsWithoutTheScope()
    {
        // The ids come from the main store's counter: a tree built in a scope gets the ids, and so
        // the order, it would have had.
        var plain = new XdmInMemoryStore();
        var scoped = new XdmInMemoryStore();
        var a = BuildTextDocument(plain, "x");
        var b = BuildTextDocument(plain, "y");
        XdmDocument c, d;
        using (scoped.TemporaryTree())
            c = BuildTextDocument(scoped, "x");
        d = BuildTextDocument(scoped, "y");

        (c.Id, d.Id).Should().Be((a.Id, b.Id));
        XdmNode.CompareDocumentOrder(c, d).Should().Be(XdmNode.CompareDocumentOrder(a, b));
    }

    [Fact]
    public void ATreeNothingRefersToIsFreed()
    {
        var store = new XdmInMemoryStore();
        var (handle, docId) = BuildAndDrop(store);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        handle.TryGetTarget(out _).Should().BeFalse("nothing refers to the tree");
        // An id kept without its node is a defect in the engine, and reads as an error, not as
        // "no such node".
        var act = () => store.GetNode(docId);
        act.Should().Throw<InvalidOperationException>().WithMessage("*freed*");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<TemporaryTreeStore> Handle, NodeId DocId) BuildAndDrop(XdmInMemoryStore store)
    {
        using (store.TemporaryTree())
        {
            var doc = BuildTextDocument(store, "v");
            return (((TemporaryTreeStore)doc.Store!).Handle, doc.Id);
        }
    }

    [Fact]
    public void ATreeANodeOfWhichIsHeldIsKept()
    {
        var store = new XdmInMemoryStore();
        XdmDocument doc;
        using (store.TemporaryTree())
            doc = BuildTextDocument(store, "kept");
        var text = store.GetNode(doc.Children[0])!;
        doc = null!;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // The text node keeps its tree, and so its parent.
        var parent = store.GetNode(text.Parent!.Value);
        parent.Should().BeOfType<XdmDocument>().Which.StringValue.Should().Be("kept");
        GC.KeepAlive(text);
    }

    [Fact]
    public async Task TheTreesOfVariableBodiesInALoopAreNotKeptInTheTransformsStore()
    {
        // 1000 iterations, each binding a body that builds <r><x>…</x></r>: the transform's store
        // held all of them (4 nodes each) until the transform ended.
        const string sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:iterate select="1 to 1000">
                  <xsl:param name="acc" select="0"/>
                  <xsl:on-completion>
                    <xsl:variable name="last"><r><x>last</x></r></xsl:variable>
                    <xsl:value-of select="$acc, $last/r/x"/>
                  </xsl:on-completion>
                  <xsl:variable name="d"><r><x><xsl:value-of select="."/></x></r></xsl:variable>
                  <xsl:next-iteration><xsl:with-param name="acc" select="$acc + string-length($d/r/x)"/></xsl:next-iteration>
                </xsl:iterate>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // The transform runs on a store of the test's own, so the nodes left in it can be counted.
        var store = new XdmInMemoryStore();
        var doc = new XdmDocument
        {
            StringValueResolver = store.StringValueResolver,
            Id = store.NextId(),
            Document = new DocumentId(1),
            Children = [],
        };
        store.Register(doc);
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(sheet);

        var result = await transformer.TransformAsync(PhoenixmlDb.Xdm.XdmSequence.OfNode(doc, store));

        result.Trim().Should().Be("2893 last");
        store.GetAllNodes().Count().Should().BeLessThan(100, "the bodies' trees are not the transform's store's");
    }

    [Fact]
    public async Task AVariableBodyIsATemporaryTreeAndItsGenerateIdIsUnchanged()
    {
        const string sheet = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">
                <xsl:variable name="v"><a><b>1</b></a></xsl:variable>
                <xsl:value-of select="$v/a/b, count($v//node()), generate-id($v) => starts-with('d0n')"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(sheet);
        transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");

        (await transformer.TransformAsync((string?)null)).Trim().Should().Be("1 3 true");
    }
}
