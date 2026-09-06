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
/// fn:parse-json($json-text as xs:string) as item()?
/// Parses a JSON string and returns an XDM map/array/atomic value.
/// </summary>
internal sealed class XsltParseJsonFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltParseJsonFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "parse-json");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => PhoenixmlDb.XQuery.Ast.XdmSequenceType.OptionalItem;
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "json-text"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.String }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var jsonText = arguments[0]?.ToString();
        if (jsonText is null)
            return ValueTask.FromResult<object?>(null);

        try
        {
            using var jsonDoc = System.Text.Json.JsonDocument.Parse(jsonText,
                new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip });
            var result = ConvertJsonToXdm(jsonDoc.RootElement);
            return ValueTask.FromResult(result);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new XsltException($"FOJS0001: Invalid JSON: {ex.Message}");
        }
    }

    internal static object? ConvertJsonToXdm(System.Text.Json.JsonElement je)
    {
        return je.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => ConvertObject(je),
            System.Text.Json.JsonValueKind.Array => ConvertArray(je),
            System.Text.Json.JsonValueKind.String => je.GetString(),
            System.Text.Json.JsonValueKind.Number => je.TryGetInt64(out var l) ? (object)l : je.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null,
            _ => throw new XsltException($"FOJS0001: Unsupported JSON value kind: {je.ValueKind}")
        };
    }

    private static OrderedXdmMap ConvertObject(System.Text.Json.JsonElement je)
    {
        var map = new OrderedXdmMap(EqualityComparer<object>.Default);
        foreach (var prop in je.EnumerateObject())
        {
            map[prop.Name] = ConvertJsonToXdm(prop.Value);
        }
        return map;
    }

    private static List<object?> ConvertArray(System.Text.Json.JsonElement je)
    {
        // Return List<object?> — the engine's XDM array representation (a single item).
        // Returning object?[] would be treated as a SEQUENCE and flattened by the
        // function-call machinery, so parse-json('[...]') would lose its array-ness
        // (Martin Honnen: parse-json($json)?* yielded the flattened members, and a
        // subsequent ?lookup hit a string → XPTY0004).
        var items = new List<object?>();
        foreach (var item in je.EnumerateArray())
        {
            items.Add(ConvertJsonToXdm(item));
        }
        return items;
    }
}
