using PhoenixmlDb.Core;

namespace PhoenixmlDb.Xslt;

/// <summary>
/// Reads the one vendor option this engine understands: a Saxon configuration document
/// declaring where XSLT packages live.
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
/// Reading the format is not Saxon emulation: the engine already accepts a package catalog, and
/// this is the only notation anyone writes one in. Unrecognised vendor options stay ignored.
/// </remarks>
internal static class SaxonVendorOptions
{
    private const string ConfigNs = "http://saxon.sf.net/ns/configuration";

    /// <summary>
    /// Builds a package catalog from a Saxon configuration in <paramref name="options"/>, or
    /// <c>null</c> when there is none to read.
    /// </summary>
    /// <param name="options">The fn:transform options map.</param>
    /// <param name="serialize">Serializes a node to XML; the two callers differ in how.</param>
    /// <param name="fallbackBaseUri">
    /// Used to resolve <c>@sourceLocation</c> when the configuration node carries no base URI.
    /// Locations are written relative to the CONFIG file, not to the stylesheet using the package.
    /// </param>
    internal static Dictionary<string, List<(string? Version, string FilePath)>>? BuildPackageCatalog(
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

        System.Xml.Linq.XNamespace ns = ConfigNs;
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
