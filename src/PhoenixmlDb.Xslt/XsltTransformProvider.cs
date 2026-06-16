using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Engine;
using PhoenixmlDb.XQuery;
using PhoenixmlDb.XQuery.Functions;
using XQueryException = PhoenixmlDb.XQuery.Functions.XQueryException;

namespace PhoenixmlDb.Xslt;

/// <summary>
/// Implements <see cref="ITransformProvider"/> using the public <see cref="XsltTransformer"/> API,
/// enabling fn:transform() from XQuery to run XSLT transformations.
/// </summary>
public sealed class XsltTransformProvider : ITransformProvider
{
    /// <summary>
    /// Optional pre-fetched contents for URIs that <c>stylesheet-location</c> would otherwise
    /// need to fetch over HTTP synchronously. Required on Blazor WebAssembly which cannot
    /// block the calling thread for sync HTTP — without this, fn:transform from XQuery hits
    /// <see cref="Engine.HttpResourceLoader.GetStringAsync"/> and throws FOXT0001.
    /// </summary>
    /// <remarks>
    /// Mirrors the engine-internal fn:transform path (XsltTransformer.cs:28485-28505) which
    /// consults <see cref="PreloadedResources"/> via <c>_context._options.PreloadedResources</c>.
    /// Set this on the singleton registered as <see cref="PhoenixmlDb.XQuery.Functions.TransformFunction.Provider"/>
    /// before running queries that call fn:transform with a stylesheet-location URI.
    /// Martin Honnen 2026-06-01: DocBook xslTNG fn:transform-from-XQuery failed on WASM.
    /// </remarks>
    public PreloadedResources? PreloadedResources { get; set; }

    /// <inheritdoc/>
    public async ValueTask<object?> TransformAsync(
        IDictionary<object, object?> options,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);

        var stylesheetLocation = GetStringOption(options, "stylesheet-location");
        var stylesheetNode = GetOption(options, "stylesheet-node");
        var stylesheetText = GetStringOption(options, "stylesheet-text");
        var deliveryFormat = GetStringOption(options, "delivery-format") ?? "document";
        var initialTemplate = GetStringOption(options, "initial-template");
        var initialMode = GetStringOption(options, "initial-mode");
        // initial-function is a QName (xs:QName per spec); function-params is an array.
        // Used by Saxon-style fn:transform invocations that target an xsl:function rather
        // than apply-templates / a named template. Without these the provider would fall
        // through to the default apply-templates path with no source — Martin Honnen's
        // report: fn:transform with initial-function returned empty ?output even with
        // delivery-format='raw'.
        var initialFunctionQName = GetOption(options, "initial-function") as QName?;
        var functionParamsRaw = GetOption(options, "function-params");
        var sourceNode = GetOption(options, "source-node");
        // source-location: alternative to source-node carrying a URI string. Per Saxon
        // (https://www.saxonica.com/html/documentation12/functions/fn/transform.html)
        // and the XPath 4.0 draft (qt4cg.org function spec), the principal input is
        // loaded from the URI — enabling streaming when the engine can read the
        // document in document order. We resolve relative URIs against the caller's
        // static base URI just like stylesheet-location does.
        var sourceLocation = GetStringOption(options, "source-location");
        var staticParamsMap = GetOption(options, "static-params") as IDictionary<object, object?>;

        // Unwrap single-item arrays (from map constructor)
        if (sourceNode is object?[] srcArr && srcArr.Length == 1)
            sourceNode = srcArr[0];
        if (stylesheetNode is object?[] ssArr && ssArr.Length == 1)
            stylesheetNode = ssArr[0];

        var nodeStore = context.NodeStore;

        // Determine stylesheet XML
        string stylesheetXml;
        Uri? baseUri = null;

