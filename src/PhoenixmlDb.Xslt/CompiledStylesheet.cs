using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;

namespace PhoenixmlDb.Xslt;

/// <summary>
/// The settings that compiling a stylesheet reads, for <see cref="CompiledStylesheet.CompileAsync"/>.
/// The same settings as the properties and parameters of <see cref="XsltTransformer.LoadStylesheetAsync"/>.
/// </summary>
public sealed class XsltCompileOptions
{
    /// <summary>
    /// Whether a DTD in the stylesheet (and in the modules it imports or includes) is processed.
    /// Default: <c>false</c>. See <see cref="XsltTransformer.AllowDtdProcessing"/>.
    /// </summary>
    public bool AllowDtdProcessing { get; init; }

    /// <summary>
    /// Whether the stylesheet is compiled schema-aware. Default: <c>true</c>.
    /// See <see cref="XsltTransformer.SchemaAware"/>.
    /// </summary>
    public bool SchemaAware { get; init; } = true;

    /// <summary>
    /// The policy for the resources that compiling reads: <c>xsl:import</c>, <c>xsl:include</c>,
    /// <c>xsl:use-package</c>, <c>xsl:import-schema</c> and documents read by static expressions.
    /// A transformer from <see cref="CompiledStylesheet.CreateTransformer"/> starts with this
    /// policy for run time too; set <see cref="XsltTransformer.ResourcePolicy"/> on it to change that.
    /// </summary>
    public PhoenixmlDb.XQuery.Security.ResourcePolicy? ResourcePolicy { get; init; }

    /// <summary>
    /// Contents fetched in advance for URIs that compiling would otherwise fetch over HTTP
    /// synchronously. See <see cref="XsltTransformer.PreloadedResources"/>. A transformer from
    /// <see cref="CompiledStylesheet.CreateTransformer"/> uses the same instance at run time, so
    /// do not change it while transformations run.
    /// </summary>
    public PreloadedResources? PreloadedResources { get; init; }

    /// <summary>
    /// XQuery library modules for <c>fn:load-xquery-module</c>, by module namespace URI.
    /// See <see cref="XsltTransformer.XQueryModules"/>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? XQueryModules { get; init; }

    /// <summary>
    /// The time limit for one regular-expression match, at compile time (static expressions,
    /// schema pattern facets) and, for a transformer from
    /// <see cref="CompiledStylesheet.CreateTransformer"/>, at run time.
    /// See <see cref="XsltTransformer.RegexMatchTimeout"/>.
    /// </summary>
    public TimeSpan? RegexMatchTimeout { get; init; }

    /// <summary>
    /// Values for the stylesheet's static parameters (<c>xsl:param static="yes"</c>), by local
    /// name, as XPath literals or plain strings. Each transformer from
    /// <see cref="CompiledStylesheet.CreateTransformer"/> also gets them as global parameters.
    /// </summary>
    public IReadOnlyDictionary<string, string>? StaticParameters { get; init; }

    /// <summary>
    /// The packages that <c>xsl:use-package</c> can load: by package name, the available
    /// versions and their files.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(string? Version, string FilePath)>>? PackageCatalog { get; init; }

    /// <summary>
    /// How to choose among package versions that all satisfy a <c>package-version</c> range.
    /// Default: <see cref="Xslt.PackageVersionResolution.Highest"/>.
    /// </summary>
    public PackageVersionResolution PackageVersionResolution { get; init; } = PackageVersionResolution.Highest;

    /// <summary>
    /// The schema provider that <c>xsl:import-schema</c> loads into and that validation uses at
    /// run time. <c>null</c> (the default): a new provider for this stylesheet, as a new
    /// <see cref="XsltTransformer"/> has. Every transformer from
    /// <see cref="CompiledStylesheet.CreateTransformer"/> uses this provider, on any thread.
    /// </summary>
    public PhoenixmlDb.XQuery.ISchemaProvider? SchemaProvider { get; init; }
}

