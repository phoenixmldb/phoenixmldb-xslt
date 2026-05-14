using System.Net.Http;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Fetches stylesheet modules and similar text resources over HTTP/HTTPS.
/// </summary>
/// <remarks>
/// <para>
/// XSLT 3.0 doesn't restrict the scheme of <c>xsl:include</c>/<c>xsl:import</c> hrefs —
/// any URI is fair game. The parser resolves the href against the entry stylesheet's
/// base URI; when that resolves to <c>http://</c> or <c>https://</c>, this helper
/// performs the fetch.
/// </para>
/// <para>
/// The parser is synchronous, so this exposes a sync-blocking <see cref="GetStringSync"/>
/// that runs the async <see cref="HttpClient"/> call to completion. Stylesheet imports
/// are infrequent (compile-time only) and modest in size, so blocking the calling thread
/// here is acceptable.
/// </para>
/// <para>
/// A single static <see cref="HttpClient"/> with a 30-second timeout is reused across
/// the process to amortize handshake/connection cost. The timeout is conservative —
/// a stylesheet over 30 s of network is almost certainly a misconfiguration that the
/// user will want to surface as an error rather than have hang the build.
/// </para>
/// <para>
/// Sandboxing remains the caller's responsibility: <c>ResourcePolicy</c> is consulted
/// before this helper is invoked, so a configured policy can deny HTTP imports
/// entirely or restrict them to specific hosts/path prefixes.
/// </para>
/// </remarks>
internal static class HttpResourceLoader
{
    private static readonly HttpClient _client = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixmlDb.Xslt");
        return c;
    }

    /// <summary>
    /// Fetches the resource at <paramref name="uri"/> as a string. Blocks the calling
    /// thread until the request completes.
    /// </summary>
    /// <exception cref="System.IO.IOException">Thrown when the request fails (network
    /// error, non-success status, timeout). Wrapping in <c>IOException</c> lets the
    /// existing parser <c>catch (IOException)</c> path produce the standard
    /// <c>XTSE0165</c> error.</exception>
    public static string GetStringSync(Uri uri)
    {
        if (OperatingSystem.IsBrowser())
            throw PreloadedResources.CreateBrowserCacheMissException(uri, "imported stylesheet");
        try
        {
            using var response = _client.GetAsync(uri).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
        catch (HttpRequestException ex)
        {
            throw new System.IO.IOException($"HTTP request for '{uri}' failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new System.IO.IOException($"HTTP request for '{uri}' timed out: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Async sibling of <see cref="GetStringSync"/>. Used by the async pre-walker
    /// in <c>LoadStylesheetAsync</c> to populate <see cref="PreloadedResources"/>
    /// before invoking the synchronous parser, so WASM hosts never hit the
    /// sync-over-async wait path that throws "Cannot wait on monitors".
    /// </summary>
    public static async Task<string> GetStringAsync(Uri uri, CancellationToken ct = default)
    {
        try
        {
            using var response = await _client.GetAsync(uri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new System.IO.IOException($"HTTP request for '{uri}' failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new System.IO.IOException($"HTTP request for '{uri}' timed out: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Streaming HTTP fetcher for documents read by <c>fn:doc</c> / <c>document()</c>.
/// </summary>
/// <remarks>
/// Separate from <see cref="HttpResourceLoader"/> because document fetching wants a
/// streaming interface (caller pipes the response into <see cref="System.IO.StreamReader"/>),
/// while stylesheet-import fetching wants an eager string for the parser. Both share the
/// same connection-pooled <see cref="HttpClient"/> behind the scenes via the JIT-loaded
/// static instance below.
/// </remarks>
internal static class HttpDocumentLoader
{
    private static readonly HttpClient _client = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PhoenixmlDb.Xslt");
        return c;
    }

    /// <summary>
    /// Opens a streaming read of <paramref name="uri"/>. The caller disposes the stream.
    /// </summary>
    public static Stream OpenRead(Uri uri)
    {
        if (OperatingSystem.IsBrowser())
            throw PreloadedResources.CreateBrowserCacheMissException(uri, "document");
        var response = _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Async sibling of <see cref="OpenRead"/>. Used by the LoadStylesheetAsync
    /// pre-walker to populate <see cref="PreloadedResources"/> with documents
    /// referenced via static <c>doc('uri-literal')</c> / <c>document('uri-literal')</c>
    /// calls — same pattern as the xsl:import preloader, fetched as a string
    /// (the runtime's fn:doc cache stores text and re-parses on read).
    /// </summary>
    public static async Task<string> GetStringAsync(Uri uri, CancellationToken ct = default)
    {
        try
        {
            using var response = await _client.GetAsync(uri, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new System.IO.IOException($"HTTP request for '{uri}' failed: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new System.IO.IOException($"HTTP request for '{uri}' timed out: {ex.Message}", ex);
        }
    }
}
