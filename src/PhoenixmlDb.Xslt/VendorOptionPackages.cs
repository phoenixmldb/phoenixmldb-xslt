using PhoenixmlDb.Core;

namespace PhoenixmlDb.Xslt;

/// <summary>
/// Extracts XSLT package declarations from the <c>vendor-options</c> map of
/// <c>fn:transform</c>.
/// </summary>
/// <remarks>
/// XSpec declares package locations the only way Saxon offers — a configuration listing
/// <c>xsltPackages/package/@name</c> and <c>@sourceLocation</c> — and passes it to fn:transform
/// under <c>vendor-options</c>. Both fn:transform implementations ignored it, so
/// <c>xsl:use-package name="http://example.org/complex-arithmetic.xsl"</c> could not resolve and
/// raised XTDE3052 "Package not found" on five XSpec suites.
///
/// This lives in one place because there are TWO fn:transform implementations —
/// <c>XsltTransformProvider</c> for XQuery callers and <c>XsltTransformFunction</c> for XSLT
/// ones — and fixing one of a pair while missing its twin has been the most common defect shape
/// in this engine. The first attempt at this fix patched the XQuery-side provider only, and the
/// failing suites go through the XSLT side.
///
/// <para>
/// <c>vendor-options</c> is the spec's own extension point (XPath and XQuery F&amp;O,
/// <c>fn:transform</c>): a map of implementation-defined options keyed by QName. Nothing in it
/// is standardised, so a reader has to recognise some concrete vocabulary. The one in use is
/// Saxon's configuration element, because that is what stylesheets in the wild — XSpec among
/// them — actually write. Recognising a vocabulary is not implementing that processor: the
/// engine's own package catalog does the work, and options in any other vocabulary are ignored
/// rather than rejected.
/// </para>
/// </remarks>
internal static class VendorOptionPackages
{
    /// <summary>
    /// The one package-declaration vocabulary recognised today. Add others beside it — the
    /// method's contract is "find package declarations", not "read this namespace".
    /// </summary>
    private const string SaxonConfigurationNs = "http://saxon.sf.net/ns/configuration";

    /// <summary>
    /// The namespace of the vendor-options map KEY, which is NOT the namespace of the
    /// configuration document element.
    /// </summary>
    /// <remarks>
    /// Saxon has two namespaces in play and they are easy to conflate. The configuration
    /// document is in <c>http://saxon.sf.net/ns/configuration</c>; the extension namespace used
    /// for QNames in stylesheets - <c>saxon:threads</c>, and the vendor-options key - is the
    /// bare <c>http://saxon.sf.net/</c>. XSpec writes
    /// <c>QName('http://saxon.sf.net/', 'configuration')</c> as the key and puts the config
    /// document in the value.
    ///
    /// Matching the key against the CONFIGURATION namespace found nothing, so the whole option
    /// was skipped and five XSpec package suites still raised XTDE3052 after the rest of this
    /// class was written. The document-side parsing below was right all along; only the lookup
    /// that reaches it was wrong.
    /// </remarks>
    private const string SaxonExtensionNs = "http://saxon.sf.net/";

    /// <summary>
    /// This engine's own vendor-option namespace. Package declarations here need no other
    /// implementation's file format:
    /// <code>
    /// 'vendor-options': map {
    ///     QName('http://phoenixml.dev/ns/vendor-options', 'packages'): map {
    ///         'http://example.org/lib.xsl' : 'lib/library.xsl',
    ///         'http://example.org/other'   : map { 'location': 'o.xsl', 'version': '2.0' }
    ///     }
    /// }
    /// </code>
    /// A map is the authoring form because vendor-options values are XDM items and a map can be
    /// written inline in XPath; requiring a configuration DOCUMENT would force every author
    /// through somebody else's schema to declare their own packages.
    /// </summary>
    internal const string PhoenixmlVendorNs = "http://phoenixml.dev/ns/vendor-options";

