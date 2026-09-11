using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xdm.Serialization;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
// XPath 4.0 ordered map: insertion-order iteration as a structural guarantee.
// xslt keeps its existing default key-equality (pass EqualityComparer<object>.Default
// at each construction site) — this change is about iteration order only.
using OrderedXdmMap = PhoenixmlDb.XQuery.Execution.OrderedXdmMap;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// IDocumentResolver for XSLT — loads documents via file URIs, converts to XDM,
/// and caches them so repeated doc() calls for the same URI return the same document.
/// </summary>
internal sealed class XsltDocumentResolver : PhoenixmlDb.XQuery.IDocumentResolver
{
    private readonly XsltStylesheet _stylesheet;
    private readonly XdmInMemoryStore? _nodeStore;
    private readonly Dictionary<string, XdmDocument?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<string>>? _collections;

    /// <summary>
    /// Pre-fetched contents for URIs the host already loaded asynchronously. Consulted
    /// before <see cref="HttpDocumentLoader.OpenRead"/>; required on Blazor WebAssembly.
    /// </summary>
    internal PreloadedResources? PreloadedResources { get; set; }

    /// <summary>Set of absolute URIs that have been read via doc()/document() during this transformation.</summary>
    internal IReadOnlyCollection<string> ReadDocumentUris => _cache.Keys;

    public XsltDocumentResolver(XsltStylesheet stylesheet, XdmInMemoryStore? nodeStore)
    {
        _stylesheet = stylesheet;
        _nodeStore = nodeStore;
    }

    /// <summary>The node store used for ID lookups in fragment identifiers.</summary>
    internal XdmInMemoryStore? NodeStore => _nodeStore;

    public void SetCollections(Dictionary<string, List<string>> collections)
    {
        _collections = collections;
    }

    /// <summary>
    /// Pre-populates the document cache with the source document so that
    /// doc(document-uri(.)) returns the same node (identity preservation).
    /// </summary>
    public void SeedSourceDocument(XdmDocument doc)
    {
        if (doc.DocumentUri != null)
            _cache[doc.DocumentUri] = doc;
    }

    public XdmDocument? ResolveDocument(string uri)
    {
        // Per XSLT spec, document('') and doc('') return the stylesheet document
        if (uri.Length == 0 && _stylesheet.BaseUri != null)
            uri = _stylesheet.BaseUri.AbsoluteUri;

        if (_cache.TryGetValue(uri, out var cached))
            return cached;

        var doc = LoadDocument(uri);
        _cache[uri] = doc;
        return doc;
    }

    public bool IsDocumentAvailable(string uri)
    {
        // Per XSLT spec, document('') and doc('') return the stylesheet document
        if (uri.Length == 0 && _stylesheet.BaseUri != null)
            uri = _stylesheet.BaseUri.AbsoluteUri;

        if (_cache.TryGetValue(uri, out var cached))
            return cached != null;

        try
        {
            var resolvedUri = ResolveUri(uri);
            if (resolvedUri.IsFile)
                return System.IO.File.Exists(resolvedUri.LocalPath);
            if (resolvedUri.Scheme == Uri.UriSchemeHttp || resolvedUri.Scheme == Uri.UriSchemeHttps)
            {
                // For HTTP we have to fetch (or HEAD) to know — reuse ResolveDocument so
                // a successful fetch is cached and the next doc() call is free.
                return ResolveDocument(uri) != null;
            }
            return false;
        }
        catch (UriFormatException)
        {
            return false;
        }
        catch (System.IO.IOException)
        {
            return false;
        }
    }

    public IEnumerable<XdmNode> ResolveCollection(string? uri)
    {
        if (_collections == null || !_collections.TryGetValue(uri ?? "", out var paths))
            return uri == null ? [] : ResolveDirectoryCollection(uri);

        var nodes = new List<XdmNode>();
        foreach (var path in paths)
        {
            // Handle fragment identifiers (e.g., "doc15.xml#frag2")
            var hashIdx = path.IndexOf('#', StringComparison.Ordinal);
            var filePath = hashIdx >= 0 ? path[..hashIdx] : path;
            var fragment = hashIdx >= 0 ? path[(hashIdx + 1)..] : null;

            var doc = LoadDocument(filePath);
            if (doc == null)
                continue;

            if (fragment != null && _nodeStore != null)
            {
                // Fragment identifier selects element by xml:id
                var matches = XsltIdFunction.FindElementsById(fragment, doc, _nodeStore);
                foreach (var match in matches)
                {
                    if (match is XdmNode node)
                        nodes.Add(node);
                }
            }
            else
            {
                nodes.Add(doc);
            }
        }
        return nodes;
    }

