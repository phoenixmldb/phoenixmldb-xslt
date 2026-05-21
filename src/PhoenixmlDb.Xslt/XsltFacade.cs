using System.IO;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.XQuery.Parser;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;

namespace PhoenixmlDb.Xslt;

/// <summary>
/// Primary API for running XSLT transformations in .NET.
/// </summary>
/// <remarks>
/// <para>
/// <c>XsltTransformer</c> provides a simple three-step workflow for XSLT processing:
/// create an instance, load a stylesheet, and transform XML input. It supports
/// XSLT 3.0 and 4.0 features including streaming, packages, higher-order functions,
/// maps/arrays.
/// </para>
/// <para>
/// <b>Invocation styles:</b> XSLT defines three ways to start a transformation:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Apply templates</b> (default) — the processor matches templates against the
/// source document. Optionally set an initial mode with <see cref="SetInitialMode"/>.
/// </description></item>
/// <item><description>
/// <b>Call template</b> — invoke a named template via <see cref="SetInitialTemplate"/>.
/// No source document is required (pass <c>null</c> to <see cref="TransformAsync(string?, CancellationToken)"/>).
/// </description></item>
/// <item><description>
/// <b>Call function</b> — invoke a public stylesheet function via
/// <see cref="SetInitialFunction"/> and <see cref="AddInitialFunctionArgument"/>.
/// </description></item>
/// </list>
/// <para>
/// <b>Thread safety:</b> Instances are lightweight and not thread-safe. Create a new
/// <c>XsltTransformer</c> for each transformation rather than sharing one across threads.
/// </para>
/// <para>
/// <b>Basic usage:</b>
/// </para>
/// <example>
/// <code>
/// var transformer = new XsltTransformer();
/// await transformer.LoadStylesheetAsync(myXsltString);
/// transformer.SetParameter("reportDate", "2025-01-15");
/// string result = await transformer.TransformAsync(inputXml);
/// </code>
/// </example>
/// <para>
/// <b>Using initial-template invocation (no source document):</b>
/// </para>
/// <example>
/// <code>
/// var transformer = new XsltTransformer();
/// await transformer.LoadStylesheetAsync(generatorStylesheet);
/// transformer.SetInitialTemplate("main");
/// string result = await transformer.TransformAsync(null);
/// </code>
/// </example>
/// </remarks>
/// <seealso cref="XsltException"/>
public sealed class XsltTransformer
{
    private XsltStylesheet? _stylesheet;
    private readonly Dictionary<string, object?> _typedParameters = new();
    private readonly Dictionary<QName, object?> _initialTemplateParams = new();
    private readonly Dictionary<QName, object?> _initialTunnelParams = new();
    private string? _initialTemplate;
    private string? _initialTemplateNamespace;
    private string? _initialMode;
    private string? _initialModeNamespace;
    private string? _initialFunction;
    private string? _initialFunctionNamespace;
    private readonly List<object?> _initialFunctionArgs = [];
    private Uri? _sourceDocumentUri;
    private string? _sourceSelect;
    private string? _initialModeSelect;
    private readonly Dictionary<string, List<string>> _collections = new();

