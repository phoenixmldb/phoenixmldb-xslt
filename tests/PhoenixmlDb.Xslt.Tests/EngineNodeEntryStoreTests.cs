using AwesomeAssertions;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <see cref="XsltTransformEngine.TransformAsync(XdmNode, XsltTransformOptions?)"/> works in the
/// source's store (<see cref="XdmNode.Store"/>). It gave the transformation no store, so:
/// <list type="bullet">
///   <item><description>a stylesheet that constructed nodes failed: a path into a temporary tree
///   ("An axis step ..."), an XPath element constructor, the element of a <c>load-xquery-module</c>
///   function (InvalidOperationException), a path on <c>fn:analyze-string</c>, <c>fn:json-to-xml</c>
///   (FOJS0001); an XPath text constructor made a text node with the id 0;</description></item>
///   <item><description>a node of another store gave wrong results: <c>key()</c> found nothing,
///   <c>xsl:number level="any"</c> gave 1 for each node, <c>root()</c> found no children, and an
///   accumulator gave XTDE3362. Such a node is now refused.</description></item>
/// </list>
/// Each case is compared with the string entry point, which parses the source into a store of its own.
/// </summary>
public sealed class EngineNodeEntryStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("phoenixml-node-entry-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string SourceXml = "<r><i>1</i><i>2</i></r>";

    private const string Declarations =
        "<xsl:key name='k' match='i' use='.'/>"
        + "<xsl:mode use-accumulators='#all'/><xsl:mode name='a' use-accumulators='#all'/>"
        + "<xsl:accumulator name='c' initial-value='0'><xsl:accumulator-rule match='i' select='$value + 1'/></xsl:accumulator>"
        + "<xsl:template match='i' mode='a'><c v='{accumulator-before(\"c\")}'/></xsl:template>";

    private XsltStylesheet Parse(string body) =>
        new StylesheetParser(new XQueryExpressionParser()).Parse(
            "<xsl:stylesheet version='3.0' xmlns:xsl='http://www.w3.org/1999/XSL/Transform'>" + Declarations
            + $"<xsl:template match='/'><out>{body}</out></xsl:template></xsl:stylesheet>",
            new Uri(Path.Combine(_dir, "s.xsl")));

    private static XsltTransformOptions WithSource() => new() { HasSourceDocument = true };

    /// <summary>
    /// <c>&lt;r&gt;&lt;i&gt;1&lt;/i&gt;&lt;i&gt;2&lt;/i&gt;&lt;/r&gt;</c> as a document of an
    /// <see cref="XdmInMemoryStore"/>, or of a temporary tree of one (the store the engine gives the
    /// tree that a variable body builds, xslt#516).
    /// </summary>
    private static XdmDocument InMemorySource(bool temporaryTree = false)
    {
        var store = new XdmInMemoryStore();
        using var scope = temporaryTree ? store.TemporaryTree() : default;
        var docId = store.NextId();
        var rId = store.NextId();
        var i1 = store.NextId();
        var t1 = store.NextId();
        var i2 = store.NextId();
        var t2 = store.NextId();
        var document = new DocumentId(1);
        var doc = new XdmDocument
        {
            StringValueResolver = store.StringValueResolver,
            Id = docId, Document = document, Children = [rId], DocumentElement = rId,
        };
        XdmElement Element(NodeId id, string name, NodeId parent, NodeId[] children) => new()
        {
            StringValueResolver = store.StringValueResolver,
            Id = id, Document = document, Namespace = NamespaceId.None, LocalName = name,
            Attributes = [], Children = children, NamespaceDeclarations = [], Parent = parent,
        };
        store.Register(doc);
        store.Register(Element(rId, "r", docId, [i1, i2]));
        store.Register(Element(i1, "i", rId, [t1]));
        store.Register(new XdmText { Id = t1, Document = document, Value = "1", Parent = i1 });
        store.Register(Element(i2, "i", rId, [t2]));
        store.Register(new XdmText { Id = t2, Document = document, Value = "2", Parent = i2 });
        return doc;
    }

    private async Task<string> ViaStringAsync(string body)
        => await new XsltTransformEngine(Parse(body)).TransformAsync(SourceXml, WithSource()).ConfigureAwait(true);

    private async Task<string> ViaNodeAsync(string body, XdmNode source)
        => await new XsltTransformEngine(Parse(body)).TransformAsync(source, WithSource()).ConfigureAwait(true);

    // The expected value where it was measured; null: the string entry point is the reference.
    public static TheoryData<string, string?> ConstructingCases() => new()
    {
        { "<xsl:variable name='v'><a><b>x</b></a></xsl:variable><xsl:copy-of select='$v/a/b'/>", "<out><b>x</b></out>" },
        { "<xsl:copy-of select=\"&lt;q/&gt;\"/>", null },
        { "<xsl:value-of select=\"string(text { 'x' })\"/>", "<out>x</out>" },
        { "<xsl:evaluate xpath=\"'string(text { ''x'' })'\"/>", "<out>x</out>" },
        { "<xsl:value-of select=\"count(analyze-string('a1b', '\\d')/*)\"/>", "<out>3</out>" },
        { "<xsl:value-of select=\"count(json-to-xml('[1,2]')//*)\"/>", "<out>3</out>" },
        { "<xsl:copy-of select=\"parse-xml('&lt;p&gt;x&lt;/p&gt;')\"/>", null },
    };

    /// <summary>The source's tree, read in the source's store.</summary>
    public static TheoryData<string, string> SourceCases() => new()
    {
        { "<xsl:value-of select=\"count(key('k', '1'))\"/>", "<out>1</out>" },
        { "<xsl:for-each select='//i'><xsl:number level='any'/>.</xsl:for-each>", "<out>1.2.</out>" },
        { "<xsl:value-of select='count(root(//i[1])/r/i)'/>|<xsl:value-of select='name(//i[1]/..)'/>", "<out>2|r</out>" },
        { "<xsl:apply-templates select='r/i' mode='a'/>", "<out><c v=\"1\"/><c v=\"2\"/></out>" },
        { "<xsl:value-of select='count(//i)'/>|<xsl:value-of select='string(r/i[2])'/>", "<out>2|2</out>" },
    };

    [Theory]
    [MemberData(nameof(ConstructingCases))]
    public async Task A_stylesheet_that_constructs_nodes_works_for_a_source_of_an_XdmInMemoryStore(string body, string? expected)
    {
        var reference = await ViaStringAsync(body).ConfigureAwait(true);
        if (expected is not null)
            reference.Should().Be(expected, "the string entry point gives it");
        reference.Should().StartWith("<out>");
        (await ViaNodeAsync(body, InMemorySource()).ConfigureAwait(true)).Should().Be(reference);
    }

    [Theory]
    [MemberData(nameof(SourceCases))]
    public async Task A_source_of_an_XdmInMemoryStore_is_read_in_its_store(string body, string expected)
    {
        (await ViaStringAsync(body).ConfigureAwait(true)).Should().Be(expected, "the string entry point gives it");
        (await ViaNodeAsync(body, InMemorySource()).ConfigureAwait(true)).Should().Be(expected);
    }

    /// <summary>
    /// A node of another store is refused: the transformation could not read its tree, and gave
    /// wrong results (key() 0, xsl:number "1.1.").
    /// </summary>
    [Theory]
    [MemberData(nameof(SourceCases))]
    public async Task A_source_of_an_XdmDocumentStore_is_refused(string body, string expected)
    {
        _ = expected;
        var source = new XQuery.XdmDocumentStore().LoadFromString(SourceXml, "urn:test:source");
        var error = await Record.ExceptionAsync(() => ViaNodeAsync(body, source)).ConfigureAwait(true);
        error.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().StartWith("The source node is a node of a XdmDocumentStore, not of an XdmInMemoryStore.");
    }

    /// <summary>A node that no store holds: the transformation constructs its nodes in a new store.</summary>
    [Theory]
    [MemberData(nameof(ConstructingCases))]
    public async Task A_source_that_no_store_holds_gets_a_new_store(string body, string? expected)
    {
        _ = expected;
        var source = new XdmDocument { Id = new NodeId(1), Document = new DocumentId(1), Children = [] };
        source.Store.Should().BeNull();
        var result = await ViaNodeAsync(body, source).ConfigureAwait(true);
        result.Should().Be(await ViaStringAsync(body).ConfigureAwait(true));
    }

    /// <summary>
    /// A node of a temporary tree (its store is the tree's) is read through the store of the
    /// transformation that built the tree.
    /// </summary>
    [Theory]
    [MemberData(nameof(SourceCases))]
    public async Task A_node_of_a_temporary_tree_is_read_in_its_transformation_store(string body, string expected)
    {
        var tree = InMemorySource(temporaryTree: true);
        tree.Store.Should().BeOfType<TemporaryTreeStore>("the precondition of this test");
        (await ViaNodeAsync(body, tree).ConfigureAwait(true)).Should().Be(expected);
    }

    [Fact]
    public async Task The_functions_of_a_load_xquery_module_make_their_nodes()
    {
        var module = Path.Combine(_dir, "m.xqm");
        await File.WriteAllTextAsync(module,
            "module namespace m = 'urn:m'; declare function m:nodes() { text { 'tx' }, <e a='1'>ex</e> };").ConfigureAwait(true);
        var body = $"<xsl:for-each select=\"load-xquery-module('urn:m', map {{ 'location-hints': '{new Uri(module).AbsoluteUri}' }})"
            + "?functions(QName('urn:m', 'nodes'))?0()\"><n k='{local-name(.)}' v='{string(.)}'/></xsl:for-each>";
        const string expected = "<out><n k=\"\" v=\"tx\"/><n k=\"e\" v=\"ex\"/></out>";
        (await ViaStringAsync(body).ConfigureAwait(true)).Should().Be(expected, "the string entry point gives it");
        (await ViaNodeAsync(body, InMemorySource()).ConfigureAwait(true)).Should().Be(expected);
    }
}
