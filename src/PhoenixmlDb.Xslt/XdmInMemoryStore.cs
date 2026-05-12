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
public sealed class XdmInMemoryStore : INodeBuilder
{
    private readonly Dictionary<NodeId, XdmNode> _nodes = new();
    private ulong _nextId = 1;
    private readonly Dictionary<string, NamespaceId> _nsToId = new(StringComparer.Ordinal);
    private readonly Dictionary<NamespaceId, string> _idToNs = new();
    private uint _nextNsId = 3; // Start after well-known IDs (Xml=1, Xmlns=2)

    public XdmInMemoryStore()
    {
        // Pre-register well-known namespaces so their IDs match the hard-coded constants
        _nsToId["http://www.w3.org/XML/1998/namespace"] = NamespaceId.Xml;     // 1
        _idToNs[NamespaceId.Xml] = "http://www.w3.org/XML/1998/namespace";
        _nsToId["http://www.w3.org/2000/xmlns/"] = NamespaceId.Xmlns;           // 2
        _idToNs[NamespaceId.Xmlns] = "http://www.w3.org/2000/xmlns/";
    }

    public NodeId NextId() => new(_nextId++);
    public void Register(XdmNode node) => _nodes[node.Id] = node;
    public XdmNode? GetNode(NodeId id) => _nodes.GetValueOrDefault(id);

    // INodeBuilder explicit interface implementations (delegate to existing methods)
    NodeId INodeBuilder.AllocateId() => NextId();
    void INodeBuilder.RegisterNode(XdmNode node) => Register(node);

    /// <summary>
    /// Removes a node from the store. Used by streaming execution to free
    /// temporary nodes after they have been processed.
    /// </summary>
    internal void Remove(NodeId id) => _nodes.Remove(id);

    public NamespaceId InternNamespace(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return NamespaceId.None;
        if (_nsToId.TryGetValue(uri, out var id))
            return id;
        id = new NamespaceId(_nextNsId++);
        _nsToId[uri] = id;
        _idToNs[id] = uri;
        return id;
    }

    public NamespaceId InternNamespace(string uri, NamespaceId preferredId)
    {
        if (string.IsNullOrEmpty(uri))
            return NamespaceId.None;
        if (_nsToId.TryGetValue(uri, out var existing))
            return existing;
        if (preferredId != NamespaceId.None && !_idToNs.ContainsKey(preferredId))
        {
            _nsToId[uri] = preferredId;
            _idToNs[preferredId] = uri;
            return preferredId;
        }
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

    public string? GetNamespaceUri(NamespaceId id)
    {
        if (id == NamespaceId.None)
            return null;
        return _idToNs.GetValueOrDefault(id);
    }

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
            if (_nodes.TryGetValue(childId, out var child))
                yield return child;
        }
    }

    public IEnumerable<XdmAttribute> GetAttributes(XdmElement elem)
    {
        foreach (var attrId in elem.Attributes)
        {
            if (_nodes.TryGetValue(attrId, out var attr) && attr is XdmAttribute a)
                yield return a;
        }
    }
}
