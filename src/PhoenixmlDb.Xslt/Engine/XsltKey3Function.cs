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
/// XSLT key() with 3 args (name, value, top).
/// </summary>
internal sealed class XsltKey3Function : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltKey3Function(DefaultXsltExecutionContext context) => _context = context;
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "key");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => new()
    {
        ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Node,
        Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrMore
    };
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "name"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.String },
        new() { Name = new QName(NamespaceId.None, "value"), Type = new PhoenixmlDb.XQuery.Ast.XdmSequenceType { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.AnyAtomicType, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrMore } },
        new() { Name = new QName(NamespaceId.None, "top"), Type = new PhoenixmlDb.XQuery.Ast.XdmSequenceType { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Node, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ExactlyOne } }
    ];

    public override async ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var keyName = arguments[0]?.ToString() ?? "";
        var topNode = arguments[2];

        // XTDE1270: The root of the tree containing the 3rd argument must be a document node
        if (topNode is XdmNode topXdmNode)
        {
            var docRoot = _context.FindDocumentForNode(topXdmNode);
            if (docRoot == null)
                throw new XsltException("XTDE1270: The root of the tree containing the node supplied as the third argument to key() is not a document node");
        }
        else if (topNode is not System.Xml.Linq.XNode)
        {
            throw new XsltException("XTDE1270: The third argument to key() must be a node");
        }

        // Resolve key name using shared resolver (handles namespace-prefixed names)
        var keyFunc = new XsltKeyFunction(_context);
        var keyDef = keyFunc.ResolveKeyDefinition(keyName);
        if (keyDef == null)
            return Array.Empty<object>();

        if (_context._nodeStore == null)
            return Array.Empty<object>();

        // Collect individual lookup values from the second argument (typed for eq comparison)
        var lookupTypedValues = XsltKeyFunction.GetIndividualTypedValues(arguments[1]);

        // Resolve collation comparer for string matching
        StringComparer? collationComparer = keyDef.Collation != null
            ? DefaultXsltExecutionContext.GetCollationComparer(keyDef.Collation) : null;

        // Collect all nodes in the tree rooted at $top (per XSLT spec §20.2)
        // This includes $top itself plus all its descendants
        var descendants = new List<XdmNode>();
        if (topNode is XdmNode topForCollection)
            descendants.Add(topForCollection);
        CollectDescendants(topNode, descendants);

        var isComposite = keyDef.Composite;
        var results = new List<object>();
        var seen = new HashSet<NodeId>();
        foreach (var node in descendants)
        {
            foreach (var def in keyDef.AllDefinitions)
            {
                bool defMatched;
                using (var mc = _context.AcquireMatchContext())
                    defMatched = def.Match.Matches(node, mc.Value);
                if (defMatched)
                {
                    var useValues = await EvaluateUseExpression(def, node).ConfigureAwait(false);
                    if (isComposite)
                    {
                        // Composite key: all use values form a single tuple, compared componentwise
                        if (useValues.Count == lookupTypedValues.Count)
                        {
                            bool allMatch = true;
                            for (int i = 0; i < useValues.Count; i++)
                            {
                                if (!XsltKeyFunction.KeyValueEquals(useValues[i], lookupTypedValues[i], collationComparer))
                                {
                                    allMatch = false;
                                    break;
                                }
                            }
                            if (allMatch && seen.Add(node.Id))
                                results.Add(node);
                        }
                    }
                    else
                    {
                        // Non-composite: each use value is a separate key
                        foreach (var uv in useValues)
                        {
                            foreach (var lv in lookupTypedValues)
                            {
                                if (XsltKeyFunction.KeyValueEquals(uv, lv, collationComparer) && seen.Add(node.Id))
                                {
                                    results.Add(node);
                                    break;
                                }
                            }
                            if (seen.Contains(node.Id))
                                break;
                        }
                    }
                }
                if (seen.Contains(node.Id))
                    break;
            }
        }

        return results.Count > 0 ? results.ToArray() : Array.Empty<object>();
    }

    private async ValueTask<List<object>> EvaluateUseExpression(PhoenixmlDb.Xslt.Ast.XsltKey keyDef, XdmNode node)
    {
        var values = new List<object>();
        _context.PushContextItem(node, 1, 1);
        try
        {
            object? result = null;
            if (keyDef.Use != null)
                result = await _context.EvaluateAsync(keyDef.Use).ConfigureAwait(false);
            else if (keyDef.UseContent != null)
                result = await _context.EvaluateSequenceConstructorAsync(keyDef.UseContent).ConfigureAwait(false);
            else
                return values;

            if (result is null)
            {
                // null result → no key value
            }
            else if (result is object[] arr)
            {
                foreach (var item in arr)
                {
                    if (item != null)
                    {
                        var a = PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(item);
                        values.Add(a ?? item);
                    }
                }
            }
            else if (result is IEnumerable<object?> seq && result is not string && result is not XdmNode)
            {
                foreach (var el in seq)
                {
                    if (el != null)
                    {
                        var a = PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(el);
                        values.Add(a ?? el);
                    }
                }
            }
            else
            {
                var atomized = PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(result);
                values.Add(atomized ?? result);
            }
        }
        finally
        {
            _context.PopContextItem();
        }
        return values;
    }

    private void CollectDescendants(object? node, List<XdmNode> descendants)
    {
        if (node == null || _context._nodeStore == null)
            return;

        if (node is XdmDocument doc)
        {
            foreach (var childId in doc.Children)
            {
                var child = _context._nodeStore.GetNode(childId);
                if (child != null)
                { descendants.Add(child); CollectDescendants(child, descendants); }
            }
        }
        else if (node is XdmElement elem)
        {
            foreach (var attrId in elem.Attributes)
            {
                var attr = _context._nodeStore.GetNode(attrId);
                if (attr != null)
                    descendants.Add(attr);
            }
            foreach (var childId in elem.Children)
            {
                var child = _context._nodeStore.GetNode(childId);
                if (child != null)
                { descendants.Add(child); CollectDescendants(child, descendants); }
            }
        }
    }
}