/// <summary>
/// A stylesheet compiled once, for any number of transformations. It is read only after it is
/// compiled, and all its members are thread-safe: transformers from
/// <see cref="CreateTransformer"/> can use it at the same time, on any threads.
/// </summary>
/// <remarks>
/// <para>
/// An <see cref="XsltTransformer"/> is not thread-safe: it runs one transformation at a time.
/// Use one transformer per thread (or per transformation); creating one is cheap, because the
/// stylesheet is not compiled again.
/// </para>
/// <para>
/// The schema provider is shared too. Two things change it during a transformation, and so are
/// not safe while other transformations of the stylesheet run: a transformer whose
/// <see cref="XsltTransformer.RegexMatchTimeout"/> is lower than the compile option's (the
/// provider's pattern limit is lowered for all its users; set the lower limit in the compile
/// options instead), and <c>fn:load-xquery-module</c> of a module that has an
/// <c>import schema</c>. A provider of your own in <see cref="XsltCompileOptions.SchemaProvider"/>
/// must allow reads from several threads at once.
/// </para>
/// <code>
/// var compiled = await CompiledStylesheet.CompileAsync(xslt, new Uri("file:///app/style.xsl"));
/// // On each thread, or for each request:
/// var transformer = compiled.CreateTransformer();
/// transformer.SetParameter("title", "Report");
/// var html = await transformer.TransformAsync(sourceXml);
/// </code>
/// </remarks>
public sealed class CompiledStylesheet
{
    private readonly XsltCompileOptions _options;

    private CompiledStylesheet(XsltStylesheet stylesheet, Uri? baseUri, XsltCompileOptions options,
        PhoenixmlDb.XQuery.ISchemaProvider? schemaProvider)
    {
        Stylesheet = stylesheet;
        BaseUri = baseUri;
        _options = options;
        SchemaProvider = schemaProvider;
        TemplateIndex = new TemplateIndex(stylesheet);
    }

    /// <summary>The base URI the stylesheet was compiled with.</summary>
    public Uri? BaseUri { get; }

    internal XsltStylesheet Stylesheet { get; }

    /// <summary>The template rules by mode, built once and read by every transformation.</summary>
    internal TemplateIndex TemplateIndex { get; }

    internal PhoenixmlDb.XQuery.ISchemaProvider? SchemaProvider { get; }

    internal XsltCompileOptions Options => _options;

