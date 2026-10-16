using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// One shared dictionary for each distinct set of namespace bindings in a stylesheet module.
/// Each literal result element, xsl:element, xsl:attribute and XPath expression kept its own
/// copy of the in-scope namespaces, nearly always the same bindings: a generated Schematron
/// validator of 2.3 MB kept 13,697 of them, 8.2 MB of the 22 MB a compile left behind (#401).
/// </summary>
/// <remarks>
/// A set is matched with the order of its bindings and its key comparer, since a literal result
/// element writes its namespace declarations in that order. A shared set is read-only: a write
/// to one would change every instruction that shares it. The pool belongs to the module's
/// document, and goes with it.
/// </remarks>
internal static class NamespaceSets
{
    private static readonly ConditionalWeakTable<XDocument, Dictionary<string, IReadOnlyDictionary<string, string>>> s_pools = new();
    private static readonly ConditionalWeakTable<XElement, Dictionary<string, IReadOnlyDictionary<string, string>>> s_rootPools = new();

    /// <summary>The empty set, shared by every instruction that has no bindings of its own.</summary>
    internal static IReadOnlyDictionary<string, string> Empty { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// The shared, read-only set equal to <paramref name="bindings"/>. A new set is a copy, so the
    /// caller's dictionary does not alias it.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Shared(XElement context, Dictionary<string, string> bindings)
    {
        var pool = context.Document is { } document
            ? s_pools.GetValue(document, static _ => new(StringComparer.Ordinal))
            : s_rootPools.GetValue(context.AncestorsAndSelf().Last(), static _ => new(StringComparer.Ordinal));
        var key = new StringBuilder(bindings.Count * 48);
        key.Append(bindings.Comparer.GetType().FullName).Append('\u0001');
        foreach (var (prefix, uri) in bindings)
            key.Append(prefix).Append('\u0002').Append(uri).Append('\u0001');
        lock (pool)
        {
            var text = key.ToString();
            if (pool.TryGetValue(text, out var shared))
                return shared;
            var copy = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(bindings, bindings.Comparer));
            pool[text] = copy;
            return copy;
        }
    }
}