    /// <summary>
    /// Secondary result documents produced by <c>xsl:result-document</c>, keyed by the
    /// <c>href</c> attribute value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This dictionary is repopulated after each call to <see cref="TransformAsync(string?, CancellationToken)"/>.
    /// Each entry maps the <c>href</c> URI from an <c>xsl:result-document</c> instruction
    /// to the serialized output string for that document.
    /// </para>
    /// <para>
    /// A common pattern is to iterate over the entries and write each to a separate file:
    /// </para>
    /// <example>
    /// <code>
    /// string primary = await transformer.TransformAsync(inputXml);
    /// File.WriteAllText("output/main.html", primary);
    ///
    /// foreach (var (href, content) in transformer.SecondaryResultDocuments)
    /// {
    ///     File.WriteAllText($"output/{href}", content);
    /// }
    /// </code>
    /// </example>
    /// </remarks>
    public IReadOnlyDictionary<string, string> SecondaryResultDocuments { get; private set; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Controls whether DTD processing is allowed when loading stylesheets.
    /// Default is <c>false</c> (DTDs are prohibited) for security.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <c>false</c>, stylesheets containing <c>&lt;!DOCTYPE&gt;</c> declarations will have
    /// DTDs ignored. Entity references in the stylesheet will not be expanded.
    /// </para>
    /// <para>
    /// Set to <c>true</c> only if you trust the stylesheet source and need DTD entity expansion
    /// (e.g., stylesheets that define shorthand entities via internal DTD subsets). When enabled,
    /// entity expansion is limited to 1,000,000 characters to mitigate entity expansion attacks.
    /// </para>
    /// </remarks>
    public bool AllowDtdProcessing { get; set; }

    /// <summary>
    /// Maximum number of secondary result documents allowed per transformation.
    /// Default is 1000. Set to 0 for unlimited.
    /// </summary>
    public int MaxResultDocuments { get; set; } = 1000;

    /// <summary>
    /// True when the loaded stylesheet declares at least one streamable mode (either an
    /// explicit <c>&lt;xsl:mode streamable="yes"/&gt;</c> declaration or a streamable
    /// default mode). The CLI uses this signal to auto-select the streaming
    /// transform path for file inputs — non-streamable stylesheets always run on the
    /// materialising path. Returns false if no stylesheet is loaded yet.
    /// </summary>
    public bool HasStreamableMode
    {
        get
        {
            if (_stylesheet == null) return false;
            foreach (var mode in _stylesheet.Modes.Values)
                if (mode.Streamable) return true;
            return false;
        }
    }

    /// <summary>
    /// Optional resource security policy. When set, controls which URIs the stylesheet can access
    /// via <c>doc()</c>, <c>unparsed-text()</c>, <c>collection()</c>, <c>xsl:result-document</c>,
    /// and <c>xsl:import</c>/<c>xsl:include</c>.
    /// See <see cref="PhoenixmlDb.XQuery.Security.ResourcePolicy.ServerDefault"/> for a secure server configuration.
    /// </summary>
    public PhoenixmlDb.XQuery.Security.ResourcePolicy? ResourcePolicy { get; set; }

    /// <summary>
    /// Pre-fetched contents for URIs that <c>xsl:import</c> / <c>xsl:include</c> /
    /// <c>fn:doc()</c> would otherwise need to fetch over HTTP synchronously.
    /// Required on Blazor WebAssembly, which cannot block the calling thread; ignored
    /// on runtimes that can. Async-fetch the resources in your host code (via
    /// <see cref="System.Net.Http.HttpClient"/> with <c>await</c> or via JS interop)
    /// and assign before calling <see cref="LoadStylesheetAsync"/>.
    /// See <see cref="PreloadedResources"/> for a usage example.
    /// </summary>
    public PreloadedResources? PreloadedResources { get; set; }

    /// <summary>
    /// Schema provider for schema-aware processing. Defaults to a fresh
    /// <see cref="PhoenixmlDb.XQuery.XsdSchemaProvider"/> with no schemas loaded;
    /// any <c>xsl:import-schema</c> declarations encountered while loading a
    /// stylesheet are routed to this provider via
    /// <see cref="PhoenixmlDb.XQuery.ISchemaProvider.ImportSchema"/>.
    /// </summary>
    /// <remarks>
    /// Replace with a custom <see cref="PhoenixmlDb.XQuery.ISchemaProvider"/> implementation
    /// (e.g. RelaxNG-backed, Schematron-derived, in-memory) before calling
    /// <see cref="LoadStylesheetAsync"/>. Set to <c>null</c> to disable schema features
    /// entirely (rare opt-out — every <c>schema-element/attribute</c> reference becomes
    /// XPST0008 and every <c>validation="strict"</c> raises a runtime error).
    /// </remarks>
    public PhoenixmlDb.XQuery.ISchemaProvider? SchemaProvider { get; set; }
        = new PhoenixmlDb.XQuery.XsdSchemaProvider();

    /// <summary>
    /// Compiles and loads an XSLT stylesheet from its XML source text.
    /// </summary>
    /// <param name="stylesheetXml">
    /// The complete XSLT stylesheet as an XML string. Must be well-formed XML with an
    /// <c>xsl:stylesheet</c> or <c>xsl:transform</c> root element.
    /// </param>
    /// <param name="baseUri">
    /// Base URI used to resolve relative references in <c>xsl:import</c>, <c>xsl:include</c>,
    /// and the <c>doc()</c> / <c>document()</c> functions. If <c>null</c>, relative URIs
    /// cannot be resolved and will cause an error.
    /// </param>
    /// <param name="staticParams">
    /// XSLT 3.0 static parameters (<c>xsl:param</c> with <c>static="yes"</c>). These are
    /// evaluated at compile time and can influence conditional compilation via
    /// <c>xsl:use-when</c> and <c>static-params</c>. Keys are parameter local names;
    /// values are string representations.
    /// </param>
    /// <param name="packageCatalog">
    /// Maps XSLT 3.0 package names to available versions and file paths, enabling
    /// <c>xsl:use-package</c> to locate and load package dependencies. Each key is a
    /// package name URI; the value is a list of <c>(Version, FilePath)</c> tuples.
    /// </param>
    /// <returns>A completed task. The stylesheet is parsed synchronously.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stylesheetXml"/> is <c>null</c>.</exception>
    /// <exception cref="XsltException">The stylesheet contains syntax errors or invalid XSLT constructs.</exception>
    public async Task LoadStylesheetAsync(string stylesheetXml, Uri? baseUri = null,
        Dictionary<string, string>? staticParams = null,
        Dictionary<string, List<(string? Version, string FilePath)>>? packageCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(stylesheetXml);

        // Pre-fetch HTTP-resolved xsl:import / xsl:include hrefs into the preload cache
        // BEFORE invoking the synchronous parser. The parser's import resolver consults
        // the cache first; with everything pre-fetched, it never hits the sync HTTP
        // fallback that throws "Cannot wait on monitors" on Blazor WebAssembly.
        // Skip when the host hasn't asked for HTTP imports (no http(s) refs present →
        // walker is cheap; trivially-malformed XML is swallowed and surfaces via the parser).
        var effectivePreload = PreloadedResources ?? new PreloadedResources();
        await HttpImportPreloader.PreloadHttpImportsAsync(stylesheetXml, baseUri, effectivePreload).ConfigureAwait(false);

        var exprParser = new XQueryExpressionParser();
        var parser = packageCatalog != null
            ? new StylesheetParser(exprParser, packageCatalog) { AllowDtdProcessing = AllowDtdProcessing, ResourcePolicy = ResourcePolicy, PreloadedResources = effectivePreload }
            : new StylesheetParser(exprParser) { AllowDtdProcessing = AllowDtdProcessing, ResourcePolicy = ResourcePolicy, PreloadedResources = effectivePreload };
        _stylesheet = parser.Parse(stylesheetXml, baseUri, staticParams);
        ResolveSchemaImports(_stylesheet, baseUri);
        // Cross-feed static-param values to the runtime parameter map. A `static="yes"`
        // parameter is resolved at compile time for use-when / shadow attributes, but the
        // same `$debug` variable is also visible at runtime — and consumers expect both
        // sides to see the same overridden value. Without this, `LoadStylesheetAsync(staticParams: { debug: "true" })`
        // would still evaluate `<xsl:param select="false()"/>` at runtime and surface false
        // for `<xsl:if test="$debug">`. Skip names that the caller has already explicitly set
        // via SetParameter, so user-provided typed values take precedence over the string
        // form supplied here.
        if (staticParams is not null)
        {
            foreach (var (name, value) in staticParams)
            {
                if (!_typedParameters.ContainsKey(name))
                    _typedParameters[name] = ParseExternalParamValue(value);
            }
        }
    }

    /// <summary>
    /// Same value-parsing heuristics as <c>StylesheetParser.PopulateExternalStaticParams</c>:
    /// recognise XPath-shaped literals, bare booleans, and numeric strings, falling through
    /// to <c>xs:untypedAtomic</c> for free-form strings. Keeps the runtime-side variable in
    /// sync with what the static-param resolver decided about the same value.
    /// </summary>
    private static object? ParseExternalParamValue(string value)
    {
        var v = value.Trim();
        if ((v.StartsWith('\'') && v.EndsWith('\'')) || (v.StartsWith('"') && v.EndsWith('"')))
            return v[1..^1];
        if (v is "true()" or "false()") return v == "true()";
        if (v == "()") return null;
        if (v.Equals("true", StringComparison.Ordinal) || v.Equals("false", StringComparison.Ordinal))
            return v == "true";
        if (long.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l))
            return l;
        if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d;
        return new Xdm.XsUntypedAtomic(value);
    }

