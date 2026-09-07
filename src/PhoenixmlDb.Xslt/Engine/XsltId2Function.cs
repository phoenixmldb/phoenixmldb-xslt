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
/// fn:id($arg, $node) — 2-arg version that uses the specified node's document tree.
/// </summary>
internal sealed class XsltId2Function : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltId2Function(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "id");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => new()
    {
        ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Element,
        Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrMore
    };
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "arg"), Type = new PhoenixmlDb.XQuery.Ast.XdmSequenceType
            { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.String, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrMore } },
        new() { Name = new QName(NamespaceId.None, "node"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.Node }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var nodeArg = arguments[1];
        XdmDocument? doc = null;
        if (nodeArg is XdmDocument d)
            doc = d;
        else if (nodeArg is XdmNode n)
            doc = _context.FindDocumentForNode(n);
        if (doc == null)
            throw new XsltException("FODC0001: No context document for fn:id");

        return ValueTask.FromResult<object?>(XsltIdFunction.FindElementsById(arguments[0], doc, _context._nodeStore!));
    }
}
