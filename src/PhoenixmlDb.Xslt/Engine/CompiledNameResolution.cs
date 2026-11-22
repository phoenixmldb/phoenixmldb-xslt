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
    /// <summary>
    /// Per type: whether it is a syntax-tree type, and the fields the walk reads. A field is left
    /// out when no value of its declared type can lead to a name: a value type, a string or Uri
    /// (the walk stops at those, and reading one through <see cref="FieldInfo.GetValue"/> boxed it),
    /// or a sealed type that holds none (<see cref="SourceLocation"/>, on every node, was most of
    /// the objects the walk visited).
    /// </summary>
    private sealed record TypeWalk(bool IsTree, FieldInfo[] Fields);

    private static readonly ConcurrentDictionary<Type, TypeWalk> s_types = new();
    private static readonly ConcurrentDictionary<Type, bool> s_canReachName = new();

    [ThreadStatic]
    private static HashSet<Type>? t_computing;

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
        if (o is null || o is string || o is Uri)
            return;
        var t = o.GetType();
        if (t.IsValueType || depth > 2000 || !seen.Add(o))
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
        var info = s_types.GetOrAdd(t, Describe);
        if (o is IEnumerable seq && !info.IsTree)
        {
            foreach (var item in seq)
                Walk(item, stylesheet, seen, depth + 1);
            return;
        }
        if (!info.IsTree)
            return;
        foreach (var f in info.Fields)
            Walk(f.GetValue(o), stylesheet, seen, depth + 1);
    }

    private static TypeWalk Describe(Type t)
    {
        if (!IsTreeType(t))
            return new TypeWalk(false, []);
        return new TypeWalk(true, ReferenceFields(t).Where(f => CanReachName(f.FieldType)).ToArray());
    }

    private static IEnumerable<FieldInfo> ReferenceFields(Type t)
    {
        for (var type = t; type is not null && type != typeof(object); type = type.BaseType)
        {
            foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!f.FieldType.IsValueType && f.FieldType != typeof(string) && f.FieldType != typeof(Uri))
                    yield return f;
            }
        }
    }

    /// <summary>
    /// Whether a value of a field declared as <paramref name="declared"/> can lead the walk to a
    /// name test or a step. True for any type that is not sealed (a subclass may be the value), so
    /// a false is exact: a sealed type that is neither a collection nor a tree type stops the walk,
    /// and a sealed tree type or collection reaches only what its fields or items can.
    /// </summary>
    private static bool CanReachName(Type declared)
    {
        if (s_canReachName.TryGetValue(declared, out var known))
            return known;
        t_computing ??= new HashSet<Type>();
        if (!t_computing.Add(declared))
            return true; // a cycle through this type: assume it can, which only keeps a field in
        try
        {
            var result = ComputeCanReachName(declared);
            s_canReachName.TryAdd(declared, result);
            return result;
        }
        finally
        {
            t_computing.Remove(declared);
        }
    }

    private static bool ComputeCanReachName(Type t)
    {
        if (t.IsValueType || t == typeof(string) || t == typeof(Uri))
            return false;
        if (t.IsArray)
            return CanReachName(t.GetElementType()!);
        if (!t.IsSealed)
            return true;
        if (typeof(NameTest).IsAssignableFrom(t) || typeof(StepExpression).IsAssignableFrom(t))
            return true;
        if (IsTreeType(t))
            return ReferenceFields(t).Any(f => CanReachName(f.FieldType));
        if (typeof(IEnumerable).IsAssignableFrom(t))
            return !t.IsGenericType || t.GetGenericArguments().Any(CanReachName);
        return false;
    }
}
