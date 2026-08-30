using PhoenixmlDb.Core;

namespace PhoenixmlDb.Xslt;

/// <summary>
/// The one place that answers "what namespace URI does this <see cref="QName"/> actually carry",
/// and matches two QNames by URI rather than by interned id.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="QName"/> equality compares <c>Namespace</c>, an interned <c>NamespaceId</c>, not the
/// URI string:
/// </para>
/// <code>
/// public bool Equals(QName other) => Namespace == other.Namespace &amp;&amp; LocalName == other.LocalName;
/// </code>
/// <para>
/// That is correct and fast for QNames the parser interned from one stylesheet, because equal
/// URIs get equal ids. It is wrong for a QName built at runtime by <c>fn:QName()</c>, which mints
/// a hash-based id that never equals the parser's id for the same URI. Any dictionary keyed by
/// QName then misses, even though the two names denote the same thing.
/// </para>
/// <para>
/// Which property holds the URI depends on how the QName was made: <c>fn:QName(uri, local)</c>
/// populates <c>RuntimeNamespace</c>, so <c>ResolvedNamespace</c> is the one to read and
/// <c>ExpandedNamespace</c> comes back empty; a parsed QName is the other way round. Reading only
/// one silently matched nothing.
/// </para>
/// <para>
/// This has now cost three separate bugs — a vendor-options package that looked absent, an
/// initial-template lookup, and <c>fn:transform</c> failing to find a namespaced entry point
/// across sixteen XSpec suites. It lives here so there is no fourth.
/// </para>
/// </remarks>
internal static class QNameNamespaces
{
    /// <summary>The namespace URI a QName carries, or the empty string for no namespace.</summary>
    /// <remarks>
    /// Three sources, because three kinds of QName reach here. A runtime <c>fn:QName()</c> fills
    /// <c>ResolvedNamespace</c>; a QName carrying its expanded form fills
    /// <c>ExpandedNamespace</c>; and a QName the parser built carries NEITHER string - it holds
    /// only the interned <c>NamespaceId</c>, so the URI has to be looked back up in the registry.
    /// Missing that last case is what made the first version of this helper still fail to match:
    /// both sides reported an empty URI and compared equal to nothing.
    /// </remarks>
    public static string UriOf(QName q, Func<QName, string?>? resolve = null)
    {
        if (!string.IsNullOrEmpty(q.ResolvedNamespace)) return q.ResolvedNamespace;
        if (!string.IsNullOrEmpty(q.ExpandedNamespace)) return q.ExpandedNamespace;
        // NamespaceRegistry only knows the WELL-KNOWN namespaces - a fixed table. An id the
        // parser interned for an arbitrary stylesheet namespace is not in it, so a caller that
        // has the node store must pass its resolver or this returns empty for exactly the
        // namespaces the caller cares about.
        return resolve?.Invoke(q)
            ?? NamespaceRegistry.GetUri(q.Namespace)
            ?? string.Empty;
    }

    /// <summary>
    /// True when two QNames denote the same expanded name, comparing namespace URIs rather than
    /// interned ids. Use this to rescue a lookup that failed on id equality, not to replace it:
    /// the id comparison is the fast path and is right for names from a single parse.
    /// </summary>
    public static bool SameExpandedName(QName a, QName b, Func<QName, string?>? resolve = null)
        => string.Equals(a.LocalName, b.LocalName, StringComparison.Ordinal)
        && string.Equals(UriOf(a, resolve), UriOf(b, resolve), StringComparison.Ordinal);
}
