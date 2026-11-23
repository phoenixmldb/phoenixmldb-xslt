using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;

namespace PhoenixmlDb.Xslt;

/// <summary>
/// The nodes of one temporary tree (xslt#516): a tree the engine builds for a moment, such as the
/// value of an <c>xsl:variable</c> or <c>xsl:param</c> body with no <c>as=</c>. Each such node's
/// <see cref="XdmNode.Store"/> is this store, so while anything refers to a node of the tree the
/// tree stays in memory, and when nothing does the garbage collector frees it. The transform's
/// <see cref="XdmInMemoryStore"/> holds the tree only weakly; it kept every temporary tree until
/// the transform ended, so a long transform grew with the number of trees it had made.
/// </summary>
/// <remarks>
/// <para>
/// The tree's ids come from the main store's counter, as before, so document order and
/// <c>generate-id()</c> do not change; the main store finds a node of the tree from its id while
/// the tree is alive. Namespace ids are the main store's.
/// </para>
/// <para>
/// A lookup this store cannot answer goes to the main store: a node of the tree is read here, any
/// other node there.
/// </para>
/// </remarks>
internal sealed class TemporaryTreeStore : IXdmNodeStore
{
    private readonly Dictionary<NodeId, XdmNode> _nodes = new();

    internal TemporaryTreeStore(XdmInMemoryStore main)
    {
        Main = main;
        Handle = new WeakReference<TemporaryTreeStore>(this);
    }

    /// <summary>The transform's store, which gives the ids and namespace ids.</summary>
    internal XdmInMemoryStore Main { get; }

    /// <summary>The weak handle the main store keeps for each id range of this tree.</summary>
    internal WeakReference<TemporaryTreeStore> Handle { get; }

    internal void Add(XdmNode node)
    {
        node.AttachStore(this);
        _nodes[node.Id] = node;
    }

    /// <summary>The node of this tree with the id, or null: no lookup in the main store.</summary>
    internal XdmNode? Own(NodeId id) => _nodes.GetValueOrDefault(id);

    internal bool Remove(NodeId id) => _nodes.Remove(id);

    /// <summary>The nodes of this tree.</summary>
    internal IEnumerable<XdmNode> Nodes => _nodes.Values;

    // Other trees whose nodes this tree's nodes name by id.
    private HashSet<TemporaryTreeStore>? _linked;

    internal void KeepAlive(TemporaryTreeStore other) => (_linked ??= []).Add(other);

    public XdmNode? GetNode(NodeId id) => _nodes.GetValueOrDefault(id) ?? Main.GetNode(id);

    public string? GetNamespaceUri(NamespaceId id) => Main.GetNamespaceUri(id);

    public bool TryGetNamespaceId(string uri, out NamespaceId id) => Main.TryGetNamespaceId(uri, out id);
}
