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
/// fn:copy-of($input as item()*) as item()* — creates deep copies of nodes.
/// </summary>
internal sealed class XsltCopyOfFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltCopyOfFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "copy-of");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => PhoenixmlDb.XQuery.Ast.XdmSequenceType.ZeroOrMoreItems;
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "input"), Type = new PhoenixmlDb.XQuery.Ast.XdmSequenceType { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Item, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrMore } }
    ];

    /// <summary>The node provider and static base URI of a call, for the base URI of its copies.</summary>
    internal static CopyBaseUris CopyBaseUrisOf(PhoenixmlDb.XQuery.Ast.ExecutionContext context)
        => context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext query
            ? new CopyBaseUris(query.NodeProvider, query.StaticBaseUri)
            : new CopyBaseUris(null, null);

    public override async ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        // fn:copy-of always copies accumulator values per XSLT 3.0 §16.6.9
        return await _context.DeepCopyWithAccumulatorsAsync(arguments[0], baseUris: CopyBaseUrisOf(context)).ConfigureAwait(false);
    }
}
