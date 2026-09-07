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
/// fn:xml-to-json($input, $options) as xs:string? — 2-arg version
/// </summary>
internal sealed class XsltXmlToJson2Function : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltXmlToJson2Function(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "xml-to-json");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => PhoenixmlDb.XQuery.Ast.XdmSequenceType.OptionalString;
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "input"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.OptionalNode },
        new() { Name = new QName(NamespaceId.None, "options"), Type = new PhoenixmlDb.XQuery.Ast.XdmSequenceType { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Item, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrOne } }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        // Validate options map and read the indent option.
        var options = arguments[1];
        var indent = false;
        if (options is IDictionary<object, object?> map)
        {
            if (map.TryGetValue("indent", out var indentVal))
            {
                if (indentVal is not bool b)
                    throw new XsltException("XPTY0004: Option 'indent' must be a boolean value");
                indent = b;
            }
            if (map.TryGetValue("validate", out var validateVal))
            {
                if (validateVal is not bool)
                    throw new XsltException("XPTY0004: Option 'validate' must be a boolean value");
                if (validateVal is true)
                    throw new XsltException("FOJS0004: Option 'validate' is true but the processor is not schema-aware");
            }
        }

        var json = XsltXmlToJsonFunction.ToJsonString(arguments[0], _context, indent);
        return ValueTask.FromResult<object?>(json);
    }
}

// ─── fn:json-to-xml ─────────────────────────────────────────────────────────
