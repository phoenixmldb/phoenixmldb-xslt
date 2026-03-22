using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Lightweight XDM node representation for streaming execution.
/// Wraps XmlReader state for the current element without requiring
/// the full document tree. Supports element, attribute, text, document,
/// comment, and processing-instruction node kinds.
/// </summary>
internal sealed class StreamingNodeContext
{
    public XdmNodeKind NodeKind { get; init; }
    public required string LocalName { get; init; }
    public required string NamespaceUri { get; init; }
    public string Prefix { get; init; } = "";
    public string? StringValue { get; set; }
    public NodeId NodeId { get; init; }

    /// <summary>Attributes of the current element (empty for non-elements).</summary>
    public List<StreamingNodeContext> Attributes { get; init; } = [];

    /// <summary>Namespace declarations on the current element.</summary>
    public Dictionary<string, string> NamespaceDeclarations { get; init; } = new();

    /// <summary>Parent context (for ancestor axis limited to streaming depth).</summary>
    public StreamingNodeContext? Parent { get; init; }

    /// <summary>Depth in the document tree (0 = document, 1 = root element).</summary>
    public int Depth { get; init; }

    /// <summary>Position among siblings of the same node kind (1-based).</summary>
    public int Position { get; set; } = 1;

    /// <summary>
    /// Creates an XdmElement from this streaming context for template matching
    /// and expression evaluation. The node is minimal — no children, no siblings.
    /// </summary>
    public XdmElement ToXdmElement(XsltTransformEngine.InMemoryNodeStore store)
    {
        var nsId = store.InternNamespace(NamespaceUri);
        var attrIds = new List<NodeId>();
        var nsBindings = new List<NamespaceBinding>();

        foreach (var attr in Attributes)
        {
            var attrNsId = store.InternNamespace(attr.NamespaceUri);
            var xdmAttr = new XdmAttribute
            {
                Id = attr.NodeId,
                Document = new DocumentId(0), // Streaming — no real document
                Namespace = attrNsId,
                LocalName = attr.LocalName,
                Prefix = attr.Prefix,
                Value = attr.StringValue ?? ""
            };
            store.Register(xdmAttr);
            attrIds.Add(attr.NodeId);
        }

        foreach (var (prefix, uri) in NamespaceDeclarations)
        {
            nsBindings.Add(new NamespaceBinding(prefix, store.InternNamespace(uri)));
        }

        var elem = new XdmElement
        {
            Id = NodeId,
            Document = new DocumentId(0), // Streaming — no real document
            Namespace = nsId,
            LocalName = LocalName,
            Prefix = Prefix,
            Attributes = attrIds,
            Children = XdmElement.EmptyChildren,
            NamespaceDeclarations = nsBindings
        };
        store.Register(elem);
        return elem;
    }
}
