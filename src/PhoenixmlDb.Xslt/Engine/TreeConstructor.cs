using System.Collections.Immutable;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Builds an XDM tree directly in an <see cref="XdmInMemoryStore"/> without
/// serializing to XML text and reparsing. Namespace undeclaration and base URI
/// are node data, so it can express results the text round-trip cannot.
/// </summary>
internal sealed class TreeConstructor
{
    private static readonly IReadOnlyDictionary<string, NamespaceId> EmptyInScope =
        new Dictionary<string, NamespaceId>();

    private readonly XdmInMemoryStore _store;
    private readonly DocumentId _documentId;
    private readonly Stack<Frame> _open = new();
    private readonly List<NodeId> _roots = new();
    private readonly Dictionary<NodeId, IReadOnlyDictionary<string, NamespaceId>> _inScopeByElement = new();
    // Adjacent-text coalescing buffer for the current context (open frame, or the fragment
    // root when no element is open). Live child text is accumulated here and materialized as a
    // single XdmText node only when a non-text sibling arrives or the context closes — matching
    // how the serialize-then-reparse path (XmlReader) merges a contiguous character run into
    // one text node (SP-B slice 4). Root-level pending text uses <see cref="_rootPendingText"/>;
    // per-frame pending text lives on <see cref="Frame.PendingText"/>.
    private System.Text.StringBuilder? _rootPendingText;

    public TreeConstructor(XdmInMemoryStore store, ulong documentId)
    {
        _store = store;
        _documentId = new DocumentId(documentId);
    }

    public int Depth => _open.Count;

    public void StartElement(NamespaceId ns, string localName, string? prefix)
    {
        var parentInScope = _open.Count > 0
            ? (IReadOnlyDictionary<string, NamespaceId>)_open.Peek().InScope
            : EmptyInScope;
        StartElementCore(ns, localName, prefix, parentInScope, inheritNamespaces: true);
    }

    /// <summary>
    /// Starts an element, seeding its in-scope namespace map from <paramref name="inScope"/>
    /// (the parent context) when <paramref name="inheritNamespaces"/> is <c>true</c> (the
    /// default XSLT 3.0 behavior, §11.9.2), or starting from an empty map — plus the element's
    /// own namespace, if it has one — when <c>false</c> (<c>xsl:copy</c>/<c>xsl:element</c>
    /// with <c>inherit-namespaces="no"</c>, §14.2).
    /// </summary>
    public void StartElement(
        NamespaceId ns,
        string localName,
        string? prefix,
        IReadOnlyDictionary<string, NamespaceId> inScope,
        bool inheritNamespaces)
    {
        StartElementCore(ns, localName, prefix, inScope, inheritNamespaces);
    }

    /// <summary>
    /// Adds a namespace declaration to the currently open element. <paramref name="uri"/> of
    /// <see cref="NamespaceId.None"/> declares an undeclaration (<c>xmlns:prefix=""</c> /
    /// <c>xmlns=""</c>), which the old serialize-reparse path could not express.
    /// </summary>
    public void AddNamespace(string prefix, NamespaceId uri)
    {
        if (_open.Count == 0)
            throw new InvalidOperationException("AddNamespace requires an open element.");

        var frame = _open.Peek();
        frame.NamespaceDeclarations.Add(new NamespaceBinding(prefix, uri));
        if (uri == NamespaceId.None)
            frame.InScope.Remove(prefix);
        else
            frame.InScope[prefix] = uri;
    }

    /// <summary>
    /// Returns the finalized in-scope namespace map of a built element (populated at
    /// <see cref="EndElement"/>). Used by <c>xsl:copy</c>/tree-equality checks that need to
    /// know exactly which prefixes were visible on a node once construction completed.
    /// </summary>
    public IReadOnlyDictionary<string, NamespaceId> InScopeOf(NodeId elementId)
        => _inScopeByElement.TryGetValue(elementId, out var map) ? map : EmptyInScope;

    /// <summary>
    /// Updates the currently open element's prefix without changing its expanded name. Used for
    /// XSLT namespace fixup (§11.7), where an <c>xsl:namespace</c> in the element's content
    /// redefines the element's prefix to a conflicting URI and the serializer renames the prefix.
    /// The rename is decided only after the content (and therefore this element's children) has
    /// executed, so the open frame is patched in place before <see cref="EndElement"/>.
    /// </summary>
    public void SetOpenElementPrefix(string? prefix)
    {
        if (_open.Count == 0)
            throw new InvalidOperationException("SetOpenElementPrefix requires an open element.");
        _open.Peek().Prefix = string.IsNullOrEmpty(prefix) ? null : prefix;
    }

