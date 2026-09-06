using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xdm.Serialization;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
// XPath 4.0 ordered map: insertion-order iteration as a structural guarantee.
// xslt keeps its existing default key-equality (pass EqualityComparer<object>.Default
// at each construction site) — this change is about iteration order only.
using OrderedXdmMap = PhoenixmlDb.XQuery.Execution.OrderedXdmMap;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Helper class for fn:snapshot implementation. Creates pruned deep copies of nodes.
/// </summary>
internal static class SnapshotHelper
{
    /// <summary>
    /// Deep copy items without ancestor chains (for fn:copy-of).
    /// </summary>
    public static object? DeepCopyItems(object? input, XdmInMemoryStore store,
        Dictionary<NodeId, NodeId>? nodeMapping = null)
    {
        if (input is null)
            return null;
        var docId = new DocumentId(1);

        if (input is object?[] arr)
        {
            var results = new List<object?>();
            foreach (var item in arr)
            {
                if (item is XdmNode node)
                    results.Add(DeepCopyNode(node, NodeId.None, docId, store, nodeMapping));
                else if (item is not null)
                    results.Add(item);
            }
            return results.ToArray();
        }

        if (input is IEnumerable<object?> seq && input is not XdmNode)
        {
            var results = new List<object?>();
            foreach (var item in seq)
            {
                if (item is XdmNode node)
                    results.Add(DeepCopyNode(node, NodeId.None, docId, store, nodeMapping));
                else if (item is not null)
                    results.Add(item);
            }
            return results.ToArray();
        }

        if (input is XdmNode singleNode)
            return DeepCopyNode(singleNode, NodeId.None, docId, store, nodeMapping);

        return input;
    }

    public static async ValueTask<object?> ProcessSnapshotAsync(object? input, DefaultXsltExecutionContext context)
    {
        if (input is null)
            return null;

        // Ensure accumulators are computed for source nodes before snapshot
        if (context._stylesheet.Accumulators.Count > 0)
        {
            foreach (var accName in context._stylesheet.Accumulators.Keys)
                await context.EnsureAccumulatorsComputedForInputAsync(accName, input).ConfigureAwait(false);
        }

        var nodeMapping = new Dictionary<NodeId, NodeId>();

        if (input is object?[] arr)
        {
            var results = new List<object?>();
            foreach (var item in arr)
            {
                if (item is not null)
                    results.Add(SnapshotItem(item, context, nodeMapping));
            }
            context.CopyAccumulatorValues(nodeMapping);
            return results.ToArray();
        }

        var result = SnapshotItem(input, context, nodeMapping);
        context.CopyAccumulatorValues(nodeMapping);
        return result;
    }

