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
/// fn:outermost($nodes as node()*) as node()* — returns nodes that have no ancestor in the input sequence.
/// </summary>
internal sealed class XsltOutermostFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltOutermostFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "outermost");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => PhoenixmlDb.XQuery.Ast.XdmSequenceType.ZeroOrMoreItems;
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "nodes"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.ZeroOrMoreItems }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var items = arguments[0] switch
        {
            null => Array.Empty<object?>(),
            IEnumerable<object?> seq => seq.ToArray(),
            _ => new[] { arguments[0] }
        };

        var store = _context._nodeStore;
        if (store == null || items.Length == 0)
            return ValueTask.FromResult<object?>(items);

        // Collect all node IDs in the input
        var nodeIds = new HashSet<NodeId>();
        foreach (var item in items)
        {
            if (item is XdmNode node)
                nodeIds.Add(node.Id);
        }

        // Filter: keep nodes whose ancestor chain doesn't contain another node from the set
        var result = new List<object?>();
        foreach (var item in items)
        {
            if (item is XdmNode node)
            {
                var hasAncestorInSet = false;
                var current = node;
                while (current.Parent.HasValue)
                {
                    var parent = store.GetNode(current.Parent.Value);
                    if (parent == null)
                        break;
                    if (nodeIds.Contains(parent.Id))
                    {
                        hasAncestorInSet = true;
                        break;
                    }
                    current = parent;
                }
                if (!hasAncestorInSet)
                    result.Add(item);
            }
            else
            {
                result.Add(item); // non-node items pass through
            }
        }

        return ValueTask.FromResult<object?>(result.ToArray());
    }
}
