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
/// fn:json-to-xml($json-text, $options) — 2-arg version
/// </summary>
internal sealed class XsltJsonToXml2Function : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltJsonToXml2Function(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "json-to-xml");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => new() { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Node, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrOne };
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "json-text"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.String },
        new() { Name = new QName(NamespaceId.None, "options"), Type = new PhoenixmlDb.XQuery.Ast.XdmSequenceType { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Item, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrOne } }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var jsonText = arguments[0]?.ToString();
        if (jsonText is null)
            return ValueTask.FromResult<object?>(null);

        var store = _context._nodeStore;
        if (store is null)
            throw new XsltException("FOJS0001: json-to-xml requires a node store");

        // Parse options map
        var liberal = false;
        var escape = false;
        var duplicates = "use-first";
        var options = arguments[1];
        if (options is IDictionary<object, object?> map)
        {
            // liberal option: must be xs:boolean
            if (map.TryGetValue("liberal", out var liberalVal))
            {
                if (liberalVal is bool lb)
                    liberal = lb;
                else if (liberalVal is null || liberalVal is object?[] arr && arr.Length == 0)
                    throw new XsltException("FOJS0001: Option 'liberal' must be a boolean value, got empty sequence");
                else
                    throw new XsltException("FOJS0001: Option 'liberal' must be a boolean value");
            }

            // validate option: must be xs:boolean
            if (map.TryGetValue("validate", out var validateVal))
            {
                if (validateVal is true)
                    throw new XsltException("FOJS0004: Option 'validate' is true but the processor is not schema-aware");
                if (validateVal is bool)
                { /* false — no action needed */ }
                else if (validateVal is null || validateVal is object?[] va && va.Length == 0)
                    throw new XsltException("XPTY0004: Option 'validate' must be a boolean value, got empty sequence");
                else if (validateVal is string)
                    throw new XsltException("XPTY0004: Option 'validate' must be a boolean value");
                else if (validateVal is object?[] va2 && va2.Length > 1)
                    throw new XsltException("XPTY0004: Option 'validate' must be a single boolean value");
                else
                    throw new XsltException("XPTY0004: Option 'validate' must be a boolean value");
            }

            // escape option: must be xs:boolean
            if (map.TryGetValue("escape", out var escapeVal))
            {
                if (escapeVal is bool eb)
                    escape = eb;
                else if (escapeVal is null || escapeVal is object?[] ea && ea.Length == 0)
                    throw new XsltException("XPTY0004: Option 'escape' must be a boolean value, got empty sequence");
                else if (escapeVal is string)
                    throw new XsltException("XPTY0004: Option 'escape' must be a boolean value");
                else if (escapeVal is object?[] ea2 && ea2.Length > 1)
                    throw new XsltException("XPTY0004: Option 'escape' must be a single boolean value");
                else
                    throw new XsltException("XPTY0004: Option 'escape' must be a boolean value");
            }

            // fallback option: must be a function
            if (map.TryGetValue("fallback", out var fallbackVal))
            {
                if (fallbackVal is not Delegate && fallbackVal is not PhoenixmlDb.XQuery.Ast.XQueryFunction)
                    throw new XsltException("XPTY0004: Option 'fallback' must be a function");
            }

            // duplicates option: must be xs:string with value use-first, retain, or reject
            if (map.TryGetValue("duplicates", out var dupVal))
            {
                if (dupVal is string ds)
                {
                    duplicates = ds switch
                    {
                        "use-first" or "retain" or "reject" => ds,
                        _ => throw new XsltException($"FOJS0005: Invalid value '{ds}' for option 'duplicates'; must be use-first, retain, or reject")
                    };
                }
                else
                    throw new XsltException("XPTY0004: Option 'duplicates' must be a string value");
            }
        }

        try
        {
            var doc = JsonToXmlConverter.Convert(jsonText, store, liberal, duplicates, escape);
            return ValueTask.FromResult<object?>(doc);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new XsltException($"FOJS0001: Invalid JSON: {ex.Message}");
        }
    }
}