    /// <summary>
    /// Forwards every captured <c>xsl:import-schema</c> declaration to the registered
    /// <see cref="SchemaProvider"/>. Schema-location URIs are resolved against the
    /// stylesheet base URI before being handed to the provider so relative hints work.
    /// </summary>
    private void ResolveSchemaImports(XsltStylesheet stylesheet, Uri? baseUri)
    {
        if (SchemaProvider is null) return;
        // Walk imported and included stylesheets too — import-schema can appear in any module.
        foreach (var import in EnumerateAllSchemaImports(stylesheet))
        {
            // A bare <xsl:import-schema/> with no namespace and no location hints is a
            // marker that puts the parser into schema-aware mode (suppresses XTSE1660 on
            // validation= attributes) without actually loading anything. Don't forward it
            // to the provider — that would fail with XQST0059 since there's nothing to load.
            if (string.IsNullOrEmpty(import.TargetNamespace) && import.SchemaLocations.Count == 0)
                continue;
            var resolved = ResolveLocations(import.SchemaLocations, baseUri);
            try
            {
                SchemaProvider.ImportSchema(import.TargetNamespace, resolved);
            }
            catch (PhoenixmlDb.XQuery.SchemaException ex)
            {
                throw new XsltException(
                    $"{ex.ErrorCode}: xsl:import-schema failed for namespace '{import.TargetNamespace}': {ex.Message}",
                    import.Location);
            }
        }
    }

    private static IEnumerable<XsltSchemaImport> EnumerateAllSchemaImports(XsltStylesheet stylesheet)
    {
        foreach (var imp in stylesheet.SchemaImports) yield return imp;
        foreach (var inc in stylesheet.Includes)
            foreach (var imp in EnumerateAllSchemaImports(inc))
                yield return imp;
        foreach (var imp in stylesheet.Imports)
            foreach (var s in EnumerateAllSchemaImports(imp))
                yield return s;
    }

    private static IReadOnlyList<string>? ResolveLocations(IReadOnlyList<string> locations, Uri? baseUri)
    {
        if (locations.Count == 0) return null;
        if (baseUri is null) return locations;
        var result = new List<string>(locations.Count);
        foreach (var loc in locations)
        {
            if (Uri.TryCreate(baseUri, loc, out var resolved))
                result.Add(resolved.IsFile ? resolved.LocalPath : resolved.ToString());
            else
                result.Add(loc);
        }
        return result;
    }

    /// <summary>
    /// Sets a stylesheet parameter with a string value.
    /// </summary>
    /// <param name="name">
    /// The local name of an <c>xsl:param</c> declared at the top level of the stylesheet.
    /// </param>
    /// <param name="value">
    /// The string value to bind. The value is supplied as <c>xs:untypedAtomic</c>,
    /// which means it will be automatically promoted during comparisons — for example,
    /// comparing it to an <c>xs:double</c> will succeed without an explicit cast.
    /// </param>
    /// <remarks>
    /// <para>
    /// If you need the parameter to carry a specific XDM type (e.g., <c>xs:integer</c>
    /// or <c>xs:boolean</c>), use the <see cref="SetParameter(string, object?)"/> overload
    /// with a typed .NET value such as <see cref="int"/> or <see cref="bool"/>.
    /// </para>
    /// </remarks>
    public void SetParameter(string name, string value)
    {
        // External string parameters are xs:untypedAtomic per XSLT §9.3,
        // enabling automatic promotion to xs:double for arithmetic
        _typedParameters[name] = new Xdm.XsUntypedAtomic(value);
    }

