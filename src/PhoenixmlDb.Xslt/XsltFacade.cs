using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.XQuery.Parser;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine;

namespace PhoenixmlDb.Xslt;

/// <summary>
/// Public API for XSLT transformations.
/// Provides a simple interface for loading stylesheets and transforming XML.
/// </summary>
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
    /// Secondary result documents produced by xsl:result-document, keyed by href URI.
    /// Populated after each call to TransformAsync.
    /// </summary>
    public IReadOnlyDictionary<string, string> SecondaryResultDocuments { get; private set; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Loads an XSLT stylesheet from a string.
    /// </summary>
    public Task LoadStylesheetAsync(string stylesheetXml, Uri? baseUri = null,
        Dictionary<string, string>? staticParams = null,
        Dictionary<string, List<(string? Version, string FilePath)>>? packageCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(stylesheetXml);
        var exprParser = new XQueryExpressionParser();
        var parser = packageCatalog != null
            ? new StylesheetParser(exprParser, packageCatalog)
            : new StylesheetParser(exprParser);
        _stylesheet = parser.Parse(stylesheetXml, baseUri, staticParams);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sets a stylesheet parameter with a string value.
    /// </summary>
    public void SetParameter(string name, string value)
    {
        // External string parameters are xs:untypedAtomic per XSLT §9.3,
        // enabling automatic promotion to xs:double for arithmetic
        _typedParameters[name] = new Xdm.XsUntypedAtomic(value);
    }

    /// <summary>
    /// Sets a stylesheet parameter with a typed value.
    /// </summary>
    public void SetParameter(string name, object? value)
    {
        _typedParameters[name] = value;
    }

    /// <summary>
    /// Sets an initial template parameter (passed via xsl:with-param to the initial template call).
    /// </summary>
    public void SetInitialTemplateParameter(QName name, object? value)
    {
        _initialTemplateParams[name] = value;
    }

    /// <summary>
    /// Sets an initial template tunnel parameter.
    /// </summary>
    public void SetInitialTunnelParameter(QName name, object? value)
    {
        _initialTunnelParams[name] = value;
    }

    /// <summary>
    /// Sets the initial named template.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054:URI-like parameters should not be strings")]
    public void SetInitialTemplate(string name, string? namespaceUri = null)
    {
        _initialTemplate = name;
        _initialTemplateNamespace = namespaceUri;
    }

    /// <summary>
    /// Sets the initial mode.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054:URI-like parameters should not be strings")]
    public void SetInitialMode(string mode, string? namespaceUri = null)
    {
        _initialMode = mode;
        _initialModeNamespace = namespaceUri;
    }

    /// <summary>
    /// Sets the initial function to call (XSLT 3.0 "call function" invocation).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054:URI-like parameters should not be strings")]
    public void SetInitialFunction(string name, string? namespaceUri = null)
    {
        _initialFunction = name;
        _initialFunctionNamespace = namespaceUri;
    }

    /// <summary>
    /// Adds a positional argument for the initial function call.
    /// </summary>
    public void AddInitialFunctionArgument(object? value)
    {
        _initialFunctionArgs.Add(value);
    }

    /// <summary>
    /// Sets the URI of the source document (for base-uri/document-uri resolution).
    /// </summary>
    public void SetSourceDocumentUri(Uri uri)
    {
        _sourceDocumentUri = uri;
    }

    /// <summary>
    /// Sets an XPath expression to select the initial context node from the source document.
    /// Example: "/doc" selects the document element named "doc" instead of the document root.
    /// </summary>
    public void SetSourceSelect(string select)
    {
        _sourceSelect = select;
    }

    /// <summary>
    /// Sets an XPath expression to determine the initial match selection for the initial mode.
    /// The expression is evaluated with the source document as context, and templates are
    /// applied to each item in the resulting sequence.
    /// </summary>
    public void SetInitialModeSelect(string select)
    {
        _initialModeSelect = select;
    }

    /// <summary>
    /// Registers a named collection of document file paths for fn:collection().
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054")]
    public void SetCollection(string uri, List<string> documentPaths)
    {
        _collections[uri] = documentPaths;
    }

    /// <summary>
    /// Transforms an XML string using the loaded stylesheet.
    /// </summary>
    public async Task<string> TransformAsync(string? inputXml, CancellationToken ct = default)
    {
        if (_stylesheet == null)
            throw new InvalidOperationException("No stylesheet loaded. Call LoadStylesheetAsync first.");

        var paramDict = new Dictionary<QName, object?>();
        foreach (var (name, value) in _typedParameters)
        {
            paramDict[new QName(NamespaceId.None, name)] = value;
        }

        var options = new XsltTransformOptions
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
            HasSourceDocument = inputXml != null,
            SourceSelect = _sourceSelect,
            InitialModeSelect = _initialModeSelect,
            Collections = _collections.Count > 0 ? _collections : null
        };

        var engine = new XsltTransformEngine(_stylesheet);

        string result;
        if (inputXml != null)
        {
            result = await engine.TransformAsync(inputXml, options).ConfigureAwait(false);
        }
        else
        {
            // No input — use empty document
            result = await engine.TransformAsync("<empty/>", options).ConfigureAwait(false);
        }

        SecondaryResultDocuments = engine.SecondaryResultDocuments;
        return result;
    }

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
    private readonly XQueryParserFacade _parser = new();

    public XQueryExpression Parse(string expression)
    {
        return _parser.Parse(expression);
    }
}
