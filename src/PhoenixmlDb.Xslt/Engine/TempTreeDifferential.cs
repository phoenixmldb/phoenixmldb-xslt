using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Differential-testing harness for the <see cref="TreeConstructor"/> migration: a
/// structural (node-model, not serialized-text) comparison of two XDM subtrees, gated
/// behind an environment-variable toggle so it is completely inert in production.
/// </summary>
/// <remarks>
/// Later tasks migrate temp-tree construction call sites from the legacy
/// serialize-then-reparse path to <see cref="TreeConstructor"/>. When <see cref="Enabled"/>
/// is on, those call sites run both builders and assert <see cref="TreeEqual"/> returns
/// <c>null</c>, catching behavioral divergence before the legacy path is deleted.
/// <see cref="IsExpectedDivergence"/> is the escape hatch for known-and-accepted
/// differences (e.g. cases where the legacy path is provably wrong and the new one is
/// right) — it starts empty and gains entries only as those cases are triaged.
/// </remarks>
internal static class TempTreeDifferential
{
    private static readonly bool _enabled =
        Environment.GetEnvironmentVariable("PXDB_TEMPTREE_DIFF") == "1";

    /// <summary>
    /// Whether the dual-run differential comparison is active. Reads the
    /// <c>PXDB_TEMPTREE_DIFF</c> environment variable once at process start; <c>false</c>
    /// by default so this harness never affects production behavior or performance.
    /// </summary>
    internal static bool Enabled => _enabled;

    /// <summary>
    /// The allowlist of case keys where a known-and-accepted divergence exists between the
    /// legacy serialize-reparse path and <see cref="TreeConstructor"/> (i.e. the legacy path
    /// is known to be wrong). Empty until later tasks triage specific cases.
    /// </summary>
    private static readonly HashSet<string> ExpectedDivergences = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns <c>true</c> if <paramref name="key"/> identifies a case with a known,
    /// accepted divergence between the two construction paths (so a differential failure
    /// for it should be suppressed rather than reported).
    /// </summary>
    internal static bool IsExpectedDivergence(string key) => ExpectedDivergences.Contains(key);

    /// <summary>
    /// Structurally compares the subtrees rooted at <paramref name="a"/> and
    /// <paramref name="b"/> in <paramref name="store"/>, node-model to node-model (never by
    /// serializing to text). Returns <c>null</c> if the trees are equal, or a
    /// human-readable description of the first difference found.
    /// </summary>
    /// <remarks>
    /// Compares, recursively: node kind; for elements, the expanded QName (namespace URI +
    /// local name), the full in-scope-namespace set as a (prefix, namespace) pair set
    /// (including undeclarations — i.e. <see cref="XdmElement.NamespaceDeclarations"/>, not
    /// just the resolved in-scope map, since that is what a text round-trip can lose),
    /// attributes as an order-independent expanded-name→value map, and children in document
    /// order; for text/comment/PI nodes, the string content (and PI target); and for every
    /// node kind, the base URI.
    /// </remarks>
    internal static string? TreeEqual(NodeId a, NodeId b, XdmInMemoryStore store)
        => Compare(a, b, store, "/");

    private static string? Compare(NodeId aId, NodeId bId, XdmInMemoryStore store, string path)
    {
        var a = store.GetNode(aId);
        var b = store.GetNode(bId);

        if (a is null || b is null)
        {
            return a is null && b is null
                ? null
                : $"{path}: node missing (a={Describe(a)}, b={Describe(b)})";
        }

        if (a.NodeKind != b.NodeKind)
            return $"{path}: node kind differs (a={a.NodeKind}, b={b.NodeKind})";

        return a.NodeKind switch
        {
            XdmNodeKind.Element => CompareElements((XdmElement)a, (XdmElement)b, store, path),
            XdmNodeKind.Text => CompareValue("text", ((XdmText)a).Value, ((XdmText)b).Value, path)
                ?? CompareBaseUri(a, b, path),
            XdmNodeKind.Comment => CompareValue("comment", ((XdmComment)a).Value, ((XdmComment)b).Value, path)
                ?? CompareBaseUri(a, b, path),
            XdmNodeKind.ProcessingInstruction => ComparePI((XdmProcessingInstruction)a, (XdmProcessingInstruction)b, path)
                ?? CompareBaseUri(a, b, path),
            XdmNodeKind.Attribute => CompareAttributeNode((XdmAttribute)a, (XdmAttribute)b, path),
            XdmNodeKind.Document => CompareDocuments((XdmDocument)a, (XdmDocument)b, store, path),
            _ => CompareValue("string-value", a.StringValue, b.StringValue, path),
        };
    }

