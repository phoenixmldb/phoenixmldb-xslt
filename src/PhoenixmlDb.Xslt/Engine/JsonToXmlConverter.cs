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
/// Converts JSON strings to XDM document trees using the XPath functions namespace.
/// </summary>
internal static class JsonToXmlConverter
{
    private static readonly NamespaceId FnNs = NamespaceId.Fn;

    public static XdmDocument Convert(string json, XdmInMemoryStore store, bool liberal = false, string duplicates = "use-first", bool escape = false)
    {
        using var jsonDoc = System.Text.Json.JsonDocument.Parse(json,
            new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = liberal, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });

        // Ensure the fn namespace is registered in the store for serialization
        EnsureFnNamespace(store);

        var docId = store.NextId();
        var rootElem = ConvertValue(jsonDoc.RootElement, null, store, duplicates, isRoot: true, escape: escape);

        var doc = new XdmDocument
        {
            StringValueResolver = store.StringValueResolver,
            Id = docId,
            Document = default,
            Children = new[] { rootElem.Id },
            DocumentElement = rootElem.Id
        };
        store.Register(doc);
        rootElem.Parent = docId;

        return doc;
    }

    private static XdmElement ConvertValue(System.Text.Json.JsonElement je, string? key, XdmInMemoryStore store, string duplicates, bool isRoot = false, bool escape = false)
    {
        return je.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => ConvertObject(je, key, store, duplicates, isRoot, escape),
            System.Text.Json.JsonValueKind.Array => ConvertArray(je, key, store, duplicates, isRoot, escape),
            System.Text.Json.JsonValueKind.String => CreateStringElement(je, key, store, isRoot, escape),
            System.Text.Json.JsonValueKind.Number => CreateSimpleElement("number", je.GetRawText(), key, store, isRoot),
            System.Text.Json.JsonValueKind.True => CreateSimpleElement("boolean", "true", key, store, isRoot),
            System.Text.Json.JsonValueKind.False => CreateSimpleElement("boolean", "false", key, store, isRoot),
            System.Text.Json.JsonValueKind.Null => CreateNullElement(key, store, isRoot),
            _ => throw new XsltException($"FOJS0001: Unsupported JSON value kind: {je.ValueKind}")
        };
    }

    private static readonly IReadOnlyList<NamespaceBinding> FnNsDecl = new[] { new NamespaceBinding("", FnNs) };

    private static XdmElement ConvertObject(System.Text.Json.JsonElement je, string? key, XdmInMemoryStore store, string duplicates, bool isRoot = false, bool escape = false)
    {
        var elemId = store.NextId();
        var children = new List<NodeId>();
        var attrs = new List<NodeId>();

        if (key != null)
            AddKeyAttribute(elemId, key, attrs, store);

        var seenKeys = duplicates != "retain" ? new HashSet<string>() : null;
        foreach (var prop in je.EnumerateObject())
        {
            if (seenKeys != null && !seenKeys.Add(prop.Name))
            {
                // Duplicate key found
                if (duplicates == "reject")
                    throw new XsltException($"FOJS0003: Duplicate key '{prop.Name}' in JSON object");
                // use-first: skip subsequent occurrences
                continue;
            }
            var child = ConvertValue(prop.Value, prop.Name, store, duplicates, escape: escape);
            child.Parent = elemId;
            children.Add(child.Id);
        }

        var elem = new XdmElement
        {
            StringValueResolver = store.StringValueResolver,
            Id = elemId,
            Document = default,
            Namespace = FnNs,
            LocalName = "map",
            Prefix = null,
            Attributes = attrs,
            Children = children,
            NamespaceDeclarations = isRoot ? FnNsDecl : System.Collections.Immutable.ImmutableArray<NamespaceBinding>.Empty
        };
        store.Register(elem);
        return elem;
    }

    private static XdmElement ConvertArray(System.Text.Json.JsonElement je, string? key, XdmInMemoryStore store, string duplicates, bool isRoot = false, bool escape = false)
    {
        var elemId = store.NextId();
        var children = new List<NodeId>();
        var attrs = new List<NodeId>();

        if (key != null)
            AddKeyAttribute(elemId, key, attrs, store);

        foreach (var item in je.EnumerateArray())
        {
            var child = ConvertValue(item, null, store, duplicates, escape: escape);
            child.Parent = elemId;
            children.Add(child.Id);
        }

        var elem = new XdmElement
        {
            StringValueResolver = store.StringValueResolver,
            Id = elemId,
            Document = default,
            Namespace = FnNs,
            LocalName = "array",
            Prefix = null,
            Attributes = attrs,
            Children = children,
            NamespaceDeclarations = isRoot ? FnNsDecl : System.Collections.Immutable.ImmutableArray<NamespaceBinding>.Empty
        };
        store.Register(elem);
        return elem;
    }

    /// <summary>
    /// Creates a fn:string element, adding escaped="true" attribute when the escape option is set
    /// and the JSON string contains backslash escape sequences.
    /// </summary>
    private static XdmElement CreateStringElement(System.Text.Json.JsonElement je, string? key, XdmInMemoryStore store, bool isRoot, bool escape)
    {
        var interpreted = je.GetString() ?? "";
        if (!escape)
            return CreateSimpleElement("string", interpreted, key, store, isRoot);

        // When escape=true, check if the raw JSON string contains escape sequences
        var raw = je.GetRawText(); // includes surrounding quotes
        var hasEscapes = raw.Length > 2 && raw.AsSpan(1, raw.Length - 2).Contains('\\');
        var elem = CreateSimpleElement("string", interpreted, key, store, isRoot);
        if (hasEscapes)
        {
            // Add escaped="true" attribute per XSLT 3.0 §22.1.2
            AddAttribute(elem.Id, NamespaceId.None, "escaped", "true", elem.Attributes as List<NodeId> ?? new List<NodeId>(), store);
        }
        return elem;
    }

    private static XdmElement CreateSimpleElement(string localName, string textValue, string? key, XdmInMemoryStore store, bool isRoot = false)
    {
        var elemId = store.NextId();
        var attrs = new List<NodeId>();

        if (key != null)
            AddKeyAttribute(elemId, key, attrs, store);

        var textId = store.NextId();
        var text = new XdmText
        {
            Id = textId,
            Document = default,
            Value = textValue,
            Parent = elemId
        };
        store.Register(text);

        var elem = new XdmElement
        {
            StringValueResolver = store.StringValueResolver,
            Id = elemId,
            Document = default,
            Namespace = FnNs,
            LocalName = localName,
            Prefix = null,
            Attributes = attrs,
            Children = new[] { textId },
            NamespaceDeclarations = isRoot ? FnNsDecl : System.Collections.Immutable.ImmutableArray<NamespaceBinding>.Empty,
            _stringValue = textValue
        };
        store.Register(elem);
        return elem;
    }

    private static XdmElement CreateNullElement(string? key, XdmInMemoryStore store, bool isRoot = false)
    {
        var elemId = store.NextId();
        var attrs = new List<NodeId>();

        if (key != null)
            AddKeyAttribute(elemId, key, attrs, store);

        var elem = new XdmElement
        {
            StringValueResolver = store.StringValueResolver,
            Id = elemId,
            Document = default,
            Namespace = FnNs,
            LocalName = "null",
            Prefix = null,
            Attributes = attrs,
            Children = System.Collections.Immutable.ImmutableArray<NodeId>.Empty,
            NamespaceDeclarations = isRoot ? FnNsDecl : System.Collections.Immutable.ImmutableArray<NamespaceBinding>.Empty
        };
        store.Register(elem);
        return elem;
    }

    private static void AddKeyAttribute(NodeId parentId, string key, List<NodeId> attrs, XdmInMemoryStore store)
    {
        AddAttribute(parentId, NamespaceId.None, "key", key, attrs, store);
    }

    private static void AddAttribute(NodeId parentId, NamespaceId ns, string localName, string value, List<NodeId> attrs, XdmInMemoryStore store)
    {
        var attrId = store.NextId();
        var attr = new XdmAttribute
        {
            Id = attrId,
            Document = default,
            Namespace = ns,
            LocalName = localName,
            Value = value,
            Parent = parentId
        };
        store.Register(attr);
        attrs.Add(attrId);
    }

    private static void EnsureFnNamespace(XdmInMemoryStore store)
    {
        store.RegisterKnownNamespace(FnNs, "http://www.w3.org/2005/xpath-functions");
    }
}

// ─── fn:parse-json ──────────────────────────────────────────────────────────
