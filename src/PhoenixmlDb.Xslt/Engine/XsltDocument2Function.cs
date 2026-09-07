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
/// XSLT document() with 2 args — URI + base node.
/// </summary>
internal sealed class XsltDocument2Function : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltDocument2Function(DefaultXsltExecutionContext context) => _context = context;
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "document");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => new()
    {
        ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Node,
        Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrMore
    };
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "uri-sequence"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.OptionalString },
        new() { Name = new QName(NamespaceId.None, "base-node"), Type = new PhoenixmlDb.XQuery.Ast.XdmSequenceType { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Node, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ExactlyOne } }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        // The second argument provides the base URI for resolving relative URIs.
        // Per XSLT 3.0 §13.1: the relative URI is resolved against the base URI of the
        // document containing the node supplied as the second argument.
        var baseNode = arguments.Count > 1 ? arguments[1] : null;
        string? baseUri = null;
        if (baseNode is Xdm.Nodes.XdmNode node)
        {
            // Use the node's base URI (for document nodes, this is the document URI)
            baseUri = node.BaseUri;
            // If the node doesn't have a base URI but is in a document, resolve via the node store
            if (baseUri == null && _context._nodeStore != null && node.Parent.HasValue)
            {
                var docNode = _context._nodeStore.GetNode(node.Parent.Value);
                while (docNode is Xdm.Nodes.XdmNode parentNode && parentNode is not Xdm.Nodes.XdmDocument && parentNode.Parent.HasValue)
                    docNode = _context._nodeStore.GetNode(parentNode.Parent.Value);
                if (docNode is Xdm.Nodes.XdmDocument docDoc)
                    baseUri = docDoc.BaseUri;
            }
        }

        // Resolve URIs from the first argument against the base URI from the second argument
        var uriArg = arguments[0];
        if (uriArg == null)
            return ValueTask.FromResult<object?>(null);

        // If we have a base URI from the second argument, resolve all URIs against it
        if (baseUri != null && context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext qc &&
            qc.DocumentResolver is not null)
        {
            // Handle sequence of URIs
            if (uriArg is object?[] arr)
                return LoadDocumentsWithBase(arr, baseUri, qc);
            if (uriArg is IEnumerable<object?> seq && uriArg is not string && uriArg is not Xdm.Nodes.XdmNode)
                return LoadDocumentsWithBase(seq, baseUri, qc);

            // Single URI
            return LoadSingleDocumentWithBase(uriArg, baseUri, qc);
        }

        // XTDE1162: If a second argument was provided but no base URI could be determined
        // (e.g., parentless text node), it's a dynamic error
        if (baseNode != null)
            throw new XsltException("XTDE1162: The second argument to the document() function has no base URI (e.g. a parentless node)");

        // Fall back to the 1-argument behavior (resolve against static base URI)
        return new XsltDocumentFunction(_context).InvokeAsync(arguments, context);
    }

    private static async ValueTask<object?> LoadDocumentsWithBase(IEnumerable<object?> items, string baseUri,
        PhoenixmlDb.XQuery.Execution.QueryExecutionContext qc)
    {
        var results = new List<object?>();
        foreach (var item in items)
        {
            if (item == null) continue;
            var doc = await LoadSingleDocumentWithBase(item, baseUri, qc).ConfigureAwait(false);
            if (doc != null)
                results.Add(doc);
        }
        return results.ToArray();
    }

    private static ValueTask<object?> LoadSingleDocumentWithBase(object uriArg, string baseUri,
        PhoenixmlDb.XQuery.Execution.QueryExecutionContext qc)
    {
        var uri = PhoenixmlDb.XQuery.Functions.ConcatFunction.XQueryStringValue(
            PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(uriArg));
        if (uri == null)
            return ValueTask.FromResult<object?>(null);

        // Extract fragment identifier
        string? fragment = null;
        var hashIdx = uri.IndexOf('#', StringComparison.Ordinal);
        if (hashIdx >= 0)
        {
            fragment = uri[(hashIdx + 1)..];
            uri = uri[..hashIdx];
        }

        // Resolve against the base node's document URI
        if (uri.Length == 0)
            uri = baseUri;
        else if (!Uri.TryCreate(uri, UriKind.Absolute, out _))
        {
            if (Uri.TryCreate(baseUri, UriKind.Absolute, out var baseUriObj))
                uri = new Uri(baseUriObj, uri).AbsoluteUri;
        }

        var doc = qc.DocumentResolver!.ResolveDocument(uri);
        if (doc != null)
        {
            if (fragment != null && qc.DocumentResolver is XsltDocumentResolver xsltResolver
                && xsltResolver.NodeStore is { } nodeStore)
            {
                var matches = XsltIdFunction.FindElementsById(fragment, doc, nodeStore);
                if (matches.Length > 0)
                    return ValueTask.FromResult(matches[0]);
                return ValueTask.FromResult<object?>(null);
            }
            return ValueTask.FromResult<object?>(doc);
        }
        throw new XsltException($"FODC0005: Cannot retrieve document at URI '{uri}'");
    }
}