    private static string? CompareElements(XdmElement a, XdmElement b, XdmInMemoryStore store, string path)
    {
        if (a.Namespace != b.Namespace || !string.Equals(a.LocalName, b.LocalName, StringComparison.Ordinal))
            return $"{path}: expanded name differs (a={ExpandedName(a, store)}, b={ExpandedName(b, store)})";

        var nsDiff = CompareNamespaceSets(a, b, path);
        if (nsDiff is not null)
            return nsDiff;

        var attrDiff = CompareAttributes(a, b, store, path);
        if (attrDiff is not null)
            return attrDiff;

        var baseUriDiff = CompareBaseUri(a, b, path);
        if (baseUriDiff is not null)
            return baseUriDiff;

        if (a.Children.Count != b.Children.Count)
            return $"{path}: child count differs (a={a.Children.Count}, b={b.Children.Count})";

        for (var i = 0; i < a.Children.Count; i++)
        {
            var childDiff = Compare(a.Children[i], b.Children[i], store, $"{path}{a.LocalName}[{i}]/");
            if (childDiff is not null)
                return childDiff;
        }

        return null;
    }

    private static string? CompareDocuments(XdmDocument a, XdmDocument b, XdmInMemoryStore store, string path)
    {
        if (a.Children.Count != b.Children.Count)
            return $"{path}: document child count differs (a={a.Children.Count}, b={b.Children.Count})";

        for (var i = 0; i < a.Children.Count; i++)
        {
            var childDiff = Compare(a.Children[i], b.Children[i], store, $"{path}[{i}]/");
            if (childDiff is not null)
                return childDiff;
        }

        return null;
    }

    private static string? CompareNamespaceSets(XdmElement a, XdmElement b, string path)
    {
        var aSet = new HashSet<(string Prefix, NamespaceId Ns)>(
            a.NamespaceDeclarations.Select(nb => (nb.Prefix, nb.Namespace)));
        var bSet = new HashSet<(string Prefix, NamespaceId Ns)>(
            b.NamespaceDeclarations.Select(nb => (nb.Prefix, nb.Namespace)));

        if (aSet.SetEquals(bSet))
            return null;

        var onlyInA = aSet.Except(bSet).ToList();
        var onlyInB = bSet.Except(aSet).ToList();
        return $"{path}: namespace declarations differ (only in a: [{string.Join(", ", onlyInA)}], only in b: [{string.Join(", ", onlyInB)}])";
    }

    private static string? CompareAttributes(XdmElement a, XdmElement b, XdmInMemoryStore store, string path)
    {
        if (a.Attributes.Count != b.Attributes.Count)
            return $"{path}: attribute count differs (a={a.Attributes.Count}, b={b.Attributes.Count})";

        var aMap = new Dictionary<(NamespaceId, string), string>();
        foreach (var attrId in a.Attributes)
        {
            if (store.GetNode(attrId) is XdmAttribute attr)
                aMap[(attr.Namespace, attr.LocalName)] = attr.Value;
        }

        var bMap = new Dictionary<(NamespaceId, string), string>();
        foreach (var attrId in b.Attributes)
        {
            if (store.GetNode(attrId) is XdmAttribute attr)
                bMap[(attr.Namespace, attr.LocalName)] = attr.Value;
        }

        foreach (var (key, aValue) in aMap)
        {
            if (!bMap.TryGetValue(key, out var bValue))
                return $"{path}: attribute {key.Item2} present only in a";
            if (!string.Equals(aValue, bValue, StringComparison.Ordinal))
                return $"{path}: attribute {key.Item2} value differs (a={aValue}, b={bValue})";
        }

        foreach (var key in bMap.Keys)
        {
            if (!aMap.ContainsKey(key))
                return $"{path}: attribute {key.Item2} present only in b";
        }

        return null;
    }

    private static string? CompareAttributeNode(XdmAttribute a, XdmAttribute b, string path)
    {
        if (a.Namespace != b.Namespace || !string.Equals(a.LocalName, b.LocalName, StringComparison.Ordinal))
            return $"{path}: attribute name differs (a={a.LocalName}, b={b.LocalName})";
        return CompareValue("attribute value", a.Value, b.Value, path);
    }

    private static string? ComparePI(XdmProcessingInstruction a, XdmProcessingInstruction b, string path)
    {
        if (!string.Equals(a.Target, b.Target, StringComparison.Ordinal))
            return $"{path}: PI target differs (a={a.Target}, b={b.Target})";
        return CompareValue("PI data", a.Value, b.Value, path);
    }

    private static string? CompareValue(string label, string a, string b, string path)
        => string.Equals(a, b, StringComparison.Ordinal)
            ? null
            : $"{path}: {label} differs (a={a}, b={b})";

    private static string? CompareBaseUri(XdmNode a, XdmNode b, string path)
        => string.Equals(a.BaseUri, b.BaseUri, StringComparison.Ordinal)
            ? null
            : $"{path}: base URI differs (a={a.BaseUri ?? "(null)"}, b={b.BaseUri ?? "(null)"})";

    private static string ExpandedName(XdmElement e, XdmInMemoryStore store)
        => $"{{{store.GetNamespaceUri(e.Namespace) ?? string.Empty}}}{e.LocalName}";

    private static string Describe(XdmNode? node)
        => node is null ? "(null)" : node.NodeKind.ToString();
}
