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
/// fn:parse-json($json-text, $options) — 2-arg version
/// </summary>
internal sealed class XsltParseJson2Function : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltParseJson2Function(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "parse-json");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => PhoenixmlDb.XQuery.Ast.XdmSequenceType.OptionalItem;
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "json-text"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.OptionalString },
        new() { Name = new QName(NamespaceId.None, "options"), Type = new PhoenixmlDb.XQuery.Ast.XdmSequenceType { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Item, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrOne } }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        return new XsltParseJsonFunction(_context).InvokeAsync(
            new object?[] { arguments[0] }, context);
    }
}
