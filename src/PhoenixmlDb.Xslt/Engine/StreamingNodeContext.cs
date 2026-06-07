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
    /// Materialized XdmAttribute instances stashed here so the processor's cleanup path
    /// can return them to its pool. Populated by <c>MaterializeElement</c>, drained in
    /// <c>CleanupStreamingNode</c>. Null until materialization runs.
    /// </summary>
    internal List<XdmAttribute>? MaterializedAttributes { get; set; }

}
