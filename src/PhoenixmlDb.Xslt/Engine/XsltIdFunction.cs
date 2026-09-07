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
/// fn:id($arg) — returns the elements with ID attributes matching the given IDREFS values.
/// Uses the context item's document tree.
/// </summary>
internal sealed class XsltIdFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltIdFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "id");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => new()
    {
        ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Element,
        Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrMore
    };
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "arg"), Type = new PhoenixmlDb.XQuery.Ast.XdmSequenceType
            { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.String, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrMore } }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var contextItem = context.ContextItem ?? _context.ContextItem;
        XdmDocument? doc = null;
        if (contextItem is XdmDocument d)
            doc = d;
        else if (contextItem is XdmNode n)
            doc = _context.FindDocumentForNode(n);
        if (doc == null)
            throw new XsltException("FODC0001: No context document for fn:id");

        return ValueTask.FromResult<object?>(FindElementsById(arguments[0], doc, _context._nodeStore!));
    }

    internal static object?[] FindElementsById(object? arg, XdmDocument doc, XdmInMemoryStore store)
    {
        var idValues = new HashSet<string>(StringComparer.Ordinal);
        CollectIdValues(arg, idValues);
        if (idValues.Count == 0)
            return Array.Empty<object?>();

        var results = new List<object?>();
        WalkForIds(doc, idValues, results, store);
        return results.ToArray();
    }

    internal static void CollectIdValues(object? arg, HashSet<string> ids)
    {
        if (arg == null)
            return;
        if (arg is string s)
        {
            foreach (var part in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                ids.Add(part);
        }
        else if (arg is object?[] arr)
        {
            foreach (var item in arr)
                CollectIdValues(item, ids);
        }
        else if (arg is IEnumerable<object?> seq)
        {
            foreach (var item in seq)
                CollectIdValues(item, ids);
        }
        else if (arg is XdmNode node)
        {
            foreach (var part in node.StringValue.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                ids.Add(part);
        }
        else
        {
            ids.Add(arg.ToString()!);
        }
    }

    private static void WalkForIds(XdmNode node, HashSet<string> ids, List<object?> results,
        XdmInMemoryStore store)
    {
        if (node is XdmElement elem)
        {
            foreach (var attr in store.GetAttributes(elem))
            {
                if (attr.IsId && ids.Contains(attr.Value))
                {
                    results.Add(elem);
                    break; // Only add the element once
                }
            }
            foreach (var childId in elem.Children)
            {
                var child = store.GetNode(childId);
                if (child != null)
                    WalkForIds(child, ids, results, store);
            }
        }
        else if (node is XdmDocument doc)
        {
            foreach (var childId in doc.Children)
            {
                var child = store.GetNode(childId);
                if (child != null)
                    WalkForIds(child, ids, results, store);
            }
        }
    }
}
