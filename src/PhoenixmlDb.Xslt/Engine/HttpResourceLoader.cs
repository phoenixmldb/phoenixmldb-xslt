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
/// The parser is synchronous, so this exposes a sync-blocking <see cref="GetStringSync(Uri)"/>
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
    // Fetches go through the engine's shared client, which follows redirects by hand so a
    // resource policy can re-authorise every hop (an allowed origin must not be able to send
    // the fetch to another host or port). With no policy, redirects are followed as before.

    /// <summary>Fetches a stylesheet module synchronously (the sync import path).</summary>
    public static string GetStringSync(Uri uri) => GetStringSync(uri, policy: null);

    /// <summary>
    /// As <see cref="GetStringSync(Uri)"/>, re-authorising every redirect target for import
    /// access under <paramref name="policy"/>. The caller authorises <paramref name="uri"/>.
    /// </summary>
    public static string GetStringSync(Uri uri, PhoenixmlDb.XQuery.Security.ResourcePolicy? policy)
    {
        if (OperatingSystem.IsBrowser())
            throw PreloadedResources.CreateBrowserCacheMissException(uri, "imported stylesheet");
        return GetStringAsync(uri, policy).GetAwaiter().GetResult();
    }

    public static Task<string> GetStringAsync(Uri uri, CancellationToken ct = default) => GetStringAsync(uri, null, ct);

    /// <summary>
    /// Fetches a stylesheet module, re-authorising every redirect target for import access
    /// under <paramref name="policy"/>. The caller authorises <paramref name="uri"/>.
    /// </summary>
    public static Task<string> GetStringAsync(Uri uri, PhoenixmlDb.XQuery.Security.ResourcePolicy? policy, CancellationToken ct = default)
        => HttpFetch.GetStringAsync(uri, policy, PhoenixmlDb.XQuery.Security.ResourceAccessKind.ImportStylesheet, ct);
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
    public static Stream OpenRead(Uri uri) => OpenRead(uri, policy: null);

    /// <summary>
    /// Opens a document, re-authorising every redirect target for read access under
    /// <paramref name="policy"/>. The caller authorises <paramref name="uri"/>.
    /// </summary>
    public static Stream OpenRead(Uri uri, PhoenixmlDb.XQuery.Security.ResourcePolicy? policy)
    {
        if (OperatingSystem.IsBrowser())
            throw PreloadedResources.CreateBrowserCacheMissException(uri, "document");
        return PhoenixmlDb.XQuery.HttpDocumentClient.OpenRead(uri, HttpFetch.RedirectCheck(policy, PhoenixmlDb.XQuery.Security.ResourceAccessKind.ReadDocument));
    }

    public static Task<string> GetStringAsync(Uri uri, CancellationToken ct = default) => GetStringAsync(uri, null, ct);

    /// <summary>Fetches a document as text, re-authorising every redirect for read access.</summary>
    public static Task<string> GetStringAsync(Uri uri, PhoenixmlDb.XQuery.Security.ResourcePolicy? policy, CancellationToken ct = default)
        => HttpFetch.GetStringAsync(uri, policy, PhoenixmlDb.XQuery.Security.ResourceAccessKind.ReadDocument, ct);
}

internal static class HttpFetch
{
    internal static Func<Uri, bool>? RedirectCheck(PhoenixmlDb.XQuery.Security.ResourcePolicy? policy, PhoenixmlDb.XQuery.Security.ResourceAccessKind access)
        => policy is null ? null : target => policy.IsAllowed(target, access);

    internal static async Task<string> GetStringAsync(Uri uri, PhoenixmlDb.XQuery.Security.ResourcePolicy? policy,
        PhoenixmlDb.XQuery.Security.ResourceAccessKind access, CancellationToken ct)
    {
        try
        {
            return await PhoenixmlDb.XQuery.HttpDocumentClient.GetStringAsync(uri, RedirectCheck(policy, access), ct).ConfigureAwait(false);
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
