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
/// fn:json-to-xml($json-text as xs:string) as document-node()?
/// Converts a JSON string to the XML representation using the XPath functions namespace.
/// </summary>
internal sealed class XsltJsonToXmlFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltJsonToXmlFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "json-to-xml");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => new() { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Node, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrOne };
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "json-text"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.OptionalString }
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

        try
        {
            var doc = JsonToXmlConverter.Convert(jsonText, store, liberal: false, duplicates: "use-first");
            return ValueTask.FromResult<object?>(doc);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new XsltException($"FOJS0001: Invalid JSON: {ex.Message}");
        }
    }
}