    /// <summary>
    /// Compiles a stylesheet from its XML text.
    /// </summary>
    /// <param name="stylesheetXml">The stylesheet: an <c>xsl:stylesheet</c>, <c>xsl:transform</c> or <c>xsl:package</c> document, or a simplified stylesheet.</param>
    /// <param name="baseUri">The stylesheet's base URI, for relative <c>xsl:import</c>, <c>xsl:include</c> and <c>doc()</c> URIs.</param>
    /// <param name="options">The compile settings; <c>null</c> for the defaults.</param>
    /// <param name="cancellationToken">Cancels the compile before the stylesheet is parsed (while imports are fetched over HTTP, for instance).</param>
    /// <returns>The compiled stylesheet.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stylesheetXml"/> is <c>null</c>.</exception>
    /// <exception cref="XsltException">The stylesheet has a static error.</exception>
    /// <exception cref="PhoenixmlDb.XQuery.Parser.XQueryParseException">An XPath expression in the stylesheet does not parse (as with <see cref="XsltTransformer.LoadStylesheetAsync"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the parse started.</exception>
    /// <remarks>
    /// The parse runs on the engine's large-stack thread, as in
    /// <see cref="XsltTransformer.LoadStylesheetAsync"/>; see it for the thread the caller resumes on.
    /// </remarks>
    public static Task<CompiledStylesheet> CompileAsync(string stylesheetXml, Uri? baseUri = null,
        XsltCompileOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stylesheetXml);
        options ??= new XsltCompileOptions();
        return CompileCoreAsync(stylesheetXml, baseUri, options,
            options.SchemaProvider ?? XsltTransformer.CreateDefaultSchemaProvider(),
            options.StaticParameters is null ? null : new Dictionary<string, string>(options.StaticParameters, StringComparer.Ordinal),
            options.PackageCatalog?.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal),
            sharedByTransformers: true, cancellationToken);
    }

    /// <summary>
    /// Compiles a stylesheet, returning on the calling thread. The synchronous form of
    /// <see cref="CompileAsync"/>; see it for the parameters and errors.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Called on browser WebAssembly, which cannot block.</exception>
    public static CompiledStylesheet Compile(string stylesheetXml, Uri? baseUri = null,
        XsltCompileOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stylesheetXml);
        return XsltTransformer.OnCallersThread(() => CompileAsync(stylesheetXml, baseUri, options));
    }

    /// <summary>
    /// A new transformer for this stylesheet. It starts with no parameters set; its
    /// <see cref="XsltTransformer.ResourcePolicy"/>, <see cref="XsltTransformer.RegexMatchTimeout"/>,
    /// <see cref="XsltTransformer.SchemaProvider"/> and the other compile settings come from the
    /// options the stylesheet was compiled with. Each transformer is used by one thread at a time.
    /// </summary>
    public XsltTransformer CreateTransformer() => new(this);

    /// <summary>
    /// Compiles with an explicit schema provider (which may be <c>null</c>: no schema features)
    /// and the parser's own dictionary types; <see cref="XsltTransformer.LoadStylesheetAsync"/>
    /// passes its own provider and the caller's dictionaries unchanged. With
    /// <c>sharedByTransformers</c>, transformers on several threads will use the result (and so
    /// its schema provider) at the same time: the provider then gets now what a transformation
    /// would otherwise add to it while others read it.
    /// </summary>
    internal static async Task<CompiledStylesheet> CompileCoreAsync(string stylesheetXml, Uri? baseUri,
        XsltCompileOptions options, PhoenixmlDb.XQuery.ISchemaProvider? schemaProvider,
        Dictionary<string, string>? staticParams,
        Dictionary<string, List<(string? Version, string FilePath)>>? packageCatalog,
        bool sharedByTransformers, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Reject a pathologically deep stylesheet up front, before ANY pass touches it: the HTTP
        // import walk, XDocument parse, instruction-tree build, streamability classification and the
        // executor all recurse per element-nesting level and would StackOverflow the host (an
        // uncatchable crash) on untrusted deep input. One iterative pre-scan here bounds them all.
        XsltTransformer.GuardStylesheetNestingDepth(stylesheetXml);

        // Pre-fetch HTTP-resolved xsl:import / xsl:include hrefs into the preload cache
        // BEFORE invoking the synchronous parser. The parser's import resolver consults
        // the cache first; with everything pre-fetched, it never hits the sync HTTP
        // fallback that throws "Cannot wait on monitors" on Blazor WebAssembly.
        // Skip when the host hasn't asked for HTTP imports (no http(s) refs present →
        // walker is cheap; trivially-malformed XML is swallowed and surfaces via the parser).
        var effectivePreload = options.PreloadedResources ?? new PreloadedResources();
        await HttpImportPreloader.PreloadHttpImportsAsync(stylesheetXml, baseUri, effectivePreload,
            options.ResourcePolicy, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var xqueryModules = options.XQueryModules?.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal);
        var exprParser = new XQueryExpressionParser();
        var parser = packageCatalog != null
            ? new StylesheetParser(exprParser, packageCatalog) { AllowDtdProcessing = options.AllowDtdProcessing, SchemaAware = options.SchemaAware, ResourcePolicy = options.ResourcePolicy, PreloadedResources = effectivePreload, VersionResolution = options.PackageVersionResolution, XQueryModules = xqueryModules, RegexMatchTimeout = options.RegexMatchTimeout }
            : new StylesheetParser(exprParser) { AllowDtdProcessing = options.AllowDtdProcessing, SchemaAware = options.SchemaAware, ResourcePolicy = options.ResourcePolicy, PreloadedResources = effectivePreload, VersionResolution = options.PackageVersionResolution, XQueryModules = xqueryModules, RegexMatchTimeout = options.RegexMatchTimeout };

        // The parse, including static expressions (use-when, static variables) that may be slow,
        // runs on the large-stack thread and does NOT hold the calling thread. After the HTTP
        // import fetch above, the calling thread is whichever pool thread completed it, and parsing
        // there held a pool thread for the whole parse whatever the host did.
        var stylesheet = await LargeStack.Run(() =>
        {
            var parsed = parser.Parse(stylesheetXml, baseUri, staticParams);
            XsltTransformer.ResolveSchemaImports(parsed, baseUri, schemaProvider, options.RegexMatchTimeout, options.ResourcePolicy);
            XsltTransformer.RequireKnownSchemaTypes(parsed, schemaProvider);
            // Every name test gets its process-wide namespace id now, so no transformation
            // writes one into the compiled stylesheet later.
            CompiledNameResolution.Resolve(parsed);
            // fn:json-to-xml with validate (and XQuery's fn:analyze-string) types its result with
            // the built-in schemas of the fn namespace, which the provider adds on first use. Added
            // during a transformation, they change the schema set while the other transformations
            // of this stylesheet read it; so a shared stylesheet's provider has them from the start.
            if (sharedByTransformers && schemaProvider is PhoenixmlDb.XQuery.XsdSchemaProvider xsd)
                xsd.TryAddBuiltInFnSchemas();
            return Task.FromResult(parsed);
        }).ConfigureAwait(false);
        return new CompiledStylesheet(stylesheet, baseUri, options, schemaProvider);
    }
}