        if (stylesheetLocation != null)
        {
            // Resolve relative URI against static base URI
            var resolvedUri = stylesheetLocation;
            var staticBase = context.StaticBaseUri;
            if (staticBase != null && !Uri.TryCreate(resolvedUri, UriKind.Absolute, out _))
            {
                if (Uri.TryCreate(staticBase, UriKind.Absolute, out var sbu))
                {
                    var resolved = new Uri(sbu, resolvedUri);
                    resolvedUri = resolved.AbsoluteUri;
                    baseUri = resolved;
                }
            }
            else if (Uri.TryCreate(resolvedUri, UriKind.Absolute, out var absUri))
            {
                baseUri = absUri;
            }

            if (baseUri != null && baseUri.IsFile)
                stylesheetXml = await File.ReadAllTextAsync(baseUri.LocalPath).ConfigureAwait(false);
            else if (baseUri != null && (baseUri.Scheme == Uri.UriSchemeHttp || baseUri.Scheme == Uri.UriSchemeHttps))
            {
                // HTTP(S) stylesheet-location: consult preload cache first, then fetch.
                // Mirrors Engine/XsltTransformer.cs:28485-28505 — the XQuery-side mirror
                // previously went straight to HttpResourceLoader and bypassed the cache,
                // so fn:transform-from-XQuery threw FOXT0001 on Blazor WASM even when the
                // host had supplied the stylesheet via PreloadedResources.
                // Martin Honnen 2026-06-01: DocBook xslTNG WASM regression.
                if (PreloadedResources is { } preloaded && preloaded.TryGet(baseUri, out var preloadedContent))
                {
                    stylesheetXml = preloadedContent;
                }
                else if (OperatingSystem.IsBrowser())
                {
                    throw new XQueryException("FOXT0001",
                        $"Cannot fetch stylesheet '{baseUri}' on Blazor WebAssembly: " +
                        "synchronous HTTP I/O is not supported. Pre-fetch the stylesheet " +
                        "asynchronously and pass it through PreloadedResources on " +
                        "XsltTransformProvider (or supply stylesheet-text directly).");
                }
                else
                {
                    stylesheetXml = await PhoenixmlDb.Xslt.Engine.HttpResourceLoader.GetStringAsync(baseUri).ConfigureAwait(false);
                }
            }
            else
            {
                var dir = staticBase != null
                    ? Path.GetDirectoryName(new Uri(staticBase).LocalPath) ?? "."
                    : ".";
                var fullPath = Path.Combine(dir, resolvedUri);
                stylesheetXml = await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
                baseUri = new Uri(fullPath);
            }
        }
        else if (stylesheetNode is XdmNode ssNode)
        {
            stylesheetXml = SerializeNode(ssNode, nodeStore);
            if (string.IsNullOrWhiteSpace(stylesheetXml))
                throw new XQueryException("FOXT0001", "Stylesheet node has no content");
        }
        else if (stylesheetText != null)
        {
            stylesheetXml = stylesheetText;
        }
        else
        {
            throw new XQueryException("FOXT0001",
                "No stylesheet specified (stylesheet-location, stylesheet-node, or stylesheet-text required)");
        }

        // Build static params dictionary
        Dictionary<string, string>? staticParams = null;
        if (staticParamsMap != null)
        {
            staticParams = new Dictionary<string, string>();
            foreach (var (key, value) in staticParamsMap)
            {
                var paramName = key is QName qn
                    ? (qn.Namespace != NamespaceId.None && !string.IsNullOrEmpty(qn.ExpandedNamespace)
                        ? $"Q{{{qn.ExpandedNamespace}}}{qn.LocalName}"
                        : qn.LocalName)
                    : key.ToString() ?? "";
                staticParams[paramName] = StringValueOf(value);
            }
        }

