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
/// XSLT root() function (0-arg) - returns the root of the tree containing the context item.
/// </summary>
internal sealed class XsltRoot0Function : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    private XsltRootFunction? _rootFunc;

    public XsltRoot0Function(DefaultXsltExecutionContext context) => _context = context;

    internal void SetRootFunc(XsltRootFunction rootFunc) => _rootFunc = rootFunc;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "root");
    public override XdmSequenceType ReturnType => XdmSequenceType.Node;
    public override IReadOnlyList<FunctionParameterDef> Parameters => [];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        // The XPath context item first: inside a path step (`$snap/root()`) it is the step's node,
        // not the XSLT current node. Reading only the XSLT context returned the root of the node
        // the template was processing, so `snapshot(...)/root()` was the ORIGINAL document
        // (W3C snapshot-0112). Same pattern as generate-id#0.
        var contextItem = XQueryFocus.ItemOrNull(context) ?? _context.ContextItem;
        if (contextItem is not XdmNode node)
            return ValueTask.FromResult<object?>(null);
        if (_rootFunc != null)
            return ValueTask.FromResult<object?>(_rootFunc.TraverseToRoot(node));
        return ValueTask.FromResult<object?>(node);
    }
}
