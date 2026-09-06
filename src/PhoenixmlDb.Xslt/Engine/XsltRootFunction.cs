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
/// XSLT root($arg) function - traverses parent chain to the root of the containing tree.
/// </summary>
internal sealed class XsltRootFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltRootFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "root");
    public override XdmSequenceType ReturnType => XdmSequenceType.OptionalNode;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
        [new() { Name = new QName(NamespaceId.None, "arg"), Type = XdmSequenceType.OptionalNode }];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var arg = arguments[0];
        if (arg is not XdmNode node)
            return ValueTask.FromResult<object?>(null);
        return ValueTask.FromResult<object?>(TraverseToRoot(node));
    }

    internal XdmNode TraverseToRoot(XdmNode node)
    {
        var current = node;
        while (current.Parent is { } parentId)
        {
            var parent = _context._nodeStore?.GetNode(parentId);
            if (parent == null)
                break;
            current = parent;
        }
        return current;
    }
}