        // Create transformer and load stylesheet
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheetXml, baseUri, staticParams).ConfigureAwait(false);

        // Set initial template / mode / function
        if (initialTemplate != null)
            transformer.SetInitialTemplate(initialTemplate);
        if (initialMode != null)
            transformer.SetInitialMode(initialMode);
        if (initialFunctionQName is { } funcQName)
        {
            // fn:QName(uri, qname) populates RuntimeNamespace, not ExpandedNamespace
            // (the latter is reserved for parser-time EQName Q{uri}local syntax).
            // ResolvedNamespace returns whichever is set — required so
            // QName('http://example.com/mf','foo') from XQuery resolves correctly
            // when used as fn:transform's initial-function key. Without this fall-through,
            // the namespace gets dropped and the function lookup hits XTDE0041 even
            // though the function is registered under the right namespace.
            var nsUri = !string.IsNullOrEmpty(funcQName.ResolvedNamespace)
                ? funcQName.ResolvedNamespace
                : null;
            transformer.SetInitialFunction(funcQName.LocalName, nsUri);
            // function-params is an XDM array — represented as List<object?> in our XDM
            // surface but coming through the map constructor it can also arrive as
            // object?[]. Walk it positionally and feed each element to the engine.
            if (functionParamsRaw is System.Collections.IList list)
            {
                foreach (var item in list)
                {
                    // Node arguments live in the XQuery-side store; their child/attribute
                    // NodeIds don't resolve in the inner XSLT engine's store. Mirror the
                    // in-engine path (XsltTransformer line ~27690): serialize to XML and
                    // wrap as CrossStoreNodeRef so the inner engine re-parses into its
                    // own store. Without this, xsl:evaluate against a passed-in node
                    // appears typed correctly but child/* navigation returns empty
                    // (Martin's Schematron repro after the namespace-id fix).
                    if (item is Xdm.Nodes.XdmElement or Xdm.Nodes.XdmDocument)
                    {
                        var xml = SerializeNode((Xdm.Nodes.XdmNode)item, nodeStore);
                        transformer.AddInitialFunctionArgument(
                            new Engine.XsltTransformEngine.CrossStoreNodeRef(
                                xml,
                                IsElement: item is Xdm.Nodes.XdmElement));
                    }
                    else
                    {
                        transformer.AddInitialFunctionArgument(item);
                    }
                }
            }
        }

        // Set static-params as runtime parameters too
        if (staticParamsMap != null)
        {
            foreach (var (key, value) in staticParamsMap)
            {
                var paramName = key is QName qn ? qn.LocalName : key.ToString() ?? "";
                transformer.SetParameter(paramName, StringValueOf(value));
            }
        }

        // Resolve the principal input.
        // Precedence: source-node beats source-location when both are supplied (Saxon
        // behaviour — the spec is silent on this). source-location fetches the URI
        // and feeds the XML to the transformer; relative URIs resolve against the
        // caller's static base URI (mirrors the stylesheet-location branch above).
        string? inputXml = null;
        if (sourceNode is XdmNode srcNode)
        {
            inputXml = SerializeNode(srcNode, nodeStore);
        }
        else if (sourceLocation != null)
        {
            var staticBase = context.StaticBaseUri;
            Uri? resolved = null;
            if (Uri.TryCreate(sourceLocation, UriKind.Absolute, out var absSrc))
            {
                resolved = absSrc;
            }
            else if (staticBase != null && Uri.TryCreate(staticBase, UriKind.Absolute, out var sb))
            {
                resolved = new Uri(sb, sourceLocation);
            }

            if (resolved == null)
            {
                // Plain relative path with no static base — read as file path.
                inputXml = await File.ReadAllTextAsync(Path.GetFullPath(sourceLocation)).ConfigureAwait(false);
            }
            else if (resolved.IsFile)
            {
                inputXml = await File.ReadAllTextAsync(resolved.LocalPath).ConfigureAwait(false);
            }
            else if (resolved.Scheme == Uri.UriSchemeHttp || resolved.Scheme == Uri.UriSchemeHttps)
            {
                if (OperatingSystem.IsBrowser())
                {
                    throw new XQueryException("FOXT0001",
                        $"Cannot fetch source from '{resolved}' on Blazor WebAssembly: " +
                        "synchronous HTTP I/O is not supported. Pre-fetch and pass via source-node instead.");
                }
                inputXml = await Engine.HttpResourceLoader.GetStringAsync(resolved).ConfigureAwait(false);
            }
            else
            {
                throw new XQueryException("FOXT0001",
                    $"source-location: unsupported URI scheme '{resolved.Scheme}'");
            }
        }

        // Build result map (XPath 4.0 ordered: "output" then secondary result docs in order)
        var resultMap = new PhoenixmlDb.XQuery.Execution.OrderedXdmMap(EqualityComparer<object>.Default);

        if (string.Equals(deliveryFormat, "raw", StringComparison.Ordinal))
        {
            // delivery-format='raw' returns the typed XDM value of the transformation
            // — preserves booleans/maps/nodes end-to-end instead of XML-roundtripping.
            // Honored when the transformation has a single well-defined return value
            // (initial-function path); template-based invocations still return a doc.
            // Found in Martin Honnen's fn:transform with initial-function returning
            // xs:boolean from xsl:evaluate, where ?output came back empty under both
            // 'document' and 'serialized' because the boolean's serialization
            // ("true"/"false") didn't reparse to a useful XDM document.
            var rawValue = await transformer.TransformToValueAsync(inputXml).ConfigureAwait(false);
            // The engine wraps any XdmNode/XdmDocument items in raw-delivery results as
            // CrossStoreNodeRef so we can re-anchor them in the XQuery store here —
            // inner-store NodeIds don't resolve outside (Martin Honnen: subtrees came
            // back as <root/> instead of <root>text</root>, path() walked stale Parent
            // chains, and multi-hop env:evaluate lost child navigation entirely).
            resultMap["output"] = ReanchorCrossStoreResult(rawValue, nodeStore as INodeBuilder);
        }
        else
        {
            // 'document' (default) or 'serialized' — go through the engine's serializer.
            var result = await transformer.TransformAsync(inputXml).ConfigureAwait(false);
            if (string.Equals(deliveryFormat, "serialized", StringComparison.Ordinal))
            {
                resultMap["output"] = result;
            }
            else
            {
                // 'document' default — parse result XML back to XDM document
                resultMap["output"] = !string.IsNullOrEmpty(result)
                    ? await ParseResultToXdmAsync(result, nodeStore).ConfigureAwait(false)
                    : null;
            }
        }

        // Secondary result documents
        foreach (var (href, content) in transformer.SecondaryResultDocuments)
        {
            if (string.Equals(deliveryFormat, "serialized", StringComparison.Ordinal))
                resultMap[href] = content;
            else
                resultMap[href] = await ParseResultToXdmAsync(content, nodeStore).ConfigureAwait(false);
        }

        return resultMap;
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static string? GetStringOption(IDictionary<object, object?> options, string key)
    {
        if (options.TryGetValue(key, out var val) && val != null)
            return StringValueOf(val);
        return null;
    }

    private static object? GetOption(IDictionary<object, object?> options, string key)
    {
        options.TryGetValue(key, out var val);
        return val;
    }

    /// <summary>
    /// Walks the raw result from <c>TransformToValueAsync</c>, re-parsing any
    /// <see cref="Engine.XsltTransformEngine.CrossStoreNodeRef"/> wrappers into the
    /// caller's <paramref name="outerBuilder"/> so the returned nodes carry NodeIds
    /// that the XQuery side can navigate. Without this, raw-delivery node results
    /// reach XQuery anchored to the (now-dying) inner XSLT engine's store; outer-side
    /// child navigation, <c>path()</c>, and serialization all return wrong/empty values.
    /// </summary>
    private static object? ReanchorCrossStoreResult(object? value, INodeBuilder? outerBuilder)
    {
        if (value is null || outerBuilder is null) return value;
        if (value is Engine.XsltTransformEngine.CrossStoreNodeRef wrapped)
            return ReanchorOne(wrapped, outerBuilder);
        if (value is object?[] arr)
        {
            var anyWrapped = false;
            for (var i = 0; i < arr.Length; i++)
            {
                if (arr[i] is Engine.XsltTransformEngine.CrossStoreNodeRef) { anyWrapped = true; break; }
            }
            if (!anyWrapped) return arr;
            var result = new object?[arr.Length];
            for (var i = 0; i < arr.Length; i++)
            {
                result[i] = arr[i] is Engine.XsltTransformEngine.CrossStoreNodeRef w
                    ? ReanchorOne(w, outerBuilder)
                    : arr[i];
            }
            return result;
        }
        return value;
    }

    private static object? ReanchorOne(Engine.XsltTransformEngine.CrossStoreNodeRef wrapped, INodeBuilder builder)
    {
        if (string.IsNullOrEmpty(wrapped.Xml)) return null;
        try
        {
            var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
            doc.LoadXml(wrapped.Xml);
            var localDoc = PhoenixmlDb.XQuery.Functions.ParseXmlFunction.ConvertToXdm(doc, builder);
            if (wrapped.IsElement && localDoc.DocumentElement is { } rootId
                && builder is INodeStore store && store.GetNode(rootId) is XdmElement rootElem)
            {
                return rootElem;
            }
            return localDoc;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string StringValueOf(object? value)
    {
        return value switch
        {
            null => "",
            string s => s,
            bool b => b ? "true" : "false",
            XdmNode node => node.StringValue,
            _ => value.ToString() ?? ""
        };
    }

    /// <summary>
    /// Serializes an XDM node to XML markup using the node store from the execution context.
    /// </summary>
    private static string SerializeNode(XdmNode node, INodeStore? nodeStore)
    {
        var sb = new StringBuilder();
        SerializeNodeToXml(node, nodeStore, sb);
        return sb.ToString();
    }

    private static void SerializeNodeToXml(XdmNode node, INodeStore? store, StringBuilder sb)
    {
        switch (node)
        {
            case XdmDocument doc:
                foreach (var childId in doc.Children)
                    if (store?.GetNode(childId) is XdmNode child)
                        SerializeNodeToXml(child, store, sb);
                break;

            case XdmElement elem:
                var prefix = elem.Prefix;
                var localName = elem.LocalName;
                var qname = !string.IsNullOrEmpty(prefix) ? $"{prefix}:{localName}" : localName;
                sb.Append('<').Append(qname);

                // Namespace declarations
                foreach (var nsDecl in elem.NamespaceDeclarations)
                {
                    var nsUri = store?.GetNamespaceUri(nsDecl.Namespace) ?? "";
                    if (string.IsNullOrEmpty(nsDecl.Prefix))
                        sb.Append(" xmlns=\"").Append(nsUri).Append('"');
                    else
                        sb.Append(" xmlns:").Append(nsDecl.Prefix).Append("=\"").Append(nsUri).Append('"');
                }

                // Attributes
                foreach (var attrId in elem.Attributes)
                    if (store?.GetNode(attrId) is XdmAttribute attr)
                    {
                        var attrName = !string.IsNullOrEmpty(attr.Prefix)
                            ? $"{attr.Prefix}:{attr.LocalName}"
                            : attr.LocalName;
                        sb.Append(' ').Append(attrName).Append("=\"").Append(EscapeAttr(attr.Value)).Append('"');
                    }

                // Children
                var hasChildren = false;
                foreach (var childId in elem.Children)
                {
                    if (!hasChildren) { sb.Append('>'); hasChildren = true; }
                    if (store?.GetNode(childId) is XdmNode child)
                        SerializeNodeToXml(child, store, sb);
                }

                if (hasChildren)
                    sb.Append("</").Append(qname).Append('>');
                else
                    sb.Append("/>");
                break;

            case XdmText text:
                sb.Append(EscapeText(text.Value));
                break;

            case XdmComment comment:
                sb.Append("<!--").Append(comment.Value).Append("-->");
                break;

            case XdmProcessingInstruction pi:
                sb.Append("<?").Append(pi.Target);
                if (!string.IsNullOrEmpty(pi.Value))
                    sb.Append(' ').Append(pi.Value);
                sb.Append("?>");
                break;
        }
    }

    private static string EscapeAttr(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal);
    }

    private static string EscapeText(string value)
        => CharacterEscaper.EscapeXmlText(value);

    /// <summary>
    /// Parses a result XML string back to an XDM document node using the context's node store,
    /// or returns the string if the node store doesn't support building or the XML is malformed.
    /// </summary>
    private static async ValueTask<object?> ParseResultToXdmAsync(string xml, INodeStore? nodeStore)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        if (nodeStore is INodeBuilder builder)
        {
            try
            {
                var xmlDoc = new XmlDocument { PreserveWhitespace = true };
                xmlDoc.LoadXml(xml);

                // Use the XQuery engine's parse-xml helper to build an XDM tree.
                // We instantiate ParseXmlFunction and invoke it — this is the simplest
                // way to reuse the existing ConvertToXdm logic without duplicating it.
                var parseXml = new ParseXmlFunction();
                var result = await parseXml.InvokeAsync(
                    [xml],
                    new SimpleExecutionContext(builder)).ConfigureAwait(false);
                if (result is XdmDocument xdmDoc)
                {
                    xdmDoc.DocumentUri = null;
                    return xdmDoc;
                }
                return result ?? xml;
            }
            catch (XmlException)
            {
                // Not well-formed XML — return as string
                return xml;
            }
        }

        // No node builder — return serialized string
        return xml;
    }

    /// <summary>
    /// Minimal execution context that exposes a node store for XDM tree construction.
    /// </summary>
    private sealed class SimpleExecutionContext(INodeBuilder nodeBuilder)
        : PhoenixmlDb.XQuery.Ast.ExecutionContext
    {
        public object? ContextItem => null;
        public INodeStore? NodeStore => nodeBuilder;
    }
}
