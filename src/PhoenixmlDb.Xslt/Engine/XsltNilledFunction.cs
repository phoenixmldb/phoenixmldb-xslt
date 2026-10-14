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
/// fn:nilled($node as node()?) as xs:boolean?
/// Returns true if the node is a validated element with xsi:nil="true".
/// </summary>
internal sealed class XsltNilledFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltNilledFunction(DefaultXsltExecutionContext context) => _context = context;

    /// <summary>
    /// The nodes of the store an element is in, for the xsi:nil attribute of a copy: the
    /// query's own resolver does not see the nodes the transformation made, and a copy of a
    /// nilled element (copy-of(), snapshot(), validation="preserve") was not nilled.
    /// </summary>
    internal static Func<NodeId, XdmNode?>? NodesOf(DefaultXsltExecutionContext context, PhoenixmlDb.XQuery.Ast.ExecutionContext query)
    {
        var fromQuery = query is PhoenixmlDb.XQuery.Execution.QueryExecutionContext q ? q.LoadNode : (Func<NodeId, XdmNode?>?)null;
        var store = context._nodeStore;
        if (store is null)
            return fromQuery;
        return id => store.GetNode(id) ?? fromQuery?.Invoke(id);
    }

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "nilled");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => new() { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Boolean, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrOne };
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "node"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.OptionalNode }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var node = arguments[0];
        if (node is null)
            return ValueTask.FromResult<object?>(null);

        // A validated element with xsi:nil="true" is nilled; it was always false here, which was
        // right only while no element was ever validated. Empty for a node of any other kind.
        if (node is XdmElement element)
            return ValueTask.FromResult<object?>(PhoenixmlDb.XQuery.Execution.TypeCastHelper.IsNilled(element,
                NodesOf(_context, context)));

        return ValueTask.FromResult<object?>(null);
    }
}
