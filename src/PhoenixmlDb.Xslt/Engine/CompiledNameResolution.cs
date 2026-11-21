using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Resolves every name test of a compiled stylesheet once, against the process-wide namespace
/// table, so that a transformation never writes to the compiled tree.
/// </summary>
/// <remarks>
/// <para>
/// A name test's namespace id used to be resolved by each transformation against its own node
/// store, whose ids differ from store to store: every transformation wrote its ids into the shared
/// tree, and two transformations of one compiled stylesheet at the same time overwrote each other.
/// Now every name test carries the process-wide id of its URI
/// (<see cref="StylesheetParser.ResolveNamespaceUri"/>), which every xslt node store uses for that
/// URI too (<see cref="XdmInMemoryStore"/>). The run-time resolutions that remain find the value
/// already there and write nothing.
/// </para>
/// <para>
/// The tree is walked by reflection over the AST types (fields, lists, dictionaries), so a name
/// test in any construct — patterns, keys, accumulators, whitespace declarations, XPath steps,
/// KindTest names, sequence types — is reached without a visitor per instruction type. It runs
/// once per compile.
/// </para>
/// </remarks>
internal static class CompiledNameResolution
{
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> s_fields = new();

    internal static void Resolve(XsltStylesheet stylesheet)
    {
        ArgumentNullException.ThrowIfNull(stylesheet);
        Walk(stylesheet, stylesheet, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
    }

    /// <summary>
    /// The namespaces of the names in an XPath step: its name test, and the element or attribute
    /// name of a kind test (<c>self::element(a:x)</c>, <c>document-node(element(a:x))</c>). A prefix
    /// the parser left unresolved takes the stylesheet's binding, an unprefixed element name takes
    /// xpath-default-namespace, then the URI's process-wide id. Shared with the run-time walk, which
    /// then changes nothing for a compiled stylesheet's own expressions; for an expression compiled
    /// at run time (xsl:evaluate) it is the only resolution, and it skipped kind tests, so
    /// <c>self::element(a:x)</c> in an evaluated path matched nothing.
    /// </summary>
    internal static void ResolveStep(StepExpression se, XsltStylesheet stylesheet)
    {
        switch (se.NodeTest)
        {
            case NameTest nt:
                ResolveName(nt, stylesheet, isElementName: se.Axis != Axis.Attribute && se.Axis != Axis.Namespace);
                break;
            case KindTest kt:
                if (kt.Name is { } kindName)
                    ResolveName(kindName, stylesheet, isElementName: kt.Kind == XdmNodeKind.Element);
                if (kt.DocumentElementTest is { } documentElement)
                    ResolveName(documentElement, stylesheet, isElementName: true);
                break;
        }
    }

    private static void ResolveName(NameTest nt, XsltStylesheet stylesheet, bool isElementName)
    {
        if (nt.Prefix != null && string.IsNullOrEmpty(nt.NamespaceUri) && nt.NamespaceUri != "*")
        {
            if (stylesheet.Namespaces.TryGetValue(nt.Prefix, out var uri))
                nt.NamespaceUri = uri;
        }
        else if (nt.Prefix == null && string.IsNullOrEmpty(nt.NamespaceUri) && !nt.IsLocalNameWildcard
                 && isElementName && stylesheet.XpathDefaultNamespace != null && !nt.ResolvedNamespace.HasValue)
        {
            nt.NamespaceUri = stylesheet.XpathDefaultNamespace;
        }
        nt.ResolveNamespace(StylesheetParser.ResolveNamespaceUri);
    }

    private static bool IsTreeType(Type t) =>
        t.Namespace is { } ns && (ns.StartsWith("PhoenixmlDb.Xslt.Ast", StringComparison.Ordinal)
            || ns.StartsWith("PhoenixmlDb.XQuery.Ast", StringComparison.Ordinal));

    private static void Walk(object? o, XsltStylesheet stylesheet, HashSet<object> seen, int depth)
    {
        if (o is null || o is string || o is Uri || o.GetType().IsValueType)
            return;
        var t = o.GetType();
        if (depth > 2000 || !seen.Add(o))
            return;
        switch (o)
        {
            case StepExpression se:
                ResolveStep(se, stylesheet);
                break;
            case NameTest nt:
                nt.ResolveNamespace(StylesheetParser.ResolveNamespaceUri);
                return;
        }
        if (o is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict)
                Walk(e.Value, stylesheet, seen, depth + 1);
            return;
        }
        if (o is IEnumerable seq && !IsTreeType(t))
        {
            foreach (var item in seq)
                Walk(item, stylesheet, seen, depth + 1);
            return;
        }
        if (!IsTreeType(t))
            return;
        foreach (var f in s_fields.GetOrAdd(t, AllFields))
            Walk(f.GetValue(o), stylesheet, seen, depth + 1);
    }

    private static FieldInfo[] AllFields(Type t)
    {
        var fields = new List<FieldInfo>();
        for (var type = t; type is not null && type != typeof(object); type = type.BaseType)
            fields.AddRange(type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        return fields.ToArray();
    }
}
