using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery;

// CA1062 (null-check public method args): trusted-builder pattern — callers are the
//   engine and external code that knows what it's doing; nullability is by signature.
// CA1725 (parameter name match interface): kept original names (id, elem) for clarity.
// CA1054 (string→Uri for "uri"): namespace URIs are XML lexical strings, not System.Uri.
// CA1055 (return type Uri): same reason.
// CA1024 (use property): GetAllNodes() yields, properties shouldn't enumerate.
#pragma warning disable CA1062, CA1725, CA1054, CA1055, CA1024

namespace PhoenixmlDb.Xslt;

/// <summary>
/// In-memory <see cref="INodeStore"/> backing the XSLT engine's XDM tree. Exposed publicly
/// so callers can construct an XDM source tree externally (e.g. via
/// <c>XmlDocumentParser</c>), pass it into <see cref="XsltTransformer"/> overloads that
/// accept an <see cref="XdmNode"/> + store, and then chain the typed result back into
/// another transformation without serializing through XML markup.
/// </summary>
/// <remarks>
/// <para>
/// Implements <see cref="INodeBuilder"/> (and therefore <see cref="INodeStore"/> /
/// <see cref="INodeProvider"/>), so the same store satisfies both XSLT and XQuery
/// engines. NodeIds and NamespaceIds allocated by this store are scoped to the store
/// instance — they are <em>not</em> portable across stores.
/// </para>
/// <para>
/// The store starts with the well-known XML and XMLNS namespace IDs pre-registered
/// (<see cref="NamespaceId.Xml"/> and <see cref="NamespaceId.Xmlns"/>) so that node
/// trees built externally see the same canonical mappings the engine uses.
/// </para>
/// </remarks>
public sealed class XdmInMemoryStore : INodeBuilder, IXdmNodeStore
{
    private readonly Dictionary<NodeId, XdmNode> _nodes = new();
    private ulong _nextId = 1;
    private readonly Dictionary<string, NamespaceId> _nsToId = new(StringComparer.Ordinal);
    private readonly Dictionary<NamespaceId, string> _idToNs = new();
    /// <summary>
    /// The first id this store gives a namespace the process-wide table does not know. Such ids
    /// come from a range the table never uses (it counts up from 100), so a store-local id is never
    /// the table's id for another URI. A compiled stylesheet's name tests carry table ids and so
    /// match by id in every store; a node whose namespace has a store-local id is matched by URI.
    /// </summary>
    internal const uint FirstLocalNamespaceId = 0x8000_0000;

    private uint _nextNsId = FirstLocalNamespaceId;

    /// <summary>
    /// The process-wide id of <paramref name="node"/>'s namespace <paramref name="id"/> when that id
    /// is store-local but the table now knows its URI (the store interned the URI before a
    /// stylesheet did); otherwise <paramref name="id"/>. Compiled name tests carry table ids, so a
    /// matcher compares this value with them.
    /// </summary>
    internal static NamespaceId CanonicalNamespaceId(XdmNode node, NamespaceId id)
    {
        if (!IsLocalNamespaceId(id) || node.Store?.GetNamespaceUri(id) is not { } uri)
            return id;
        return Engine.StylesheetParser.TryGetNamespaceId(uri, out var tableId) ? tableId : id;
    }

    /// <summary>Whether <paramref name="id"/> is a store-local namespace id (see <see cref="FirstLocalNamespaceId"/>).</summary>
    internal static bool IsLocalNamespaceId(NamespaceId id) => id.Value >= FirstLocalNamespaceId;

    public XdmInMemoryStore()
    {
        // Pre-register well-known namespaces so their IDs match the hard-coded constants
        _nsToId["http://www.w3.org/XML/1998/namespace"] = NamespaceId.Xml;     // 1
        _idToNs[NamespaceId.Xml] = "http://www.w3.org/XML/1998/namespace";
        _nsToId["http://www.w3.org/2000/xmlns/"] = NamespaceId.Xmlns;           // 2
        _idToNs[NamespaceId.Xmlns] = "http://www.w3.org/2000/xmlns/";
    }