    /// <summary>
    /// Sets a stylesheet parameter with a typed value, preserving its XDM type.
    /// </summary>
    /// <param name="name">
    /// The local name of an <c>xsl:param</c> declared at the top level of the stylesheet.
    /// </param>
    /// <param name="value">
    /// The typed value to bind. .NET types are mapped to XDM types: <see cref="int"/> and
    /// <see cref="long"/> become <c>xs:integer</c>, <see cref="double"/> becomes
    /// <c>xs:double</c>, <see cref="bool"/> becomes <c>xs:boolean</c>, <see cref="decimal"/>
    /// becomes <c>xs:decimal</c>, and <see cref="string"/> becomes <c>xs:untypedAtomic</c>.
    /// Pass <c>null</c> to set the parameter to an empty sequence.
    /// </param>
    /// <remarks>
    /// Parameters must match <c>xsl:param</c> declarations in the stylesheet. If the
    /// stylesheet declares a parameter with <c>as="xs:integer"</c> and you supply a string,
    /// a type error will occur at runtime.
    /// </remarks>
    public void SetParameter(string name, object? value)
    {
        _typedParameters[name] = value;
    }

    /// <summary>
    /// Sets an initial template parameter, passed via <c>xsl:with-param</c> to the initial
    /// template when using call-template invocation.
    /// </summary>
    /// <param name="name">The qualified name matching an <c>xsl:param</c> in the initial template.</param>
    /// <param name="value">The value to bind to the parameter.</param>
    /// <remarks>
    /// These parameters are distinct from stylesheet-level parameters set via
    /// <see cref="SetParameter(string, object?)"/>. They correspond to parameters declared
    /// inside the named template specified by <see cref="SetInitialTemplate"/>.
    /// </remarks>
    public void SetInitialTemplateParameter(QName name, object? value)
    {
        _initialTemplateParams[name] = value;
    }

    /// <summary>
    /// Sets an initial template tunnel parameter, which propagates through the call chain
    /// without being explicitly declared at each level.
    /// </summary>
    /// <param name="name">The qualified name of the tunnel parameter.</param>
    /// <param name="value">The value to bind to the tunnel parameter.</param>
    /// <remarks>
    /// Tunnel parameters are an XSLT 2.0+ feature that allows values to "tunnel" through
    /// intermediate template calls to deeply nested templates that declare matching
    /// <c>xsl:param tunnel="yes"</c> parameters.
    /// </remarks>
    public void SetInitialTunnelParameter(QName name, object? value)
    {
        _initialTunnelParams[name] = value;
    }

    /// <summary>
    /// Sets the initial named template for call-template invocation.
    /// </summary>
    /// <param name="name">
    /// The template name. For templates in a namespace, use either a prefixed name
    /// (e.g., <c>"my:main"</c>) with the namespace URI in <paramref name="namespaceUri"/>,
    /// or just the local name with the namespace URI.
    /// </param>
    /// <param name="namespaceUri">
    /// The namespace URI of the template, or <c>null</c> for templates in no namespace.
    /// </param>
    /// <remarks>
    /// <para>
    /// When an initial template is set, the transformation begins by calling that named
    /// template rather than applying templates to the source document. This means the
    /// <c>inputXml</c> parameter of <see cref="TransformAsync(string?, CancellationToken)"/> can be <c>null</c> —
    /// the template generates output without needing a source document.
    /// </para>
    /// <para>
    /// The conventional entry-point template name is <c>"xsl:initial-template"</c>
    /// (in the XSLT namespace), which is the XSLT 3.0 default initial template.
    /// </para>
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054:URI-like parameters should not be strings")]
    public void SetInitialTemplate(string name, string? namespaceUri = null)
    {
        _initialTemplate = name;
        _initialTemplateNamespace = namespaceUri;
    }

    /// <summary>
    /// Sets the initial mode, determining which set of templates is applied to the source document.
    /// </summary>
    /// <param name="mode">
    /// The mode name. Use <c>"#unnamed"</c> to explicitly select the unnamed (default) mode,
    /// or a specific mode name to select templates declared with that <c>mode</c> attribute.
    /// </param>
    /// <param name="namespaceUri">
    /// The namespace URI of the mode, or <c>null</c> for modes in no namespace.
    /// </param>
    /// <remarks>
    /// <para>
    /// Modes allow a stylesheet to define multiple sets of template rules for the same
    /// input nodes. For example, a stylesheet might have a <c>"toc"</c> mode for generating
    /// a table of contents and a default mode for the main output.
    /// </para>
    /// <para>
    /// If no initial mode is set, the unnamed (default) mode is used. The stylesheet's
    /// <c>xsl:stylesheet/@default-mode</c> attribute can also influence this behavior.
    /// </para>
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054:URI-like parameters should not be strings")]
    public void SetInitialMode(string mode, string? namespaceUri = null)
    {
        _initialMode = mode;
        _initialModeNamespace = namespaceUri;
    }

    /// <summary>
    /// Sets the initial function to call, using the XSLT 3.0 "call function" invocation style.
    /// </summary>
    /// <param name="name">
    /// The function name. Must match a public <c>xsl:function</c> declared with
    /// <c>visibility="public"</c> (or the default visibility in XSLT 3.0).
    /// </param>
    /// <param name="namespaceUri">
    /// The namespace URI of the function. Stylesheet functions must be in a non-null namespace.
    /// </param>
    /// <remarks>
    /// <para>
    /// When using call-function invocation, supply arguments via
    /// <see cref="AddInitialFunctionArgument"/> in the order declared by the function's
    /// <c>xsl:param</c> elements. No source document is required — pass <c>null</c> to
    /// <see cref="TransformAsync(string?, CancellationToken)"/>.
    /// </para>
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054:URI-like parameters should not be strings")]
    public void SetInitialFunction(string name, string? namespaceUri = null)
    {
        _initialFunction = name;
        _initialFunctionNamespace = namespaceUri;
    }

