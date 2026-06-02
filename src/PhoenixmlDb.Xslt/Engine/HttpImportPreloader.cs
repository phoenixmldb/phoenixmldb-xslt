using System.Xml.Linq;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Walks an XSLT module's <c>xsl:import</c> / <c>xsl:include</c> graph asynchronously,
/// fetching every HTTP/HTTPS-resolved href and storing it in <see cref="PreloadedResources"/>.
/// Called from <c>LoadStylesheetAsync</c> BEFORE the synchronous parser runs, so the
/// parser's import resolver always finds HTTP modules in the cache and never invokes
/// the sync-blocking <c>HttpResourceLoader.GetStringSync</c> path that breaks on WASM
/// (single-threaded runtime can't wait on monitors).
/// </summary>
/// <remarks>
/// Best-effort: any fetch failure is swallowed so the synchronous parser surfaces the
/// canonical XTSE0165 error with full context (the parser already handles cache miss
/// by attempting the sync HTTP fetch on non-browser runtimes, and producing a clear
/// "preload required" exception on browser runtimes).
/// </remarks>
internal static class HttpImportPreloader
{
    private static readonly XNamespace XsltNs = "http://www.w3.org/1999/XSL/Transform";

    public static async Task PreloadHttpImportsAsync(
        string rootStylesheetXml,
        Uri? rootBaseUri,
        PreloadedResources resources,
        CancellationToken ct = default)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        await WalkAsync(rootStylesheetXml, rootBaseUri, resources, visited, ct).ConfigureAwait(false);
    }

    // Matches doc('http(s)://...') or document('http(s)://...') — single or double quoted —
    // in any XPath attribute value. Permissive (also catches text content) but the URL
    // pattern is strict enough to avoid false positives on regular prose. Fragment
    // identifiers stripped so doc('http://x/y#z') and doc('http://x/y') share a cache entry.
    private static readonly System.Text.RegularExpressions.Regex DocCallPattern =
        new(@"\b(?:doc|document)\s*\(\s*['""](https?://[^'""#\s]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Matches fn:transform(...) with a map containing 'stylesheet-location':'http(s)://...'.
    // DocBook xslTNG chains its pipeline modules via this pattern (the $v:standard-transforms
    // map): each module is loaded dynamically rather than via xsl:import. Without picking
    // those URLs up here, the host (Blazor WASM) can't pre-fetch the modules and the runtime
    // falls through to sync HTTP → "Cannot wait on monitors" — Martin Honnen 2026-06-01.
    // Quote style and the prefix on transform() are independent; key/value gap is permissive.
    // Note: only catches literal-URL forms — computed URIs (variable refs, concat) still
    // require manual PreloadedResources population.
    private static readonly System.Text.RegularExpressions.Regex TransformStylesheetLocationPattern =
        new(@"\b(?:fn:)?transform\s*\([^)]*?['""]stylesheet-location['""]\s*:\s*['""](https?://[^'""#\s]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Singleline);

    /// <summary>
    /// Walks the stylesheet XML and returns every HTTP(S) URL the preloader would consider
    /// fetching: <c>xsl:import</c>/<c>xsl:include</c> hrefs, <c>doc()</c>/<c>document()</c>
    /// literal URIs, and <c>fn:transform(map{'stylesheet-location':'...'})</c> URIs.
    /// Used by tests; the runtime preloader uses <see cref="PreloadHttpImportsAsync"/> which
    /// also fetches.
    /// </summary>
    internal static HashSet<Uri> DiscoverHttpUrls(string rootStylesheetXml, Uri? rootBaseUri)
    {
        var result = new HashSet<Uri>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        DiscoverRecursive(rootStylesheetXml, rootBaseUri, result, visited);
        return result;
    }

    private static void DiscoverRecursive(string xml, Uri? baseUri, HashSet<Uri> result, HashSet<string> visited)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml, LoadOptions.None); }
        catch (System.Xml.XmlException) { return; }

        var hrefs = doc.Descendants()
            .Where(e => e.Name.Namespace == XsltNs
                        && (e.Name.LocalName == "import" || e.Name.LocalName == "include"))
            .Select(e => e.Attribute("href")?.Value)
            .Where(h => !string.IsNullOrEmpty(h))
            .Cast<string>();

        foreach (var href in hrefs)
        {
            if (!Uri.TryCreate(baseUri, href, out var resolved)) continue;
            if (resolved.Scheme is not ("http" or "https")) continue;
            if (!visited.Add(resolved.AbsoluteUri)) continue;
            result.Add(resolved);
        }

        foreach (System.Text.RegularExpressions.Match m in DocCallPattern.Matches(xml))
        {
            if (!Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out var u)) continue;
            if (u.Scheme is not ("http" or "https")) continue;
            if (visited.Add(u.AbsoluteUri)) result.Add(u);
        }

        foreach (System.Text.RegularExpressions.Match m in TransformStylesheetLocationPattern.Matches(xml))
        {
            if (!Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out var u)) continue;
            if (u.Scheme is not ("http" or "https")) continue;
            if (visited.Add(u.AbsoluteUri)) result.Add(u);
        }
    }

    private static async Task WalkAsync(
        string xml,
        Uri? baseUri,
        PreloadedResources resources,
        HashSet<string> visited,
        CancellationToken ct)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml, LoadOptions.None); }
        catch (System.Xml.XmlException) { return; } // Malformed: let the sync parser surface the error.

        var hrefs = doc.Descendants()
            .Where(e => e.Name.Namespace == XsltNs
                        && (e.Name.LocalName == "import" || e.Name.LocalName == "include"))
            .Select(e => e.Attribute("href")?.Value)
            .Where(h => !string.IsNullOrEmpty(h))
            .Cast<string>()
            .ToList();

        foreach (var href in hrefs)
        {
            ct.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(baseUri, href, out var resolved)) continue;
            if (resolved.Scheme is not ("http" or "https")) continue;
            var key = resolved.AbsoluteUri;
            if (!visited.Add(key)) continue;
            if (resources.TryGet(resolved, out _)) continue;

            string content;
            try { content = await HttpResourceLoader.GetStringAsync(resolved, ct).ConfigureAwait(false); }
            catch (System.IO.IOException) { continue; } // Best effort — parser will report the canonical error.

            resources.Add(resolved, content);
            // Recurse: the imported module may itself import more modules.
            await WalkAsync(content, resolved, resources, visited, ct).ConfigureAwait(false);
        }

        // doc('http://...') / document('http://...') — runtime fn:doc HTTP path.
        // Same WASM concern: a sync HttpClient call from inside a query throws
        // "Cannot wait on monitors". Pre-fetch literal-URI references so they're
        // already in the cache when XPath evaluates the call. Computed URIs
        // (variable refs, concat, etc.) still require the host to populate
        // PreloadedResources manually; we only catch static literals here.
        foreach (System.Text.RegularExpressions.Match m in DocCallPattern.Matches(xml))
        {
            ct.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out var docUri)) continue;
            if (!visited.Add(docUri.AbsoluteUri)) continue;
            if (resources.TryGet(docUri, out _)) continue;

            try
            {
                var docContent = await HttpDocumentLoader.GetStringAsync(docUri, ct).ConfigureAwait(false);
                resources.Add(docUri, docContent);
            }
            catch (System.IO.IOException) { /* runtime will surface the FODC0002 error */ }
        }

        // fn:transform(map{'stylesheet-location':'http://...'}) — DocBook xslTNG dispatches
        // its pipeline modules via this pattern rather than xsl:import. WASM can't perform
        // sync HTTP, so pre-fetch literal-URL forms here. The fetched module is itself an
        // XSLT stylesheet — recurse so its own imports / nested transform() calls are also
        // preloaded. Martin Honnen 2026-06-01.
        foreach (System.Text.RegularExpressions.Match m in TransformStylesheetLocationPattern.Matches(xml))
        {
            ct.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out var xslUri)) continue;
            if (!visited.Add(xslUri.AbsoluteUri)) continue;
            if (resources.TryGet(xslUri, out _)) continue;

            string xslContent;
            try { xslContent = await HttpResourceLoader.GetStringAsync(xslUri, ct).ConfigureAwait(false); }
            catch (System.IO.IOException) { continue; }

            resources.Add(xslUri, xslContent);
            await WalkAsync(xslContent, xslUri, resources, visited, ct).ConfigureAwait(false);
        }
    }
}
