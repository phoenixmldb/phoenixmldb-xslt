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
/// fn:transform($options as map(*)) as map(*)
/// Runs an XSLT transformation and returns the result as a map.
/// XSLT 3.0 §25.3 / XPath Functions and Operators §17.2.
/// </summary>
internal sealed class XsltTransformFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltTransformFunction(DefaultXsltExecutionContext context) => _context = context;
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "transform");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => new()
    {
        ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Map,
        Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ExactlyOne
    };
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "options"), Type = new PhoenixmlDb.XQuery.Ast.XdmSequenceType
        {
            ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Map,
            Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ExactlyOne
        }}
    ];

    public override async ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        if (arguments[0] is not IDictionary<object, object?> options)
            throw new XsltException("FOXT0001: The argument to fn:transform must be a map");

        // Extract options
        var stylesheetLocation = GetStringOption(options, "stylesheet-location");
        var stylesheetNode = GetOption(options, "stylesheet-node");
        var stylesheetText = GetStringOption(options, "stylesheet-text");
        var packageName = GetStringOption(options, "package-name");
        var packageVersion = GetStringOption(options, "package-version");
        var deliveryFormat = GetStringOption(options, "delivery-format") ?? "document";
        var postProcessFn = GetOption(options, "post-process") as PhoenixmlDb.XQuery.Ast.XQueryFunction;
        var initialTemplate = GetQNameOption(options, "initial-template");
        var initialMode = GetQNameOption(options, "initial-mode");
        var initialFunctionQName = GetQNameOption(options, "initial-function");
        var functionParamsRaw = GetOption(options, "function-params");
        var sourceNode = GetOption(options, "source-node");
        var initialMatchSelection = GetOption(options, "initial-match-selection");
        // fn:transform's global-context-item option (XSLT 3.0 / the fn:transform spec): the item
        // to serve as the global context item. It was not read at all, so a caller supplying it
        // got no focus and any template evaluating "." raised XPDY0002. XSpec passes x:context
        // this way whenever the context is not a node - it cannot use source-node for an atomic.
        var globalContextItem = GetOption(options, "global-context-item");
        if (globalContextItem is object?[] gciArr && gciArr.Length == 1)
            globalContextItem = gciArr[0];
        // NOTE: a NODE global context item is passed through AS-IS, deliberately. Wrapping it
        // as a CrossStoreNodeRef (the way function-params are) makes the inner engine re-parse
        // it, which mints a NEW node — and these suites assert node IDENTITY
        // ("$x:result is $x:context"). Measured 2026-09-02: wrapping regressed
        // external_multiple-context-items_function from 1/2 back to an XPDY0002 abort. The
        // caller and inner engine usually share a node store, so no transport is needed; the
        // genuinely-cross-store case remains unhandled. See memory: global-context-item node
        // identity.
        var staticParamsMap = GetOption(options, "static-params") as IDictionary<object, object?>;

        // Load the stylesheet
        string stylesheetXml;
        Uri? baseUri = null;

        if (stylesheetLocation != null)
        {
            // Resolve relative URI against static base URI
            var resolvedUri = stylesheetLocation;
            string? staticBase = null;
            if (context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext qec)
                staticBase = qec.StaticBaseUri;
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

            // Load the stylesheet content from the resolved URI
            if (baseUri != null && baseUri.IsFile)
                stylesheetXml = await System.IO.File.ReadAllTextAsync(baseUri.LocalPath).ConfigureAwait(false);
            else if (baseUri != null && (baseUri.Scheme == Uri.UriSchemeHttp || baseUri.Scheme == Uri.UriSchemeHttps))
            {
                // HTTP(S) stylesheet-location: consult preload cache first, then fetch.
                // Previously this hit File.ReadAllTextAsync(resolvedUri) which on Linux/WASM
                // mangled "https://host/..." into "/https:/host/..." (Path normalization
                // collapsed the double slash) and threw FileNotFoundException — Martin
                // Honnen's Docbook-on-Blazor fn:transform repro.
                var preloaded = _context._options.PreloadedResources;
                if (preloaded != null && preloaded.TryGet(baseUri, out var preloadedContent))
                {
                    stylesheetXml = preloadedContent;
                }
                else if (OperatingSystem.IsBrowser())
                {
                    throw new XsltException(
                        $"FOXT0001: Cannot fetch stylesheet '{baseUri}' on Blazor WebAssembly: " +
                        "synchronous HTTP I/O is not supported. Pre-fetch the stylesheet " +
                        "asynchronously and pass it through PreloadedResources to LoadStylesheetAsync.");
                }
                else
                {
                    stylesheetXml = await HttpResourceLoader.GetStringAsync(baseUri).ConfigureAwait(false);
                }
            }
            else if (baseUri != null)
                stylesheetXml = await System.IO.File.ReadAllTextAsync(resolvedUri).ConfigureAwait(false);
            else
            {
                // Try as file path
                var dir = staticBase != null
                    ? System.IO.Path.GetDirectoryName(new Uri(staticBase).LocalPath) ?? "."
                    : ".";
                var fullPath = System.IO.Path.Combine(dir, resolvedUri);
                stylesheetXml = await System.IO.File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
                baseUri = new Uri(fullPath);
            }
        }
        else if (stylesheetNode is Xdm.Nodes.XdmNode node)
        {
            // Serialize the node to XML — StringValue strips markup which breaks parsing
            stylesheetXml = _context.SerializeXdmNodeToXml(node);
            if (string.IsNullOrWhiteSpace(stylesheetXml))
                throw new XsltException("FOXT0001: Stylesheet node has no content");
        }
        else if (stylesheetText != null)
        {
            stylesheetXml = stylesheetText;
        }
        else if (packageName != null)
        {
            // Resolve package from catalog
            var catalog = _context._stylesheet.PackageCatalog;
            if (catalog == null || !catalog.TryGetValue(packageName, out var entries))
                throw new XsltException($"FOXT0001: Package '{packageName}' not found");

            // Version matching — select the highest matching version.
            var matchedFile = StylesheetParser.SelectMatchingPackage(
                entries, packageVersion, PackageVersionResolution.Highest);
            if (matchedFile == null)
                throw new XsltException($"FOXT0001: No matching version for package '{packageName}' (requested '{packageVersion}')");

            stylesheetXml = await System.IO.File.ReadAllTextAsync(matchedFile).ConfigureAwait(false);
            baseUri = new Uri(System.IO.Path.GetFullPath(matchedFile));
        }
        else
        {
            throw new XsltException("FOXT0001: No stylesheet specified (stylesheet-location, stylesheet-node, stylesheet-text, or package-name required)");
        }

        // Parse the stylesheet, passing static-params if provided. Forward
        // PreloadedResources so xsl:imports inside the dynamically-loaded
        // stylesheet also benefit from the host's pre-fetched cache (WASM).
        var exprParser = new PhoenixmlDb.Xslt.XQueryExpressionParser();
        // A Saxon configuration in vendor-options declares where packages live; without it the
        // only catalog is the CALLING stylesheet's, which has none, so xsl:use-package in the
        // transformed stylesheet raised XTDE3052. See VendorOptionPackages — the XQuery-side
        // provider reads the same option through the same helper.
        var catalog2 = PhoenixmlDb.Xslt.VendorOptionPackages.BuildCatalog(
                           options, n => _context.SerializeXdmNodeToXml(n), baseUri)
                       ?? _context._stylesheet.PackageCatalog;
        var parser = catalog2 != null
            ? new StylesheetParser(exprParser, catalog2) { PreloadedResources = _context._options.PreloadedResources }
            : new StylesheetParser(exprParser) { PreloadedResources = _context._options.PreloadedResources };
        Dictionary<string, string>? externalStaticParams = null;
        if (staticParamsMap != null)
        {
            externalStaticParams = new Dictionary<string, string>();
            foreach (var (key, value) in staticParamsMap)
            {
                // Keys are QNames — use local name for unprefixed, Q{uri}local for namespaced
                var paramName = key is QName qn
                    ? (qn.Namespace != NamespaceId.None && !string.IsNullOrEmpty(qn.ExpandedNamespace)
                        ? $"Q{{{qn.ExpandedNamespace}}}{qn.LocalName}"
                        : qn.LocalName)
                    : key.ToString() ?? "";
                externalStaticParams[paramName] = DefaultXsltExecutionContext.StringValueOf(value);
            }
        }
        var stylesheet = parser.Parse(stylesheetXml, baseUri, externalStaticParams);

        // Unwrap source-node if it came wrapped in a single-item sequence (from map constructor)
        if (sourceNode is object?[] srcArr && srcArr.Length == 1)
            sourceNode = srcArr[0];

        // Build transform options
        var hasSource = sourceNode is Xdm.Nodes.XdmNode;
        object? initialModeSelectValue = null;
        if (initialMatchSelection != null && !hasSource && !initialTemplate.HasValue)
            initialModeSelectValue = initialMatchSelection;

        // An initial-match-selection given ALONGSIDE an initial-template used to be dropped on the
        // floor: the branch above requires !initialTemplate.HasValue, and nothing else looked at
        // it. The named template then ran with no focus and anything evaluating "." raised
        // XPDY0002. Supplying both is exactly how XSpec invokes a stylesheet under test - the
        // selection carries x:context, which may be an atomic value and so cannot travel as
        // source-node.
        object? initialContextItem = globalContextItem;
        if (initialContextItem == null && initialMatchSelection != null && initialTemplate.HasValue)
        {
            var sel = initialMatchSelection is object?[] selArr && selArr.Length == 1
                ? selArr[0]
                : initialMatchSelection;
            // Only a single item can be a context item; a longer selection is a match selection
            // for apply-templates, which this invocation is not doing.
            if (sel is not object?[])
                initialContextItem = sel;
        }

        // Build initial parameters from static-params (they serve as both compile-time
        // and runtime values) plus any regular stylesheet-params
        var initialParams = new Dictionary<QName, object?>();
        if (staticParamsMap != null)
        {
            foreach (var (key, value) in staticParamsMap)
            {
                var qn = key is QName q ? q : new QName(NamespaceId.None, key.ToString() ?? "");
                initialParams[qn] = DefaultXsltExecutionContext.StringValueOf(value);
            }
        }

        // stylesheet-params overlays the static-params above: both end up as global parameter
        // values, but these carry real XDM items and must NOT be string-valued the way
        // static-params are (those are compile-time strings). Applied second so an explicit
        // stylesheet-params entry wins over a same-named static one.
        // Node-valued parameters must cross into the inner engine the same way function-params
        // do: serialized, then re-parsed into the inner store. Passing the node itself hands
        // over Children NodeIds that mean nothing there, so the subtree arrives EMPTY — a
        // parameter <kid>text</kid> became <kid/>. function-params has always wrapped; these
        // three did not.
        if (TransformParameterOptions.Read(options, "stylesheet-params") is { } styleParams)
        {
            foreach (var (name, value) in styleParams)
                initialParams[name] = XsltTransformEngine.WrapNodesForCrossStoreTransport(value, _context);
        }
        // template-params and tunnel-params are deliberately NOT wrapped. Measured 2026-09-03:
        // wrapping them regressed external_nested-template-call and external_nested-function-call
        // from 6/0 to 3/3 and broke external_coverage-contents-table outright, because these
        // paths often already share the caller's node store — re-parsing there mints new nodes
        // and costs identity for no gain. stylesheet-params genuinely needed it (a global
        // parameter arrived with an empty subtree); these did not. See BUGS.md #20: the real fix
        // is to wrap only when the stores actually differ.
        var templateParams = TransformParameterOptions.Read(options, "template-params")
                             ?? new Dictionary<QName, object?>();
        var tunnelParams = TransformParameterOptions.Read(options, "tunnel-params")
                           ?? new Dictionary<QName, object?>();

        // initial-function: extract function-params (XDM array → IList) and pass to engine.
        // Mirrors XsltTransformProvider's path used when fn:transform is invoked from
        // XQuery — the XSLT-internal path was previously dropping these on the floor,
        // so calling fn:transform with initial-function from inside an XSLT silently
        // returned empty (Martin Honnen 2026-05-15 follow-up to #60).
        //
        // XdmNode arguments are serialized to XML here using the OUTER engine's node
        // store (this is the only place we still have access to it). The inner engine's
        // TransformRawAsync recognises the CrossStoreNodeRef wrapper and re-parses the
        // XML into its own node store, so xsl:evaluate / XPath inside the function can
        // navigate the node (Martin Honnen 2026-05-18: string($context-item) was
        // returning empty because the foreign node's child NodeIds didn't resolve in
        // the inner store).
        //
        // fn:QName(uri, localName) creates a QName with NamespaceId.None and only
        // ResolvedNamespace populated. The engine's function lookup keys on QName
        // equality, which uses the interned NamespaceId — so we must re-intern the
        // namespace via StylesheetParser to match the registered function key.
        QName? resolvedInitialFunction = initialFunctionQName;
        if (initialFunctionQName is { } ifq && !string.IsNullOrEmpty(ifq.ResolvedNamespace))
        {
            var nsId = StylesheetParser.ResolveNamespaceUri(ifq.ResolvedNamespace);
            if (nsId != ifq.Namespace)
            {
                resolvedInitialFunction = new QName(nsId, ifq.LocalName, ifq.Prefix)
                { ExpandedNamespace = ifq.ExpandedNamespace, RuntimeNamespace = ifq.RuntimeNamespace };
            }
        }
        var initialFunctionArgs = new List<object?>();
        if (resolvedInitialFunction != null && functionParamsRaw is System.Collections.IList paramList)
        {
            foreach (var item in paramList)
            {
                // Only a bare element/document is wrapped, deliberately. Extending this to
                // sequences (via WrapNodesForCrossStoreTransport, which recurses) does fix a
                // multi-node argument arriving empty — but measured 2026-09-03 it regressed
                // external_nested-function-call from 6/0 to 3/3, because these arguments usually
                // already share the caller's store and re-parsing costs node identity. See
                // BUGS.md #20: wrap only when the stores actually differ.
                if (item is Xdm.Nodes.XdmElement or Xdm.Nodes.XdmDocument)
                {
                    var xml = _context.SerializeXdmNodeToXml((Xdm.Nodes.XdmNode)item);
                    initialFunctionArgs.Add(new XsltTransformEngine.CrossStoreNodeRef(xml, IsElement: item is Xdm.Nodes.XdmElement));
                }
                else
                {
                    initialFunctionArgs.Add(item);
                }
            }
        }

        var isRaw = string.Equals(deliveryFormat, "raw", StringComparison.Ordinal);

        var transformOptions = new XsltTransformOptions
        {
            InitialTemplate = initialTemplate,
            InitialMode = initialMode,
            InitialFunction = resolvedInitialFunction,
            InitialFunctionArguments = initialFunctionArgs,
            HasSourceDocument = hasSource,
            InitialModeSelectValue = initialModeSelectValue,
            InitialParameters = initialParams,
            InitialTemplateParameters = templateParams,
            InitialTunnelParameters = tunnelParams,
            InitialContextItem = initialContextItem,
            GlobalContextItem = globalContextItem,
            // Under delivery-format='raw' the caller takes the typed XDM value and DISCARDS the
            // serialized buffer. Without this flag TransformRawAsync still built that buffer,
            // and building it means taking the string value of the result - which for a map is
            // FOTY0013. So a function returning a map failed on serialization the caller never
            // wanted. XsltTransformProvider, the XQuery-side twin of this function, has always
            // set the equivalent flag; this one did not.
            ReturnRawXdm = isRaw,
        };

        // Create engine and run transformation
        var engine = new XsltTransformEngine(stylesheet);

        // Determine the base URI to stamp on result document nodes (delivery-format='document').
        // Per XSLT 3.0 a result tree's document node carries a non-null static base URI; without
        // this the parsed result tree had base-uri(.)='' and DocBook's m:docbook pass (which runs
        // over the in-engine fn:transform result) hit FORG0002 in resolve-uri(@fileref, base-uri(.)).
        // DocBook's fp:run-transforms calls transform() from *inside* an XSLT stylesheet, so it
        // runs through this in-engine path — not XsltTransformProvider.
        // Precedence: base-output-uri option → source node's base → caller's static base.
        string? resultBaseUri = GetStringOption(options, "base-output-uri");
        if (resultBaseUri == null && sourceNode is Xdm.Nodes.XdmNode srcBaseNode)
            resultBaseUri = PhoenixmlDb.XQuery.Functions.BaseUriFunction.ComputeBaseUri(srcBaseNode, _context._nodeStore);
        if (resultBaseUri == null && context is PhoenixmlDb.XQuery.Execution.QueryExecutionContext qecBase)
            resultBaseUri = qecBase.StaticBaseUri;

        // Build result map
        var resultMap = new OrderedXdmMap(EqualityComparer<object>.Default);

        if (isRaw)
        {
            // Raw delivery: get XDM values directly (preserves function items, maps, etc.)
            object? rawResult;
            if (sourceNode is Xdm.Nodes.XdmNode srcNode)
            {
                var rawSrcXml = _context.SerializeXdmNodeToXml(srcNode);
                rawResult = await engine.TransformRawAsync(rawSrcXml, transformOptions).ConfigureAwait(false);
            }
            else
            {
                rawResult = await engine.TransformRawAsync("<empty/>", transformOptions).ConfigureAwait(false);
            }
            // Re-anchor into the CALLER's store, exactly as XsltTransformProvider does for
            // XQuery callers. Without this the CrossStoreNodeRef wrapper leaked out as an
            // atomic value and the result was not a node at all.
            var reanchored = XsltTransformProvider.ReanchorCrossStoreResult(
                rawResult, _context._nodeStore as PhoenixmlDb.XQuery.INodeBuilder);
            // A template whose body CONSTRUCTS nodes (a literal result element, xsl:element…)
            // writes them to the output buffer rather than the sequence collector, so the raw
            // transform has nothing typed to return and falls back to the serialized text. Under
            // delivery-format='raw' that text is the caller's whole result: handing back the
            // markup as a STRING made ?output a string, and copy-of then emitted &lt;in/&gt;
            // where the node was expected (W3C transform-005/006/008). Parse it back into the
            // caller's store — unwrapped, since raw delivers the nodes themselves and not a
            // document node (that is what delivery-format='document' is for). Text with no
            // markup stays a string: there is no node to recover, and the string IS the result.
            if (reanchored is string rawText && rawText.Contains('<', StringComparison.Ordinal))
                reanchored = ParseResultAsXdm(rawText, _context._nodeStore) ?? reanchored;
            resultMap["output"] = reanchored;

            // Into the CALLER's store, like ?output above: a node parsed into a store the
            // surrounding evaluation does not consult resolves its children against the caller's
            // store instead, where the same ids belong to other nodes — the secondary result
            // came back holding the PRIMARY result's content. That hazard was invisible while
            // this parse failed on the XML declaration and returned the markup as a string.
            foreach (var (href, content) in engine.SecondaryResultDocuments)
                resultMap[href] = ParseResultAsXdm(content, _context._nodeStore);
        }
        else
        {
            string result;
            if (sourceNode is Xdm.Nodes.XdmNode srcNode)
            {
                // Serialize the source node to XML so the inner engine can parse and
                // register it in its own node store. Passing the outer node store directly
                // doesn't work because the inner engine needs its own independent store
                // for nodes it creates during transformation.
                var srcXml = _context.SerializeXdmNodeToXml(srcNode);
                result = await engine.TransformAsync(srcXml, transformOptions).ConfigureAwait(false);
            }
            else
            {
                result = await engine.TransformAsync("<empty/>", transformOptions).ConfigureAwait(false);
            }

            if (string.Equals(deliveryFormat, "serialized", StringComparison.Ordinal))
            {
                resultMap["output"] = result;
            }
            else
            {
                resultMap["output"] = !string.IsNullOrEmpty(result) ? ParseResultAsDocument(result, _context._nodeStore, resultBaseUri) : null;
            }

            foreach (var (href, content) in engine.SecondaryResultDocuments)
            {
                if (string.Equals(deliveryFormat, "serialized", StringComparison.Ordinal))
                    resultMap[href] = content;
                else
                {
                    // A secondary result document's base URI is its own (absolute) href,
                    // resolved against the result base when relative.
                    string? secondaryBase = href;
                    if (!Uri.TryCreate(href, UriKind.Absolute, out _)
                        && resultBaseUri != null
                        && Uri.TryCreate(resultBaseUri, UriKind.Absolute, out var rbu)
                        && Uri.TryCreate(rbu, href, out var resolvedHref))
                    {
                        secondaryBase = resolvedHref.AbsoluteUri;
                    }
                    resultMap[href] = ParseResultAsDocument(content, _context._nodeStore, secondaryBase);
                }
            }
        }

        // Apply post-process function to each result document
        if (postProcessFn != null)
        {
            var keys = new List<object>(resultMap.Keys);
            foreach (var key in keys)
            {
                var uri = key is string s ? s : key.ToString() ?? "";
                var result = resultMap[key];
                var args = new List<object?> { uri, result };
                var processed = await postProcessFn.InvokeAsync(args, context).ConfigureAwait(false);
                resultMap[key] = processed;
            }
        }

        return resultMap;
    }

    private static object? ParseResultAsDocument(string xml, XdmInMemoryStore? store = null, string? resultBaseUri = null)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        try
        {
            var xmlDoc = new System.Xml.XmlDocument();
            xmlDoc.PreserveWhitespace = true;
            xmlDoc.LoadXml(xml);
            store ??= new XdmInMemoryStore();
            var doc = XsltTransformEngine.ConvertToXdm(xmlDoc, store);
            // Result trees have no document URI, but per XSLT 3.0 their document node carries
            // a non-null static base URI. DocBook's m:docbook pass runs over this tree and a
            // null base produced base-uri(.)='' → FORG0002 in resolve-uri(@fileref, base-uri(.)).
            if (resultBaseUri != null)
                doc.BaseUri = resultBaseUri;
            return doc;
        }
        catch (System.Xml.XmlException)
        {
            // Not well-formed XML — return as text
            return xml;
        }
    }

    private static object? ParseResultAsXdm(string xml, XdmInMemoryStore? store = null)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;
        // A serialized result document carries an XML declaration, which is legal at the start of
        // a document and illegal inside the wrapper this parse uses. The parse then failed and the
        // caller was handed the MARKUP AS A STRING, so the secondary results of a raw transform
        // came back escaped (W3C transform-008). Drop the declaration — the wrapper supplies the
        // document context it was describing.
        var declEnd = xml.StartsWith("<?xml", StringComparison.Ordinal)
            ? xml.IndexOf("?>", StringComparison.Ordinal)
            : -1;
        if (declEnd >= 0)
            xml = xml[(declEnd + 2)..].TrimStart();

        try
        {
            var xmlDoc = new System.Xml.XmlDocument();
            xmlDoc.PreserveWhitespace = true;
            xmlDoc.LoadXml($"<_wrap_>{xml}</_wrap_>");
            var nodeStore = store ?? new XdmInMemoryStore();
            var xdmDoc = XsltTransformEngine.ConvertToXdm(xmlDoc, nodeStore);
            if (xdmDoc.DocumentElement.HasValue && xdmDoc.DocumentElement.Value != NodeId.None)
            {
                var wrapper = nodeStore.GetNode(xdmDoc.DocumentElement.Value) as Xdm.Nodes.XdmElement;
                if (wrapper != null)
                {
                    var children = nodeStore.GetChildren(wrapper).ToList();
                    if (children.Count == 1)
                    {
                        if (children[0] is Xdm.Nodes.XdmNode cn)
                            cn.Parent = null;
                        return children[0];
                    }
                    foreach (var child in children)
                    {
                        if (child is Xdm.Nodes.XdmNode cn)
                            cn.Parent = null;
                    }
                    return children.ToArray();
                }
            }
            return xml;
        }
        catch (System.Xml.XmlException)
        {
            return xml;
        }
    }

    private static string? GetStringOption(IDictionary<object, object?> options, string key)
    {
        if (options.TryGetValue(key, out var val) && val != null)
            return DefaultXsltExecutionContext.StringValueOf(val);
        return null;
    }

    private static object? GetOption(IDictionary<object, object?> options, string key)
    {
        options.TryGetValue(key, out var val);
        return val;
    }

    private static QName? GetQNameOption(IDictionary<object, object?> options, string key)
    {
        if (!options.TryGetValue(key, out var val) || val == null)
            return null;
        if (val is QName qn)
        {
            // Resolve RuntimeNamespace/ExpandedNamespace to well-known NamespaceId
            // fn:QName() creates hash-based NamespaceIds that don't match well-known ones
            var resolved = qn.ResolvedNamespace;
            if (resolved != null)
            {
                var nsId = ResolveWellKnownNamespace(resolved);
                if (nsId != qn.Namespace)
                {
                    return new QName(nsId, qn.LocalName, qn.Prefix)
                        { ExpandedNamespace = qn.ExpandedNamespace, RuntimeNamespace = qn.RuntimeNamespace };
                }
            }
            return qn;
        }
        // String value — parse as QName (NCName or EQName)
        var str = DefaultXsltExecutionContext.StringValueOf(val);
        if (str.StartsWith("Q{", StringComparison.Ordinal))
        {
            var closeBrace = str.IndexOf('}', 2);
            if (closeBrace > 0)
            {
                var ns = str[2..closeBrace];
                var local = str[(closeBrace + 1)..];
                var nsId = ResolveWellKnownNamespace(ns);
                return new QName(nsId, local) { ExpandedNamespace = ns };
            }
        }
        return new QName(NamespaceId.None, str);
    }

    private static NamespaceId ResolveWellKnownNamespace(string uri)
    {
        return uri switch
        {
            "http://www.w3.org/1999/XSL/Transform" => NamespaceId.Xslt,
            "http://www.w3.org/2001/XMLSchema" => NamespaceId.Xsd,
            "http://www.w3.org/XML/1998/namespace" => NamespaceId.Xml,
            "http://www.w3.org/2000/xmlns/" => NamespaceId.Xmlns,
            "http://www.w3.org/2005/xpath-functions" => NamespaceId.Fn,
            "http://www.w3.org/2005/xpath-functions/map" => NamespaceId.Map,
            "http://www.w3.org/2005/xpath-functions/array" => NamespaceId.Array,
            "http://www.w3.org/2005/xpath-functions/math" => NamespaceId.Math,
            // Anything else is a namespace the STYLESHEET declared, and the fallback used to be
            // NamespaceId.None. That is not a harmless miss: a namespaced initial-mode arriving
            // as "no namespace" does not fail to resolve, it resolves to a DIFFERENT mode that
            // happens to share the local name, and runs it. Interning through the parser's own
            // table gives back the id the parser assigned the declaration.
            _ => QNameNamespaces.InternedIdFor(uri)
        };
    }



}
