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
/// fn:xml-to-json($input as node()?) as xs:string?
/// Converts the XML representation of JSON (using the XPath functions namespace)
/// back to a JSON string.
/// </summary>
internal sealed class XsltXmlToJsonFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltXmlToJsonFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "xml-to-json");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => PhoenixmlDb.XQuery.Ast.XdmSequenceType.OptionalString;
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "input"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.OptionalNode }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        return ValueTask.FromResult<object?>(ToJsonString(arguments[0], _context));
    }

    /// <summary>
    /// Core of fn:xml-to-json: resolves the input to the root element of an XML JSON
    /// representation and serializes it to a (compact) JSON string. Returns null for an
    /// empty input. Shared by the 1-arg and 2-arg (options) overloads.
    /// </summary>
    internal static string? ToJsonString(object? input, DefaultXsltExecutionContext context, bool indent = false)
    {
        if (input is null)
            return null;
        // XPTY0004: xml-to-json expects a single node, not a sequence
        if (input is object?[] arr && arr.Length > 1)
            throw new XsltException("XPTY0004: A sequence of " + arr.Length + " items is not allowed as the first argument of xml-to-json()");

        var store = context._nodeStore;
        if (store is null)
            throw new XsltException("FOJS0006: xml-to-json requires a node store");

        var elem = ResolveToElement(input, store);
        if (elem is null)
            return null;

        var sb = new System.Text.StringBuilder();
        SerializeJsonElement(elem, store, sb, indent, 0);
        return sb.ToString();
    }

    internal static XdmElement? ResolveToElement(object? input, XdmInMemoryStore store)
    {
        if (input is XdmElement el)
            return el;
        if (input is XdmDocument doc)
        {
            // Find first element child, but check for multiple elements (FOJS0006)
            XdmElement? first = null;
            foreach (var childId in doc.Children)
            {
                if (store.GetNode(childId) is XdmElement child)
                {
                    if (first != null)
                        throw new XsltException("FOJS0006: xml-to-json input contains multiple element children");
                    first = child;
                }
            }
            return first;
        }
        return null;
    }

    internal static void SerializeJsonElement(XdmElement elem, XdmInMemoryStore store, System.Text.StringBuilder sb, bool indent = false, int depth = 0)
    {
        // Elements must be in the http://www.w3.org/2005/xpath-functions namespace
        var localName = elem.LocalName;

        switch (localName)
        {
            case "null":
            {
                // Null must have no non-whitespace text content and no element children
                ValidateNoElementChildren(elem, store, "null");
                var nullText = GetTextContent(elem, store).Trim();
                if (nullText.Length > 0)
                    throw new XsltException("FOJS0006: null element must have no content");
                ValidateAttributes(elem, store, "null", ["key", "escaped-key"]);
                sb.Append("null");
                break;
            }

            case "boolean":
            {
                ValidateNoElementChildren(elem, store, "boolean");
                ValidateAttributes(elem, store, "boolean", ["key", "escaped-key"]);
                var text = GetTextContent(elem, store).Trim();
                // Accepts "true", "false", "1", "0"
                sb.Append(text switch
                {
                    "true" or "1" => "true",
                    "false" or "0" => "false",
                    _ => throw new XsltException($"FOJS0006: Invalid boolean value: '{text}'")
                });
                break;
            }

            case "number":
            {
                ValidateNoElementChildren(elem, store, "number");
                ValidateAttributes(elem, store, "number", ["key", "escaped-key"]);
                var text = GetTextContent(elem, store).Trim();
                // Validate it's a valid JSON number
                if (!IsValidJsonNumber(text))
                    throw new XsltException($"FOJS0006: Invalid number value: '{text}'");
                // The lexical form is an xs:double; emit its canonical JSON-number form
                // (strip leading zeros, preserve -0, canonical exponent, etc.).
                var numVal = double.Parse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture);
                sb.Append(PhoenixmlDb.XQuery.Functions.XmlToJsonFunction.FormatJsonNumber(numVal));
                break;
            }

            case "string":
            {
                // String elements must contain only text nodes (no element children)
                ValidateNoElementChildren(elem, store, "string");
                var escaped = GetAttributeValue(elem, "escaped", store);
                ValidateBooleanAttribute(escaped, "escaped");
                ValidateAttributes(elem, store, "string", ["key", "escaped-key", "escaped"]);
                var isEscaped = IsTruthy(escaped);
                var text = GetTextContent(elem, store);

                sb.Append('"');
                if (isEscaped)
                    AppendValidatedEscapedJsonString(text, sb);
                else
                    AppendJsonString(text, sb);
                sb.Append('"');
                break;
            }

            case "array":
            {
                ValidateAttributes(elem, store, "array", ["key", "escaped-key"]);
                // Array must not contain non-whitespace text
                ValidateNoSignificantText(elem, store, "array");
                sb.Append('[');
                var first = true;
                foreach (var childId in elem.Children)
                {
                    var child = store.GetNode(childId);
                    if (child is XdmElement childElem && childElem.Namespace == NamespaceId.Fn)
                    {
                        if (!first)
                            sb.Append(',');
                        if (indent)
                            XsltTransformEngine.AppendJsonNewlineIndent(sb, depth + 1);
                        first = false;
                        SerializeJsonElement(childElem, store, sb, indent, depth + 1);
                    }
                    else if (child is XdmElement childElem2)
                    {
                        // Element not in fn namespace — try processing anyway
                        // (namespace IDs may differ in RTF node stores)
                        if (!first)
                            sb.Append(',');
                        if (indent)
                            XsltTransformEngine.AppendJsonNewlineIndent(sb, depth + 1);
                        first = false;
                        SerializeJsonElement(childElem2, store, sb, indent, depth + 1);
                    }
                }
                if (!first && indent)
                    XsltTransformEngine.AppendJsonNewlineIndent(sb, depth);
                sb.Append(']');
                break;
            }

            case "map":
            {
                ValidateAttributes(elem, store, "map", ["key", "escaped-key"]);
                // Map must not contain non-whitespace text
                ValidateNoSignificantText(elem, store, "map");
                sb.Append('{');
                var first = true;
                var seenKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var childId in elem.Children)
                {
                    var child = store.GetNode(childId);
                    if (child is XdmElement childElem && childElem.Namespace == NamespaceId.Fn)
                    {
                        if (!first)
                            sb.Append(',');
                        if (indent)
                            XsltTransformEngine.AppendJsonNewlineIndent(sb, depth + 1);
                        first = false;

                        // Get the key
                        var key = GetAttributeValue(childElem, "key", store);
                        if (key is null)
                            throw new XsltException("FOJS0006: Map entry missing 'key' attribute");

                        var escapedKey = GetAttributeValue(childElem, "escaped-key", store);
                        ValidateBooleanAttribute(escapedKey, "escaped-key");
                        var isEscapedKey = IsTruthy(escapedKey);

                        // Duplicate detection uses decoded key values
                        var decodedKey = DecodeJsonKey(key, isEscapedKey);
                        if (!XsltTransformEngine.TryAddJsonKey(seenKeys, decodedKey))
                            throw new XsltException($"FOJS0006: Duplicate key in map: '{key}'");

                        sb.Append('"');
                        if (isEscapedKey)
                            AppendValidatedEscapedJsonString(key, sb);
                        else
                            AppendJsonString(key, sb);
                        sb.Append('"');
                        sb.Append(indent ? ": " : ":");
                        SerializeJsonElement(childElem, store, sb, indent, depth + 1);
                    }
                    else if (child is XdmElement childElem2)
                    {
                        // Element not in fn namespace — try processing anyway
                        // (namespace IDs may differ in RTF node stores)
                        if (!first)
                            sb.Append(',');
                        if (indent)
                            XsltTransformEngine.AppendJsonNewlineIndent(sb, depth + 1);
                        first = false;

                        var key2 = GetAttributeValue(childElem2, "key", store);
                        if (key2 is null)
                            throw new XsltException("FOJS0006: Map entry missing 'key' attribute");

                        var escapedKey2 = GetAttributeValue(childElem2, "escaped-key", store);
                        ValidateBooleanAttribute(escapedKey2, "escaped-key");
                        var isEscapedKey2 = IsTruthy(escapedKey2);

                        var decodedKey2 = DecodeJsonKey(key2, isEscapedKey2);
                        if (!XsltTransformEngine.TryAddJsonKey(seenKeys, decodedKey2))
                            throw new XsltException($"FOJS0006: Duplicate key in map: '{key2}'");

                        sb.Append('"');
                        if (isEscapedKey2)
                            AppendValidatedEscapedJsonString(key2, sb);
                        else
                            AppendJsonString(key2, sb);
                        sb.Append('"');
                        sb.Append(indent ? ": " : ":");
                        SerializeJsonElement(childElem2, store, sb, indent, depth + 1);
                    }
                }
                if (!first && indent)
                    XsltTransformEngine.AppendJsonNewlineIndent(sb, depth);
                sb.Append('}');
                break;
            }

            default:
                throw new XsltException($"FOJS0006: Unknown JSON element type: '{localName}'");
        }
    }

    /// <summary>
    /// Gets the concatenated text content of an element (ignoring comments and PIs).
    /// </summary>
    internal static string GetTextContent(XdmElement elem, XdmInMemoryStore store)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var childId in elem.Children)
        {
            var child = store.GetNode(childId);
            if (child is XdmText text)
                sb.Append(text.Value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Gets an attribute value by local name (no namespace).
    /// </summary>
    internal static string? GetAttributeValue(XdmElement elem, string localName, XdmInMemoryStore store)
    {
        foreach (var attrId in elem.Attributes)
        {
            var attr = store.GetNode(attrId) as XdmAttribute;
            if (attr != null && attr.LocalName == localName && attr.Namespace == NamespaceId.None)
                return attr.Value;
        }
        return null;
    }

    /// <summary>
    /// Checks if a string attribute value is truthy (true/1, ignoring whitespace).
    /// </summary>
    internal static bool IsTruthy(string? value)
    {
        if (value is null)
            return false;
        var trimmed = value.Trim();
        return trimmed == "true" || trimmed == "1";
    }

    /// <summary>
    /// Appends a single non-backslash character to fn:xml-to-json string output. On top of
    /// the shared JSON per-character escaper this applies the two fn:xml-to-json-specific
    /// rules: the solidus is escaped as <c>\/</c> (bug 29665), and DEL together with the C1
    /// control block (#x7F–#x9F) are output as <c>\uXXXX</c>.
    /// </summary>
    internal static void AppendJsonChar(System.Text.StringBuilder sb, char c)
    {
        if (c == '/')
            sb.Append("\\/");
        else if (c >= '\x7F' && c <= '\x9F')
            sb.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
        else
            CharacterEscaper.AppendJsonEscapedChar(sb, c);
    }

    /// <summary>
    /// Appends a string to the JSON output, escaping characters that need it per JSON spec.
    /// Used when escaped="false" (or absent) — the input is plain text.
    /// </summary>
    internal static void AppendJsonString(string text, System.Text.StringBuilder sb)
    {
        foreach (var c in text)
        {
            if (c == '\\') { sb.Append("\\\\"); continue; }
            CharacterEscaper.AppendJsonEscapedChar(sb, c);
        }
    }

    /// <summary>
    /// Appends a string to the JSON output when escaped="true" — the input already contains
    /// JSON escape sequences like \n, \uXXXX etc. We pass through backslash sequences as-is
    /// but still need to escape any characters that would be invalid in a JSON string.
    /// </summary>
    internal static void AppendEscapedJsonString(string text, System.Text.StringBuilder sb)
    {
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                // Pass through recognized JSON escape sequences
                var next = text[i + 1];
                switch (next)
                {
                    case '"':
                    case '\\':
                    case '/':
                    case 'b':
                    case 'f':
                    case 'n':
                    case 'r':
                    case 't':
                        sb.Append(c);
                        sb.Append(next);
                        i++;
                        continue;
                    case 'u' when i + 5 < text.Length:
                        // \uXXXX — pass through
                        sb.Append(text, i, 6);
                        i += 5;
                        continue;
                }
            }

            AppendJsonChar(sb, c);
        }
    }

    /// <summary>
    /// Validates that a string is a valid JSON number.
    /// </summary>
    internal static bool IsValidJsonNumber(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        // Allow a superset: optional minus, digits, optional dot+digits, optional E/e +/- digits
        int i = 0;
        if (i < text.Length && text[i] == '-')
            i++;
        if (i >= text.Length)
            return false;

        // Integer part (allow leading zeros for lenient parsing)
        bool hasDigit = false;
        while (i < text.Length && text[i] >= '0' && text[i] <= '9')
        { i++; hasDigit = true; }

        // Fractional part
        if (i < text.Length && text[i] == '.')
        {
            i++;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9')
            { i++; hasDigit = true; }
        }

        if (!hasDigit)
            return false;

        // Exponent part
        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            i++;
            if (i < text.Length && (text[i] == '+' || text[i] == '-'))
                i++;
            bool hasExpDigit = false;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9')
            { i++; hasExpDigit = true; }
            if (!hasExpDigit)
                return false;
        }

        return i == text.Length;
    }

    /// <summary>
    /// Validates that an element has no child elements (only text/comments/PIs allowed).
    /// </summary>
    internal static void ValidateNoElementChildren(XdmElement elem, XdmInMemoryStore store, string type)
    {
        foreach (var childId in elem.Children)
        {
            if (store.GetNode(childId) is XdmElement)
                throw new XsltException($"FOJS0006: {type} element must not contain child elements");
        }
    }

    /// <summary>
    /// Validates that a container element (array/map) has no non-whitespace text content.
    /// </summary>
    internal static void ValidateNoSignificantText(XdmElement elem, XdmInMemoryStore store, string type)
    {
        foreach (var childId in elem.Children)
        {
            if (store.GetNode(childId) is XdmText text && text.Value.AsSpan().Trim().Length > 0)
                throw new XsltException($"FOJS0006: {type} element must not contain text content");
        }
    }

    /// <summary>
    /// Validates that only allowed attributes are present on an element.
    /// </summary>
    internal static void ValidateAttributes(XdmElement elem, XdmInMemoryStore store, string type, string[] allowed)
    {
        foreach (var attrId in elem.Attributes)
        {
            var attr = store.GetNode(attrId) as XdmAttribute;
            if (attr == null)
                continue;
            if (attr.Namespace == NamespaceId.None)
            {
                bool found = false;
                foreach (var a in allowed)
                {
                    if (attr.LocalName == a)
                    { found = true; break; }
                }
                if (!found)
                    throw new XsltException($"FOJS0006: Invalid attribute '{attr.LocalName}' on {type} element");
            }
            else
            {
                // Check if this is an attribute in the fn namespace — reject unknown ones
                var nsUri = store.GetNamespaceUri(attr.Namespace);
                if (nsUri == "http://www.w3.org/2005/xpath-functions")
                    throw new XsltException($"FOJS0006: Invalid attribute in fn namespace '{attr.LocalName}' on {type} element");
            }
        }
    }

    /// <summary>
    /// Validates that a boolean-like attribute has value "true", "false", "1", or "0".
    /// </summary>
    internal static void ValidateBooleanAttribute(string? value, string attrName)
    {
        if (value is null)
            return;
        var trimmed = value.Trim();
        if (trimmed != "true" && trimmed != "false" && trimmed != "1" && trimmed != "0")
            throw new XsltException($"FOJS0006: Invalid value '{value}' for attribute '{attrName}'");
    }

    /// <summary>
    /// Like AppendEscapedJsonString but validates that escape sequences are valid JSON.
    /// Throws FOJS0006 for invalid escape sequences.
    /// </summary>
    internal static void AppendValidatedEscapedJsonString(string text, System.Text.StringBuilder sb)
    {
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                if (i + 1 >= text.Length)
                    throw new XsltException("FOJS0006: Incomplete escape sequence at end of string");
                var next = text[i + 1];
                switch (next)
                {
                    case '"':
                    case '\\':
                    case '/':
                    case 'b':
                    case 'f':
                    case 'n':
                    case 'r':
                    case 't':
                        sb.Append(c);
                        sb.Append(next);
                        i++;
                        continue;
                    case 'u':
                        if (i + 5 >= text.Length)
                            throw new XsltException("FOJS0006: Incomplete \\u escape sequence");
                        // Validate hex digits
                        for (int j = i + 2; j < i + 6; j++)
                        {
                            if (!IsHexDigit(text[j]))
                                throw new XsltException($"FOJS0006: Invalid \\u escape sequence: '{text.Substring(i, 6)}'");
                        }
                        sb.Append(text, i, 6);
                        i += 5;
                        continue;
                    default:
                        throw new XsltException($"FOJS0006: Invalid escape sequence '\\{next}'");
                }
            }

            AppendJsonChar(sb, c);
        }
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>
    /// Decodes a JSON key to its actual string value for duplicate detection.
    /// When escaped-key is true, JSON escape sequences (\n, \uXXXX, \", \\, etc.) are decoded.
    /// When escaped-key is false, the raw key IS the decoded value.
    /// </summary>
    internal static string DecodeJsonKey(string rawKey, bool isEscaped)
    {
        if (!isEscaped)
            return rawKey;

        var sb = new System.Text.StringBuilder(rawKey.Length);
        for (int i = 0; i < rawKey.Length; i++)
        {
            var c = rawKey[i];
            if (c == '\\' && i + 1 < rawKey.Length)
            {
                var next = rawKey[i + 1];
                switch (next)
                {
                    case '"':
                        sb.Append('"');
                        i++;
                        continue;
                    case '\\':
                        sb.Append('\\');
                        i++;
                        continue;
                    case '/':
                        sb.Append('/');
                        i++;
                        continue;
                    case 'b':
                        sb.Append('\b');
                        i++;
                        continue;
                    case 'f':
                        sb.Append('\f');
                        i++;
                        continue;
                    case 'n':
                        sb.Append('\n');
                        i++;
                        continue;
                    case 'r':
                        sb.Append('\r');
                        i++;
                        continue;
                    case 't':
                        sb.Append('\t');
                        i++;
                        continue;
                    case 'u' when i + 5 < rawKey.Length:
                        var hex = rawKey.Substring(i + 2, 4);
                        if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var codePoint))
                        {
                            sb.Append((char)codePoint);
                            i += 5;
                            continue;
                        }
                        break;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