    public NodeId NextId() => new(_nextId++);
    public void Register(XdmNode node)
    {
        // The order between trees (xslt#542): a node of a parsed document carries its document's
        // parse ordinal, any other node this store's construction ordinal. A construction ordinal
        // has the high bit set, so a parsed document precedes every constructed tree, whenever
        // either was built; the rule XQuery's store has (TreeOrdinalAllocator). Within a tree,
        // and between constructed trees, the order is the node ids' (creation) order, as before.
        if (node.TreeOrdinal == 0)
            node.TreeOrdinal = _parseOrdinal != 0 ? _parseOrdinal : ConstructionOrdinal;
        // A node constructed while a temporary tree is being built, with an id this store gave
        // since, is a node of that tree. Two kinds of node registered meanwhile are not: a node of
        // a parsed document (a parse ordinal), such as one doc() loads in a variable body; and a
        // node of the streamed input, which the transform gives ids from a counter of its own
        // (from 5 000 000). The tree finds its nodes by the ids this store gave it, so a streamed
        // element taken into it was found, and its attributes and children were not.
        if (_openTree is { } tree && node.Id.Value >= _openTreeStart && node.Id.Value < _nextId
            && (node.TreeOrdinal & TreeOrdinalAllocator.ConstructedFlag) != 0)
        {
            tree.Add(node);
            return;
        }
        // The node's ids and namespace ids are this store's; Store says so to an engine that
        // meets the node outside this store (BUGS.md #124).
        node.AttachStore(this);
        _nodes[node.Id] = node;
        if (_treeRanges.Count > 0)
            PinLinkedTrees(node);
    }

    // A node of this store that names a node of a temporary tree (its parent, a child or an
    // attribute) keeps that tree for as long as this store lives: this store holds its own nodes
    // strongly, and a tree it only named by id would otherwise be freed under it.
    private HashSet<TemporaryTreeStore>? _pinnedTrees;

    private void PinLinkedTrees(XdmNode node)
    {
        foreach (var id in LinkedIds(node))
        {
            if (!_nodes.ContainsKey(id) && TreeOf(id) is { } tree)
                (_pinnedTrees ??= []).Add(tree);
        }
    }

    /// <summary>The ids <paramref name="node"/> names: its parent, children and attributes.</summary>
    internal static IEnumerable<NodeId> LinkedIds(XdmNode node)
    {
        if (node.Parent is { } parent && parent != NodeId.None)
            yield return parent;
        var (children, attributes) = node switch
        {
            XdmElement e => (e.Children, e.Attributes),
            XdmDocument d => (d.Children, (IReadOnlyList<NodeId>)[]),
            _ => ((IReadOnlyList<NodeId>)[], (IReadOnlyList<NodeId>)[]),
        };
        foreach (var id in children)
            yield return id;
        foreach (var id in attributes)
            yield return id;
    }

    // ---- Temporary trees (xslt#516) ------------------------------------------------------------
    //
    // A temporary tree is held by its own TemporaryTreeStore, which its nodes name as their Store:
    // a node that is still referred to keeps its tree, and a tree nothing refers to is freed. This
    // store keeps, for each run of ids a tree was given, a weak handle, so that it can still find
    // a node of a live tree from its id (the engine resolves children, attributes and parents by
    // id here). Ids are given in increasing order, so the runs are in order of their start.

    private readonly record struct TreeRange(ulong Start, ulong End, WeakReference<TemporaryTreeStore> Tree);

    private readonly List<TreeRange> _treeRanges = [];
    private int _pruneTreeRangesAt = 1024;
    private TemporaryTreeStore? _openTree;
    private ulong _openTreeStart;

    /// <summary>
    /// Until the scope is disposed, the nodes given ids and registered are those of one new
    /// temporary tree, held by a store of its own. Scopes nest: a tree built while another is
    /// being built is a tree of its own.
    /// </summary>
    internal TemporaryTreeScope TemporaryTree()
    {
        var previous = _openTree;
        if (previous is not null)
            CloseTreeRun(previous);
        _openTree = new TemporaryTreeStore(this);
        _openTreeStart = _nextId;
        return new TemporaryTreeScope(this, previous);
    }

