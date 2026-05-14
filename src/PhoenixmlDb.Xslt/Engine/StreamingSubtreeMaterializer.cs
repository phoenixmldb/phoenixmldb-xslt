using System.Collections.Immutable;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Walks an <see cref="XmlReader"/> positioned at a StartElement and builds an
/// in-memory <see cref="XdmElement"/> subtree directly into the supplied
/// <see cref="XdmInMemoryStore"/> — no string round-trip. Used by the
/// streaming snapshot()/copy-of() fallback in <c>XsltTransformer</c>.
/// </summary>
/// <remarks>
/// On return, the reader is positioned at the matching EndElement of the
/// captured root (or, for an empty element, still on the empty element itself).
/// The caller controls reader advance from there — this materializer does not
/// consume past the matched subtree.
/// </remarks>
internal static class StreamingSubtreeMaterializer
{
    public static XdmElement? Materialize(XmlReader reader, XdmInMemoryStore store, DocumentId documentId)
    {
        if (reader.NodeType != XmlNodeType.Element) return null;

        var rootDepth = reader.Depth;
        var stack = new Stack<Frame>();
        var rootIsEmpty = reader.IsEmptyElement;

        // Push root frame
        var rootFrame = ReadElementStart(reader, store, documentId, parent: null);
        stack.Push(rootFrame);

        if (rootIsEmpty)
            return FinalizeFrame(rootFrame, store);

        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var isEmpty = reader.IsEmptyElement;
                    var parent = stack.Peek();
                    var frame = ReadElementStart(reader, store, documentId, parent.Id);
                    if (isEmpty)
                    {
                        var elem = FinalizeFrame(frame, store);
                        parent.Children.Add(elem.Id);
                    }
                    else
                    {
                        stack.Push(frame);
                    }
                    break;
                }

                case XmlNodeType.EndElement:
                {
                    var frame = stack.Pop();
                    var elem = FinalizeFrame(frame, store);
                    if (stack.Count == 0) return elem;
                    stack.Peek().Children.Add(elem.Id);
                    if (reader.Depth == rootDepth) return elem;
                    break;
                }

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                {
                    var textId = store.NextId();
                    var text = new XdmText
                    {
                        Id = textId,
                        Document = documentId,
                        Value = reader.Value,
                        Parent = stack.Peek().Id
                    };
                    store.Register(text);
                    stack.Peek().Children.Add(textId);
                    break;
                }

                case XmlNodeType.Whitespace:
                    // Skip whitespace-only text between elements unless preserved.
                    // Streaming-mode parser default mirrors XSLT strip-space behavior;
                    // we follow that here for parity.
                    break;

                case XmlNodeType.Comment:
                {
                    var commentId = store.NextId();
                    var comment = new XdmComment
                    {
                        Id = commentId,
                        Document = documentId,
                        Value = reader.Value,
                        Parent = stack.Peek().Id
                    };
                    store.Register(comment);
                    stack.Peek().Children.Add(commentId);
                    break;
                }

                case XmlNodeType.ProcessingInstruction:
                {
                    var piId = store.NextId();
                    var pi = new XdmProcessingInstruction
                    {
                        Id = piId,
                        Document = documentId,
                        Target = reader.Name,
                        Value = reader.Value,
                        Parent = stack.Peek().Id
                    };
                    store.Register(pi);
                    stack.Peek().Children.Add(piId);
                    break;
                }
            }
        }

        // Reader exhausted before EndElement — fold up whatever we have.
        XdmElement? tail = null;
        while (stack.Count > 0)
        {
            var frame = stack.Pop();
            var elem = FinalizeFrame(frame, store);
            if (stack.Count == 0) tail = elem;
            else stack.Peek().Children.Add(elem.Id);
        }
        return tail;
    }

    private static Frame ReadElementStart(XmlReader reader, XdmInMemoryStore store, DocumentId documentId, NodeId? parent)
    {
        var elemId = store.NextId();
        var attributes = new List<NodeId>();
        var nsBindings = new List<NamespaceBinding>();

        if (reader.HasAttributes)
        {
            for (int i = 0; i < reader.AttributeCount; i++)
            {
                reader.MoveToAttribute(i);
                if (reader.Prefix == "xmlns" || (reader.Prefix.Length == 0 && reader.LocalName == "xmlns"))
                {
                    var prefix = reader.Prefix == "xmlns" ? reader.LocalName : "";
                    nsBindings.Add(new NamespaceBinding(prefix, store.InternNamespace(reader.Value)));
                }
                else
                {
                    var attrId = store.NextId();
                    var attr = new XdmAttribute
                    {
                        Id = attrId,
                        Document = documentId,
                        Namespace = store.InternNamespace(reader.NamespaceURI ?? string.Empty),
                        LocalName = reader.LocalName,
                        Prefix = string.IsNullOrEmpty(reader.Prefix) ? null : reader.Prefix,
                        Value = reader.Value,
                        Parent = elemId
                    };
                    store.Register(attr);
                    attributes.Add(attrId);
                }
            }
            reader.MoveToElement();
        }

        return new Frame
        {
            Id = elemId,
            DocumentId = documentId,
            Namespace = store.InternNamespace(reader.NamespaceURI ?? string.Empty),
            LocalName = reader.LocalName,
            Prefix = string.IsNullOrEmpty(reader.Prefix) ? null : reader.Prefix,
            Attributes = attributes,
            NamespaceDeclarations = nsBindings,
            Parent = parent,
            Children = new List<NodeId>()
        };
    }

    private static XdmElement FinalizeFrame(Frame frame, XdmInMemoryStore store)
    {
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
            Parent = frame.Parent
        };
        store.Register(elem);
        return elem;
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