    /// <summary>
    /// Builds a package catalog from any recognised package declarations in
    /// <paramref name="options"/>, or <c>null</c> when there are none.
    /// </summary>
    /// <param name="options">The fn:transform options map.</param>
    /// <param name="serialize">Serializes a node to XML; the two callers differ in how.</param>
    /// <param name="fallbackBaseUri">
    /// Used to resolve <c>@sourceLocation</c> when the configuration node carries no base URI.
    /// Locations are written relative to the CONFIG file, not to the stylesheet using the package.
    /// </param>
    internal static Dictionary<string, List<(string? Version, string FilePath)>>? BuildCatalog(
        IDictionary<object, object?> options,
        Func<Xdm.Nodes.XdmNode, string> serialize,
        Uri? fallbackBaseUri)
    {
        if (!options.TryGetValue("vendor-options", out var raw)
            || raw is not IDictionary<object, object?> vendorOptions)
            return null;

        Dictionary<string, List<(string? Version, string FilePath)>>? catalog = null;

        // Native vocabulary first, so an author who is not using Saxon has a supported route.
        foreach (var (key, value) in vendorOptions)
        {
            if (key is not QName nk
                || !string.Equals(NamespaceOf(nk), PhoenixmlVendorNs, StringComparison.Ordinal)
                || !string.Equals(nk.LocalName, "packages", StringComparison.Ordinal))
                continue;
            AddNativePackages(value, fallbackBaseUri, ref catalog);
        }

        // Then Saxon's configuration element, matched on its FULL name. Matching bare
        // local-name "configuration" would have claimed any other vendor's option that
        // happened to share it, which is the opposite of leaving other vocabularies alone.
        object? configValue = null;
        foreach (var (key, value) in vendorOptions)
        {
            // Both Saxon-owned namespaces are accepted: the extension namespace is what
            // Saxon and XSpec actually write, the configuration namespace is taken too because
            // a producer that reached for the document's own namespace is unambiguously still
            // naming Saxon's option. Neither can collide with another vendor.
            if (key is QName qn
                && string.Equals(qn.LocalName, "configuration", StringComparison.Ordinal)
                && NamespaceOf(qn) is var kns
                && (string.Equals(kns, SaxonExtensionNs, StringComparison.Ordinal)
                    || string.Equals(kns, SaxonConfigurationNs, StringComparison.Ordinal)))
            { configValue = value; break; }
        }
        if (configValue is object?[] arr && arr.Length == 1)
            configValue = arr[0];
        if (configValue is not Xdm.Nodes.XdmNode configNode)
            return catalog;

        System.Xml.Linq.XDocument doc;
        try
        {
            doc = System.Xml.Linq.XDocument.Parse(serialize(configNode));
        }
        catch (System.Xml.XmlException)
        {
            return catalog;   // unreadable; keep anything the native vocabulary supplied
        }

        var configBase = configNode.BaseUri is { Length: > 0 } b
                         && Uri.TryCreate(b, UriKind.Absolute, out var cb)
            ? cb
            : fallbackBaseUri;

        System.Xml.Linq.XNamespace ns = SaxonConfigurationNs;
        foreach (var pkg in doc.Descendants(ns + "package"))
        {
            var name = pkg.Attribute("name")?.Value;
            var location = pkg.Attribute("sourceLocation")?.Value;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(location))
                continue;
            var resolved = configBase != null && Uri.TryCreate(configBase, location, out var abs)
                ? abs.LocalPath
                : location;
            catalog ??= new Dictionary<string, List<(string? Version, string FilePath)>>(StringComparer.Ordinal);
            if (!catalog.TryGetValue(name, out var list))
                catalog[name] = list = [];
            list.Add((pkg.Attribute("version")?.Value, resolved));
        }
        return catalog;
    }

    /// <summary>
    /// The namespace URI a QName key actually carries.
    /// </summary>
    /// <remarks>
    /// <c>fn:QName(uri, local)</c> populates <c>RuntimeNamespace</c>, so <c>ResolvedNamespace</c>
    /// is the property that holds the URI; <c>ExpandedNamespace</c> comes back EMPTY. Comparing
    /// the wrong one silently matched nothing, so a correctly written vendor option looked
    /// absent and the package stayed unresolved. XsltTransformProvider carries a note about the
    /// same trap costing an initial-template lookup — worth reading before adding a third
    /// QName comparison anywhere.
    /// </remarks>
    private static string NamespaceOf(QName q)
        => !string.IsNullOrEmpty(q.ResolvedNamespace) ? q.ResolvedNamespace
         : q.ExpandedNamespace ?? string.Empty;

    /// <summary>
    /// Reads the native <c>packages</c> map: package name -> location string, or -> a map with
    /// <c>location</c> and optional <c>version</c>.
    /// </summary>
    private static void AddNativePackages(object? value, Uri? baseUri,
        ref Dictionary<string, List<(string? Version, string FilePath)>>? catalog)
    {
        if (value is object?[] one && one.Length == 1) value = one[0];
        if (value is not IDictionary<object, object?> map) return;

        foreach (var (nameKey, spec) in map)
        {
            var name = nameKey as string ?? nameKey?.ToString();
            if (string.IsNullOrEmpty(name)) continue;

            string? location = null, version = null;
            switch (spec)
            {
                case string sLoc:
                    location = sLoc;
                    break;
                case IDictionary<object, object?> detail:
                    detail.TryGetValue("location", out var l);
                    detail.TryGetValue("version", out var v);
                    location = l as string ?? l?.ToString();
                    version = v as string ?? v?.ToString();
                    break;
            }
            if (string.IsNullOrEmpty(location)) continue;

            var resolved = baseUri != null && Uri.TryCreate(baseUri, location, out var abs)
                ? abs.LocalPath
                : location;
            catalog ??= new Dictionary<string, List<(string? Version, string FilePath)>>(StringComparer.Ordinal);
            if (!catalog.TryGetValue(name, out var list))
                catalog[name] = list = [];
            list.Add((version, resolved));
        }
    }

}