    /// <summary>
    /// Adds a positional argument for the initial function call.
    /// </summary>
    /// <param name="value">
    /// The argument value. Arguments are positional — add them in the same order as the
    /// <c>xsl:param</c> declarations in the target <c>xsl:function</c>.
    /// </param>
    /// <remarks>
    /// Only used with call-function invocation (see <see cref="SetInitialFunction"/>).
    /// Call this method once per function parameter, in declaration order.
    /// </remarks>
    public void AddInitialFunctionArgument(object? value)
    {
        _initialFunctionArgs.Add(value);
    }

    /// <summary>
    /// Sets the URI of the source document, used for <c>base-uri()</c> and
    /// <c>document-uri()</c> resolution during transformation.
    /// </summary>
    /// <param name="uri">
    /// The URI to associate with the source document. This does not load the document
    /// from the URI — it only sets the base URI metadata on the document node created
    /// from the <c>inputXml</c> string passed to <see cref="TransformAsync(string?, CancellationToken)"/>.
    /// </param>
    /// <remarks>
    /// Setting a source document URI is important when the stylesheet uses relative URIs
    /// in <c>doc()</c> or <c>document()</c> calls that should resolve relative to the
    /// source document's location rather than the stylesheet's base URI.
    /// </remarks>
    public void SetSourceDocumentUri(Uri uri)
    {
        _sourceDocumentUri = uri;
    }

    /// <summary>
    /// Sets an XPath expression to select the initial context node from the source document.
    /// </summary>
    /// <param name="select">
    /// An XPath expression evaluated against the source document. The result becomes the
    /// initial context item for the transformation. For example, <c>"/doc"</c> selects the
    /// document element named <c>doc</c> instead of the document root node.
    /// </param>
    /// <remarks>
    /// By default, the initial context item is the document node (root) of the source document.
    /// Use this method when the stylesheet expects a specific element or node as its starting
    /// context rather than the root.
    /// </remarks>
    public void SetSourceSelect(string select)
    {
        _sourceSelect = select;
    }

    /// <summary>
    /// Sets an XPath expression to determine the initial match selection for the initial mode.
    /// </summary>
    /// <param name="select">
    /// An XPath expression evaluated with the source document as context. Templates from the
    /// initial mode are applied to each item in the resulting sequence, rather than to the
    /// document root.
    /// </param>
    /// <remarks>
    /// <para>
    /// This corresponds to the XSLT 3.0 <c>initial-match-selection</c> concept. It allows
    /// you to apply templates to a computed set of nodes rather than the single root node.
    /// For example, <c>"//chapter"</c> would apply templates to every <c>chapter</c> element
    /// in the source document.
    /// </para>
    /// </remarks>
    public void SetInitialModeSelect(string select)
    {
        _initialModeSelect = select;
    }

    /// <summary>
    /// Registers a named collection of document file paths, making them available to the
    /// XPath <c>fn:collection()</c> function during transformation.
    /// </summary>
    /// <param name="uri">
    /// The collection URI that the stylesheet passes to <c>fn:collection()</c>.
    /// Use an empty string for the default collection (called with no arguments).
    /// </param>
    /// <param name="documentPaths">
    /// File system paths to XML documents that comprise the collection.
    /// </param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054")]
    public void SetCollection(string uri, List<string> documentPaths)
    {
        _collections[uri] = documentPaths;
    }

    /// <summary>
    /// Sets a trace listener for debugging template matching, function calls, and
    /// built-in rule invocations during transformation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The callback receives three arguments:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>depth</b> (<see cref="int"/>) — the call-stack depth, useful for indenting trace output.
    /// </description></item>
    /// <item><description>
    /// <b>eventType</b> (<see cref="string"/>) — the kind of event, e.g. <c>"template-match"</c>,
    /// <c>"function-call"</c>, or <c>"built-in-rule"</c>.
    /// </description></item>
    /// <item><description>
    /// <b>details</b> (<see cref="string"/>) — a human-readable description of the event,
    /// including the matched pattern, function name, or rule information.
    /// </description></item>
    /// </list>
    /// <example>
    /// <code>
    /// transformer.TraceListener = (depth, eventType, details) =>
    ///     Console.WriteLine($"{new string(' ', depth * 2)}[{eventType}] {details}");
    /// </code>
    /// </example>
    /// </remarks>
    public Action<int, string, string>? TraceListener { get; set; }

    /// <summary>
    /// Listener for <c>xsl:message</c> output. Receives the message text and a boolean
    /// indicating whether <c>terminate="yes"</c> was specified.
    /// When not set, messages are silently discarded.
    /// </summary>
    public Action<string, bool>? MessageListener { get; set; }

    /// <summary>
    /// Extended listener for <c>xsl:message</c> that also receives source location (line, column).
    /// Takes precedence over <see cref="MessageListener"/> when set.
    /// </summary>
    public Action<string, bool, int, int>? MessageListenerWithLocation { get; set; }

