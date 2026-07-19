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
    private readonly XdmInMemoryStore _store;
    private readonly DocumentId _documentId;
    private readonly Stack<Frame> _open = new();
    private readonly List<NodeId> _roots = new();

    public TreeConstructor(XdmInMemoryStore store, ulong documentId)
    {
        _store = store;
        _documentId = new DocumentId(documentId);
    }

    public int Depth => _open.Count;

    public void StartElement(NamespaceId ns, string localName, string? prefix)
    {
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
    }
}
