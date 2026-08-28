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

        object? configValue = null;
        foreach (var (key, value) in vendorOptions)
        {
            if (key is QName qn && qn.LocalName == "configuration")
            { configValue = value; break; }
        }
        if (configValue is object?[] arr && arr.Length == 1)
            configValue = arr[0];
        if (configValue is not Xdm.Nodes.XdmNode configNode)
            return null;

        System.Xml.Linq.XDocument doc;
        try
        {
            doc = System.Xml.Linq.XDocument.Parse(serialize(configNode));
        }
        catch (System.Xml.XmlException)
        {
            return null;   // unreadable; leave the catalog unset rather than guess
        }

        var configBase = configNode.BaseUri is { Length: > 0 } b
                         && Uri.TryCreate(b, UriKind.Absolute, out var cb)
            ? cb
            : fallbackBaseUri;

        System.Xml.Linq.XNamespace ns = SaxonConfigurationNs;
        Dictionary<string, List<(string? Version, string FilePath)>>? catalog = null;
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
}
