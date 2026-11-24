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
/// XSLT document() function — loads external document(s).
/// </summary>
internal sealed class XsltDocumentFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltDocumentFunction(DefaultXsltExecutionContext context) => _context = context;
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "document");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => new()
    {
        ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Node,
        Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrMore
    };
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "uri-sequence"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.ZeroOrMoreItems }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        // The document() function loads external documents by URI.
        // It can accept a sequence of URIs (e.g., document(node-set/@attr)).
        var arg = arguments[0];
        if (arg == null)
            return ValueTask.FromResult<object?>(null);

        // Handle sequence of URIs
        if (arg is object?[] arr)
            return LoadMultipleDocuments(arr, context);
        if (arg is IEnumerable<object?> seq && arg is not string && arg is not Xdm.Nodes.XdmNode)
            return LoadMultipleDocuments(seq, context);

        return LoadSingleDocument(arg, context);
    }

    private static ValueTask<object?> LoadMultipleDocuments(IEnumerable<object?> items, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var results = new List<object?>();
        foreach (var item in items)
        {
            if (item == null)
                continue;
            // Per XSLT §16.1: when the argument is a node, relative URIs are resolved
            // against the base URI of that node, not the static base URI.
            var nodeBaseUri = GetNodeBaseUri(item, context);
            var uri = PhoenixmlDb.XQuery.Functions.ConcatFunction.XQueryStringValue(
                PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(item));
            if (uri == null)
                continue;

            // Extract fragment identifier
            string? fragment = null;
            var hashIdx = uri.IndexOf('#', StringComparison.Ordinal);
            if (hashIdx >= 0)
            {
                fragment = uri[(hashIdx + 1)..];
                uri = uri[..hashIdx];
                RequireShorthandPointer(fragment);
            }

            RequireValidUriReference(uri);
            RequireBaseUriForNodeArgument(item, nodeBaseUri, uri);
            if (context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext qc &&
                qc.DocumentResolver is not null)
            {
                uri = nodeBaseUri != null
                    ? ResolveUriAgainstBase(uri, nodeBaseUri)
                    : ResolveUriAgainstStaticBase(uri, qc);
                var doc = ResolveDocument(qc, uri);
                if (doc != null)
                {
                    if (fragment != null && qc.DocumentResolver is XsltDocumentResolver xsltResolver
                        && xsltResolver.NodeStore is { } nodeStore)
                    {
                        var matches = XsltIdFunction.FindElementsById(fragment, doc, nodeStore);
                        if (matches.Length > 0)
                            results.Add(matches[0]);
                        // Fragment not found → skip (empty result for this URI)
                    }
                    else
                    {
                        results.Add(doc);
                    }
                }
                else
                    throw new XsltException($"FODC0002: Cannot retrieve document at URI '{uri}'");
            }
            else
                throw new XsltException($"FODC0002: Cannot retrieve document at URI '{uri}'");
        }
        return ValueTask.FromResult<object?>(results.ToArray());
    }

    /// <summary>
    /// The document at <paramref name="uri"/>. A refusal of a relative URI, which reaches here
    /// only when there is no base URI to resolve it against, is FODC0002 like any other
    /// document that cannot be retrieved; it reached the caller as ResourceAccessDeniedException.
    /// </summary>
    /// <remarks>
    /// document() raises the errors of fn:doc (XSLT 3.0 §20.1): FODC0002 for a resource that
    /// cannot be retrieved or parsed, FODC0005 only for a URI that is not valid. It raised
    /// FODC0005 for both.
    /// </remarks>
    private static XdmDocument? ResolveDocument(PhoenixmlDb.XQuery.Execution.QueryExecutionContext context, string uri)
    {
        try
        {
            return PhoenixmlDb.XQuery.Security.ResourceGate.ResolveDocument(context.DocumentResolver!, uri, PhoenixmlDb.XQuery.Security.ResourceGate.Caller(context));
        }
        catch (PhoenixmlDb.XQuery.Security.ResourceAccessDeniedException e) when (PhoenixmlDb.XQuery.Functions.LocationResolver.IsRelativeName(uri))
        {
            throw new XsltException($"FODC0002: Cannot retrieve document at URI '{uri}': {e.Message}", e);
        }
    }

    private static ValueTask<object?> LoadSingleDocument(object arg, PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        // Per XSLT §16.1: when the argument is a node, relative URIs are resolved
        // against the base URI of that node, not the static base URI.
        var nodeBaseUri = GetNodeBaseUri(arg, context);
        var uri = PhoenixmlDb.XQuery.Functions.ConcatFunction.XQueryStringValue(
            PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(arg));
        if (uri == null)
            return ValueTask.FromResult<object?>(null);

        // Extract fragment identifier (e.g., "doc.xml#frag" → fragment="frag")
        string? fragment = null;
        var hashIdx = uri.IndexOf('#', StringComparison.Ordinal);
        if (hashIdx >= 0)
        {
            fragment = uri[(hashIdx + 1)..];
            uri = uri[..hashIdx];
            RequireShorthandPointer(fragment);
        }

        RequireValidUriReference(uri);
        RequireBaseUriForNodeArgument(arg, nodeBaseUri, uri);
        if (context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext queryContext &&
            queryContext.DocumentResolver is not null)
        {
            uri = nodeBaseUri != null
                ? ResolveUriAgainstBase(uri, nodeBaseUri)
                : ResolveUriAgainstStaticBase(uri, queryContext);
            var doc = ResolveDocument(queryContext, uri);
            if (doc != null)
            {
                // If a fragment identifier is present, select element by ID
                if (fragment != null && queryContext.DocumentResolver is XsltDocumentResolver xsltResolver
                    && xsltResolver.NodeStore is { } nodeStore)
                {
                    var matches = XsltIdFunction.FindElementsById(fragment, doc, nodeStore);
                    if (matches.Length > 0)
                        return ValueTask.FromResult(matches[0]);
                    // Fragment not found → return empty sequence (per XSLT §13.1)
                    return ValueTask.FromResult<object?>(null);
                }
                return ValueTask.FromResult<object?>(doc);
            }
        }

        // FODC0002: the document cannot be retrieved (fn:doc's code, XSLT 3.0 §20.1)
        throw new XsltException($"FODC0002: Cannot retrieve document at URI '{uri}'");
    }

    /// <summary>
    /// Resolves a URI against the static base URI of the calling module.
    /// Empty URIs resolve to the static base URI itself; relative URIs resolve against it.
    /// </summary>
    private static string ResolveUriAgainstStaticBase(string uri, PhoenixmlDb.XQuery.Execution.QueryExecutionContext queryContext)
    {
        if (queryContext.StaticBaseUri == null)
            return uri;
        if (uri.Length == 0)
            return queryContext.StaticBaseUri;
        // Only relative URIs are resolved; file:/abs/path is absolute (file:///abs/path).
        return PhoenixmlDb.XQuery.Functions.LocationResolver.Absolute(uri, queryContext.StaticBaseUri)
            ?? throw new XsltException($"FODC0005: The URI '{uri}' cannot be resolved against the base URI");
    }

    /// <summary>
    /// Extracts the base URI from a node item for document() relative URI resolution.
    /// Returns null for non-node items.
    /// </summary>
    private static string? GetNodeBaseUri(object? item, PhoenixmlDb.XQuery.Ast.ExecutionContext? context = null)
    {
        if (item is System.Xml.XmlNode xmlNode)
        {
            var baseUri = xmlNode.BaseURI;
            return !string.IsNullOrEmpty(baseUri) ? baseUri : null;
        }
        if (item is Xdm.Nodes.XdmNode xdmNode)
        {
            var nodeProvider = (context as PhoenixmlDb.XQuery.Execution.QueryExecutionContext)?.NodeProvider;
            return PhoenixmlDb.XQuery.Functions.BaseUriFunction.ComputeBaseUri(xdmNode, nodeProvider);
        }
        if (item is System.Xml.Linq.XObject xObj)
        {
            var baseUri = xObj.BaseUri;
            return !string.IsNullOrEmpty(baseUri) ? baseUri : null;
        }
        return null;
    }

    /// <summary>
    /// Resolves a relative URI against a given base URI string.
    /// </summary>
    /// <summary>
    /// When the argument is a NODE, a relative reference resolves against that node's base URI
    /// (XSLT 3.0 §20.1). A node with no base URI — a parentless text node built by a variable —
    /// leaves nothing to resolve against, which is XTDE1162. It fell back to the static base
    /// instead and then failed to retrieve, reporting FODC0005 (error-1162a).
    /// </summary>
    private static void RequireBaseUriForNodeArgument(object? item, string? nodeBaseUri, string uri)
    {
        if (item is not Xdm.Nodes.XdmNode || nodeBaseUri != null || uri.Length == 0)
            return;
        if (Uri.TryCreate(uri, UriKind.Absolute, out _))
            return;
        throw new XsltException(
            $"XTDE1162: The relative URI '{uri}' cannot be resolved: the node supplied to document() has no base URI");
    }


    private static string ResolveUriAgainstBase(string uri, string baseUriStr)
    {
        if (string.IsNullOrEmpty(baseUriStr))
            return uri;
        if (uri.Length == 0)
            return baseUriStr;
        return PhoenixmlDb.XQuery.Functions.LocationResolver.Absolute(uri, baseUriStr)
            ?? throw new XsltException($"FODC0005: The URI '{uri}' cannot be resolved against the base URI");
    }

    /// <summary>
    /// A reference that is not a valid URI reference is FODC0005, checked before it is resolved,
    /// as fn:doc checks it (PhoenixmlDb.XQuery's DocFunction; document() raises fn:doc's errors,
    /// XSLT 3.0 §20.1). Without the check, whether such a reference failed as invalid depended on
    /// the base URI: with an empty one it was looked up as it was, and failed as not found.
    /// </summary>
    internal static void RequireValidUriReference(string uri)
    {
        if (uri.Length > 0 && (!Uri.TryCreate(uri, UriKind.RelativeOrAbsolute, out _) || IsColonInFirstRelativeSegment(uri)))
            throw new XsltException($"FODC0005: The URI '{uri}' passed to document() is not a valid URI");
    }

    /// <summary>
    /// A relative reference whose first path segment contains a colon is not a URI reference
    /// (RFC 3986 §4.2: that segment would read as a scheme), though System.Uri accepts it as
    /// relative. The same rule as fn:doc's.
    /// </summary>
    private static bool IsColonInFirstRelativeSegment(string uri)
    {
        var end = uri.IndexOfAny(['/', '?', '#']);
        var first = end < 0 ? uri : uri[..end];
        var colon = first.IndexOf(':', StringComparison.Ordinal);
        // scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ): before such a colon the reference
        // is absolute (file:/abs/path, urn:a:b).
        return colon >= 0 && !IsScheme(first.AsSpan(0, colon));
    }

    private static bool IsScheme(ReadOnlySpan<char> s)
    {
        if (s.Length == 0 || !char.IsAsciiLetter(s[0]))
            return false;
        foreach (var c in s)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
                return false;
        }
        return true;
    }

    /// <summary>
    /// A fragment identifier on an XML resource is an XPointer shorthand pointer, an NCName naming
    /// an element by ID. Anything else does not conform to the media type's fragment rules, which
    /// is XTDE1160 (XSLT 3.0 §20.1). It was taken as an ID that matched nothing, so document()
    /// returned the empty sequence silently (W3C error-1160a: '#123456789'). Checked before the
    /// resource is fetched.
    /// </summary>
    internal static void RequireShorthandPointer(string fragment)
    {
        try
        {
            System.Xml.XmlConvert.VerifyNCName(fragment);
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException)
        {
            throw new XsltException(
                $"XTDE1160: The fragment identifier '#{fragment}' is not a shorthand pointer (an NCName), so it does not conform to the fragment rules for XML");
        }
    }
}
