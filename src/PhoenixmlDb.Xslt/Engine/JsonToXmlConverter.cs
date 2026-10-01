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
    private const string FnNamespaceUri = "http://www.w3.org/2005/xpath-functions";

    // The functions namespace's id IN THIS STORE. It was the predeclared NamespaceId.Fn, stamped on
    // every element — but template patterns resolve their names through the store's intern table,
    // and a store that had already interned this URI (pattern resolution does, at transform start)
    // holds it under another id. So match="fn:null" never matched a json-to-xml element, while
    // `instance of element(fn:null)`, which compares URIs, said it should: the W3C XSLT
    // implementation of xml-to-json fell through to its catch-all template and terminated
    // (xml-to-json-A2-*, -B2-*, once the harness passed their environment's static param).
    private static NamespaceId FnNsOf(XdmInMemoryStore store) => store.InternNamespace(FnNamespaceUri, NamespaceId.Fn);

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

    private static NamespaceBinding[] FnNsDeclOf(XdmInMemoryStore store) => new[] { new NamespaceBinding("", FnNsOf(store)) };

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
            var effectiveKey = escape ? EscapeSpecialCharacters(prop.Name) : ReplaceXmlInvalidCharacters(prop.Name);
            var child = ConvertValue(prop.Value, effectiveKey, store, duplicates, escape: escape);
            if (escape && effectiveKey.Contains('\\', StringComparison.Ordinal))
                AddAttribute(child.Id, NamespaceId.None, "escaped-key", "true", child.Attributes as List<NodeId> ?? new List<NodeId>(), store);
            child.Parent = elemId;
            children.Add(child.Id);
        }

        var elem = new XdmElement
        {
            StringValueResolver = store.StringValueResolver,
            Id = elemId,
            Document = default,
            Namespace = FnNsOf(store),
            LocalName = "map",
            Prefix = null,
            Attributes = attrs,
            Children = children,
            NamespaceDeclarations = isRoot ? FnNsDeclOf(store) : System.Collections.Immutable.ImmutableArray<NamespaceBinding>.Empty
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
            Namespace = FnNsOf(store),
            LocalName = "array",
            Prefix = null,
            Attributes = attrs,
            Children = children,
            NamespaceDeclarations = isRoot ? FnNsDeclOf(store) : System.Collections.Immutable.ImmutableArray<NamespaceBinding>.Empty
        };
        store.Register(elem);
        return elem;
    }

    /// <summary>
    /// Creates a fn:string element (F&amp;O 3.1 §17.5.3). With escape=false, a character that is not
    /// valid in XML becomes U+FFFD. With escape=true, every "special" character is written as a JSON
    /// escape sequence whether or not the input escaped it, and escaped="true" marks a value that
    /// then contains one.
    /// </summary>
    /// <remarks>
    /// The interpreted value used to be written as-is: a JSON "\u0000" put a raw NUL into the tree,
    /// so the serialized result was not XML (W3C json-to-xml-error-015, error-3250a), and with
    /// escape=true a form feed came out unescaped (json-to-xml-escape-005/006).
    /// </remarks>
    private static XdmElement CreateStringElement(System.Text.Json.JsonElement je, string? key, XdmInMemoryStore store, bool isRoot, bool escape)
    {
        var interpreted = je.GetString() ?? "";
        if (!escape)
            return CreateSimpleElement("string", ReplaceXmlInvalidCharacters(interpreted), key, store, isRoot);

        var escaped = EscapeSpecialCharacters(interpreted);
        var elem = CreateSimpleElement("string", escaped, key, store, isRoot);
        if (escaped.Contains('\\', StringComparison.Ordinal))
            AddAttribute(elem.Id, NamespaceId.None, "escaped", "true", elem.Attributes as List<NodeId> ?? new List<NodeId>(), store);
        return elem;
    }

    // Valid XML 1.0 characters: #x9 | #xA | #xD | [#x20-#xD7FF] | [#xE000-#xFFFD] | [#x10000-#x10FFFF].
    // A surrogate is valid only as half of a pair; the caller checks pairing.
    private static bool IsXmlInvalid(char c)
        => c < 0x20 ? c is not ('\t' or '\n' or '\r') : c is '\uFFFE' or '\uFFFF';

    private static bool IsUnpairedSurrogate(string s, int i)
        => char.IsHighSurrogate(s[i]) ? i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])
         : char.IsLowSurrogate(s[i]) && (i == 0 || !char.IsHighSurrogate(s[i - 1]));

    /// <summary>escape=false: characters not valid in XML become U+FFFD.</summary>
    private static string ReplaceXmlInvalidCharacters(string s)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < s.Length; i++)
        {
            if (IsXmlInvalid(s[i]) || IsUnpairedSurrogate(s, i))
            {
                sb ??= new StringBuilder(s, 0, i, s.Length);
                sb.Append('\uFFFD');
            }
            else
                sb?.Append(s[i]);
        }
        return sb?.ToString() ?? s;
    }

    /// <summary>
    /// escape=true: the backslash, the controls x00-x1F and x7F-x9F, and characters not valid in
    /// XML are written as JSON escapes, two-character where one exists (\f) and \uXXXX otherwise.
    /// </summary>
    private static string EscapeSpecialCharacters(string s)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            string? esc = c switch
            {
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c < 0x20 || (c >= 0x7F && c <= 0x9F) || IsXmlInvalid(c) || IsUnpairedSurrogate(s, i)
                    => "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture),
                _ => null,
            };
            if (esc != null)
            {
                sb ??= new StringBuilder(s, 0, i, s.Length + 8);
                sb.Append(esc);
            }
            else
                sb?.Append(c);
        }
        return sb?.ToString() ?? s;
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
            Namespace = FnNsOf(store),
            LocalName = localName,
            Prefix = null,
            Attributes = attrs,
            Children = new[] { textId },
            NamespaceDeclarations = isRoot ? FnNsDeclOf(store) : System.Collections.Immutable.ImmutableArray<NamespaceBinding>.Empty,
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
            Namespace = FnNsOf(store),
            LocalName = "null",
            Prefix = null,
            Attributes = attrs,
            Children = System.Collections.Immutable.ImmutableArray<NodeId>.Empty,
            NamespaceDeclarations = isRoot ? FnNsDeclOf(store) : System.Collections.Immutable.ImmutableArray<NamespaceBinding>.Empty
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
        FnNsOf(store);
    }
}

// ─── fn:parse-json ──────────────────────────────────────────────────────────
