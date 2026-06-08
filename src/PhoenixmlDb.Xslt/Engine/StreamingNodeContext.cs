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
/// <remarks>
/// All properties are mutable so instances can be reused via the processor's
/// per-event pool. The type is internal and not held across
/// <c>CleanupStreamingNode</c>, so mutation is safe.
/// </remarks>
internal sealed class StreamingNodeContext
{
    public XdmNodeKind NodeKind { get; set; }
    public string LocalName { get; set; } = "";
    public string NamespaceUri { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string? StringValue { get; set; }
    public NodeId NodeId { get; set; }

    /// <summary>
    /// Attributes of the current element. Null when the element has no attributes —
    /// callers must treat null the same as empty.
    /// </summary>
    public List<StreamingNodeContext>? Attributes { get; set; }

    /// <summary>
    /// Namespace declarations on the current element. Null when no <c>xmlns:*</c>
    /// appears — callers must treat null as empty.
    /// </summary>
    public Dictionary<string, string>? NamespaceDeclarations { get; set; }

    /// <summary>Parent context (for ancestor axis limited to streaming depth).</summary>
    public StreamingNodeContext? Parent { get; set; }

    /// <summary>Depth in the document tree (0 = document, 1 = root element).</summary>
    public int Depth { get; set; }

    /// <summary>Position among siblings of the same node kind (1-based).</summary>
    public int Position { get; set; } = 1;

    /// <summary>
    /// Materialized XdmAttribute instances stashed here so the processor's cleanup path
    /// can return them to its pool. Populated by <c>MaterializeElement</c>, drained in
    /// <c>CleanupStreamingNode</c>. Null until materialization runs.
    /// </summary>
    internal List<XdmAttribute>? MaterializedAttributes { get; set; }
}