    private void StartElementCore(
        NamespaceId ns,
        string localName,
        string? prefix,
        IReadOnlyDictionary<string, NamespaceId> inScope,
        bool inheritNamespaces)
    {
        // Text preceding this child element belongs to the parent context and must be
        // materialized before the element node is interposed.
        FlushPendingText();
        var effectiveInScope = new Dictionary<string, NamespaceId>();
        if (inheritNamespaces)
        {
            foreach (var kv in inScope)
                effectiveInScope[kv.Key] = kv.Value;
        }
        if (ns != NamespaceId.None)
            effectiveInScope[prefix ?? string.Empty] = ns;

        var frame = new Frame
        {
            Id = _store.NextId(),
            DocumentId = _documentId,
            Namespace = ns,
            LocalName = localName,
            Prefix = string.IsNullOrEmpty(prefix) ? null : prefix,
            Attributes = new List<NodeId>(),
            NamespaceDeclarations = new List<NamespaceBinding>(),
            Parent = _open.Count > 0 ? _open.Peek().Id : (NodeId?)null,
            Children = new List<NodeId>(),
            InScope = effectiveInScope,
        };
        _open.Push(frame);
    }

    public void AppendText(string value)
    {
        if (value.Length == 0)
            return; // empty text nodes are never stored (XDM); an empty run adds nothing
        // Accumulate into the current context's pending buffer so adjacent text runs coalesce
        // into one node, matching the reparse path. The node is created on the next flush.
        if (_open.Count > 0)
            (_open.Peek().PendingText ??= new System.Text.StringBuilder()).Append(value);
        else
            (_rootPendingText ??= new System.Text.StringBuilder()).Append(value);
    }

    /// <summary>
    /// Materializes any buffered adjacent text for the current context (open frame, or the
    /// fragment root) into a single <see cref="XdmText"/> node and links it in document order.
    /// A no-op when there is no pending text.
    /// </summary>
    private void FlushPendingText()
    {
        var sb = _open.Count > 0 ? _open.Peek().PendingText : _rootPendingText;
        if (sb is null || sb.Length == 0)
            return;
        var value = sb.ToString();
        sb.Clear();
        var id = _store.NextId();
        var text = new XdmText
        {
            Id = id,
            Document = _documentId,
            Value = value,
            Parent = _open.Count > 0 ? _open.Peek().Id : (NodeId?)null,
        };
        _store.Register(text);
        AddChildRaw(id);
    }

    /// <summary>
    /// Adds an attribute node to the currently open element. Attributes are separate
    /// <see cref="XdmAttribute"/> nodes registered in the store and referenced by
    /// <see cref="NodeId"/> from <see cref="XdmElement.Attributes"/>, not inline objects.
    /// </summary>
    public void AddAttribute(NamespaceId ns, string localName, string? prefix, string value)
    {
        if (_open.Count == 0)
            throw new InvalidOperationException("AddAttribute requires an open element.");

        var frame = _open.Peek();
        var id = _store.NextId();
        var attr = new XdmAttribute
        {
            Id = id,
            Document = frame.DocumentId,
            Namespace = ns,
            LocalName = localName,
            Prefix = string.IsNullOrEmpty(prefix) ? null : prefix,
            Value = value,
            Parent = frame.Id,
        };
        _store.Register(attr);
        frame.Attributes.Add(id);
    }

    public void AppendComment(string value)
    {
        var id = _store.NextId();
        var comment = new XdmComment
        {
            Id = id,
            Document = _documentId,
            Value = value,
            Parent = _open.Count > 0 ? _open.Peek().Id : (NodeId?)null,
        };
        _store.Register(comment);
        AddChild(id);
    }

    public void AppendProcessingInstruction(string target, string data)
    {
        var id = _store.NextId();
        var pi = new XdmProcessingInstruction
        {
            Id = id,
            Document = _documentId,
            Target = target,
            Value = data,
            Parent = _open.Count > 0 ? _open.Peek().Id : (NodeId?)null,
        };
        _store.Register(pi);
        AddChild(id);
    }

    /// <summary>
    /// Appends an already-built/source node as a child of the currently open element (or
    /// as a fragment root) in document order. Replaces the old serialize/reparse interleave
    /// path — the node keeps its identity, and is simply re-parented and linked in.
    /// </summary>
    public void AppendNode(NodeId existing)
    {
        var node = _store.GetNode(existing);
        if (node is not null)
            node.Parent = _open.Count > 0 ? _open.Peek().Id : (NodeId?)null;
        AddChild(existing);
    }