    public static object? SnapshotItem(object item, DefaultXsltExecutionContext context,
        Dictionary<NodeId, NodeId>? nodeMapping = null)
    {
        if (item is not XdmNode node)
            return item; // Non-node items pass through unchanged

        var store = context._nodeStore;
        if (store is null)
            return item;

        var docId = new DocumentId(1);

        // Build the ancestor chain from root to the target node
        var ancestors = new List<XdmNode>();
        var current = node;
        while (current.Parent is { } parentId && parentId != NodeId.None)
        {
            var parent = store.GetNode(parentId);
            if (parent is null)
                break;
            ancestors.Add(parent);
            current = parent;
        }
        ancestors.Reverse(); // Now: root → ... → parent of target

        // Case 1: Document node — deep copy the entire document
        if (node is XdmDocument)
        {
            return DeepCopyNode(node, NodeId.None, docId, store, nodeMapping);
        }

        // Case 2: Orphan node (no parent) — deep copy just the node
        if (ancestors.Count == 0)
        {
            return DeepCopyNode(node, NodeId.None, docId, store, nodeMapping);
        }

        // Case 3: Node with ancestors — create pruned tree
        XdmNode? previousCopy = null;
        NodeId previousCopyId = NodeId.None;

        foreach (var ancestor in ancestors)
        {
            if (ancestor is XdmDocument ancestorDoc)
            {
                var newDocId = store.NextId();
                nodeMapping?.TryAdd(ancestorDoc.Id, newDocId);
                var newDoc = new XdmDocument
                {
                    Id = newDocId,
                    Document = docId,
                    Parent = NodeId.None,
                    DocumentUri = null, // Snapshot docs lose their URI
                    Children = [] // Will be updated via LinkChild
                };
                store.Register(newDoc);
                previousCopy = newDoc;
                previousCopyId = newDocId;
            }
            else if (ancestor is XdmElement ancestorElem)
            {
                var newElemId = store.NextId();
                nodeMapping?.TryAdd(ancestorElem.Id, newElemId);

                // Copy attributes
                var newAttrIds = new List<NodeId>();
                foreach (var attrId in ancestorElem.Attributes)
                {
                    if (store.GetNode(attrId) is XdmAttribute origAttr)
                    {
                        var newAttrId = store.NextId();
                        nodeMapping?.TryAdd(origAttr.Id, newAttrId);
                        var newAttr = new XdmAttribute
                        {
                            Id = newAttrId,
                            Document = docId,
                            Parent = newElemId,
                            Namespace = origAttr.Namespace,
                            LocalName = origAttr.LocalName,
                            Prefix = origAttr.Prefix,
                            Value = origAttr.Value
                        };
                        store.Register(newAttr);
                        newAttrIds.Add(newAttrId);
                    }
                }

                var newElem = new XdmElement
                {
                    Id = newElemId,
                    Document = docId,
                    Parent = previousCopyId,
                    Namespace = ancestorElem.Namespace,
                    LocalName = ancestorElem.LocalName,
                    Prefix = ancestorElem.Prefix,
                    Attributes = newAttrIds,
                    Children = [], // Will be updated via LinkChild
                    NamespaceDeclarations = ancestorElem.NamespaceDeclarations
                };
                store.Register(newElem);

                // Link previous level to this element
                LinkChild(previousCopy, newElemId, store);

                previousCopy = newElem;
                previousCopyId = newElemId;
            }
        }

        // Deep-copy the target node and attach it to the last ancestor copy
        var targetCopy = DeepCopyNode(node, previousCopyId, docId, store, nodeMapping);

        // Link the target copy to the last ancestor
        if (targetCopy is not null)
            LinkChild(previousCopy, targetCopy.Id, store);

        // Compute string values for ancestor chain
        if (targetCopy is not null)
            ComputeAncestorStringValues(previousCopy, targetCopy, store);

        return targetCopy;
    }

    private static void LinkChild(XdmNode? parent, NodeId childId, XdmInMemoryStore store)
    {
        if (parent is XdmDocument doc)
        {
            var newDoc = new XdmDocument
            {
                Id = doc.Id,
                Document = doc.Document,
                Parent = doc.Parent,
                DocumentUri = doc.DocumentUri,
                DocumentElement = childId,
                Children = [childId],
                DocumentElementLocalName = (store.GetNode(childId) as XdmElement)?.LocalName
            };
            store.Register(newDoc);
        }
        else if (parent is XdmElement elem)
        {
            var newElem = new XdmElement
            {
                Id = elem.Id,
                Document = elem.Document,
                Parent = elem.Parent,
                Namespace = elem.Namespace,
                LocalName = elem.LocalName,
                Prefix = elem.Prefix,
                Attributes = elem.Attributes,
                Children = [childId],
                NamespaceDeclarations = elem.NamespaceDeclarations
            };
            // Recompute string value after adding child
            var child = store.GetNode(childId);
            newElem._stringValue = child?.StringValue ?? elem.StringValue;
            store.Register(newElem);
        }
    }

    private static void ComputeAncestorStringValues(XdmNode? lastAncestorCopy, XdmNode targetCopy, XdmInMemoryStore store)
    {
        var targetStringValue = targetCopy.StringValue;
        var current = lastAncestorCopy;
        while (current is not null)
        {
            if (current is XdmElement e)
                e._stringValue = targetStringValue;
            else if (current is XdmDocument d)
                d._stringValue = targetStringValue;

            if (current.Parent is { } pid && pid != NodeId.None)
                current = store.GetNode(pid);
            else
                break;
        }
    }

