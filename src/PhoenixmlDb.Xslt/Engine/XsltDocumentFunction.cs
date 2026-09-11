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
            }

            RequireBaseUriForNodeArgument(item, nodeBaseUri, uri);
            if (context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext qc &&
                qc.DocumentResolver is not null)
            {
                uri = nodeBaseUri != null
                    ? ResolveUriAgainstBase(uri, nodeBaseUri)
                    : ResolveUriAgainstStaticBase(uri, qc);
                var doc = qc.DocumentResolver.ResolveDocument(uri);
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
                    throw new XsltException($"FODC0005: Cannot retrieve document at URI '{uri}'");
            }
            else
                throw new XsltException($"FODC0005: Cannot retrieve document at URI '{uri}'");
        }
        return ValueTask.FromResult<object?>(results.ToArray());
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
        }

        RequireBaseUriForNodeArgument(arg, nodeBaseUri, uri);
        if (context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext queryContext &&
            queryContext.DocumentResolver is not null)
        {
            uri = nodeBaseUri != null
                ? ResolveUriAgainstBase(uri, nodeBaseUri)
                : ResolveUriAgainstStaticBase(uri, queryContext);
            var doc = queryContext.DocumentResolver.ResolveDocument(uri);
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

        // FODC0005: Cannot resolve document URI
        throw new XsltException($"FODC0005: Cannot retrieve document at URI '{uri}'");
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
        // Only resolve relative URIs — absolute URIs are used as-is
        if (!Uri.TryCreate(uri, UriKind.Absolute, out _))
        {
            if (Uri.TryCreate(queryContext.StaticBaseUri, UriKind.Absolute, out var baseUri))
            {
                var resolved = new Uri(baseUri, uri);
                return resolved.AbsoluteUri;
            }
        }
        return uri;
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
        if (!Uri.TryCreate(uri, UriKind.Absolute, out _))
        {
            if (Uri.TryCreate(baseUriStr, UriKind.Absolute, out var baseUri))
            {
                var resolved = new Uri(baseUri, uri);
                return resolved.AbsoluteUri;
            }
        }
        return uri;
    }
}