    /// <summary>
    /// Transforms an XML string using the loaded stylesheet and returns the serialized
    /// primary result document.
    /// </summary>
    /// <param name="inputXml">
    /// The source XML document to transform, or <c>null</c> when using call-template
    /// or call-function invocation that does not require a source document.
    /// When <c>null</c>, an empty placeholder document is used internally.
    /// </param>
    /// <param name="ct">
    /// Cancellation token for aborting long-running transformations. When cancelled,
    /// an <see cref="OperationCanceledException"/> is thrown.
    /// </param>
    /// <returns>
    /// The serialized primary result document as a string. The serialization format
    /// (XML, HTML, text, JSON, or adaptive) is determined by the stylesheet's
    /// <c>xsl:output</c> declaration.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// No stylesheet has been loaded. Call <see cref="LoadStylesheetAsync"/> first.
    /// </exception>
    /// <exception cref="XsltException">
    /// A runtime error occurred during transformation, such as a type error,
    /// missing template, or evaluation failure.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The <paramref name="ct"/> cancellation token was triggered.
    /// </exception>
    /// <remarks>
    /// <para>
    /// After this method returns, any secondary outputs produced by
    /// <c>xsl:result-document</c> instructions are available in
    /// <see cref="SecondaryResultDocuments"/>.
    /// </para>
    /// </remarks>
    public async Task<string> TransformAsync(string? inputXml, CancellationToken ct = default)
    {
        if (_stylesheet == null)
            throw new InvalidOperationException("No stylesheet loaded. Call LoadStylesheetAsync first.");

        var options = BuildTransformOptions(hasSource: inputXml != null, rawBox: null, ct: ct);

        var engine = new XsltTransformEngine(_stylesheet, SchemaProvider);
        // No input — use empty document
        var result = await engine.TransformAsync(inputXml ?? "<empty/>", options).ConfigureAwait(false);

        // If a handler is set, write secondary results to the provided writers
        if (ResultDocumentHandler != null)
        {
            foreach (var (href, content) in engine.SecondaryResultDocuments)
            {
                var writer = ResultDocumentHandler(href);
                await writer.WriteAsync(content).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
            SecondaryResultDocuments = new Dictionary<string, string>();
        }
        else
        {
            SecondaryResultDocuments = engine.SecondaryResultDocuments;
        }
        return result;
    }

    /// <summary>
    /// Transforms <paramref name="inputXml"/> and returns the RAW XDM value of the
    /// transformation — the typed result (xs:boolean, xs:integer, map, array, node, …)
    /// preserved end-to-end, not serialized to a string and reparsed.
    ///
    /// Used by <c>fn:transform()</c> with <c>delivery-format='raw'</c> from XQuery,
    /// where the caller wants to consume the typed result directly rather than as
    /// XML markup. Currently only honored when the transformation is invoked via
    /// <see cref="SetInitialFunction"/> (the only case where there's a single
    /// well-defined return value); template-based invocations still serialize.
    /// </summary>
    /// <returns>
    /// The raw XDM value: a single item, an <c>object?[]</c> for sequences, or
    /// <c>null</c> for the empty sequence.
    /// </returns>
    public async Task<object?> TransformToValueAsync(string? inputXml, CancellationToken ct = default)
    {
        if (_stylesheet == null)
            throw new InvalidOperationException("No stylesheet loaded. Call LoadStylesheetAsync first.");

        var rawBox = new RawResultBox();
        var options = BuildTransformOptions(hasSource: inputXml != null, rawBox: rawBox, ct: ct);

        var engine = new XsltTransformEngine(_stylesheet, SchemaProvider);
        // Engine still produces a serialized output buffer alongside the raw value;
        // we discard the buffer and return the raw value directly.
        _ = await engine.TransformAsync(inputXml ?? "<empty/>", options).ConfigureAwait(false);

        SecondaryResultDocuments = engine.SecondaryResultDocuments;
        return rawBox.Value;
    }

    /// <summary>
    /// Transforms an <see cref="Xdm.XdmSequence"/> source — pass the typed result of one
    /// transformation directly into another, without serializing through XML markup.
    /// The engine reads the sequence's backing <c>Store</c> to navigate any node items.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <paramref name="source"/> is null or empty, the transformation runs without
    /// a principal source document — appropriate for <c>xsl:initial-template</c> /
    /// <c>xsl:initial-function</c> invocation. Otherwise the first node item in the
    /// sequence becomes the principal source.
    /// </para>
    /// <para>
    /// If the sequence contains node items, its <see cref="Xdm.XdmSequence.Store"/> must
    /// be a compatible <see cref="XdmInMemoryStore"/> — typically a sequence produced
    /// by a previous call to <see cref="TransformToSequenceAsync(Xdm.XdmSequence?, CancellationToken)"/>.
    /// </para>
    /// </remarks>
    public async Task<string> TransformAsync(Xdm.XdmSequence? source, CancellationToken ct = default)
    {
        if (_stylesheet == null)
            throw new InvalidOperationException("No stylesheet loaded. Call LoadStylesheetAsync first.");

        var (sourceNode, store) = ExtractSourceFromSequence(source);
        var options = BuildTransformOptions(hasSource: sourceNode != null, rawBox: null, ct: ct);

        var engine = new XsltTransformEngine(_stylesheet, SchemaProvider);
        var result = sourceNode != null
            ? await engine.TransformAsync(sourceNode, options, store).ConfigureAwait(false)
            : await engine.TransformAsync("<empty/>", options).ConfigureAwait(false);

        if (ResultDocumentHandler != null)
        {
            foreach (var (href, content) in engine.SecondaryResultDocuments)
            {
                var writer = ResultDocumentHandler(href);
                await writer.WriteAsync(content).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
            SecondaryResultDocuments = new Dictionary<string, string>();
        }
        else
        {
            SecondaryResultDocuments = engine.SecondaryResultDocuments;
        }
        return result;
    }

    /// <summary>
    /// Transforms an <see cref="Xdm.XdmSequence"/> and returns the raw XDM result wrapped
    /// in another <see cref="Xdm.XdmSequence"/> that carries the engine's node-store, so
    /// the result can be passed directly to another <see cref="TransformAsync(Xdm.XdmSequence?, CancellationToken)"/>
    /// or XQuery call without serialization.
    /// </summary>
    public async Task<Xdm.XdmSequence> TransformToSequenceAsync(Xdm.XdmSequence? source, CancellationToken ct = default)
    {
        if (_stylesheet == null)
            throw new InvalidOperationException("No stylesheet loaded. Call LoadStylesheetAsync first.");

        var (sourceNode, sourceStore) = ExtractSourceFromSequence(source);
        // Use the source's store so result nodes share it; fall back to a fresh one
        // when the source has no nodes (engine will populate it during execution).
        var resultStore = sourceStore ?? new XdmInMemoryStore();

        // Set ReturnRawXdm + RawResult so the initial-function path stores its typed
        // result in the box (otherwise SerializeFunctionResult atomizes it to a string).
        // The initial-template / initial-mode / default paths use TransformRawAsync's
        // own sequence-collection path, which already preserves typed values.
        var rawBox = new RawResultBox();
        var options = BuildTransformOptions(hasSource: sourceNode != null, rawBox: rawBox, ct: ct);

        var engine = new XsltTransformEngine(_stylesheet, SchemaProvider);
        // Use TransformRawAsync, which sets up sequence collection across ALL invocation
        // paths (initial-function, initial-template, initial-mode, default apply-templates)
        // and returns the typed result. The plain TransformAsync path only captures
        // initial-function results into RawResult, leaving template/mode invocations
        // stuck on the text-serializer path.
        var raw = sourceNode != null
            ? await engine.TransformRawAsync(sourceNode, options, resultStore).ConfigureAwait(false)
            : await engine.TransformRawAsync("<empty/>", options).ConfigureAwait(false);

        SecondaryResultDocuments = engine.SecondaryResultDocuments;

        // Prefer the explicit RawResult box (initial-function path) when set; otherwise
        // use TransformRawAsync's return value (template / mode / default paths).
        var typedResult = rawBox.Value ?? raw;

        // Initial-template / initial-mode / default paths whose templates use plain LRE
        // (no <xsl:document> wrapper, no xsl:output method=adaptive/json) come back as a
        // serialized XML string. For the chaining use case the caller wants a navigable
        // node — re-parse the markup into a fresh XdmDocument backed by `resultStore` so
        // downstream TransformAsync(XdmSequence) calls can navigate it.
        if (typedResult is string xml && LooksLikeXml(xml))
        {
            var docNode = TryParseToXdmDocument(xml, resultStore);
            if (docNode != null)
                typedResult = docNode;
        }

        // Normalize raw value into an item list. Single item → 1-item sequence;
        // object?[] → as-is; null → empty sequence.
        return typedResult switch
        {
            null => Xdm.XdmSequence.Empty,
            object?[] arr => Xdm.XdmSequence.FromEngineResult(arr, resultStore),
            _ => Xdm.XdmSequence.FromEngineResult(new[] { typedResult }, resultStore),
        };
    }

    /// <summary>Heuristic: the result starts with a tag, suggesting XML markup.</summary>
    private static bool LooksLikeXml(string s)
    {
        var i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i < s.Length && s[i] == '<';
    }

    /// <summary>
    /// Parses serialized XML markup into an <see cref="Xdm.Nodes.XdmDocument"/> registered
    /// with <paramref name="store"/>. Returns null if parsing fails (the caller falls back
    /// to keeping the raw string).
    /// </summary>
    private static Xdm.Nodes.XdmDocument? TryParseToXdmDocument(string xml, XdmInMemoryStore store)
    {
        try
        {
            // Wrap in a synthetic root so multi-element fragments parse, then extract
            // the first child as the result document element.
            var doc = new System.Xml.XmlDocument { PreserveWhitespace = true };
            doc.LoadXml($"<_root_>{xml}</_root_>");
            var firstChild = doc.DocumentElement?.FirstChild;
            if (firstChild == null)
                return null;

            // Re-load just the first child as a standalone document so ConvertToXdm
            // produces a clean XdmDocument with that element as document-element.
            var inner = new System.Xml.XmlDocument { PreserveWhitespace = true };
            inner.LoadXml(firstChild.OuterXml);
            return Engine.XsltTransformEngine.ConvertToXdm(inner, store);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Pulls the first node item out of a sequence (to use as principal source) and
    /// the store backing the sequence's node items. Returns (null, null) for null
    /// or empty input — the transformation will run source-less.
    /// </summary>
    private static (Xdm.Nodes.XdmNode? node, XdmInMemoryStore? store) ExtractSourceFromSequence(Xdm.XdmSequence? source)
    {
        if (source is null || source.IsEmpty)
            return (null, null);

        Xdm.Nodes.XdmNode? firstNode = null;
        for (var i = 0; i < source.Count; i++)
        {
            if (source[i] is Xdm.Nodes.XdmNode n)
            {
                firstNode = n;
                break;
            }
        }
        if (firstNode == null)
            return (null, null);

        if (source.Store is XdmInMemoryStore store)
            return (firstNode, store);

        // Sequence carries node items but the store is missing or of an incompatible
        // type. The engine needs the matching store to navigate the node's children.
        throw new InvalidOperationException(
            "XdmSequence contains node items but Store is null or not an XdmInMemoryStore. " +
            "Construct the sequence via XsltTransformer.TransformToSequenceAsync (which carries " +
            "the engine's store) or via XdmSequence.OfNode(node, store) with a matching store.");
    }

    /// <summary>
    /// Builds the <see cref="XsltTransformOptions"/> from the transformer's current
    /// configuration (parameters, initial-template / mode / function selection, listeners,
    /// resource policy, preloaded resources). Centralized so the four <c>TransformAsync</c>
    /// / <c>TransformToValueAsync</c> overloads stay in sync as new options are added.
    /// </summary>
    private XsltTransformOptions BuildTransformOptions(bool hasSource, RawResultBox? rawBox, CancellationToken ct)
    {
        var paramDict = new Dictionary<QName, object?>();
        foreach (var (name, value) in _typedParameters)
            paramDict[new QName(NamespaceId.None, name)] = value;

        return new XsltTransformOptions
        {
            InitialTemplate = _initialTemplate != null
                ? ResolveQName(_initialTemplate, _initialTemplateNamespace)
                : null,
            InitialMode = _initialMode != null
                ? ResolveQName(_initialMode, _initialModeNamespace)
                : null,
            InitialFunction = _initialFunction != null
                ? ResolveQName(_initialFunction, _initialFunctionNamespace)
                : null,
            InitialFunctionArguments = _initialFunctionArgs,
            InitialParameters = paramDict,
            InitialTemplateParameters = _initialTemplateParams,
            InitialTunnelParameters = _initialTunnelParams,
            CancellationToken = ct,
            SourceDocumentUri = _sourceDocumentUri,
            HasSourceDocument = hasSource,
            SourceSelect = _sourceSelect,
            InitialModeSelect = _initialModeSelect,
            Collections = _collections.Count > 0 ? _collections : null,
            TraceListener = TraceListener,
            MessageListener = MessageListener,
            MessageListenerWithLocation = MessageListenerWithLocation,
            ResourcePolicy = ResourcePolicy,
            PreloadedResources = PreloadedResources,
            ReturnRawXdm = rawBox != null,
            RawResult = rawBox ?? new RawResultBox(),
        };
    }

    /// <summary>
    /// Transforms XML from a <see cref="TextReader"/> source.
    /// </summary>
    public async Task<string> TransformAsync(TextReader inputXml, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputXml);
        var xml = await inputXml.ReadToEndAsync(ct).ConfigureAwait(false);
        return await TransformAsync(xml, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Transforms XML from a <see cref="Stream"/> source.
    /// </summary>
    public async Task<string> TransformAsync(Stream inputXml, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputXml);
        using var reader = new StreamReader(inputXml, leaveOpen: true);
        return await TransformAsync(reader, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Transforms XML and writes the primary result to a <see cref="TextWriter"/>.
    /// </summary>
    public async Task TransformAsync(string? inputXml, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        var result = await TransformAsync(inputXml, ct).ConfigureAwait(false);
        await output.WriteAsync(result).ConfigureAwait(false);
    }

    /// <summary>
    /// Transforms XML from a <see cref="TextReader"/> and writes to a <see cref="TextWriter"/>.
    /// </summary>
    public async Task TransformAsync(TextReader inputXml, TextWriter output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputXml);
        ArgumentNullException.ThrowIfNull(output);
        var xml = await inputXml.ReadToEndAsync(ct).ConfigureAwait(false);
        var result = await TransformAsync(xml, ct).ConfigureAwait(false);
        await output.WriteAsync(result).ConfigureAwait(false);
    }

    /// <summary>
    /// Transforms XML from a <see cref="Stream"/> and writes to a <see cref="Stream"/>.
    /// </summary>
    public async Task TransformAsync(Stream inputXml, Stream output, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputXml);
        ArgumentNullException.ThrowIfNull(output);
        using var reader = new StreamReader(inputXml, leaveOpen: true);
        var writer = new StreamWriter(output, leaveOpen: true);
        await using (writer.ConfigureAwait(false))
        {
            await TransformAsync(reader, writer, ct).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Optional callback that provides a <see cref="TextWriter"/> for each secondary result document
    /// produced by <c>xsl:result-document</c>. When set, secondary documents are written directly
    /// to the provided writer instead of accumulating in <see cref="SecondaryResultDocuments"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The callback receives the <c>href</c> value from <c>xsl:result-document</c> and must return
    /// a <see cref="TextWriter"/>. The caller is responsible for disposing the writer after
    /// <see cref="TransformAsync(string?, CancellationToken)"/> completes.
    /// </para>
    /// <example>
    /// <code>
    /// transformer.ResultDocumentHandler = href =>
    ///     new StreamWriter(File.Create($"output/{href}"));
    ///
    /// await transformer.TransformAsync(inputXml);
    /// // Secondary documents have been written to files.
    /// // SecondaryResultDocuments will be empty when a handler is set.
    /// </code>
    /// </example>
    /// </remarks>
    public Func<string, TextWriter>? ResultDocumentHandler { get; set; }

    private static QName ResolveQName(string name, string? namespaceUri)
    {
        var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
        if (colonIdx > 0)
        {
            var prefix = name[..colonIdx];
            var localName = name[(colonIdx + 1)..];
            var nsId = namespaceUri != null
                ? StylesheetParser.ResolveNamespaceUri(namespaceUri)
                : NamespaceId.None;
            return new QName(nsId, localName, prefix);
        }
        var nsIdFallback = namespaceUri != null
            ? StylesheetParser.ResolveNamespaceUri(namespaceUri)
            : NamespaceId.None;
        return new QName(nsIdFallback, name);
    }
}

/// <summary>
/// Adapts XQueryParserFacade to IExpressionParser for XSLT stylesheet parsing.
/// </summary>
internal sealed class XQueryExpressionParser : IExpressionParser
{
    // XSLT/XPath retains the namespace axis (deprecated but optional) — let it through.
    // XQuery's XQST0134 only applies when this parser is invoked from a pure XQuery context.
    private readonly XQueryParserFacade _parser = new() { AllowNamespaceAxis = true };

    public XQueryExpression Parse(string expression)
    {
        return _parser.Parse(expression);
    }
}
