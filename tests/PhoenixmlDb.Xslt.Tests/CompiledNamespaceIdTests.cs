using AwesomeAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Namespace ids of a compiled stylesheet and of the node stores that run it. A name test carries
/// the process-wide id of its URI (<see cref="StylesheetParser.ResolveNamespaceUri"/>), resolved once
/// at compile; a store uses that id for every URI the table knows, and an id of its own, from a range
/// the table never uses, for a URI only its input names. A store keeps one id per URI, even when the
/// table learns the URI after the store did; a matcher maps such a node's id to the table's through
/// the node's URI (<see cref="XdmInMemoryStore.CanonicalNamespaceId"/>).
/// </summary>
public class CompiledNamespaceIdTests
{
    private static string FreshUri() => "urn:phx-nsid-" + Guid.NewGuid().ToString("N");

    private static XdmElement Element(XdmInMemoryStore store, NamespaceId ns)
    {
        var elem = new XdmElement
        {
            Id = store.NextId(),
            Document = new DocumentId(1),
            LocalName = "x",
            Namespace = ns,
            Attributes = [],
            Children = [],
            NamespaceDeclarations = [],
        };
        store.Register(elem);
        return elem;
    }

    [Fact]
    public void A_store_takes_the_table_id_of_a_uri_the_table_knows()
    {
        var uri = FreshUri();
        var tableId = StylesheetParser.ResolveNamespaceUri(uri);

        new XdmInMemoryStore().InternNamespace(uri).Should().Be(tableId);
        new XdmInMemoryStore().InternNamespace(uri).Should().Be(tableId, "every store gives a known URI the same id");
        XdmInMemoryStore.IsLocalNamespaceId(tableId).Should().BeFalse();
    }

    /// <summary>
    /// The order of parsers' condition 1: the store interns the URI, THEN the table gives it an id
    /// (an xsl:evaluate or fn:transform compiled during the transformation). The store keeps its
    /// own id — one URI has one id in a store — and a node it holds still matches the table's id.
    /// </summary>
    [Fact]
    public void A_store_keeps_its_id_when_the_table_learns_the_uri_later()
    {
        var uri = FreshUri();
        var store = new XdmInMemoryStore();

        var localId = store.InternNamespace(uri);
        XdmInMemoryStore.IsLocalNamespaceId(localId).Should().BeTrue();
        StylesheetParser.TryGetNamespaceId(uri, out _).Should().BeFalse("only the store has seen the URI");
        var node = Element(store, localId);

        var tableId = StylesheetParser.ResolveNamespaceUri(uri);

        tableId.Should().NotBe(localId);
        store.InternNamespace(uri).Should().Be(localId, "the store's own id wins over the table's");
        store.GetNamespaceUri(localId).Should().Be(uri);
        XdmInMemoryStore.CanonicalNamespaceId(node, node.Namespace).Should().Be(tableId);
    }

    [Fact]
    public void A_table_id_is_never_a_store_local_id()
    {
        var store = new XdmInMemoryStore();
        var local = Enumerable.Range(0, 50).Select(_ => store.InternNamespace(FreshUri())).ToList();
        var table = Enumerable.Range(0, 50).Select(_ => StylesheetParser.ResolveNamespaceUri(FreshUri())).ToList();

        local.Should().OnlyContain(id => XdmInMemoryStore.IsLocalNamespaceId(id));
        table.Should().OnlyContain(id => !XdmInMemoryStore.IsLocalNamespaceId(id));
        local.Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// A store gives the namespaces of its input ids of its own: a transformation of an input with
    /// many namespaces gives none of them a table id, through the tree, node-name(), namespace-uri(),
    /// copies and computed attributes, and serialization. (Names a transformation BUILDS from strings —
    /// a computed xsl:element namespace, an EQName string — still go through the table; BUGS.md, the
    /// process-wide namespace table.)
    /// </summary>
    [Fact]
    public async Task The_namespaces_of_an_input_never_enter_the_table()
    {
        var uris = Enumerable.Range(0, 300).Select(_ => FreshUri()).ToList();
        var input = "<r>" + string.Concat(uris.Select((u, i) => $"<p{i}:e xmlns:p{i}=\"{u}\" p{i}:a=\"1\"/>")) + "</r>";
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/">
                <out n="{count(//*)}" u="{count(distinct-values(//*/namespace-uri()))}" q="{count(//*/node-name())}">
                  <xsl:copy-of select="r/*[position() mod 7 = 0]"/>
                  <xsl:for-each select="r/*[position() mod 5 = 0]"><xsl:copy><xsl:copy-of select="@*"/></xsl:copy></xsl:for-each>
                  <xsl:for-each select="r/*/@*[position() mod 11 = 0]"><xsl:attribute name="{name()}" namespace="{namespace-uri()}" select="."/></xsl:for-each>
                </out>
              </xsl:template>
            </xsl:stylesheet>
            """);

        var result = await t.TransformAsync(input);

        result.Should().Contain("n=\"301\"").And.Contain("u=\"301\"");
        uris.Where(u => StylesheetParser.TryGetNamespaceId(u, out _)).Should().BeEmpty();
    }

    /// <summary>
    /// End to end, condition 1's order: the input's namespace is new to the process, so the store
    /// gives it a local id; xsl:evaluate then compiles a path that names it, which gives it a table
    /// id; the path must still select the node.
    /// </summary>
    [Fact]
    public async Task An_evaluated_path_selects_a_node_whose_namespace_the_store_saw_first()
    {
        var uri = FreshUri();
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:evaluate xpath="'count(//l:x), count(//*[self::l:x]), exists(/r/l:x[@n = 1])'" context-item="." xmlns:l="{{uri}}"/>
              </xsl:template>
            </xsl:stylesheet>
            """);
        StylesheetParser.TryGetNamespaceId(uri, out _).Should().BeFalse("the stylesheet names the URI in no name test");

        var result = await t.TransformAsync($"""<r xmlns:l="{uri}"><l:x n="1"/><l:x n="2"/></r>""");

        result.Trim().Should().Be("2 2 true");
        StylesheetParser.TryGetNamespaceId(uri, out _).Should().BeTrue("xsl:evaluate compiled a name test for it");
    }

}
