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

    private void StartElementCore(
        NamespaceId ns,
        string localName,
        string? prefix,
        IReadOnlyDictionary<string, NamespaceId> inScope,
        bool inheritNamespaces)
    {
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
        var id = _store.NextId();
        var text = new XdmText
        {
            Id = id,
            Document = _documentId,
            Value = value,
            Parent = _open.Count > 0 ? _open.Peek().Id : (NodeId?)null,
        };
        _store.Register(text);
        AddChild(id);
    }

    public void EndElement()
    {
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
        elem._stringValue = ComputeStringValue(frame);
        _store.Register(elem);
        _inScopeByElement[frame.Id] = frame.InScope;
        AddChild(frame.Id);
    }

    public IReadOnlyList<NodeId> FinishFragment() => _roots;

    public XdmDocument FinishDocument()
    {
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
    }
}
