using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// The in-scope namespaces of a stylesheet element, worked out once for each element. Each
/// instruction and XPath expression walked its ancestors and their attributes to find them: a
/// generated Schematron validator of 2.3 MB made 72,000 such walks a compile, each with its
/// iterators and a dictionary (#401). An element that declares no namespace has its parent's set.
/// </summary>
internal static class NamespaceScopes
{
    private static readonly ConditionalWeakTable<XElement, IReadOnlyDictionary<string, string>> s_all = new();
    private static readonly ConditionalWeakTable<XElement, IReadOnlyDictionary<string, string>> s_prefixed = new();
    private static readonly ConditionalWeakTable<XElement, object> s_declaresBelowRoot = new();
    private static readonly object s_yes = new();
    private static readonly object s_no = new();

    /// <summary>
    /// Every namespace binding in scope on <paramref name="element"/>, the default namespace as
    /// "": the nearest declaration of a prefix first, then the outer ones, as walking outward from
    /// the element finds them.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> InScope(XElement element)
        => Bindings(element, s_all, prefixedOnly: false);

    /// <summary>The prefixed namespace bindings in scope on <paramref name="element"/>, in the same order.</summary>
    internal static IReadOnlyDictionary<string, string> Prefixed(XElement element)
        => Bindings(element, s_prefixed, prefixedOnly: true);

    /// <summary>
    /// Whether <paramref name="element"/> or an ancestor below the module's root element declares
    /// a prefixed namespace.
    /// </summary>
    internal static bool DeclaresBelowRoot(XElement element)
    {
        if (s_declaresBelowRoot.TryGetValue(element, out var known))
            return ReferenceEquals(known, s_yes);
        var parent = element.Parent;
        var result = parent is not null
            && (DeclaresPrefixed(element) || DeclaresBelowRoot(parent));
        s_declaresBelowRoot.AddOrUpdate(element, result ? s_yes : s_no);
        return result;
    }

    private static IReadOnlyDictionary<string, string> Bindings(XElement element,
        ConditionalWeakTable<XElement, IReadOnlyDictionary<string, string>> memo, bool prefixedOnly)
    {
        if (memo.TryGetValue(element, out var known))
            return known;
        var outer = element.Parent is { } parent ? Bindings(parent, memo, prefixedOnly) : NamespaceSets.Empty;
        IReadOnlyDictionary<string, string> result;
        if (!(prefixedOnly ? DeclaresPrefixed(element) : DeclaresAny(element)))
            result = outer;
        else
        {
            var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var a = element.FirstAttribute; a is not null; a = a.NextAttribute)
            {
                if (!a.IsNamespaceDeclaration || (prefixedOnly && a.Name.Namespace != XNamespace.Xmlns))
                    continue;
                bindings.TryAdd(a.Name.Namespace == XNamespace.Xmlns ? a.Name.LocalName : "", a.Value);
            }
            foreach (var (prefix, uri) in outer)
                bindings.TryAdd(prefix, uri);
            result = NamespaceSets.Shared(element, bindings);
        }
        memo.AddOrUpdate(element, result);
        return result;
    }

    private static bool DeclaresAny(XElement element)
    {
        for (var a = element.FirstAttribute; a is not null; a = a.NextAttribute)
            if (a.IsNamespaceDeclaration)
                return true;
        return false;
    }

    private static bool DeclaresPrefixed(XElement element)
    {
        for (var a = element.FirstAttribute; a is not null; a = a.NextAttribute)
            if (a.IsNamespaceDeclaration && a.Name.Namespace == XNamespace.Xmlns)
                return true;
        return false;
    }
}