    internal readonly struct TemporaryTreeScope : IDisposable
    {
        private readonly XdmInMemoryStore _store;
        private readonly TemporaryTreeStore? _previous;

        internal TemporaryTreeScope(XdmInMemoryStore store, TemporaryTreeStore? previous)
        {
            _store = store;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_store is null || _store._openTree is not { } tree)
                return;
            _store.CloseTreeRun(tree);
            _store._openTree = _previous;
            _store._openTreeStart = _store._nextId;
            _store.LinkTree(tree);
        }
    }

    // A tree whose nodes name nodes of another tree (an id of a parent, child or attribute it does
    // not hold) keeps that tree: the ids are all it has of them.
    private void LinkTree(TemporaryTreeStore tree)
    {
        foreach (var node in tree.Nodes)
        {
            foreach (var id in LinkedIds(node))
            {
                if (tree.Own(id) is null && TreeOf(id) is { } other && !ReferenceEquals(other, tree))
                    tree.KeepAlive(other);
            }
        }
    }

    // Records the run of ids the open tree was given since its scope (re)opened.
    private void CloseTreeRun(TemporaryTreeStore tree)
    {
        if (_nextId <= _openTreeStart)
            return;
        if (_treeRanges.Count >= _pruneTreeRangesAt)
        {
            _treeRanges.RemoveAll(static r => !r.Tree.TryGetTarget(out _));
            _pruneTreeRangesAt = Math.Max(1024, _treeRanges.Count * 2);
        }
        _treeRanges.Add(new TreeRange(_openTreeStart, _nextId, tree.Handle));
    }

    // The temporary tree whose ids include id: the open tree, or one with a recorded run. A run
    // whose tree was freed is an error, not "no such node": an id of a freed tree was kept where
    // its node was not, and reading it as absent would give wrong results silently.
    private TemporaryTreeStore? TreeOf(NodeId id)
    {
        var value = id.Value;
        if (_openTree is { } open && value >= _openTreeStart && value < _nextId)
            return open;
        int lo = 0, hi = _treeRanges.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var range = _treeRanges[mid];
            if (value < range.Start)
                hi = mid - 1;
            else if (value >= range.End)
                lo = mid + 1;
            else if (range.Tree.TryGetTarget(out var tree))
                return tree;
            else
                throw new InvalidOperationException(
                    $"Node {value} belongs to a temporary tree that was freed: an id of it was kept without the node (xslt#516).");
        }
        return null;
    }

    // A node of this store or of a live temporary tree.
    private XdmNode? Lookup(NodeId id)
        => _nodes.TryGetValue(id, out var node) ? node : TreeOf(id)?.Own(id);

    // The parse ordinal of the document being parsed (ParsedDocument), or 0.
    private ulong _parseOrdinal;
    private ulong _constructionOrdinal;

    /// <summary>The tree ordinal of every node this store constructs (allocated on first use).</summary>
    internal ulong ConstructionOrdinal
        => _constructionOrdinal != 0 ? _constructionOrdinal : (_constructionOrdinal = TreeOrdinalAllocator.NextConstructionOrdinal());

    /// <summary>A new parse ordinal: the tree ordinal of every node of one parsed document.</summary>
    internal static ulong NextParseOrdinal() => TreeOrdinalAllocator.NextParseOrdinal();

    /// <summary>
    /// Until the scope is disposed, the nodes registered are those of one parsed document and
    /// carry a new parse ordinal. For a document built in one call, such as from an
    /// <see cref="System.Xml.XmlDocument"/>; a streamed document stamps its nodes itself.
    /// </summary>
    internal ParsedDocumentScope ParsedDocument() => new(this, NextParseOrdinal());

    /// <summary>The same, for the nodes of a document that already has a parse ordinal.</summary>
    internal ParsedDocumentScope ParsedDocument(ulong ordinal) => new(this, ordinal);

    // The parse ordinal of the document a streaming reader reads: one per reader, so every path
    // that builds nodes of a streamed document (the streaming processor, the striding drivers,
    // the document node of xsl:source-document) gives them the same one.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Xml.XmlReader, object> s_streamOrdinals = new();

    /// <summary>The parse ordinal of the streamed document <paramref name="reader"/> reads.</summary>
    internal static ulong ParseOrdinalOf(System.Xml.XmlReader reader)
        => (ulong)s_streamOrdinals.GetValue(reader, static _ => NextParseOrdinal());

    internal readonly struct ParsedDocumentScope : IDisposable
    {
        private readonly XdmInMemoryStore _store;
        private readonly ulong _previous;

        internal ParsedDocumentScope(XdmInMemoryStore store, ulong ordinal)
        {
            _store = store;
            _previous = store._parseOrdinal;
            store._parseOrdinal = ordinal;
        }

        public void Dispose()
        {
            if (_store is not null)
                _store._parseOrdinal = _previous;
        }
    }
    public XdmNode? GetNode(NodeId id) => Lookup(id);

    /// <summary>
    /// Removes a node the engine built for a moment only, such as the context of a streamed
    /// predicate, so that streaming memory does not grow with the input (xslt#520). Its
    /// attributes and children are separate entries: remove them too.
    /// </summary>
    internal void Unregister(NodeId id) => Remove(id);

    /// <summary>
    /// Computes an element's or document node's string value from its children in this store,
    /// for nodes built without one. Give it to every element and document node the engine
    /// constructs: <c>StringValueResolver = store.StringValueResolver</c>.
    /// </summary>
    /// <remarks>
    /// A node's string value is its cached value, else its resolver's answer, else, silently,
    /// the empty string. So a node built with children but no computed value read as empty,
    /// indistinguishable from a genuinely empty one: <c>xsl:value-of</c> of an
    /// <c>as="document-node()"</c> variable printed nothing, and so did json-to-xml's result.
    /// Nodes that do compute their value keep it; the cached value always wins over this.
    /// </remarks>
    public XdmNode.XdmStringValueResolver StringValueResolver => _stringValueResolver ??= ComputeStringValue;

    private XdmNode.XdmStringValueResolver? _stringValueResolver;

    private string ComputeStringValue(XdmNode node)
    {
        var children = node switch
        {
            XdmElement e => e.Children,
            XdmDocument d => d.Children,
            _ => null,
        };
        if (children is not { Count: > 0 })
            return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (var id in children)
        {
            switch (GetNode(id))
            {
                case XdmText text: sb.Append(text.Value); break;
                // An element child answers from its own cache or resolver.
                case XdmElement child: sb.Append(child.StringValue); break;
            }
        }
        return sb.ToString();
    }

    // INodeBuilder explicit interface implementations (delegate to existing methods)
    NodeId INodeBuilder.AllocateId() => NextId();
    void INodeBuilder.RegisterNode(XdmNode node) => Register(node);

    /// <summary>
    /// Removes a node from the store. Used by streaming execution to free
    /// temporary nodes after they have been processed.
    /// </summary>
    internal void Remove(NodeId id)
    {
        if (!_nodes.Remove(id))
            TreeOf(id)?.Remove(id);
    }

    /// <summary>
    /// Returns the next NodeId that would be allocated, without consuming it.
    /// Used by callers that need to hand the value to an allocator (e.g.
    /// <c>XmlDocumentParser</c>) before reserving the range via
    /// <see cref="AdvanceNextIdBy"/>.
    /// </summary>
    internal NodeId PeekNextId() => new(_nextId);

    /// <summary>
    /// Advances the internal NodeId counter by <paramref name="count"/>.
    /// Used after an external allocator (e.g. <c>XmlDocumentParser</c>) has
    /// claimed a contiguous range starting at <see cref="PeekNextId"/>.
    /// </summary>
    internal void AdvanceNextIdBy(ulong count) => _nextId += count;

    public NamespaceId InternNamespace(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return NamespaceId.None;
        // This store's own id first: one URI has one id in a store, even if the process-wide
        // table gave the URI an id after this store did.
        if (_nsToId.TryGetValue(uri, out var id))
            return id;
        if (Engine.StylesheetParser.TryGetNamespaceId(uri, out id) && !_idToNs.ContainsKey(id))
        {
            _nsToId[uri] = id;
            _idToNs[id] = uri;
            return id;
        }
        do { id = new NamespaceId(_nextNsId++); } while (_idToNs.ContainsKey(id));
        _nsToId[uri] = id;
        _idToNs[id] = uri;
        return id;
    }

    /// <summary>
    /// Interns <paramref name="uri"/>; <paramref name="preferredId"/> is not used. A URI's id is the
    /// process-wide table's (one source of truth with the stylesheet parser) or a store-local one,
    /// so a preferred id from elsewhere can never be given to one URI here and to another in the
    /// table.
    /// </summary>
    public NamespaceId InternNamespace(string uri, NamespaceId preferredId)
    {
        _ = preferredId;
        return InternNamespace(uri);
    }

    /// <summary>
    /// Registers a well-known NamespaceId → URI mapping (does not allocate a new ID).
    /// </summary>
    public void RegisterKnownNamespace(NamespaceId id, string uri)
    {
        _idToNs.TryAdd(id, uri);
        _nsToId.TryAdd(uri, id);
    }

    /// <inheritdoc />
    public bool TryGetNamespaceId(string uri, out NamespaceId id)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Length == 0)
        {
            id = NamespaceId.None;
            return true;
        }
        return _nsToId.TryGetValue(uri, out id);
    }

    public string? GetNamespaceUri(NamespaceId id)
    {
        if (id == NamespaceId.None)
            return null;
        return _idToNs.GetValueOrDefault(id);
    }

    /// <summary>The nodes of this store; not those of temporary trees, which have stores of their own.</summary>
    public IEnumerable<XdmNode> GetAllNodes() => _nodes.Values;

    public IEnumerable<XdmNode> GetChildren(XdmNode node)
    {
        var childIds = node switch
        {
            XdmDocument doc => doc.Children,
            XdmElement elem => elem.Children,
            _ => (IReadOnlyList<NodeId>)[]
        };

        foreach (var childId in childIds)
        {
            if (Lookup(childId) is { } child)
                yield return child;
        }
    }

    /// <summary>The ids of <paramref name="node"/>'s children, as <see cref="GetChildren"/> reads them.</summary>
    internal static IReadOnlyList<NodeId> ChildIds(XdmNode node) => node switch
    {
        XdmDocument doc => doc.Children,
        XdmElement elem => elem.Children,
        _ => (IReadOnlyList<NodeId>)[]
    };

    /// <summary>The child with this id, or null, as <see cref="GetChildren"/> resolves it.</summary>
    internal XdmNode? ChildNode(NodeId id) => Lookup(id);

    /// <summary>
    /// The children of <paramref name="node"/> as a new list, sized once: what
    /// <see cref="GetChildren"/> yields, without its iterator. The built-in template rule and
    /// xsl:apply-templates read a node's children this way at every node they visit.
    /// </summary>
    internal List<object> ChildList(XdmNode node)
    {
        var childIds = ChildIds(node);
        var list = new List<object>(childIds.Count);
        for (var i = 0; i < childIds.Count; i++)
        {
            if (Lookup(childIds[i]) is { } child)
                list.Add(child);
        }
        return list;
    }

    public IEnumerable<XdmAttribute> GetAttributes(XdmElement elem)
    {
        foreach (var attrId in elem.Attributes)
        {
            if (Lookup(attrId) is XdmAttribute a)
                yield return a;
        }
    }

    /// <summary>
    /// Allocation-free attribute iteration: returns a struct enumerator that walks
    /// <see cref="XdmElement.Attributes"/> by index (no boxed IEnumerator) and looks
    /// up each attribute in the store. Use this on hot paths where the alternative
    /// is the <see cref="GetAttributes(XdmElement)"/> yield-iterator state machine.
    /// </summary>
    public AttributeEnumerator EnumerateAttributes(XdmElement elem) => new(this, elem);

#pragma warning disable CA1815, CA1034 // Equality on enumerators is meaningless; nesting is intentional for ergonomics.
    public struct AttributeEnumerator
#pragma warning restore CA1815, CA1034
    {
        private readonly XdmInMemoryStore _store;
        private readonly IReadOnlyList<NodeId> _attrIds;
        private int _index;
        private XdmAttribute? _current;

        internal AttributeEnumerator(XdmInMemoryStore store, XdmElement elem)
        {
            _store = store;
            _attrIds = elem.Attributes;
            _index = -1;
            _current = null;
        }

        public readonly XdmAttribute Current => _current!;

        public bool MoveNext()
        {
            while (++_index < _attrIds.Count)
            {
                if (_store.Lookup(_attrIds[_index]) is XdmAttribute a)
                {
                    _current = a;
                    return true;
                }
            }
            return false;
        }

        public readonly AttributeEnumerator GetEnumerator() => this;
    }
}