    private static XdmNode? DeepCopyNode(XdmNode node, NodeId parentId, DocumentId docId,
        XdmInMemoryStore store, Dictionary<NodeId, NodeId>? nodeMapping = null)
    {
        switch (node)
        {
            case XdmDocument doc:
            {
                var newId = store.NextId();
                nodeMapping?.TryAdd(doc.Id, newId);
                var childIds = new List<NodeId>();
                NodeId? docElemId = null;

                foreach (var childId in doc.Children)
                {
                    if (store.GetNode(childId) is { } child)
                    {
                        var copy = DeepCopyNode(child, newId, docId, store, nodeMapping);
                        if (copy is not null)
                        {
                            childIds.Add(copy.Id);
                            if (copy is XdmElement && docElemId is null)
                                docElemId = copy.Id;
                        }
                    }
                }

                var newDoc = new XdmDocument
                {
                    Id = newId,
                    Document = docId,
                    Parent = NodeId.None,
                    DocumentUri = null,
                    DocumentElement = docElemId,
                    Children = childIds,
                    DocumentElementLocalName = docElemId.HasValue ? (store.GetNode(docElemId.Value) as XdmElement)?.LocalName : null
                };
                newDoc._stringValue = doc.StringValue;
                store.Register(newDoc);
                return newDoc;
            }

            case XdmElement elem:
            {
                var newId = store.NextId();
                nodeMapping?.TryAdd(elem.Id, newId);

                // Copy attributes
                var newAttrIds = new List<NodeId>();
                foreach (var attrId in elem.Attributes)
                {
                    if (store.GetNode(attrId) is XdmAttribute origAttr)
                    {
                        var newAttrId = store.NextId();
                        nodeMapping?.TryAdd(origAttr.Id, newAttrId);
                        var newAttr = new XdmAttribute
                        {
                            Id = newAttrId,
                            Document = docId,
                            Parent = newId,
                            Namespace = origAttr.Namespace,
                            LocalName = origAttr.LocalName,
                            Prefix = origAttr.Prefix,
                            Value = origAttr.Value
                        };
                        store.Register(newAttr);
                        newAttrIds.Add(newAttrId);
                    }
                }

                // Deep copy children
                var childIds = new List<NodeId>();
                foreach (var childId in elem.Children)
                {
                    if (store.GetNode(childId) is { } child)
                    {
                        var copy = DeepCopyNode(child, newId, docId, store, nodeMapping);
                        if (copy is not null)
                            childIds.Add(copy.Id);
                    }
                }

                var newElem = new XdmElement
                {
                    Id = newId,
                    Document = docId,
                    Parent = parentId,
                    Namespace = elem.Namespace,
                    LocalName = elem.LocalName,
                    Prefix = elem.Prefix,
                    Attributes = newAttrIds,
                    Children = childIds,
                    NamespaceDeclarations = elem.NamespaceDeclarations
                };
                newElem._stringValue = elem.StringValue;
                store.Register(newElem);
                return newElem;
            }

            case XdmText text:
            {
                var newId = store.NextId();
                nodeMapping?.TryAdd(text.Id, newId);
                var newText = new XdmText
                {
                    Id = newId,
                    Document = docId,
                    Parent = parentId,
                    Value = text.Value
                };
                store.Register(newText);
                return newText;
            }

            case XdmComment comment:
            {
                var newId = store.NextId();
                nodeMapping?.TryAdd(comment.Id, newId);
                var newComment = new XdmComment
                {
                    Id = newId,
                    Document = docId,
                    Parent = parentId,
                    Value = comment.Value
                };
                store.Register(newComment);
                return newComment;
            }

            case XdmProcessingInstruction pi:
            {
                var newId = store.NextId();
                nodeMapping?.TryAdd(pi.Id, newId);
                var newPi = new XdmProcessingInstruction
                {
                    Id = newId,
                    Document = docId,
                    Parent = parentId,
                    Target = pi.Target,
                    Value = pi.Value
                };
                store.Register(newPi);
                return newPi;
            }

            case XdmAttribute attr:
            {
                var newId = store.NextId();
                nodeMapping?.TryAdd(attr.Id, newId);
                var newAttr = new XdmAttribute
                {
                    Id = newId,
                    Document = docId,
                    Parent = parentId,
                    Namespace = attr.Namespace,
                    LocalName = attr.LocalName,
                    Prefix = attr.Prefix,
                    Value = attr.Value
                };
                store.Register(newAttr);
                return newAttr;
            }

            case XdmNamespace ns:
            {
                var newId = store.NextId();
                nodeMapping?.TryAdd(ns.Id, newId);
                var newNs = new XdmNamespace
                {
                    Id = newId,
                    Document = docId,
                    Parent = parentId,
                    Prefix = ns.Prefix,
                    Uri = ns.Uri
                };
                store.Register(newNs);
                return newNs;
            }

            default:
                return null;
        }
    }
}

// ─── XSLT-aware format-number ───────────────────────────────────────────────

// ─── fn:copy-of ─────────────────────────────────────────────────────────────
