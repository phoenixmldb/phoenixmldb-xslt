namespace PhoenixmlDb.Xslt;

/// <summary>
/// Pre-fetched contents for URIs that the engine would otherwise need to fetch over
/// the network synchronously (<c>xsl:import</c>, <c>xsl:include</c>, <c>fn:doc()</c>).
/// </summary>
/// <remarks>
/// <para>
/// On runtimes that support thread blocking (server, desktop, CLI), the engine's
/// HTTP loaders run an async <see cref="System.Net.Http.HttpClient"/> call to
/// completion synchronously. That's fine for those runtimes — but Blazor
/// WebAssembly is single-threaded and cannot park a thread on a monitor wait, so
/// the same code throws <c>"Cannot wait on monitors on this runtime"</c>.
/// </para>
/// <para>
/// To run on WASM, fetch the resources asynchronously in your host code (via
/// <see cref="System.Net.Http.HttpClient"/> with <c>await</c>, or via JS interop)
/// and pass the contents through a <see cref="PreloadedResources"/> instance to
/// <see cref="XsltTransformer.LoadStylesheetAsync"/>. The engine consults this
/// cache before falling back to its synchronous HTTP loaders.
/// </para>
/// <para>
/// On WASM, a cache miss for an absolute http(s) URI raises a clear engine
/// exception that names the missing URI rather than the obscure runtime
/// <c>Cannot wait on monitors</c> error.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Blazor WebAssembly: pre-fetch every URI the stylesheet will need before loading.
/// var http = new HttpClient();
/// var transpileXsl = await http.GetStringAsync("https://example.org/transpile.xsl");
/// var priceSch    = await http.GetStringAsync("https://example.org/price.sch");
///
/// var preloaded = new PreloadedResources();
/// preloaded.Add(new Uri("https://example.org/transpile.xsl"), transpileXsl);
/// preloaded.Add(new Uri("https://example.org/price.sch"),    priceSch);
///
/// var t = new XsltTransformer();
/// await t.LoadStylesheetAsync(stylesheetXml, baseUri, preloadedResources: preloaded);
/// var result = await t.TransformAsync(sourceXml);
/// </code>
/// </example>
public sealed class PreloadedResources
{
    private readonly Dictionary<string, string> _content = new(StringComparer.Ordinal);

    /// <summary>
    /// Number of URIs registered.
    /// </summary>
    public int Count => _content.Count;

    /// <summary>
    /// Adds <paramref name="content"/> for <paramref name="uri"/>. Existing entries
    /// are overwritten.
    /// </summary>
    public void Add(Uri uri, string content)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(content);
        _content[uri.AbsoluteUri] = content;
    }

    /// <summary>
    /// Tries to retrieve the pre-loaded content for <paramref name="uri"/>.
    /// </summary>
    public bool TryGet(Uri uri, out string content)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return _content.TryGetValue(uri.AbsoluteUri, out content!);
    }

    /// <summary>
    /// Clears all entries.
    /// </summary>
    public void Clear() => _content.Clear();

    /// <summary>
    /// True if a pre-loaded entry exists for <paramref name="uri"/>.
    /// </summary>
    public bool Contains(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return _content.ContainsKey(uri.AbsoluteUri);
    }

    /// <summary>
    /// Helper used by the engine's HTTP loader fallbacks to surface a clear,
    /// actionable error when running on a runtime that cannot block the calling
    /// thread (Blazor WebAssembly).
    /// </summary>
    internal static System.IO.IOException CreateBrowserCacheMissException(Uri uri, string what)
    {
        return new System.IO.IOException(
            $"Cannot fetch '{uri}' on this runtime: synchronous HTTP I/O is not " +
            $"supported here (typical on Blazor WebAssembly). Pre-fetch the " +
            $"{what} asynchronously in your host code and supply the content via " +
            $"PreloadedResources passed to LoadStylesheetAsync.");
    }
}