    /// <summary>
    /// Sets the base URI of the currently open element, applied to
    /// <see cref="XdmNode.BaseUri"/>/<see cref="XdmNode.CopySourceBaseUri"/> at
    /// <see cref="EndElement"/>.
    /// </summary>
    public void SetBaseUri(string baseUri)
    {
        if (_open.Count == 0)
            throw new InvalidOperationException("SetBaseUri requires an open element.");

        _open.Peek().BaseUri = baseUri;
    }

    /// <summary>
    /// Records the source element's base URI on the currently open element, applied to
    /// <see cref="XdmNode.CopySourceBaseUri"/> ONLY (leaving <see cref="XdmNode.BaseUri"/>
    /// null) at <see cref="EndElement"/>. This is the shape an <c>xsl:copy</c> of a source
    /// element produces (§11.9.1): base-uri() reads the preserved copy-source base, and the
    /// serialize-then-reparse path it replaces likewise sets only CopySourceBaseUri, so a
    /// differential BaseUri comparison stays in parity.
    /// </summary>
    public void SetCopySourceBaseUri(string baseUri)
    {
        if (_open.Count == 0)
            throw new InvalidOperationException("SetCopySourceBaseUri requires an open element.");

        _open.Peek().CopySourceBaseUri = baseUri;
    }

    public void EndElement()
    {
        // Materialize this element's trailing text into its own children before it is sealed.
        FlushPendingText();
        var frame = _open.Pop();
        var elem = new XdmElement
        {
            Id = frame.Id,
            Document = frame.DocumentId,
            Namespace = frame.Namespace,
            LocalName = frame.LocalName,
            Prefix = frame.Prefix,
            Attributes = frame.Attributes.Count == 0 ? XdmElement.EmptyAttributes : frame.Attributes.ToImmutableArray(),
            Children = frame.Children.Count == 0 ? XdmElement.EmptyChildren : frame.Children.ToImmutableArray(),
            NamespaceDeclarations = frame.NamespaceDeclarations.Count == 0
                ? XdmElement.EmptyNamespaceDeclarations
                : frame.NamespaceDeclarations.ToImmutableArray(),
            Parent = frame.Parent,
        };
        if (frame.BaseUri is not null)
        {
            elem.BaseUri = frame.BaseUri;
            elem.CopySourceBaseUri = frame.BaseUri;
        }
        if (frame.CopySourceBaseUri is not null)
            elem.CopySourceBaseUri = frame.CopySourceBaseUri;
        elem._stringValue = ComputeStringValue(frame);
        _store.Register(elem);
        _inScopeByElement[frame.Id] = frame.InScope;
        AddChild(frame.Id);
    }

    public IReadOnlyList<NodeId> FinishFragment()
    {
        FlushPendingText(); // trailing fragment-root text
        return _roots;
    }

    public XdmDocument FinishDocument()
    {
        FlushPendingText(); // trailing document-root text
        var docNodeId = _store.NextId();
        var doc = new XdmDocument
        {
            Id = docNodeId,
            Document = _documentId,
            Children = _roots.Count == 0 ? XdmDocument.EmptyChildren : _roots.ToImmutableArray(),
        };
        _store.Register(doc);
        return doc;
    }

    private void AddChild(NodeId id)
    {
        // Any text preceding this node (comment/PI/element/appended node) comes first.
        FlushPendingText();
        AddChildRaw(id);
    }

    private void AddChildRaw(NodeId id)
    {
        if (_open.Count > 0)
            _open.Peek().Children.Add(id);
        else
            _roots.Add(id);
    }

    private string ComputeStringValue(Frame frame)
    {
        if (frame.Children.Count == 0)
            return string.Empty;

        var sb = new System.Text.StringBuilder();
        foreach (var childId in frame.Children)
        {
            switch (_store.GetNode(childId))
            {
                case XdmText t: sb.Append(t.Value); break;
                case XdmElement ce: sb.Append(ce.StringValue); break;
            }
        }
        return sb.ToString();
    }

    private sealed class Frame
    {
        public required NodeId Id;
        public required DocumentId DocumentId;
        public required NamespaceId Namespace;
        public required string LocalName;
        public required string? Prefix;
        public required List<NodeId> Attributes;
        public required List<NamespaceBinding> NamespaceDeclarations;
        public required NodeId? Parent;
        public required List<NodeId> Children;
        public required Dictionary<string, NamespaceId> InScope;
        public string? BaseUri;
        public string? CopySourceBaseUri;
        // Adjacent-text coalescing buffer for this element's direct children (SP-B slice 4).
        public System.Text.StringBuilder? PendingText;
    }
}