    /// <summary>
    /// A collection URI naming a directory is the XML files in it, in path order. The query
    /// takes <c>select=</c> (a file-name glob using <c>*</c> and <c>?</c>) and <c>recurse=yes</c>,
    /// separated by <c>;</c> or <c>&amp;</c> — the Saxon form (<c>dir?select=*.xml</c>) the W3C
    /// suite uses (merge-097). Files that do not parse as XML are left out. Each document is
    /// resolved as doc() would, so it is the same node doc() returns for its URI.
    /// </summary>
    private List<XdmNode> ResolveDirectoryCollection(string uri)
    {
        var queryIdx = uri.IndexOf('?', StringComparison.Ordinal);
        var location = queryIdx >= 0 ? uri[..queryIdx] : uri;
        string? select = null;
        var recurse = false;
        if (queryIdx >= 0)
        {
            foreach (var part in uri[(queryIdx + 1)..].Split([';', '&'], StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=', StringComparison.Ordinal);
                if (eq < 0)
                    continue;
                var value = Uri.UnescapeDataString(part[(eq + 1)..]);
                switch (part[..eq])
                {
                    case "select": select = value; break;
                    case "recurse": recurse = value is "yes" or "true" or "1"; break;
                }
            }
        }

        string directory;
        try
        {
            var resolved = ResolveUri(location.Length == 0 ? "." : location);
            if (!resolved.IsAbsoluteUri || !resolved.IsFile || !System.IO.Directory.Exists(resolved.LocalPath))
                return [];
            directory = resolved.LocalPath;
        }
        catch (UriFormatException)
        {
            return [];
        }

        var pattern = select == null
            ? null
            : new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(select).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var files = System.IO.Directory.EnumerateFiles(directory, "*",
                recurse ? System.IO.SearchOption.AllDirectories : System.IO.SearchOption.TopDirectoryOnly)
            .Where(f => pattern == null || pattern.IsMatch(System.IO.Path.GetFileName(f)))
            .Order(StringComparer.Ordinal);

        var nodes = new List<XdmNode>();
        foreach (var file in files)
        {
            if (ResolveDocument(new Uri(file).AbsoluteUri) is { } doc)
                nodes.Add(doc);
        }
        return nodes;
    }

    private Uri ResolveUri(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var absUri))
            return absUri;

        if (_stylesheet.BaseUri != null)
            return new Uri(_stylesheet.BaseUri, uri);

        return new Uri(uri, UriKind.RelativeOrAbsolute);
    }

    private XdmDocument? LoadDocument(string uri)
    {
        try
        {
            var resolvedUri = ResolveUri(uri);
            if (!resolvedUri.IsAbsoluteUri)
                return null;

            string xmlContent;
            if (resolvedUri.Scheme == Uri.UriSchemeHttp || resolvedUri.Scheme == Uri.UriSchemeHttps)
            {
                // Fetch over HTTP. Same opt-in-by-scheme rule as xsl:import — if a stylesheet
                // running over HTTPS calls fn:doc with a relative URI that resolves to HTTPS,
                // we honor it. ResourcePolicy still gates this when configured (the policy-
                // enforcing wrapper sits in front of this resolver in the call chain).
                //
                // Preload-cache check: callers running on Blazor WebAssembly (or any runtime
                // that disallows monitor waits) must supply pre-fetched content here, since
                // the synchronous HttpClient call below blocks the calling thread.
                if (PreloadedResources is { } preloaded && preloaded.TryGet(resolvedUri, out var preloadedContent))
                {
                    xmlContent = preloadedContent;
                }
                else if (OperatingSystem.IsBrowser())
                {
                    throw new XsltException(
                        $"FODC0002: Cannot fetch '{resolvedUri}' on Blazor WebAssembly: " +
                        "synchronous HTTP I/O is not supported. Pre-fetch the document " +
                        "asynchronously and pass it through PreloadedResources to LoadStylesheetAsync.");
                }
                else
                {
                    using var stream = HttpDocumentLoader.OpenRead(resolvedUri);
                    using var reader = new System.IO.StreamReader(stream);
                    xmlContent = reader.ReadToEnd();
                }
            }
            else if (resolvedUri.IsFile)
            {
                if (!System.IO.File.Exists(resolvedUri.LocalPath))
                    return null;
                xmlContent = System.IO.File.ReadAllText(resolvedUri.LocalPath);
            }
            else
            {
                return null;
            }

            var xmlDoc = new XmlDocument { PreserveWhitespace = true };
            xmlDoc.LoadXml(xmlContent);

            var nodeStore = _nodeStore ?? new XdmInMemoryStore();
            var xdmDoc = XsltTransformEngine.ConvertToXdm(xmlDoc, nodeStore, resolvedUri.AbsoluteUri);

            // Apply xsl:strip-space declarations
            if (_stylesheet.StripSpace.Count > 0)
            {
                foreach (var nt in _stylesheet.StripSpace)
                    nt.ResolveNamespace(nodeStore.InternNamespace);
                foreach (var nt in _stylesheet.PreserveSpace)
                    nt.ResolveNamespace(nodeStore.InternNamespace);

                XsltTransformEngine.StripWhitespaceNodes(xdmDoc, _stylesheet.StripSpace, _stylesheet.PreserveSpace, nodeStore);
            }

            return xdmDoc;
        }
        catch (UriFormatException)
        {
            return null;
        }
        catch (System.IO.IOException)
        {
            return null;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
