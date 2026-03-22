using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Processes an XML document via XmlReader in a single forward pass,
/// dispatching to XSLT templates along the way. This is the core of
/// XSLT 3.0 streaming execution: the source document is never fully
/// materialized in memory.
/// </summary>
internal sealed class StreamingXmlProcessor
{
    private readonly XsltStylesheet _stylesheet;
    private readonly TemplateIndex _templateIndex;
    private readonly DefaultXsltExecutionContext _context;
    private readonly XsltTransformEngine.InMemoryNodeStore _nodeStore;
    private readonly QName? _mode;
    private ulong _nextNodeId = 1_000_000; // High start to avoid collision with tree nodes

    public StreamingXmlProcessor(
        XsltStylesheet stylesheet,
        TemplateIndex templateIndex,
        DefaultXsltExecutionContext context,
        XsltTransformEngine.InMemoryNodeStore nodeStore,
        QName? mode)
    {
        _stylesheet = stylesheet;
        _templateIndex = templateIndex;
        _context = context;
        _nodeStore = nodeStore;
        _mode = mode;
    }

    /// <summary>
    /// Processes the XML document from the reader in a single streaming pass.
    /// </summary>
    public async ValueTask ProcessAsync(XmlReader reader, CancellationToken ct = default)
    {
        var ancestorStack = new Stack<StreamingNodeContext>();

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                {
                    var nodeId = new NodeId(_nextNodeId++);
                    var isEmptyElement = reader.IsEmptyElement;

                    // Collect attributes and namespace declarations
                    var attrs = new List<StreamingNodeContext>();
                    var nsDecls = new Dictionary<string, string>();
                    if (reader.HasAttributes)
                    {
                        for (int i = 0; i < reader.AttributeCount; i++)
                        {
                            reader.MoveToAttribute(i);
                            if (reader.Prefix == "xmlns" || (reader.Prefix.Length == 0 && reader.LocalName == "xmlns"))
                            {
                                var prefix = reader.Prefix == "xmlns" ? reader.LocalName : "";
                                nsDecls[prefix] = reader.Value;
                            }
                            else
                            {
                                attrs.Add(new StreamingNodeContext
                                {
                                    NodeKind = XdmNodeKind.Attribute,
                                    LocalName = reader.LocalName,
                                    NamespaceUri = reader.NamespaceURI,
                                    Prefix = reader.Prefix,
                                    StringValue = reader.Value,
                                    NodeId = new NodeId(_nextNodeId++),
                                    Depth = reader.Depth + 1
                                });
                            }
                        }
                        reader.MoveToElement();
                    }

                    var current = new StreamingNodeContext
                    {
                        NodeKind = XdmNodeKind.Element,
                        LocalName = reader.LocalName,
                        NamespaceUri = reader.NamespaceURI,
                        Prefix = reader.Prefix,
                        NodeId = nodeId,
                        Attributes = attrs,
                        NamespaceDeclarations = nsDecls,
                        Parent = ancestorStack.Count > 0 ? ancestorStack.Peek() : null,
                        Depth = reader.Depth
                    };

                    // Match and execute template
                    var xdmElem = current.ToXdmElement(_nodeStore);
                    await _context.MatchAndExecuteStreamingNodeAsync(xdmElem, _mode, current.Position)
                        .ConfigureAwait(false);

                    if (!isEmptyElement)
                    {
                        ancestorStack.Push(current);
                    }
                    else
                    {
                        // Self-closing: clean up the temporary XDM node immediately
                        CleanupStreamingNode(current);
                    }
                    break;
                }

                case XmlNodeType.EndElement:
                {
                    if (ancestorStack.Count > 0)
                    {
                        var closingContext = ancestorStack.Pop();
                        // Clean up temporary XDM nodes to free memory
                        CleanupStreamingNode(closingContext);
                    }
                    break;
                }

                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                {
                    // Text nodes in streaming: create temporary text node, match templates
                    var textNodeId = new NodeId(_nextNodeId++);
                    var textNode = new XdmText
                    {
                        Id = textNodeId,
                        Document = new DocumentId(0),
                        Value = reader.Value
                    };
                    _nodeStore.Register(textNode);

                    // Match text node templates (e.g., text() match patterns)
                    await _context.MatchAndExecuteStreamingNodeAsync(textNode, _mode, 1)
                        .ConfigureAwait(false);

                    // Clean up
                    _nodeStore.Remove(textNodeId);
                    break;
                }

                case XmlNodeType.Comment:
                {
                    var commentId = new NodeId(_nextNodeId++);
                    var comment = new XdmComment
                    {
                        Id = commentId,
                        Document = new DocumentId(0),
                        Value = reader.Value
                    };
                    _nodeStore.Register(comment);
                    await _context.MatchAndExecuteStreamingNodeAsync(comment, _mode, 1)
                        .ConfigureAwait(false);
                    _nodeStore.Remove(commentId);
                    break;
                }

                case XmlNodeType.ProcessingInstruction:
                {
                    var piId = new NodeId(_nextNodeId++);
                    var pi = new XdmProcessingInstruction
                    {
                        Id = piId,
                        Document = new DocumentId(0),
                        Target = reader.LocalName,
                        Value = reader.Value
                    };
                    _nodeStore.Register(pi);
                    await _context.MatchAndExecuteStreamingNodeAsync(pi, _mode, 1)
                        .ConfigureAwait(false);
                    _nodeStore.Remove(piId);
                    break;
                }
            }
        }
    }

    private void CleanupStreamingNode(StreamingNodeContext ctx)
    {
        // Remove temporary XDM nodes to free memory
        foreach (var attr in ctx.Attributes)
            _nodeStore.Remove(attr.NodeId);
        _nodeStore.Remove(ctx.NodeId);
    }
}
