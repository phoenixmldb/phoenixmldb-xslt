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
/// XSLT-aware fn:deep-equal($parameter1, $parameter2) — performs proper structural comparison of XDM nodes.
/// </summary>
internal sealed class XsltDeepEqualFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;

    public XsltDeepEqualFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "deep-equal");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => PhoenixmlDb.XQuery.Ast.XdmSequenceType.Boolean;
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "parameter1"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.ZeroOrMoreItems },
        new() { Name = new QName(NamespaceId.None, "parameter2"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.ZeroOrMoreItems }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        // Resolve the effective collation: use the XSLT transformer's default collation
        var collationUri = _context.DefaultCollation;
        var comparison = collationUri switch
        {
            null or "" or "http://www.w3.org/2005/xpath-functions/collation/codepoint" => StringComparison.Ordinal,
            "http://www.w3.org/2005/xpath-functions/collation/html-ascii-case-insensitive" => StringComparison.OrdinalIgnoreCase,
            _ when collationUri.StartsWith("http://www.w3.org/2013/collation/UCA", StringComparison.Ordinal) => StringComparison.InvariantCultureIgnoreCase,
            _ => StringComparison.Ordinal
        };

        var seqA = ToList(arguments[0]);
        var seqB = ToList(arguments[1]);

        if (seqA.Count != seqB.Count)
            return ValueTask.FromResult<object?>(false);

        for (int i = 0; i < seqA.Count; i++)
        {
            if (!DeepEqualItems(seqA[i], seqB[i], comparison))
                return ValueTask.FromResult<object?>(false);
        }
        return ValueTask.FromResult<object?>(true);
    }

    private static List<object?> ToList(object? arg) => arg switch
    {
        null => [],
        // An XDM array and an XDM map are single ITEMS, not sequences. The engine represents
        // an array as List<object?>, which IS IEnumerable<object?>, so the sequence arm below
        // silently flattened [1] into its members - and deep-equal([1], 1) answered TRUE.
        // That is worse than the map case: a wrong answer rather than an error.
        List<object?> => [arg],
        IDictionary<object, object?> => [arg],
        IEnumerable<object?> seq => seq.ToList(),
        _ => [arg]
    };

    private bool DeepEqualItems(object? a, object? b, StringComparison comparison)
    {
        if (a is null && b is null)
            return true;
        if (a is null || b is null)
            return false;

        // Both are nodes — structural comparison
        if (a is XdmNode nodeA && b is XdmNode nodeB)
            return DeepEqualNodes(nodeA, nodeB, comparison);
        if (a is XdmNode || b is XdmNode)
            return false;

        // XDM arrays: equal size and members deep-equal. Recursing through THIS method rather
        // than handing the whole array to TypeCastHelper keeps the XSLT-aware node comparison
        // above in play for nodes stored inside an array.
        if (a is List<object?> arrA && b is List<object?> arrB)
        {
            if (arrA.Count != arrB.Count)
                return false;
            for (var i = 0; i < arrA.Count; i++)
                if (!DeepEqualItems(arrA[i], arrB[i], comparison))
                    return false;
            return true;
        }
        if (a is List<object?> || b is List<object?>)
            return false;

        // XDM maps: same keys, and the value for each key deep-equal. fn:deep-equal is defined
        // for maps (XPath 3.1 F&O 14.2.2) and does NOT atomize them, so falling through to the
        // atomizing comparison below raised FOTY0013 "Atomization is not defined for maps" on
        // every map comparison — including XSpec's x:deep-equal, which is how this was found.
        // The XQuery twin of this function has handled maps since 2026-04-01; this one, which
        // shadows it whenever a stylesheet is running, never did.
        if (a is IDictionary<object, object?> mapA && b is IDictionary<object, object?> mapB)
        {
            if (mapA.Count != mapB.Count)
                return false;
            foreach (var kv in mapA)
            {
                if (!mapB.TryGetValue(kv.Key, out var bVal))
                    return false;
                if (!DeepEqualItems(kv.Value, bVal, comparison))
                    return false;
            }
            return true;
        }
        if (a is IDictionary<object, object?> || b is IDictionary<object, object?>)
            return false;

        // Both are atomic — value comparison
        return PhoenixmlDb.XQuery.Execution.TypeCastHelper.DeepEquals(
            PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(a),
            PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(b),
            comparison);
    }

    private bool DeepEqualNodes(XdmNode a, XdmNode b, StringComparison comparison)
    {
        if (a.NodeKind != b.NodeKind)
            return false;
        var store = _context._nodeStore;
        if (store == null)
            return false;

        return (a, b) switch
        {
            (XdmElement elemA, XdmElement elemB) => DeepEqualElements(elemA, elemB, store, comparison),
            (XdmText, XdmText) => string.Equals(a.StringValue, b.StringValue, comparison),
            (XdmComment, XdmComment) => string.Equals(a.StringValue, b.StringValue, StringComparison.Ordinal),
            (XdmDocument docA, XdmDocument docB) => DeepEqualChildren(docA, docB, store, comparison),
            _ when a.IsProcessingInstruction && b.IsProcessingInstruction =>
                a.NodeName?.LocalName == b.NodeName?.LocalName &&
                string.Equals(a.StringValue, b.StringValue, StringComparison.Ordinal),
            _ when a.IsAttribute && b.IsAttribute =>
                a.NodeName?.LocalName == b.NodeName?.LocalName &&
                a.NodeName?.Namespace == b.NodeName?.Namespace &&
                string.Equals(a.StringValue, b.StringValue, comparison),
            (XdmNamespace nsA, XdmNamespace nsB) =>
                string.Equals(nsA.Prefix, nsB.Prefix, StringComparison.Ordinal) &&
                string.Equals(nsA.Uri, nsB.Uri, comparison),
            _ => false
        };
    }

    private bool DeepEqualElements(XdmElement a, XdmElement b, XdmInMemoryStore store, StringComparison comparison)
    {
        // Compare element names (names always use ordinal comparison)
        if (a.Namespace != b.Namespace || !string.Equals(a.LocalName, b.LocalName, StringComparison.Ordinal))
            return false;

        // Compare attributes (unordered)
        var attrsA = store.GetAttributes(a).ToList();
        var attrsB = store.GetAttributes(b).ToList();
        if (attrsA.Count != attrsB.Count)
            return false;

        foreach (var attrA in attrsA)
        {
            var match = attrsB.FirstOrDefault(ab =>
                ab.Namespace == attrA.Namespace &&
                string.Equals(ab.LocalName, attrA.LocalName, StringComparison.Ordinal));
            if (match == null || !string.Equals(attrA.Value, match.Value, comparison))
                return false;
        }

        // Compare children (ordered)
        return DeepEqualChildren(a, b, store, comparison);
    }

    private bool DeepEqualChildren(XdmNode a, XdmNode b, XdmInMemoryStore store, StringComparison comparison)
    {
        var childrenA = store.GetChildren(a).ToList();
        var childrenB = store.GetChildren(b).ToList();
        if (childrenA.Count != childrenB.Count)
            return false;

        for (int i = 0; i < childrenA.Count; i++)
        {
            if (!DeepEqualNodes(childrenA[i], childrenB[i], comparison))
                return false;
        }
        return true;
    }
}
