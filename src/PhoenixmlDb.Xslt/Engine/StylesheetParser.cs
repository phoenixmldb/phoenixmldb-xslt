using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Parses XSLT stylesheets from XML.
/// </summary>
public sealed class StylesheetParser
{
    private static readonly XNamespace XsltNs = "http://www.w3.org/1999/XSL/Transform";
    private static readonly char[] WhitespaceSeparators = { ' ', '\t', '\n', '\r' };

    private readonly IExpressionParser _expressionParser;
    private Uri? _baseUri;
    private readonly HashSet<string> _loadedStylesheets = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<QName, XElement> _modeElements = new();
    private bool _defaultExpandText;
    /// <summary>
    /// Tracks all mode names referenced by templates and apply-templates in the current stylesheet,
    /// for XTSE3085 validation when declared-modes="yes".
    /// </summary>
    private List<(QName Mode, SourceLocation? Location)>? _usedModeReferences;
    // Process-wide dynamic namespace intern table. ConcurrentDictionary + an Interlocked-managed
    // counter make ResolveNamespaceUri safe under concurrent transforms/parses (#116) — a plain
    // Dictionary with a check-then-act and a non-atomic `_nextNamespaceId++` could corrupt the
    // table (throw / loop), hand the same id to two URIs, or throw "collection modified" when
    // DynamicNamespaces is enumerated during a concurrent write.
    private static readonly ConcurrentDictionary<string, NamespaceId> _dynamicNamespaces = new();
    // Holds the LAST-allocated dynamic id; Interlocked.Increment hands out the next. Starts one
    // below FirstUserNamespaceId so the first allocation is exactly FirstUserNamespaceId.
    private static uint _nextNamespaceId = NamespaceId.FirstUserNamespaceId - 1;

    /// <summary>
    /// Current XElement context for namespace resolution (set during instruction parsing).
    /// </summary>
    private XElement? _nsContext;

    /// <summary>
    /// Current effective default mode for apply-templates/apply-imports/next-match.
    /// Set by the default-mode attribute on xsl:stylesheet, XSLT instructions,
    /// or xsl:default-mode on literal result elements.
    /// null means #unnamed mode (the default).
    /// </summary>
    private QName? _currentDefaultMode;

    /// <summary>
    /// Extension element namespace URIs in scope during parsing.
    /// Includes both stylesheet-level and locally-scoped (from extension-element-prefixes on LREs/XSLT elements).
    /// </summary>
    private readonly HashSet<string> _extensionNamespaces = new();

    /// <summary>
    /// Reference to the stylesheet currently being parsed, for access to attribute sets
    /// during streamability checking of xsl:source-document.
    /// </summary>
    private XsltStylesheet? _currentStylesheet;

    /// <summary>
    /// Whether the current stylesheet has xsl:import-schema.
    /// When true, XTSE1660 validation="strict" checks are suppressed
    /// (we accept the declaration but treat strict as strip at runtime).
    /// </summary>
    private bool _hasImportSchema;

    /// <summary>
    /// Tracks template names that were added by MergePackageComponents
    /// (from xsl:use-package). Used to allow consuming stylesheet templates
    /// to overwrite package templates without triggering XTSE0660.
    /// </summary>
    private readonly HashSet<QName> _packageMergedTemplateNames = new();

    /// <summary>
    /// Set to true when parsing an imported module (xsl:import).
    /// xsl:use-package is not permitted in imported modules (XTSE3008).
    /// </summary>
    private bool _insideImportedModule;

    /// <summary>
    /// Checks if validation="strict" or type attributes should be rejected (XTSE1660).
    /// Returns false (suppress error) when xsl:import-schema is present.
    /// </summary>
    private bool ShouldRejectSchemaAware => !_hasImportSchema;

    /// <summary>
    /// Variables available in use-when static context (quantified expression bindings).
    /// </summary>
    private readonly Dictionary<QName, object?> _staticVariables = new();

    /// <summary>
    /// Static variable names (full QName) that were introduced by imported (lower-precedence)
    /// modules. Value is true for xsl:variable, false for xsl:param. Keyed by full QName so that
    /// names like `v:debug` (in a custom namespace) and `debug` (no namespace) are not conflated
    /// — that mistake produced spurious XTSE3450 errors against DocBook xslTNG (`v:debug` in the
    /// docbook variables namespace coexisting with the `debug` static param).
    /// Used for XTSE3450 detection when the importing module re-declares the same variable.
    /// </summary>
    private readonly Dictionary<QName, bool> _importedStaticVarNames = new();

    /// <summary>
    /// Static param names whose values were provided by the calling processor (external params).
    /// These override the select expression and suppress XPST0008 for forward references.
    /// </summary>
    private readonly HashSet<string> _externalStaticParamNames = new();

    /// <summary>
    /// Maps (line, column) to the original element prefix from the source XML.
    /// Built via XmlReader pre-pass since LINQ to XML loses prefix information.
    /// </summary>
    private Dictionary<(int Line, int Col), string>? _elementPrefixMap;

    /// <summary>
    /// Package catalog mapping package name URIs → list of (version, file path) pairs.
    /// Used by xsl:use-package to resolve package references to files.
    /// </summary>
    private readonly Dictionary<string, List<(string? Version, string FilePath)>>? _packageCatalog;

    /// <summary>
    /// When true, DTD processing is allowed in stylesheet loading. Default is false (secure).
    /// </summary>
    public bool AllowDtdProcessing { get; init; }

    /// <summary>
    /// Optional resource policy for controlling xsl:import/xsl:include resolution.
    /// </summary>
    internal PhoenixmlDb.XQuery.Security.ResourcePolicy? ResourcePolicy { get; init; }

    /// <summary>
    /// Optional pre-fetched HTTP imports / includes. Consulted before the parser falls
    /// back to <see cref="HttpResourceLoader.GetStringSync"/>, so callers running on a
    /// runtime that cannot block (Blazor WebAssembly) can pre-fetch async and pass the
    /// content through. See <see cref="PreloadedResources"/> for usage.
    /// </summary>
    internal PreloadedResources? PreloadedResources { get; init; }

    public StylesheetParser(IExpressionParser expressionParser)
    {
        _expressionParser = expressionParser;
    }

    public StylesheetParser(IExpressionParser expressionParser,
        Dictionary<string, List<(string? Version, string FilePath)>>? packageCatalog)
    {
        _expressionParser = expressionParser;
        _packageCatalog = packageCatalog;
    }

    /// <summary>
    /// Resolves a namespace URI to its interned NamespaceId, creating one if needed.
    /// Used by the public API to match template/mode names against compiled stylesheet names.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054:URI-like parameters should not be strings")]
    public static NamespaceId ResolveNamespaceUri(string namespaceUri)
    {
        if (string.IsNullOrEmpty(namespaceUri))
            return NamespaceId.None;
        if (_wellKnownNamespaces.TryGetValue(namespaceUri, out var nsId))
            return nsId;
        // Thread-safe intern: GetOrAdd guarantees a single stored id per URI even when the
        // factory runs concurrently for the same key, and Interlocked.Increment makes id
        // allocation atomic. Under contention the factory may run more than once and skip a
        // few ids — harmless, since ids only need to be unique. (#116)
        return _dynamicNamespaces.GetOrAdd(namespaceUri,
            _ => new NamespaceId(Interlocked.Increment(ref _nextNamespaceId)));
    }

    /// <summary>
    /// All dynamically-registered URI → NamespaceId mappings.
    /// Used by the function library to resolve EQName function calls.
    /// </summary>
    public static IReadOnlyDictionary<string, NamespaceId> DynamicNamespaces => _dynamicNamespaces;

    /// <summary>
    /// Parses an XSLT stylesheet from a string.
    /// </summary>
    public XsltStylesheet Parse(string xml, Uri? baseUri = null, Dictionary<string, string>? externalStaticParams = null,
        bool isLibraryPackage = false)
    {
        ArgumentNullException.ThrowIfNull(xml);
        // Strip leading BOM character (U+FEFF) that may be present when reading
        // UTF-8 files with BOM — XDocument.Parse rejects it as invalid content
        if (xml.Length > 0 && xml[0] == '\uFEFF')
            xml = xml[1..];
        _baseUri = baseUri;
        XDocument doc;
        // Always go through XmlReader when a base URI is available so XElement.BaseUri is
        // populated — that's what diagnostics surface as the originating module path. The
        // DTD-processing branch is the same code path with extra settings.
        if (baseUri != null)
        {
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = AllowDtdProcessing && xml.Contains("<!DOCTYPE", StringComparison.Ordinal)
                    ? System.Xml.DtdProcessing.Parse
                    : System.Xml.DtdProcessing.Prohibit,
                MaxCharactersFromEntities = 1_000_000,
            };
            if (settings.DtdProcessing == System.Xml.DtdProcessing.Parse)
                settings.XmlResolver = new System.Xml.XmlUrlResolver();
            using var reader = System.Xml.XmlReader.Create(new System.IO.StringReader(xml), settings, baseUri.AbsoluteUri);
            doc = XDocument.Load(reader, LoadOptions.SetLineInfo | LoadOptions.SetBaseUri | LoadOptions.PreserveWhitespace);
        }
        else
        {
            doc = XDocument.Parse(xml, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        }
        // Pre-pass with XmlReader to capture original element prefixes.
        // LINQ to XML loses prefix info when multiple prefixes map to the same namespace.
        _elementPrefixMap = BuildElementPrefixMap(xml);

        ResolveShadowAttributes(doc.Root!, externalStaticParams, baseUri);

        // Pre-populate _staticVariables with externally-provided static param values
        // so that ParseStylesheet skips select evaluation for these params
        PopulateExternalStaticParams(externalStaticParams);

        var stylesheet = ParseStylesheet(doc.Root!);
        // Store the package catalog on the stylesheet for fn:transform package-name resolution
        if (_packageCatalog != null)
            stylesheet.PackageCatalog = _packageCatalog;
        // Validate decimal formats at the top level, after all imports have been resolved.
        // This must happen here (not in ParseStylesheet) because imported stylesheets may have
        // same-precedence conflicts that are overridden by higher-precedence declarations.
        ValidateDecimalFormats(stylesheet);
        ValidateAttributeSetReferences(stylesheet);
        if (!isLibraryPackage)
            ValidateNoAbstractComponents(stylesheet);
        ValidateOutputCharacterMapReferences(stylesheet);
        ValidatePatternFunctionReferences(stylesheet);
        ValidateStreamableTemplates(stylesheet);
        return stylesheet;
    }

    private static Dictionary<(int Line, int Col), string>? BuildElementPrefixMap(string xml)
    {
        Dictionary<(int, int), string>? map = null;
        try
        {
            using var reader = XmlReader.Create(new System.IO.StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore
            });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader is IXmlLineInfo lineInfo && lineInfo.HasLineInfo())
                {
                    // Record EVERY element's source prefix (empty for default-namespace
                    // elements). Recording even the empty case is important: it lets
                    // ParseLiteralResultElement distinguish "this element had no prefix
                    // in source" (lookup hit with empty value → don't propagate any
                    // ancestor's prefix) from "no entry exists for this position"
                    // (lookup miss → fall back to LINQ walk).
                    //
                    // Without the empty-case recording, a default-ns element would miss
                    // the map and fall through to the LINQ walk, which can return a
                    // non-empty prefix because LINQ's GetPrefixOfNamespace finds ANY
                    // ancestor xmlns:* declaration matching the element's namespace —
                    // even when the element itself had no prefix in source. That bug
                    // surfaced as Martin Honnen's `<theme>` and later `<link>` LREs
                    // being serialized as `<xsl:theme>` / `<xsl:link>` in Docbook TNG.
                    map ??= new Dictionary<(int, int), string>();
                    map[(lineInfo.LineNumber, lineInfo.LinePosition)] = reader.Prefix ?? "";
                    // Also record attribute prefixes (needed when multiple prefixes share same URI)
                    if (reader.HasAttributes)
                    {
                        for (int i = 0; i < reader.AttributeCount; i++)
                        {
                            reader.MoveToAttribute(i);
                            if (!string.IsNullOrEmpty(reader.Prefix) && !reader.IsDefault
                                && reader.Prefix != "xmlns"
                                && reader is IXmlLineInfo attrLineInfo && attrLineInfo.HasLineInfo())
                            {
                                map[(attrLineInfo.LineNumber, attrLineInfo.LinePosition)] = reader.Prefix;
                            }
                        }
                        reader.MoveToElement();
                    }
                }
            }
        }
        catch (XmlException)
        {
            // If XmlReader fails (e.g. DTD issues), fall back to LINQ to XML prefix resolution
        }
        return map;
    }

    /// <summary>
    /// Parses an XSLT stylesheet from a stream.
    /// </summary>
    public XsltStylesheet Parse(Stream stream, Uri? baseUri = null, Dictionary<string, string>? externalStaticParams = null)
    {
        _baseUri = baseUri;
        var doc = XDocument.Load(stream, LoadOptions.SetLineInfo);
        ResolveShadowAttributes(doc.Root!, externalStaticParams);

        // Pre-populate _staticVariables with externally-provided static param values
        // so that ParseStylesheet skips select evaluation for these params
        PopulateExternalStaticParams(externalStaticParams);

        var stylesheet = ParseStylesheet(doc.Root!);
        ValidateDecimalFormats(stylesheet);
        ValidateAttributeSetReferences(stylesheet);
        ValidatePatternFunctionReferences(stylesheet);
        ValidateStreamableTemplates(stylesheet);
        return stylesheet;
    }

    /// <summary>
    /// Pre-populates _staticVariables and _externalStaticParamNames from externally-provided
    /// static param values. This allows ParseStylesheet to skip select evaluation and
    /// forward-reference error checking for params whose values come from the calling processor.
    /// </summary>
    private void PopulateExternalStaticParams(Dictionary<string, string>? externalStaticParams)
    {
        if (externalStaticParams == null) return;
        foreach (var (name, value) in externalStaticParams)
        {
            var qname = new QName(NamespaceId.None, name);
            var val = value.Trim();
            // CLI-friendly value parsing. Order matters: literal-quoted strings first so a
            // value like "'true'" stays a string. Then XPath-shaped literals (true()/false()/()),
            // then bare booleans (the typical command-line spelling), then numerics. Anything
            // else falls through as an xs:untypedAtomic-like raw string — the static-param
            // consumer (use-when, shadow attrs) coerces via boolean()/number() at use time.
            if ((val.StartsWith('\'') && val.EndsWith('\'')) || (val.StartsWith('"') && val.EndsWith('"')))
                _staticVariables[qname] = val[1..^1];
            else if (val is "true()" or "false()")
                _staticVariables[qname] = val == "true()" ? (object)true : false;
            else if (val == "()")
                _staticVariables[qname] = null;
            else if (val.Equals("true", StringComparison.Ordinal) || val.Equals("false", StringComparison.Ordinal))
                _staticVariables[qname] = val == "true";
            else if (long.TryParse(val, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l))
                _staticVariables[qname] = l;
            else if (double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                _staticVariables[qname] = d;
            else
                _staticVariables[qname] = val;
            _externalStaticParamNames.Add(name);
        }
    }

    /// <summary>
    /// Parses an XSLT stylesheet from an XElement.
    /// </summary>
    public XsltStylesheet ParseStylesheet(XElement element) => ParseStylesheet(element, isTopLevel: true);

    private XsltStylesheet ParseStylesheet(XElement element, bool isTopLevel)
    {
        ArgumentNullException.ThrowIfNull(element);

        // Accept xsl:stylesheet, xsl:transform, and xsl:package (XSLT 3.0)
        if (element.Name != XsltNs + "stylesheet" && element.Name != XsltNs + "transform" && element.Name != XsltNs + "package")
        {
            // Simplified stylesheet (single template)
            return ParseSimplifiedStylesheet(element);
        }

        // Scope mode element tracking to this stylesheet level (for XTSE0545 conflict detection)
        var prevModeElements = _modeElements;
        _modeElements = new Dictionary<QName, XElement>();

        var versionAttr = element.Attribute("version");
        if (versionAttr == null && !element.Attributes().Any(a => a.Name.LocalName == "_version"))
            throw new XsltException("XTSE0010: The 'version' attribute is required on xsl:stylesheet/xsl:transform", GetSourceLocation(element));
        var version = string.IsNullOrWhiteSpace(versionAttr?.Value) ? "3.0" : versionAttr.Value;

        // XTSE0110: The version attribute must be a valid xs:decimal
        if (versionAttr != null && !string.IsNullOrWhiteSpace(versionAttr.Value))
            ValidateDecimalValue(versionAttr.Value, "XTSE0110", "version", GetSourceLocation(element));

        // XTSE0125: Validate default-collation contains a recognized collation URI
        var defaultCollationAttr = element.Attribute("default-collation");
        if (defaultCollationAttr != null)
            ValidateCollationList(defaultCollationAttr.Value, GetSourceLocation(element));

        // Check for expand-text at stylesheet level
        var expandTextAttr = element.Attribute(XsltNs + "expand-text") ?? element.Attribute("expand-text");
        _defaultExpandText = expandTextAttr?.Value is "yes" or "1" or "true";

        var xpathDefaultNs = element.Attribute("xpath-default-namespace")?.Value;

        // Parse default-mode on the stylesheet root element
        var defaultModeAttr = element.Attribute("default-mode");
        QName? stylesheetDefaultMode = null;
        if (defaultModeAttr != null && defaultModeAttr.Value != "#unnamed")
        {
            _nsContext = element;
            stylesheetDefaultMode = ParseQName(defaultModeAttr.Value, element);
            _nsContext = null;
        }
        _currentDefaultMode = stylesheetDefaultMode;

        // XTSE0090: Validate no unknown attributes on stylesheet/transform/package
        // xsl:package also allows: name, package-version, declared-modes
        ValidateAllowedAttributes(element, GetSourceLocation(element),
            "id", "input-type-annotations", "name", "package-version", "declared-modes");

        // XTSE1660: Non-schema-aware processor must reject default-validation="strict"
        var defaultValidationAttr = element.Attribute("default-validation");
        if (defaultValidationAttr != null && defaultValidationAttr.Value.Trim() is "strict")
            throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept default-validation=\"{defaultValidationAttr.Value.Trim()}\"",
                GetSourceLocation(element));

        // Parse input-type-annotations
        var inputTypeAnnotationsAttr = element.Attribute("input-type-annotations");
        var inputTypeAnnotations = Ast.TypeAnnotations.Unspecified;
        if (inputTypeAnnotationsAttr != null)
        {
            inputTypeAnnotations = inputTypeAnnotationsAttr.Value.Trim() switch
            {
                "strip" => Ast.TypeAnnotations.Strip,
                "preserve" => Ast.TypeAnnotations.Preserve,
                "unspecified" => Ast.TypeAnnotations.Unspecified,
                _ => throw new XsltException($"XTSE0020: Invalid value '{inputTypeAnnotationsAttr.Value}' for input-type-annotations. Must be 'strip', 'preserve', or 'unspecified'",
                    GetSourceLocation(element))
            };
        }

        // Parse declared-modes and detect xsl:package
        var isPackage = element.Name == XsltNs + "package";

        // XTSE0090: package-version is only allowed on xsl:package
        // In forwards-compatibility mode (version > 3.0), unknown attributes are tolerated
        if (!isPackage && element.Attribute("package-version") != null
            && decimal.TryParse(version, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var versionNum)
            && versionNum <= 3.0m)
            throw new XsltException("XTSE0090: The 'package-version' attribute is not allowed on xsl:stylesheet/xsl:transform (only on xsl:package)",
                GetSourceLocation(element));

        var declaredModesAttr = element.Attribute("declared-modes");
        // For xsl:package, declared-modes defaults to true; for xsl:stylesheet, defaults to false
        var declaredModes = isPackage;
        if (declaredModesAttr != null)
            declaredModes = declaredModesAttr.Value.Trim() is "yes" or "1" or "true";

        var stylesheet = new XsltStylesheet
        {
            Version = version,
            XpathDefaultNamespace = string.IsNullOrEmpty(xpathDefaultNs) ? null : xpathDefaultNs,
            DefaultMode = stylesheetDefaultMode,
            DefaultCollation = ResolveDefaultCollation(defaultCollationAttr?.Value),
            BaseUri = ResolveEffectiveBaseUri(element) ?? _baseUri,
            InputTypeAnnotations = inputTypeAnnotations,
            IsPackage = isPackage,
            DeclaredModes = declaredModes
        };
        _currentStylesheet = stylesheet;

        // Enable mode reference tracking for declared-modes validation
        var prevModeReferences = _usedModeReferences;
        if (declaredModes)
            _usedModeReferences = new List<(QName, SourceLocation?)>();

        // Pre-scan for xsl:import-schema to suppress XTSE1660 when present
        _hasImportSchema = element.Elements(XsltNs + "import-schema").Any();

        // Parse namespace declarations from root and all descendant elements
        // so xs:QName() type constructor can resolve prefixes at runtime
        foreach (var ns in element.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            var prefix = ns.Name.LocalName == "xmlns" ? "" : ns.Name.LocalName;
            stylesheet.Namespaces[prefix] = ns.Value;
        }
        foreach (var desc in element.Descendants())
        {
            foreach (var ns in desc.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                var prefix = ns.Name.LocalName == "xmlns" ? "" : ns.Name.LocalName;
                stylesheet.Namespaces.TryAdd(prefix, ns.Value);
            }
        }

        // Parse exclude-result-prefixes
        var excludeAttr = element.Attribute("exclude-result-prefixes");
        if (excludeAttr != null)
        {
            foreach (var prefix in excludeAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (prefix == "#all")
                {
                    // §7.1.2: #all excludes all namespaces in scope on THIS element.
                    // Expand to actual prefixes so LRE-only namespaces are not affected.
                    foreach (var ns in element.Attributes().Where(a => a.IsNamespaceDeclaration))
                    {
                        var p = ns.Name.LocalName == "xmlns" ? "" : ns.Name.LocalName;
                        var u = ns.Value;
                        // Skip XSLT namespace (always excluded anyway) and XML namespace
                        if (u == "http://www.w3.org/1999/XSL/Transform"
                            || u == "http://www.w3.org/XML/1998/namespace")
                            continue;
                        if (string.IsNullOrEmpty(p))
                            stylesheet.ExcludeResultPrefixes.Add("#default");
                        else
                            stylesheet.ExcludeResultPrefixes.Add(p);
                    }
                }
                else if (prefix == "#default")
                {
                    // XTSE0809: #default requires a default namespace binding
                    if (string.IsNullOrEmpty(element.GetDefaultNamespace().NamespaceName))
                        throw new XsltException("XTSE0809: The value '#default' is used in exclude-result-prefixes but the element has no default namespace",
                            GetSourceLocation(element));
                    stylesheet.ExcludeResultPrefixes.Add(prefix);
                }
                else
                {
                    // XTSE0808: prefix must have an in-scope namespace binding
                    var ns = element.GetNamespaceOfPrefix(prefix);
                    if (ns == null)
                        throw new XsltException($"XTSE0808: Namespace prefix '{prefix}' used in exclude-result-prefixes is not declared",
                            GetSourceLocation(element));
                    stylesheet.ExcludeResultPrefixes.Add(prefix);
                }
            }
        }

        // XTSE1430: Validate and store extension-element-prefixes
        var extPrefixesAttr = element.Attribute("extension-element-prefixes");
        if (extPrefixesAttr != null)
        {
            foreach (var prefix in extPrefixesAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (prefix == "#default")
                {
                    stylesheet.ExtensionElementPrefixes.Add(prefix);
                    continue;
                }
                var ns = element.GetNamespaceOfPrefix(prefix);
                if (ns == null)
                    throw new XsltException($"XTSE1430: Namespace prefix '{prefix}' used in extension-element-prefixes is not declared",
                        GetSourceLocation(element));
                // XTSE0800: A reserved namespace must not be used as an extension namespace
                var nsUri = ns.NamespaceName;
                if (nsUri is "http://www.w3.org/1999/XSL/Transform"
                    or "http://www.w3.org/2001/XMLSchema"
                    or "http://www.w3.org/2001/XMLSchema-instance"
                    or "http://www.w3.org/XML/1998/namespace")
                    throw new XsltException($"XTSE0800: The namespace '{nsUri}' is a reserved namespace and must not be used as an extension element namespace",
                        GetSourceLocation(element));
                // Store the namespace URI so we can match regardless of prefix used
                stylesheet.ExtensionElementPrefixes.Add(nsUri);
            }
        }

        // Populate parser-level extension namespace tracking from stylesheet-level declarations
        foreach (var extNs in stylesheet.ExtensionElementPrefixes)
            _extensionNamespaces.Add(extNs);

        // XTSE0120: An xsl:stylesheet element must not have any text node children
        foreach (var node in element.Nodes())
        {
            if (node is XText text && !string.IsNullOrWhiteSpace(text.Value))
                throw new XsltException("XTSE0120: An xsl:stylesheet element must not have any text node children",
                    GetSourceLocation(element));
        }

        // Parse children
        foreach (var child in element.Elements())
        {
            // Evaluate use-when BEFORE any processing (spec §3.8 conditional inclusion)
            if (!ShouldIncludeElement(child))
                continue;

            if (child.Name.Namespace != XsltNs)
            {
                // XTSE0130: Child element of xsl:stylesheet with null namespace URI is an error
                if (child.Name.Namespace == XNamespace.None)
                    throw new XsltException($"XTSE0130: Element '{child.Name.LocalName}' in xsl:stylesheet has no namespace URI",
                        GetSourceLocation(child));
                // Check for XSLT elements inside non-XSLT top-level elements
                if (child.Descendants().Any(d => d.Name.Namespace == XsltNs))
                {
                    throw new XsltException($"XTSE0130: XSLT element found inside non-XSLT element '{child.Name.LocalName}'",
                        GetSourceLocation(child));
                }
                continue;
            }

            // Forwards compatibility: if the effective version for this element is > 3.0,
            // it may contain unknown elements that should be silently ignored.
            // The effective version is the per-element version attribute, or the stylesheet version.
            var childVersion = child.Attribute("version")?.Value ?? version;
            var childVersionNum = ParseVersionNumber(childVersion);

            switch (child.Name.LocalName)
            {
                case "import":
                    {
                        ValidateAllowedAttributes(child, GetSourceLocation(child), "href");
                        ValidateEmptyElement(child);
                        // Save current static variables — importing module has higher precedence
                        var savedStaticVars = new Dictionary<QName, object?>(_staticVariables);
                        var savedInsideImported = _insideImportedModule;
                        _insideImportedModule = true;
                        var imported = LoadExternalStylesheet(child);
                        _insideImportedModule = savedInsideImported;
                        // Track which variables were newly added by the import (lower precedence)
                        // Also track whether each is a variable or param for XTSE3450 consistency check
                        var importedParamNames = imported != null
                            ? new HashSet<QName>(imported.Parameters.Where(p => p.Static).Select(p => p.Name))
                            : new HashSet<QName>();
                        foreach (var svName in _staticVariables.Keys)
                        {
                            if (!savedStaticVars.ContainsKey(svName))
                                _importedStaticVarNames[svName] = !importedParamNames.Contains(svName); // true=variable, false=param
                        }
                        // Restore importing module's static variable values (higher precedence wins)
                        foreach (var (svName, svVal) in savedStaticVars)
                            _staticVariables[svName] = svVal;
                        if (imported != null)
                        {
                            stylesheet.Imports.Add(imported);
                        }
                    }
                    break;

                case "include":
                    {
                        ValidateAllowedAttributes(child, GetSourceLocation(child), "href");
                        ValidateEmptyElement(child);
                        var included = LoadExternalStylesheet(child);
                        if (included != null)
                        {
                            // Include: merge all declarations at the same precedence
                            MergeStylesheet(stylesheet, included);
                        }
                    }
                    break;

                case "template":
                    var template = ParseTemplate(child, version);
                    stylesheet.Templates.Add(template);
                    if (template.Name.HasValue)
                    {
                        if (stylesheet.NamedTemplates.TryGetValue(template.Name.Value, out var existingTmpl))
                        {
                            // If the existing template was merged from a package, overwrite silently
                            // (consuming stylesheet's own template takes precedence).
                            // If it was defined in THIS module, that's a genuine duplicate → XTSE0660.
                            if (!_packageMergedTemplateNames.Contains(template.Name.Value))
                                throw new XsltException($"XTSE0660: Duplicate named template '{template.Name.Value.LocalName}'",
                                    GetSourceLocation(child));
                        }
                        stylesheet.NamedTemplates[template.Name.Value] = template;
                    }
                    break;

                case "variable":
                    var variable = ParseVariable(child);
                    if (stylesheet.Variables.Any(v => v.Name.Equals(variable.Name)) ||
                        stylesheet.Parameters.Any(p => p.Name.Equals(variable.Name)))
                        throw new XsltException($"XTSE0630: Duplicate global variable '{variable.Name.LocalName}'",
                            GetSourceLocation(child));
                    stylesheet.Variables.Add(variable);
                    // Track static variables for use-when and shadow attribute resolution
                    if (variable.Static && variable.Select != null)
                    {
                        var varKey = variable.Name;
                        var newVal = EvaluateStaticSelectSafe(variable.Select, child);
                        // XTSE3450: A later higher-precedence static variable conflicts with
                        // an earlier lower-precedence one from an import
                        if (_importedStaticVarNames.TryGetValue(varKey, out var importedIsVariable))
                        {
                            // Inconsistency: one is xsl:variable and the other is xsl:param
                            if (!importedIsVariable) // imported was a param, this is a variable
                                throw new XsltException($"XTSE3450: Static variable '{varKey.LocalName}' is inconsistent with an imported static parameter of the same name",
                                    GetSourceLocation(child));
                            if (_staticVariables.TryGetValue(varKey, out var importedVal) && !Equals(newVal, importedVal))
                                throw new XsltException($"XTSE3450: Static variable '{varKey.LocalName}' has value '{newVal}' which is inconsistent with the imported value '{importedVal}'",
                                    GetSourceLocation(child));
                        }
                        _staticVariables[varKey] = newVal;
                    }
                    break;

                case "param":
                    var param = ParseParam(child, isGlobal: true, allowTunnel: false);
                    if (stylesheet.Parameters.Any(p => p.Name.Equals(param.Name)) ||
                        stylesheet.Variables.Any(v => v.Name.Equals(param.Name)))
                        throw new XsltException($"XTSE0630: Duplicate global parameter '{param.Name.LocalName}'",
                            GetSourceLocation(child));
                    stylesheet.Parameters.Add(param);
                    // Track static params for use-when and shadow attribute resolution
                    if (param.Static && param.Select != null)
                    {
                        var paramKey = param.Name;
                        // Skip select evaluation if the value was provided externally by the calling processor
                        // (external params are passed as bare names, hence keyed by LocalName here).
                        if (_externalStaticParamNames.Contains(paramKey.LocalName))
                            break;
                        var newParamVal = EvaluateStaticSelectSafe(param.Select, child);
                        // XTSE3450: check same as for variables
                        if (_importedStaticVarNames.TryGetValue(paramKey, out var importedParamIsVariable))
                        {
                            // Inconsistency: one is xsl:variable and the other is xsl:param
                            if (importedParamIsVariable) // imported was a variable, this is a param
                                throw new XsltException($"XTSE3450: Static parameter '{paramKey.LocalName}' is inconsistent with an imported static variable of the same name",
                                    GetSourceLocation(child));
                            if (_staticVariables.TryGetValue(paramKey, out var importedParamVal) && !Equals(newParamVal, importedParamVal))
                                throw new XsltException($"XTSE3450: Static parameter '{paramKey.LocalName}' has value '{newParamVal}' which is inconsistent with the imported value '{importedParamVal}'",
                                    GetSourceLocation(child));
                        }
                        _staticVariables[paramKey] = newParamVal;
                    }
                    break;

                case "function":
                    var func = ParseFunction(child);
                    var funcKey = (func.Name, func.Parameters.Count);
                    if (stylesheet.Functions.ContainsKey(funcKey))
                        throw new XsltException($"XTSE0770: Duplicate function declaration '{func.Name.LocalName}' with arity {func.Parameters.Count}",
                            GetSourceLocation(child));
                    stylesheet.Functions[funcKey] = func;
                    break;

                case "key":
                    var key = ParseKey(child, stylesheet.DefaultCollation);
                    // XTSE1222: Duplicate key declarations must have matching composite attribute
                    if (stylesheet.Keys.TryGetValue(key.Name, out var existingKey) && existingKey.Composite != key.Composite)
                        throw new XsltException($"XTSE1222: Conflicting xsl:key declarations for key '{key.Name.LocalName}': composite attribute values differ",
                            GetSourceLocation(child));
                    // XTSE1220: Duplicate key declarations must have the same collation
                    if (existingKey != null && !string.Equals(existingKey.Collation, key.Collation, StringComparison.Ordinal))
                        throw new XsltException($"XTSE1220: Conflicting xsl:key declarations for key '{key.Name.LocalName}': collation values differ ('{existingKey.Collation ?? "codepoint"}' vs '{key.Collation ?? "codepoint"}')",
                            GetSourceLocation(child));
                    if (existingKey != null)
                    {
                        // Multiple key definitions with same name: append to existing
                        existingKey.OtherDefinitions ??= new List<XsltKey>();
                        existingKey.OtherDefinitions.Add(key);
                    }
                    else
                    {
                        stylesheet.Keys[key.Name] = key;
                    }
                    break;

                case "output":
                    stylesheet.Outputs.Add(ParseOutput(child));
                    break;

                case "attribute-set":
                    var attrSet = ParseAttributeSet(child);
                    // Merge same-named attribute sets per XSLT spec
                    if (stylesheet.AttributeSets.TryGetValue(attrSet.Name, out var existing))
                    {
                        // Preserve per-definition parts for correct interleaving
                        existing.Parts ??= new List<XsltAttributeSetPart>
                        {
                            new() { UseAttributeSets = new List<QName>(existing.UseAttributeSets), Attributes = new List<XsltAttribute>(existing.Attributes), BaseUri = existing.BaseUri }
                        };
                        existing.Parts.Add(new XsltAttributeSetPart
                        {
                            UseAttributeSets = attrSet.UseAttributeSets,
                            Attributes = attrSet.Attributes,
                            BaseUri = attrSet.BaseUri
                        });
                        // Also maintain flat lists for backwards compat / simple cases
                        existing.Attributes.AddRange(attrSet.Attributes);
                        foreach (var u in attrSet.UseAttributeSets)
                        {
                            if (!existing.UseAttributeSets.Contains(u))
                                existing.UseAttributeSets.Add(u);
                        }
                    }
                    else
                    {
                        stylesheet.AttributeSets[attrSet.Name] = attrSet;
                    }
                    break;

                case "character-map":
                    var charMap = ParseCharacterMap(child);
                    // XTSE1580: Duplicate character map names at same import precedence
                    if (stylesheet.CharacterMaps.ContainsKey(charMap.Name))
                        throw new XsltException($"XTSE1580: Duplicate xsl:character-map declarations for name '{charMap.Name.LocalName}' at the same import precedence",
                            GetSourceLocation(child));
                    stylesheet.CharacterMaps[charMap.Name] = charMap;
                    break;

                case "decimal-format":
                    var decFormat = ParseDecimalFormat(child);
                    // Use empty QName for the default (unnamed) decimal format
                    var decFormatKey = decFormat.Name ?? new QName(NamespaceId.None, "");
                    if (stylesheet.DecimalFormats.TryGetValue(decFormatKey, out var existingDf))
                    {
                        // Merge: for each attribute, use the explicitly-set value from the new declaration,
                        // falling back to the existing declaration's value.
                        stylesheet.DecimalFormats[decFormatKey] = MergeDecimalFormats(existingDf, decFormat, child);
                    }
                    else
                    {
                        stylesheet.DecimalFormats[decFormatKey] = decFormat;
                    }
                    break;

                case "strip-space":
                    ValidateAllowedAttributes(child, GetSourceLocation(child), "elements");
                    ValidateEmptyElement(child);
                    var stripElements = child.Attribute("elements")?.Value ?? "";
                    foreach (var name in stripElements.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        stylesheet.StripSpace.Add(ParseNameTest(name, child));
                    }
                    break;

                case "preserve-space":
                    ValidateAllowedAttributes(child, GetSourceLocation(child), "elements");
                    ValidateEmptyElement(child);
                    var preserveElements = child.Attribute("elements")?.Value ?? "";
                    foreach (var name in preserveElements.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        stylesheet.PreserveSpace.Add(ParseNameTest(name, child));
                    }
                    break;

                case "namespace-alias":
                {
                    var stylesheetPrefix = child.Attribute("stylesheet-prefix")?.Value;
                    var resultPrefix = child.Attribute("result-prefix")?.Value;
                    // XTSE0010: Both stylesheet-prefix and result-prefix are required
                    if (stylesheetPrefix == null)
                        throw new XsltException("XTSE0010: xsl:namespace-alias requires a stylesheet-prefix attribute",
                            GetSourceLocation(child));
                    if (resultPrefix == null)
                        throw new XsltException("XTSE0010: xsl:namespace-alias requires a result-prefix attribute",
                            GetSourceLocation(child));
                    {
                        // XTSE0812: Validate prefixes are declared
                        if (stylesheetPrefix != "#default" && child.GetNamespaceOfPrefix(stylesheetPrefix) == null)
                            throw new XsltException($"XTSE0812: Namespace prefix '{stylesheetPrefix}' used in stylesheet-prefix of xsl:namespace-alias is not declared",
                                GetSourceLocation(child));
                        if (resultPrefix != "#default" && child.GetNamespaceOfPrefix(resultPrefix) == null)
                            throw new XsltException($"XTSE0812: Namespace prefix '{resultPrefix}' used in result-prefix of xsl:namespace-alias is not declared",
                                GetSourceLocation(child));

                        // Resolve the stylesheet prefix to its namespace URI
                        string stylesheetNsUri;
                        if (stylesheetPrefix == "#default")
                            stylesheetNsUri = child.GetDefaultNamespace().NamespaceName;
                        else
                            stylesheetNsUri = child.GetNamespaceOfPrefix(stylesheetPrefix)?.NamespaceName ?? "";

                        // Resolve the result prefix to its namespace URI
                        string resultNsUri;
                        string outputPrefix;
                        if (resultPrefix == "#default")
                        {
                            resultNsUri = child.GetDefaultNamespace().NamespaceName;
                            outputPrefix = "";
                        }
                        else
                        {
                            resultNsUri = child.GetNamespaceOfPrefix(resultPrefix)?.NamespaceName ?? "";
                            outputPrefix = resultPrefix;
                        }

                        // XTSE0810: Conflicting namespace-alias at same import precedence
                        if (stylesheet.NamespaceAliases.TryGetValue(stylesheetNsUri, out var existingAlias)
                            && existingAlias.ResultUri != resultNsUri)
                            throw new XsltException($"XTSE0810: Conflicting xsl:namespace-alias declarations for namespace '{stylesheetNsUri}' at the same import precedence",
                                GetSourceLocation(child));

                        stylesheet.NamespaceAliases[stylesheetNsUri] = (resultNsUri, outputPrefix);
                    }
                    break;
                }

                case "accumulator":
                    var acc = ParseAccumulator(child);
                    // Track duplicates within the same module for XTSE3350 detection.
                    // Don't throw yet — import precedence may resolve the conflict.
                    if (stylesheet.Accumulators.ContainsKey(acc.Name))
                        stylesheet.DuplicateAccumulatorNames.Add(acc.Name);
                    stylesheet.Accumulators[acc.Name] = acc;
                    break;

                case "mode":
                    var mode = ParseMode(child);
                    // Use empty QName for the unnamed (default) mode
                    var modeKey = mode.Name ?? new QName(NamespaceId.None, "");
                    // XTSE0545: Check for conflicting mode declarations at same import precedence
                    if (_modeElements.TryGetValue(modeKey, out var prevModeElement))
                    {
                        // Compare explicitly-set attributes between the two declarations (same module)
                        CheckModeAttrConflict(prevModeElement, child, "on-no-match", mode.Name?.LocalName ?? "(unnamed)");
                        CheckModeAttrConflict(prevModeElement, child, "on-multiple-match", mode.Name?.LocalName ?? "(unnamed)");
                        CheckModeAttrConflict(prevModeElement, child, "streamable", mode.Name?.LocalName ?? "(unnamed)");
                        // use-accumulators: defer conflict — may be resolved by higher-precedence import
                        if (UseAccumulatorsConflict(stylesheet.Modes[modeKey], mode))
                            stylesheet.ConflictingModeAccumulators.Add(modeKey);
                        // visibility: defer conflict — may be resolved by higher-precedence import
                        if (VisibilityConflict(stylesheet.Modes[modeKey], mode))
                            stylesheet.ConflictingModeVisibility.Add(modeKey);
                    }
                    else if (stylesheet.Modes.TryGetValue(modeKey, out var mergedMode))
                    {
                        // Mode exists from an included module — defer conflict check
                        if (UseAccumulatorsConflict(mergedMode, mode))
                            stylesheet.ConflictingModeAccumulators.Add(modeKey);
                        if (VisibilityConflict(mergedMode, mode))
                            stylesheet.ConflictingModeVisibility.Add(modeKey);
                    }
                    _modeElements[modeKey] = child;
                    stylesheet.Modes[modeKey] = mode;
                    break;

                // xsl:global-context-item declares constraints on the global context item.
                case "global-context-item":
                {
                    var gciUse = child.Attribute("use")?.Value?.Trim();
                    var gciAs = child.Attribute("as")?.Value?.Trim();
                    // XTSE3089: as + use="absent" is a static error
                    if (gciUse == "absent" && gciAs != null)
                        throw new XsltException("XTSE3089: xsl:global-context-item specifies use='absent' together with an 'as' attribute",
                            GetSourceLocation(child));
                    // XTSE3087: Multiple xsl:global-context-item declarations in the same module
                    if (stylesheet.GlobalContextItemUse != null)
                        throw new XsltException("XTSE3087: A stylesheet module contains more than one xsl:global-context-item declaration",
                            GetSourceLocation(child));
                    // Store the declaration
                    var useValue = gciUse switch
                    {
                        "required" => ContextItemUse.Required,
                        "absent" => ContextItemUse.Absent,
                        _ => ContextItemUse.Optional
                    };
                    stylesheet.GlobalContextItemUse = useValue;
                    if (gciAs != null)
                    {
                        var savedNsContext = _nsContext;
                        _nsContext = child;
                        stylesheet.GlobalContextItemAs = ParseSequenceType(gciAs, child);
                        _nsContext = savedNsContext;
                    }
                    break;
                }

                case "item-type":
                    // XSLT 4.0: xsl:item-type declares a named type alias
                    ParseItemType(child, stylesheet);
                    break;

                case "use-package":
                    if (_insideImportedModule)
                        throw new XsltException("XTSE3008: xsl:use-package is not permitted in an imported stylesheet module",
                            GetSourceLocation(child));
                    ParseUsePackage(child, stylesheet);
                    break;

                case "expose":
                    // Collect expose declarations — applied after all components are parsed
                    stylesheet.ExposeDeclarations.Add(new ExposeDeclaration
                    {
                        Component = child.Attribute("component")?.Value,
                        Names = child.Attribute("names")?.Value,
                        Visibility = ParseVisibility(child.Attribute("visibility")?.Value),
                        Element = child
                    });
                    break;

                case "import-schema":
                    {
                        // Capture the import for runtime resolution against the registered
                        // ISchemaProvider. namespace + schema-location attributes are both
                        // optional per the schema; missing namespace = no-namespace schema.
                        _hasImportSchema = true;
                        var nsAttr = child.Attribute("namespace")?.Value ?? "";
                        var locAttr = child.Attribute("schema-location")?.Value;
                        var locations = !string.IsNullOrWhiteSpace(locAttr)
                            ? locAttr.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries)
                            : Array.Empty<string>();
                        // The xmlns:* declarations on the element provide the prefix binding
                        // for any prefixed schema-element/attribute references later. Capture
                        // the prefix that maps to this namespace, if any.
                        string? prefix = null;
                        foreach (var attr in child.Attributes())
                        {
                            if (attr.IsNamespaceDeclaration && attr.Value == nsAttr)
                            {
                                prefix = attr.Name.LocalName == "xmlns" ? null : attr.Name.LocalName;
                                break;
                            }
                        }
                        stylesheet.SchemaImports.Add(new XsltSchemaImport
                        {
                            TargetNamespace = nsAttr,
                            Prefix = prefix,
                            SchemaLocations = locations,
                            Location = GetSourceLocation(child),
                        });
                    }
                    break;

                default:
                    // Forwards compatibility: if effective version > 3.0, unknown XSLT top-level
                    // declarations are silently ignored (per XSLT spec section 3.8).
                    if (childVersionNum > 3.0m)
                        break;
                    // XTSE0010: An XSLT element appears in a position where it is not permitted
                    throw new XsltException($"XTSE0010: Element xsl:{child.Name.LocalName} is not permitted as a top-level stylesheet element",
                        GetSourceLocation(child));
            }
        }

        // Apply xsl:expose declarations — changes visibility of the package's own components
        if (stylesheet.ExposeDeclarations.Count > 0)
            ApplyExposeDeclarations(stylesheet);

        // XTSE0265: Check for conflicting input-type-annotations across all modules
        foreach (var imported in stylesheet.Imports)
            CheckInputTypeAnnotationsConflict(stylesheet, imported);

        // Merge imported declarations at lower precedence — only at top level.
        // For sub-modules (included/imported), imports are kept separate and
        // transferred to the including module via MergeStylesheet, so the top-level
        // can resolve precedence correctly. Without this, templates from a sub-module's
        // imports would be treated as same-precedence and incorrectly trigger XTSE0660.
        if (isTopLevel)
        {
            // For named templates/functions/keys/variables: later imports have higher precedence,
            // so process in reverse order (TryAdd keeps first = highest precedence).
            // For attribute sets: merge in forward order so higher-precedence attributes come last
            // and override lower-precedence ones. Track which sets are locally defined so that
            // the main module's parts always come last (highest precedence).
            for (var i = stylesheet.Imports.Count - 1; i >= 0; i--)
                MergeImportedNamedDeclarations(stylesheet, stylesheet.Imports[i]);
            var localAttrSetNames = new HashSet<QName>(stylesheet.AttributeSets.Keys);
            foreach (var imported in stylesheet.Imports)
                MergeImportedAttributeSets(stylesheet, imported, localAttrSetNames);

            // Collect outputs from imported stylesheets (lower precedence)
            CollectImportedOutputs(stylesheet, stylesheet, 1);

            // Merge multiple xsl:output declarations with the same (or no) name
            MergeOutputDeclarations(stylesheet);

            // Merge decimal formats from imported stylesheets (lower precedence)
            MergeImportedDecimalFormats(stylesheet);

            // Merge namespace aliases from imported stylesheets (lower precedence)
            MergeImportedNamespaceAliases(stylesheet);
        }

        // Note: ValidateDecimalFormats is called only from the top-level Parse() method,
        // NOT here in ParseStylesheet, because imported stylesheets may have same-precedence
        // conflicts that are resolved by higher-precedence overrides in the importing stylesheet.

        // Conflicting strip-space/preserve-space is a static error (XTSE0270)
        CheckStripSpaceConflicts(stylesheet);

        // Note: ValidateAttributeSetReferences is called from the top-level Parse() method,
        // NOT here in ParseStylesheet, because imported stylesheets may reference attribute sets
        // defined in sibling imports that haven't been merged yet.

        // Validate character map references (XTSE1590, XTSE1600)
        ValidateCharacterMapReferences(stylesheet);

        // XTSE3085: When declared-modes="yes", all used modes must be declared via xsl:mode
        // Only check for packages that set declared-modes themselves — included xsl:stylesheet
        // modules inherit the parent's _usedModeReferences but should not validate against
        // their own (empty) mode declarations.
        if (declaredModes && _usedModeReferences != null)
        {
            var declaredModeNames = new HashSet<QName>(stylesheet.Modes.Keys);
            // The unnamed mode key is QName(None, "")
            var unnamedModeKey = new QName(NamespaceId.None, "");
            foreach (var (modeRef, refLocation) in _usedModeReferences)
            {
                // Map #default sentinel to the unnamed mode key
                var lookupKey = modeRef.Equals(TemplateIndex.DefaultModeSentinel) ? unnamedModeKey : modeRef;
                if (!declaredModeNames.Contains(lookupKey))
                {
                    var modeName = modeRef.Equals(TemplateIndex.DefaultModeSentinel) ? "#unnamed" : modeRef.LocalName;
                    throw new XsltException(
                        $"XTSE3085: Mode '{modeName}' is used but not declared, and declared-modes=\"yes\" is in effect",
                        refLocation);
                }
            }
        }
        _usedModeReferences = prevModeReferences;

        _modeElements = prevModeElements;

        // XTSE3350: Duplicate accumulators in the top-level module are always an error
        // (no higher-precedence import can override them).
        // Only check at the top level — imported modules' duplicates may be resolved by
        // a higher-precedence declaration in the importing module.
        if (isTopLevel && stylesheet.DuplicateAccumulatorNames.Count > 0)
        {
            var dupName = stylesheet.DuplicateAccumulatorNames.First();
            throw new XsltException($"XTSE3350: Duplicate accumulator name '{dupName.LocalName}'");
        }

        // XTSE0545: Unresolved mode use-accumulators conflicts
        if (isTopLevel && stylesheet.ConflictingModeAccumulators.Count > 0)
        {
            var conflictName = stylesheet.ConflictingModeAccumulators.First();
            throw new XsltException(
                $"XTSE0545: Conflicting xsl:mode declarations for mode '{conflictName.LocalName}': attribute 'use-accumulators' has conflicting values at the same import precedence");
        }

        // XTSE0545: Unresolved mode visibility conflicts
        if (isTopLevel && stylesheet.ConflictingModeVisibility.Count > 0)
        {
            var conflictName = stylesheet.ConflictingModeVisibility.First();
            throw new XsltException(
                $"XTSE0545: Conflicting xsl:mode declarations for mode '{conflictName.LocalName}': attribute 'visibility' has conflicting values at the same import precedence");
        }

        return stylesheet;
    }

    /// <summary>
    /// Parses an xsl:use-package declaration, resolves the package from the catalog,
    /// applies overrides, and merges visible components into the consuming stylesheet.
    /// </summary>
    private void ParseUsePackage(XElement element, XsltStylesheet stylesheet)
    {
        var packageName = element.Attribute("name")?.Value
            ?? throw new XsltException("XTSE0010: xsl:use-package requires a 'name' attribute",
                GetSourceLocation(element));
        var packageVersion = element.Attribute("package-version")?.Value;

        // Resolve the package file from the catalog
        if (_packageCatalog == null || !_packageCatalog.TryGetValue(packageName, out var packageEntries))
            throw new XsltException($"XTDE3052: Package '{packageName}' not found",
                GetSourceLocation(element));

        // Version matching
        var packageFile = ResolvePackageVersion(packageEntries, packageVersion, packageName, GetSourceLocation(element));

        // Parse the package stylesheet with a fresh parser sharing our expression parser and catalog
        var packageParser = new StylesheetParser(_expressionParser, _packageCatalog) { AllowDtdProcessing = AllowDtdProcessing, ResourcePolicy = ResourcePolicy, PreloadedResources = PreloadedResources };
        var packageXml = System.IO.File.ReadAllText(packageFile);
        var packageBaseUri = new Uri(Path.GetFullPath(packageFile));
        var packageStylesheet = packageParser.Parse(packageXml, packageBaseUri, isLibraryPackage: true);

        // First pass: apply overrides and collect overridden component names
        var overriddenTemplateNames = new HashSet<QName>();
        var overriddenFunctionKeys = new HashSet<(QName, int)>();
        foreach (var child in element.Elements())
        {
            if (child.Name == XsltNs + "override")
            {
                // Collect names of overridden components for XTSE3051 checking
                foreach (var overChild in child.Elements())
                {
                    if (overChild.Name.Namespace != XsltNs) continue;
                    if (overChild.Name.LocalName == "template")
                    {
                        var nameAttr = overChild.Attribute("name")?.Value;
                        if (nameAttr != null)
                        {
                            _nsContext = overChild;
                            overriddenTemplateNames.Add(ParseQName(nameAttr, overChild));
                            _nsContext = null;
                        }
                    }
                    else if (overChild.Name.LocalName == "function")
                    {
                        var nameAttr = overChild.Attribute("name")?.Value;
                        if (nameAttr != null)
                        {
                            _nsContext = overChild;
                            var funcName = ParseQName(nameAttr, overChild);
                            var arity = overChild.Elements(XsltNs + "param").Count();
                            overriddenFunctionKeys.Add((funcName, arity));
                            _nsContext = null;
                        }
                    }
                }
                ApplyOverrides(child, packageStylesheet);
            }
        }

        // Second pass: apply accept declarations (after overrides, for XTSE3051 checking)
        foreach (var child in element.Elements())
        {
            if (child.Name == XsltNs + "accept")
            {
                ApplyAccept(child, packageStylesheet, overriddenTemplateNames, overriddenFunctionKeys);
            }
        }

        // Merge visible (public/final) components into the consuming stylesheet at lower precedence
        var beforeMerge = new HashSet<QName>(stylesheet.NamedTemplates.Keys);
        MergePackageComponents(stylesheet, packageStylesheet);
        // Track which template names were added by this package merge
        foreach (var name in stylesheet.NamedTemplates.Keys)
        {
            if (!beforeMerge.Contains(name))
                _packageMergedTemplateNames.Add(name);
        }
    }

    /// <summary>
    /// Resolves a package file path from the catalog using version matching.
    /// </summary>
    private static string ResolvePackageVersion(
        List<(string? Version, string FilePath)> entries,
        string? requestedVersion,
        string packageName,
        SourceLocation? location)
    {
        if (entries.Count == 0)
            throw new XsltException($"XTDE3052: Package '{packageName}' has no available versions", location);

        // No version requested — take the first (or only) entry
        if (string.IsNullOrEmpty(requestedVersion))
            return entries[0].FilePath;

        // Try match using catalog version (handles exact, wildcard, and normalized comparison)
        foreach (var (version, filePath) in entries)
        {
            if (VersionMatches(version, requestedVersion))
                return filePath;
        }

        // Fallback: the catalog version may differ from the package file's actual version.
        // Try reading the package-version from the file itself.
        foreach (var (_, filePath) in entries)
        {
            try
            {
                if (!System.IO.File.Exists(filePath)) continue;
                var doc = XDocument.Load(filePath, LoadOptions.None);
                var fileVersion = doc.Root?.Attribute("package-version")?.Value;
                if (fileVersion != null && VersionMatches(fileVersion, requestedVersion))
                    return filePath;
            }
            catch (System.IO.IOException ex) { throw new XsltException($"XTDE3052: Failed to load package '{filePath}': {ex.Message}", location); }
            catch (System.Xml.XmlException ex) { throw new XsltException($"XTDE3052: Failed to parse package '{filePath}': {ex.Message}", location); }
        }

        throw new XsltException($"XTDE3052: No matching version for package '{packageName}' " +
            $"(requested '{requestedVersion}')", location);
    }

    /// <summary>
    /// Public version matching for fn:transform package-version resolution.
    /// </summary>
    public static bool VersionMatchesPublic(string? available, string? requested)
        => VersionMatches(available, requested);

    private static bool VersionMatches(string? available, string? requested)
    {
        if (requested == null || requested == "*") return true;
        if (available == null)
        {
            // Versionless package defaults to version "1"
            available = "1";
        }

        // Version list: "1.0.0, 2.0" — match if any element matches
        if (requested.Contains(',', StringComparison.Ordinal))
        {
            foreach (var part in requested.Split(','))
            {
                if (VersionMatches(available, part.Trim()))
                    return true;
            }
            return false;
        }

        // Range: "1.5 to 2.5", "to 1.5", "1.5 to" — inclusive range match
        if (requested.StartsWith("to ", StringComparison.Ordinal))
        {
            var high = requested[3..].Trim();
            return CompareVersions(available, high) <= 0;
        }
        var toIndex = requested.IndexOf(" to ", StringComparison.Ordinal);
        if (toIndex >= 0)
        {
            var low = requested[..toIndex].Trim();
            var high = requested[(toIndex + 4)..].Trim();
            if (high.Length == 0)
                return CompareVersions(available, low) >= 0; // "1.5 to" = 1.5 or above
            return CompareVersions(available, low) >= 0 && CompareVersions(available, high) <= 0;
        }

        // Minimum: "1.5+" — version 1.5 or above
        if (requested.EndsWith('+'))
        {
            var min = requested[..^1];
            return CompareVersions(available, min) >= 0;
        }

        // Wildcard suffix matching: "1.*" matches "1.0", "1.5", etc.
        if (requested.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = requested[..^2];
            if (available.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (available.Length == prefix.Length || available[prefix.Length] == '.')
                    return true;
            }
            var normAvail = NormalizeVersion(available);
            if (normAvail.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (normAvail.Length == prefix.Length || normAvail[prefix.Length] == '.')
                    return true;
            }
            return false;
        }

        // Exact match (with normalization)
        return string.Equals(available, requested, StringComparison.Ordinal)
            || NormalizeVersion(available) == NormalizeVersion(requested);
    }

    /// <summary>
    /// Compares two version strings. Supports numeric and pre-release (SemVer-style) comparison.
    /// Returns negative if a &lt; b, zero if equal, positive if a &gt; b.
    /// </summary>
    private static int CompareVersions(string a, string b)
    {
        // Split into numeric part and pre-release part (e.g., "2.0.0-alpha" → "2.0.0" + "alpha")
        var (aNum, aPre) = SplitPreRelease(a);
        var (bNum, bPre) = SplitPreRelease(b);

        // Compare numeric parts
        var aParts = aNum.Split('.');
        var bParts = bNum.Split('.');
        var maxLen = Math.Max(aParts.Length, bParts.Length);
        for (var i = 0; i < maxLen; i++)
        {
            var ai = i < aParts.Length && int.TryParse(aParts[i], out var av) ? av : 0;
            var bi = i < bParts.Length && int.TryParse(bParts[i], out var bv) ? bv : 0;
            if (ai != bi) return ai.CompareTo(bi);
        }

        // Same numeric version — compare pre-release (per SemVer: no pre-release > pre-release)
        if (aPre == null && bPre == null) return 0;
        if (aPre == null) return 1;  // 2.0.0 > 2.0.0-alpha
        if (bPre == null) return -1; // 2.0.0-alpha < 2.0.0
        return string.Compare(aPre, bPre, StringComparison.Ordinal);
    }

    private static (string Numeric, string? PreRelease) SplitPreRelease(string version)
    {
        var idx = version.IndexOf('-', StringComparison.Ordinal);
        return idx >= 0 ? (version[..idx], version[(idx + 1)..]) : (version, null);
    }

    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrEmpty(version)) return "1.0.0";
        var parts = version.Split('.');
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => version
        };
    }

    /// <summary>
    /// Applies xsl:override children to a package stylesheet — replaces matching
    /// components and stores originals for xsl:original resolution.
    /// </summary>
    private void ApplyOverrides(XElement overrideElement, XsltStylesheet packageStylesheet)
    {
        // Track overridden components for XTSE0770 duplicate detection
        var overriddenTemplates = new HashSet<QName>();
        var overriddenFunctions = new HashSet<(QName, int)>();
        var overriddenVariables = new HashSet<QName>();

        // xsl:override may have default-mode attribute — set it for template parsing
        var savedDefaultMode = _currentDefaultMode;
        var overrideDefaultMode = overrideElement.Attribute("default-mode")?.Value;
        if (overrideDefaultMode != null && overrideDefaultMode != "#unnamed")
        {
            _nsContext = overrideElement;
            _currentDefaultMode = ParseQName(overrideDefaultMode, overrideElement);
            _nsContext = null;
        }

        foreach (var child in overrideElement.Elements())
        {
            if (child.Name.Namespace != XsltNs) continue;
            _nsContext = child;

            switch (child.Name.LocalName)
            {
                case "mode":
                case "output":
                case "decimal-format":
                case "namespace-alias":
                case "character-map":
                case "import":
                case "include":
                case "key":
                case "accumulator":
                case "use-package":
                case "expose":
                    throw new XsltException(
                        $"XTSE0010: xsl:{child.Name.LocalName} is not permitted as a child of xsl:override",
                        GetSourceLocation(child));
                case "template":
                {
                    var overrideTemplate = ParseTemplate(child);
                    if (overrideTemplate.Name is { } templateName)
                    {
                        // XTSE0770: Duplicate override of same component
                        if (!overriddenTemplates.Add(templateName))
                            throw new XsltException(
                                $"XTSE0770: Duplicate override of template '{templateName.LocalName}'",
                                GetSourceLocation(child));
                        // Named template override — must match an existing component
                        if (packageStylesheet.NamedTemplates.TryGetValue(templateName, out var original))
                        {
                            // XTSE3060: Can only override public or abstract components
                            if (original.Visibility is Visibility.Private or Visibility.Final)
                                throw new XsltException(
                                    $"XTSE3060: Cannot override template '{templateName.LocalName}' which has visibility '{original.Visibility.ToString().ToUpperInvariant()}'",
                                    GetSourceLocation(child));
                            // XTSE3070: Override signature must be compatible with original
                            ValidateOverrideSignature(overrideTemplate, original, templateName, child);
                            overrideTemplate.OriginalTemplate = original;
                            packageStylesheet.NamedTemplates[templateName] = overrideTemplate;
                        }
                        else
                        {
                            throw new XsltException(
                                $"XTSE3058: Override template '{templateName.LocalName}' does not match any component in the used package",
                                GetSourceLocation(child));
                        }
                    }
                    if (overrideTemplate.Match != null)
                    {
                        // XTSE3440: Template rules in xsl:override must not use #all, #unnamed,
                        // or #default (when default mode is unnamed)
                        foreach (var mode in overrideTemplate.Modes)
                        {
                            if (mode.LocalName is "#all" or "#unnamed" or "#default"
                                || mode.Equals(new QName(NamespaceId.None, "")))
                            {
                                throw new XsltException(
                                    $"XTSE3440: Template rule in xsl:override must not use mode '{mode.LocalName}'",
                                    GetSourceLocation(child));
                            }
                        }
                        if (overrideTemplate.Modes.Count == 0)
                        {
                            // No explicit mode = default mode. Check if the xsl:override element
                            // has a default-mode attribute — if so, templates inherit that mode.
                            var overrideDefMode = overrideElement.Attribute("default-mode")?.Value;
                            if (overrideDefMode == null || overrideDefMode == "#unnamed")
                            {
                                throw new XsltException(
                                    "XTSE3440: Template rule in xsl:override must specify an explicit mode",
                                    GetSourceLocation(child));
                            }
                        }
                        // XTSE3060: Check that the mode used is overridable (not final/private)
                        foreach (var mode in overrideTemplate.Modes)
                        {
                            if (packageStylesheet.Modes.TryGetValue(mode, out var modeDecl))
                            {
                                if (modeDecl.Visibility is Visibility.Final)
                                    throw new XsltException(
                                        $"XTSE3060: Cannot override template rule in mode '{mode.LocalName}' which has visibility 'final'",
                                        GetSourceLocation(child));
                                if (modeDecl.Visibility is Visibility.Private)
                                    throw new XsltException(
                                        $"XTSE3060: Cannot override template rule in mode '{mode.LocalName}' which has visibility 'private'",
                                        GetSourceLocation(child));
                            }
                        }

                        // XTSE3460: apply-imports is not allowed in override template rules
                        if (ContainsApplyImports(overrideTemplate.Body))
                            throw new XsltException(
                                "XTSE3460: xsl:apply-imports is not allowed in a template rule within xsl:override",
                                GetSourceLocation(child));

                        // Template rule override — replace matching templates by mode+match
                        var replaced = false;
                        for (var i = 0; i < packageStylesheet.Templates.Count; i++)
                        {
                            var pkgTemplate = packageStylesheet.Templates[i];
                            if (pkgTemplate.Name != null && overrideTemplate.Name != null
                                && pkgTemplate.Name.Equals(overrideTemplate.Name))
                            {
                                overrideTemplate.OriginalTemplate = pkgTemplate;
                                packageStylesheet.Templates[i] = overrideTemplate;
                                replaced = true;
                                break;
                            }
                        }
                        if (!replaced)
                            packageStylesheet.Templates.Add(overrideTemplate);
                    }
                    break;
                }
                case "function":
                {
                    var overrideFunc = ParseFunction(child);
                    var key = (overrideFunc.Name, overrideFunc.Parameters.Count);
                    // XTSE0770: Duplicate override
                    if (!overriddenFunctions.Add(key))
                        throw new XsltException(
                            $"XTSE0770: Duplicate override of function '{overrideFunc.Name.LocalName}#{overrideFunc.Parameters.Count}'",
                            GetSourceLocation(child));
                    if (packageStylesheet.Functions.TryGetValue(key, out var original))
                    {
                        // XTSE3060: Can only override public or abstract components
                        if (original.Visibility is Visibility.Private or Visibility.Final)
                            throw new XsltException(
                                $"XTSE3060: Cannot override function '{overrideFunc.Name.LocalName}' which has visibility '{original.Visibility.ToString().ToUpperInvariant()}'",
                                GetSourceLocation(child));
                        // XTSE3070: Override signature must be compatible
                        ValidateOverrideFunctionSignature(overrideFunc, original, child);
                        overrideFunc.OriginalFunction = original;
                        packageStylesheet.Functions[key] = overrideFunc;
                    }
                    else
                    {
                        throw new XsltException(
                            $"XTSE3058: Override function '{overrideFunc.Name.LocalName}' does not match any component in the used package",
                            GetSourceLocation(child));
                    }
                    break;
                }
                case "variable":
                {
                    var overrideVar = ParseVariable(child);
                    // Replace matching variable
                    for (var i = 0; i < packageStylesheet.Variables.Count; i++)
                    {
                        if (packageStylesheet.Variables[i].Name.Equals(overrideVar.Name))
                        {
                            packageStylesheet.Variables[i] = overrideVar;
                            break;
                        }
                    }
                    break;
                }
                case "param":
                {
                    var overrideParam = ParseParam(child, isGlobal: true);
                    // Replace matching parameter
                    for (var i = 0; i < packageStylesheet.Parameters.Count; i++)
                    {
                        if (packageStylesheet.Parameters[i].Name.Equals(overrideParam.Name))
                        {
                            packageStylesheet.Parameters[i] = overrideParam;
                            break;
                        }
                    }
                    break;
                }
                case "attribute-set":
                {
                    var overrideAttrSet = ParseAttributeSet(child);
                    packageStylesheet.AttributeSets[overrideAttrSet.Name] = overrideAttrSet;
                    break;
                }
            }
            _nsContext = null;
        }

        // Restore default mode
        _currentDefaultMode = savedDefaultMode;
    }

    /// <summary>
    /// Validates that an overriding template's signature is compatible with the original (XTSE3070).
    /// </summary>
    private static void ValidateOverrideSignature(
        Ast.XsltTemplate overrideTemplate, Ast.XsltTemplate original,
        QName templateName, XElement element)
    {
        var location = GetSourceLocation(element);

        // Return type must match
        if (overrideTemplate.As != null && original.As != null
            && overrideTemplate.As.ItemType != original.As.ItemType)
            throw new XsltException(
                $"XTSE3070: Override template '{templateName.LocalName}' has incompatible return type",
                location);

        // Parameter types must be compatible
        foreach (var overParam in overrideTemplate.Parameters)
        {
            var origParam = original.Parameters.FirstOrDefault(p => p.Name.Equals(overParam.Name));
            if (origParam != null && overParam.As != null && origParam.As != null)
            {
                if (overParam.As.ItemType != origParam.As.ItemType)
                    throw new XsltException(
                        $"XTSE3070: Override template '{templateName.LocalName}' parameter '{overParam.Name.LocalName}' " +
                        $"has incompatible type (override: {overParam.As.ItemType}, original: {origParam.As.ItemType})",
                        location);
            }
            // Check tunnel mismatch
            if (origParam != null && overParam.Tunnel != origParam.Tunnel)
                throw new XsltException(
                    $"XTSE3070: Override template '{templateName.LocalName}' parameter '{overParam.Name.LocalName}' " +
                    $"tunnel attribute does not match original",
                    location);
        }

        // Context-item type must be compatible — adding a type constraint where original has none is incompatible
        if (overrideTemplate.ContextItemAs != null && original.ContextItemAs == null)
            throw new XsltException(
                $"XTSE3070: Override template '{templateName.LocalName}' adds a context-item type constraint not present in original",
                location);
        if (overrideTemplate.ContextItemAs != null && original.ContextItemAs != null
            && overrideTemplate.ContextItemAs.ItemType != original.ContextItemAs.ItemType)
            throw new XsltException(
                $"XTSE3070: Override template '{templateName.LocalName}' has incompatible context-item type",
                location);

        // Context-item use must be compatible — changing use is incompatible
        // (except both Optional which is the default)
        if (overrideTemplate.ContextItemUse != original.ContextItemUse)
            throw new XsltException(
                $"XTSE3070: Override template '{templateName.LocalName}' has incompatible context-item use",
                location);
    }

    /// <summary>
    /// Validates that an overriding function's signature is compatible with the original (XTSE3070).
    /// </summary>
    private static void ValidateOverrideFunctionSignature(
        Ast.XsltFunction overrideFunc, Ast.XsltFunction original, XElement element)
    {
        var location = GetSourceLocation(element);

        // Return type must match
        if (overrideFunc.As != null && original.As != null
            && overrideFunc.As.ItemType != original.As.ItemType)
            throw new XsltException(
                $"XTSE3070: Override function '{overrideFunc.Name.LocalName}' has incompatible return type",
                location);

        // Parameter types must be compatible
        for (var i = 0; i < overrideFunc.Parameters.Count && i < original.Parameters.Count; i++)
        {
            var overParam = overrideFunc.Parameters[i];
            var origParam = original.Parameters[i];
            if (overParam.As != null && origParam.As != null
                && overParam.As.ItemType != origParam.As.ItemType)
                throw new XsltException(
                    $"XTSE3070: Override function '{overrideFunc.Name.LocalName}' parameter '{overParam.Name.LocalName}' " +
                    $"has incompatible type",
                    location);
        }

        // new-each-time (determinism) must be compatible
        var overNewEach = overrideFunc.NewEachTime ?? "yes";
        var origNewEach = original.NewEachTime ?? "yes";
        if (overNewEach != origNewEach)
            throw new XsltException(
                $"XTSE3070: Override function '{overrideFunc.Name.LocalName}' has incompatible determinism " +
                $"(new-each-time: override='{overNewEach}', original='{origNewEach}')",
                location);
    }

    /// <summary>
    /// Checks if a sequence constructor contains xsl:apply-imports (recursively through all children).
    /// </summary>
    private static bool ContainsApplyImports(XsltSequenceConstructor body)
    {
        foreach (var instr in body.Instructions)
        {
            if (ContainsApplyImportsInInstruction(instr))
                return true;
        }
        return false;
    }

    private static bool ContainsApplyImportsInInstruction(Ast.XsltInstruction instr)
    {
        if (instr is Ast.XsltApplyImports) return true;
        // Use reflection-free checks for common instruction types with nested content
        if (instr is Ast.XsltIf ifInstr)
            return ContainsApplyImports(ifInstr.Then);
        if (instr is Ast.XsltChoose chooseInstr)
        {
            foreach (var when in chooseInstr.When)
                if (ContainsApplyImports(when.Body)) return true;
            if (chooseInstr.Otherwise != null && ContainsApplyImports(chooseInstr.Otherwise)) return true;
        }
        if (instr is Ast.XsltLiteralResultElement lre)
            return ContainsApplyImports(lre.Content);
        if (instr is Ast.XsltElement elem && elem.Content != null)
            return ContainsApplyImports(elem.Content);
        if (instr is Ast.XsltForEach forEach)
            return ContainsApplyImports(forEach.Body);
        return false;
    }

    /// <summary>
    /// Applies xsl:accept visibility changes to package components.
    /// Validates that named components exist (XTSE3030) and visibility is compatible (XTSE3040).
    /// </summary>
    private static void ApplyAccept(XElement acceptElement, XsltStylesheet packageStylesheet,
        HashSet<QName>? overriddenTemplateNames = null, HashSet<(QName, int)>? overriddenFunctionKeys = null)
    {
        var component = acceptElement.Attribute("component")?.Value;
        var names = acceptElement.Attribute("names")?.Value;
        var visibilityStr = acceptElement.Attribute("visibility")?.Value;
        if (component == null || names == null || visibilityStr == null) return;

        var visibility = ParseVisibility(visibilityStr);
        var location = GetSourceLocation(acceptElement);

        foreach (var token in names.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            // Wildcards always match — skip validation
            if (token == "*" || token.EndsWith(":*", StringComparison.Ordinal)) continue;

            // XTSE3051: accept must not name a component also in xsl:override
            switch (component)
            {
                case "template":
                {
                    var qname = new QName(NamespaceId.None, token);
                    if (overriddenTemplateNames != null && overriddenTemplateNames.Contains(qname))
                        throw new XsltException(
                            $"XTSE3051: Component '{token}' in xsl:accept is also declared in xsl:override",
                            location);
                    // XTSE3030: must match an existing component
                    if (!packageStylesheet.NamedTemplates.TryGetValue(qname, out var tmpl))
                        throw new XsltException(
                            $"XTSE3030: xsl:accept names template '{token}' which does not exist in the used package",
                            location);
                    // XTSE3040: visibility change must be compatible
                    // Private → anything other than private/hidden is incompatible
                    // Final → public is incompatible (final can't become overridable)
                    if ((tmpl.Visibility == Visibility.Private && visibility is not (Visibility.Private or Visibility.Hidden))
                        || (tmpl.Visibility == Visibility.Final && visibility == Visibility.Public))
                        throw new XsltException(
                            $"XTSE3040: Cannot change visibility of {tmpl.Visibility.ToString().ToUpperInvariant()} template '{token}' to {visibility.ToString().ToUpperInvariant()}",
                            location);
                    break;
                }
                case "function":
                {
                    // Parse function name and optional arity: "p:f1#0" → name="p:f1", arity=0
                    var funcToken = token;
                    var arity = -1;
                    var hashIdx = token.IndexOf('#', StringComparison.Ordinal);
                    if (hashIdx >= 0)
                    {
                        funcToken = token[..hashIdx];
                        _ = int.TryParse(token[(hashIdx + 1)..], out arity);
                    }
                    // Try to resolve and check visibility using namespace from the element
                    try
                    {
                        var colonIdx = funcToken.IndexOf(':', StringComparison.Ordinal);
                        QName funcName;
                        if (colonIdx > 0)
                        {
                            var prefix = funcToken[..colonIdx];
                            var localName = funcToken[(colonIdx + 1)..];
                            var nsUri = acceptElement.GetNamespaceOfPrefix(prefix)?.NamespaceName;
                            var nsId = nsUri != null ? ResolveNamespaceUri(nsUri) : NamespaceId.None;
                            funcName = new QName(nsId, localName, prefix);
                        }
                        else
                        {
                            funcName = new QName(NamespaceId.None, funcToken);
                        }
                        foreach (var (fkey, func) in packageStylesheet.Functions)
                        {
                            if (fkey.Name.Equals(funcName) && (arity < 0 || fkey.Arity == arity))
                            {
                                if ((func.Visibility == Visibility.Private && visibility is not (Visibility.Private or Visibility.Hidden))
                                    || (func.Visibility == Visibility.Final && visibility == Visibility.Public))
                                    throw new XsltException(
                                        $"XTSE3040: Cannot change visibility of {func.Visibility.ToString().ToUpperInvariant()} function '{token}' to {visibility.ToString().ToUpperInvariant()}",
                                        location);
                            }
                        }
                    }
                    catch (XsltException ex) when (ex.Message.Contains("XTSE3040", StringComparison.Ordinal))
                    { throw; }
                    break;
                }
                case "variable":
                {
                    var qname = new QName(NamespaceId.None, token);
                    if (!packageStylesheet.Variables.Any(v => v.Name.LocalName == token))
                        throw new XsltException(
                            $"XTSE3030: xsl:accept names variable '{token}' which does not exist in the used package",
                            location);
                    break;
                }
            }
        }

        // Apply visibility changes for wildcard patterns
        // (wildcards skip validation above but still need to change component visibility)
        foreach (var token in names.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var isWildcard = token == "*" || token.EndsWith(":*", StringComparison.Ordinal);
            var components = component == "*"
                ? new[] { "template", "function", "variable", "attribute-set", "mode" }
                : new[] { component };

            foreach (var comp in components)
            {
                switch (comp)
                {
                    case "template":
                        foreach (var (name, tmpl) in packageStylesheet.NamedTemplates)
                        {
                            if (MatchesAcceptPattern(name, token, isWildcard, acceptElement))
                                packageStylesheet.NamedTemplates[name] = CloneTemplateWithVisibility(tmpl, visibility);
                        }
                        break;
                    case "function":
                        foreach (var (key, func) in packageStylesheet.Functions)
                        {
                            if (MatchesAcceptPattern(key.Name, token, isWildcard, acceptElement))
                                packageStylesheet.Functions[key] = CloneFunctionWithVisibility(func, visibility);
                        }
                        break;
                    case "variable":
                        for (var i = 0; i < packageStylesheet.Variables.Count; i++)
                        {
                            if (MatchesAcceptPattern(packageStylesheet.Variables[i].Name, token, isWildcard, acceptElement))
                                packageStylesheet.Variables[i] = CloneVariableWithVisibility(packageStylesheet.Variables[i], visibility);
                        }
                        break;
                    case "attribute-set":
                        foreach (var (name, attrSet) in packageStylesheet.AttributeSets)
                        {
                            if (MatchesAcceptPattern(name, token, isWildcard, acceptElement))
                                packageStylesheet.AttributeSets[name] = CloneAttributeSetWithVisibility(attrSet, visibility);
                        }
                        break;
                    case "mode":
                        foreach (var (name, mode) in packageStylesheet.Modes)
                        {
                            if (MatchesAcceptPattern(name, token, isWildcard, acceptElement))
                                packageStylesheet.Modes[name] = new Ast.XsltMode
                                {
                                    Name = mode.Name, Streamable = mode.Streamable,
                                    OnNoMatch = mode.OnNoMatch, OnMultipleMatch = mode.OnMultipleMatch,
                                    UseAllAccumulators = mode.UseAllAccumulators,
                                    UseAccumulatorNames = mode.UseAccumulatorNames,
                                    Visibility = visibility,
                                    VisibilityAttr = acceptElement.Attribute("visibility")?.Value,
                                    TypedValueWarnings = mode.TypedValueWarnings, Typed = mode.Typed,
                                    UseAccumulatorsAttr = mode.UseAccumulatorsAttr,
                                };
                        }
                        break;
                }
            }
        }
    }

    private static bool MatchesAcceptPattern(QName name, string pattern, bool isWildcard, XElement element)
    {
        if (pattern == "*") return true;
        if (isWildcard && pattern.EndsWith(":*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2];
            var nsUri = element.GetNamespaceOfPrefix(prefix)?.NamespaceName;
            if (nsUri != null)
            {
                var nsId = ResolveNamespaceUri(nsUri);
                return name.Namespace == nsId;
            }
            return name.Prefix == prefix;
        }
        return name.LocalName == pattern;
    }

    /// <summary>
    /// Merges visible (public/final) components from a package into the consuming stylesheet.
    /// Uses the same TryAdd pattern as import merging — consuming stylesheet declarations take precedence.
    /// </summary>
    /// <summary>
    /// Applies xsl:expose declarations to change visibility of components within the package.
    /// Per XSLT 3.0 §3.6.3, expose changes the visibility of the package's own components
    /// based on component type and name pattern matching.
    /// </summary>
    private void ApplyExposeDeclarations(XsltStylesheet stylesheet)
    {
        foreach (var expose in stylesheet.ExposeDeclarations)
        {
            if (expose.Component == null || expose.Names == null) continue;
            var nameTokens = expose.Names.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
            var visibility = expose.Visibility;

            foreach (var token in nameTokens)
            {
                var isWildcard = token == "*" || token.EndsWith(":*", StringComparison.Ordinal);

                // Determine which component types to process
                var components = expose.Component == "*"
                    ? new[] { "template", "function", "variable", "attribute-set", "mode" }
                    : new[] { expose.Component };

                foreach (var comp in components)
                {
                    switch (comp)
                    {
                        case "template":
                            foreach (var (name, tmpl) in stylesheet.NamedTemplates)
                            {
                                if (MatchesExposePattern(name, token, isWildcard, expose.Element))
                                    stylesheet.NamedTemplates[name] = CloneTemplateWithVisibility(tmpl, visibility);
                            }
                            break;
                        case "function":
                            foreach (var (key, func) in stylesheet.Functions)
                            {
                                if (MatchesExposePattern(key.Name, token, isWildcard, expose.Element))
                                    stylesheet.Functions[key] = CloneFunctionWithVisibility(func, visibility);
                            }
                            break;
                        case "variable":
                            for (var i = 0; i < stylesheet.Variables.Count; i++)
                            {
                                if (MatchesExposePattern(stylesheet.Variables[i].Name, token, isWildcard, expose.Element))
                                    stylesheet.Variables[i] = CloneVariableWithVisibility(stylesheet.Variables[i], visibility);
                            }
                            break;
                        case "attribute-set":
                            foreach (var (name, attrSet) in stylesheet.AttributeSets)
                            {
                                if (MatchesExposePattern(name, token, isWildcard, expose.Element))
                                    stylesheet.AttributeSets[name] = CloneAttributeSetWithVisibility(attrSet, visibility);
                            }
                            break;
                        case "mode":
                            foreach (var (name, mode) in stylesheet.Modes)
                            {
                                if (MatchesExposePattern(name, token, isWildcard, expose.Element))
                                    stylesheet.Modes[name] = new Ast.XsltMode
                                    {
                                        Name = mode.Name,
                                        Streamable = mode.Streamable,
                                        OnNoMatch = mode.OnNoMatch,
                                        OnMultipleMatch = mode.OnMultipleMatch,
                                        UseAllAccumulators = mode.UseAllAccumulators,
                                        UseAccumulatorNames = mode.UseAccumulatorNames,
                                        Visibility = visibility,
                                        VisibilityAttr = expose.Element?.Attribute("visibility")?.Value,
                                        TypedValueWarnings = mode.TypedValueWarnings,
                                        Typed = mode.Typed,
                                        UseAccumulatorsAttr = mode.UseAccumulatorsAttr,
                                    };
                            }
                            break;
                    }
                }
            }
        }
    }

    private bool MatchesExposePattern(QName name, string pattern, bool isWildcard, System.Xml.Linq.XElement? element)
    {
        if (pattern == "*") return true;
        if (isWildcard && pattern.EndsWith(":*", StringComparison.Ordinal))
        {
            // Match by namespace prefix: "p:*" matches all names in the p: namespace
            var prefix = pattern[..^2];
            // Resolve the prefix to a namespace URI
            var nsUri = element?.GetNamespaceOfPrefix(prefix)?.NamespaceName;
            if (nsUri != null)
            {
                var nsId = ResolveNamespaceUri(nsUri);
                return name.Namespace == nsId;
            }
            return name.Prefix == prefix;
        }
        // Exact name match
        if (element != null)
        {
            _nsContext = element;
            try
            {
                var patternQName = ParseQName(pattern, element);
                return name.Equals(patternQName) || name.LocalName == patternQName.LocalName;
            }
            catch (XsltException)
            {
                return name.LocalName == pattern;
            }
            finally { _nsContext = null; }
        }
        return name.LocalName == pattern;
    }

    // Clone helpers — XsltTemplate/Function/Variable use init properties, so we create new instances
    private static Ast.XsltTemplate CloneTemplateWithVisibility(Ast.XsltTemplate t, Ast.Visibility v) => new()
    {
        Name = t.Name, Match = t.Match, Priority = t.Priority, Modes = t.Modes,
        As = t.As, Parameters = t.Parameters, Body = t.Body, Visibility = v,
        UnionGroupId = t.UnionGroupId, Version = t.Version, BaseUri = t.BaseUri,
        DefaultCollation = t.DefaultCollation, ContextItemUse = t.ContextItemUse,
        ContextItemAs = t.ContextItemAs, OriginalTemplate = t.OriginalTemplate
    };

    private static Ast.XsltFunction CloneFunctionWithVisibility(Ast.XsltFunction f, Ast.Visibility v) => new()
    {
        Name = f.Name, As = f.As, Parameters = f.Parameters, Body = f.Body,
        Override = f.Override, Visibility = v, Cache = f.Cache,
        NewEachTime = f.NewEachTime, Streamability = f.Streamability,
        OriginalFunction = f.OriginalFunction
    };

    private static Ast.XsltVariable CloneVariableWithVisibility(Ast.XsltVariable v, Ast.Visibility vis) => new()
    {
        Name = v.Name, As = v.As, Select = v.Select, Content = v.Content,
        Static = v.Static, Visibility = vis, BaseUri = v.BaseUri, Version = v.Version
    };

    private static Ast.XsltAttributeSet CloneAttributeSetWithVisibility(Ast.XsltAttributeSet a, Ast.Visibility v) => new()
    {
        Name = a.Name, UseAttributeSets = a.UseAttributeSets, Attributes = a.Attributes,
        Visibility = v, Streamable = a.Streamable, BaseUri = a.BaseUri, Parts = a.Parts
    };

    private static void MergePackageComponents(XsltStylesheet target, XsltStylesheet package)
    {
        // Stamp all components with their originating package for package-local resolution
        // (decimal formats, keys, character maps, outputs, variables)
        foreach (var (_, func) in package.Functions)
            func.PackageStylesheet ??= package;
        foreach (var (_, tmpl) in package.NamedTemplates)
            tmpl.PackageStylesheet ??= package;
        foreach (var tmpl in package.Templates)
            tmpl.PackageStylesheet ??= package;

        // Named templates: merge all except abstract (private templates may be called
        // by public templates via xsl:call-template from the same package).
        foreach (var (name, template) in package.NamedTemplates)
        {
            if (template.Visibility is not (Visibility.Abstract or Visibility.Hidden))
                target.NamedTemplates.TryAdd(name, template);
        }

        // Template rules: add public/final and overrides
        foreach (var template in package.Templates)
        {
            if (template.Visibility is not (Visibility.Abstract or Visibility.Hidden))
                target.Templates.Add(template);
        }

        // Functions: merge ALL into the function library (private functions may be called
        // by public functions internally). Visibility enforcement happens at the call site.
        foreach (var (key, func) in package.Functions)
        {
            if (func.Visibility is not (Visibility.Abstract or Visibility.Hidden))
                target.Functions.TryAdd(key, func);
        }

        // Variables: merge all except abstract (private variables may be referenced
        // by public functions from the same package)
        foreach (var variable in package.Variables)
        {
            if (variable.Visibility is not (Visibility.Abstract or Visibility.Hidden)
                && !target.Variables.Any(v => v.Name.Equals(variable.Name)))
                target.Variables.Add(variable);
        }

        // Parameters: merge all
        foreach (var param in package.Parameters)
        {
            if (!target.Parameters.Any(p => p.Name.Equals(param.Name)))
                target.Parameters.Add(param);
        }

        // Attribute sets: merge all except abstract (private sets may be referenced
        // by public sets via use-attribute-sets chains)
        foreach (var (name, attrSet) in package.AttributeSets)
        {
            if (attrSet.Visibility is not (Visibility.Abstract or Visibility.Hidden))
                target.AttributeSets.TryAdd(name, attrSet);
        }

        // Keys: merge
        foreach (var (name, key) in package.Keys)
        {
            if (target.Keys.TryGetValue(name, out var existing))
            {
                existing.OtherDefinitions ??= new List<XsltKey>();
                existing.OtherDefinitions.Add(key);
            }
            else
            {
                target.Keys[name] = key;
            }
        }

        // Modes: merge
        foreach (var (key, mode) in package.Modes)
        {
            if (mode.Visibility is Visibility.Public or Visibility.Final)
                target.Modes.TryAdd(key, mode);
        }

        // Strip/preserve space: merge
        target.StripSpace.AddRange(package.StripSpace);
        target.PreserveSpace.AddRange(package.PreserveSpace);

        // Accumulators: merge all
        foreach (var (name, acc) in package.Accumulators)
            target.Accumulators.TryAdd(name, acc);

        // Character maps, decimal formats, namespace aliases, and outputs are
        // package-local per XSLT 3.0 spec — they do NOT cross package boundaries.
        // (use-package-101: "Decimal formats are local to a package")

        // Namespace bindings: merge (needed for function name resolution across packages)
        foreach (var (prefix, uri) in package.Namespaces)
            target.Namespaces.TryAdd(prefix, uri);

        // Extension namespaces: merge
        foreach (var extNs in package.ExtensionElementPrefixes)
            target.ExtensionElementPrefixes.Add(extNs);

        // Global context item: per XSLT 3.0 §3.7.2, xsl:global-context-item in a library package
        // is private to that package — EXCEPT use="required" which causes XTTE0590 at runtime.
        if (package.GlobalContextItemUse == ContextItemUse.Required)
        {
            // Library package declares use="required" — this is an error per spec
            throw new XsltException("XTTE0590: A library package declares xsl:global-context-item with use=\"required\"");
        }
        // Otherwise, only merge if the consuming package has no declaration
        if (target.GlobalContextItemUse == null && package.GlobalContextItemUse != null)
        {
            target.GlobalContextItemUse = package.GlobalContextItemUse;
            target.GlobalContextItemAs = package.GlobalContextItemAs;
        }
    }

    /// <summary>
    /// Merge named templates, functions, keys, and variables from an imported stylesheet.
    /// Called in reverse import order so later imports (higher precedence) are TryAdd'd first.
    /// A stylesheet's own declarations take precedence over its imports, so TryAdd own first.
    /// </summary>
    private static void MergeImportedNamedDeclarations(XsltStylesheet target, XsltStylesheet imported)
    {
        // Add the imported stylesheet's OWN declarations first (higher precedence than its imports)
        foreach (var (name, template) in imported.NamedTemplates)
            target.NamedTemplates.TryAdd(name, template);

        foreach (var (name, func) in imported.Functions)
            target.Functions.TryAdd(name, func);

        foreach (var (name, key) in imported.Keys)
        {
            if (target.Keys.TryGetValue(name, out var existingKey))
            {
                // Merge same-named key definitions per XSLT spec (union of matches)
                existingKey.OtherDefinitions ??= new List<XsltKey>();
                existingKey.OtherDefinitions.Add(key);
                if (key.OtherDefinitions != null)
                    existingKey.OtherDefinitions.AddRange(key.OtherDefinitions);
            }
            else
            {
                target.Keys[name] = key;
            }
        }

        foreach (var variable in imported.Variables)
        {
            if (!target.Variables.Any(v => v.Name.Equals(variable.Name)))
                target.Variables.Add(variable);
        }

        // Merge character maps from imports (TryAdd preserves higher-precedence definitions)
        foreach (var (name, charMap) in imported.CharacterMaps)
            target.CharacterMaps.TryAdd(name, charMap);

        // Merge decimal formats from imports
        foreach (var (name, decFmt) in imported.DecimalFormats)
            target.DecimalFormats.TryAdd(name, decFmt);

        // Merge accumulators from imports (TryAdd preserves higher-precedence definitions)
        foreach (var (name, acc) in imported.Accumulators)
            target.Accumulators.TryAdd(name, acc);

        // XTSE3350: Check for duplicate accumulators in imported module that weren't
        // overridden by a higher-precedence definition in the target
        foreach (var dupName in imported.DuplicateAccumulatorNames)
        {
            if (!target.Accumulators.TryGetValue(dupName, out var existing) || existing == imported.Accumulators[dupName])
                throw new XsltException($"XTSE3350: Duplicate accumulator name '{dupName.LocalName}'");
        }

        // Merge modes from imports (property-level merge with import precedence)
        foreach (var (key, importedMode) in imported.Modes)
        {
            if (!target.Modes.TryGetValue(key, out var existingMode))
            {
                target.Modes[key] = importedMode;
            }
            else
            {
                // Merge: higher-precedence (existing) explicit properties win;
                // fill in unset properties from imported mode
                target.Modes[key] = new XsltMode
                {
                    Name = existingMode.Name,
                    Streamable = existingMode.Streamable || importedMode.Streamable,
                    OnNoMatch = existingMode.OnNoMatch ?? importedMode.OnNoMatch,
                    OnMultipleMatch = existingMode.OnMultipleMatch,
                    UseAllAccumulators = existingMode.UseAllAccumulators || importedMode.UseAllAccumulators,
                    UseAccumulatorNames = existingMode.UseAccumulatorNames.Count > 0
                        ? existingMode.UseAccumulatorNames
                        : importedMode.UseAccumulatorNames,
                    Visibility = existingMode.VisibilityAttr != null ? existingMode.Visibility : importedMode.Visibility,
                    VisibilityAttr = existingMode.VisibilityAttr ?? importedMode.VisibilityAttr,
                    TypedValueWarnings = existingMode.TypedValueWarnings ?? importedMode.TypedValueWarnings,
                    Typed = existingMode.Typed || importedMode.Typed,
                    UseAccumulatorsAttr = existingMode.UseAccumulatorsAttr ?? importedMode.UseAccumulatorsAttr,
                };
                // If the higher-precedence module has explicit use-accumulators,
                // it resolves any conflict from the imported module
                if (existingMode.UseAccumulatorsAttr != null)
                    target.ConflictingModeAccumulators.Remove(key);
                // If the higher-precedence module has explicit visibility,
                // it resolves any conflict from the imported module
                if (existingMode.VisibilityAttr != null)
                    target.ConflictingModeVisibility.Remove(key);
            }
        }

        // Propagate unresolved conflicts from imported stylesheet
        foreach (var conflict in imported.ConflictingModeAccumulators)
        {
            // Only propagate if the target doesn't have its own higher-precedence declaration
            if (!target.Modes.TryGetValue(conflict, out var conflictMode) || conflictMode.UseAccumulatorsAttr == null)
                target.ConflictingModeAccumulators.Add(conflict);
        }

        foreach (var conflict in imported.ConflictingModeVisibility)
        {
            if (!target.Modes.TryGetValue(conflict, out var conflictMode) || conflictMode.VisibilityAttr == null)
                target.ConflictingModeVisibility.Add(conflict);
        }

        // Merge namespace prefix bindings from imported modules
        // (needed for element-available, function-available prefix resolution at runtime)
        foreach (var (prefix, uri) in imported.Namespaces)
            target.Namespaces.TryAdd(prefix, uri);

        // Merge extension element namespaces from imported modules
        foreach (var extNs in imported.ExtensionElementPrefixes)
            target.ExtensionElementPrefixes.Add(extNs);

        // Then recursively merge nested imports (reverse order for precedence among siblings)
        for (var i = imported.Imports.Count - 1; i >= 0; i--)
            MergeImportedNamedDeclarations(target, imported.Imports[i]);
    }

    /// <summary>
    /// Merge attribute sets from an imported stylesheet.
    /// Called in forward import order so higher-precedence attributes come last and override.
    /// localAttrSetNames tracks which attribute sets were defined in the main module itself
    /// (highest precedence). Imported parts are inserted before local parts but after
    /// previously-imported parts to maintain correct import precedence ordering.
    /// </summary>
    private static void MergeImportedAttributeSets(XsltStylesheet target, XsltStylesheet imported, HashSet<QName> localAttrSetNames)
    {
        // Recursively merge nested imports first (forward order)
        foreach (var nestedImport in imported.Imports)
            MergeImportedAttributeSets(target, nestedImport, localAttrSetNames);

        foreach (var (name, attrSet) in imported.AttributeSets)
        {
            if (target.AttributeSets.TryGetValue(name, out var existingSet))
            {
                // Preserve per-definition parts for correct interleaving.
                existingSet.Parts ??= new List<XsltAttributeSetPart>
                {
                    new() { UseAttributeSets = new List<QName>(existingSet.UseAttributeSets), Attributes = new List<XsltAttribute>(existingSet.Attributes) }
                };

                // If the existing set includes a locally-defined part (from the main module),
                // insert imported parts BEFORE it (at Parts.Count - 1) so the local part
                // executes last and wins (highest precedence, last-written wins).
                // If the existing set is entirely from earlier imports, append at the end
                // so later imports (higher precedence) execute last and win.
                var isLocal = localAttrSetNames.Contains(name);
                var insertAt = isLocal ? existingSet.Parts.Count - 1 : existingSet.Parts.Count;

                if (attrSet.Parts != null)
                    existingSet.Parts.InsertRange(insertAt, attrSet.Parts);
                else
                    existingSet.Parts.Insert(insertAt, new XsltAttributeSetPart { UseAttributeSets = attrSet.UseAttributeSets, Attributes = attrSet.Attributes });

                existingSet.Attributes.AddRange(attrSet.Attributes);
                foreach (var u in attrSet.UseAttributeSets)
                {
                    if (!existingSet.UseAttributeSets.Contains(u))
                        existingSet.UseAttributeSets.Add(u);
                }
            }
            else
            {
                target.AttributeSets[name] = attrSet;
            }
        }
    }

    /// <summary>
    /// Merges multiple xsl:output declarations with the same name per XSLT spec.
    /// Later declarations override earlier ones for each attribute; cdata-section-elements are unioned.
    /// </summary>
    /// <summary>
    /// XTSE0265: It is a static error if one stylesheet module specifies input-type-annotations="strip"
    /// and another specifies input-type-annotations="preserve".
    /// </summary>
    private static void CheckInputTypeAnnotationsConflict(XsltStylesheet target, XsltStylesheet source)
    {
        if (target.InputTypeAnnotations != Ast.TypeAnnotations.Unspecified
            && source.InputTypeAnnotations != Ast.TypeAnnotations.Unspecified
            && target.InputTypeAnnotations != source.InputTypeAnnotations)
        {
            var targetVal = target.InputTypeAnnotations == Ast.TypeAnnotations.Strip ? "strip" : "preserve";
            var sourceVal = source.InputTypeAnnotations == Ast.TypeAnnotations.Strip ? "strip" : "preserve";
            throw new XsltException(
                $"XTSE0265: Conflicting input-type-annotations: one module specifies '{targetVal}' and another specifies '{sourceVal}'");
        }
    }

    private static void CheckStripSpaceConflicts(XsltStylesheet stylesheet)
    {
        if (stylesheet.StripSpace.Count == 0 || stylesheet.PreserveSpace.Count == 0)
            return;

        foreach (var strip in stylesheet.StripSpace)
        {
            foreach (var preserve in stylesheet.PreserveSpace)
            {
                // Check for conflict: same local name and compatible namespace
                if (strip.LocalName == preserve.LocalName &&
                    (strip.NamespaceUri ?? "") == (preserve.NamespaceUri ?? ""))
                {
                    throw new XsltException(
                        $"XTSE0270: Conflicting strip-space and preserve-space declarations for element '{strip}'");
                }
            }
        }
    }

    private static void ValidateAttributeSetReferences(XsltStylesheet stylesheet)
    {
        foreach (var (name, attrSet) in stylesheet.AttributeSets)
        {
            foreach (var usedName in attrSet.UseAttributeSets)
            {
                if (!stylesheet.AttributeSets.ContainsKey(usedName))
                    throw new XsltException($"XTSE0710: Attribute set '{usedName}' referenced by '{name}' is not defined");
            }
        }

        // Also validate use-attribute-sets on instructions (xsl:copy, xsl:element, LREs)
        foreach (var template in stylesheet.Templates)
        {
            ValidateUseAttributeSetRefsInInstructions(template.Body, stylesheet);
        }
        // XTSE0720: Check for circular attribute set references
        foreach (var (name, _) in stylesheet.AttributeSets)
        {
            var visited = new HashSet<QName>();
            var queue = new Queue<QName>();
            queue.Enqueue(name);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!stylesheet.AttributeSets.TryGetValue(current, out var currentSet))
                    continue;
                foreach (var usedName in currentSet.UseAttributeSets)
                {
                    if (usedName == name)
                        throw new XsltException($"XTSE0720: Attribute set '{name}' directly or indirectly references itself");
                    if (visited.Add(usedName))
                        queue.Enqueue(usedName);
                }
            }
        }
    }

    private static void ValidateNoAbstractComponents(XsltStylesheet stylesheet)
    {
        // XTSE3080: A top-level package must not contain components with visibility="abstract".
        // Abstract components are only valid in library packages intended for use-package overriding.
        if (!stylesheet.IsPackage) return;

        foreach (var template in stylesheet.Templates)
        {
            if (template.Visibility == Visibility.Abstract)
                throw new XsltException("XTSE3080: Top-level package contains a template with visibility=\"abstract\"");
        }
        foreach (var (_, func) in stylesheet.Functions)
        {
            if (func.Visibility == Visibility.Abstract)
                throw new XsltException("XTSE3080: Top-level package contains a function with visibility=\"abstract\"");
        }
        foreach (var variable in stylesheet.Variables)
        {
            if (variable.Visibility == Visibility.Abstract)
                throw new XsltException("XTSE3080: Top-level package contains a variable with visibility=\"abstract\"");
        }
        foreach (var (_, attrSet) in stylesheet.AttributeSets)
        {
            if (attrSet.Visibility == Visibility.Abstract)
                throw new XsltException("XTSE3080: Top-level package contains an attribute-set with visibility=\"abstract\"");
        }
    }

    /// <summary>
    /// XTSE1590: Validates that use-character-maps on xsl:output reference defined character maps.
    /// Called at top level after all imports are merged.
    /// </summary>
    private static void ValidateOutputCharacterMapReferences(XsltStylesheet stylesheet)
    {
        foreach (var output in stylesheet.Outputs)
        {
            foreach (var usedName in output.UseCharacterMaps)
            {
                if (!stylesheet.CharacterMaps.ContainsKey(usedName))
                    throw new XsltException($"XTSE1590: xsl:output references undefined character map '{usedName.LocalName}'");
            }
        }
    }

    /// <summary>
    /// XPST0017: Validates that function calls in match pattern predicates reference
    /// known functions (standard library or user-defined xsl:function).
    /// </summary>
    private static void ValidatePatternFunctionReferences(XsltStylesheet stylesheet)
    {
        var lib = PhoenixmlDb.XQuery.Functions.FunctionLibrary.Standard;
        // Well-known namespace IDs that contain standard functions
        var wellKnownNamespaces = new HashSet<NamespaceId>
        {
            NamespaceId.None,
            PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn,
            PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Xs,
            PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Math,
            PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Map,
            PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Array,
            PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Local,
            NamespaceId.Xslt,
        };

        foreach (var template in stylesheet.Templates)
        {
            if (template.Match != null)
            {
                ValidatePatternFunctionsInPattern(template.Match, stylesheet, lib, wellKnownNamespaces);
            }
        }
    }

    private static void ValidateStreamableTemplates(XsltStylesheet stylesheet)
    {
        // Collect all streamable mode names
        var streamableModes = new HashSet<QName>();
        foreach (var (name, mode) in stylesheet.Modes)
        {
            if (mode.Streamable)
                streamableModes.Add(name);
        }

        if (streamableModes.Count == 0) return;

        // Check each template that participates in a streamable mode
        foreach (var template in stylesheet.Templates)
        {
            if (template.Match == null) continue; // Named-only templates aren't in any mode

            bool inStreamableMode = false;
            foreach (var mode in template.Modes)
            {
                if (mode.Equals(TemplateIndex.AllModeSentinel))
                {
                    // #all mode: template participates in all modes including streamable ones
                    inStreamableMode = true;
                    break;
                }
                // Map #default sentinel to the unnamed mode key
                var lookupKey = mode.Equals(TemplateIndex.DefaultModeSentinel) ? new QName(NamespaceId.None, "") : mode;
                if (streamableModes.Contains(lookupKey))
                {
                    inStreamableMode = true;
                    break;
                }
            }

            // If no mode specified, template is in the default mode
            if (template.Modes.Count == 0)
            {
                var unnamedKey = new QName(NamespaceId.None, "");
                if (streamableModes.Contains(unnamedKey))
                    inStreamableMode = true;
            }

            if (inStreamableMode)
            {
                StreamabilityChecker.CheckStreamablePattern(template.Match, null);
                StreamabilityChecker.CheckStreamableTemplateBody(template.Body, null, stylesheet.AttributeSets, stylesheet.Functions);
            }
        }
    }

    private static void ValidatePatternFunctionsInPattern(
        XsltPattern pattern, XsltStylesheet stylesheet,
        PhoenixmlDb.XQuery.Functions.FunctionLibrary lib,
        HashSet<NamespaceId> wellKnownNamespaces)
    {
        switch (pattern)
        {
            case PathPattern pp:
                foreach (var step in pp.Steps)
                {
                    foreach (var pred in step.Predicates)
                        ValidateFunctionCallsInExpression(pred, stylesheet, lib, wellKnownNamespaces);
                }
                break;
            case UnionPattern up:
                foreach (var p in up.Patterns)
                    ValidatePatternFunctionsInPattern(p, stylesheet, lib, wellKnownNamespaces);
                break;
            case ExceptPattern ep:
                ValidatePatternFunctionsInPattern(ep.Left, stylesheet, lib, wellKnownNamespaces);
                ValidatePatternFunctionsInPattern(ep.Right, stylesheet, lib, wellKnownNamespaces);
                break;
            case IntersectPattern ip:
                ValidatePatternFunctionsInPattern(ip.Left, stylesheet, lib, wellKnownNamespaces);
                ValidatePatternFunctionsInPattern(ip.Right, stylesheet, lib, wellKnownNamespaces);
                break;
            case DotPattern dp:
                foreach (var pred in dp.Predicates)
                    ValidateFunctionCallsInExpression(pred, stylesheet, lib, wellKnownNamespaces);
                break;
        }
    }

    private static void ValidateFunctionCallsInExpression(
        PhoenixmlDb.XQuery.Ast.XQueryExpression expr, XsltStylesheet stylesheet,
        PhoenixmlDb.XQuery.Functions.FunctionLibrary lib,
        HashSet<NamespaceId> wellKnownNamespaces)
    {
        switch (expr)
        {
            case PhoenixmlDb.XQuery.Ast.FunctionCallExpression fc:
                ValidateSingleFunctionCall(fc, stylesheet, lib, wellKnownNamespaces);
                foreach (var arg in fc.Arguments)
                    ValidateFunctionCallsInExpression(arg, stylesheet, lib, wellKnownNamespaces);
                break;
            case PhoenixmlDb.XQuery.Ast.BinaryExpression be:
                ValidateFunctionCallsInExpression(be.Left, stylesheet, lib, wellKnownNamespaces);
                ValidateFunctionCallsInExpression(be.Right, stylesheet, lib, wellKnownNamespaces);
                break;
            case PhoenixmlDb.XQuery.Ast.UnaryExpression ue:
                ValidateFunctionCallsInExpression(ue.Operand, stylesheet, lib, wellKnownNamespaces);
                break;
            case PhoenixmlDb.XQuery.Ast.PathExpression pe:
                if (pe.InitialExpression != null)
                    ValidateFunctionCallsInExpression(pe.InitialExpression, stylesheet, lib, wellKnownNamespaces);
                foreach (var step in pe.Steps)
                    ValidateFunctionCallsInExpression(step, stylesheet, lib, wellKnownNamespaces);
                break;
            case PhoenixmlDb.XQuery.Ast.StepExpression se:
                foreach (var pred in se.Predicates)
                    ValidateFunctionCallsInExpression(pred, stylesheet, lib, wellKnownNamespaces);
                break;
            case PhoenixmlDb.XQuery.Ast.FilterExpression fe:
                ValidateFunctionCallsInExpression(fe.Primary, stylesheet, lib, wellKnownNamespaces);
                foreach (var pred in fe.Predicates)
                    ValidateFunctionCallsInExpression(pred, stylesheet, lib, wellKnownNamespaces);
                break;
            case PhoenixmlDb.XQuery.Ast.IfExpression ie:
                ValidateFunctionCallsInExpression(ie.Condition, stylesheet, lib, wellKnownNamespaces);
                ValidateFunctionCallsInExpression(ie.Then, stylesheet, lib, wellKnownNamespaces);
                if (ie.Else != null)
                    ValidateFunctionCallsInExpression(ie.Else, stylesheet, lib, wellKnownNamespaces);
                break;
            case PhoenixmlDb.XQuery.Ast.SequenceExpression seq:
                foreach (var item in seq.Items)
                    ValidateFunctionCallsInExpression(item, stylesheet, lib, wellKnownNamespaces);
                break;
        }
    }

    private static void ValidateSingleFunctionCall(
        PhoenixmlDb.XQuery.Ast.FunctionCallExpression fc, XsltStylesheet stylesheet,
        PhoenixmlDb.XQuery.Functions.FunctionLibrary lib,
        HashSet<NamespaceId> wellKnownNamespaces)
    {
        var name = fc.Name;
        var arity = fc.Arguments.Count;

        // Functions in well-known namespaces (fn, xs, math, etc.) or with no namespace
        // are assumed valid — they'll be validated at runtime
        if (wellKnownNamespaces.Contains(name.Namespace))
            return;

        // Functions with EQName syntax where the URI matches a well-known namespace
        if (name.ExpandedNamespace != null)
        {
            // Skip validation for well-known namespace URIs
            return;
        }

        // User-namespace function: must be declared as xsl:function
        if (stylesheet.Functions.ContainsKey((name, arity)))
            return;

        // Check if ANY arity exists for this function name (for better error message)
        var displayName = name.Prefix != null ? $"{name.Prefix}:{name.LocalName}" : name.LocalName;
        throw new XsltException($"XPST0017: Cannot find a {arity}-argument function named {{{displayName}}}. " +
                                 "No user-defined function with this name and arity is available.");
    }

    private static void ValidateUseAttributeSetRefsInInstructions(XsltSequenceConstructor body, XsltStylesheet stylesheet)
    {
        foreach (var instruction in body.Instructions)
        {
            ValidateUseAttributeSetRefsInInstruction(instruction, stylesheet);
        }
    }

    private static void ValidateUseAttributeSetRefsInInstruction(XsltInstruction instruction, XsltStylesheet stylesheet)
    {
        // Check use-attribute-sets on instructions that support them
        List<QName>? useAttrSets = instruction switch
        {
            XsltElement e => e.UseAttributeSets,
            XsltCopy c => c.UseAttributeSets,
            XsltLiteralResultElement lre => lre.UseAttributeSets,
            _ => null
        };

        if (useAttrSets != null)
        {
            foreach (var usedName in useAttrSets)
            {
                if (!stylesheet.AttributeSets.ContainsKey(usedName))
                    throw new XsltException($"XTSE0710: Attribute set '{usedName}' is not defined");
            }
        }

        // Recurse into child sequence constructors
        switch (instruction)
        {
            case XsltSequenceConstructor sc:
                foreach (var child in sc.Instructions)
                    ValidateUseAttributeSetRefsInInstruction(child, stylesheet);
                break;
            case XsltElement e:
                ValidateUseAttributeSetRefsInInstructions(e.Content, stylesheet);
                break;
            case XsltCopy c when c.Content != null:
                ValidateUseAttributeSetRefsInInstructions(c.Content, stylesheet);
                break;
            case XsltLiteralResultElement lre:
                ValidateUseAttributeSetRefsInInstructions(lre.Content, stylesheet);
                break;
            case XsltIf i:
                ValidateUseAttributeSetRefsInInstructions(i.Then, stylesheet);
                break;
            case XsltChoose ch:
                foreach (var w in ch.When)
                    ValidateUseAttributeSetRefsInInstructions(w.Body, stylesheet);
                if (ch.Otherwise != null)
                    ValidateUseAttributeSetRefsInInstructions(ch.Otherwise, stylesheet);
                break;
            case XsltForEach fe:
                ValidateUseAttributeSetRefsInInstructions(fe.Body, stylesheet);
                break;
            case XsltTry t:
                if (t.Body != null)
                    ValidateUseAttributeSetRefsInInstructions(t.Body, stylesheet);
                foreach (var c in t.Catches)
                {
                    if (c.Body != null)
                        ValidateUseAttributeSetRefsInInstructions(c.Body, stylesheet);
                }
                break;
        }
    }

    /// <summary>
    /// Validates character map references: XTSE1590 (undefined) and XTSE1600 (circular).
    /// </summary>
    private static void ValidateCharacterMapReferences(XsltStylesheet stylesheet)
    {
        foreach (var (name, charMap) in stylesheet.CharacterMaps)
        {
            foreach (var usedName in charMap.UseCharacterMaps)
            {
                if (!stylesheet.CharacterMaps.ContainsKey(usedName))
                    throw new XsltException($"XTSE1590: Character map '{name.LocalName}' references undefined character map '{usedName.LocalName}'");
            }
        }

        // XTSE1600: Check for circular character map references
        foreach (var (name, _) in stylesheet.CharacterMaps)
        {
            var visited = new HashSet<QName>();
            var queue = new Queue<QName>();
            queue.Enqueue(name);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!stylesheet.CharacterMaps.TryGetValue(current, out var currentMap))
                    continue;
                foreach (var usedName in currentMap.UseCharacterMaps)
                {
                    if (usedName == name)
                        throw new XsltException($"XTSE1600: Character map '{name.LocalName}' directly or indirectly references itself");
                    if (visited.Add(usedName))
                        queue.Enqueue(usedName);
                }
            }
        }
    }

    /// <summary>
    /// Merges decimal formats from imported stylesheets into the main stylesheet.
    /// Imported formats have lower precedence — they're only used for attributes
    /// not already defined in the importing stylesheet.
    /// </summary>
    private static void MergeImportedDecimalFormats(XsltStylesheet stylesheet)
    {
        foreach (var imported in stylesheet.Imports)
        {
            // Recursively merge imports of imports
            MergeImportedDecimalFormats(imported);

            foreach (var (name, importedDf) in imported.DecimalFormats)
            {
                if (stylesheet.DecimalFormats.TryGetValue(name, out var existingDf))
                {
                    // Merge: existing (higher precedence) overrides imported (lower precedence)
                    // For each property, keep existing if it differs from default, otherwise use imported
                    var defaults = new XsltDecimalFormat();
                    stylesheet.DecimalFormats[name] = new XsltDecimalFormat
                    {
                        Name = existingDf.Name,
                        DecimalSeparator = existingDf.DecimalSeparator != defaults.DecimalSeparator ? existingDf.DecimalSeparator : importedDf.DecimalSeparator,
                        GroupingSeparator = existingDf.GroupingSeparator != defaults.GroupingSeparator ? existingDf.GroupingSeparator : importedDf.GroupingSeparator,
                        Infinity = existingDf.Infinity != defaults.Infinity ? existingDf.Infinity : importedDf.Infinity,
                        MinusSign = existingDf.MinusSign != defaults.MinusSign ? existingDf.MinusSign : importedDf.MinusSign,
                        NaN = existingDf.NaN != defaults.NaN ? existingDf.NaN : importedDf.NaN,
                        Percent = existingDf.Percent != defaults.Percent ? existingDf.Percent : importedDf.Percent,
                        PerMille = existingDf.PerMille != defaults.PerMille ? existingDf.PerMille : importedDf.PerMille,
                        ZeroDigit = existingDf.ZeroDigit != defaults.ZeroDigit ? existingDf.ZeroDigit : importedDf.ZeroDigit,
                        Digit = existingDf.Digit != defaults.Digit ? existingDf.Digit : importedDf.Digit,
                        PatternSeparator = existingDf.PatternSeparator != defaults.PatternSeparator ? existingDf.PatternSeparator : importedDf.PatternSeparator,
                        ExponentSeparator = existingDf.ExponentSeparator != defaults.ExponentSeparator ? existingDf.ExponentSeparator : importedDf.ExponentSeparator,
                        // Keep any conflict from the higher-precedence format's same-level merging;
                        // imported conflicts are resolved by this higher-precedence override
                        HasConflict = existingDf.HasConflict,
                        ConflictDescription = existingDf.ConflictDescription
                    };
                }
                else
                {
                    // No existing format — add the imported one directly
                    stylesheet.DecimalFormats[name] = importedDf;
                }
            }
        }
    }

    /// <summary>
    /// Merges namespace aliases from imported stylesheets into the main stylesheet.
    /// Imported aliases have lower precedence — main module aliases take priority.
    /// </summary>
    private static void MergeImportedNamespaceAliases(XsltStylesheet stylesheet)
    {
        foreach (var imported in stylesheet.Imports)
        {
            MergeImportedNamespaceAliases(imported);

            foreach (var (nsUri, alias) in imported.NamespaceAliases)
            {
                // TryAdd: main module (higher precedence) takes priority
                stylesheet.NamespaceAliases.TryAdd(nsUri, alias);
            }
        }
    }

    /// <summary>
    /// Validates all decimal formats after merging is complete.
    /// Checks for: unresolved same-precedence conflicts (XTSE1290) and
    /// duplicate character roles (XTSE1300).
    /// </summary>
    private static void ValidateDecimalFormats(XsltStylesheet stylesheet)
    {
        foreach (var (_, df) in stylesheet.DecimalFormats)
        {
            // Check for unresolved same-precedence conflicts (XTSE1290)
            if (df.HasConflict)
                throw new XsltException(df.ConflictDescription ?? "XTSE1290: Conflicting decimal-format declarations");

            // Check that no two properties share the same character (XTSE1300)
            var roles = new Dictionary<string, string>();
            void CheckRole(string c, string role)
            {
                if (roles.TryGetValue(c, out var existingRole))
                    throw new XsltException(
                        $"XTSE1300: Character '{c}' cannot be used as both {existingRole} and {role}");
                roles[c] = role;
            }
            CheckRole(df.DecimalSeparator, "decimal-separator");
            CheckRole(df.GroupingSeparator, "grouping-separator");
            CheckRole(df.Percent, "percent");
            CheckRole(df.PerMille, "per-mille");
            CheckRole(df.ZeroDigit, "zero-digit");
            CheckRole(df.Digit, "digit");
            CheckRole(df.PatternSeparator, "pattern-separator");
            CheckRole(df.ExponentSeparator, "exponent-separator");
        }
    }

    /// <summary>
    /// Recursively collect outputs from imported stylesheets, assigning import precedence levels.
    /// </summary>
    private static void CollectImportedOutputs(XsltStylesheet target, XsltStylesheet source, int precedenceLevel)
    {
        foreach (var imported in source.Imports)
        {
            foreach (var output in imported.Outputs)
            {
                output.ImportPrecedence = precedenceLevel;
                target.Outputs.Add(output);
            }
            // Recursively collect from nested imports at even lower precedence
            CollectImportedOutputs(target, imported, precedenceLevel + 1);
        }
    }

    private static void MergeOutputDeclarations(XsltStylesheet stylesheet)
    {
        if (stylesheet.Outputs.Count <= 1)
            return;

        // Group by name (null for unnamed). Key on the EXPANDED QName identity (namespace URI
        // + local name), never the lexical prefix: two xsl:output declarations whose @name uses
        // different prefixes bound to the same namespace URI denote the same output definition
        // and must merge (output-0135). QName.ToString() emits the prefix when no expanded
        // namespace is attached, so it cannot be the key.
        static string OutputKey(QName name)
            => "{" + (name.ResolvedNamespace ?? "#" + name.Namespace.Value.ToString(CultureInfo.InvariantCulture)) + "}" + name.LocalName;
        var groups = new Dictionary<string, List<XsltOutput>>();
        foreach (var output in stylesheet.Outputs)
        {
            var key = output.Name != null ? OutputKey(output.Name.Value) : "";
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<XsltOutput>();
                groups[key] = list;
            }
            list.Add(output);
        }

        stylesheet.Outputs.Clear();
        foreach (var (_, outputList) in groups)
        {
            if (outputList.Count == 1)
            {
                stylesheet.Outputs.Add(outputList[0]);
                continue;
            }

            // XTSE1560: Check for conflicting attribute values at same import precedence,
            // but only when a higher-precedence output doesn't resolve the conflict
            var byPrecedence = outputList.GroupBy(o => o.ImportPrecedence).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var (_, samePrecList) in byPrecedence)
            {
                for (var i = 0; i < samePrecList.Count; i++)
                {
                    for (var j = i + 1; j < samePrecList.Count; j++)
                    {
                        var a = samePrecList[i];
                        var b = samePrecList[j];
                        // Only report conflict if no higher-precedence output sets the attribute
                        var higherPrec = outputList.Where(o => o.ImportPrecedence < a.ImportPrecedence).ToList();
                        if (a.Method != null && b.Method != null && a.Method != b.Method
                            && !higherPrec.Any(o => o.Method != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: method '{a.Method}' vs '{b.Method}'");
                        if (a.Indent != null && b.Indent != null && a.Indent != b.Indent
                            && !higherPrec.Any(o => o.Indent != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: indent values differ");
                        if (a.Encoding != null && b.Encoding != null && !string.Equals(a.Encoding, b.Encoding, StringComparison.OrdinalIgnoreCase)
                            && !higherPrec.Any(o => o.Encoding != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: encoding '{a.Encoding}' vs '{b.Encoding}'");
                        if (a.OmitXmlDeclaration != null && b.OmitXmlDeclaration != null && a.OmitXmlDeclaration != b.OmitXmlDeclaration
                            && !higherPrec.Any(o => o.OmitXmlDeclaration != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: omit-xml-declaration values differ");
                        if (a.Standalone != null && b.Standalone != null && a.Standalone != b.Standalone
                            && !higherPrec.Any(o => o.Standalone != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: standalone values differ");
                        if (a.Version != null && b.Version != null && a.Version != b.Version
                            && !higherPrec.Any(o => o.Version != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: version '{a.Version}' vs '{b.Version}'");
                        if (a.MediaType != null && b.MediaType != null && a.MediaType != b.MediaType
                            && !higherPrec.Any(o => o.MediaType != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: media-type '{a.MediaType}' vs '{b.MediaType}'");
                        if (a.DoctypePublic != null && b.DoctypePublic != null && a.DoctypePublic != b.DoctypePublic
                            && !higherPrec.Any(o => o.DoctypePublic != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: doctype-public values differ");
                        if (a.DoctypeSystem != null && b.DoctypeSystem != null && a.DoctypeSystem != b.DoctypeSystem
                            && !higherPrec.Any(o => o.DoctypeSystem != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: doctype-system values differ");
                        if (a.NormalizationForm != null && b.NormalizationForm != null && a.NormalizationForm != b.NormalizationForm
                            && !higherPrec.Any(o => o.NormalizationForm != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: normalization-form values differ");
                        if (a.IncludeContentType != null && b.IncludeContentType != null && a.IncludeContentType != b.IncludeContentType
                            && !higherPrec.Any(o => o.IncludeContentType != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: include-content-type values differ");
                    }
                }
            }

            // Merge: highest precedence (lowest ImportPrecedence number) wins for each attribute.
            // Sort by ImportPrecedence ascending so highest-precedence outputs are first.
            var sorted = outputList.OrderBy(o => o.ImportPrecedence).ToList();
            var merged = new XsltOutput
            {
                Name = outputList[0].Name,
                Method = sorted.Select(o => o.Method).FirstOrDefault(m => m != null),
                Version = sorted.Select(o => o.Version).FirstOrDefault(v => v != null),
                Encoding = sorted.Select(o => o.Encoding).FirstOrDefault(e => e != null),
                OmitXmlDeclaration = sorted.Select(o => o.OmitXmlDeclaration).FirstOrDefault(v => v != null),
                Standalone = sorted.Select(o => o.Standalone).FirstOrDefault(v => v != null),
                DoctypePublic = sorted.Select(o => o.DoctypePublic).FirstOrDefault(v => v != null),
                DoctypeSystem = sorted.Select(o => o.DoctypeSystem).FirstOrDefault(v => v != null),
                Indent = sorted.Select(o => o.Indent).FirstOrDefault(v => v != null),
                MediaType = sorted.Select(o => o.MediaType).FirstOrDefault(v => v != null),
                ItemSeparator = sorted.Select(o => o.ItemSeparator).FirstOrDefault(v => v != null),
                NormalizationForm = sorted.Select(o => o.NormalizationForm).FirstOrDefault(v => v != null),
                UndeclarePrefixes = sorted.Select(o => o.UndeclarePrefixes).FirstOrDefault(v => v != null),
                IncludeContentType = sorted.Select(o => o.IncludeContentType).FirstOrDefault(v => v != null),
                HtmlVersion = sorted.Select(o => o.HtmlVersion).FirstOrDefault(v => v != null),
                BuildTree = sorted.Select(o => o.BuildTree).FirstOrDefault(v => v != null),
                AllowDuplicateNames = sorted.Select(o => o.AllowDuplicateNames).FirstOrDefault(v => v != null),
                ByteOrderMark = sorted.Select(o => o.ByteOrderMark).FirstOrDefault(v => v != null),
                JsonNodeOutputMethod = sorted.Select(o => o.JsonNodeOutputMethod).FirstOrDefault(v => v != null),
            };

            // Union cdata-section-elements from all declarations
            HashSet<QName>? cdataElements = null;
            foreach (var o in outputList)
            {
                if (o.CdataSectionElements != null)
                {
                    cdataElements ??= new HashSet<QName>();
                    cdataElements.UnionWith(o.CdataSectionElements);
                }
            }
            merged.CdataSectionElements = cdataElements;

            // Union use-character-maps
            foreach (var o in outputList)
            {
                foreach (var m in o.UseCharacterMaps)
                {
                    if (!merged.UseCharacterMaps.Contains(m))
                        merged.UseCharacterMaps.Add(m);
                }
            }

            // Union suppress-indentation from all declarations
            HashSet<QName>? suppressElements = null;
            foreach (var o in outputList)
            {
                if (o.SuppressIndentation != null)
                {
                    suppressElements ??= new HashSet<QName>();
                    suppressElements.UnionWith(o.SuppressIndentation);
                }
            }
            merged.SuppressIndentation = suppressElements;

            stylesheet.Outputs.Add(merged);
        }
    }

    private XsltStylesheet? LoadExternalStylesheet(XElement element)
    {
        var hrefAttr = element.Attribute("href");
        // XTSE0010: href is a required attribute on xsl:import and xsl:include
        if (hrefAttr == null || string.IsNullOrEmpty(hrefAttr.Value))
            throw new XsltException("XTSE0010: xsl:import/xsl:include must have an href attribute",
                GetSourceLocation(element));

        var href = hrefAttr.Value;

        // Strip fragment identifier for embedded stylesheet references (§3.11.2)
        string? fragmentId = null;
        var hrefPath = href;
        var hashIndex = href.IndexOf('#', StringComparison.Ordinal);
        if (hashIndex >= 0)
        {
            fragmentId = href[(hashIndex + 1)..];
            hrefPath = href[..hashIndex];
        }

        // Determine effective base URI for resolution.
        // DTD entity expansion may give elements a different BaseUri than _baseUri.
        var effectiveBase = _baseUri;
        if (!string.IsNullOrEmpty(element.BaseUri) &&
            Uri.TryCreate(element.BaseUri, UriKind.Absolute, out var elementBaseUri) &&
            (_baseUri == null || elementBaseUri.AbsoluteUri != _baseUri.AbsoluteUri))
        {
            effectiveBase = elementBaseUri;
        }

        // Resolve href against base URI. Two paths from here on:
        //  - file:// (or unspecified): resolve to a local path and read with File.ReadAllText
        //  - http(s)://: fetch with HttpClient. We treat HTTP imports as opt-in by URI scheme —
        //    if the entry stylesheet was loaded over HTTPS, its imports come over HTTPS too.
        //    This mirrors how Saxon resolves imports against the entry's system id.
        Uri? resolvedUri = null;
        string? resolvedPath = null;
        if (effectiveBase != null)
        {
            try
            {
                resolvedUri = new Uri(effectiveBase, hrefPath);
                if (resolvedUri.IsFile)
                    resolvedPath = resolvedUri.LocalPath;
            }
            catch (UriFormatException)
            {
                // Fall through to try direct path
            }
        }

        var isHttp = resolvedUri != null && (resolvedUri.Scheme == Uri.UriSchemeHttp || resolvedUri.Scheme == Uri.UriSchemeHttps);

        if (!isHttp && (resolvedPath == null || !File.Exists(resolvedPath)))
        {
            // Try as relative path from base URI directory
            if (effectiveBase?.IsFile == true)
            {
                var baseDir = Path.GetDirectoryName(effectiveBase.LocalPath);
                if (baseDir != null)
                {
                    resolvedPath = Path.Combine(baseDir, hrefPath);
                }
            }
        }

        if (!isHttp && (resolvedPath == null || !File.Exists(resolvedPath)))
            throw new XsltException($"XTSE0165: Cannot find stylesheet module '{href}'",
                GetSourceLocation(element));

        // Recursion key: the absolute URI for HTTP imports, the canonical full path for file imports.
        var recursionKey = isHttp ? resolvedUri!.AbsoluteUri : Path.GetFullPath(resolvedPath!);
        if (!_loadedStylesheets.Add(recursionKey))
            throw new XsltException($"XTSE0210: Stylesheet module '{href}' directly or indirectly imports itself",
                GetSourceLocation(element));

        // Resource policy: check import access and try custom resolver
        string? policyResolvedXml = null;
        if (ResourcePolicy != null)
        {
            var importUri = isHttp ? resolvedUri! : new Uri(recursionKey);
            if (!ResourcePolicy.IsAllowed(importUri, PhoenixmlDb.XQuery.Security.ResourceAccessKind.ImportStylesheet))
                throw new XsltException(
                    $"XTSE0165: Resource policy denied import access to '{href}'",
                    GetSourceLocation(element));

            policyResolvedXml = ResourcePolicy.ResourceResolver?.ResolveStylesheetModule(href, _baseUri);
        }

        try
        {
            string xml;
            if (policyResolvedXml != null)
                xml = policyResolvedXml;
            else if (isHttp)
            {
                // Consult the preload cache first: callers running on Blazor
                // WebAssembly (or any runtime that disallows monitor-waits) must
                // supply pre-fetched content this way, since the synchronous
                // HttpClient call below blocks the calling thread to completion.
                if (PreloadedResources is { } preloaded && preloaded.TryGet(resolvedUri!, out var preloadedXml))
                {
                    xml = preloadedXml;
                }
                else if (OperatingSystem.IsBrowser())
                {
                    throw new XsltException(
                        $"XTSE0165: Cannot fetch xsl:import/xsl:include '{href}' on Blazor WebAssembly: " +
                        "synchronous HTTP I/O is not supported. Pre-fetch the imported stylesheet " +
                        "asynchronously and pass it through PreloadedResources to LoadStylesheetAsync.",
                        GetSourceLocation(element));
                }
                else
                {
                    xml = HttpResourceLoader.GetStringSync(resolvedUri!);
                }
            }
            else
                xml = File.ReadAllText(resolvedPath!);
            var savedBaseUri = _baseUri;
            var savedDefaultMode = _currentDefaultMode;
            _baseUri = isHttp ? resolvedUri! : new Uri(recursionKey);
            // Always load imported/included modules through XmlReader so SetBaseUri can
            // populate XElement.BaseUri with the imported module's URI — that's what
            // diagnostics later surface as "this error came from <module>".
            var importSettings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = AllowDtdProcessing && xml.Contains("<!DOCTYPE", StringComparison.Ordinal)
                    ? System.Xml.DtdProcessing.Parse
                    : System.Xml.DtdProcessing.Prohibit,
                MaxCharactersFromEntities = 1_000_000,
            };
            if (importSettings.DtdProcessing == System.Xml.DtdProcessing.Parse)
                importSettings.XmlResolver = new System.Xml.XmlUrlResolver();
            using var importReader = System.Xml.XmlReader.Create(new System.IO.StringReader(xml), importSettings, _baseUri.AbsoluteUri);
            var doc = XDocument.Load(importReader, LoadOptions.SetLineInfo | LoadOptions.SetBaseUri | LoadOptions.PreserveWhitespace);
            // For embedded stylesheets, find the element with matching id (§3.11.2)
            XElement stylesheetRoot;
            if (fragmentId != null)
            {
                XNamespace xmlNs = "http://www.w3.org/XML/1998/namespace";
                stylesheetRoot = doc.Descendants()
                    .FirstOrDefault(e =>
                        e.Attribute("id")?.Value == fragmentId ||
                        e.Attribute(xmlNs + "id")?.Value == fragmentId)
                    ?? throw new XsltException(
                        $"XTSE0165: Cannot find embedded stylesheet with id '{fragmentId}' in '{hrefPath}'",
                        GetSourceLocation(element));

                // Verify it's an xsl:stylesheet or xsl:transform element
                if (stylesheetRoot.Name.Namespace != XsltNs ||
                    stylesheetRoot.Name.LocalName is not ("stylesheet" or "transform"))
                    throw new XsltException(
                        $"XTSE0165: Element with id '{fragmentId}' is not an xsl:stylesheet/xsl:transform element",
                        GetSourceLocation(element));
            }
            else
            {
                stylesheetRoot = doc.Root!;
            }

            // Handle xml:base on embedded stylesheet elements
            if (fragmentId != null)
            {
                XNamespace xmlNs = "http://www.w3.org/XML/1998/namespace";
                var xmlBase = stylesheetRoot.Attribute(xmlNs + "base")?.Value;
                if (xmlBase != null)
                {
                    _baseUri = new Uri(_baseUri!, xmlBase);
                }
            }

            ResolveShadowAttributes(stylesheetRoot);
            // Check use-when on the root xsl:stylesheet/xsl:transform element
            if (!ShouldIncludeElement(stylesheetRoot))
                return null;
            // Save and replace _elementPrefixMap with one built from THIS module's xml.
            // The map is keyed by (line, col) which are module-relative; querying entries
            // built from the entry stylesheet against XElements from an included module
            // returns wrong prefixes (Docbook TNG: head.xsl `<link>` LREs were getting
            // prefix `xsl` because line 190 of head.xsl matched line 190 of docbook.xsl
            // in the entry stylesheet's map — different file, same coordinates).
            var savedPrefixMap = _elementPrefixMap;
            _elementPrefixMap = BuildElementPrefixMap(xml);
            XsltStylesheet? result;
            try
            {
                result = ParseStylesheet(stylesheetRoot, isTopLevel: false);
            }
            finally
            {
                _elementPrefixMap = savedPrefixMap;
            }
            _baseUri = savedBaseUri;
            _currentDefaultMode = savedDefaultMode;
            return result;
        }
        catch (IOException ex)
        {
            throw new XsltException($"XTSE0165: Cannot load stylesheet module '{href}': {ex.Message}",
                GetSourceLocation(element));
        }
        catch (XmlException ex)
        {
            throw new XsltException($"XTSE0165: Invalid XML in stylesheet module '{href}': {ex.Message}",
                GetSourceLocation(element));
        }
        // Let XsltException propagate — errors in imported/included stylesheets
        // should cause compilation to fail (XTSE0010, XTSE0090, etc.)
        finally
        {
            _loadedStylesheets.Remove(recursionKey);
        }
    }

    private static void MergeStylesheet(XsltStylesheet target, XsltStylesheet source)
    {
        // XTSE0265: Conflicting input-type-annotations across modules
        CheckInputTypeAnnotationsConflict(target, source);

        // Include semantics: merge all declarations at same precedence
        target.Templates.AddRange(source.Templates);
        // Only add variables/params that don't already exist (by name)
        foreach (var variable in source.Variables)
        {
            if (!target.Variables.Any(v => v.Name.Equals(variable.Name)))
                target.Variables.Add(variable);
        }
        foreach (var param in source.Parameters)
        {
            if (!target.Parameters.Any(p => p.Name.Equals(param.Name)))
                target.Parameters.Add(param);
        }

        foreach (var (name, template) in source.NamedTemplates)
        {
            if (!target.NamedTemplates.TryAdd(name, template))
                throw new XsltException($"XTSE0660: Duplicate named template '{name.LocalName}' at the same import precedence");
        }

        foreach (var (name, func) in source.Functions)
        {
            target.Functions.TryAdd(name, func);
        }

        foreach (var (name, key) in source.Keys)
        {
            if (target.Keys.TryGetValue(name, out var existingKey))
            {
                // Merge same-named key definitions per XSLT spec (union of matches)
                existingKey.OtherDefinitions ??= new List<XsltKey>();
                existingKey.OtherDefinitions.Add(key);
                // Also merge any other definitions from the source key
                if (key.OtherDefinitions != null)
                    existingKey.OtherDefinitions.AddRange(key.OtherDefinitions);
            }
            else
            {
                target.Keys[name] = key;
            }
        }

        foreach (var (name, attrSet) in source.AttributeSets)
        {
            // Merge same-named attribute sets per XSLT spec
            if (target.AttributeSets.TryGetValue(name, out var existingSet))
            {
                existingSet.Attributes.AddRange(attrSet.Attributes);
                foreach (var u in attrSet.UseAttributeSets)
                {
                    if (!existingSet.UseAttributeSets.Contains(u))
                        existingSet.UseAttributeSets.Add(u);
                }
            }
            else
            {
                target.AttributeSets[name] = attrSet;
            }
        }

        foreach (var (name, charMap) in source.CharacterMaps)
        {
            target.CharacterMaps.TryAdd(name, charMap);
        }

        foreach (var (name, decFmt) in source.DecimalFormats)
        {
            target.DecimalFormats.TryAdd(name, decFmt);
        }

        target.Outputs.AddRange(source.Outputs);
        target.StripSpace.AddRange(source.StripSpace);
        target.PreserveSpace.AddRange(source.PreserveSpace);

        // Merge namespace aliases from included modules (same import precedence)
        foreach (var (nsUri, alias) in source.NamespaceAliases)
        {
            if (target.NamespaceAliases.TryGetValue(nsUri, out var existing))
            {
                if (existing.ResultUri != alias.ResultUri)
                    throw new XsltException($"XTSE0810: Conflicting xsl:namespace-alias declarations for namespace '{nsUri}' at the same import precedence");
            }
            else
            {
                target.NamespaceAliases[nsUri] = alias;
            }
        }

        // Merge global-context-item declarations (XTSE3087 for inconsistency)
        if (source.GlobalContextItemUse != null)
        {
            if (target.GlobalContextItemUse != null)
            {
                // Both modules declare xsl:global-context-item — check consistency
                if (target.GlobalContextItemUse != source.GlobalContextItemUse)
                    throw new XsltException("XTSE3087: Inconsistent xsl:global-context-item declarations across stylesheet modules");
                // Check consistency of the 'as' type across modules
                var targetAs = target.GlobalContextItemAs?.ToString();
                var sourceAs = source.GlobalContextItemAs?.ToString();
                if (targetAs != sourceAs)
                    throw new XsltException($"XTSE3087: Inconsistent xsl:global-context-item 'as' type across stylesheet modules ('{targetAs ?? "item()"}' vs '{sourceAs ?? "item()"}')");

            }
            else
            {
                target.GlobalContextItemUse = source.GlobalContextItemUse;
                target.GlobalContextItemAs = source.GlobalContextItemAs;
            }
        }

        // Merge accumulators from included modules (same import precedence)
        foreach (var (name, acc) in source.Accumulators)
        {
            if (target.Accumulators.ContainsKey(name))
                target.DuplicateAccumulatorNames.Add(name);
            else
                target.Accumulators[name] = acc;
        }

        // Merge mode declarations from included modules (same import precedence)
        foreach (var (name, mode) in source.Modes)
        {
            if (target.Modes.TryGetValue(name, out var existingMode))
            {
                // Conflicting use-accumulators at same import precedence — defer the error
                // because a higher-precedence import may override and resolve the conflict
                if (UseAccumulatorsConflict(existingMode, mode))
                    target.ConflictingModeAccumulators.Add(name);
                if (VisibilityConflict(existingMode, mode))
                    target.ConflictingModeVisibility.Add(name);
            }
            else
            {
                target.Modes[name] = mode;
            }
        }
        // Propagate conflicts from source
        foreach (var conflict in source.ConflictingModeAccumulators)
            target.ConflictingModeAccumulators.Add(conflict);
        foreach (var conflict in source.ConflictingModeVisibility)
            target.ConflictingModeVisibility.Add(conflict);

        // Merge namespace prefix bindings from included modules
        // (needed for element-available, function-available prefix resolution at runtime)
        foreach (var (prefix, uri) in source.Namespaces)
        {
            target.Namespaces.TryAdd(prefix, uri);
        }

        // Merge extension element namespaces from included modules
        // (needed for element-available() to return true for extension elements at runtime)
        foreach (var extNs in source.ExtensionElementPrefixes)
            target.ExtensionElementPrefixes.Add(extNs);

        // Also merge any nested imports
        target.Imports.AddRange(source.Imports);
    }

    private XsltStylesheet ParseSimplifiedStylesheet(XElement element)
    {
        // XTSE0150: A simplified stylesheet must have an xsl:version attribute
        var versionAttr = element.Attribute(XsltNs + "version");
        if (versionAttr == null)
            throw new XsltException("XTSE0150: A literal result element used as a simplified stylesheet module must have an xsl:version attribute",
                GetSourceLocation(element));

        // A simplified stylesheet is a literal result element that acts as the template body
        var stylesheet = new XsltStylesheet
        {
            Version = versionAttr.Value,
            BaseUri = _baseUri
        };

        // Create implicit template matching /
        var template = new XsltTemplate
        {
            Match = new PathPattern
            {
                Steps =
                [
                    new PatternStep
                    {
                        Axis = Axis.Self,
                        NodeTest = new KindTest { Kind = XdmNodeKind.Document }
                    }
                ]
            },
            Body = new XsltSequenceConstructor
            {
                Instructions = [ParseInstruction(element)]
            }
        };

        stylesheet.Templates.Add(template);
        return stylesheet;
    }

    private XsltTemplate ParseTemplate(XElement element, string? moduleVersion = null)
    {
        var prevContext = _nsContext;
        _nsContext = element;

        var nameAttr = element.Attribute("name");
        var matchAttr = element.Attribute("match");
        var priorityAttr = element.Attribute("priority");
        var modeAttr = element.Attribute("mode");
        var asAttr = element.Attribute("as");
        var versionAttr = element.Attribute("version");

        // XTSE0090: Validate no unknown attributes
        ValidateAllowedAttributes(element, GetSourceLocation(element), "match", "name", "priority", "mode", "as", "visibility", "version", "default-collation");

        // XTSE0500: Template must have at least match or name
        if (matchAttr == null && nameAttr == null)
            throw new XsltException("XTSE0500: An xsl:template element must have either a match attribute or a name attribute, or both",
                GetSourceLocation(element));
        // XTSE0500: Template without match must not have mode or priority
        if (matchAttr == null)
        {
            if (modeAttr != null)
                throw new XsltException("XTSE0500: An xsl:template element with no match attribute must have no mode attribute",
                    GetSourceLocation(element));
            if (priorityAttr != null)
                throw new XsltException("XTSE0500: An xsl:template element with no match attribute must have no priority attribute",
                    GetSourceLocation(element));
        }

        // XTSE0020: Validate name/mode are valid QNames
        if (nameAttr != null)
            ValidateQNameValue(nameAttr.Value, "name", GetSourceLocation(element));

        // Apply default-mode on this template FIRST — per XSLT spec, the effective
        // default-mode of an element is its own default-mode attribute if present,
        // otherwise inherited from its parent. This affects both the template's match
        // mode (when mode attr is absent) and instructions within the body.
        var prevDefaultMode = _currentDefaultMode;
        var templateDefaultMode = element.Attribute("default-mode");
        if (templateDefaultMode != null)
            _currentDefaultMode = templateDefaultMode.Value == "#unnamed" ? null : ParseQName(templateDefaultMode.Value, element);

        var modes = new List<QName>();
        if (modeAttr != null)
        {
            var modeTokens = modeAttr.Value.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
            // XTSE0550: mode list must not be empty
            if (modeTokens.Length == 0)
                throw new XsltException("XTSE0550: The mode attribute of xsl:template must not be empty", GetSourceLocation(element));
            var seenModes = new HashSet<string>();
            bool hasAll = false;
            foreach (var mode in modeTokens)
            {
                // XTSE0550: duplicate mode tokens
                if (!seenModes.Add(mode))
                    throw new XsltException($"XTSE0550: Duplicate mode '{mode}' in mode list of xsl:template", GetSourceLocation(element));
                if (mode == "#all")
                {
                    hasAll = true;
                    modes.Add(TemplateIndex.AllModeSentinel);
                    continue;
                }
                if (mode == "#default" || mode == "#unnamed")
                {
                    // Resolve #default/#unnamed to the effective default mode
                    if (mode == "#default" && _currentDefaultMode.HasValue)
                        modes.Add(_currentDefaultMode.Value);
                    else
                        modes.Add(TemplateIndex.DefaultModeSentinel);
                    continue;
                }
                // XTSE0550: validate mode token is a valid QName (no #, !, etc.)
                if (mode.StartsWith('#'))
                    throw new XsltException($"XTSE0550: Invalid mode token '{mode}' in mode list of xsl:template", GetSourceLocation(element));
                ValidateQNameValue(mode, "mode", GetSourceLocation(element));
                modes.Add(ParseQName(mode, element));
            }
            // XTSE0550: #all must not appear with other modes
            if (hasAll && modeTokens.Length > 1)
                throw new XsltException("XTSE0550: The token '#all' must not appear together with any other value in the mode attribute", GetSourceLocation(element));
        }
        else if (matchAttr != null && _currentDefaultMode.HasValue)
        {
            // Template with no mode attribute: per spec, it matches in the default mode.
            // The effective default mode comes from this template's own default-mode
            // attribute, or inherited from the stylesheet's default-mode.
            modes.Add(_currentDefaultMode.Value);
        }

        // Track mode references for XTSE3085 (declared-modes) validation
        if (_usedModeReferences != null && matchAttr != null)
        {
            if (modes.Count == 0)
            {
                // No mode attribute on a matching template → uses unnamed mode implicitly
                _usedModeReferences.Add((TemplateIndex.DefaultModeSentinel, GetSourceLocation(element)));
            }
            else
            {
                foreach (var m in modes)
                {
                    // #all doesn't need validation (it's a wildcard)
                    if (!m.Equals(TemplateIndex.AllModeSentinel))
                        _usedModeReferences.Add((m, GetSourceLocation(element)));
                }
            }
        }

        var parameters = new List<XsltParam>();
        var bodyInstructions = new List<XsltInstruction>();
        var expandText = IsExpandTextActive(element);
        var seenBody = false;
        var seenParam = false;
        var contextItemUse = ContextItemUse.Optional;
        XdmSequenceType? contextItemAs = null;
        var seenContextItem = false;

        var nodeList = element.Nodes().ToList();
        var preserveSpace = IsXmlSpacePreserve(element);
        for (var ni = 0; ni < nodeList.Count; ni++)
        {
            switch (nodeList[ni])
            {
                case XElement child when child.Name == XsltNs + "param":
                    if (ShouldIncludeElement(child))
                    {
                        if (seenBody)
                            throw new XsltException("XTSE0010: xsl:param elements must come before any other content in xsl:template",
                                GetSourceLocation(child));
                        seenParam = true;
                        parameters.Add(ParseParam(child));
                    }
                    break;
                case XElement child when child.Name == XsltNs + "context-item":
                    if (ShouldIncludeElement(child))
                    {
                        // XTSE0010: xsl:context-item must come before xsl:param and body content
                        if (seenParam)
                            throw new XsltException("XTSE0010: xsl:context-item must appear before xsl:param in xsl:template",
                                GetSourceLocation(child));
                        if (seenBody)
                            throw new XsltException("XTSE0010: xsl:context-item must appear before any other content in xsl:template",
                                GetSourceLocation(child));

                        // XTSE0010: Only one xsl:context-item allowed per template
                        if (seenContextItem)
                            throw new XsltException("XTSE0010: Only one xsl:context-item declaration is allowed per template",
                                GetSourceLocation(child));
                        seenContextItem = true;

                        // XTSE0090: Validate allowed attributes (only 'as' and 'use')
                        ValidateAllowedAttributes(child, GetSourceLocation(child), "as", "use");

                        var useAttr = child.Attribute("use")?.Value?.Trim();
                        var ciAsAttr = child.Attribute("as")?.Value?.Trim();

                        contextItemUse = useAttr switch
                        {
                            "required" => ContextItemUse.Required,
                            "absent" => ContextItemUse.Absent,
                            null or "optional" => ContextItemUse.Optional,
                            _ => throw new XsltException($"XTSE0020: Invalid value '{useAttr}' for xsl:context-item/@use", GetSourceLocation(child))
                        };

                        if (ciAsAttr != null)
                        {
                            // XTSE3088: use="absent" with as attribute is a static error
                            if (contextItemUse == ContextItemUse.Absent)
                                throw new XsltException("XTSE3088: xsl:context-item specifies use='absent' together with an 'as' attribute",
                                    GetSourceLocation(child));
                            // Strip comments from as attribute (per bug 29814)
                            var cleanedAs = System.Text.RegularExpressions.Regex.Replace(ciAsAttr, @"\(:.*?:\)", "").Trim();
                            // XTSE0020: Occurrence indicators not allowed in xsl:context-item/@as
                            if (cleanedAs.EndsWith('?') || cleanedAs.EndsWith('*') || cleanedAs.EndsWith('+'))
                                throw new XsltException("XTSE0020: Occurrence indicator is not allowed in xsl:context-item/@as",
                                    GetSourceLocation(child));
                            contextItemAs = ParseSequenceType(cleanedAs, child);
                            // XPST0051: If the type is unknown (fell through to ItemType.Item)
                            // and the original string is a prefixed name, the type doesn't exist
                            if (contextItemAs.ItemType == ItemType.Item
                                && cleanedAs != "item()"
                                && !cleanedAs.StartsWith("item(", StringComparison.Ordinal)
                                && cleanedAs.Contains(':', StringComparison.Ordinal))
                                throw new XsltException($"XPST0051: Unknown type '{ciAsAttr}' in xsl:context-item/@as",
                                    GetSourceLocation(child));
                        }
                    }
                    break;
                case XElement child:
                    seenBody = true;
                    bodyInstructions.Add(ParseInstruction(child));
                    break;
                case XText:
                case XComment:
                case XProcessingInstruction:
                    // Use contiguous-run grouping (same as ParseSequenceConstructor):
                    // merge text across comments/PIs and retain the whole run if any
                    // text in it is non-whitespace. This preserves whitespace-only text
                    // nodes adjacent to non-whitespace text separated by XML comments.
                    var hasNonWhitespace = false;
                    var sb = new System.Text.StringBuilder();
                    while (ni < nodeList.Count && nodeList[ni] is XText or XComment or XProcessingInstruction)
                    {
                        if (nodeList[ni] is XText textNode)
                        {
                            sb.Append(textNode.Value);
                            if (!hasNonWhitespace && !IsXmlWhitespaceOnly(textNode.Value))
                                hasNonWhitespace = true;
                        }
                        ni++;
                    }
                    ni--; // Back up since the for loop will increment
                    var textValue = sb.ToString();

                    // In the preamble (before params/context-item), whitespace-only
                    // text nodes are always stripped even with xml:space="preserve".
                    // preserveSpace only applies once we're in the body section.
                    if (hasNonWhitespace || (seenBody && preserveSpace))
                    {
                        seenBody = true;
                        bodyInstructions.Add(CreateTextInstruction(textValue, expandText, element));
                    }
                    break;
            }
        }

        // XTSE0580: Duplicate parameter names within a template
        var paramNames = new HashSet<QName>();
        foreach (var p in parameters)
        {
            if (!paramNames.Add(p.Name))
                throw new XsltException($"XTSE0580: Duplicate parameter name '{p.Name.LocalName}' in template",
                    GetSourceLocation(element));
        }

        QName? templateName = nameAttr != null ? ParseQName(nameAttr.Value, element) : null;

        // XTSE0020: If the template has match but no name, only use="required" is allowed
        // (match templates always receive a context item via apply-templates)
        if (matchAttr != null && nameAttr == null && contextItemUse == ContextItemUse.Absent)
            throw new XsltException("XTSE0020: xsl:context-item use='absent' is not allowed on a template rule that has no name attribute",
                GetSourceLocation(element));

        // XTSE0080: Template name must not be in the XSLT namespace
        // Exception: xsl:initial-template is allowed (XSLT 3.0 spec section 3.11)
        if (templateName != null && templateName.Value.Namespace == NamespaceId.Xslt
            && templateName.Value.LocalName != "initial-template")
            throw new XsltException("XTSE0080: The name of a template must not be in the XSLT namespace",
                GetSourceLocation(element));

        _nsContext = prevContext;
        _currentDefaultMode = prevDefaultMode;
        return new XsltTemplate
        {
            Name = templateName,
            Match = matchAttr != null ? ParsePattern(matchAttr.Value, element) : null,
            Priority = priorityAttr != null ? ParsePriorityValue(priorityAttr.Value, GetSourceLocation(element)) : null,
            Modes = modes,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            Parameters = parameters,
            Body = new XsltSequenceConstructor { Instructions = bodyInstructions },
            Version = versionAttr?.Value ?? moduleVersion,
            BaseUri = ResolveEffectiveBaseUri(element),
            DefaultCollation = ResolveDefaultCollation(element.Attribute("default-collation")?.Value),
            ContextItemUse = contextItemUse,
            ContextItemAs = contextItemAs,
            Visibility = ParseVisibility(element.Attribute("visibility")?.Value)
        };
    }

    private XsltVariable ParseVariable(XElement element)
    {
        var prevContext = _nsContext;
        _nsContext = element;
        var nameAttr = element.Attribute("name");
        if (nameAttr == null)
            throw new XsltException("XTSE0010: xsl:variable must have a name attribute", GetSourceLocation(element));
        // XTSE0020: Validate name is a valid QName
        ValidateQNameValue(nameAttr.Value, "name", GetSourceLocation(element));
        var name = ParseQName(nameAttr.Value, element);
        var asAttr = element.Attribute("as");
        var selectAttr = element.Attribute("select");
        var staticAttr = element.Attribute("static");
        var location = GetSourceLocation(element);

        // XTSE0090: Validate no unknown attributes
        ValidateAllowedAttributes(element, location, "name", "select", "as", "static", "visibility");

        // Validate static attribute value
        if (staticAttr != null)
        {
            var staticVal = staticAttr.Value.Trim();
            if (staticVal is not ("yes" or "no" or "true" or "false" or "1" or "0"))
                throw new XsltException($"XTSE0020: Invalid value '{staticAttr.Value}' for 'static' attribute", location);
        }
        var isStatic = staticAttr != null && staticAttr.Value.Trim() is "yes" or "true" or "1";

        // XTSE0010: static variable must not have content body (must use select)
        // Evaluate use-when on child elements first — excluded elements don't count as content
        if (isStatic && element.Nodes().Any(n => (n is XElement el && ShouldIncludeElement(el)) || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0010: A variable with static='yes' must not have content (use the 'select' attribute instead)", location);

        // XTSE0090: visibility is not allowed on static variables
        if (isStatic && element.Attribute("visibility") != null)
            throw new XsltException("XTSE0090: The 'visibility' attribute is not allowed on a variable with static='yes'", location);

        // XTSE0620: select attribute and non-empty content are mutually exclusive
        // Evaluate use-when on child elements first — excluded elements don't count as content
        if (selectAttr != null && element.Nodes().Any(n => (n is XElement el && ShouldIncludeElement(el)) || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0620: An xsl:variable element must not have both a select attribute and non-empty content", location);

        _nsContext = prevContext;
        var selectExpr = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null;

        // XPST0008: A global variable must not reference itself in its own select expression
        // (XSLT 3.0 §9.9.2: the scope of a global variable excludes its own definition)
        var isGlobal = element.Parent?.Name.Namespace == XsltNs
                       && element.Parent.Name.LocalName is "stylesheet" or "transform" or "package";
        if (isGlobal && selectExpr != null && ContainsVariableReference(selectExpr, name))
            throw new XsltException($"XPST0008: Variable ${name.LocalName} references itself in its own definition", location);

        return new XsltVariable
        {
            Name = name,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            Select = selectExpr,
            Content = ParseContentBody(element, selectAttr),
            Static = isStatic,
            Visibility = ParseVisibility(element.Attribute("visibility")?.Value),
            BaseUri = ResolveEffectiveBaseUri(element),
            Version = element.Attribute("version")?.Value
        };
    }

    private XsltParam ParseParam(XElement element, bool isGlobal = false, bool allowTunnel = true)
    {
        var name = ParseQName(element.Attribute("name")!.Value, element);
        var asAttr = element.Attribute("as");
        var selectAttr = element.Attribute("select");
        var requiredAttr = element.Attribute("required");
        var tunnelAttr = element.Attribute("tunnel");
        var staticAttr = element.Attribute("static");
        var location = GetSourceLocation(element);

        // XTSE0090: Validate no unknown attributes
        ValidateAllowedAttributes(element, location, "name", "select", "as", "required", "tunnel", "static");

        // Validate static attribute value
        if (staticAttr != null)
        {
            var staticVal = staticAttr.Value.Trim();
            if (staticVal is not ("yes" or "no" or "true" or "false" or "1" or "0"))
                throw new XsltException($"XTSE0020: Invalid value '{staticAttr.Value}' for 'static' attribute", location);
        }
        var isStatic = staticAttr != null && staticAttr.Value.Trim() is "yes" or "true" or "1";

        // XTSE0020: static is only allowed on global params
        if (isStatic && !isGlobal)
            throw new XsltException("XTSE0020: The 'static' attribute is not allowed on a non-global xsl:param", location);

        // XTSE0010: static param must not have content body (must use select)
        // Evaluate use-when on child elements first — excluded elements don't count as content
        if (isStatic && element.Nodes().Any(n => (n is XElement el && ShouldIncludeElement(el)) || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0010: A parameter with static='yes' must not have content (use the 'select' attribute instead)", location);

        // XTSE0020: tunnel is not allowed on static params
        if (isStatic && tunnelAttr != null && tunnelAttr.Value.Trim() is "yes" or "true" or "1")
            throw new XsltException("XTSE0020: The 'tunnel' attribute is not allowed on a parameter with static='yes'", location);

        // XTSE0020: tunnel="yes" is only allowed on template params (not global or function params)
        if (!allowTunnel && tunnelAttr != null && tunnelAttr.Value.Trim() is "yes" or "true" or "1")
            throw new XsltException("XTSE0020: The 'tunnel' attribute with value 'yes' is not allowed on xsl:param in this context", location);

        // XTSE0020: Validate required attribute value
        if (requiredAttr != null)
        {
            var val = requiredAttr.Value.Trim();
            if (val != "yes" && val != "no" && val != "true" && val != "false" && val != "1" && val != "0")
                throw new XsltException($"XTSE0020: Invalid value '{requiredAttr.Value}' for required attribute: must be 'yes', 'no', 'true', 'false', '1', or '0'", location);
        }

        var isRequired = requiredAttr != null && (requiredAttr.Value.Trim() is "yes" or "true" or "1");

        // XTSE0010: required param must not have a default value (select or content)
        if (isRequired && selectAttr != null)
            throw new XsltException("XTSE0010: A parameter with required='yes' must not have a select attribute", location);
        if (isRequired && element.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0010: A parameter with required='yes' must not have content", location);

        // XTSE0620: select attribute and non-empty content are mutually exclusive
        if (selectAttr != null && element.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0620: An xsl:param element must not have both a select attribute and non-empty content", location);

        return new XsltParam
        {
            Name = name,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = ParseContentBody(element, selectAttr),
            Required = isRequired,
            Tunnel = ParseYesNoBoolean(tunnelAttr, "tunnel", location),
            Static = isStatic,
            BaseUri = ResolveEffectiveBaseUri(element),
            Version = element.Attribute("version")?.Value
        };
    }

    private XsltFunction ParseFunction(XElement element)
    {
        var location = GetSourceLocation(element);
        var nameValue = element.Attribute("name")?.Value
            ?? throw new XsltException("XTSE0010: xsl:function must have a name attribute", location);

        // Handle EQName syntax: Q{uri}local
        QName name;
        if (nameValue.StartsWith("Q{", StringComparison.Ordinal))
        {
            var closeBrace = nameValue.IndexOf('}', 2);
            if (closeBrace < 0)
                throw new XsltException($"XTSE0020: Invalid EQName '{nameValue}' for name attribute", location);
            var nsUri = nameValue[2..closeBrace];
            var localName = nameValue[(closeBrace + 1)..];
            if (string.IsNullOrEmpty(localName))
                throw new XsltException($"XTSE0020: Invalid EQName '{nameValue}': missing local name", location);
            var nsId = ResolveNamespaceUri(nsUri);
            name = new QName(nsId, localName);
        }
        else
        {
            // Validate QName syntax before parsing
            if (nameValue.Any(c => !char.IsLetterOrDigit(c) && c != ':' && c != '_' && c != '-' && c != '.'))
                throw new XsltException($"XTSE0020: Invalid QName '{nameValue}' for name attribute", location);
            name = ParseQName(nameValue, element);
        }

        // XTSE0740: Function name must be in a namespace
        if (name.Namespace == NamespaceId.None)
            throw new XsltException("XTSE0740: The name of a stylesheet function must have a non-null namespace URI", location);

        // XTSE0080: Function name must not be in a reserved namespace
        if (name.Namespace == NamespaceId.Xslt
            || name.Namespace == new NamespaceId(2) // xs (XMLSchema)
            || name.Namespace == NamespaceId.Fn
            || name.Namespace == NamespaceId.Map
            || name.Namespace == NamespaceId.Array
            || name.Namespace == NamespaceId.Math)
            throw new XsltException($"XTSE0080: The name of stylesheet function '{name}' is in a reserved namespace", location);

        // XTSE0020: Validate attribute values
        var overrideAttr = element.Attribute("override");
        if (overrideAttr != null && ParseYesNo(overrideAttr) == null)
            throw new XsltException($"XTSE0020: Invalid value '{overrideAttr.Value}' for override attribute", location);

        var overrideExtAttr = element.Attribute("override-extension-function");
        if (overrideExtAttr != null && ParseYesNo(overrideExtAttr) == null)
            throw new XsltException($"XTSE0020: Invalid value '{overrideExtAttr.Value}' for override-extension-function attribute", location);

        // XTSE0020: if both override and override-extension-function are present, they must agree
        if (overrideAttr != null && overrideExtAttr != null)
        {
            var overrideVal = ParseYesNo(overrideAttr) ?? false;
            var overrideExtVal = ParseYesNo(overrideExtAttr) ?? false;
            if (overrideVal != overrideExtVal)
                throw new XsltException("XTSE0020: The 'override' and 'override-extension-function' attributes have conflicting values on xsl:function", location);
        }

        var newEachTimeAttr = element.Attribute("new-each-time");
        if (newEachTimeAttr != null)
        {
            var val = newEachTimeAttr.Value.Trim();
            if (val != "yes" && val != "no" && val != "maybe")
                throw new XsltException($"XTSE0020: Invalid value '{newEachTimeAttr.Value}' for new-each-time attribute: must be 'yes', 'no', or 'maybe'", location);
        }

        var cacheAttr = element.Attribute("cache");
        if (cacheAttr != null && ParseYesNo(cacheAttr) == null)
            throw new XsltException($"XTSE0020: Invalid value '{cacheAttr.Value}' for cache attribute", location);

        var asAttr = element.Attribute("as");

        var parameters = new List<XsltParam>();
        var instructions = new List<XsltInstruction>();

        // Parse function body using Nodes() (not Elements()) to capture literal text
        // between instructions — e.g., <xsl:text/>[<xsl:value-of/>]<xsl:text/>
        var expandText = IsExpandTextActive(element);
        var preserveSpace = IsXmlSpacePreserve(element);
        var bodyNodes = element.Nodes().ToList();
        var pastParams = false;
        for (var ni = 0; ni < bodyNodes.Count; ni++)
        {
            switch (bodyNodes[ni])
            {
                case XElement child:
                    if (!ShouldIncludeElement(child)) continue;
                    if (!pastParams && child.Name == XsltNs + "param")
                    {
                        // XTSE0020: required='no' is not allowed on function params (params are always required)
                        var requiredAttr = child.Attribute("required");
                        if (requiredAttr != null && requiredAttr.Value.Trim() == "no")
                            throw new XsltException("XTSE0020: The required attribute of xsl:param within xsl:function must not have the value 'no'",
                                GetSourceLocation(child));
                        // XTSE0760: Function params must not have default values (no select, no content)
                        if (child.Attribute("select") != null)
                            throw new XsltException("XTSE0760: An xsl:param element within xsl:function must not have a select attribute",
                                GetSourceLocation(child));
                        if (child.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
                            throw new XsltException("XTSE0760: An xsl:param element within xsl:function must not have non-empty content",
                                GetSourceLocation(child));
                        parameters.Add(ParseParam(child, allowTunnel: false));
                        continue;
                    }
                    pastParams = true;
                    instructions.Add(ParseInstruction(child));
                    break;
                case XText:
                    // Collect contiguous text run (same as ParseSequenceConstructor)
                    var hasNonWhitespace = false;
                    var sb = new System.Text.StringBuilder();
                    while (ni < bodyNodes.Count && bodyNodes[ni] is XText or XComment or XProcessingInstruction)
                    {
                        if (bodyNodes[ni] is XText textNode)
                        {
                            sb.Append(textNode.Value);
                            if (!hasNonWhitespace && !IsXmlWhitespaceOnly(textNode.Value))
                                hasNonWhitespace = true;
                        }
                        ni++;
                    }
                    ni--;
                    if (hasNonWhitespace || preserveSpace)
                    {
                        pastParams = true;
                        // Use XsltText (not XsltLiteralText) for function body literal text.
                        // XsltText routes through WriteTextItem, which uses the sequence
                        // accumulator in function bodies. This ensures literal text like
                        // "[" between xsl:text and xsl:value-of is captured alongside other
                        // text items and properly merged into the function result.
                        var textValue = sb.ToString();
                        if (expandText && textValue.Contains('{', StringComparison.Ordinal))
                            instructions.Add(CreateTextInstruction(textValue, expandText, element));
                        else
                            instructions.Add(new XsltText { Value = textValue });
                    }
                    break;
            }
        }

        // XTSE0580: No two sibling xsl:param elements may have the same expanded QName
        var paramNames = new HashSet<string>();
        foreach (var p in parameters)
        {
            var paramKey = $"{p.Name.Namespace}:{p.Name.LocalName}";
            if (!paramNames.Add(paramKey))
                throw new XsltException($"XTSE0580: Duplicate parameter name '{p.Name.LocalName}' in xsl:function", location);
        }

        var body = new XsltSequenceConstructor { Instructions = instructions };

        // XTSE3430: Check function streamability if declared
        var streamabilityAttr = element.Attribute("streamability");
        if (streamabilityAttr != null)
        {
            var streamability = streamabilityAttr.Value.Trim();
            if (streamability is "absorbing" or "filter" or "inspection" or "shallow-descent"
                or "deep-descent" or "ascent")
            {
                StreamabilityChecker.CheckStreamableFunctionBody(body, streamability, parameters, location, name);
            }
        }

        var streamabilityValue = streamabilityAttr?.Value.Trim();
        if (streamabilityValue is not ("absorbing" or "filter" or "inspection"
            or "shallow-descent" or "deep-descent" or "ascent"))
            streamabilityValue = null;

        return new XsltFunction
        {
            Name = name,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            Parameters = parameters,
            Body = body,
            Override = ParseYesNo(overrideAttr) ?? true,
            Visibility = ParseVisibility(element.Attribute("visibility")?.Value),
            Cache = ParseYesNo(cacheAttr) ?? false,
            NewEachTime = newEachTimeAttr?.Value?.Trim(),
            Streamability = streamabilityValue
        };
    }

    private XsltKey ParseKey(XElement element, string? stylesheetDefaultCollation = null)
    {
        // XTSE0090: Validate no unknown attributes
        ValidateAllowedAttributes(element, GetSourceLocation(element), "name", "match", "use", "composite", "collation", "default-collation");

        // XTSE0020: Validate name is a valid QName
        ValidateQNameValue(element.Attribute("name")!.Value, "name", GetSourceLocation(element));

        var name = ParseQName(element.Attribute("name")!.Value, element);
        var match = ParsePattern(element.Attribute("match")!.Value, element);
        var useAttr = element.Attribute("use");
        var collationAttr = element.Attribute("collation");
        var compositeAttr = element.Attribute("composite");
        var defaultCollationAttr = element.Attribute("default-collation");

        // XTSE1210: xsl:key collation must be a recognized collation URI
        if (collationAttr != null)
            ValidateCollationList(collationAttr.Value, GetSourceLocation(element), "XTSE1210");

        // XTSE1205: xsl:key must have either use attribute or non-empty content, not both
        var hasContent = element.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value)));
        if (useAttr != null && hasContent)
            throw new XsltException("XTSE1205: An xsl:key element must not have both a use attribute and non-empty content",
                GetSourceLocation(element));
        if (useAttr == null && !hasContent)
            throw new XsltException("XTSE1205: An xsl:key element must have either a use attribute or non-empty content",
                GetSourceLocation(element));

        // Resolve effective collation: explicit collation > default-collation on element > stylesheet default collation
        var effectiveCollation = collationAttr?.Value
            ?? ResolveDefaultCollation(defaultCollationAttr?.Value)
            ?? stylesheetDefaultCollation;

        return new XsltKey
        {
            Name = name,
            Match = match,
            Use = useAttr != null ? ParseExpr(useAttr.Value, useAttr) : null,
            UseContent = useAttr == null && element.HasElements
                ? ParseSequenceConstructor(element)
                : null,
            Collation = effectiveCollation,
            Composite = ParseYesNo(compositeAttr) ?? false
        };
    }

    private static XsltOutput ParseOutput(XElement element)
    {
        // XTSE1570: Validate method attribute
        var methodAttr = element.Attribute("method");
        if (methodAttr != null)
        {
            var methodValue = methodAttr.Value.Trim();
            if (methodValue.Contains(':', StringComparison.Ordinal))
            {
                // Prefixed QName for extension method — validate it's a valid QName
                var parts = methodValue.Split(':', 2);
                if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1])
                    || methodValue.Contains("::", StringComparison.Ordinal))
                    throw new XsltException($"XTSE1570: Invalid output method '{methodValue}': must be a valid EQName");
                // Validate the prefix is bound
                if (element.GetNamespaceOfPrefix(parts[0]) == null)
                    throw new XsltException($"XTSE1570: Output method '{methodValue}' uses undeclared prefix '{parts[0]}'");
            }
            else if (methodValue is not ("xml" or "html" or "xhtml" or "text" or "json" or "adaptive"))
            {
                throw new XsltException($"XTSE1570: Invalid output method '{methodValue}': unprefixed method must be one of xml, html, xhtml, text, json, or adaptive");
            }
        }

        var output = new XsltOutput
        {
            Name = element.Attribute("name") != null
                ? ParseQName(element.Attribute("name")!.Value, element)
                : null,
            Method = element.Attribute("method")?.Value switch
            {
                "xml" => OutputMethod.Xml,
                "html" => OutputMethod.Html,
                "xhtml" => OutputMethod.Xhtml,
                "text" => OutputMethod.Text,
                "json" => OutputMethod.Json,
                "adaptive" => OutputMethod.Adaptive,
                "csv" => OutputMethod.Csv,
                null => null,
                _ => OutputMethod.Xml
            },
            Version = element.Attribute("version")?.Value,
            Encoding = element.Attribute("encoding")?.Value,
            OmitXmlDeclaration = ParseYesNo(element.Attribute("omit-xml-declaration")),
            Standalone = ParseYesNo(element.Attribute("standalone")),
            DoctypePublic = element.Attribute("doctype-public")?.Value,
            DoctypeSystem = element.Attribute("doctype-system")?.Value,
            Indent = ParseYesNo(element.Attribute("indent")),
            MediaType = element.Attribute("media-type")?.Value,
            ItemSeparator = element.Attribute("item-separator")?.Value,
            NormalizationForm = element.Attribute("normalization-form")?.Value,
            UndeclarePrefixes = ParseYesNo(element.Attribute("undeclare-prefixes")),
            IncludeContentType = ParseYesNo(element.Attribute("include-content-type")),
            HtmlVersion = element.Attribute("html-version")?.Value,
            BuildTree = element.Attribute("build-tree")?.Value,
            AllowDuplicateNames = ParseYesNo(element.Attribute("allow-duplicate-names")),
            ByteOrderMark = ParseYesNo(element.Attribute("byte-order-mark")),
            JsonNodeOutputMethod = element.Attribute("json-node-output-method")?.Value,
        };

        // Validate html-version: must be a valid decimal number (XTSE0020)
        if (output.HtmlVersion != null &&
            !decimal.TryParse(output.HtmlVersion, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            throw new XsltException($"XTSE0020: Invalid html-version value '{output.HtmlVersion}': must be a decimal number");
        }

        // XTSE0020: reject invalid values for the yes-or-no serialization attributes.
        // The serialization spec ties these to SEPM0016 at serialization time; XSLT
        // catches them statically as XTSE0020. Only "yes"/"no"/"true"/"false"/"1"/"0"
        // (case-sensitive, per xsl:yes-or-no) are permitted, so uppercase values such as
        // "TRUE"/"YES" are errors rather than being silently ignored.
        ValidateYesNoOutputAttribute(element, "omit-xml-declaration");
        ValidateYesNoOutputAttribute(element, "indent");
        ValidateYesNoOutputAttribute(element, "include-content-type");
        ValidateYesNoOutputAttribute(element, "allow-duplicate-names");
        ValidateYesNoOutputAttribute(element, "byte-order-mark");
        ValidateYesNoOutputAttribute(element, "escape-uri-attributes");
        ValidateYesNoOutputAttribute(element, "undeclare-prefixes");

        // standalone additionally permits the value "omit".
        var standaloneAttr = element.Attribute("standalone");
        if (standaloneAttr != null)
        {
            var v = standaloneAttr.Value.Trim();
            if (v is not ("yes" or "no" or "true" or "false" or "1" or "0" or "omit"))
                throw new XsltException($"XTSE0020: Invalid standalone value '{standaloneAttr.Value}': must be yes, no, true, false, 1, 0, or omit");
        }

        // doctype-public must be a valid XML PubidLiteral (XTSE0020).
        var doctypePublicAttr = element.Attribute("doctype-public");
        if (doctypePublicAttr != null && !IsValidPublicId(doctypePublicAttr.Value))
            throw new XsltException($"XTSE0020: Invalid doctype-public value '{doctypePublicAttr.Value}': not a valid public identifier");

        var cdataAttr = element.Attribute("cdata-section-elements");
        if (cdataAttr != null)
        {
            output.CdataSectionElements = cdataAttr.Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(n => ParseQName(n, element))
                .ToHashSet();
        }

        var useCharMapsAttr = element.Attribute("use-character-maps");
        if (useCharMapsAttr != null)
        {
            foreach (var n in useCharMapsAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                output.UseCharacterMaps.Add(ParseQName(n, element));
            }
        }

        var suppressIndentAttr = element.Attribute("suppress-indentation");
        if (suppressIndentAttr != null)
        {
            output.SuppressIndentation = suppressIndentAttr.Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(n => ParseQName(n, element))
                .ToHashSet();
        }

        return output;
    }

    /// <summary>
    /// XTSE0020: throw when a present xsl:output yes-or-no attribute carries a value outside
    /// the permitted set ("yes"/"no"/"true"/"false"/"1"/"0"). Absent attributes are ignored.
    /// </summary>
    private static void ValidateYesNoOutputAttribute(XElement element, string attrName)
    {
        var attr = element.Attribute(attrName);
        if (attr == null) return;
        if (ParseYesNo(attr) == null)
            throw new XsltException($"XTSE0020: Invalid value '{attr.Value}' for xsl:output {attrName} attribute (must be yes, no, true, false, 1, or 0)");
    }

    /// <summary>
    /// XTSE0020: the disable-output-escaping attribute (on xsl:text / xsl:value-of) is a
    /// yes-or-no; reject any present value outside the permitted set (e.g. "YES", " ").
    /// </summary>
    private static void ValidateDoeAttribute(XAttribute? attr, SourceLocation? location)
    {
        if (attr == null) return;
        if (ParseYesNo(attr) == null)
            throw new XsltException($"XTSE0020: Invalid disable-output-escaping value '{attr.Value}' (must be yes, no, true, false, 1, or 0)", location);
    }

    /// <summary>Validates an XML PubidLiteral (public identifier) character set.</summary>
    private static bool IsValidPublicId(string value)
    {
        foreach (var c in value)
        {
            bool ok = c is ' ' or '\r' or '\n'
                || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || "-'()+,./:=?;!*#@$_%".Contains(c, StringComparison.Ordinal);
            if (!ok) return false;
        }
        return true;
    }

    private XsltAttributeSet ParseAttributeSet(XElement element)
    {
        // XTSE0090: Validate no unknown attributes
        ValidateAllowedAttributes(element, GetSourceLocation(element), "name", "use-attribute-sets", "visibility", "streamable");

        // XTSE0020: Validate name is a valid QName
        ValidateQNameValue(element.Attribute("name")!.Value, "name", GetSourceLocation(element));

        var name = ParseQName(element.Attribute("name")!.Value, element);
        var useAttr = element.Attribute("use-attribute-sets");

        var useAttributeSets = new List<QName>();
        if (useAttr != null)
        {
            foreach (var n in useAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                useAttributeSets.Add(ParseQName(n, element));
            }
        }

        // XTSE0010: xsl:attribute-set may only contain xsl:attribute children and no text
        foreach (var node in element.Nodes())
        {
            if (node is XText text && !IsXmlWhitespaceOnly(text.Value))
                throw new XsltException("XTSE0010: Text content is not allowed in xsl:attribute-set", GetSourceLocation(element));
            if (node is XElement child && child.Name != XsltNs + "attribute")
                throw new XsltException($"XTSE0010: Only xsl:attribute is allowed as a child of xsl:attribute-set, found {child.Name.LocalName}",
                    GetSourceLocation(child));
        }

        var attributes = new List<XsltAttribute>();
        foreach (var child in element.Elements(XsltNs + "attribute"))
        {
            attributes.Add((XsltAttribute)ParseInstruction(child));
        }

        var streamableAttr = element.Attribute("streamable")?.Value?.Trim();
        var streamable = streamableAttr != null && NormalizeYesNo(streamableAttr, "streamable", "xsl:attribute-set", element);

        // XTSE3430: If declared streamable="yes", validate that attribute expressions are actually streamable
        if (streamable)
        {
            StreamabilityChecker.CheckStreamableAttributeSet(attributes, GetSourceLocation(element));
        }

        return new XsltAttributeSet
        {
            Name = name,
            UseAttributeSets = useAttributeSets,
            Attributes = attributes,
            Visibility = ParseVisibility(element.Attribute("visibility")?.Value),
            Streamable = streamable,
            BaseUri = ResolveEffectiveBaseUri(element)
        };
    }

    private static XsltCharacterMap ParseCharacterMap(XElement element)
    {
        var name = ParseQName(element.Attribute("name")!.Value, element);
        var useAttr = element.Attribute("use-character-maps");

        var useCharacterMaps = new List<QName>();
        if (useAttr != null)
        {
            foreach (var n in useAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                useCharacterMaps.Add(ParseQName(n, element));
            }
        }

        var mappings = new Dictionary<int, string>();
        foreach (var child in element.Elements(XsltNs + "output-character"))
        {
            var charAttr = child.Attribute("character")?.Value
                ?? throw new XsltException("XTSE0010: xsl:output-character requires a 'character' attribute",
                    GetSourceLocation(child));
            var stringAttr = child.Attribute("string")?.Value
                ?? throw new XsltException("XTSE0010: xsl:output-character requires a 'string' attribute",
                    GetSourceLocation(child));
            // The 'character' attribute must be a single XML character (one Unicode code
            // point). That is one UTF-16 code unit for BMP characters, or a surrogate pair
            // for astral characters (> U+FFFF). Key the mapping on the code point so astral
            // characters are not silently dropped (character-map-007/010).
            if (charAttr.Length == 1)
            {
                mappings[charAttr[0]] = stringAttr;
            }
            else if (charAttr.Length == 2 && char.IsHighSurrogate(charAttr[0]) && char.IsLowSurrogate(charAttr[1]))
            {
                mappings[char.ConvertToUtf32(charAttr[0], charAttr[1])] = stringAttr;
            }
        }

        return new XsltCharacterMap
        {
            Name = name,
            UseCharacterMaps = useCharacterMaps,
            Mappings = mappings
        };
    }

    private static XsltDecimalFormat ParseDecimalFormat(XElement element)
    {
        // XTSE0020: Validate name is a valid QName (no AVTs, no invalid characters)
        var nameVal = element.Attribute("name")?.Value;
        if (nameVal != null)
            ValidateQNameValue(nameVal, "name", GetSourceLocation(element));

        // Validate single-character attributes (XTSE0020)
        ValidateSingleCharAttr(element, "decimal-separator");
        ValidateSingleCharAttr(element, "grouping-separator");
        ValidateSingleCharAttr(element, "minus-sign");
        ValidateSingleCharAttr(element, "percent");
        ValidateSingleCharAttr(element, "per-mille");
        ValidateSingleCharAttr(element, "zero-digit");
        ValidateSingleCharAttr(element, "digit");
        ValidateSingleCharAttr(element, "pattern-separator");
        ValidateSingleCharAttr(element, "exponent-separator");

        var decSep = GetFirstTextElement(element, "decimal-separator") ?? ".";
        var grpSep = GetFirstTextElement(element, "grouping-separator") ?? ",";
        var percent = GetFirstTextElement(element, "percent") ?? "%";
        var perMille = GetFirstTextElement(element, "per-mille") ?? "\u2030";
        var zeroDigit = GetFirstTextElement(element, "zero-digit") ?? "0";
        var digit = GetFirstTextElement(element, "digit") ?? "#";
        var patSep = GetFirstTextElement(element, "pattern-separator") ?? ";";
        var expSep = GetFirstTextElement(element, "exponent-separator") ?? "e";

        // Validate zero-digit is a Unicode digit character (XTSE1295)
        if (element.Attribute("zero-digit") != null)
        {
            var zdRune = GetFirstRune(zeroDigit);
            if (!System.Text.Rune.IsDigit(zdRune) || System.Text.Rune.GetNumericValue(zdRune) != 0)
                throw new XsltException(
                    $"XTSE1295: The zero-digit character '{zeroDigit}' must be a Unicode digit with value zero",
                    GetSourceLocation(element));
        }

        // Note: XTSE1300 (duplicate character roles) is checked AFTER all merging
        // in ValidateDecimalFormats(), since partial declarations may have false conflicts
        // with default values that will be resolved during merge.

        // Track which attributes are explicitly set (for merge conflict detection)
        var explicitAttrs = new HashSet<string>();
        string[] charAttrNames = ["decimal-separator", "grouping-separator", "infinity", "minus-sign",
            "NaN", "percent", "per-mille", "zero-digit", "digit", "pattern-separator", "exponent-separator"];
        foreach (var attrName in charAttrNames)
        {
            if (element.Attribute(attrName) != null)
                explicitAttrs.Add(attrName);
        }

        return new XsltDecimalFormat
        {
            Name = element.Attribute("name") != null
                ? ParseQName(element.Attribute("name")!.Value, element)
                : null,
            DecimalSeparator = decSep,
            GroupingSeparator = grpSep,
            Infinity = element.Attribute("infinity")?.Value ?? "Infinity",
            MinusSign = GetFirstTextElement(element, "minus-sign") ?? "-",
            NaN = element.Attribute("NaN")?.Value ?? "NaN",
            Percent = percent,
            PerMille = perMille,
            ZeroDigit = zeroDigit,
            Digit = digit,
            PatternSeparator = patSep,
            ExponentSeparator = expSep,
            ExplicitAttributes = explicitAttrs
        };
    }

    private static void ValidateSingleCharAttr(XElement element, string attrName)
    {
        var attr = element.Attribute(attrName);
        if (attr != null && new System.Globalization.StringInfo(attr.Value).LengthInTextElements != 1)
            throw new XsltException(
                $"XTSE0020: Attribute '{attrName}' must be a single character, got '{attr.Value}'",
                GetSourceLocation(element));
    }

    /// <summary>
    /// Extracts the first text element (Unicode codepoint, including surrogate pairs) from an attribute value.
    /// Returns null if the attribute is not present.
    /// </summary>
    private static string? GetFirstTextElement(XElement element, string attrName)
    {
        var attr = element.Attribute(attrName);
        if (attr == null) return null;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(attr.Value);
        return enumerator.MoveNext() ? enumerator.GetTextElement() : null;
    }

    /// <summary>
    /// Gets the first Rune from a text element string (for Unicode property checks).
    /// </summary>
    private static System.Text.Rune GetFirstRune(string textElement)
    {
        System.Text.Rune.DecodeFromUtf16(textElement, out var rune, out _);
        return rune;
    }

    /// <summary>
    /// Checks for a conflicting attribute value during merge. Returns a conflict description if found,
    /// or null if no conflict. Only reports a conflict when BOTH declarations explicitly set the
    /// attribute to different values. Does NOT throw — conflicts are deferred until after import resolution.
    /// </summary>
    private static string? FindDecimalFormatConflict(string attrName, object existingVal, object newVal,
        XElement newElement, HashSet<string> existingExplicit)
    {
        // Only conflict when both declarations explicitly set the attribute to different values
        var newExplicit = newElement.Attribute(attrName) != null;
        if (newExplicit && existingExplicit.Contains(attrName) && !Equals(existingVal, newVal))
            return $"XTSE1290: Conflicting values for '{attrName}' in decimal-format declaration";
        return null;
    }

    /// <summary>
    /// Merges two decimal format declarations with the same name.
    /// Per XSLT spec, multiple declarations at the same import precedence are allowed if they don't
    /// conflict on the same attribute. Conflicts are recorded (not thrown) so higher-precedence
    /// import declarations can override them.
    /// </summary>
    private static XsltDecimalFormat MergeDecimalFormats(XsltDecimalFormat existing, XsltDecimalFormat newDf, XElement newElement)
    {
        var existingExplicit = existing.ExplicitAttributes;

        // Detect conflicting attribute values (XTSE1290) — deferred until after import resolution
        // Only flags when BOTH declarations explicitly set the same attribute to different values
        string? conflict = FindDecimalFormatConflict("decimal-separator", existing.DecimalSeparator, newDf.DecimalSeparator, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("grouping-separator", existing.GroupingSeparator, newDf.GroupingSeparator, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("infinity", existing.Infinity, newDf.Infinity, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("minus-sign", existing.MinusSign, newDf.MinusSign, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("NaN", existing.NaN, newDf.NaN, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("percent", existing.Percent, newDf.Percent, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("per-mille", existing.PerMille, newDf.PerMille, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("zero-digit", existing.ZeroDigit, newDf.ZeroDigit, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("digit", existing.Digit, newDf.Digit, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("pattern-separator", existing.PatternSeparator, newDf.PatternSeparator, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("exponent-separator", existing.ExponentSeparator, newDf.ExponentSeparator, newElement, existingExplicit);

        // Merge explicit attribute sets
        var mergedExplicit = new HashSet<string>(existingExplicit);
        mergedExplicit.UnionWith(newDf.ExplicitAttributes);

        return new XsltDecimalFormat
        {
            Name = existing.Name,
            DecimalSeparator = newElement.Attribute("decimal-separator") != null ? newDf.DecimalSeparator : existing.DecimalSeparator,
            GroupingSeparator = newElement.Attribute("grouping-separator") != null ? newDf.GroupingSeparator : existing.GroupingSeparator,
            Infinity = newElement.Attribute("infinity") != null ? newDf.Infinity : existing.Infinity,
            MinusSign = newElement.Attribute("minus-sign") != null ? newDf.MinusSign : existing.MinusSign,
            NaN = newElement.Attribute("NaN") != null ? newDf.NaN : existing.NaN,
            Percent = newElement.Attribute("percent") != null ? newDf.Percent : existing.Percent,
            PerMille = newElement.Attribute("per-mille") != null ? newDf.PerMille : existing.PerMille,
            ZeroDigit = newElement.Attribute("zero-digit") != null ? newDf.ZeroDigit : existing.ZeroDigit,
            Digit = newElement.Attribute("digit") != null ? newDf.Digit : existing.Digit,
            PatternSeparator = newElement.Attribute("pattern-separator") != null ? newDf.PatternSeparator : existing.PatternSeparator,
            ExponentSeparator = newElement.Attribute("exponent-separator") != null ? newDf.ExponentSeparator : existing.ExponentSeparator,
            ExplicitAttributes = mergedExplicit,
            HasConflict = existing.HasConflict || conflict != null,
            ConflictDescription = conflict ?? existing.ConflictDescription
        };
    }

    private XsltAccumulator ParseAccumulator(XElement element)
    {
        var name = ParseQName(element.Attribute("name")!.Value, element);
        var asAttr = element.Attribute("as");
        var initialValueAttr = element.Attribute("initial-value");
        var streamableAttr = element.Attribute("streamable");

        var rules = new List<XsltAccumulatorRule>();
        foreach (var child in element.Elements(XsltNs + "accumulator-rule"))
        {
            var matchStr = child.Attribute("match")!.Value;

            // XPST0008: $value is not in scope in accumulator match patterns (only in select)
            if (System.Text.RegularExpressions.Regex.IsMatch(matchStr, @"\$value\b"))
                throw new XsltException("XPST0008: Variable reference '$value' is not allowed in accumulator match pattern",
                    GetSourceLocation(child));

            var match = ParsePattern(matchStr, child);
            var phase = child.Attribute("phase")?.Value;
            var selectAttr = child.Attribute("select");

            rules.Add(new XsltAccumulatorRule
            {
                Match = match,
                Phase = phase == "end" ? AccumulatorPhase.End : AccumulatorPhase.Start,
                Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
                Content = selectAttr == null && child.HasElements
                    ? ParseSequenceConstructor(child)
                    : null
            });
        }

        if (rules.Count == 0)
            throw new XsltException("XTSE0010: xsl:accumulator must contain at least one xsl:accumulator-rule", GetSourceLocation(element));

        var isStreamable = NormalizeYesNo(streamableAttr?.Value, "streamable", "xsl:accumulator", element);

        // XTSE3430: Streamable accumulator patterns must be motionless
        // (no predicates, no upward/sibling axis navigation)
        if (isStreamable)
        {
            foreach (var rule in rules)
            {
                if (HasNonMotionlessPredicates(rule.Match))
                    throw new XsltException("XTSE3430: Accumulator rule pattern is not motionless: patterns in a streamable accumulator must not contain predicates",
                        GetSourceLocation(element));
            }
        }

        return new XsltAccumulator
        {
            Name = name,
            SourceName = element.Attribute("name")!.Value,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            InitialValue = ParseExpr(initialValueAttr!.Value),
            Rules = rules,
            Streamable = isStreamable
        };
    }

    private static XsltMode ParseMode(XElement element)
    {
        // XTSE0010: xsl:mode must be empty (no child elements)
        if (element.Elements().Any())
            throw new XsltException("XTSE0010: xsl:mode must be empty",
                GetSourceLocation(element));

        var nameAttr = element.Attribute("name");
        var streamableAttr = element.Attribute("streamable");
        var onNoMatchAttr = element.Attribute("on-no-match");
        var onMultipleMatchAttr = element.Attribute("on-multiple-match");
        var visibilityAttr = element.Attribute("visibility");
        var useAccumulatorsAttr = element.Attribute("use-accumulators");

        // Parse use-accumulators: "#all" or space-separated list of QNames
        var useAllAccumulators = false;
        var useAccumulatorNames = new List<QName>();
        if (useAccumulatorsAttr != null)
        {
            var val = useAccumulatorsAttr.Value.Trim();
            if (val == "#all")
            {
                useAllAccumulators = true;
            }
            else
            {
                foreach (var name in val.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    useAccumulatorNames.Add(ParseQName(name, element));
                }
            }
        }

        // XTSE0020: The unnamed mode cannot be public or final
        if (nameAttr == null && visibilityAttr != null)
        {
            var vis = visibilityAttr.Value.Trim();
            if (vis is "public" or "final")
                throw new XsltException(
                    $"XTSE0020: The unnamed mode cannot be {vis}",
                    GetSourceLocation(element));
        }

        // Validate warning-on-no-match (XTSE0020 for invalid values like "Yes")
        var warningOnNoMatchAttr = element.Attribute("warning-on-no-match");
        if (warningOnNoMatchAttr != null)
            NormalizeYesNo(warningOnNoMatchAttr.Value.Trim(), "warning-on-no-match", "xsl:mode", element);

        // Validate warning-on-multiple-match (XTSE0020 for invalid values like "Yes")
        var warningOnMultipleMatchAttr = element.Attribute("warning-on-multiple-match");
        if (warningOnMultipleMatchAttr != null)
            NormalizeYesNo(warningOnMultipleMatchAttr.Value.Trim(), "warning-on-multiple-match", "xsl:mode", element);

        // Validate typed attribute (XTSE0020 for invalid values like "No")
        var typedAttr = element.Attribute("typed");
        if (typedAttr != null)
        {
            var typedVal = typedAttr.Value.Trim();
            if (typedVal is not ("yes" or "no" or "strict" or "lax" or "unspecified" or "true" or "false" or "0" or "1"))
                throw new XsltException(
                    $"XTSE0020: Invalid value '{typedVal}' for attribute 'typed' on xsl:mode (must be 'yes', 'no', 'strict', 'lax', or 'unspecified')",
                    GetSourceLocation(element));
        }

        var isTyped = typedAttr != null && typedAttr.Value.Trim() is "yes" or "strict" or "lax" or "true" or "1";

        return new XsltMode
        {
            Name = nameAttr != null ? ParseQName(nameAttr.Value, element) : null,
            Streamable = streamableAttr?.Value == "yes",
            OnNoMatch = onNoMatchAttr != null ? ParseOnNoMatchBehavior(onNoMatchAttr.Value, element) : null,
            OnMultipleMatch = onMultipleMatchAttr != null ? ParseOnMultipleMatchBehavior(onMultipleMatchAttr.Value, element) : OnMultipleMatchBehavior.UseLast,
            Visibility = ParseVisibility(visibilityAttr?.Value),
            VisibilityAttr = visibilityAttr?.Value.Trim(),
            UseAllAccumulators = useAllAccumulators,
            UseAccumulatorNames = useAccumulatorNames,
            UseAccumulatorsAttr = useAccumulatorsAttr?.Value.Trim(),
            Typed = isTyped
        };
    }

    private static OnNoMatchBehavior ParseOnNoMatchBehavior(string value, XElement element) => value.Trim() switch
    {
        "deep-copy" => OnNoMatchBehavior.DeepCopy,
        "shallow-copy" => OnNoMatchBehavior.ShallowCopy,
        "deep-skip" => OnNoMatchBehavior.DeepSkip,
        "shallow-skip" => OnNoMatchBehavior.ShallowSkip,
        "text-only-copy" => OnNoMatchBehavior.TextOnlyCopy,
        "fail" => OnNoMatchBehavior.Fail,
        _ => throw new XsltException(
            $"XTSE0020: Invalid value '{value}' for attribute 'on-no-match' on xsl:mode (must be 'deep-copy', 'shallow-copy', 'deep-skip', 'shallow-skip', 'text-only-copy', or 'fail')",
            GetSourceLocation(element))
    };

    /// <summary>
    /// Normalizes a yes/no attribute value, also accepting boolean-like values (true/false/0/1).
    /// Throws XTSE0020 for invalid values like "No", "Yes" (case-sensitive).
    /// </summary>
    private static bool NormalizeYesNo(string? value, string attrName, string elementName, XElement element)
    {
        return value?.Trim() switch
        {
            null or "no" or "false" or "0" => false,
            "yes" or "true" or "1" => true,
            _ => throw new XsltException(
                $"XTSE0020: Invalid value '{value}' for attribute '{attrName}' on {elementName} (must be 'yes' or 'no')",
                GetSourceLocation(element))
        };
    }

    private static OnMultipleMatchBehavior ParseOnMultipleMatchBehavior(string value, XElement element) => value.Trim() switch
    {
        "use-last" => OnMultipleMatchBehavior.UseLast,
        "fail" => OnMultipleMatchBehavior.Fail,
        _ => throw new XsltException(
            $"XTSE0020: Invalid value '{value}' for attribute 'on-multiple-match' on xsl:mode (must be 'use-last' or 'fail')",
            GetSourceLocation(element))
    };

    private static Visibility ParseVisibility(string? value) => value switch
    {
        "public" => Visibility.Public,
        "private" => Visibility.Private,
        "final" => Visibility.Final,
        "abstract" => Visibility.Abstract,
        "hidden" => Visibility.Hidden,
        // Default per XSLT 3.0 §3.5: components declared at the top level of an
        // xsl:package element default to private, but components in any other
        // stylesheet module (the common case) default to public. Defaulting to
        // Private here broke `xsl:evaluate` calls into ordinary stylesheet
        // functions like Docbook TNG's fp:pi-from-list — XTDE3160 fired even
        // though the function was correctly accessible from the rest of the
        // stylesheet. Package-aware code paths set Private explicitly when
        // needed; this default covers the non-package common case.
        _ => Visibility.Public
    };

    private XsltSequenceConstructor ParseSequenceConstructor(XElement element)
    {
        var instructions = new List<XsltInstruction>();
        var expandText = IsExpandTextActive(element);

        // Per XSLT spec: comments and PIs are stripped first, then for each
        // contiguous run of sibling text nodes, if ANY text node in the run is
        // non-whitespace, ALL text nodes in the run are retained. This handles
        // CDATA sections adjacent to whitespace and comments splitting text nodes.
        var nodes = element.Nodes().ToList();
        var isInsideText = element.Name == XsltNs + "text";
        var preserveSpace = IsXmlSpacePreserve(element);
        for (var ni = 0; ni < nodes.Count; ni++)
        {
            switch (nodes[ni])
            {
                case XText:
                    // Collect the entire contiguous run of text nodes (and comments/PIs)
                    var runStart = ni;
                    var hasNonWhitespace = false;
                    var sb = new System.Text.StringBuilder();
                    while (ni < nodes.Count && nodes[ni] is XText or XComment or XProcessingInstruction)
                    {
                        if (nodes[ni] is XText textNode)
                        {
                            sb.Append(textNode.Value);
                            if (!hasNonWhitespace && !IsXmlWhitespaceOnly(textNode.Value))
                                hasNonWhitespace = true;
                        }
                        // Comments and PIs are stripped (skipped)
                        ni++;
                    }
                    ni--; // Back up since the for loop will increment
                    var value = sb.ToString();

                    // Retain text if: any text in run is non-whitespace, inside xsl:text,
                    // or xml:space="preserve"
                    if (hasNonWhitespace || isInsideText || preserveSpace)
                    {
                        instructions.Add(CreateTextInstruction(value, expandText, element));
                    }
                    break;

                case XElement child:
                    instructions.Add(ParseInstruction(child));
                    break;
            }
        }

        // XTSE0010: xsl:on-empty must be the absolute final instruction in a sequence constructor.
        // Nothing — not even xsl:on-non-empty — may follow it.
        bool sawOnEmpty = false;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i] is XsltOnEmpty)
            {
                sawOnEmpty = true;
            }
            else if (sawOnEmpty)
            {
                throw new XsltException("XTSE0010: No instructions may follow xsl:on-empty in a sequence constructor", instructions[i].Location);
            }
        }

        return new XsltSequenceConstructor { Instructions = instructions };
    }

    private XsltInstruction ParseInstruction(XElement element)
    {
        // Evaluate use-when BEFORE any parsing (spec §3.8 conditional inclusion)
        if (!ShouldIncludeElement(element))
            return new XsltNoOp { Location = GetSourceLocation(element) };

        var location = GetSourceLocation(element);
        var prevContext = _nsContext;
        _nsContext = element;

        // Scope default-mode: check for default-mode attribute on XSLT elements
        // or xsl:default-mode on literal result elements.
        // Per XSLT spec 3.5.1, default-mode applies "within or on" the element.
        var prevDefaultMode = _currentDefaultMode;
        if (element.Name.Namespace == XsltNs)
        {
            var dmAttr = element.Attribute("default-mode");
            if (dmAttr != null)
                _currentDefaultMode = dmAttr.Value == "#unnamed" ? null : ParseQName(dmAttr.Value, element);
        }
        else
        {
            var dmAttr = element.Attribute(XsltNs + "default-mode");
            if (dmAttr != null)
                _currentDefaultMode = dmAttr.Value == "#unnamed" ? null : ParseQName(dmAttr.Value, element);
        }

        try
        {
            // Check if it's an XSLT instruction
            if (element.Name.Namespace == XsltNs)
            {
                var instruction = element.Name.LocalName switch
                {
                    "apply-templates" => ParseApplyTemplates(element, location),
                    "call-template" => ParseCallTemplate(element, location),
                    "apply-imports" => ParseApplyImports(element, location),
                    "next-match" => ParseNextMatch(element, location),
                    "for-each" => ParseForEach(element, location),
                    "for-each-group" => ParseForEachGroup(element, location),
                    "iterate" => ParseIterate(element, location),
                    "if" => ParseIf(element, location),
                    "choose" => ParseChoose(element, location),
                    "switch" => ParseSwitch(element, location),
                    "record" => ParseRecord(element, location),
                    "for-each-member" => ParseForEachMember(element, location),
                    "try" => ParseTry(element, location),
                    "element" => ParseElement(element, location),
                    "attribute" => ParseAttribute(element, location),
                    "text" => ParseText(element, location),
                    "value-of" => ParseValueOf(element, location),
                    "copy" => ParseCopy(element, location),
                    "copy-of" => ParseCopyOf(element, location),
                    "sequence" => ParseSequenceInstr(element, location),
                    "comment" => ParseComment(element, location),
                    "processing-instruction" => ParsePI(element, location),
                    "namespace" => ParseNamespaceInstr(element, location),
                    "document" => ParseDocument(element, location),
                    "result-document" => ParseResultDocument(element, location),
                    "message" => ParseMessage(element, location),
                    "assert" => ParseAssert(element, location),
                    "variable" => ParseVariableInstr(element, location),
                    "param" => throw new XsltException("XTSE0010: xsl:param is not allowed here; it can only appear at the start of xsl:template, xsl:function, or xsl:iterate", location),
                    "number" => ParseNumber(element, location),
                    "perform-sort" => ParsePerformSort(element, location),
                    "analyze-string" => ParseAnalyzeString(element, location),
                    "break" => ParseBreak(element, location),
                    "next-iteration" => ParseNextIteration(element, location),
                    "merge" => ParseMerge(element, location),
                    "fork" => ParseFork(element, location),
                    "map" => ParseMap(element, location),
                    "map-entry" => ParseMapEntry(element, location),
                    "array" => ParseArray(element, location),
                    "array-member" => ParseArrayMember(element, location),
                    "fallback" => ParseFallbackAsNoOp(element, location),
                    "where-populated" => ParseWherePopulated(element, location),
                    "on-empty" => ParseOnEmpty(element, location),
                    "on-non-empty" => ParseOnNonEmpty(element, location),
                    "evaluate" => ParseEvaluate(element, location),
                    "source-document" => ParseSourceDocument(element, location),
                    "stream" => ParseStream(element, location),
                    _ => ParseUnknownInstruction(element, location)
                };
                // Propagate standard attributes from XSLT elements (per XSLT 3.0 §3.5)
                instruction.Version = element.Attribute("version")?.Value;
                instruction.DefaultCollation = ResolveDefaultCollation(element.Attribute("default-collation")?.Value);
                // xml:base on XSLT instructions overrides static-base-uri() for expressions in scope
                if (element.Attribute(XNamespace.Xml + "base") != null)
                    instruction.StaticBaseUri = ResolveEffectiveBaseUri(element)?.ToString();
                return instruction;
            }

            // Check if this is an extension element (non-XSLT element in a namespace declared
            // via extension-element-prefixes). If so, use xsl:fallback children.
            if (IsExtensionElement(element))
            {
                // Recognize EXSLT exsl:document — treat as xsl:result-document
                if (element.Name.NamespaceName == "http://exslt.org/common"
                    && element.Name.LocalName == "document")
                {
                    return ParseExslDocument(element, location);
                }

                var fallbacks = element.Elements(XsltNs + "fallback").ToList();
                if (fallbacks.Count > 0)
                {
                    var instructions2 = new List<XsltInstruction>();
                    foreach (var fb in fallbacks)
                        instructions2.Add(ParseSequenceConstructor(fb));
                    return instructions2.Count == 1
                        ? instructions2[0]
                        : new XsltSequenceConstructor { Instructions = instructions2 };
                }
                // No fallback — generate a dynamic error that fires only if executed
                return new XsltDynamicError
                {
                    ErrorCode = "XTDE1450",
                    Message = $"Extension instruction '{element.Name}' is not supported and has no xsl:fallback",
                    Location = location
                };
            }

            // It's a literal result element
            return ParseLiteralResultElement(element, location);
        }
        finally
        {
            _nsContext = prevContext;
            _currentDefaultMode = prevDefaultMode;
        }
    }

    /// <summary>
    /// Checks if an element is in an extension namespace (declared via extension-element-prefixes
    /// on the stylesheet or on ancestor LRE/XSLT elements).
    /// </summary>
    private bool IsExtensionElement(XElement element)
    {
        var nsUri = element.Name.NamespaceName;
        if (string.IsNullOrEmpty(nsUri)) return false;

        // Check global stylesheet-level extension namespace URIs
        if (_extensionNamespaces.Contains(nsUri)) return true;

        // Walk element and ancestors looking for scoped extension-element-prefixes declarations
        for (var ancestor = element; ancestor != null; ancestor = ancestor.Parent)
        {
            // Check xsl:extension-element-prefixes on LRE ancestors
            var extAttr = ancestor.Attribute(XsltNs + "extension-element-prefixes")
                       ?? ancestor.Attribute("extension-element-prefixes");
            if (extAttr != null)
            {
                foreach (var prefix in extAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (prefix == "#default")
                    {
                        var defaultNs = ancestor.GetDefaultNamespace().NamespaceName;
                        if (defaultNs == nsUri) return true;
                    }
                    else
                    {
                        var prefixNs = ancestor.GetNamespaceOfPrefix(prefix);
                        if (prefixNs?.NamespaceName == nsUri) return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Parses xsl:fallback as a no-op, but still evaluates use-when attributes on descendant
    /// elements per XSLT spec — use-when errors must still be raised even in ignored content.
    /// </summary>
    private XsltNoOp ParseFallbackAsNoOp(XElement element, SourceLocation? location)
    {
        // Walk descendant elements and evaluate use-when attributes to surface errors
        foreach (var desc in element.Descendants())
        {
            ShouldIncludeElement(desc);
        }
        return new XsltNoOp { Location = location };
    }

    private XsltInstruction ParseUnknownInstruction(XElement element, SourceLocation? location)
    {
        // Forwards compatibility: if the effective version for this element is > 3.0,
        // extract and parse xsl:fallback children instead of throwing.
        var versionAttr = element.Attribute("version")?.Value;
        var effectiveVersion = versionAttr ?? GetEffectiveVersion(element);
        if (ParseVersionNumber(effectiveVersion) > 3.0m)
        {
            var fallbacks = element.Elements(XsltNs + "fallback").ToList();
            if (fallbacks.Count == 0)
                throw new XsltException($"XTSE0010: Unknown XSLT instruction '{element.Name.LocalName}' with no xsl:fallback", location);

            var instructions = new List<XsltInstruction>();
            foreach (var fb in fallbacks)
                instructions.Add(ParseSequenceConstructor(fb));
            return instructions.Count == 1
                ? instructions[0]
                : new XsltSequenceConstructor { Instructions = instructions };
        }

        throw new XsltException($"Unknown XSLT instruction: {element.Name.LocalName}", location);
    }

    private XsltApplyTemplates ParseApplyTemplates(XElement element, SourceLocation? location)
    {
        // XTSE0090: Validate no unknown attributes
        ValidateAllowedAttributes(element, location, "select", "mode");

        var selectAttr = element.Attribute("select");
        var modeAttr = element.Attribute("mode");

        // XTSE0020: Validate mode is a valid QName (no AVTs)
        var modeValue = modeAttr?.Value.Trim();
        if (modeValue != null && modeValue is not ("#default" or "#current" or "#unnamed"))
            ValidateQNameValue(modeValue, "mode", location);

        var sorts = new List<XsltSort>();
        var withParams = new List<XsltWithParam>();

        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child)) continue;
            if (child.Name == XsltNs + "sort")
                sorts.Add(ParseSort(child));
            else if (child.Name == XsltNs + "with-param")
            {
                var wp = ParseWithParam(child);
                if (withParams.Any(p => p.Name.Equals(wp.Name)))
                    throw new XsltException($"XTSE0670: Duplicate xsl:with-param '{wp.Name.LocalName}' in xsl:apply-templates",
                        GetSourceLocation(child));
                withParams.Add(wp);
            }
            else
                throw new XsltException($"XTSE0010: Only xsl:sort and xsl:with-param are allowed as children of xsl:apply-templates, found {child.Name.LocalName}",
                    GetSourceLocation(child));
        }

        QName? mode = _currentDefaultMode; // Start with the effective default mode
        bool useCurrentMode = false;
        if (modeValue != null)
        {
            if (modeValue == "#default")
                mode = _currentDefaultMode; // Explicitly use the effective default mode
            else if (modeValue == "#current")
                useCurrentMode = true;
            else if (modeValue == "#unnamed")
                mode = null; // Explicit unnamed mode
            else
                mode = ParseQName(modeValue, element);
        }

        // Track mode references for XTSE3085 (declared-modes) validation
        if (_usedModeReferences != null && !useCurrentMode)
        {
            var refMode = mode ?? TemplateIndex.DefaultModeSentinel;
            _usedModeReferences.Add((refMode, location ?? GetSourceLocation(element)));
        }

        return new XsltApplyTemplates
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Mode = mode,
            UseCurrentMode = useCurrentMode,
            Sorts = sorts,
            WithParams = withParams
        };
    }

    private XsltCallTemplate ParseCallTemplate(XElement element, SourceLocation? location)
    {
        // XTSE0090: Validate no unknown attributes
        ValidateAllowedAttributes(element, location, "name");

        var name = ParseQName(element.Attribute("name")!.Value, element);

        var withParams = new List<XsltWithParam>();
        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child)) continue;
            if (child.Name == XsltNs + "with-param")
            {
                var wp = ParseWithParam(child);
                if (withParams.Any(p => p.Name.Equals(wp.Name)))
                    throw new XsltException($"XTSE0670: Duplicate xsl:with-param '{wp.Name.LocalName}' in xsl:call-template",
                        GetSourceLocation(child));
                withParams.Add(wp);
            }
            else
                throw new XsltException($"XTSE0010: Only xsl:with-param is allowed as a child of xsl:call-template, found {child.Name.LocalName}",
                    GetSourceLocation(child));
        }

        return new XsltCallTemplate
        {
            Location = location,
            Name = name,
            WithParams = withParams
        };
    }

    private XsltApplyImports ParseApplyImports(XElement element, SourceLocation? location)
    {
        // XTSE0090: Validate no unknown attributes (apply-imports has no element-specific attributes)
        ValidateAllowedAttributes(element, location);

        var withParams = new List<XsltWithParam>();
        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child)) continue;
            if (child.Name == XsltNs + "with-param")
            {
                var wp = ParseWithParam(child);
                if (withParams.Any(p => p.Name.Equals(wp.Name)))
                    throw new XsltException($"XTSE0670: Duplicate xsl:with-param '{wp.Name.LocalName}' in xsl:apply-imports",
                        GetSourceLocation(child));
                withParams.Add(wp);
            }
            else
                throw new XsltException($"XTSE0010: Only xsl:with-param is allowed as a child of xsl:apply-imports, found {child.Name.LocalName}",
                    GetSourceLocation(child));
        }

        return new XsltApplyImports
        {
            Location = location,
            WithParams = withParams
        };
    }

    private XsltNextMatch ParseNextMatch(XElement element, SourceLocation? location)
    {
        var withParams = new List<XsltWithParam>();
        XsltSequenceConstructor? fallback = null;

        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child)) continue;
            if (child.Name == XsltNs + "with-param")
                withParams.Add(ParseWithParam(child));
            else if (child.Name == XsltNs + "fallback")
                fallback = ParseSequenceConstructor(child);
        }

        return new XsltNextMatch
        {
            Location = location,
            WithParams = withParams,
            Fallback = fallback
        };
    }

    private XsltForEach ParseForEach(XElement element, SourceLocation? location)
    {
        var select = ParseExpr(element.Attribute("select")!.Value, element.Attribute("select"));

        var sorts = new List<XsltSort>();
        var bodyInstructions = new List<XsltInstruction>();
        var expandText = IsExpandTextActive(element);
        var preserveSpace = IsXmlSpacePreserve(element);
        var pastSorts = false;

        // Pre-scan to find where sort elements end, so we can correctly identify
        // body content for xml:space="preserve" whitespace handling
        var nodes = element.Nodes().ToList();
        int lastSortIndex = -1;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is XElement child && child.Name == XsltNs + "sort" && ShouldIncludeElement(child))
                lastSortIndex = i;
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            switch (node)
            {
                case XElement child when child.Name == XsltNs + "sort":
                    if (!ShouldIncludeElement(child)) break;
                    if (pastSorts)
                        throw new XsltException("XTSE0010: xsl:sort elements must come before other content in xsl:for-each", location);
                    sorts.Add(ParseSort(child));
                    break;
                case XElement child:
                    pastSorts = true;
                    bodyInstructions.Add(ParseInstruction(child));
                    break;
                case XText text:
                    bool inBody = i > lastSortIndex;
                    if (!IsXmlWhitespaceOnly(text.Value))
                    {
                        pastSorts = true;
                        bodyInstructions.Add(CreateTextInstruction(text.Value, expandText, element));
                    }
                    else if (inBody && preserveSpace)
                    {
                        // xml:space="preserve": whitespace-only text nodes in the body
                        // (after all sort elements) are preserved per XSLT §4.3
                        bodyInstructions.Add(CreateTextInstruction(text.Value, expandText, element));
                    }
                    break;
            }
        }

        return new XsltForEach
        {
            Location = location,
            Select = select,
            Sorts = sorts,
            Body = new XsltSequenceConstructor { Instructions = bodyInstructions }
        };
    }

    private XsltForEachGroup ParseForEachGroup(XElement element, SourceLocation? location)
    {
        var select = ParseExpr(element.Attribute("select")!.Value, element.Attribute("select"));
        var groupByAttr = element.Attribute("group-by");
        var groupAdjacentAttr = element.Attribute("group-adjacent");
        var groupStartingWithAttr = element.Attribute("group-starting-with");
        var groupEndingWithAttr = element.Attribute("group-ending-with");
        var collationAttr = element.Attribute("collation");
        var compositeAttr = element.Attribute("composite");

        var sorts = new List<XsltSort>();
        var bodyInstructions = new List<XsltInstruction>();
        var expandText = IsExpandTextActive(element);
        var preserveSpace = IsXmlSpacePreserve(element);

        var nodes = element.Nodes().ToList();
        int lastSortIndex = -1;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is XElement child && child.Name == XsltNs + "sort" && ShouldIncludeElement(child))
                lastSortIndex = i;
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            switch (node)
            {
                case XElement child when child.Name == XsltNs + "sort":
                    if (ShouldIncludeElement(child))
                        sorts.Add(ParseSort(child));
                    break;
                case XElement child:
                    bodyInstructions.Add(ParseInstruction(child));
                    break;
                case XText text:
                    bool inBody = i > lastSortIndex;
                    if (!IsXmlWhitespaceOnly(text.Value))
                        bodyInstructions.Add(CreateTextInstruction(text.Value, expandText, element));
                    else if (inBody && preserveSpace)
                        bodyInstructions.Add(CreateTextInstruction(text.Value, expandText, element));
                    break;
            }
        }

        // XTSE1080: Exactly one grouping attribute must be present
        var groupingAttrCount = (groupByAttr != null ? 1 : 0)
                              + (groupAdjacentAttr != null ? 1 : 0)
                              + (groupStartingWithAttr != null ? 1 : 0)
                              + (groupEndingWithAttr != null ? 1 : 0);
        if (groupingAttrCount == 0)
            throw new XsltException("XTSE1080: xsl:for-each-group must have one of the attributes group-by, group-adjacent, group-starting-with, or group-ending-with", location);
        if (groupingAttrCount > 1)
            throw new XsltException("XTSE1080: xsl:for-each-group must not have more than one of the attributes group-by, group-adjacent, group-starting-with, or group-ending-with", location);

        // XTSE1090: collation only allowed with group-by or group-adjacent
        if (collationAttr != null && groupByAttr == null && groupAdjacentAttr == null)
            throw new XsltException("XTSE1090: The collation attribute of xsl:for-each-group may only be specified when group-by or group-adjacent is specified", location);

        // XTSE0020: composite attribute must be a valid xsl:yes-or-no value
        if (compositeAttr != null && ParseYesNo(compositeAttr) == null)
            throw new XsltException($"XTSE0020: Invalid value '{compositeAttr.Value}' for composite attribute", location);

        // XTSE1017: stable attribute only on first xsl:sort
        for (int i = 1; i < sorts.Count; i++)
        {
            if (sorts[i].Stable != null)
                throw new XsltException("XTSE1017: The stable attribute is permitted only on the first xsl:sort element within xsl:for-each-group", location);
        }

        return new XsltForEachGroup
        {
            Location = location,
            Select = select,
            GroupBy = groupByAttr != null ? ParseExpr(groupByAttr.Value, groupByAttr) : null,
            GroupAdjacent = groupAdjacentAttr != null ? ParseExpr(groupAdjacentAttr.Value, groupAdjacentAttr) : null,
            GroupStartingWith = groupStartingWithAttr != null ? ParsePattern(groupStartingWithAttr.Value, element) : null,
            GroupEndingWith = groupEndingWithAttr != null ? ParsePattern(groupEndingWithAttr.Value, element) : null,
            Collation = collationAttr != null ? ParseAvt(collationAttr.Value, element, collationAttr) : null,
            Composite = ParseYesNo(compositeAttr) ?? false,
            Sorts = sorts,
            Body = new XsltSequenceConstructor { Instructions = bodyInstructions }
        };
    }

    private XsltIterate ParseIterate(XElement element, SourceLocation? location)
    {
        var select = ParseExpr(element.Attribute("select")!.Value, element.Attribute("select"));

        var parameters = new List<XsltParam>();
        XsltSequenceConstructor? onCompletion = null;
        var bodyInstructions = new List<XsltInstruction>();

        var expandText = IsExpandTextActive(element);

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child when child.Name == XsltNs + "param":
                    if (ShouldIncludeElement(child))
                        parameters.Add(ParseParam(child));
                    break;
                case XElement child when child.Name == XsltNs + "on-completion":
                {
                    var selectAttr = child.Attribute("select");
                    if (selectAttr != null && child.Nodes().Any())
                        throw new XsltException("XTSE3125: xsl:on-completion must not have both a select attribute and content", location);
                    if (selectAttr != null)
                    {
                        // on-completion with select: wrap as xsl:sequence instruction
                        var seqInstr = new XsltSequence
                        {
                            Location = GetSourceLocation(child),
                            Select = ParseExpr(selectAttr.Value, selectAttr)
                        };
                        onCompletion = new XsltSequenceConstructor { Instructions = new List<XsltInstruction> { seqInstr } };
                    }
                    else
                    {
                        onCompletion = ParseSequenceConstructor(child);
                    }
                    break;
                }
                case XElement child:
                    bodyInstructions.Add(ParseInstruction(child));
                    break;
                case XText text:
                    if (!IsXmlWhitespaceOnly(text.Value))
                        bodyInstructions.Add(CreateTextInstruction(text.Value, expandText, element));
                    break;
            }
        }

        // Validate: no duplicate param names (XTSE0580)
        var paramNames = new HashSet<QName>();
        foreach (var param in parameters)
        {
            if (!paramNames.Add(param.Name))
                throw new XsltException($"XTSE0580: Duplicate parameter name '{param.Name}' in xsl:iterate", location);
        }

        // XTSE3520: xsl:iterate parameters must have a default value (select or content)
        // Exception: if the type allows empty sequence (? or *), empty sequence is the implicit default
        foreach (var param in parameters)
        {
            if (param.Select == null && param.Content == null && !param.Required)
            {
                if (param.As != null && (param.As.Occurrence == PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrOne
                    || param.As.Occurrence == PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrMore))
                    continue; // Empty sequence is a valid default for optional types
                throw new XsltException($"XTSE3520: Parameter '{param.Name}' in xsl:iterate must have a select attribute or child content as a default value",
                    location);
            }
        }

        var result = new XsltIterate
        {
            Location = location,
            Select = select,
            Params = parameters,
            OnCompletion = onCompletion,
            Body = new XsltSequenceConstructor { Instructions = bodyInstructions }
        };

        // XTSE3130: validate that xsl:next-iteration with-params reference iterate params
        ValidateNextIterationParams(result.Body, paramNames, location);

        return result;
    }

    private static void ValidateNextIterationParams(XsltSequenceConstructor body, HashSet<QName> iterateParams, SourceLocation? location)
    {
        foreach (var instr in body.Instructions)
        {
            if (instr is XsltNextIteration ni)
            {
                foreach (var wp in ni.WithParams)
                {
                    if (!iterateParams.Contains(wp.Name))
                        throw new XsltException($"XTSE3130: xsl:next-iteration references parameter '{wp.Name}' which is not declared on the enclosing xsl:iterate", location);
                }
            }
            else if (instr is XsltIf ifInstr)
            {
                ValidateNextIterationParams(ifInstr.Then, iterateParams, location);
            }
            else if (instr is XsltChoose choose)
            {
                foreach (var when in choose.When)
                    ValidateNextIterationParams(when.Body, iterateParams, location);
                if (choose.Otherwise != null)
                    ValidateNextIterationParams(choose.Otherwise, iterateParams, location);
            }
        }
    }

    private XsltIf ParseIf(XElement element, SourceLocation? location)
    {
        var testAttr = element.Attribute("test")
            ?? throw new XsltException("XTSE0010: xsl:if requires a 'test' attribute", location);
        var test = ParseExpr(testAttr.Value, testAttr);

        return new XsltIf
        {
            Location = location,
            Test = test,
            Then = ParseSequenceConstructor(element)
        };
    }

    private XsltChoose ParseChoose(XElement element, SourceLocation? location)
    {
        // XTSE0010: xsl:choose must not contain text content
        foreach (var node in element.Nodes())
        {
            if (node is XText text && !IsXmlWhitespaceOnly(text.Value))
                throw new XsltException("XTSE0010: Text content is not allowed in xsl:choose", location);
        }

        var whens = new List<XsltWhen>();
        XsltSequenceConstructor? otherwise = null;

        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child)) continue;
            if (child.Name == XsltNs + "when")
            {
                if (otherwise != null)
                    throw new XsltException("XTSE0010: xsl:when must not appear after xsl:otherwise in xsl:choose", location);
                var testAttr = child.Attribute("test")
                    ?? throw new XsltException("XTSE0010: xsl:when requires a 'test' attribute", location);
                // Resolve namespace prefixes against the xsl:when itself so that locally-declared
                // xmlns:* on the when (e.g. DocBook xslTNG's `<xsl:when xmlns:ls="..." test="/ls:locale">`)
                // are visible to the test expression.
                var savedNsCtx = _nsContext;
                _nsContext = child;
                XQueryExpression test;
                try { test = ParseExpr(testAttr.Value, testAttr); }
                finally { _nsContext = savedNsCtx; }
                whens.Add(new XsltWhen
                {
                    Test = test,
                    Body = ParseSequenceConstructor(child)
                });
            }
            else if (child.Name == XsltNs + "otherwise")
            {
                if (otherwise != null)
                    throw new XsltException("XTSE0010: xsl:choose must not contain more than one xsl:otherwise", location);
                otherwise = ParseSequenceConstructor(child);
            }
            else
                throw new XsltException($"XTSE0010: Only xsl:when and xsl:otherwise are allowed as children of xsl:choose, found {child.Name.LocalName}",
                    GetSourceLocation(child));
        }

        // XTSE0010: xsl:choose must contain at least one xsl:when
        if (whens.Count == 0)
            throw new XsltException("XTSE0010: xsl:choose must contain at least one xsl:when element", location);

        return new XsltChoose
        {
            Location = location,
            When = whens,
            Otherwise = otherwise
        };
    }

    /// <summary>
    /// Parses xsl:item-type (XSLT 4.0) — declares a named type alias.
    /// </summary>
    private void ParseItemType(XElement element, XsltStylesheet stylesheet)
    {
        var nameAttr = element.Attribute("name")?.Value
            ?? throw new XsltException("XTSE0010: xsl:item-type requires a 'name' attribute",
                GetSourceLocation(element));
        var asAttr = element.Attribute("as")?.Value
            ?? throw new XsltException("XTSE0010: xsl:item-type requires an 'as' attribute",
                GetSourceLocation(element));

        _nsContext = element;
        var name = ParseQName(nameAttr, element);
        var typeSpec = ParseSequenceType(asAttr, element);
        _nsContext = null;

        stylesheet.NamedTypes[name] = typeSpec;
    }

    /// <summary>
    /// Parses xsl:record (XSLT 4.0) — constructs a record (map with string keys).
    /// </summary>
    private Ast.XsltRecord ParseRecord(XElement element, SourceLocation? location)
    {
        var entries = new List<(string Name, Ast.XsltSequenceConstructor Value)>();

        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child)) continue;
            if (child.Name == XsltNs + "entry")
            {
                var nameAttr = child.Attribute("key")?.Value ?? child.Attribute("name")?.Value;
                if (nameAttr != null)
                    entries.Add((nameAttr, ParseSequenceConstructor(child)));
            }
        }

        return new Ast.XsltRecord
        {
            Location = location,
            Entries = entries
        };
    }

    /// <summary>
    /// Parses xsl:switch (XSLT 4.0) — like xsl:choose but with a select expression.
    /// </summary>
    private XsltSwitch ParseSwitch(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select")
            ?? throw new XsltException("XTSE0010: xsl:switch requires a 'select' attribute", location);

        var whens = new List<Ast.XsltWhen>();
        Ast.XsltSequenceConstructor? otherwise = null;

        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child)) continue;
            if (child.Name == XsltNs + "when")
            {
                if (otherwise != null)
                    throw new XsltException("XTSE0010: xsl:when must not appear after xsl:otherwise in xsl:switch", location);
                var testAttr = child.Attribute("test")
                    ?? throw new XsltException("XTSE0010: xsl:when requires a 'test' attribute", location);
                whens.Add(new Ast.XsltWhen
                {
                    Test = ParseExpr(testAttr.Value, testAttr),
                    Body = ParseSequenceConstructor(child)
                });
            }
            else if (child.Name == XsltNs + "otherwise")
            {
                if (otherwise != null)
                    throw new XsltException("XTSE0010: xsl:switch must not contain more than one xsl:otherwise", location);
                otherwise = ParseSequenceConstructor(child);
            }
            else
                throw new XsltException($"XTSE0010: Only xsl:when and xsl:otherwise are allowed as children of xsl:switch",
                    GetSourceLocation(child));
        }

        if (whens.Count == 0)
            throw new XsltException("XTSE0010: xsl:switch must contain at least one xsl:when element", location);

        return new Ast.XsltSwitch
        {
            Location = location,
            Select = ParseExpr(selectAttr.Value, selectAttr),
            When = whens,
            Otherwise = otherwise
        };
    }

    /// <summary>
    /// Parses xsl:for-each-member (XSLT 4.0) — iterates over array members.
    /// </summary>
    private Ast.XsltForEachMember ParseForEachMember(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select")
            ?? throw new XsltException("XTSE0010: xsl:for-each-member requires a 'select' attribute", location);

        return new Ast.XsltForEachMember
        {
            Location = location,
            Select = ParseExpr(selectAttr.Value, selectAttr),
            Body = ParseSequenceConstructor(element)
        };
    }

    private XsltTry ParseTry(XElement element, SourceLocation? location)
    {
        var rollbackAttr = element.Attribute("rollback-output");
        var selectAttr = element.Attribute("select");

        var catches = new List<XsltCatch>();
        var catchElements = new List<XElement>();
        XsltSequenceConstructor? bodyContent = null;
        var expandText = IsExpandTextActive(element);

        // XTSE3140: xsl:try with select must not have child content other than xsl:catch and xsl:fallback
        if (selectAttr != null)
        {
            foreach (var node in element.Nodes())
            {
                if (node is XElement child && child.Name != XsltNs + "catch" && child.Name != XsltNs + "fallback")
                    throw new XsltException("XTSE3140: xsl:try with a select attribute must not have content other than xsl:catch and xsl:fallback",
                        GetSourceLocation(child));
                if (node is XText text && !IsXmlWhitespaceOnly(text.Value))
                    throw new XsltException("XTSE3140: xsl:try with a select attribute must not have text content", location);
            }
        }

        // Collect variable names declared in the try body (for scope validation)
        var tryBodyVarNames = new HashSet<string>();

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child when child.Name == XsltNs + "catch":
                {
                    var errorsAttr = child.Attribute("errors");
                    var errors = new List<QName>();
                    if (errorsAttr != null)
                    {
                        foreach (var e in errorsAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            var trimmed = e.Trim();
                            if (trimmed == "*")
                            {
                                // Wildcard: matches any error code
                                errors.Add(new QName(NamespaceId.None, "*"));
                            }
                            else if (trimmed.StartsWith("*:", StringComparison.Ordinal))
                            {
                                // *:localname — matches localname in any namespace
                                errors.Add(new QName(NamespaceId.None, trimmed[2..], "*"));
                            }
                            else
                            {
                                errors.Add(ParseQName(trimmed, child));
                            }
                        }
                    }

                    // xsl:catch can have either a select attribute or a body
                    var catchSelectAttr = child.Attribute("select");

                    // XTSE3150: xsl:catch with select must not have content
                    ValidateSelectContentExclusive(catchSelectAttr, child, "XTSE3150", "xsl:catch", GetSourceLocation(child));

                    catches.Add(new XsltCatch
                    {
                        Errors = errors,
                        SelectExpression = catchSelectAttr != null ? ParseExpr(catchSelectAttr.Value, catchSelectAttr) : null,
                        Body = catchSelectAttr == null ? ParseSequenceConstructor(child) : null
                    });
                    catchElements.Add(child);
                    break;
                }
                case XElement child:
                    bodyContent ??= new XsltSequenceConstructor { Instructions = new List<XsltInstruction>() };
                    ((List<XsltInstruction>)bodyContent.Instructions).Add(ParseInstruction(child));
                    // Track variable declarations in try body
                    if (child.Name == XsltNs + "variable")
                    {
                        var nameAttr = child.Attribute("name");
                        if (nameAttr != null)
                            tryBodyVarNames.Add(nameAttr.Value);
                    }
                    break;
                case XText text:
                    if (!IsXmlWhitespaceOnly(text.Value))
                    {
                        bodyContent ??= new XsltSequenceConstructor { Instructions = new List<XsltInstruction>() };
                        ((List<XsltInstruction>)bodyContent.Instructions).Add(CreateTextInstruction(text.Value, expandText, element));
                    }
                    break;
            }
        }

        // XPST0008: Variables declared in xsl:try body are not visible in xsl:catch,
        // but only flag this if the variable doesn't also exist in an outer scope
        if (tryBodyVarNames.Count > 0)
        {
            // Remove names that are also visible from outer scope
            foreach (var varName in tryBodyVarNames.ToList())
            {
                if (IsVariableDeclaredInOuterScope(element, varName))
                    tryBodyVarNames.Remove(varName);
            }

            foreach (var catchElem in catchElements)
            {
                var catchXml = catchElem.ToString();
                foreach (var varName in tryBodyVarNames)
                {
                    if (catchXml.Contains($"${varName}", StringComparison.Ordinal))
                        throw new XsltException($"XPST0008: Variable ${varName} declared in xsl:try is not visible in xsl:catch", location);
                }
            }
        }

        return new XsltTry
        {
            Location = location,
            SelectExpression = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Body = selectAttr == null ? (bodyContent ?? new XsltSequenceConstructor { Instructions = [] }) : null,
            Catches = catches,
            Rollback = rollbackAttr?.Value != "no"
        };
    }

    /// <summary>
    /// Validates that a QName attribute value is syntactically valid (XTSE0020).
    /// Rejects AVT syntax ({...}) and invalid QName characters.
    /// </summary>
    private static void ValidateQNameValue(string value, string attrName, SourceLocation? location)
    {
        value = value.Trim();
        // EQName syntax Q{uri}local is always valid — skip all checks
        if (value.StartsWith("Q{", StringComparison.Ordinal))
            return;
        // AVT syntax {..} is not permitted in QName attributes
        if (value.Contains('{', StringComparison.Ordinal) || value.Contains('}', StringComparison.Ordinal))
            throw new XsltException($"XTSE0020: Attribute value templates are not permitted in the '{attrName}' attribute", location);
        if (value.Length > 0 && char.IsAsciiDigit(value[0]))
            throw new XsltException($"XTSE0020: Invalid QName '{value}' for '{attrName}' attribute: names must not start with a digit", location);
        if (value.Contains('/', StringComparison.Ordinal))
            throw new XsltException($"XTSE0020: Invalid QName '{value}' for '{attrName}' attribute", location);
        if (value.Contains("::", StringComparison.Ordinal))
            throw new XsltException($"XTSE0020: Invalid QName '{value}' for '{attrName}' attribute", location);
        // Check for common invalid NCName characters
        foreach (var ch in value)
        {
            if (ch == '!' || ch == '#' || ch == '@' || ch == '$' || ch == '%' ||
                ch == '(' || ch == ')' || ch == '[' || ch == ']' || ch == ',' ||
                ch == '=' || ch == '+' || ch == '<' || ch == '>' || ch == '?')
            {
                throw new XsltException($"XTSE0020: Invalid QName '{value}' for '{attrName}' attribute: character '{ch}' is not allowed", location);
            }
        }
    }

    /// <summary>
    /// Validates that an XSLT element required to be empty has no content other than comments/PIs (XTSE0260).
    /// </summary>
    private static void ValidateEmptyElement(XElement element)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XComment || node is XProcessingInstruction)
                continue;
            if (node is XText text && IsXmlWhitespaceOnly(text.Value) && element.Attribute(XNamespace.Xml + "space")?.Value != "preserve")
                continue;
            throw new XsltException($"XTSE0260: The xsl:{element.Name.LocalName} element must be empty",
                GetSourceLocation(element));
        }
    }

    /// <summary>
    /// Validates that an XSLT instruction element has no unknown attributes (XTSE0090).
    /// Namespace declaration attributes (xmlns:*) are always allowed.
    /// </summary>
    private static void ValidateAllowedAttributes(XElement element, SourceLocation? location, params string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed);
        // In forwards-compatible mode (version > 3.0), unknown attributes are silently ignored
        var elementVersion = element.Attribute("version")?.Value ?? GetEffectiveVersion(element);
        var forwardsCompatible = ParseVersionNumber(elementVersion) > 3.0m;
        foreach (var attr in element.Attributes())
        {
            if (attr.IsNamespaceDeclaration) continue;
            // Attributes in the XSLT namespace are not permitted on XSLT elements
            if (attr.Name.Namespace == XsltNs)
                throw new XsltException($"XTSE0090: Attribute 'xsl:{attr.Name.LocalName}' in the XSLT namespace is not permitted on an XSLT element", location);
            if (attr.Name.Namespace != XNamespace.None) continue; // Extension attributes in other namespaces OK
            // Standard attributes allowed on all XSLT elements (XSLT 3.0, section 3.7)
            if (attr.Name.LocalName is "use-when" or "default-collation" or "default-mode"
                or "default-validation" or "exclude-result-prefixes" or "expand-text"
                or "extension-element-prefixes" or "version" or "xpath-default-namespace") continue;
            // Shadow attributes (starting with '_') are compile-time AVTs, always allowed
            if (attr.Name.LocalName.StartsWith('_')) continue;
            if (!allowedSet.Contains(attr.Name.LocalName))
            {
                if (forwardsCompatible) continue; // FC mode: silently ignore unknown attributes
                throw new XsltException($"XTSE0090: Attribute '{attr.Name.LocalName}' is not permitted on xsl:{element.Name.LocalName}", location);
            }
        }
    }

    /// <summary>
    /// Checks if two xsl:mode elements have conflicting values for a given attribute.
    /// </summary>
    private static void CheckModeAttrConflict(XElement prev, XElement current, string attrName, string modeName)
    {
        var prevAttr = prev.Attribute(attrName);
        var currAttr = current.Attribute(attrName);
        if (prevAttr != null && currAttr != null && prevAttr.Value != currAttr.Value)
            throw new XsltException($"XTSE0545: Conflicting xsl:mode declarations for mode '{modeName}': attribute '{attrName}' has values '{prevAttr.Value}' and '{currAttr.Value}'",
                GetSourceLocation(current));
    }

    /// <summary>
    /// Checks if two mode declarations have conflicting use-accumulators.
    /// Comparison is set-based on resolved QNames (order and prefix don't matter).
    /// </summary>
    private static bool UseAccumulatorsConflict(XsltMode a, XsltMode b)
    {
        if (a.UseAccumulatorNames.Count == 0 || b.UseAccumulatorNames.Count == 0)
            return false; // one or both don't specify use-accumulators
        var setA = new HashSet<QName>(a.UseAccumulatorNames);
        var setB = new HashSet<QName>(b.UseAccumulatorNames);
        return !setA.SetEquals(setB);
    }

    private static bool VisibilityConflict(XsltMode a, XsltMode b)
    {
        if (a.VisibilityAttr == null || b.VisibilityAttr == null)
            return false; // one or both don't explicitly specify visibility
        return a.VisibilityAttr != b.VisibilityAttr;
    }

    /// <summary>
    /// Validates that select attribute and non-empty content are not both present.
    /// </summary>
    private static void ValidateSelectContentExclusive(XAttribute? selectAttr, XElement element, string errorCode, string instrName, SourceLocation? location)
    {
        if (selectAttr != null && element.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException($"{errorCode}: {instrName} must not have both a select attribute and non-empty content", location);
    }

    /// <summary>
    /// Validates that a string value is a valid xs:decimal (no scientific notation).
    /// </summary>
    private static void ValidateDecimalValue(string value, string errorCode, string attrName, SourceLocation? location)
    {
        var trimmed = value.Trim();
        if (trimmed.Contains('e', StringComparison.OrdinalIgnoreCase))
            throw new XsltException($"{errorCode}: Invalid {attrName} value '{value}': must be a valid xs:decimal (scientific notation is not allowed)", location);
        if (!double.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
            throw new XsltException($"{errorCode}: Invalid {attrName} value '{value}': must be a valid xs:decimal", location);
    }

    /// <summary>
    /// Parses a priority value, validating it is valid xs:decimal (no scientific notation).
    /// </summary>
    private static double ParsePriorityValue(string value, SourceLocation? location)
    {
        ValidateDecimalValue(value, "XTSE0530", "priority", location);
        return double.Parse(value.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

    private XsltElement ParseElement(XElement element, SourceLocation? location)
    {
        ValidateAllowedAttributes(element, location,
            "name", "namespace", "use-attribute-sets", "inherit-namespaces", "validation", "type");

        var nameAttr = element.Attribute("name");
        if (nameAttr == null)
            throw new XsltException("XTSE0010: xsl:element must have a name attribute", location);
        var name = ParseAvt(nameAttr.Value, element, nameAttr);
        var namespaceAttr = element.Attribute("namespace");
        var useAttributeSetsAttr = element.Attribute("use-attribute-sets");
        var inheritNamespacesAttr = element.Attribute("inherit-namespaces");
        var validationAttr = element.Attribute("validation");
        var typeAttr = element.Attribute("type");

        // XTSE1660: Non-schema-aware processor must reject type attribute
        if (typeAttr != null && ShouldRejectSchemaAware)
            throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the type attribute on xsl:element", location);
        // XTSE1660: Non-schema-aware processor must reject validation="strict" or "type"
        if (validationAttr != null)
        {
            var v = validationAttr.Value.Trim();
            if (v is "strict" or "type" && ShouldRejectSchemaAware)
                throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept validation=\"{v}\" on xsl:element", location);
        }

        // XTSE0020: Validate inherit-namespaces value
        if (inheritNamespacesAttr != null && ParseYesNo(inheritNamespacesAttr) == null)
            throw new XsltException($"XTSE0020: Invalid value '{inheritNamespacesAttr.Value}' for inherit-namespaces attribute", location);

        var useAttributeSets = new List<QName>();
        if (useAttributeSetsAttr != null)
        {
            foreach (var n in useAttributeSetsAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                useAttributeSets.Add(ParseQName(n, element));
            }
        }

        // Capture in-scope namespace bindings for prefix resolution at runtime
        var inScopeNamespaces = new Dictionary<string, string>();
        foreach (var nsAttr in element.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            var prefix = nsAttr.Name.LocalName == "xmlns" ? "" : nsAttr.Name.LocalName;
            inScopeNamespaces[prefix] = nsAttr.Value;
        }
        // Also include inherited namespaces from ancestor elements
        for (var ancestor = element.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            foreach (var nsAttr in ancestor.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                var prefix = nsAttr.Name.LocalName == "xmlns" ? "" : nsAttr.Name.LocalName;
                inScopeNamespaces.TryAdd(prefix, nsAttr.Value);
            }
        }

        return new XsltElement
        {
            Location = location,
            Name = name,
            Namespace = namespaceAttr != null ? ParseAvt(namespaceAttr.Value, element, namespaceAttr) : null,
            UseAttributeSets = useAttributeSets,
            InheritNamespaces = ParseYesNo(inheritNamespacesAttr),
            Validation = ParseValidationMode(validationAttr),
            // Type: rejected by XTSE1660 for non-schema-aware processors
            Content = ParseSequenceConstructor(element),
            InScopeNamespaces = inScopeNamespaces,
            BaseUri = ResolveEffectiveBaseUri(element)
        };
    }

    private XsltAttribute ParseAttribute(XElement element, SourceLocation? location)
    {
        var nameAttr = element.Attribute("name");
        if (nameAttr == null)
            throw new XsltException("XTSE0010: xsl:attribute must have a name attribute", location);
        var name = ParseAvt(nameAttr.Value, element, nameAttr);
        var namespaceAttr = element.Attribute("namespace");
        var selectAttr = element.Attribute("select");
        var separatorAttr = element.Attribute("separator");
        var validationAttr = element.Attribute("validation");
        var typeAttr = element.Attribute("type");

        // XTSE1660: Non-schema-aware processor must reject type attribute
        if (typeAttr != null && ShouldRejectSchemaAware)
            throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the type attribute on xsl:attribute", location);
        if (validationAttr != null)
        {
            var v = validationAttr.Value.Trim();
            if (v is "strict" or "type" && ShouldRejectSchemaAware)
                throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept validation=\"{v}\" on xsl:attribute", location);
        }

        // XTSE0840: select and non-empty content are mutually exclusive
        ValidateSelectContentExclusive(selectAttr, element, "XTSE0840", "xsl:attribute", location);

        // Collect in-scope namespaces for prefix resolution (XTDE0860)
        var inScopeNamespaces = new Dictionary<string, string>();
        foreach (var nsAttr in element.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            var prefix = nsAttr.Name.LocalName == "xmlns" ? "" : nsAttr.Name.LocalName;
            inScopeNamespaces[prefix] = nsAttr.Value;
        }
        for (var ancestor = element.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            foreach (var nsAttr in ancestor.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                var prefix = nsAttr.Name.LocalName == "xmlns" ? "" : nsAttr.Name.LocalName;
                inScopeNamespaces.TryAdd(prefix, nsAttr.Value);
            }
        }

        return new XsltAttribute
        {
            Location = location,
            Name = name,
            Namespace = namespaceAttr != null ? ParseAvt(namespaceAttr.Value, element, namespaceAttr) : null,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.Nodes().Any()
                ? ParseSequenceConstructor(element)
                : null,
            Separator = separatorAttr != null ? ParseAvt(separatorAttr.Value, element, separatorAttr) : null,
            Validation = ParseValidationMode(validationAttr),
            // Type: rejected by XTSE1660 for non-schema-aware processors
            InScopeNamespaces = inScopeNamespaces
        };
    }

    private XsltInstruction ParseText(XElement element, SourceLocation? location)
    {
        // XTSE0010: xsl:text must not contain child elements
        if (element.Elements().Any())
            throw new XsltException("XTSE0010: xsl:text must not contain child elements", location);

        var doeTextAttr = element.Attribute("disable-output-escaping");
        ValidateDoeAttribute(doeTextAttr, location);
        var doe = doeTextAttr?.Value.Trim() is "yes" or "true" or "1";
        var expandText = IsExpandTextActive(element);
        var value = element.Value;

        // When expand-text is active and the text contains TVT expressions, parse as TVT
        if (expandText && value.Contains('{', StringComparison.Ordinal))
        {
            var avt = ParseAvtFromText(value, element);
            // If the AVT resolved to just a single literal, keep as plain text
            if (avt.Parts.Count == 1 && avt.Parts[0] is AvtLiteral)
            {
                return new XsltText { Location = location, Value = value, DisableOutputEscaping = doe };
            }
            return new XsltTextValueTemplate { Template = avt, Location = location };
        }

        return new XsltText
        {
            Location = location,
            Value = value,
            DisableOutputEscaping = doe
        };
    }

    private XsltValueOf ParseValueOf(XElement element, SourceLocation? location)
    {
        ValidateAllowedAttributes(element, location, "select", "separator", "disable-output-escaping");
        var selectAttr = element.Attribute("select");
        var separatorAttr = element.Attribute("separator");
        var doeAttr = element.Attribute("disable-output-escaping");
        ValidateDoeAttribute(doeAttr, location);

        // XTSE0870: select and non-empty content are mutually exclusive
        ValidateSelectContentExclusive(selectAttr, element, "XTSE0870", "xsl:value-of", location);

        var hasContent = selectAttr == null && element.Nodes().Any();

        return new XsltValueOf
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = hasContent ? ParseSequenceConstructor(element) : null,
            // null means "use default" — in ValueOfAsync, default is " " for select, "" for content,
            // and in 1.0 backwards-compatible mode, first-value semantics are used instead.
            Separator = separatorAttr != null ? ParseAvt(separatorAttr.Value, element, separatorAttr) : null,
            DisableOutputEscaping = doeAttr?.Value.Trim() is "yes" or "true" or "1"
        };
    }

    private XsltCopy ParseCopy(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select");
        var copyNamespacesAttr = element.Attribute("copy-namespaces");
        var inheritNamespacesAttr = element.Attribute("inherit-namespaces");
        var useAttributeSetsAttr = element.Attribute("use-attribute-sets");
        var validationAttr = element.Attribute("validation");
        var typeAttr = element.Attribute("type");

        // XTSE1660: Non-schema-aware processor must reject type attribute
        if (typeAttr != null)
        {
            if (ShouldRejectSchemaAware)
                throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the type attribute on xsl:copy", location);
            // Even with import-schema, we can't resolve schema types at runtime
            throw new XsltException($"XTTE1535: Schema type '{typeAttr.Value}' cannot be resolved on xsl:copy (schema validation not supported)", location);
        }
        if (validationAttr != null)
        {
            var v = validationAttr.Value.Trim();
            if (v is "strict" or "type" && ShouldRejectSchemaAware)
                throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept validation=\"{v}\" on xsl:copy", location);
        }

        // XTSE0020: copy-namespaces must be a valid yes/no value
        if (copyNamespacesAttr != null && !copyNamespacesAttr.Value.Contains('{', StringComparison.Ordinal))
        {
            var val = copyNamespacesAttr.Value.Trim();
            if (val != "yes" && val != "no" && val != "true" && val != "false" && val != "1" && val != "0")
                throw new XsltException($"XTSE0020: Invalid value '{copyNamespacesAttr.Value}' for copy-namespaces attribute", location);
        }

        var useAttributeSets = new List<QName>();
        if (useAttributeSetsAttr != null)
        {
            foreach (var n in useAttributeSetsAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                useAttributeSets.Add(ParseQName(n, element));
            }
        }

        return new XsltCopy
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            CopyNamespaces = ParseYesNo(copyNamespacesAttr),
            InheritNamespaces = ParseYesNo(inheritNamespacesAttr),
            UseAttributeSets = useAttributeSets,
            Validation = ParseValidationMode(validationAttr),
            // Type: rejected by XTSE1660 for non-schema-aware processors
            Content = element.Nodes().Any() ? ParseSequenceConstructor(element) : null
        };
    }

    private XsltCopyOf ParseCopyOf(XElement element, SourceLocation? location)
    {
        // XTSE0090: Reject unknown attributes on xsl:copy-of
        foreach (var attr in element.Attributes())
        {
            if (attr.IsNamespaceDeclaration || attr.Name.Namespace != XNamespace.None) continue;
            var localName = attr.Name.LocalName;
            // Skip shadow attributes (underscore-prefixed) — they are XSLT 3.0 compile-time resolved
            if (localName.StartsWith('_')) continue;
            if (localName is not ("select" or "copy-namespaces" or "copy-accumulators" or "validation" or "type"))
                throw new XsltException($"XTSE0090: Attribute '{localName}' is not allowed on xsl:copy-of", location);
        }

        // XTSE0260: xsl:copy-of must not have child content
        if (element.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0260: xsl:copy-of must not have child content", location);

        var selectAttr = element.Attribute("select");
        if (selectAttr == null)
            throw new XsltException("XTSE0010: xsl:copy-of requires a select attribute", location);

        var select = ParseExpr(selectAttr.Value, selectAttr);
        var copyNamespacesAttr = element.Attribute("copy-namespaces");
        var copyAccumulatorsAttr = element.Attribute("copy-accumulators");
        var validationAttr = element.Attribute("validation");
        var typeAttr = element.Attribute("type");

        // XTSE1660: Non-schema-aware processor must reject type attribute
        if (typeAttr != null)
        {
            if (ShouldRejectSchemaAware)
                throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the type attribute on xsl:copy-of", location);
            throw new XsltException($"XTTE1535: Schema type '{typeAttr.Value}' cannot be resolved on xsl:copy-of (schema validation not supported)", location);
        }
        if (validationAttr != null)
        {
            var v = validationAttr.Value.Trim();
            if (v is "strict" or "type" && ShouldRejectSchemaAware)
                throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept validation=\"{v}\" on xsl:copy-of", location);
        }

        // XTSE0020: copy-namespaces must be a valid yes/no value
        if (copyNamespacesAttr != null && !copyNamespacesAttr.Value.Contains('{', StringComparison.Ordinal))
        {
            var val = copyNamespacesAttr.Value.Trim();
            if (val != "yes" && val != "no" && val != "true" && val != "false" && val != "1" && val != "0")
                throw new XsltException($"XTSE0020: Invalid value '{copyNamespacesAttr.Value}' for copy-namespaces attribute", location);
        }

        return new XsltCopyOf
        {
            Location = location,
            Select = select,
            CopyNamespaces = ParseYesNo(copyNamespacesAttr),
            CopyAccumulators = ParseYesNo(copyAccumulatorsAttr),
            Validation = ParseValidationMode(validationAttr)
        };
    }

    private XsltSequence ParseSequenceInstr(XElement element, SourceLocation? location)
    {
        // XTSE0090: 'as' attribute is not permitted on xsl:sequence
        if (element.Attribute("as") != null)
            throw new XsltException("XTSE0090: Attribute 'as' is not permitted on xsl:sequence", location);

        var selectAttr = element.Attribute("select");
        var hasContent = element.Nodes().Any(n => (n is XElement e && e.Name != XsltNs + "fallback") || (n is XText t && !string.IsNullOrWhiteSpace(t.Value)));

        // XTSE3185 (XSLT 3.0): xsl:sequence must not have both select and content
        if (selectAttr != null && hasContent)
            throw new XsltException("XTSE3185: xsl:sequence must not have both a select attribute and child content", location);

        // Content: parse when no select and element has content (child elements OR significant text).
        // Text-only content is important for expand-text TVTs like <xsl:sequence expand-text="yes">{expr}</xsl:sequence>.
        var hasContentForParsing = selectAttr == null && (element.HasElements || hasContent);

        return new XsltSequence
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = hasContentForParsing
                ? ParseSequenceConstructor(element)
                : null
        };
    }

    private XsltComment ParseComment(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select");

        // XTSE0940: select and non-empty content are mutually exclusive
        ValidateSelectContentExclusive(selectAttr, element, "XTSE0940", "xsl:comment", location);

        return new XsltComment
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.Nodes().Any()
                ? ParseSequenceConstructor(element)
                : null
        };
    }

    private XsltProcessingInstruction ParsePI(XElement element, SourceLocation? location)
    {
        var name = ParseAvt(element.Attribute("name")!.Value, element, element.Attribute("name"));
        var selectAttr = element.Attribute("select");

        // XTSE0880: select and non-empty content are mutually exclusive
        ValidateSelectContentExclusive(selectAttr, element, "XTSE0880", "xsl:processing-instruction", location);

        return new XsltProcessingInstruction
        {
            Location = location,
            Name = name,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.Nodes().Any()
                ? ParseSequenceConstructor(element)
                : null
        };
    }

    private XsltNamespace ParseNamespaceInstr(XElement element, SourceLocation? location)
    {
        var name = ParseAvt(element.Attribute("name")!.Value, element, element.Attribute("name"));
        var selectAttr = element.Attribute("select");

        // XTSE0910: select and non-empty content are mutually exclusive
        ValidateSelectContentExclusive(selectAttr, element, "XTSE0910", "xsl:namespace", location);

        return new XsltNamespace
        {
            Location = location,
            Name = name,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.Nodes().Any()
                ? ParseSequenceConstructor(element)
                : null
        };
    }

    private XsltDocument ParseDocument(XElement element, SourceLocation? location)
    {
        var validationAttr = element.Attribute("validation");
        var typeAttr = element.Attribute("type");

        // XTSE1660: Non-schema-aware processor must reject type attribute
        if (typeAttr != null && ShouldRejectSchemaAware)
            throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the type attribute on xsl:document", location);
        if (validationAttr != null)
        {
            var v = validationAttr.Value.Trim();
            if (v is "strict" or "type" && ShouldRejectSchemaAware)
                throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept validation=\"{v}\" on xsl:document", location);
        }

        return new XsltDocument
        {
            Location = location,
            Validation = ParseValidationMode(validationAttr),
            // Type: rejected by XTSE1660 for non-schema-aware processors
            Content = ParseSequenceConstructor(element)
        };
    }

    private XsltResultDocument ParseResultDocument(XElement element, SourceLocation? location)
    {
        var hrefAttr = element.Attribute("href");
        var formatAttr = element.Attribute("format");
        var validationAttr = element.Attribute("validation");
        var typeAttr = element.Attribute("type");
        var methodAttr = element.Attribute("method");
        var omitXmlDeclAttr = element.Attribute("omit-xml-declaration");
        var encodingAttr = element.Attribute("encoding");
        var indentAttr = element.Attribute("indent");
        var buildTreeAttr = element.Attribute("build-tree");
        var itemSeparatorAttr = element.Attribute("item-separator");
        var htmlVersionAttr = element.Attribute("html-version");
        var useCharMapsAttr = element.Attribute("use-character-maps");
        var allowDupNamesAttr = element.Attribute("allow-duplicate-names");

        // XTSE0020: html-version must be a valid decimal number (static check only — AVTs validated at runtime)
        if (htmlVersionAttr != null && !htmlVersionAttr.Value.Contains('{', StringComparison.Ordinal))
        {
            if (!decimal.TryParse(htmlVersionAttr.Value.Trim(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out _))
                throw new XsltException($"XTSE0020: Invalid html-version value '{htmlVersionAttr.Value}': must be a decimal number", location);
        }

        // XTSE0020: reject invalid literal values for the yes-or-no serialization attributes.
        // Values may be AVTs (e.g. standalone="{$x}"); those are validated at runtime, so
        // skip any value containing a "{".
        foreach (var yn in new[] { "omit-xml-declaration", "indent", "byte-order-mark",
                                   "escape-uri-attributes", "include-content-type",
                                   "undeclare-prefixes", "allow-duplicate-names" })
        {
            var a = element.Attribute(yn);
            if (a != null && !a.Value.Contains('{', StringComparison.Ordinal) && ParseYesNo(a) == null)
                throw new XsltException($"XTSE0020: Invalid value '{a.Value}' for xsl:result-document {yn} attribute (must be yes, no, true, false, 1, or 0)", location);
        }
        var rdStandalone = element.Attribute("standalone");
        if (rdStandalone != null && !rdStandalone.Value.Contains('{', StringComparison.Ordinal))
        {
            var v = rdStandalone.Value.Trim();
            if (v is not ("yes" or "no" or "true" or "false" or "1" or "0" or "omit"))
                throw new XsltException($"XTSE0020: Invalid standalone value '{rdStandalone.Value}' on xsl:result-document: must be yes, no, true, false, 1, 0, or omit", location);
        }
        var rdDoctypePublic = element.Attribute("doctype-public");
        if (rdDoctypePublic != null && !rdDoctypePublic.Value.Contains('{', StringComparison.Ordinal)
            && !IsValidPublicId(rdDoctypePublic.Value))
            throw new XsltException($"XTSE0020: Invalid doctype-public value '{rdDoctypePublic.Value}' on xsl:result-document: not a valid public identifier", location);

        // XTSE1660: Non-schema-aware processor must reject type attribute
        if (typeAttr != null && ShouldRejectSchemaAware)
            throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the type attribute on xsl:result-document", location);
        if (validationAttr != null)
        {
            var v = validationAttr.Value.Trim();
            if (v is "strict" or "type" && ShouldRejectSchemaAware)
                throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept validation=\"{v}\" on xsl:result-document", location);
        }

        var formatAvt = formatAttr != null ? ParseAvt(formatAttr.Value, element, formatAttr) : null;
        // Resolve static format names at parse time (namespace-aware)
        QName? resolvedFormat = null;
        if (formatAttr != null && formatAvt != null
            && formatAvt.Parts.Count == 1 && formatAvt.Parts[0] is AvtLiteral)
        {
            resolvedFormat = ParseQName(formatAttr.Value.Trim(), element);
        }

        // Collect namespace bindings for runtime resolution of prefixed format names
        IReadOnlyDictionary<string, string>? nsBindings = null;
        if (formatAvt != null && resolvedFormat == null)
        {
            var bindings = new Dictionary<string, string>();
            foreach (var ns in element.AncestorsAndSelf().SelectMany(e => e.Attributes().Where(a => a.IsNamespaceDeclaration)))
            {
                var prefix = ns.Name.LocalName == "xmlns" ? "" : ns.Name.LocalName;
                bindings.TryAdd(prefix, ns.Value);
            }
            nsBindings = bindings;
        }

        var useCharMaps = new List<QName>();
        if (useCharMapsAttr != null)
        {
            foreach (var n in useCharMapsAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                useCharMaps.Add(ParseQName(n, element));
        }

        return new XsltResultDocument
        {
            Location = location,
            Href = hrefAttr != null ? ParseAvt(hrefAttr.Value, element, hrefAttr) : null,
            Format = formatAvt,
            ResolvedFormatName = resolvedFormat,
            Validation = ParseValidationMode(validationAttr),
            // Type: rejected by XTSE1660 for non-schema-aware processors
            Method = methodAttr != null ? ParseAvt(methodAttr.Value, element, methodAttr) : null,
            OmitXmlDeclaration = omitXmlDeclAttr != null ? ParseAvt(omitXmlDeclAttr.Value, element, omitXmlDeclAttr) : null,
            Encoding = encodingAttr != null ? ParseAvt(encodingAttr.Value, element, encodingAttr) : null,
            Indent = indentAttr != null ? ParseAvt(indentAttr.Value, element, indentAttr) : null,
            BuildTree = ParseYesNo(buildTreeAttr),
            ItemSeparator = itemSeparatorAttr != null ? ParseAvt(itemSeparatorAttr.Value, element, itemSeparatorAttr) : null,
            AllowDuplicateNames = allowDupNamesAttr != null ? ParseAvt(allowDupNamesAttr.Value, element, allowDupNamesAttr) : null,
            UseCharacterMaps = useCharMaps,
            NamespaceBindings = nsBindings,
            Content = ParseSequenceConstructor(element)
        };
    }

    /// <summary>
    /// Parse EXSLT exsl:document extension element as an xsl:result-document equivalent.
    /// exsl:document uses 'href' for the output URI and standard serialization attributes.
    /// </summary>
    private XsltResultDocument ParseExslDocument(XElement element, SourceLocation? location)
    {
        var hrefAttr = element.Attribute("href");
        var methodAttr = element.Attribute("method");
        var encodingAttr = element.Attribute("encoding");
        var indentAttr = element.Attribute("indent");

        return new XsltResultDocument
        {
            Location = location,
            Href = hrefAttr != null ? ParseAvt(hrefAttr.Value, element, hrefAttr) : null,
            Method = methodAttr != null ? ParseAvt(methodAttr.Value, element, methodAttr) : null,
            Encoding = encodingAttr != null ? ParseAvt(encodingAttr.Value, element, encodingAttr) : null,
            Indent = indentAttr != null ? ParseAvt(indentAttr.Value, element, indentAttr) : null,
            Content = ParseSequenceConstructor(element)
        };
    }

    private XsltSourceDocument ParseSourceDocument(XElement element, SourceLocation? location, bool forceStreamable = false)
    {
        var hrefAttr = element.Attribute("href");
        var streamableAttr = element.Attribute("streamable");
        var validationAttr = element.Attribute("validation");
        var useAccumulatorsAttr = element.Attribute("use-accumulators");

        if (hrefAttr == null)
            throw new XsltException("xsl:source-document requires href attribute", location);

        var useAccumulators = new List<QName>();
        if (useAccumulatorsAttr != null)
        {
            foreach (var name in useAccumulatorsAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (name == "#all")
                    continue; // Will be handled at runtime by using all declared accumulators
                useAccumulators.Add(ParseQName(name, element));
            }
        }

        var result = new XsltSourceDocument
        {
            Location = location,
            Href = ParseAvt(hrefAttr.Value, element, hrefAttr),
            Streamable = forceStreamable || streamableAttr?.Value == "yes",
            Validation = ParseValidationMode(validationAttr) ?? Ast.ValidationMode.Strip,
            Content = element.Nodes().Any() ? ParseSequenceConstructor(element) : null,
            BaseUri = ResolveEffectiveBaseUri(element),
            UseAccumulators = useAccumulators
        };

        // XTSE3430: Check streamability of the body when streamable="yes".
        // Defer the error to runtime so shared stylesheets with multiple templates
        // can compile even if some templates have non-streamable source-document bodies.
        if (result.Streamable)
        {
            try
            {
                StreamabilityChecker.CheckSourceDocumentBody(result.Content, location, _currentStylesheet?.AttributeSets, _currentStylesheet?.Functions);
            }
            catch (XsltException ex)
            {
                result = new XsltSourceDocument
                {
                    Location = result.Location,
                    Href = result.Href,
                    Streamable = result.Streamable,
                    Validation = result.Validation,
                    Content = result.Content,
                    BaseUri = result.BaseUri,
                    UseAccumulators = result.UseAccumulators,
                    StreamabilityError = ex.Message
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Parses xsl:stream — an alias for xsl:source-document with streamable="yes" (XSLT 3.0 §8.4).
    /// </summary>
    private XsltSourceDocument ParseStream(XElement element, SourceLocation? location)
        => ParseSourceDocument(element, location, forceStreamable: true);


    /// <summary>
    /// Resolves the effective base URI for an element by walking up xml:base attributes
    /// per the XML Base specification (RFC 2396/3986).
    /// </summary>
    private Uri? ResolveEffectiveBaseUri(XElement element)
    {
        // Collect xml:base attributes from the element and its ancestors (innermost first)
        var xmlBaseAttrs = new List<string>();
        for (XElement? el = element; el != null; el = el.Parent)
        {
            var xmlBase = el.Attribute(XNamespace.Xml + "base");
            if (xmlBase != null)
                xmlBaseAttrs.Add(xmlBase.Value);
        }

        if (xmlBaseAttrs.Count == 0)
            return _baseUri;

        // Start from the stylesheet base URI, then apply xml:base values from outermost to innermost
        Uri? result = _baseUri;
        for (int i = xmlBaseAttrs.Count - 1; i >= 0; i--)
        {
            var xmlBase = xmlBaseAttrs[i];
            // Check for genuine absolute URI (with explicit scheme like http:, https:, etc.)
            // On Linux, Uri.TryCreate("/path", Absolute) succeeds as file:///path, but in
            // XML Base spec /path is a relative URI reference that should resolve against the parent.
            if (Uri.TryCreate(xmlBase, UriKind.Absolute, out var absUri)
                && xmlBase.Contains(':', StringComparison.Ordinal)
                && !xmlBase.StartsWith('/')
                && !xmlBase.StartsWith('.'))
                result = absUri;
            else if (result != null)
                result = new Uri(result, xmlBase);
            else if (Uri.TryCreate(xmlBase, UriKind.Relative, out _))
                continue; // Cannot resolve relative without a base — skip
        }

        return result;
    }

    private XsltMessage ParseMessage(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select");
        var terminateAttr = element.Attribute("terminate");
        var errorCodeAttr = element.Attribute("error-code");

        // Validate terminate attribute: must be AVT or static "yes"/"no" (XSLT 2.0) or yes-or-no (XSLT 3.0)
        bool isTerminateAvt = false;
        if (terminateAttr != null)
        {
            var tv = terminateAttr.Value;
            bool isAvt = tv.Contains('{', StringComparison.Ordinal);
            if (!isAvt && tv is not ("yes" or "no" or "true" or "false" or "1" or "0"))
                throw new XsltException($"XTSE0020: Invalid value for 'terminate' attribute: '{tv}'. Must be 'yes' or 'no'.");
            isTerminateAvt = isAvt;
        }

        return new XsltMessage
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.Nodes().Any() ? ParseSequenceConstructor(element) : null,
            Terminate = terminateAttr?.Value is "yes" or "true" or "1",
            TerminateAvt = isTerminateAvt ? ParseAvt(terminateAttr!.Value, element, terminateAttr) : null,
            ErrorCode = errorCodeAttr?.Value
        };
    }

    private XsltAssert ParseAssert(XElement element, SourceLocation? location)
    {
        var test = ParseExpr(element.Attribute("test")!.Value, element.Attribute("test"));
        var selectAttr = element.Attribute("select");
        var errorCodeAttr = element.Attribute("error-code");

        return new XsltAssert
        {
            Location = location,
            Test = test,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.HasElements
                ? ParseSequenceConstructor(element)
                : null,
            ErrorCode = errorCodeAttr?.Value
        };
    }

    private XsltVariableInstruction ParseVariableInstr(XElement element, SourceLocation? location)
    {
        var name = ParseQName(element.Attribute("name")!.Value, element);
        var asAttr = element.Attribute("as");
        var selectAttr = element.Attribute("select");

        // XTSE0620: select attribute and non-empty content are mutually exclusive
        if (selectAttr != null && element.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0620: An xsl:variable element must not have both a select attribute and non-empty content", location);

        return new XsltVariableInstruction
        {
            Location = location,
            Name = name,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = ParseContentBody(element, selectAttr),
            BaseUri = ResolveEffectiveBaseUri(element)
        };
    }

    private XsltParamInstruction ParseParamInstr(XElement element, SourceLocation? location)
    {
        var name = ParseQName(element.Attribute("name")!.Value, element);
        var asAttr = element.Attribute("as");
        var selectAttr = element.Attribute("select");
        var requiredAttr = element.Attribute("required");
        var tunnelAttr = element.Attribute("tunnel");

        return new XsltParamInstruction
        {
            Location = location,
            Name = name,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = ParseContentBody(element, selectAttr),
            Required = requiredAttr?.Value == "yes",
            Tunnel = ParseYesNoBoolean(tunnelAttr, "tunnel", location)
        };
    }

    private XsltNumber ParseNumber(XElement element, SourceLocation? location)
    {
        var valueAttr = element.Attribute("value");
        var selectAttr = element.Attribute("select");
        var levelAttr = element.Attribute("level");
        var countAttr = element.Attribute("count");
        var fromAttr = element.Attribute("from");
        var formatAttr = element.Attribute("format");
        var langAttr = element.Attribute("lang");
        var letterValueAttr = element.Attribute("letter-value");
        var ordinalAttr = element.Attribute("ordinal");
        var groupingSeparatorAttr = element.Attribute("grouping-separator");
        var groupingSizeAttr = element.Attribute("grouping-size");
        var startAtAttr = element.Attribute("start-at");

        // XTSE0975: value attribute of xsl:number must not be combined with select, level, count, or from
        if (valueAttr != null)
        {
            if (selectAttr != null)
                throw new XsltException("XTSE0975: The value and select attributes of xsl:number are mutually exclusive", location);
            if (levelAttr != null)
                throw new XsltException("XTSE0975: The value and level attributes of xsl:number are mutually exclusive", location);
            if (countAttr != null)
                throw new XsltException("XTSE0975: The value and count attributes of xsl:number are mutually exclusive", location);
            if (fromAttr != null)
                throw new XsltException("XTSE0975: The value and from attributes of xsl:number are mutually exclusive", location);
        }

        // Validate start-at: must be a list of space-separated integers (XTSE0020)
        if (startAtAttr != null && !startAtAttr.Value.Contains('{', StringComparison.Ordinal))
        {
            var parts = startAtAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (!long.TryParse(part, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
                    throw new XsltException($"XTSE0020: Invalid start-at value '{startAtAttr.Value}': " +
                        "must be a list of space-separated integers");
            }
        }

        return new XsltNumber
        {
            Location = location,
            Value = valueAttr != null ? ParseExpr(valueAttr.Value, valueAttr) : null,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Level = levelAttr?.Value switch
            {
                "single" => NumberLevel.Single,
                "multiple" => NumberLevel.Multiple,
                "any" => NumberLevel.Any,
                _ => NumberLevel.Single
            },
            Count = countAttr != null ? ParsePattern(countAttr.Value, element) : null,
            From = fromAttr != null ? ParsePattern(fromAttr.Value, element) : null,
            Format = formatAttr != null ? ParseAvt(formatAttr.Value, element, formatAttr) : null,
            Lang = langAttr != null ? ParseAvt(langAttr.Value, element, langAttr) : null,
            LetterValue = letterValueAttr != null ? ParseAvt(letterValueAttr.Value, element, letterValueAttr) : null,
            OrdinalValue = ordinalAttr != null ? ParseAvt(ordinalAttr.Value, element, ordinalAttr) : null,
            GroupingSeparator = groupingSeparatorAttr != null ? ParseAvt(groupingSeparatorAttr.Value, element, groupingSeparatorAttr) : null,
            GroupingSize = groupingSizeAttr != null ? ParseAvt(groupingSizeAttr.Value, element, groupingSizeAttr) : null,
            StartAt = startAtAttr != null ? ParseAvt(startAtAttr.Value, element, startAtAttr) : null
        };
    }

    private XsltPerformSort ParsePerformSort(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select");

        // XTSE1040: when select is present, only xsl:sort and xsl:fallback children are allowed
        if (selectAttr != null)
        {
            foreach (var child in element.Elements())
            {
                if (child.Name != XsltNs + "sort" && child.Name != XsltNs + "fallback")
                    throw new XsltException($"XTSE1040: xsl:perform-sort with a select attribute must not contain {child.Name.LocalName} — only xsl:sort and xsl:fallback are allowed",
                        GetSourceLocation(child));
            }
        }

        var sorts = new List<XsltSort>();
        var contentInstructions = new List<XsltInstruction>();
        var expandText = IsExpandTextActive(element);
        var pastSorts = false;

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child when child.Name == XsltNs + "sort":
                    if (!ShouldIncludeElement(child)) break;
                    if (pastSorts)
                        throw new XsltException("XTSE0010: xsl:sort elements must come before other content in xsl:perform-sort", location);
                    sorts.Add(ParseSort(child));
                    break;
                case XElement child:
                    pastSorts = true;
                    contentInstructions.Add(ParseInstruction(child));
                    break;
                case XText text:
                    if (!IsXmlWhitespaceOnly(text.Value))
                    {
                        pastSorts = true;
                        contentInstructions.Add(CreateTextInstruction(text.Value, expandText, element));
                    }
                    break;
            }
        }

        return new XsltPerformSort
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Sorts = sorts,
            Content = contentInstructions.Count > 0
                ? new XsltSequenceConstructor { Instructions = contentInstructions }
                : null
        };
    }

    private XsltAnalyzeString ParseAnalyzeString(XElement element, SourceLocation? location)
    {
        var select = ParseExpr(element.Attribute("select")!.Value, element.Attribute("select"));
        var regex = ParseAvt(element.Attribute("regex")!.Value, element, element.Attribute("regex"));
        var flagsAttr = element.Attribute("flags");

        XsltSequenceConstructor? matchingSubstring = null;
        XsltSequenceConstructor? nonMatchingSubstring = null;
        bool seenMatching = false;
        bool seenNonMatching = false;
        bool seenFallback = false;

        foreach (var child in element.Elements())
        {
            if (child.Name == XsltNs + "matching-substring")
            {
                // XTSE0010: matching-substring must come before non-matching-substring and fallback
                if (seenNonMatching || seenFallback)
                    throw new XsltException(
                        "XTSE0010: xsl:matching-substring must appear before xsl:non-matching-substring and xsl:fallback",
                        GetSourceLocation(child));
                matchingSubstring = ParseSequenceConstructor(child);
                seenMatching = true;
            }
            else if (child.Name == XsltNs + "non-matching-substring")
            {
                // XTSE0010: non-matching-substring must come before fallback
                if (seenFallback)
                    throw new XsltException(
                        "XTSE0010: xsl:non-matching-substring must appear before xsl:fallback",
                        GetSourceLocation(child));
                nonMatchingSubstring = ParseSequenceConstructor(child);
                seenNonMatching = true;
            }
            else if (child.Name == XsltNs + "fallback")
            {
                seenFallback = true;
            }
            else
            {
                // XTSE0010: unexpected child element
                if (seenMatching || seenNonMatching)
                    throw new XsltException(
                        $"XTSE0010: Unexpected child element in xsl:analyze-string: {child.Name.LocalName}",
                        GetSourceLocation(child));
            }
        }

        // XTSE1130: at least one of matching-substring/non-matching-substring required
        if (matchingSubstring == null && nonMatchingSubstring == null)
            throw new InvalidOperationException(
                "XTSE1130: xsl:analyze-string must contain xsl:matching-substring or xsl:non-matching-substring");

        return new XsltAnalyzeString
        {
            Location = location,
            Select = select,
            Regex = regex,
            Flags = flagsAttr != null ? ParseAvt(flagsAttr.Value, element, flagsAttr) : null,
            MatchingSubstring = matchingSubstring,
            NonMatchingSubstring = nonMatchingSubstring
        };
    }

    private XsltBreak ParseBreak(XElement element, SourceLocation? location)
    {
        // Validate lexical enclosure: must be inside xsl:iterate body
        ValidateIterateChildLocation(element, "xsl:break", location);

        // Validate: must be last instruction in its sequence constructor (XTSE3120)
        ValidateLastInSequence(element, "xsl:break", location);

        var selectAttr = element.Attribute("select");

        // Validate: select and content are mutually exclusive (XTSE3125)
        if (selectAttr != null && element.Nodes().Any())
            throw new XsltException("XTSE3125: xsl:break must not have both a select attribute and content", location);

        return new XsltBreak
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.HasElements
                ? ParseSequenceConstructor(element)
                : null
        };
    }

    private XsltNextIteration ParseNextIteration(XElement element, SourceLocation? location)
    {
        // Validate lexical enclosure: must be inside xsl:iterate
        ValidateIterateChildLocation(element, "xsl:next-iteration", location);

        var withParams = new List<XsltWithParam>();
        foreach (var child in element.Elements(XsltNs + "with-param"))
        {
            if (!ShouldIncludeElement(child)) continue;
            withParams.Add(ParseWithParam(child));
        }

        // Validate: no duplicate with-param names (XTSE0670)
        var paramNames = new HashSet<QName>();
        foreach (var wp in withParams)
        {
            if (!paramNames.Add(wp.Name))
                throw new XsltException($"XTSE0670: Duplicate with-param name '{wp.Name}' in xsl:next-iteration", location);
        }

        return new XsltNextIteration
        {
            Location = location,
            WithParams = withParams
        };
    }

    private XsltMerge ParseMerge(XElement element, SourceLocation? location)
    {
        var sources = new List<XsltMergeSource>();
        XsltSequenceConstructor? action = null;
        var actionCount = 0;

        foreach (var child in element.Elements())
        {
            if (child.Name == XsltNs + "merge-source")
            {
                sources.Add(ParseMergeSource(child));
            }
            else if (child.Name == XsltNs + "merge-action")
            {
                actionCount++;
                if (actionCount > 1)
                    throw new XsltException("XTSE0010: xsl:merge must not contain more than one xsl:merge-action", location);
                action = ParseSequenceConstructor(child);
            }
            else if (child.Name == XsltNs + "fallback")
            {
                // xsl:fallback must appear after xsl:merge-action
                if (actionCount == 0)
                    throw new XsltException("XTSE0010: xsl:fallback must appear after xsl:merge-action in xsl:merge", location);
                // Ignore fallback content
            }
            else
            {
                // XTSE0010: Only merge-source, merge-action, and fallback allowed as children
                throw new XsltException($"XTSE0010: xsl:merge must not contain {child.Name.LocalName}", location);
            }
        }

        if (action == null)
            throw new XsltException("XTSE0010: xsl:merge must contain xsl:merge-action", location);

        if (sources.Count == 0)
            throw new XsltException("XTSE0010: xsl:merge must contain at least one xsl:merge-source", location);

        // XTSE3190: Sibling merge-source elements must not have the same name
        // Only applies to explicitly named sources; unnamed sources are allowed to coexist
        var mergeSourceNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source.Name is { } sourceName && !mergeSourceNames.Add(sourceName))
                throw new XsltException($"XTSE3190: Sibling xsl:merge-source elements must not have the same name '{sourceName}'",
                    source.Location);
        }

        // XTSE2200: All merge-sources must have the same number of merge-keys
        if (sources.Count > 1)
        {
            var expectedKeyCount = sources[0].MergeKeys.Count;
            for (var i = 1; i < sources.Count; i++)
            {
                if (sources[i].MergeKeys.Count != expectedKeyCount)
                    throw new XsltException(
                        $"XTSE2200: All xsl:merge-source elements must have the same number of xsl:merge-key children (expected {expectedKeyCount}, found {sources[i].MergeKeys.Count})",
                        sources[i].Location);
            }

            // XTSE0020: Validate that order attributes are consistent across sources for each key position
            for (var ki = 0; ki < expectedKeyCount; ki++)
            {
                var firstOrder = sources[0].MergeKeys[ki].Order;
                var firstDataType = sources[0].MergeKeys[ki].DataType;
                var firstLang = sources[0].MergeKeys[ki].Lang;
                for (var si = 1; si < sources.Count; si++)
                {
                    // Static check: if order/data-type/lang are literal (non-AVT) values, they must match
                    var otherDataType = sources[si].MergeKeys[ki].DataType;
                    if (firstDataType != null && otherDataType != null)
                    {
                        // Check for literal mismatch
                        var firstDtVal = GetLiteralAvtValue(firstDataType);
                        var otherDtVal = GetLiteralAvtValue(otherDataType);
                        if (firstDtVal != null && otherDtVal != null && firstDtVal != otherDtVal)
                            throw new XsltException(
                                $"XTDE2210: Incompatible data-type attributes on merge-key: '{firstDtVal}' vs '{otherDtVal}'",
                                location);
                    }
                    var otherLang = sources[si].MergeKeys[ki].Lang;
                    if (firstLang != null && otherLang != null)
                    {
                        var firstLangVal = GetLiteralAvtValue(firstLang);
                        var otherLangVal = GetLiteralAvtValue(otherLang);
                        if (firstLangVal != null && otherLangVal != null && firstLangVal != otherLangVal)
                            throw new XsltException(
                                $"XTDE2210: Incompatible lang attributes on merge-key: '{firstLangVal}' vs '{otherLangVal}'",
                                location);
                    }
                }
            }
        }

        return new XsltMerge
        {
            Location = location,
            Sources = sources,
            Action = action
        };
    }

    private XsltMergeSource ParseMergeSource(XElement element)
    {
        var location = GetSourceLocation(element);
        var nameAttr = element.Attribute("name");
        var selectAttr = element.Attribute("select");
        var forEachItemAttr = element.Attribute("for-each-item");
        var forEachSourceAttr = element.Attribute("for-each-source");
        var sortBeforeMergeAttr = element.Attribute("sort-before-merge");
        var streamableAttr = element.Attribute("streamable");
        var useAccumulatorsAttr = element.Attribute("use-accumulators");

        if (selectAttr == null)
            throw new XsltException("XTSE0010: xsl:merge-source must have a select attribute", location);

        // XTSE0020: Validate streamable is yes/no if present
        NormalizeYesNo(streamableAttr?.Value, "streamable", "xsl:merge-source", element);

        // XTSE3195: Cannot specify both for-each-item and for-each-source
        if (forEachItemAttr != null && forEachSourceAttr != null)
            throw new XsltException("XTSE3195: xsl:merge-source must not have both for-each-item and for-each-source", location);

        // XTSE1505: validation and type are mutually exclusive
        var validationAttr = element.Attribute("validation");
        var typeAttr = element.Attribute("type");
        if (validationAttr != null && typeAttr != null)
            throw new XsltException("XTSE1505: The validation and type attributes are mutually exclusive on xsl:merge-source", location);

        // XTSE1660/XTSE1650: type attribute requires schema-aware processing
        if (typeAttr != null)
        {
            if (ShouldRejectSchemaAware)
                throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the type attribute on xsl:merge-source", location);
            // Even with import-schema, we don't actually resolve schema types
            throw new XsltException($"XTTE1540: Schema type '{typeAttr.Value}' cannot be resolved (schema validation not supported)", location);
        }

        // XTSE1660: validation="strict" requires schema-aware processing
        if (validationAttr != null && validationAttr.Value.Trim() is "strict" or "type" && ShouldRejectSchemaAware)
            throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept validation=\"{validationAttr.Value.Trim()}\" on xsl:merge-source", location);

        // XTTE1510: validation="strict" with import-schema but no actual validation runtime
        if (validationAttr != null && validationAttr.Value.Trim() == "strict" && _hasImportSchema)
            throw new XsltException("XTTE1510: Schema validation is not supported (validation=\"strict\" on xsl:merge-source)", location);

        // XTSE0020: Validate name is a valid NCName if specified
        if (nameAttr != null)
        {
            try { System.Xml.XmlConvert.VerifyNCName(nameAttr.Value); }
            catch { throw new XsltException($"XTSE0020: Invalid NCName '{nameAttr.Value}' for name attribute on xsl:merge-source", location); }
        }

        var mergeKeys = new List<XsltMergeKey>();

        foreach (var child in element.Elements())
        {
            if (child.Name == XsltNs + "merge-key")
            {
                mergeKeys.Add(ParseMergeKey(child));
            }
            else
            {
                // XTSE0010: Only merge-key allowed as child of merge-source
                throw new XsltException($"XTSE0010: xsl:merge-source must not contain {child.Name.LocalName}", location);
            }
        }

        // Parse use-accumulators attribute
        var useAccumulators = new List<QName>();
        if (useAccumulatorsAttr != null)
        {
            var accNames = useAccumulatorsAttr.Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var accNameStr in accNames)
            {
                useAccumulators.Add(ParseQName(accNameStr, element));
            }
        }

        return new XsltMergeSource
        {
            Name = nameAttr?.Value,
            Location = location,
            Select = ParseExpr(selectAttr.Value, selectAttr),
            ForEachItem = forEachItemAttr != null ? ParseExpr(forEachItemAttr.Value, forEachItemAttr) : null,
            ForEachSource = forEachSourceAttr != null ? ParseExpr(forEachSourceAttr.Value, forEachSourceAttr) : null,
            SortBeforeMerge = sortBeforeMergeAttr != null
                ? ParseYesNo(sortBeforeMergeAttr) ?? throw new XsltException($"XTSE0020: Invalid value '{sortBeforeMergeAttr.Value}' for sort-before-merge attribute (must be yes/no/true/false/0/1)", location)
                : false,
            Streamable = streamableAttr != null
                ? ParseYesNo(streamableAttr) ?? false
                : false,
            MergeKeys = mergeKeys,
            UseAccumulators = useAccumulators
        };
    }

    private XsltMergeKey ParseMergeKey(XElement element)
    {
        var location = GetSourceLocation(element);
        var selectAttr = element.Attribute("select");
        var orderAttr = element.Attribute("order");
        var collationAttr = element.Attribute("collation");
        var dataTypeAttr = element.Attribute("data-type");
        var langAttr = element.Attribute("lang");

        // XTSE0090: Reject disallowed attributes on xsl:merge-key
        var stableAttr = element.Attribute("stable");
        if (stableAttr != null)
            throw new XsltException("XTSE0090: Attribute 'stable' is not allowed on xsl:merge-key", location);

        // XTSE3200: xsl:merge-key with select must not have content
        ValidateSelectContentExclusive(selectAttr, element, "XTSE3200", "xsl:merge-key", location);

        // xsl:merge-key can use either select attribute or child content
        XQueryExpression? select = null;
        XsltSequenceConstructor? content = null;
        if (selectAttr != null)
        {
            select = ParseExpr(selectAttr.Value, selectAttr);
        }
        else if (element.HasElements || element.Nodes().Any(n => n is XText t && !string.IsNullOrWhiteSpace(t.Value)))
        {
            // Body content — parse as a sequence constructor
            content = ParseSequenceConstructor(element);
        }
        else
        {
            // Default: select="."
            select = ParseExpr(".");
        }

        return new XsltMergeKey
        {
            Select = select,
            Content = content,
            Order = orderAttr != null ? ParseAvt(orderAttr.Value, element, orderAttr) : null,
            Collation = collationAttr != null ? ParseAvt(collationAttr.Value, element, collationAttr) : null,
            DataType = dataTypeAttr != null ? ParseAvt(dataTypeAttr.Value, element, dataTypeAttr) : null,
            Lang = langAttr != null ? ParseAvt(langAttr.Value, element, langAttr) : null
        };
    }

    private XsltFork ParseFork(XElement element, SourceLocation? location)
    {
        var forEachGroups = new List<XsltForEachGroup>();
        var sequences = new List<XsltSequenceConstructor>();
        var resultDocuments = new List<XsltResultDocument>();

        foreach (var child in element.Elements())
        {
            if (child.Name == XsltNs + "for-each-group")
                forEachGroups.Add((XsltForEachGroup)ParseForEachGroup(child, GetSourceLocation(child)));
            else if (child.Name == XsltNs + "sequence")
            {
                // xsl:sequence in xsl:fork: parse as instruction and wrap in a sequence constructor
                var instr = ParseSequenceInstr(child, GetSourceLocation(child));
                sequences.Add(new XsltSequenceConstructor { Instructions = [instr] });
            }
            else if (child.Name == XsltNs + "result-document")
                resultDocuments.Add((XsltResultDocument)ParseResultDocument(child, GetSourceLocation(child)));
        }

        return new XsltFork
        {
            Location = location,
            ForEachGroups = forEachGroups,
            Sequences = sequences,
            ResultDocuments = resultDocuments
        };
    }

    private XsltMap ParseMap(XElement element, SourceLocation? location)
    {
        return new XsltMap
        {
            Location = location,
            Content = element.Nodes().Any() ? ParseSequenceConstructor(element) : null
        };
    }

    private XsltMapEntry ParseMapEntry(XElement element, SourceLocation? location)
    {
        var key = ParseExpr(element.Attribute("key")!.Value, element.Attribute("key"));
        var selectAttr = element.Attribute("select");

        // XTSE3280: xsl:map-entry with select must not have content other than xsl:fallback
        if (selectAttr != null)
        {
            foreach (var node in element.Nodes())
            {
                if (node is XElement child && child.Name != XsltNs + "fallback")
                    throw new XsltException("XTSE3280: xsl:map-entry with a select attribute must not have content other than xsl:fallback",
                        GetSourceLocation(child));
                if (node is XText text && !IsXmlWhitespaceOnly(text.Value))
                    throw new XsltException("XTSE3280: xsl:map-entry with a select attribute must not have text content", location);
            }
        }

        return new XsltMapEntry
        {
            Location = location,
            Key = key,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = ParseContentBody(element, selectAttr)
        };
    }

    private XsltArray ParseArray(XElement element, SourceLocation? location)
    {
        return new XsltArray
        {
            Location = location,
            Content = element.Nodes().Any() ? ParseSequenceConstructor(element) : null
        };
    }

    private XsltArrayMember ParseArrayMember(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select");

        return new XsltArrayMember
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.HasElements
                ? ParseSequenceConstructor(element)
                : null
        };
    }

    private XsltLiteralResultElement ParseLiteralResultElement(XElement element, SourceLocation? location)
    {
        // Determine the prefix the stylesheet author intended for this LRE.
        // The XmlReader-built `_elementPrefixMap` is authoritative: it records EVERY
        // element's source prefix (including empty string for default-namespace elements).
        // Map hit with empty value → element had no prefix in source, return null/empty
        // and let serialization use the default-namespace binding. Map hit with non-empty
        // → use that exact prefix. Only fall through to the LINQ walk on a true miss
        // (e.g. when the prefix map couldn't be built — DTD failure, etc.).
        //
        // Earlier versions tried to skip the map for elements LINQ-to-XML thought had no
        // prefix, but `XElement.GetPrefixOfNamespace` returns ANY ancestor's prefix that
        // maps to the element's namespace — so for an element in the default xhtml namespace
        // when both `xmlns="xhtml"` and `xmlns:h="xhtml"` are in scope, LINQ returned "h"
        // and the guard let stale prefix-map hits leak through. Recording the source's
        // empty prefix in the map closes that hole. (Martin Honnen Docbook TNG bug — first
        // surfaced as `<xsl:theme>`, persisted as `<xsl:link>` after the partial fix.)
        string? lrePrefix = null;
        if (element.Name.Namespace != XNamespace.None)
        {
            if (_elementPrefixMap != null && element is IXmlLineInfo eli && eli.HasLineInfo()
                && _elementPrefixMap.TryGetValue((eli.LineNumber, eli.LinePosition), out var originalPrefix))
            {
                lrePrefix = string.IsNullOrEmpty(originalPrefix) ? null : originalPrefix;
            }
            else
            {
                // Fallback: walk up ancestor namespace declarations
                var nsUri = element.Name.NamespaceName;
                var found = false;
                for (var el = element; el != null && !found; el = el.Parent)
                {
                    foreach (var a in el.Attributes().Where(a => a.IsNamespaceDeclaration && a.Value == nsUri))
                    {
                        if (a.Name.Namespace == XNamespace.None) // xmlns="..." (default ns)
                            lrePrefix = null;
                        else
                            lrePrefix = a.Name.LocalName; // xmlns:prefix="..."
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    lrePrefix = element.GetPrefixOfNamespace(element.Name.Namespace);
                    if (string.IsNullOrEmpty(lrePrefix))
                        lrePrefix = null;
                }
            }
        }

        var name = new QName(
            new NamespaceId(0), // Would need proper namespace resolution
            element.Name.LocalName,
            lrePrefix
        );

        var attributes = new Dictionary<QName, XsltAttributeValueTemplate>();
        var namespaceDeclarations = new Dictionary<string, string>();
        var useAttributeSets = new List<QName>();
        var excludeResultPrefixes = new HashSet<string>();
        bool? inheritNamespaces = null;
        string? version = null;
        string? defaultCollation = null;

        foreach (var attr in element.Attributes())
        {
            if (attr.IsNamespaceDeclaration)
            {
                var prefix = attr.Name.LocalName == "xmlns" ? "" : attr.Name.LocalName;
                namespaceDeclarations[prefix] = attr.Value;
            }
            else if (attr.Name.Namespace == XsltNs)
            {
                // XSLT extension attributes
                switch (attr.Name.LocalName)
                {
                    case "use-attribute-sets":
                        foreach (var n in attr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            useAttributeSets.Add(ParseQName(n, element));
                        }
                        break;
                    case "inherit-namespaces":
                        // XSLT 3.0: accepts "yes", "no", "true", "false", "1", "0" with whitespace
                        inheritNamespaces = attr.Value.Trim() switch
                        {
                            "yes" or "true" or "1" => true,
                            "no" or "false" or "0" => false,
                            _ => null
                        };
                        break;
                    case "exclude-result-prefixes":
                        foreach (var p in attr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (p == "#all")
                            {
                                excludeResultPrefixes.Add(p);
                            }
                            else if (p == "#default")
                            {
                                // XTSE0809: #default requires a default namespace binding
                                if (string.IsNullOrEmpty(element.GetDefaultNamespace().NamespaceName))
                                    throw new XsltException("XTSE0809: The value '#default' is used in exclude-result-prefixes but the element has no default namespace",
                                        GetSourceLocation(element));
                                excludeResultPrefixes.Add(p);
                            }
                            else
                            {
                                // XTSE0808: prefix must have an in-scope namespace binding
                                var ns = element.GetNamespaceOfPrefix(p);
                                if (ns == null)
                                    throw new XsltException($"XTSE0808: Namespace prefix '{p}' used in exclude-result-prefixes is not declared",
                                        GetSourceLocation(element));
                                excludeResultPrefixes.Add(p);
                            }
                        }
                        break;
                    case "extension-element-prefixes":
                        foreach (var p in attr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            // XTSE1430: validate prefix is bound
                            if (p != "#default")
                            {
                                var extNs = element.GetNamespaceOfPrefix(p);
                                if (extNs == null)
                                    throw new XsltException($"XTSE1430: Namespace prefix '{p}' used in extension-element-prefixes is not declared",
                                        GetSourceLocation(element));
                                // XTSE0800: A reserved namespace must not be used as an extension namespace
                                if (extNs.NamespaceName is "http://www.w3.org/1999/XSL/Transform"
                                    or "http://www.w3.org/2001/XMLSchema"
                                    or "http://www.w3.org/2001/XMLSchema-instance"
                                    or "http://www.w3.org/XML/1998/namespace")
                                    throw new XsltException($"XTSE0800: The namespace '{extNs.NamespaceName}' is a reserved namespace and must not be used as an extension element namespace",
                                        GetSourceLocation(element));
                            }
                            excludeResultPrefixes.Add(p);
                        }
                        break;
                    case "version":
                        // xsl:version sets the effective XSLT version for this subtree
                        version = attr.Value.Trim();
                        break;
                    // Other valid XSLT attributes on LREs per spec section 11.1
                    case "default-collation":
                        defaultCollation = ResolveDefaultCollation(attr.Value.Trim());
                        break;
                    case "default-mode":
                    case "expand-text":
                    case "use-when":
                    case "xpath-default-namespace":
                        break;
                    case "type":
                        // XTSE1660: Non-schema-aware processor must reject xsl:type
                        if (ShouldRejectSchemaAware)
                            throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the xsl:type attribute",
                                GetSourceLocation(element));
                        break;
                    case "validation":
                        // XTSE1660: Non-schema-aware processor can only accept strip/preserve/lax
                        if (attr.Value.Trim() is "strict" && ShouldRejectSchemaAware)
                            throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept xsl:validation=\"{attr.Value.Trim()}\"",
                                GetSourceLocation(element));
                        break;
                    case "default-validation":
                        // XTSE1660: Non-schema-aware processor can only accept strip/preserve/lax
                        if (attr.Value.Trim() is "strict" && ShouldRejectSchemaAware)
                            throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept default-validation=\"{attr.Value.Trim()}\"",
                                GetSourceLocation(element));
                        break;
                    default:
                        // Forwards compatibility: ignore unknown XSLT attributes when
                        // effective version > 3.0 (per XSLT spec section 3.8)
                        var lreEffectiveVersion = version ?? GetEffectiveVersion(element);
                        if (ParseVersionNumber(lreEffectiveVersion) > 3.0m)
                            break;
                        throw new XsltException(
                            $"XTSE0805: Attribute '{attr.Name.LocalName}' in the XSLT namespace is not permitted on a literal result element",
                            GetSourceLocation(element));
                }
            }
            else
            {
                // Resolve the attribute's namespace URI to a NamespaceId (thread-safe intern)
                var attrNsName = attr.Name.NamespaceName;
                var attrNsId = ResolveNamespaceUri(attrNsName);
                // Use prefix map from XmlReader when available (preserves original prefix
                // even when multiple prefixes share the same namespace URI)
                string? attrPrefix = null;
                if (_elementPrefixMap != null && attr is IXmlLineInfo attrLi && attrLi.HasLineInfo())
                    _elementPrefixMap.TryGetValue((attrLi.LineNumber, attrLi.LinePosition), out attrPrefix);
                attrPrefix ??= element.GetPrefixOfNamespace(attr.Name.Namespace);
                var attrName = new QName(
                    attrNsId,
                    attr.Name.LocalName,
                    attrPrefix
                );
                attributes[attrName] = ParseAvt(attr.Value, element, attr);
            }
        }

        // Inherit namespace declarations and exclude-result-prefixes from ancestor elements
        for (var ancestor = element.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            foreach (var nsAttr in ancestor.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                var prefix = nsAttr.Name.LocalName == "xmlns" ? "" : nsAttr.Name.LocalName;
                namespaceDeclarations.TryAdd(prefix, nsAttr.Value);
            }

            // Inherit exclude-result-prefixes from ancestor XSLT elements
            // §7.1.2: #all excludes only namespaces in scope on the declaring element,
            // so expand to actual prefixes rather than passing through literally.
            var erpAttr = ancestor.Attribute("exclude-result-prefixes")
                          ?? ancestor.Attribute(XsltNs + "exclude-result-prefixes");
            if (erpAttr != null)
            {
                foreach (var p in erpAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (p == "#all")
                    {
                        foreach (var ns in ancestor.Attributes().Where(a => a.IsNamespaceDeclaration))
                        {
                            var np = ns.Name.LocalName == "xmlns" ? "" : ns.Name.LocalName;
                            var nu = ns.Value;
                            if (nu == "http://www.w3.org/1999/XSL/Transform"
                                || nu == "http://www.w3.org/XML/1998/namespace")
                                continue;
                            excludeResultPrefixes.Add(string.IsNullOrEmpty(np) ? "#default" : np);
                        }
                    }
                    else
                    {
                        excludeResultPrefixes.Add(p);
                    }
                }
            }
        }

        // Resolve xml:base on LRE for static-base-uri() of descendant expressions
        var xmlBase = element.Attribute(XNamespace.Xml + "base");
        string? staticBaseUri = null;
        if (xmlBase != null)
            staticBaseUri = ResolveEffectiveBaseUri(element)?.ToString();

        return new XsltLiteralResultElement
        {
            Location = location,
            Name = name,
            Attributes = attributes,
            NamespaceDeclarations = namespaceDeclarations,
            UseAttributeSets = useAttributeSets,
            InheritNamespaces = inheritNamespaces,
            ExcludeResultPrefixes = excludeResultPrefixes,
            Version = version,
            DefaultCollation = defaultCollation,
            StaticBaseUri = staticBaseUri,
            Content = ParseSequenceConstructor(element)
        };
    }

    private XsltSort ParseSort(XElement element)
    {
        var prevContext = _nsContext;
        _nsContext = element;
        var selectAttr = element.Attribute("select");
        var langAttr = element.Attribute("lang");
        var orderAttr = element.Attribute("order");
        var collationAttr = element.Attribute("collation");
        var stableAttr = element.Attribute("stable");
        var caseOrderAttr = element.Attribute("case-order");
        var dataTypeAttr = element.Attribute("data-type");

        // XTSE0020: lang must be a valid language tag when statically known
        if (langAttr != null && !langAttr.Value.Contains('{', StringComparison.Ordinal))
        {
            var langVal = langAttr.Value.Trim();
            if (langVal.Length > 0 && (!char.IsLetter(langVal[0]) || langVal.Any(c => !char.IsLetterOrDigit(c) && c != '-')))
                throw new XsltException($"XTSE0020: Invalid language tag '{langVal}' in xsl:sort lang attribute",
                    GetSourceLocation(element));
        }

        // XTSE1017: stable attribute only allowed on the first xsl:sort in a sibling sequence
        if (stableAttr != null && element.Parent != null)
        {
            var firstSort = element.Parent.Elements(XsltNs + "sort").FirstOrDefault();
            if (firstSort != null && firstSort != element)
                throw new XsltException("XTSE1017: The stable attribute may only appear on the first xsl:sort in a sequence of sibling sort elements",
                    GetSourceLocation(element));
        }

        // XTSE0020: stable must be a valid boolean value (or an AVT)
        if (stableAttr != null && !stableAttr.Value.Contains('{', StringComparison.Ordinal))
        {
            var stableVal = stableAttr.Value.Trim();
            if (stableVal != "yes" && stableVal != "no" && stableVal != "true" && stableVal != "false"
                && stableVal != "1" && stableVal != "0")
                throw new XsltException($"XTSE0020: Invalid value '{stableAttr.Value}' for stable attribute: must be 'yes', 'no', 'true', 'false', '1', or '0'",
                    GetSourceLocation(element));
        }

        // XTSE1015: select and non-empty content are mutually exclusive on xsl:sort
        ValidateSelectContentExclusive(selectAttr, element, "XTSE1015", "xsl:sort", GetSourceLocation(element));

        _nsContext = prevContext;
        return new XsltSort
        {
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.HasElements ? ParseSequenceConstructor(element) : null,
            Lang = langAttr != null ? ParseAvt(langAttr.Value, element, langAttr) : null,
            Order = orderAttr != null ? ParseAvt(orderAttr.Value, element, orderAttr) : null,
            Collation = collationAttr != null ? ParseAvt(collationAttr.Value, element, collationAttr) : null,
            Stable = stableAttr != null ? ParseAvt(stableAttr.Value, element, stableAttr) : null,
            CaseOrder = caseOrderAttr != null ? ParseAvt(caseOrderAttr.Value, element, caseOrderAttr) : null,
            DataType = dataTypeAttr != null ? ParseAvt(dataTypeAttr.Value, element, dataTypeAttr) : null
        };
    }

    private XsltWithParam ParseWithParam(XElement element)
    {
        var name = ParseQName(element.Attribute("name")!.Value, element);
        var asAttr = element.Attribute("as");
        var selectAttr = element.Attribute("select");
        var tunnelAttr = element.Attribute("tunnel");

        // XTSE0090: required attribute is not permitted on xsl:with-param
        if (element.Attribute("required") != null)
            throw new XsltException("XTSE0090: The required attribute is not permitted on xsl:with-param",
                GetSourceLocation(element));

        // XTSE0620: select attribute and non-empty content are mutually exclusive
        if (selectAttr != null && element.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0620: An xsl:with-param element must not have both a select attribute and non-empty content",
                GetSourceLocation(element));

        // Use the with-param element as namespace context for select expressions,
        // since it may have local namespace declarations (e.g., xmlns:S="...")
        var prevContext = _nsContext;
        _nsContext = element;
        try
        {
            return new XsltWithParam
            {
                Name = name,
                As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
                Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
                Content = selectAttr == null && element.Nodes().Any()
                    ? ParseSequenceConstructor(element)
                    : null,
                Tunnel = ParseYesNoBoolean(tunnelAttr, "tunnel", GetSourceLocation(element))
            };
        }
        finally
        {
            _nsContext = prevContext;
        }
    }

    /// <summary>
    /// Validates that xsl:break or xsl:next-iteration is lexically within an xsl:iterate body.
    /// Throws XTSE0010 if not inside iterate, XTSE3120 if inside a forbidden construct.
    /// </summary>
    private static void ValidateIterateChildLocation(XElement element, string instrName, SourceLocation? location)
    {
        var current = element.Parent;
        while (current != null)
        {
            if (current.Name == XsltNs + "iterate")
                return; // Found enclosing xsl:iterate — valid

            if (current.Name == XsltNs + "template" || current.Name == XsltNs + "function")
                throw new XsltException($"XTSE0010: {instrName} must be lexically within xsl:iterate", location);

            // Check for forbidden enclosing constructs
            if (current.Name.Namespace != XsltNs || // LRE
                current.Name.LocalName is "for-each" or "for-each-group" or "analyze-string")
                throw new XsltException($"XTSE3120: {instrName} must not appear within {current.Name.LocalName}", location);

            current = current.Parent;
        }
        throw new XsltException($"XTSE0010: {instrName} must be lexically within xsl:iterate", location);
    }

    /// <summary>
    /// Validates that an instruction (xsl:break/xsl:next-iteration) is in tail position
    /// within the xsl:iterate body. Tail position is recursive: the instruction must be
    /// last in its sequence constructor, AND if inside xsl:if/xsl:choose/xsl:try, those
    /// containers must themselves be in tail position.
    /// </summary>
    private static void ValidateLastInSequence(XElement element, string instrName, SourceLocation? location)
    {
        // Structural XSLT elements that are not sequence instructions
        var structuralElements = new HashSet<string>
        {
            "fallback", "catch", "param", "sort", "on-completion",
            "when", "otherwise", "matching-substring", "non-matching-substring"
        };

        // Containers that allow tail-position propagation (the instruction is in tail position
        // if it's last in the container AND the container is itself in tail position)
        var tailPositionContainers = new HashSet<string>
        {
            "if", "choose", "try", "when", "otherwise", "catch"
        };

        var current = element;
        while (current != null)
        {
            var foundSelf = false;
            foreach (var sibling in current.Parent!.Nodes())
            {
                if (sibling == current)
                {
                    foundSelf = true;
                    continue;
                }
                if (!foundSelf) continue;

                // After self, check for any non-structural, non-whitespace content
                if (sibling is XElement sibElem)
                {
                    if (sibElem.Name.Namespace == XsltNs && structuralElements.Contains(sibElem.Name.LocalName))
                        continue;
                    throw new XsltException($"XTSE3120: {instrName} must be in tail position within xsl:iterate body", location);
                }
                if (sibling is XText text && !string.IsNullOrWhiteSpace(text.Value))
                {
                    throw new XsltException($"XTSE3120: {instrName} must be in tail position within xsl:iterate body", location);
                }
            }

            // If the parent is xsl:iterate, we've reached the iterate body — tail position confirmed
            if (current.Parent!.Name == XsltNs + "iterate")
                return;

            // If the parent is a tail-position container (if/choose/try/when/otherwise/catch),
            // we need to check that the container itself is in tail position
            if (current.Parent!.Name.Namespace == XsltNs
                && tailPositionContainers.Contains(current.Parent!.Name.LocalName))
            {
                // For when/otherwise/catch, we check the grandparent (choose/try) for tail position
                current = current.Parent!.Name.LocalName is "when" or "otherwise" or "catch"
                    ? current.Parent!.Parent!  // check the xsl:choose or xsl:try itself
                    : current.Parent!;         // check the xsl:if or xsl:try itself
                continue;
            }

            // Parent is not a tail-position container — stop checking
            return;
        }
    }

    /// <summary>
    /// Determines if expand-text is active for a given element by walking up
    /// the ancestor chain. The nearest expand-text attribute wins, falling back
    /// to the stylesheet-level default.
    /// </summary>
    private bool IsExpandTextActive(XElement element)
    {
        // Walk ancestors from element up to stylesheet, nearest wins
        var current = element;
        while (current != null)
        {
            // Check xsl:expand-text (on LREs) or expand-text (on XSLT instructions).
            // Unprefixed expand-text is only an XSLT attribute on elements in the XSLT namespace;
            // on non-XSLT elements (LREs), it's just a regular attribute and should be ignored.
            var attr = current.Attribute(XsltNs + "expand-text")
                       ?? (current.Name.Namespace == XsltNs ? current.Attribute("expand-text") : null);
            if (attr != null)
            {
                var v = attr.Value.Trim();
                return v switch
                {
                    "yes" or "1" or "true" => true,
                    "no" or "0" or "false" => false,
                    _ => throw new XsltException(
                        $"XTSE0020: Invalid value '{attr.Value}' for expand-text attribute; must be yes|no|true|false|1|0",
                        GetSourceLocation(current))
                };
            }
            current = current.Parent;
        }
        return _defaultExpandText;
    }

    /// <summary>
    /// Returns true if the string is null, empty, or contains only XML whitespace
    /// characters (#x20, #x9, #xD, #xA). Unlike string.IsNullOrWhiteSpace(), this
    /// does NOT treat U+00A0 (non-breaking space) or other Unicode whitespace as whitespace.
    /// </summary>
    private static bool IsXmlWhitespaceOnly(string? value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        foreach (var c in value)
        {
            if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                return false;
        }
        return true;
    }

    private static string GetEffectiveVersion(XElement element)
    {
        // Walk up the ancestor chain looking for a version attribute
        for (var el = element.Parent; el != null; el = el.Parent)
        {
            // On an XSLT element, check the "version" attribute directly
            if (el.Name.Namespace == XsltNs)
            {
                var v = el.Attribute("version")?.Value;
                if (v != null) return v;
            }
            else
            {
                // On a non-XSLT (LRE) element, check xsl:version
                var v = el.Attribute(XsltNs + "version")?.Value;
                if (v != null) return v;
            }
        }
        return "3.0";
    }

    /// <summary>
    /// Returns true if the effective version of the element is less than 2.0
    /// (i.e., XSLT 1.0 backwards-compatible mode).
    /// </summary>
    private static bool IsBackwardsCompatible(XElement element)
    {
        var version = element.Attribute("version")?.Value ?? GetEffectiveVersion(element);
        return ParseVersionNumber(version) < 2.0m;
    }

    private static decimal ParseVersionNumber(string? version)
    {
        if (version != null && decimal.TryParse(version.Trim(),
            System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint
            | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite,
            System.Globalization.CultureInfo.InvariantCulture, out var result))
            return result;
        return 3.0m; // Non-numeric version strings default to 3.0
    }

    private static bool IsXmlSpacePreserve(XElement element)
    {
        var current = element;
        while (current != null)
        {
            var attr = current.Attribute(XNamespace.Xml + "space");
            if (attr != null)
                return attr.Value == "preserve";
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// Creates either a plain XsltLiteralText or an XsltTextValueTemplate depending
    /// on whether expand-text is active and the text contains curly braces.
    /// </summary>
    private XsltInstruction CreateTextInstruction(string value, bool expandText, XElement context)
    {
        if (expandText && value.Contains('{', StringComparison.Ordinal))
        {
            var avt = ParseAvtFromText(value, context);
            // If the AVT is just a single literal (no expressions), use the literal's
            // decoded value (with empty TVT expressions like {} stripped)
            if (avt.Parts.Count == 1 && avt.Parts[0] is AvtLiteral lit)
            {
                return new XsltLiteralText { Value = lit.Value };
            }
            if (avt.Parts.Count == 0)
            {
                // All expressions were empty — produce empty text (vacuous)
                return new XsltLiteralText { Value = "" };
            }
            return new XsltTextValueTemplate { Template = avt };
        }
        return new XsltLiteralText { Value = value };
    }

    private XsltAttributeValueTemplate ParseAvt(string value, XElement context, System.Xml.Linq.XAttribute? sourceAttribute = null)
    {
        // D2 source-location-audit: when the AVT comes from a real attribute, compute
        // the file-absolute position of every inner {…} expression's first character
        // and pass it through ParseExprAt so runtime errors pin to the offending
        // token, not to the start of the attribute. Multi-line AVTs are handled by
        // counting newlines in the prefix.
        var (avtBaseLine, avtBaseCol, moduleUri) = ComputeAvtBasePosition(sourceAttribute);
        return ParseAvtCore(value, context, avtBaseLine, avtBaseCol, moduleUri);
    }

    /// <summary>
    /// D3: parse an AVT-style template that comes from element text content rather
    /// than an attribute value (e.g. expand-text=yes TVT). Uses the first descendant
    /// <see cref="XText"/>'s IXmlLineInfo to seed the base position so inner-expression
    /// errors carry the right file (line, col).
    /// </summary>
    private XsltAttributeValueTemplate ParseAvtFromText(string value, XElement context)
    {
        var (line, col, module) = ComputeTextBasePosition(context);
        return ParseAvtCore(value, context, line, col, module);
    }

    private XsltAttributeValueTemplate ParseAvtCore(string value, XElement context, int avtBaseLine, int avtBaseCol, string? moduleUri)
    {
        var parts = new List<AvtPart>();
        var current = 0;

        while (current < value.Length)
        {
            var openBrace = value.IndexOf('{', current);

            if (openBrace < 0)
            {
                // No more expressions, rest is literal
                if (current < value.Length)
                {
                    CheckUnescapedRightBrace(value[current..], context);
                    parts.Add(new AvtLiteral { Value = UnescapeAvt(value[current..]) });
                }
                break;
            }

            // Check for escaped brace
            if (openBrace + 1 < value.Length && value[openBrace + 1] == '{')
            {
                CheckUnescapedRightBrace(value[current..(openBrace + 1)], context);
                parts.Add(new AvtLiteral { Value = UnescapeAvt(value[current..(openBrace + 1)]) });
                current = openBrace + 2;
                continue;
            }

            // Add literal before the brace
            if (openBrace > current)
            {
                CheckUnescapedRightBrace(value[current..openBrace], context);
                parts.Add(new AvtLiteral { Value = UnescapeAvt(value[current..openBrace]) });
            }

            // Find closing brace
            var closeBrace = FindMatchingBrace(value, openBrace);
            if (closeBrace < 0)
            {
                throw new XsltException("Unmatched '{' in attribute value template", GetSourceLocation(context));
            }

            var expr = value[(openBrace + 1)..closeBrace];
            // Empty expressions {} or comment-only expressions {(: ... :)}
            // produce empty text per XSLT 3.0 spec
            var exprTrimmed = StripXPathComments(expr).Trim();
            if (exprTrimmed.Length == 0)
            {
                // Empty expression — produces empty string (vacuous text node)
                // Don't add anything; this effectively disappears
            }
            else
            {
                XQueryExpression innerExpr;
                if (avtBaseLine > 0)
                {
                    // Compute absolute (line, col) of the character just past the '{'.
                    var (lineOffset, colOnFinalLine) = OffsetToLineColumn(value, openBrace + 1);
                    var innerLine = avtBaseLine + lineOffset;
                    var innerCol = lineOffset == 0 ? avtBaseCol + colOnFinalLine : colOnFinalLine + 1;
                    innerExpr = ParseExprAt(expr, innerLine, innerCol, moduleUri);
                }
                else
                {
                    innerExpr = ParseExpr(expr);
                }
                parts.Add(new AvtExpression { Expression = innerExpr });
            }

            current = closeBrace + 1;
        }

        if (parts.Count == 0)
        {
            parts.Add(new AvtLiteral { Value = "" });
        }

        return new XsltAttributeValueTemplate { Parts = parts };
    }

    /// <summary>
    /// Computes the file-absolute (line, column) of the first character of the AVT's
    /// value text plus the module URI, from the source attribute. Returns (0, 0, null)
    /// when no attribute or no line info is available — ParseAvt then falls back to
    /// the legacy locationless ParseExpr path.
    /// </summary>
    private static (int Line, int Column, string? ModuleUri) ComputeAvtBasePosition(System.Xml.Linq.XAttribute? attribute)
    {
        if (attribute is not System.Xml.IXmlLineInfo li || !li.HasLineInfo()) return (0, 0, null);
        var attrName = attribute.Name.LocalName;
        if (!string.IsNullOrEmpty(attribute.Name.NamespaceName)
            && attribute.Parent?.GetPrefixOfNamespace(attribute.Name.Namespace) is { Length: > 0 } prefix)
            attrName = prefix + ":" + attrName;
        var valueStartCol = li.LinePosition + attrName.Length + 2;
        return (li.LineNumber, valueStartCol, attribute.Parent?.BaseUri);
    }

    /// <summary>
    /// D3 helper: compute the file-absolute base position for an AVT/TVT that comes
    /// from element text content. Uses the first descendant <see cref="XText"/>'s
    /// IXmlLineInfo. When the element has multiple text-node children (e.g.
    /// interrupted by xsl:value-of), only the first text node's position is used —
    /// inner-expression positions for later text nodes will be off but at least
    /// land in the same file/line range; full multi-text accuracy is a follow-up.
    /// </summary>
    private static (int Line, int Column, string? ModuleUri) ComputeTextBasePosition(XElement element)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText t && t is System.Xml.IXmlLineInfo tli && tli.HasLineInfo())
                return (tli.LineNumber, tli.LinePosition, element.BaseUri);
        }
        // Fallback to element's own start position
        if (element is System.Xml.IXmlLineInfo eli && eli.HasLineInfo())
            return (eli.LineNumber, eli.LinePosition, element.BaseUri);
        return (0, 0, element.BaseUri);
    }

    /// <summary>
    /// Counts newlines in <paramref name="text"/> from index 0 to (exclusive) <paramref name="offset"/>
    /// and returns <c>(linesAfterStart, columnOnLastLine)</c>. Used to map an offset
    /// inside an attribute value to a (line, col) within the value.
    /// </summary>
    private static (int Lines, int ColumnOnLastLine) OffsetToLineColumn(string text, int offset)
    {
        var lines = 0;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\n') { lines++; lineStart = i + 1; }
        }
        return (lines, offset - lineStart);
    }

    private static int FindMatchingBrace(string value, int openBrace)
    {
        var depth = 1;
        var inString = false;
        var stringChar = '\0';
        var commentDepth = 0;

        for (var i = openBrace + 1; i < value.Length; i++)
        {
            var c = value[i];

            if (inString)
            {
                if (c == stringChar)
                    inString = false;
                continue;
            }

            // Handle XPath comments (: ... :) which can nest
            if (commentDepth > 0)
            {
                if (c == '(' && i + 1 < value.Length && value[i + 1] == ':')
                {
                    commentDepth++;
                    i++; // skip ':'
                }
                else if (c == ':' && i + 1 < value.Length && value[i + 1] == ')')
                {
                    commentDepth--;
                    i++; // skip ')'
                }
                continue;
            }

            // Check for comment start
            if (c == '(' && i + 1 < value.Length && value[i + 1] == ':')
            {
                commentDepth++;
                i++; // skip ':'
                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                    inString = true;
                    stringChar = c;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return i;
                    break;
            }
        }

        return -1;
    }

    /// <summary>
    /// XTSE0370: Check for unescaped right curly bracket in fixed part of AVT/TVT.
    /// </summary>
    private static void CheckUnescapedRightBrace(string literal, XElement context)
    {
        for (var i = 0; i < literal.Length; i++)
        {
            if (literal[i] == '}')
            {
                if (i + 1 < literal.Length && literal[i + 1] == '}')
                {
                    i++; // skip escaped }}
                }
                else
                {
                    throw new XsltException("XTSE0370: An unescaped right curly bracket '}' in a fixed part of an attribute value template is not allowed",
                        GetSourceLocation(context));
                }
            }
        }
    }

    /// <summary>
    /// XTSE0125: Validates that a default-collation list contains at least one recognized collation URI.
    /// </summary>
    private static void ValidateCollationList(string value, SourceLocation? location, string errorCode = "XTSE0125")
    {
        var uris = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var uri in uris)
        {
            if (string.Equals(uri, "http://www.w3.org/2005/xpath-functions/collation/codepoint", StringComparison.Ordinal)
                || string.Equals(uri, "http://www.w3.org/2005/xpath-functions/collation/html-ascii-case-insensitive", StringComparison.Ordinal)
                || string.Equals(uri, "http://www.w3.org/2013/collation/UCA", StringComparison.Ordinal)
                || uri.StartsWith("http://www.w3.org/2013/collation/UCA?", StringComparison.Ordinal))
                return; // At least one recognized collation found
        }
        throw new XsltException($"{errorCode}: No recognized collation URI: '{value}'", location);
    }

    /// <summary>
    /// Resolves a default-collation attribute value to the first recognized collation URI.
    /// Returns null if no recognized collation or value is null.
    /// </summary>
    private static string? ResolveDefaultCollation(string? value)
    {
        if (value == null) return null;
        var uris = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var uri in uris)
        {
            if (string.Equals(uri, "http://www.w3.org/2005/xpath-functions/collation/codepoint", StringComparison.Ordinal)
                || string.Equals(uri, "http://www.w3.org/2005/xpath-functions/collation/html-ascii-case-insensitive", StringComparison.Ordinal)
                || string.Equals(uri, "http://www.w3.org/2013/collation/UCA", StringComparison.Ordinal)
                || uri.StartsWith("http://www.w3.org/2013/collation/UCA?", StringComparison.Ordinal))
                return uri;
        }
        return null;
    }

    private static string UnescapeAvt(string value)
    {
        return value.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Strips XPath comments (: ... :) from an expression, handling nesting.
    /// Used to check if an AVT expression is effectively empty after comment removal.
    /// </summary>
    private static string StripXPathComments(string expr)
    {
        var sb = new System.Text.StringBuilder(expr.Length);
        var commentDepth = 0;
        for (var i = 0; i < expr.Length; i++)
        {
            var c = expr[i];
            if (commentDepth > 0)
            {
                if (c == '(' && i + 1 < expr.Length && expr[i + 1] == ':')
                {
                    commentDepth++;
                    i++;
                }
                else if (c == ':' && i + 1 < expr.Length && expr[i + 1] == ')')
                {
                    commentDepth--;
                    i++;
                }
            }
            else if (c == '(' && i + 1 < expr.Length && expr[i + 1] == ':')
            {
                commentDepth++;
                i++;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Checks if a pattern has non-motionless predicates (predicates on element/document
    /// nodes that may require traversing children to evaluate). Predicates on leaf nodes
    /// (text, attribute, comment, PI) are considered motionless.
    /// </summary>
    private static bool HasNonMotionlessPredicates(XsltPattern pattern) => pattern switch
    {
        PathPattern pp => pp.Steps.Any(s => s.Predicates.Count > 0 && !IsLeafNodeTest(s.NodeTest)),
        UnionPattern up => up.Patterns.Any(HasNonMotionlessPredicates),
        DotPattern dp => dp.Predicates.Count > 0,
        _ => false
    };

    private static bool IsLeafNodeTest(PhoenixmlDb.XQuery.Ast.NodeTest test) => test switch
    {
        PhoenixmlDb.XQuery.Ast.KindTest kt => kt.Kind is XdmNodeKind.Text or XdmNodeKind.Attribute
            or XdmNodeKind.Comment or XdmNodeKind.ProcessingInstruction,
        _ => false // NameTest matches elements by default — not a leaf node
    };

    private XsltPattern ParsePattern(string pattern, XElement context)
    {
        // Strip XPath comments (:...:) before parsing. Comments can nest.
        pattern = StripXPathComments(pattern);

        // XTSE1060: current-group() is not allowed in patterns
        if (System.Text.RegularExpressions.Regex.IsMatch(pattern, @"\bcurrent-group\s*\("))
            throw new XsltException("XTSE1060: current-group() is not allowed in a pattern", GetSourceLocation(context));
        // XTSE1070: current-grouping-key() is not allowed in patterns
        if (System.Text.RegularExpressions.Regex.IsMatch(pattern, @"\bcurrent-grouping-key\s*\("))
            throw new XsltException("XTSE1070: current-grouping-key() is not allowed in a pattern", GetSourceLocation(context));
        // XTSE3470: current-merge-group() is not allowed in patterns
        if (System.Text.RegularExpressions.Regex.IsMatch(pattern, @"\bcurrent-merge-group\s*\("))
            throw new XsltException("XTSE3470: current-merge-group() is not allowed in a pattern", GetSourceLocation(context));
        // XTSE3500: current-merge-key() is not allowed in patterns
        if (System.Text.RegularExpressions.Regex.IsMatch(pattern, @"\bcurrent-merge-key\s*\("))
            throw new XsltException("XTSE3500: current-merge-key() is not allowed in a pattern", GetSourceLocation(context));

        // Split union patterns at '|' outside of brackets
        var unionParts = SplitUnionPattern(pattern);
        if (unionParts.Count > 1)
        {
            var parsedParts = unionParts.Select(p => ParsePattern(p.Trim(), context)).ToList();
            // XTSE0340: Union operator '|' is not allowed with predicate patterns (.[pred])
            // because '|' only applies to node patterns, not atomic value patterns
            if (parsedParts.Any(p => p is DotPattern))
                throw new XsltException("XTSE0340: Union operator '|' is not allowed with predicate patterns", GetSourceLocation(context));
            return new UnionPattern
            {
                Patterns = parsedParts
            };
        }

        // XSLT 3.0: Split on 'except'/'intersect' keywords at the top level
        // Per grammar: IntersectExceptPattern ::= PathPattern (('intersect' | 'except') PathPattern)*
        {
            var exceptIntersectParts = SplitExceptIntersect(pattern);
            if (exceptIntersectParts != null)
            {
                // Build left-associative chain: A except B except C → (A except B) except C
                var result = ParsePattern(exceptIntersectParts[0].Part.Trim(), context);
                for (var idx = 1; idx < exceptIntersectParts.Count; idx++)
                {
                    var right = ParsePattern(exceptIntersectParts[idx].Part.Trim(), context);
                    result = exceptIntersectParts[idx].IsExcept
                        ? new ExceptPattern { Left = result, Right = right }
                        : new IntersectPattern { Left = result, Right = right };
                }
                return result;
            }
        }

        // Handle "/" (document root) pattern, optionally with predicates in parenthesized form: "/", "(/)[pred]"
        // Note: "/[pred]" without parens is a syntax error per bug 18861 (XTSE0340)
        {
            var trimmedRoot = pattern.Trim();
            var wasParenthesized = false;
            // Unwrap parenthesized form: (/)[pred] → /[pred]
            if (trimmedRoot.StartsWith("(/)", StringComparison.Ordinal))
            {
                trimmedRoot = "/" + trimmedRoot[3..].TrimStart();
                wasParenthesized = true;
            }
            if (trimmedRoot == "/" || (wasParenthesized && trimmedRoot.Length > 1 && trimmedRoot[0] == '/'
                && trimmedRoot.AsSpan(1).TrimStart().StartsWith("[")))
            {
                var predicates = new List<XQueryExpression>();
                var predPart = trimmedRoot[1..].Trim();
                while (predPart.StartsWith('['))
                {
                    var bracketDepth = 0;
                    var k = 0;
                    for (; k < predPart.Length; k++)
                    {
                        if (predPart[k] == '[') bracketDepth++;
                        else if (predPart[k] == ']') { bracketDepth--; if (bracketDepth == 0) break; }
                        else if (predPart[k] == '\'' || predPart[k] == '"')
                        {
                            var q = predPart[k];
                            k++;
                            while (k < predPart.Length && predPart[k] != q) k++;
                        }
                    }
                    if (k < predPart.Length)
                    {
                        var predExpr = predPart[1..k];
                        try { predicates.Add(ParseExpression(predExpr, context)); }
#pragma warning disable CA1031 // Intentional broad catch — see comment below
                        catch (Exception)
                        {
                            // Predicate parsing may fail during pattern optimization (e.g. forward
                            // references, complex expressions). The predicate is still evaluated at
                            // runtime via the full XPath evaluator; skipping it here only means this
                            // optimization path won't pre-filter using it.
                        }
#pragma warning restore CA1031
                        predPart = predPart[(k + 1)..].Trim();
                    }
                    else break;
                }
                return new PathPattern
                {
                    Steps = [new PatternStep
                    {
                        Axis = Axis.Self,
                        NodeTest = new KindTest { Kind = XdmNodeKind.Document },
                        Predicates = predicates
                    }]
                };
            }

            // "/[pred]" without parentheses is a syntax error per bug 18861
            if (!wasParenthesized && trimmedRoot.Length > 1 && trimmedRoot[0] == '/'
                && trimmedRoot.AsSpan(1).TrimStart().StartsWith("["))
                throw new XsltException("XTSE0340: /[predicate] is not a valid pattern; use (/)[predicate] instead", GetSourceLocation(context));
        }

        // Handle "." and ".[pred1][pred2]..." patterns (XSLT 3.0: matches any item)
        // Allow whitespace between "." and "[" per XPath grammar
        {
            var trimmedPat = pattern.Trim();
            if (trimmedPat == "." || (trimmedPat.Length > 1 && trimmedPat[0] == '.'
                && trimmedPat.AsSpan(1).TrimStart().StartsWith("[")))
            {
                var predicates = new List<XQueryExpression>();
                // Extract all predicate expressions [pred1][pred2]...
                var predPart = trimmedPat[1..].Trim();
                while (predPart.StartsWith('['))
                {
                    // Find matching ']', respecting nesting
                    var bracketDepth = 0;
                    var k = 0;
                    for (; k < predPart.Length; k++)
                    {
                        if (predPart[k] == '[') bracketDepth++;
                        else if (predPart[k] == ']') { bracketDepth--; if (bracketDepth == 0) break; }
                        else if (predPart[k] == '\'' || predPart[k] == '"')
                        {
                            var q = predPart[k];
                            k++;
                            while (k < predPart.Length && predPart[k] != q) k++;
                        }
                    }
                    if (k < predPart.Length)
                    {
                        var predExpr = predPart[1..k];
                        try
                        {
                            predicates.Add(ParseExpression(predExpr, context));
                        }
#pragma warning disable CA1031 // Intentional broad catch — see comment below
                        catch (Exception)
                        {
                            // Predicate parsing may fail during pattern optimization (e.g. forward
                            // references, complex expressions). The predicate is still evaluated at
                            // runtime via the full XPath evaluator; skipping it here only means this
                            // optimization path won't pre-filter using it.
                        }
#pragma warning restore CA1031
                        predPart = predPart[(k + 1)..].Trim();
                    }
                    else break;
                }
                return new DotPattern { Predicates = predicates };
            }
        }

        // XTSE0340: Parenthesized predicate patterns like "(.[pred])" are not valid
        {
            var trimmedPat = pattern.Trim();
            if (trimmedPat.StartsWith('(') && trimmedPat.EndsWith(')'))
            {
                var inner = trimmedPat[1..^1].Trim();
                if (inner.Length > 0 && inner[0] == '.' && (inner.Length == 1 || inner.AsSpan(1).TrimStart().StartsWith("[")))
                    throw new XsltException("XTSE0340: Predicate pattern cannot be parenthesized", GetSourceLocation(context));
            }
        }

        // Expand parenthesized unions within path steps:
        // "x/(child::a|descendant::b)" → "x/child::a | x/descendant::b"
        {
            var expanded = ExpandParenthesizedUnions(pattern.Trim());
            if (expanded != null)
            {
                return new UnionPattern
                {
                    Patterns = expanded.Select(p => ParsePattern(p.Trim(), context)).ToList()
                };
            }
        }

        // Expand parenthesized except/intersect within path steps:
        // "x/(descendant::a except child::a)" → ExceptPattern(x/descendant::a, x/child::a)
        // Context-scoped matching ensures both sides are evaluated from the same ancestor.
        {
            var parenExcept = TryExpandParenthesizedExceptIntersect(pattern.Trim(), context);
            if (parenExcept != null)
                return parenExcept;
        }

        // key() function call patterns: key('name', value), key('name', value)//child, etc.
        {
            var keyPattern = TryParseKeyPattern(pattern.Trim(), context);
            if (keyPattern != null)
                return keyPattern;
        }

        // id() function call patterns: id(value), id(value)//child, etc.
        {
            var idPattern = TryParseIdPattern(pattern.Trim(), context);
            if (idPattern != null)
                return idPattern;
        }

        // XSLT 3.0: Variable reference patterns: $var, $var/path, $var//path
        {
            var varPattern = TryParseVariableReferencePattern(pattern.Trim(), context);
            if (varPattern != null)
                return varPattern;
        }

        // XSLT 3.0: doc() function patterns: doc('uri'), doc('uri')/path, doc('uri')//path
        {
            var docPattern = TryParseDocFunctionPattern(pattern.Trim(), context);
            if (docPattern != null)
                return docPattern;
        }

        // Simple path pattern — handle '//' by splitting carefully
        var steps = new List<PatternStep>();
        var trimmed = pattern.Trim();
        var startsWithRoot = false;

        // Split into segments, tracking whether '//' was used between them
        var segments = new List<(string name, bool descendantSeparator)>();
        var i = 0;

        // Detect leading '/' or '//'
        if (i < trimmed.Length && trimmed[i] == '/')
        {
            i++;
            if (i < trimmed.Length && trimmed[i] == '/')
            {
                // Leading '//' — descendant of root (match anywhere)
                i++;
                var seg = ReadSegment(trimmed, ref i);
                if (!string.IsNullOrWhiteSpace(seg))
                    segments.Add((seg, true));
            }
            else
            {
                // Leading '/' — direct child of document root
                startsWithRoot = true;
                var seg = ReadSegment(trimmed, ref i);
                if (!string.IsNullOrWhiteSpace(seg))
                    segments.Add((seg, false));
            }
        }
        else if (i < trimmed.Length)
        {
            var seg = ReadSegment(trimmed, ref i);
            if (!string.IsNullOrWhiteSpace(seg))
                segments.Add((seg, false));
        }

        // Parse remaining segments
        while (i < trimmed.Length)
        {
            if (trimmed[i] == '/')
            {
                i++;
                if (i < trimmed.Length && trimmed[i] == '/')
                {
                    // '//' separator
                    i++;
                    var seg = ReadSegment(trimmed, ref i);
                    if (!string.IsNullOrWhiteSpace(seg))
                        segments.Add((seg, true));
                }
                else
                {
                    // '/' separator
                    var seg = ReadSegment(trimmed, ref i);
                    if (!string.IsNullOrWhiteSpace(seg))
                        segments.Add((seg, false));
                }
            }
            else
            {
                break; // shouldn't happen
            }
        }

        // If pattern starts with '/', prepend a document-root step
        if (startsWithRoot)
        {
            steps.Add(new PatternStep
            {
                Axis = Axis.Self,
                NodeTest = new KindTest { Kind = XdmNodeKind.Document }
            });
        }

        foreach (var (part, descendantSep) in segments)
        {
            var axis = Axis.Child;
            var name = part;

            // Extract predicates (e.g., "item[@type='a']" -> name="item", predicates=["@type='a'"])
            var predicates = new List<XQueryExpression>();
            var predStart = name.IndexOf('[', StringComparison.Ordinal);
            if (predStart >= 0)
            {
                var predPart = name[predStart..];
                // Whitespace is permitted between a NodeTest and its predicate
                // (e.g. "letters [true()]"); trim it so the node test stays valid.
                name = name[..predStart].Trim();

                // Parse each predicate expression between matching brackets
                var pPos = 0;
                while (pPos < predPart.Length)
                {
                    if (predPart[pPos] == '[')
                    {
                        pPos++;
                        var depth = 1;
                        var exprStart = pPos;
                        while (pPos < predPart.Length && depth > 0)
                        {
                            if (predPart[pPos] == '[') depth++;
                            else if (predPart[pPos] == ']') depth--;
                            if (depth > 0) pPos++;
                        }
                        if (pPos > exprStart)
                        {
                            var exprText = predPart[exprStart..pPos].Trim();
                            if (!string.IsNullOrEmpty(exprText))
                            {
                                try
                                {
                                    predicates.Add(ParseExpression(exprText, context));
                                }
#pragma warning disable CA1031 // Predicate parsing failure should not crash pattern parsing
                                catch (Exception ex) when (ex is not XsltException && ex is not PhoenixmlDb.XQuery.Execution.XQueryRuntimeException)
                                {
                                    // If predicate parsing fails for non-static reasons, skip it
                                }
#pragma warning restore CA1031
                            }
                        }
                        if (pPos < predPart.Length) pPos++; // skip ']'
                    }
                    else
                    {
                        pPos++;
                    }
                }
            }

            var explicitAxis = false;
            if (name.StartsWith('@'))
            {
                axis = Axis.Attribute;
                explicitAxis = true;
                name = name[1..];
            }
            else if (name.StartsWith("attribute::", StringComparison.Ordinal))
            {
                axis = Axis.Attribute;
                explicitAxis = true;
                name = name["attribute::".Length..];
            }
            else if (name.StartsWith("child::", StringComparison.Ordinal))
            {
                axis = Axis.Child;
                explicitAxis = true;
                name = name["child::".Length..];
            }
            else if (name.StartsWith("self::", StringComparison.Ordinal))
            {
                axis = Axis.Self;
                explicitAxis = true;
                name = name["self::".Length..];
            }
            else if (name.StartsWith("descendant-or-self::", StringComparison.Ordinal))
            {
                axis = Axis.DescendantOrSelf;
                explicitAxis = true;
                name = name["descendant-or-self::".Length..];
            }
            else if (name.StartsWith("descendant::", StringComparison.Ordinal))
            {
                axis = Axis.Descendant;
                explicitAxis = true;
                name = name["descendant::".Length..];
            }
            else if (name.StartsWith("parent::", StringComparison.Ordinal))
            {
                axis = Axis.Parent;
                explicitAxis = true;
                name = name["parent::".Length..];
            }
            else if (name.StartsWith("namespace::", StringComparison.Ordinal))
            {
                axis = Axis.Namespace;
                explicitAxis = true;
                name = name["namespace::".Length..];
            }
            else if (name.StartsWith("..", StringComparison.Ordinal))
            {
                axis = Axis.Parent;
                explicitAxis = true;
                name = "*";
            }
            else if (name == ".")
            {
                axis = Axis.Self;
                explicitAxis = true;
                name = "*";
            }

            // XTSE0340/XPST0017: Reject function calls that aren't valid pattern functions
            // Valid pattern functions (key, id, doc, document, root, element-with-id) are handled earlier.
            // Any remaining name with '(' that isn't a recognized kind test or valid function is invalid.
            if (name.Contains('(', StringComparison.Ordinal) && name.EndsWith(')')
                && !name.StartsWith("node(", StringComparison.Ordinal)
                && !name.StartsWith("text(", StringComparison.Ordinal)
                && !name.StartsWith("comment(", StringComparison.Ordinal)
                && !name.StartsWith("processing-instruction(", StringComparison.Ordinal)
                && !name.StartsWith("document-node(", StringComparison.Ordinal)
                && !name.StartsWith("namespace-node(", StringComparison.Ordinal)
                && !name.StartsWith("element(", StringComparison.Ordinal)
                && !name.StartsWith("schema-element(", StringComparison.Ordinal)
                && !name.StartsWith("attribute(", StringComparison.Ordinal)
                && !name.StartsWith("schema-attribute(", StringComparison.Ordinal)
                && !name.StartsWith("root(", StringComparison.Ordinal))
            {
                throw new XsltException($"XTSE0340: '{name}' is not allowed in a pattern", GetSourceLocation(context));
            }

            var nodeTest = ParseNodeTest(name, context, isAttribute: axis == Axis.Attribute);

            // KindTest patterns like attribute() and namespace-node() imply their axis
            // only when no explicit axis was provided (e.g., bare "attribute()" → Axis.Attribute,
            // but "child::attribute()" keeps Axis.Child per XSLT spec §5.5.3)
            if (!explicitAxis)
            {
                if (nodeTest is KindTest { Kind: XdmNodeKind.Attribute })
                    axis = Axis.Attribute;
                else if (nodeTest is KindTest { Kind: XdmNodeKind.Namespace })
                    axis = Axis.Namespace;
            }

            steps.Add(new PatternStep
            {
                Axis = axis,
                NodeTest = nodeTest,
                DescendantSeparator = descendantSep,
                Predicates = predicates
            });
        }

        return new PathPattern { Steps = steps };
    }

    /// <summary>
    /// Attempts to parse a key() function call pattern.
    /// Returns a KeyPattern if the pattern starts with "key(", otherwise null.
    /// Handles: key('name', value), key('name', $var), key('name', value)//child, etc.
    /// </summary>
    private KeyPattern? TryParseKeyPattern(string pattern, XElement context)
    {
        if (!pattern.StartsWith("key(", StringComparison.Ordinal))
            return null;

        // Find the matching closing parenthesis for key(...)
        // Start at index 4 (after "key(") to scan inside the parentheses
        var depth = 0;
        var closeIdx = -1;
        for (var k = 4; k < pattern.Length; k++)
        {
            var c = pattern[k];
            if (c == '(') depth++;
            else if (c == ')')
            {
                if (depth == 0) { closeIdx = k; break; }
                depth--;
            }
            else if (c == '\'' || c == '"')
            {
                var q = c;
                k++;
                while (k < pattern.Length && pattern[k] != q) k++;
            }
        }

        if (closeIdx < 0)
            return null;

        // Extract the two arguments: key('name', value)
        var argsStr = pattern[4..closeIdx];
        var (keyName, valueExpr) = ParseKeyPatternArgs(argsStr, context);

        if (keyName == null)
            return null;

        // Check for continuation path: key(...)//p or key(...)/p
        var rest = pattern[(closeIdx + 1)..].TrimStart();
        if (rest.Length == 0)
        {
            return new KeyPattern
            {
                KeyName = keyName,
                ValueExpression = valueExpr!
            };
        }

        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            var continuationStr = rest[2..];
            return new KeyPattern
            {
                KeyName = keyName,
                ValueExpression = valueExpr!,
                Continuation = ParsePattern(continuationStr, context),
                DescendantSeparator = true
            };
        }

        if (rest.StartsWith('/'))
        {
            var continuationStr = rest[1..];
            return new KeyPattern
            {
                KeyName = keyName,
                ValueExpression = valueExpr!,
                Continuation = ParsePattern(continuationStr, context),
                DescendantSeparator = false
            };
        }

        // Unrecognized continuation — not a key pattern
        return null;
    }

    /// <summary>
    /// Parses the arguments of a key() function call in a pattern.
    /// Returns (keyName, valueExpression).
    /// </summary>
    private (string? keyName, XQueryExpression? valueExpr) ParseKeyPatternArgs(string argsStr, XElement context)
    {
        // Split on comma, respecting string literals
        var commaIdx = -1;
        var inQuote = false;
        var quoteChar = '\0';
        var parenDepth = 0;
        for (var k = 0; k < argsStr.Length; k++)
        {
            var c = argsStr[k];
            if (inQuote)
            {
                if (c == quoteChar) inQuote = false;
            }
            else if (c == '\'' || c == '"')
            {
                inQuote = true;
                quoteChar = c;
            }
            else if (c == '(') parenDepth++;
            else if (c == ')') parenDepth--;
            else if (c == ',' && parenDepth == 0)
            {
                commaIdx = k;
                break;
            }
        }

        if (commaIdx < 0)
            return (null, null);

        var firstArg = argsStr[..commaIdx].Trim();
        var secondArg = argsStr[(commaIdx + 1)..].Trim();

        // First argument should be a string literal (key name)
        string? keyName = null;
        if (firstArg.Length >= 2 && (firstArg[0] == '\'' || firstArg[0] == '"') && firstArg[^1] == firstArg[0])
        {
            keyName = firstArg[1..^1];
        }
        else
        {
            return (null, null); // Not a string literal
        }

        // Second argument: parse as an XQuery expression
        try
        {
            var valueExpr = ParseExpr(secondArg);

            // XTSE0340: In a pattern, the second argument to key() must be a literal or variable reference
            if (valueExpr is not (XQuery.Ast.LiteralExpression or XQuery.Ast.VariableReference))
            {
                throw new XsltException(
                    "XTSE0340: The second argument to key() in a pattern must be a literal or variable reference",
                    GetSourceLocation(context));
            }

            return (keyName, valueExpr);
        }
#pragma warning disable CA1031
        catch (XsltException)
        {
            throw; // Re-throw XTSE0340
        }
        catch (Exception)
        {
            return (null, null);
        }
#pragma warning restore CA1031
    }

    private IdPattern? TryParseIdPattern(string pattern, XElement context)
    {
        if (!pattern.StartsWith("id(", StringComparison.Ordinal))
            return null;

        // Make sure it's not generate-id( or some other function ending in "id("
        // by checking the character before "id(" is not a letter/digit
        // Actually, since we trim the pattern, "id(" at position 0 is always valid.

        // Find the matching closing parenthesis for id(...)
        var depth = 0;
        var closeIdx = -1;
        for (var k = 3; k < pattern.Length; k++)
        {
            var c = pattern[k];
            if (c == '(') depth++;
            else if (c == ')')
            {
                if (depth == 0) { closeIdx = k; break; }
                depth--;
            }
            else if (c == '\'' || c == '"')
            {
                var q = c;
                k++;
                while (k < pattern.Length && pattern[k] != q) k++;
            }
        }

        if (closeIdx < 0)
            return null;

        // Extract the argument: id(value)
        var argStr = pattern[3..closeIdx].Trim();
        XQueryExpression valueExpr;
        try
        {
            valueExpr = ParseExpr(argStr);
        }
#pragma warning disable CA1031
        catch (Exception)
        {
            return null;
        }
#pragma warning restore CA1031

        // Check for continuation path: id(...)//p or id(...)/p
        var rest = pattern[(closeIdx + 1)..].TrimStart();
        if (rest.Length == 0)
        {
            return new IdPattern { ValueExpression = valueExpr };
        }

        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            return new IdPattern
            {
                ValueExpression = valueExpr,
                Continuation = ParsePattern(rest[2..], context),
                DescendantSeparator = true
            };
        }

        if (rest.StartsWith('/'))
        {
            return new IdPattern
            {
                ValueExpression = valueExpr,
                Continuation = ParsePattern(rest[1..], context),
                DescendantSeparator = false
            };
        }

        // Unrecognized continuation — not an id pattern
        return null;
    }

    private VariableReferencePattern? TryParseVariableReferencePattern(string pattern, XElement context)
    {
        if (pattern.Length < 2 || pattern[0] != '$')
            return null;

        // Extract the variable name: $name or $prefix:name or $Q{uri}name
        var nameStart = 1;
        var nameEnd = nameStart;

        // Handle EQName: $Q{uri}local
        if (pattern.Length > nameStart + 1 && pattern[nameStart] == 'Q' && pattern[nameStart + 1] == '{')
        {
            var closeBrace = pattern.IndexOf('}', nameStart + 2);
            if (closeBrace < 0) return null;
            nameEnd = closeBrace + 1;
            // Continue past the local name
            while (nameEnd < pattern.Length && (char.IsLetterOrDigit(pattern[nameEnd]) || pattern[nameEnd] == '_' || pattern[nameEnd] == '-' || pattern[nameEnd] == '.'))
                nameEnd++;
        }
        else
        {
            // Regular QName: $name or $prefix:name
            while (nameEnd < pattern.Length && (char.IsLetterOrDigit(pattern[nameEnd]) || pattern[nameEnd] == '_' || pattern[nameEnd] == '-' || pattern[nameEnd] == '.' || pattern[nameEnd] == ':'))
                nameEnd++;
        }

        if (nameEnd <= nameStart) return null;

        var varNameStr = pattern[nameStart..nameEnd];
        var varName = ParseQName(varNameStr, context);

        // Check for continuation path: $var//path or $var/path
        var rest = pattern[nameEnd..].TrimStart();
        if (rest.Length == 0)
        {
            return new VariableReferencePattern { VariableName = varName };
        }

        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            return new VariableReferencePattern
            {
                VariableName = varName,
                Continuation = ParsePattern(rest[2..], context),
                DescendantSeparator = true
            };
        }

        if (rest.StartsWith('/'))
        {
            return new VariableReferencePattern
            {
                VariableName = varName,
                Continuation = ParsePattern(rest[1..], context),
                DescendantSeparator = false
            };
        }

        // Unrecognized continuation — not a variable reference pattern
        return null;
    }

    private DocFunctionPattern? TryParseDocFunctionPattern(string pattern, XElement context)
    {
        // Match doc('uri') or doc("uri") at the start of a pattern
        if (!pattern.StartsWith("doc(", StringComparison.Ordinal) &&
            !pattern.StartsWith("doc (", StringComparison.Ordinal))
            return null;

        var openParen = pattern.IndexOf('(', StringComparison.Ordinal);
        if (openParen < 0) return null;

        // Find the matching closing paren
        var depth = 1;
        var i = openParen + 1;
        while (i < pattern.Length && depth > 0)
        {
            if (pattern[i] == '(') depth++;
            else if (pattern[i] == ')') depth--;
            else if (pattern[i] == '\'' || pattern[i] == '"')
            {
                var q = pattern[i];
                i++;
                while (i < pattern.Length && pattern[i] != q) i++;
            }
            i++;
        }
        if (depth != 0) return null;

        var closeParen = i - 1;
        var argsStr = pattern[(openParen + 1)..closeParen].Trim();

        // Extract the URI string argument — must be a string literal
        if (argsStr.Length < 2) return null;
        if ((argsStr[0] != '\'' && argsStr[0] != '"') || argsStr[^1] != argsStr[0])
            return null;
        var uri = argsStr[1..^1];

        // Resolve relative URI against the stylesheet base URI
        var baseUri = ResolveEffectiveBaseUri(context);
        if (baseUri != null)
        {
            try
            {
                var resolvedUri = new Uri(baseUri, uri);
                uri = resolvedUri.ToString();
            }
#pragma warning disable CA1031 // Intentional broad catch — see comment below
            catch (Exception)
            {
                // If the relative URI cannot be resolved against the stylesheet base URI at
                // compile time (e.g. unknown base URI, opaque URI scheme), keep the original
                // literal URI. It will be resolved at runtime by the document resolver.
            }
#pragma warning restore CA1031
        }

        // Check for continuation path
        var rest = pattern[(closeParen + 1)..].TrimStart();
        if (rest.Length == 0)
        {
            return new DocFunctionPattern { DocumentUri = uri };
        }

        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            return new DocFunctionPattern
            {
                DocumentUri = uri,
                Continuation = ParsePattern(rest[2..], context),
                DescendantSeparator = true
            };
        }

        if (rest.StartsWith('/'))
        {
            return new DocFunctionPattern
            {
                DocumentUri = uri,
                Continuation = ParsePattern(rest[1..], context),
                DescendantSeparator = false
            };
        }

        // Unrecognized continuation
        return null;
    }

    private static string ReadSegment(string pattern, ref int pos)
    {
        var start = pos;
        var bracketDepth = 0;
        var braceDepth = 0;
        while (pos < pattern.Length)
        {
            var c = pattern[pos];
            if (c == '[') bracketDepth++;
            else if (c == ']') bracketDepth--;
            else if (c == '{') braceDepth++;
            else if (c == '}') braceDepth--;
            else if (c == '/' && bracketDepth == 0 && braceDepth == 0) break;
            pos++;
        }
        return pattern[start..pos];
    }

    /// <summary>
    /// Splits a pattern at '|' delimiters outside of brackets and string literals.
    /// </summary>
    private static List<string> SplitUnionPattern(string pattern)
    {
        var parts = new List<string>();
        var bracketDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var start = 0;

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (inSingleQuote)
            {
                if (c == '\'') inSingleQuote = false;
            }
            else if (inDoubleQuote)
            {
                if (c == '"') inDoubleQuote = false;
            }
            else
            {
                switch (c)
                {
                    case '\'': inSingleQuote = true; break;
                    case '"': inDoubleQuote = true; break;
                    case '[': bracketDepth++; break;
                    case ']': bracketDepth--; break;
                    case '(' : bracketDepth++; break;
                    case ')': bracketDepth--; break;
                    case '|' when bracketDepth == 0:
                        parts.Add(pattern[start..i]);
                        start = i + 1;
                        break;
                    // XSLT 3.0: 'union' keyword as synonym for '|' in patterns
                    case 'u' when bracketDepth == 0
                        && i + 5 <= pattern.Length
                        && pattern.AsSpan(i, 5).SequenceEqual("union")
                        && (i == 0 || !char.IsLetterOrDigit(pattern[i - 1]) && pattern[i - 1] != '_' && pattern[i - 1] != '-')
                        && (i + 5 >= pattern.Length || !char.IsLetterOrDigit(pattern[i + 5]) && pattern[i + 5] != '_' && pattern[i + 5] != '-'):
                        parts.Add(pattern[start..i]);
                        start = i + 5;
                        i += 4; // loop will increment to i+5
                        break;
                }
            }
        }

        parts.Add(pattern[start..]);
        return parts;
    }

    /// <summary>
    /// Expands parenthesized unions within a path pattern into top-level union alternatives.
    /// E.g. "x/(child::a|descendant::b)" → ["x/child::a", "x/descendant::b"]
    /// Returns null if no expansion is needed.
    /// </summary>
    private static List<string>? ExpandParenthesizedUnions(string pattern)
    {
        // Scan the pattern for a '(' that is at the path-step level (not inside brackets/quotes)
        var bracketDepth = 0;
        var parenDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (inSingleQuote) { if (c == '\'') inSingleQuote = false; continue; }
            if (inDoubleQuote) { if (c == '"') inDoubleQuote = false; continue; }
            switch (c)
            {
                case '\'': inSingleQuote = true; break;
                case '"': inDoubleQuote = true; break;
                case '[': bracketDepth++; break;
                case ']': bracketDepth--; break;
                case '(' when bracketDepth == 0 && parenDepth == 0:
                {
                    // Found a top-level '(' — find the matching ')'
                    var openPos = i;
                    var depth = 1;
                    var j = i + 1;
                    while (j < pattern.Length && depth > 0)
                    {
                        var cc = pattern[j];
                        if (cc == '(') depth++;
                        else if (cc == ')') depth--;
                        j++;
                    }
                    if (depth != 0) break; // unmatched paren, skip
                    var closePos = j - 1;
                    var inner = pattern[(openPos + 1)..closePos];

                    // Split inner on '|' at depth 0
                    var alternatives = SplitUnionPattern(inner);
                    if (alternatives.Count <= 1) { parenDepth++; break; } // no union, skip

                    // Build expanded patterns: prefix + alternative + suffix
                    var prefix = pattern[..openPos];
                    var suffix = pattern[(closePos + 1)..];
                    var result = new List<string>();
                    foreach (var alt in alternatives)
                    {
                        result.Add((prefix + alt.Trim() + suffix).Trim());
                    }
                    return result;
                }
                case '(': parenDepth++; break;
                case ')': parenDepth--; break;
            }
        }

        return null; // no expansion needed
    }

    /// <summary>
    /// Splits a pattern on 'except' or 'intersect' keywords at depth 0 (outside brackets, parens, quotes, braces).
    /// Returns null if no such keyword is found.
    /// Result: list of (Part, IsExcept) where IsExcept is true for 'except', false for 'intersect'.
    /// The first element's IsExcept value is meaningless (it's the leftmost operand).
    /// </summary>
    private static List<(string Part, bool IsExcept)>? SplitExceptIntersect(string pattern)
    {
        // Find all top-level 'except'/'intersect' keyword positions
        var splits = new List<(int Position, int Length, bool IsExcept)>();
        var bracketDepth = 0;
        var parenDepth = 0;
        var braceDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (inSingleQuote) { if (c == '\'') inSingleQuote = false; continue; }
            if (inDoubleQuote) { if (c == '"') inDoubleQuote = false; continue; }
            switch (c)
            {
                case '\'': inSingleQuote = true; continue;
                case '"': inDoubleQuote = true; continue;
                case '[': bracketDepth++; continue;
                case ']': bracketDepth--; continue;
                case '(': parenDepth++; continue;
                case ')': parenDepth--; continue;
                case '{': braceDepth++; continue;
                case '}': braceDepth--; continue;
            }
            if (bracketDepth != 0 || parenDepth != 0 || braceDepth != 0) continue;

            // Check word boundary before the keyword
            if (i > 0 && (char.IsLetterOrDigit(pattern[i - 1]) || pattern[i - 1] == '-' || pattern[i - 1] == '_'))
                continue;

            if (c == 'e' && i + 6 <= pattern.Length && pattern.AsSpan(i, 6).SequenceEqual("except")
                && (i + 6 >= pattern.Length || !char.IsLetterOrDigit(pattern[i + 6]) && pattern[i + 6] != '-' && pattern[i + 6] != '_'))
            {
                splits.Add((i, 6, true));
                i += 5; // skip rest of keyword
            }
            else if (c == 'i' && i + 9 <= pattern.Length && pattern.AsSpan(i, 9).SequenceEqual("intersect")
                && (i + 9 >= pattern.Length || !char.IsLetterOrDigit(pattern[i + 9]) && pattern[i + 9] != '-' && pattern[i + 9] != '_'))
            {
                splits.Add((i, 9, false));
                i += 8;
            }
        }

        if (splits.Count == 0) return null;

        var parts = new List<(string Part, bool IsExcept)>();
        var prevEnd = 0;
        foreach (var (pos, len, isExcept) in splits)
        {
            parts.Add((pattern[prevEnd..pos].Trim(), isExcept));
            prevEnd = pos + len;
        }
        // Add the final part after the last keyword
        parts.Add((pattern[prevEnd..].Trim(), splits[^1].IsExcept));

        return parts;
    }

    /// <summary>
    /// Handles parenthesized except/intersect within path patterns.
    /// E.g., "x/(descendant::a except child::a)" → ExceptPattern(ParsePattern("x/descendant::a"), ParsePattern("x/child::a"))
    /// Returns null if no parenthesized except/intersect is found.
    /// </summary>
    private XsltPattern? TryExpandParenthesizedExceptIntersect(string pattern, XElement context)
    {
        var bracketDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (inSingleQuote) { if (c == '\'') inSingleQuote = false; continue; }
            if (inDoubleQuote) { if (c == '"') inDoubleQuote = false; continue; }
            switch (c)
            {
                case '\'': inSingleQuote = true; break;
                case '"': inDoubleQuote = true; break;
                case '[': bracketDepth++; break;
                case ']': bracketDepth--; break;
                case '(' when bracketDepth == 0:
                {
                    var openPos = i;
                    var depth = 1;
                    var j = i + 1;
                    while (j < pattern.Length && depth > 0)
                    {
                        if (pattern[j] == '(') depth++;
                        else if (pattern[j] == ')') depth--;
                        j++;
                    }
                    if (depth != 0) break;
                    var closePos = j - 1;
                    var inner = pattern[(openPos + 1)..closePos];

                    // Check if inner contains except/intersect at depth 0
                    var excIntParts = SplitExceptIntersect(inner);
                    if (excIntParts == null || excIntParts.Count < 2) break;

                    // Build expanded patterns: prefix + each part + suffix
                    var prefix = pattern[..openPos];
                    var suffix = pattern[(closePos + 1)..];

                    // Build the left-associative except/intersect chain with prefix/suffix
                    var result = ParsePattern((prefix + excIntParts[0].Part.Trim() + suffix).Trim(), context);
                    for (var idx = 1; idx < excIntParts.Count; idx++)
                    {
                        var right = ParsePattern((prefix + excIntParts[idx].Part.Trim() + suffix).Trim(), context);
                        result = excIntParts[idx].IsExcept
                            ? new ExceptPattern { Left = result, Right = right }
                            : new IntersectPattern { Left = result, Right = right };
                    }
                    return result;
                }
            }
        }

        return null;
    }

    private static NodeTest ParseNodeTest(string name, XElement context, bool isAttribute = false)
    {
        // Kind tests
        if (name == "node()")
            return new KindTest { Kind = XdmNodeKind.None }; // None = any node
        if (name == "text()")
            return new KindTest { Kind = XdmNodeKind.Text };
        if (name == "comment()")
            return new KindTest { Kind = XdmNodeKind.Comment };
        if (name.StartsWith("processing-instruction(", StringComparison.Ordinal))
        {
            // Extract PI target name from processing-instruction('name') or processing-instruction("name")
            var inner = name["processing-instruction(".Length..^1].Trim();
            NameTest? piNameTest = null;
            if (inner.Length >= 2 && (inner[0] == '\'' || inner[0] == '"') && inner[^1] == inner[0])
            {
                var piName = inner[1..^1];
                if (!string.IsNullOrEmpty(piName))
                    piNameTest = new NameTest { LocalName = piName };
            }
            else if (!string.IsNullOrEmpty(inner))
            {
                // Unquoted PI name — validate it's a valid NCName (no colons)
                if (inner.Contains(':', StringComparison.Ordinal))
                    throw new XsltException($"XTSE0340: Processing instruction name '{inner}' must not contain a colon");
                piNameTest = new NameTest { LocalName = inner };
            }
            return new KindTest { Kind = XdmNodeKind.ProcessingInstruction, Name = piNameTest };
        }
        if (name is "document-node()" or "root()")
            return new KindTest { Kind = XdmNodeKind.Document };
        // document-node(element(E)) or document-node(element(E, type))
        if (name.StartsWith("document-node(element(", StringComparison.Ordinal) && name.EndsWith("))", StringComparison.Ordinal))
        {
            var inner = name["document-node(element(".Length..^2].Trim();
            NameTest? elemName = null;
            if (inner.Length > 0 && inner != "*")
            {
                // Handle element(name) or element(name, type) — only use name part
                var commaIdx = inner.IndexOf(',', StringComparison.Ordinal);
                var elemLocalName = commaIdx >= 0 ? inner[..commaIdx].Trim() : inner;
                elemName = ParseNameTest(elemLocalName, context, isAttribute: false) as NameTest;
            }
            return new KindTest { Kind = XdmNodeKind.Document, DocumentElementTest = elemName };
        }
        if (name.StartsWith("document-node(schema-element(", StringComparison.Ordinal) && name.EndsWith("))", StringComparison.Ordinal))
        {
            // Treat schema-element the same as element for non-schema-aware processor
            var inner = name["document-node(schema-element(".Length..^2].Trim();
            NameTest? elemName = inner.Length > 0 ? ParseNameTest(inner, context, isAttribute: false) as NameTest : null;
            return new KindTest { Kind = XdmNodeKind.Document, DocumentElementTest = elemName };
        }
        if (name == "namespace-node()")
            return new KindTest { Kind = XdmNodeKind.Namespace };

        // XSLT 2.0+: element() and attribute() KindTest patterns
        if (name.StartsWith("element(", StringComparison.Ordinal) && name.EndsWith(')'))
            return ParseKindTestWithArgs(XdmNodeKind.Element, name["element(".Length..^1].Trim(), context);
        if (name.StartsWith("schema-element(", StringComparison.Ordinal) && name.EndsWith(')'))
            return ParseKindTestWithArgs(XdmNodeKind.Element, name["schema-element(".Length..^1].Trim(), context);
        if (name.StartsWith("attribute(", StringComparison.Ordinal) && name.EndsWith(')'))
            return ParseKindTestWithArgs(XdmNodeKind.Attribute, name["attribute(".Length..^1].Trim(), context);
        if (name.StartsWith("schema-attribute(", StringComparison.Ordinal) && name.EndsWith(')'))
            return ParseKindTestWithArgs(XdmNodeKind.Attribute, name["schema-attribute(".Length..^1].Trim(), context);

        return ParseNameTest(name, context, isAttribute);
    }

    /// <summary>
    /// Parses the inner arguments of element(...) or attribute(...) KindTest patterns.
    /// Handles: element(), element(*), element(name), element(*, type), element(name, type).
    /// </summary>
    private static KindTest ParseKindTestWithArgs(XdmNodeKind kind, string inner, XElement context)
    {
        if (inner.Length == 0 || inner == "*")
            return new KindTest { Kind = kind };

        // Split on comma (for element(name, type) or element(*, type))
        var commaIdx = inner.IndexOf(',', StringComparison.Ordinal);
        if (commaIdx >= 0)
        {
            var namePart = inner[..commaIdx].Trim();
            var typePart = inner[(commaIdx + 1)..].Trim();

            NameTest? nameTest = null;
            if (namePart != "*" && !string.IsNullOrEmpty(namePart))
                nameTest = ParseNameTest(namePart, context);

            XdmTypeName? typeName = null;
            if (!string.IsNullOrEmpty(typePart))
            {
                if (typePart.Contains(':', StringComparison.Ordinal))
                {
                    var typeParts = typePart.Split(':');
                    var typeNs = context.GetNamespaceOfPrefix(typeParts[0])?.NamespaceName;
                    typeName = new XdmTypeName { Prefix = typeParts[0], NamespaceUri = typeNs, LocalName = typeParts[1] };
                }
                else
                {
                    typeName = new XdmTypeName { LocalName = typePart };
                }
            }

            return new KindTest { Kind = kind, Name = nameTest, TypeName = typeName };
        }

        // Single argument: element(name)
        return new KindTest
        {
            Kind = kind,
            Name = ParseNameTest(inner, context)
        };
    }

    private static NameTest ParseNameTest(string name, XElement context, bool isAttribute = false)
    {
        if (name == "*")
        {
            return new NameTest { LocalName = "*" };
        }

        // EQName: Q{uri}local
        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var closeBrace = name.IndexOf('}', 2);
            if (closeBrace > 0)
            {
                var uri = name[2..closeBrace];
                var local = name[(closeBrace + 1)..];
                return new NameTest
                {
                    NamespaceUri = string.IsNullOrEmpty(uri) ? null : uri,
                    LocalName = local
                };
            }
        }

        if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            var prefix = parts[0];

            // *:NCName means wildcard namespace, specific local name
            if (prefix == "*")
            {
                return new NameTest
                {
                    Prefix = "*",
                    NamespaceUri = "*",
                    LocalName = parts[1]
                };
            }

            var ns = context.GetNamespaceOfPrefix(prefix)?.NamespaceName;
            if (ns == null)
                throw new XsltException($"XTSE0280: Prefix '{prefix}' is not declared");
            return new NameTest
            {
                Prefix = prefix,
                NamespaceUri = ns,
                LocalName = parts[1]
            };
        }

        // Validate the name is a valid NCName — reject names starting with a digit
        // (e.g., "1223" in match="name/1223" or count="2+2")
        if (name.Length > 0 && name != "*" && char.IsAsciiDigit(name[0]))
            throw new XsltException($"XTSE0340: '{name}' is not a valid name test in a pattern");

        // Apply xpath-default-namespace for unprefixed element name tests
        // Per spec, xpath-default-namespace does NOT apply to attribute names
        if (!isAttribute)
        {
            var xdn = GetXpathDefaultNamespace(context);
            if (xdn != null)
                return new NameTest { LocalName = name, NamespaceUri = xdn };
        }

        return new NameTest { LocalName = name };
    }

    // Map well-known namespace URIs to the NamespaceId values that FunctionLibrary uses.
    // Unknown namespaces keep NamespaceId(0) to match the XQuery parser's behavior
    // (XQueryAstBuilder.MakeQName uses default NamespaceId for all prefixed names).
    private static readonly Dictionary<string, NamespaceId> _wellKnownNamespaces = new()
    {
        ["http://www.w3.org/XML/1998/namespace"] = NamespaceId.Xml,                  // 1
        ["http://www.w3.org/2001/XMLSchema"] = new NamespaceId(2),                   // FunctionNamespaces.Xs
        ["http://www.w3.org/2005/xpath-functions"] = NamespaceId.Fn,                 // 5
        ["http://www.w3.org/2005/xpath-functions/map"] = NamespaceId.Map,            // 6
        ["http://www.w3.org/2005/xpath-functions/array"] = NamespaceId.Array,        // 7
        ["http://www.w3.org/2005/xpath-functions/math"] = NamespaceId.Math,          // 8
        ["http://www.w3.org/1999/XSL/Transform"] = NamespaceId.Xslt,                // 10
    };

    /// <summary>
    /// Parses content body for variables/params: handles both child elements and text-only content.
    /// </summary>
    private XsltSequenceConstructor? ParseContentBody(XElement element, XAttribute? selectAttr)
    {
        if (selectAttr != null) return null;
        if (element.HasElements) return ParseSequenceConstructor(element);
        if (!element.IsEmpty)
        {
            var textValue = element.Value;
            if (!string.IsNullOrEmpty(textValue))
                return new XsltSequenceConstructor { Instructions = [new XsltLiteralText { Value = textValue }] };
        }
        return null;
    }

    private static QName ParseQName(string name, XElement context)
    {
        name = name.Trim();

        // Handle EQName syntax: Q{namespace-uri}local-name
        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var closeBrace = name.IndexOf('}', 2);
            if (closeBrace > 1)
            {
                var nsUri = name[2..closeBrace];
                var localName = name[(closeBrace + 1)..];
                var nsId = ResolveNamespaceUri(nsUri);
                return new QName(nsId, localName) { ExpandedNamespace = nsUri };
            }
        }

        if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            var ns = context.GetNamespaceOfPrefix(parts[0])?.NamespaceName;
            // XTSE0280: Prefixed QName must have an in-scope namespace binding
            if (ns == null)
                throw new XsltException($"XTSE0280: Namespace prefix '{parts[0]}' in QName '{name}' is not declared",
                    GetSourceLocation(context));
            // Assign/resolve the namespace ID (thread-safe intern)
            var nsId = ResolveNamespaceUri(ns);
            return new QName(nsId, parts[1], parts[0]);
        }

        return new QName(NamespaceId.None, name);
    }

    /// <summary>
    /// Parses an XPath/XQuery expression and resolves namespace prefixes in QNames
    /// using the XSLT element's namespace context.
    /// </summary>
    private XQueryExpression ParseExpression(string expression, XElement context)
    {
        var expr = _expressionParser.Parse(expression);
        ResolveExpressionNamespaces(expr, context);
        AttachXsltSourceLocation(expr, context);
        return expr;
    }

    /// <summary>
    /// Parses an XPath expression using the current namespace context (_nsContext).
    /// This is the primary method for parsing expressions within XSLT instructions.
    /// </summary>
    /// <remarks>
    /// When <paramref name="sourceAttribute"/> is supplied, the parser shifts every
    /// sub-expression's <see cref="SourceLocation"/> from being xpath-string-relative
    /// (line/col within the inline XPath text) to being XSLT-file-absolute. This is
    /// the Phase D1 source-location-audit fix: prior to this, an error inside
    /// <c>select="foo[bad-syntax"</c> would report the column WITHIN the XPath
    /// (e.g. 14), not the column in the actual stylesheet (e.g. 35) — useless for
    /// LSP diagnostics that need to squiggle the offending token.
    /// </remarks>
    private XQueryExpression ParseExpr(string expression, System.Xml.Linq.XAttribute? sourceAttribute = null)
    {
        var expr = _expressionParser.Parse(expression);
        if (_nsContext != null)
        {
            ResolveExpressionNamespaces(expr, _nsContext);
            // When sourceAttribute is supplied, the more-precise shift below sets both
            // Module and absolute file coordinates on every sub-expression; the legacy
            // AttachXsltSourceLocation only stamps Module on the top-level expression
            // and leaves child line/col xpath-relative.
            if (sourceAttribute == null)
                AttachXsltSourceLocation(expr, _nsContext);
        }
        if (sourceAttribute != null)
            ShiftExpressionLocationsToFileAbsolute(expr, sourceAttribute);
        return expr;
    }

    /// <summary>
    /// Like <see cref="ParseExpr(string, System.Xml.Linq.XAttribute?)"/> but with an
    /// explicit base position — used by AVT (D2) and inline-expression (D3) parsers
    /// where the inner XPath starts at an offset INSIDE the attribute value (not at
    /// the start). Caller supplies the absolute file position of the inner XPath's
    /// first character plus the module URI.
    /// </summary>
    private XQueryExpression ParseExprAt(string expression, int absoluteLine, int absoluteColumn, string? moduleUri)
    {
        var expr = _expressionParser.Parse(expression);
        if (_nsContext != null)
            ResolveExpressionNamespaces(expr, _nsContext);
        ShiftExpressionLocationsAt(expr, absoluteLine, absoluteColumn, moduleUri);
        return expr;
    }

    /// <summary>
    /// Shifts every <see cref="SourceLocation"/> on the parsed expression tree from
    /// XPath-relative coordinates to file-absolute. Same arithmetic as
    /// <see cref="ShiftExpressionLocationsToFileAbsolute"/> but with explicitly
    /// supplied base position (D2 needs this for AVT inner expressions).
    /// </summary>
    private static void ShiftExpressionLocationsAt(
        XQueryExpression expr, int absoluteLine, int absoluteColumn, string? moduleUri)
    {
        WalkExpressions(expr, node =>
        {
            if (node.Location is not { } loc) return;
            var fileLine = absoluteLine + loc.Line - 1;
            var fileCol = loc.Line == 1 ? absoluteColumn + loc.Column : loc.Column;
            node.Location = loc with { Line = fileLine, Column = fileCol, Module = string.IsNullOrEmpty(moduleUri) ? loc.Module : moduleUri };
        });
    }

    /// <summary>
    /// Shifts every <see cref="SourceLocation"/> on the parsed expression tree from
    /// XPath-string-relative coordinates to absolute coordinates within the source
    /// XSLT file. The XPath text begins inside an attribute value; the value's start
    /// position is computed from the attribute's <see cref="System.Xml.IXmlLineInfo"/>
    /// plus the attribute name length and the <c>="</c> delimiter (3 chars).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Line offset (D5): XPaths can span multiple lines (rare but legal). Line N within
    /// the XPath maps to file line <c>(attr-line + N - 1)</c>. Column on line 1 of
    /// the XPath gets the value's start column added; subsequent lines start at
    /// column 1 of the file (no offset), which matches XML attribute-value continuation.
    /// </para>
    /// <para>
    /// Column conventions: ANTLR (used by the XQuery parser) reports columns 0-based;
    /// <see cref="System.Xml.IXmlLineInfo"/> reports them 1-based. We treat the
    /// <see cref="SourceLocation.Column"/> stored on parsed expressions as ANTLR's
    /// 0-based form and add the 1-based attribute-value start column directly — the
    /// off-by-one cancels out for line-1 columns since ANTLR's "first character" is 0
    /// while the attribute value's "first character" is at 1-based column N.
    /// </para>
    /// </remarks>
    private static void ShiftExpressionLocationsToFileAbsolute(
        XQueryExpression expr, System.Xml.Linq.XAttribute attribute)
    {
        if (attribute is not System.Xml.IXmlLineInfo li || !li.HasLineInfo()) return;
        var attrLine = li.LineNumber;
        // Position of the first character of the attribute value:
        //   attr-name + '="' = name.Length + 2 chars after the attribute name's start column.
        // The attribute's reported LinePosition points at the attribute name; add the
        // name length plus the '="' delimiter.
        var attrName = attribute.Name.LocalName;
        if (!string.IsNullOrEmpty(attribute.Name.NamespaceName)
            && attribute.Parent?.GetPrefixOfNamespace(attribute.Name.Namespace) is { Length: > 0 } prefix)
            attrName = prefix + ":" + attrName;
        var valueStartColumn = li.LinePosition + attrName.Length + 2;

        var moduleUri = attribute.Parent?.BaseUri;
        WalkExpressions(expr, node =>
        {
            if (node.Location is not { } loc) return;
            // Line: ANTLR is 1-based for line as well. XPath line 1 → attrLine.
            var fileLine = attrLine + loc.Line - 1;
            // Column: only line 1 of the XPath gets the value-start offset. For line N>1,
            // the XPath continues at column 1 of the next file line (no offset).
            var fileCol = loc.Line == 1 ? valueStartColumn + loc.Column : loc.Column;
            node.Location = loc with { Line = fileLine, Column = fileCol, Module = string.IsNullOrEmpty(moduleUri) ? loc.Module : moduleUri };
        });
    }

    /// <summary>
    /// Walks an XQuery expression tree and applies <paramref name="visit"/> to every
    /// node. Mirrors the structure of <see cref="ResolveExpressionNamespaces"/> /
    /// <see cref="ContainsVariableReference"/> but factored as a generic post-order walk.
    /// </summary>
    private static void WalkExpressions(XQueryExpression expr, Action<XQueryExpression> visit)
    {
        visit(expr);
        switch (expr)
        {
            case BinaryExpression be:
                WalkExpressions(be.Left, visit);
                WalkExpressions(be.Right, visit);
                break;
            case UnaryExpression ue:
                WalkExpressions(ue.Operand, visit);
                break;
            case PathExpression pe:
                if (pe.InitialExpression != null) WalkExpressions(pe.InitialExpression, visit);
                foreach (var s in pe.Steps) WalkExpressions(s, visit);
                break;
            case StepExpression se:
                foreach (var p in se.Predicates) WalkExpressions(p, visit);
                break;
            case FilterExpression fe:
                WalkExpressions(fe.Primary, visit);
                foreach (var p in fe.Predicates) WalkExpressions(p, visit);
                break;
            case IfExpression ie:
                WalkExpressions(ie.Condition, visit);
                WalkExpressions(ie.Then, visit);
                if (ie.Else != null) WalkExpressions(ie.Else, visit);
                break;
            case FunctionCallExpression fc:
                foreach (var a in fc.Arguments) WalkExpressions(a, visit);
                break;
            case SequenceExpression seq:
                foreach (var i in seq.Items) WalkExpressions(i, visit);
                break;
            case InstanceOfExpression inst:
                WalkExpressions(inst.Expression, visit);
                break;
            case CastExpression cast:
                WalkExpressions(cast.Expression, visit);
                break;
            case CastableExpression castable:
                WalkExpressions(castable.Expression, visit);
                break;
            case TreatExpression treat:
                WalkExpressions(treat.Expression, visit);
                break;
            case SimpleMapExpression sme:
                WalkExpressions(sme.Left, visit);
                WalkExpressions(sme.Right, visit);
                break;
            case StringConcatExpression sce:
                foreach (var o in sce.Operands) WalkExpressions(o, visit);
                break;
            case RangeExpression re:
                WalkExpressions(re.Start, visit);
                WalkExpressions(re.End, visit);
                break;
            case ArrowExpression ae:
                WalkExpressions(ae.Expression, visit);
                WalkExpressions(ae.FunctionCall, visit);
                break;
            case InlineFunctionExpression ife:
                if (ife.Body != null) WalkExpressions(ife.Body, visit);
                break;
            case DynamicFunctionCallExpression dfc:
                WalkExpressions(dfc.FunctionExpression, visit);
                foreach (var a in dfc.Arguments) WalkExpressions(a, visit);
                break;
            case FlworExpression flwor:
                foreach (var clause in flwor.Clauses)
                {
                    if (clause is ForClause forClause)
                        foreach (var b in forClause.Bindings) WalkExpressions(b.Expression, visit);
                    else if (clause is LetClause letClause)
                        foreach (var b in letClause.Bindings) WalkExpressions(b.Expression, visit);
                    else if (clause is WhereClause whereClause)
                        WalkExpressions(whereClause.Condition, visit);
                    else if (clause is OrderByClause orderBy)
                        foreach (var s in orderBy.OrderSpecs) WalkExpressions(s.Expression, visit);
                }
                WalkExpressions(flwor.ReturnExpression, visit);
                break;
        }
    }

    /// <summary>
    /// Records the originating XSLT module URI + element line/column on the parsed
    /// expression's <see cref="SourceLocation"/>. Without this, runtime XQuery errors
    /// raised from XPath embedded in XSLT show only "[line N, col M]" relative to the
    /// inline XPath string — useless across thousands of similar expressions in real
    /// stylesheets (Docbook TNG, Schxslt2, etc.). With it, the EvaluateAsync diagnostic
    /// can prefix errors with the actual file URI and the line of the XSLT instruction
    /// that contained the offending XPath.
    /// </summary>
    private static void AttachXsltSourceLocation(XQueryExpression expr, XElement context)
    {
        if (context is not System.Xml.IXmlLineInfo li || !li.HasLineInfo()) return;
        var moduleUri = context.BaseUri;
        if (string.IsNullOrEmpty(moduleUri)) return;
        // Preserve any existing Line/Column from the XPath parser (which gave us the
        // position WITHIN the inline XPath string), but stamp Module = XSLT file URI so
        // the runtime can attribute errors to the right source file. The XSLT element's
        // line is also useful, so when the parsed expression has no Location yet,
        // synthesize one from the element's position.
        expr.Location = expr.Location is { } existing
            ? existing with { Module = moduleUri }
            : new SourceLocation(li.LineNumber, li.LinePosition, 0, 0) { Module = moduleUri };
    }

    /// <summary>
    /// Walks an XQuery expression tree and resolves namespace prefixes on
    /// VariableReference and FunctionCallExpression QNames.
    /// </summary>
    private static void ResolveExpressionNamespaces(XQueryExpression expr, XElement context)
    {
        switch (expr)
        {
            case VariableReference vr:
                if (!string.IsNullOrEmpty(vr.Name.Prefix) && vr.Name.Namespace == NamespaceId.None)
                    vr.Name = ParseQName($"{vr.Name.Prefix}:{vr.Name.LocalName}", context);
                // EQName variable references: resolve ExpandedNamespace to NamespaceId so
                // Dictionary lookup matches the declared variable's QName (record struct equality)
                else if (vr.Name.ExpandedNamespace != null && vr.Name.Namespace == NamespaceId.None)
                    vr.Name = ParseQName($"Q{{{vr.Name.ExpandedNamespace}}}{vr.Name.LocalName}", context);
                break;
            case FunctionCallExpression fc:
                if (!string.IsNullOrEmpty(fc.Name.Prefix) && fc.Name.Namespace == NamespaceId.None)
                    fc.Name = ParseQName($"{fc.Name.Prefix}:{fc.Name.LocalName}", context);
                foreach (var arg in fc.Arguments)
                    ResolveExpressionNamespaces(arg, context);
                break;
            case BinaryExpression be:
                ResolveExpressionNamespaces(be.Left, context);
                ResolveExpressionNamespaces(be.Right, context);
                break;
            case UnaryExpression ue:
                ResolveExpressionNamespaces(ue.Operand, context);
                break;
            case PathExpression pe:
                if (pe.InitialExpression != null)
                    ResolveExpressionNamespaces(pe.InitialExpression, context);
                foreach (var step in pe.Steps)
                    ResolveExpressionNamespaces(step, context);
                break;
            case StepExpression se:
                if (se.NodeTest is NameTest nt)
                {
                    if (!string.IsNullOrEmpty(nt.Prefix) && nt.Prefix != "*" && nt.NamespaceUri == null)
                    {
                        // Resolve explicit prefix to URI
                        var ns = context.GetNamespaceOfPrefix(nt.Prefix)?.NamespaceName;
                        if (ns != null)
                            nt.NamespaceUri = ns;
                        else if (!IsBackwardsCompatible(context))
                            throw new XsltException($"XPST0081: Namespace prefix '{nt.Prefix}' has not been declared");
                        // In backwards-compatible mode (XSLT 1.0), leave prefix unresolved —
                        // error deferred to runtime per XSLT §3.12
                    }
                    else if (nt.Prefix == null && nt.NamespaceUri == null && !nt.IsLocalNameWildcard
                             && se.Axis != Axis.Attribute && se.Axis != Axis.Namespace)
                    {
                        // Apply xpath-default-namespace for unprefixed element name tests
                        var xdn = GetXpathDefaultNamespace(context);
                        if (xdn != null)
                            nt.NamespaceUri = xdn;
                    }
                }
                foreach (var pred in se.Predicates)
                    ResolveExpressionNamespaces(pred, context);
                break;
            case FilterExpression fe:
                ResolveExpressionNamespaces(fe.Primary, context);
                foreach (var pred in fe.Predicates)
                    ResolveExpressionNamespaces(pred, context);
                break;
            case IfExpression ie:
                ResolveExpressionNamespaces(ie.Condition, context);
                ResolveExpressionNamespaces(ie.Then, context);
                if (ie.Else != null)
                    ResolveExpressionNamespaces(ie.Else, context);
                break;
            case FlworExpression flwor:
                foreach (var clause in flwor.Clauses)
                {
                    if (clause is ForClause forClause)
                        foreach (var binding in forClause.Bindings)
                            ResolveExpressionNamespaces(binding.Expression, context);
                    else if (clause is LetClause letClause)
                        foreach (var binding in letClause.Bindings)
                            ResolveExpressionNamespaces(binding.Expression, context);
                    else if (clause is WhereClause whereClause)
                        ResolveExpressionNamespaces(whereClause.Condition, context);
                    else if (clause is OrderByClause orderBy)
                        foreach (var spec in orderBy.OrderSpecs)
                            ResolveExpressionNamespaces(spec.Expression, context);
                }
                ResolveExpressionNamespaces(flwor.ReturnExpression, context);
                break;
            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    ResolveExpressionNamespaces(item, context);
                break;
            case InstanceOfExpression inst:
                ResolveExpressionNamespaces(inst.Expression, context);
                ValidateUnprefixedTypeName(inst.TargetType, context);
                break;
            case CastExpression cast:
                ResolveExpressionNamespaces(cast.Expression, context);
                ValidateUnprefixedTypeName(cast.TargetType, context);
                break;
            case CastableExpression castable:
                ResolveExpressionNamespaces(castable.Expression, context);
                ValidateUnprefixedTypeName(castable.TargetType, context);
                break;
            case TreatExpression treat:
                ResolveExpressionNamespaces(treat.Expression, context);
                ValidateUnprefixedTypeName(treat.TargetType, context);
                break;
            case SimpleMapExpression sme:
                ResolveExpressionNamespaces(sme.Left, context);
                ResolveExpressionNamespaces(sme.Right, context);
                break;
            case StringConcatExpression sce:
                foreach (var operand in sce.Operands)
                    ResolveExpressionNamespaces(operand, context);
                break;
            case RangeExpression re:
                ResolveExpressionNamespaces(re.Start, context);
                ResolveExpressionNamespaces(re.End, context);
                break;
            case ArrowExpression ae:
                ResolveExpressionNamespaces(ae.Expression, context);
                ResolveExpressionNamespaces(ae.FunctionCall, context);
                break;
            case InlineFunctionExpression ife:
                if (ife.Body != null)
                    ResolveExpressionNamespaces(ife.Body, context);
                break;
            case DynamicFunctionCallExpression dfc:
                ResolveExpressionNamespaces(dfc.FunctionExpression, context);
                foreach (var arg in dfc.Arguments)
                    ResolveExpressionNamespaces(arg, context);
                break;
        }
    }

    /// <summary>
    /// Checks if an expression tree contains a VariableReference matching the given name.
    /// Used to detect self-referencing global variables (XPST0008).
    /// </summary>
    private static bool ContainsVariableReference(XQueryExpression expr, QName name)
    {
        switch (expr)
        {
            case VariableReference vr:
                return vr.Name.LocalName == name.LocalName && vr.Name.Namespace == name.Namespace;
            case BinaryExpression be:
                return ContainsVariableReference(be.Left, name) || ContainsVariableReference(be.Right, name);
            case UnaryExpression ue:
                return ContainsVariableReference(ue.Operand, name);
            case PathExpression pe:
                if (pe.InitialExpression != null && ContainsVariableReference(pe.InitialExpression, name))
                    return true;
                return pe.Steps.Any(s => ContainsVariableReference(s, name));
            case StepExpression se:
                return se.Predicates.Any(p => ContainsVariableReference(p, name));
            case FilterExpression fe:
                return ContainsVariableReference(fe.Primary, name) || fe.Predicates.Any(p => ContainsVariableReference(p, name));
            case IfExpression ie:
                return ContainsVariableReference(ie.Condition, name) || ContainsVariableReference(ie.Then, name)
                       || (ie.Else != null && ContainsVariableReference(ie.Else, name));
            case FunctionCallExpression fc:
                return fc.Arguments.Any(a => ContainsVariableReference(a, name));
            case SequenceExpression seq:
                return seq.Items.Any(i => ContainsVariableReference(i, name));
            case InlineFunctionExpression ife:
                return ife.Body != null && ContainsVariableReference(ife.Body, name);
            case DynamicFunctionCallExpression dfc:
                return ContainsVariableReference(dfc.FunctionExpression, name)
                       || dfc.Arguments.Any(a => ContainsVariableReference(a, name));
            case SimpleMapExpression sme:
                return ContainsVariableReference(sme.Left, name) || ContainsVariableReference(sme.Right, name);
            case StringConcatExpression sce:
                return sce.Operands.Any(o => ContainsVariableReference(o, name));
            case RangeExpression re:
                return ContainsVariableReference(re.Start, name) || ContainsVariableReference(re.End, name);
            case ArrowExpression ae:
                return ContainsVariableReference(ae.Expression, name) || ContainsVariableReference(ae.FunctionCall, name);
            case FlworExpression flwor:
                foreach (var clause in flwor.Clauses)
                {
                    if (clause is ForClause forClause && forClause.Bindings.Any(b => ContainsVariableReference(b.Expression, name)))
                        return true;
                    if (clause is LetClause letClause && letClause.Bindings.Any(b => ContainsVariableReference(b.Expression, name)))
                        return true;
                    if (clause is WhereClause whereClause && ContainsVariableReference(whereClause.Condition, name))
                        return true;
                    if (clause is OrderByClause orderBy && orderBy.OrderSpecs.Any(s => ContainsVariableReference(s.Expression, name)))
                        return true;
                }
                return ContainsVariableReference(flwor.ReturnExpression, name);
            case InstanceOfExpression inst:
                return ContainsVariableReference(inst.Expression, name);
            case CastExpression cast:
                return ContainsVariableReference(cast.Expression, name);
            case CastableExpression castable:
                return ContainsVariableReference(castable.Expression, name);
            case TreatExpression treat:
                return ContainsVariableReference(treat.Expression, name);
            default:
                return false;
        }
    }

    /// <summary>
    /// Gets the effective xpath-default-namespace from the context element or its XSLT ancestors.
    /// </summary>
    private static string? GetXpathDefaultNamespace(XElement context)
    {
        for (var el = context; el != null; el = el.Parent)
        {
            // Check for xsl:xpath-default-namespace (XSLT 3.0 standard attribute)
            var xdn = el.Attribute(XsltNs + "xpath-default-namespace");
            if (xdn != null)
                return string.IsNullOrEmpty(xdn.Value) ? null : xdn.Value;

            // Check for xpath-default-namespace on XSLT elements
            if (el.Name.Namespace == XsltNs)
            {
                xdn = el.Attribute("xpath-default-namespace");
                if (xdn != null)
                    return string.IsNullOrEmpty(xdn.Value) ? null : xdn.Value;
            }
        }
        return null;
    }

    /// <summary>
    /// Validates that an unprefixed atomic type name is in scope via xpath-default-namespace.
    /// Per XPath 3.1 §2.5.5.2, unprefixed type names in instance-of/cast/castable resolve using
    /// the default element/type namespace. If that namespace is not XSD, the name is unknown → XPST0051.
    /// </summary>
    private static void ValidateUnprefixedTypeName(XdmSequenceType type, XElement context)
    {
        if (type.UnprefixedTypeName == null) return;
        var xpathDefaultNs = GetXpathDefaultNamespace(context);
        if (xpathDefaultNs != "http://www.w3.org/2001/XMLSchema")
            throw new XsltException($"XPST0051: Unknown atomic type '{type.UnprefixedTypeName}' " +
                $"(unprefixed type names require xpath-default-namespace=\"http://www.w3.org/2001/XMLSchema\")",
                GetSourceLocation(context));
    }

    /// <summary>
    /// Checks if a variable name is declared in an outer scope (preceding siblings, ancestor scopes, or globals).
    /// Used to determine if a try-body variable shadows an outer variable visible from catch clauses.
    /// </summary>
    private static bool IsVariableDeclaredInOuterScope(XElement tryElement, string varName)
    {
        // Walk up from the try element checking preceding siblings at each level
        var current = tryElement;
        while (current.Parent != null)
        {
            foreach (var sibling in current.ElementsBeforeSelf())
            {
                if ((sibling.Name == XsltNs + "variable" || sibling.Name == XsltNs + "param")
                    && sibling.Attribute("name")?.Value == varName)
                    return true;
            }
            // Check template/function params
            if (current.Parent.Name == XsltNs + "template" || current.Parent.Name == XsltNs + "function")
            {
                foreach (var param in current.Parent.Elements(XsltNs + "param"))
                {
                    if (param.Attribute("name")?.Value == varName)
                        return true;
                }
            }
            // Check global scope
            if (current.Parent.Name == XsltNs + "stylesheet" || current.Parent.Name == XsltNs + "transform")
            {
                foreach (var child in current.Parent.Elements())
                {
                    if ((child.Name == XsltNs + "variable" || child.Name == XsltNs + "param")
                        && child.Attribute("name")?.Value == varName)
                        return true;
                }
                break;
            }
            current = current.Parent;
        }
        return false;
    }

    /// <summary>
    /// Splits a possibly-prefixed XML name into (localName, namespaceUri). Resolves prefixes
    /// against the in-scope namespace declarations on <paramref name="context"/>. Recognizes
    /// EQName syntax <c>Q{uri}local</c>. Returns <c>("", null)</c> for the wildcard <c>*</c>.
    /// </summary>
    private static (string localName, string? namespaceUri) SplitPrefixedName(string name, XElement? context)
    {
        if (string.IsNullOrEmpty(name) || name == "*")
            return ("", null);
        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var close = name.IndexOf('}', 2);
            if (close > 0 && close < name.Length - 1)
                return (name[(close + 1)..], name[2..close]);
            return (name, null);
        }
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return (name, null);
        var prefix = name[..colon];
        var local = name[(colon + 1)..];
        var ns = context?.GetNamespaceOfPrefix(prefix)?.NamespaceName;
        return (local, ns);
    }

    private static XdmSequenceType ParseSequenceType(string type, XElement? context = null)
    {
        // Simplified - would use full type parser
        var occurrence = Occurrence.ExactlyOne;

        // Strip occurrence indicator (* + ?) from end, but only if it's at the top level
        // (not inside nested parentheses). e.g., "map(*)" — the * is inside parens.
        // "map(xs:string, element()?)" — the ? is inside parens (part of value type).
        // "element()?" — the ? IS a top-level occurrence indicator.
        {
            var lastChar = type.Length > 0 ? type[^1] : '\0';
            if (lastChar is '*' or '+' or '?')
            {
                // Check if the indicator is at top-level (paren depth 0)
                int depth = 0;
                for (int oi = 0; oi < type.Length - 1; oi++)
                {
                    if (type[oi] == '(') depth++;
                    else if (type[oi] == ')') depth--;
                }
                if (depth == 0)
                {
                    // Also check it's not part of the type name: map(*), function(*)
                    var prevChar = type.Length >= 2 ? type[^2] : '\0';
                    if (lastChar == '*' && prevChar == '(')
                    {
                        // map(*), function(*), array(*) — not an occurrence indicator
                    }
                    else if (lastChar == '?')
                    {
                        // element()? at depth 0 is occurrence; (?)" should not match
                        if (!type.EndsWith("(?)", StringComparison.Ordinal))
                        {
                            occurrence = Occurrence.ZeroOrOne;
                            type = type[..^1].Trim();
                        }
                    }
                    else if (lastChar == '*')
                    {
                        occurrence = Occurrence.ZeroOrMore;
                        type = type[..^1].Trim();
                    }
                    else if (lastChar == '+')
                    {
                        occurrence = Occurrence.OneOrMore;
                        type = type[..^1].Trim();
                    }
                }
            }
        }

        // Normalize internal whitespace: "element ()" → "element()", "document-node ()" → "document-node()"
        type = System.Text.RegularExpressions.Regex.Replace(type.Trim(), @"\s*\(\s*\)", "()");
        type = System.Text.RegularExpressions.Regex.Replace(type, @"\s*\(\s*\*\s*\)", "(*)");

        // Strip outer parentheses: "(function(...) as ...)" → "function(...) as ..."
        // Only strip when the parens are truly wrapping the whole type (balanced)
        while (type.StartsWith('(') && type.EndsWith(')'))
        {
            // Verify the opening paren matches the closing one (not an inner group)
            int depth = 0;
            bool isWrap = true;
            for (int pi = 0; pi < type.Length - 1; pi++)
            {
                if (type[pi] == '(') depth++;
                else if (type[pi] == ')') depth--;
                if (depth == 0) { isWrap = false; break; }
            }
            if (isWrap)
                type = type[1..^1].Trim();
            else
                break;
        }

        // Apply xpath-default-namespace: when set to the XSD namespace, unprefixed atomic type
        // names (like "double") should be resolved as "xs:double". Per XSLT 3.0 spec section 5.2,
        // xpath-default-namespace applies to type names in SequenceType syntax.
        if (context != null && !type.Contains(':', StringComparison.Ordinal) && !type.Contains('(', StringComparison.Ordinal))
        {
            var xdn = GetXpathDefaultNamespace(context);
            if (xdn == "http://www.w3.org/2001/XMLSchema")
                type = "xs:" + type;
        }

        // Normalize any namespace prefix bound to the XSD namespace to the canonical "xs:"
        // so the type-name switch (keyed on "xs:") recognizes it. A stylesheet may bind e.g.
        // xmlns:xsd="http://www.w3.org/2001/XMLSchema" and write as="xsd:string" (attr/as-0116);
        // without this the type resolved to item() and `instance of` checks were wrong.
        if (context != null && !type.Contains('(', StringComparison.Ordinal))
        {
            var colon = type.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                var prefix = type[..colon];
                if (prefix != "xs"
                    && context.GetNamespaceOfPrefix(prefix)?.NamespaceName == "http://www.w3.org/2001/XMLSchema")
                {
                    type = "xs:" + type[(colon + 1)..];
                }
            }
        }

        // Handle parameterized map types: map(xs:string, xs:boolean)
        if (type.StartsWith("map(", StringComparison.Ordinal) && type.EndsWith(')') && type != "map(*)")
        {
            var inner = type[4..^1].Trim(); // content between "map(" and ")"
            var commaIdx = inner.IndexOf(',', StringComparison.Ordinal);
            if (commaIdx > 0)
            {
                var keyTypeStr = inner[..commaIdx].Trim();
                var valueTypeStr = inner[(commaIdx + 1)..].Trim();
                var keyType = ParseAtomicItemType(keyTypeStr);
                var valueType = ParseAtomicItemType(valueTypeStr);
                return new XdmSequenceType
                {
                    ItemType = ItemType.Map,
                    Occurrence = occurrence,
                    MapKeyType = keyType,
                    MapValueType = valueType
                };
            }
        }

        // Handle empty-sequence() — must return before the itemType switch
        if (type == "empty-sequence()")
        {
            return new XdmSequenceType { ItemType = ItemType.Item, Occurrence = Occurrence.Zero };
        }

        // Handle element(name) or element(name, type) or element(*, type) — named element type test.
        // Routed through SplitPrefixedName so EQName syntax (Q{ns}local) parses correctly:
        // the previous local-only split-on-':' handler swallowed the namespace URI's colon
        // (turning Q{urn:expected}root into ElementName="expected}root" with no namespace).
        if (type.StartsWith("element(", StringComparison.Ordinal) && type.EndsWith(')')  &&
            type != "element()" && type != "element(*)")
        {
            var inner = type[8..^1].Trim();
            var commaIdx = inner.IndexOf(',', StringComparison.Ordinal);
            var namePart = commaIdx >= 0 ? inner[..commaIdx].Trim() : inner;
            if (namePart == "*" || string.IsNullOrEmpty(namePart))
                return new XdmSequenceType { ItemType = ItemType.Element, Occurrence = occurrence };
            var (localName, nsUri) = SplitPrefixedName(namePart, context);
            return new XdmSequenceType
            {
                ItemType = ItemType.Element,
                Occurrence = occurrence,
                ElementName = localName,
                ElementNamespace = nsUri,
            };
        }

        // Handle document-node(element(name)) or document-node(element(name, type)) — document with named element test
        if (type.StartsWith("document-node(element(", StringComparison.Ordinal) && type.EndsWith("))", StringComparison.Ordinal))
        {
            var inner = type[22..^2].Trim();
            // Handle document-node(element(name, type)) — extract just the name part
            var commaIdx2 = inner.IndexOf(',', StringComparison.Ordinal);
            if (commaIdx2 >= 0)
                inner = inner[..commaIdx2].Trim();
            if (inner == "*" || inner.Length == 0)
                return new XdmSequenceType { ItemType = ItemType.Document, Occurrence = occurrence };
            var (docLocal, _) = SplitPrefixedName(inner, context);
            // XdmSequenceType has no DocumentElementNamespace yet; document-element namespace
            // matching is a future enhancement (rarely used in real stylesheets).
            return new XdmSequenceType
            {
                ItemType = ItemType.Document,
                Occurrence = occurrence,
                DocumentElementName = docLocal,
            };
        }

        // Handle attribute(name) or attribute(name, type) or attribute(*, type) — named attribute type test
        if (type.StartsWith("attribute(", StringComparison.Ordinal) && type.EndsWith(')')  &&
            type != "attribute()" && type != "attribute(*)")
        {
            var inner = type[10..^1].Trim();
            var commaIdx3 = inner.IndexOf(',', StringComparison.Ordinal);
            var namePart = commaIdx3 >= 0 ? inner[..commaIdx3].Trim() : inner;
            if (namePart == "*" || string.IsNullOrEmpty(namePart))
                return new XdmSequenceType { ItemType = ItemType.Attribute, Occurrence = occurrence };
            var (attrLocal, attrNs) = SplitPrefixedName(namePart, context);
            return new XdmSequenceType
            {
                ItemType = ItemType.Attribute,
                Occurrence = occurrence,
                AttributeName = attrLocal,
                AttributeNamespace = attrNs,
            };
        }

        // Handle schema-element(name) — schema-aware element test. The provider
        // supplies substitution-group / type-annotation matching at runtime.
        if (type.StartsWith("schema-element(", StringComparison.Ordinal) && type.EndsWith(')'))
        {
            var inner = type["schema-element(".Length..^1].Trim();
            var (localName, nsUri) = SplitPrefixedName(inner, context);
            return new XdmSequenceType
            {
                ItemType = ItemType.SchemaElement,
                Occurrence = occurrence,
                SchemaElementName = localName,
                SchemaElementNamespace = nsUri,
            };
        }

        // Handle schema-attribute(name)
        if (type.StartsWith("schema-attribute(", StringComparison.Ordinal) && type.EndsWith(')'))
        {
            var inner = type["schema-attribute(".Length..^1].Trim();
            var (localName, nsUri) = SplitPrefixedName(inner, context);
            return new XdmSequenceType
            {
                ItemType = ItemType.SchemaAttribute,
                Occurrence = occurrence,
                SchemaAttributeName = localName,
                SchemaAttributeNamespace = nsUri,
            };
        }

        // Handle parameterized array(TYPE) and map(KEY, VALUE) forms
        if (type.StartsWith("array(", StringComparison.Ordinal) && type.EndsWith(')'))
            return new XdmSequenceType { ItemType = ItemType.Array, Occurrence = occurrence };
        if (type.StartsWith("map(", StringComparison.Ordinal) && type.EndsWith(')'))
            return new XdmSequenceType { ItemType = ItemType.Map, Occurrence = occurrence };

        // Handle function types: function(*), function(T1, T2, ...) as ReturnType
        if (type.StartsWith("function(", StringComparison.Ordinal) && type != "function(*)")
        {
            // Find matching closing paren for the parameter list (start after opening '(' at index 8)
            int depth = 0;
            int closeIdx = -1;
            for (int fi = 9; fi < type.Length; fi++)
            {
                if (type[fi] == '(') depth++;
                else if (type[fi] == ')')
                {
                    if (depth == 0) { closeIdx = fi; break; }
                    depth--;
                }
            }
            if (closeIdx > 0)
            {
                var paramsPart = type[9..closeIdx].Trim();
                var afterParen = type[(closeIdx + 1)..].Trim();
                if (paramsPart.Length > 0)
                {
                    // Has parameter types — return type is required (XPST0003)
                    if (!afterParen.StartsWith("as ", StringComparison.OrdinalIgnoreCase))
                        throw new XsltException("XPST0003: Function type with parameter types requires 'as ReturnType'");

                    // Parse parameter types
                    var paramTypes = ParseFunctionParameterTypes(paramsPart, context);
                    // Parse return type (skip "as ")
                    var returnTypeStr = afterParen[3..].Trim();
                    var returnType = ParseSequenceType(returnTypeStr, context);

                    return new XdmSequenceType
                    {
                        ItemType = ItemType.Function,
                        Occurrence = occurrence,
                        FunctionParameterTypes = paramTypes,
                        FunctionReturnType = returnType
                    };
                }
                // function() as ReturnType — zero-arity typed function
                if (afterParen.StartsWith("as ", StringComparison.OrdinalIgnoreCase))
                {
                    var returnTypeStr = afterParen[3..].Trim();
                    var returnType = ParseSequenceType(returnTypeStr, context);
                    return new XdmSequenceType
                    {
                        ItemType = ItemType.Function,
                        Occurrence = occurrence,
                        FunctionParameterTypes = Array.Empty<XdmSequenceType>(),
                        FunctionReturnType = returnType
                    };
                }
                return new XdmSequenceType { ItemType = ItemType.Function, Occurrence = occurrence };
            }
        }

        var itemType = type switch
        {
            "item()" => ItemType.Item,
            "node()" => ItemType.Node,
            "element()" or "element(*)" => ItemType.Element,
            "attribute()" or "attribute(*)" => ItemType.Attribute,
            "text()" => ItemType.Text,
            "document-node()" => ItemType.Document,
            "comment()" => ItemType.Comment,
            "processing-instruction()" => ItemType.ProcessingInstruction,
            "namespace-node()" => ItemType.Node,
            "xs:string" => ItemType.String,
            "xs:integer" => ItemType.Integer,
            "xs:decimal" => ItemType.Decimal,
            "xs:double" => ItemType.Double,
            "xs:float" => ItemType.Float,
            "xs:boolean" => ItemType.Boolean,
            "xs:date" => ItemType.Date,
            "xs:dateTime" => ItemType.DateTime,
            "xs:time" => ItemType.Time,
            "xs:anyAtomicType" => ItemType.AnyAtomicType,
            "xs:untypedAtomic" => ItemType.UntypedAtomic,
            "xs:anyURI" => ItemType.AnyUri,
            "xs:QName" => ItemType.QName,
            "xs:duration" => ItemType.Duration,
            "xs:yearMonthDuration" => ItemType.YearMonthDuration,
            "xs:dayTimeDuration" => ItemType.DayTimeDuration,
            "xs:gYearMonth" => ItemType.GYearMonth,
            "xs:gYear" => ItemType.GYear,
            "xs:gMonthDay" => ItemType.GMonthDay,
            "xs:gDay" => ItemType.GDay,
            "xs:gMonth" => ItemType.GMonth,
            "xs:hexBinary" => ItemType.HexBinary,
            "xs:base64Binary" => ItemType.Base64Binary,
            // Derived string types — all subtypes of xs:string
            "xs:normalizedString" or "xs:token" or "xs:language" or "xs:NMTOKEN"
                or "xs:Name" or "xs:NCName" or "xs:ID" or "xs:IDREF" or "xs:ENTITY" => ItemType.String,
            // Derived integer types — all subtypes of xs:integer
            "xs:long" or "xs:int" or "xs:short" or "xs:byte"
                or "xs:nonNegativeInteger" or "xs:positiveInteger"
                or "xs:nonPositiveInteger" or "xs:negativeInteger"
                or "xs:unsignedLong" or "xs:unsignedInt" or "xs:unsignedShort" or "xs:unsignedByte" => ItemType.Integer,
            "xs:NOTATION" => ItemType.AnyAtomicType,
            "map(*)" => ItemType.Map,
            "array(*)" => ItemType.Array,
            "function(*)" => ItemType.Function,
            _ => ItemType.Item
        };

        return new XdmSequenceType { ItemType = itemType, Occurrence = occurrence };
    }

    /// <summary>
    /// Parses comma-separated function parameter types like "xs:string, xs:integer" or
    /// "xs:anyAtomicType?, item()*". Respects nested parentheses so types like
    /// "function(xs:string) as xs:boolean" are not split incorrectly.
    /// </summary>
    private static List<XdmSequenceType> ParseFunctionParameterTypes(string paramsPart, XElement? context)
    {
        var result = new List<XdmSequenceType>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < paramsPart.Length; i++)
        {
            if (paramsPart[i] == '(') depth++;
            else if (paramsPart[i] == ')') depth--;
            else if (paramsPart[i] == ',' && depth == 0)
            {
                result.Add(ParseSequenceType(paramsPart[start..i].Trim(), context));
                start = i + 1;
            }
        }
        if (start < paramsPart.Length)
            result.Add(ParseSequenceType(paramsPart[start..].Trim(), context));
        return result;
    }

    private static ItemType ParseAtomicItemType(string type) => type switch
    {
        "xs:string" => ItemType.String,
        "xs:integer" => ItemType.Integer,
        "xs:decimal" => ItemType.Decimal,
        "xs:double" => ItemType.Double,
        "xs:float" => ItemType.Float,
        "xs:boolean" => ItemType.Boolean,
        "xs:date" => ItemType.Date,
        "xs:dateTime" => ItemType.DateTime,
        "xs:time" => ItemType.Time,
        "xs:anyAtomicType" => ItemType.AnyAtomicType,
        "xs:untypedAtomic" => ItemType.UntypedAtomic,
        "xs:anyURI" => ItemType.AnyUri,
        "xs:QName" => ItemType.QName,
        "xs:duration" => ItemType.Duration,
        "xs:yearMonthDuration" => ItemType.YearMonthDuration,
        "xs:dayTimeDuration" => ItemType.DayTimeDuration,
        "xs:gYearMonth" => ItemType.GYearMonth,
        "xs:gYear" => ItemType.GYear,
        "xs:gMonthDay" => ItemType.GMonthDay,
        "xs:gDay" => ItemType.GDay,
        "xs:gMonth" => ItemType.GMonth,
        "xs:hexBinary" => ItemType.HexBinary,
        "xs:base64Binary" => ItemType.Base64Binary,
        _ => ItemType.Item
    };

    private static bool? ParseYesNo(XAttribute? attr)
    {
        if (attr == null) return null;

        // XSLT 3.0: xsl:yes-or-no accepts "yes", "no", "true", "false", "1", "0"
        // with leading/trailing whitespace stripped
        return attr.Value.Trim() switch
        {
            "yes" or "true" or "1" => true,
            "no" or "false" or "0" => false,
            _ => null
        };
    }

    private static string? GetLiteralAvtValue(XsltAttributeValueTemplate avt)
    {
        if (avt.Parts.Count == 1 && avt.Parts[0] is AvtLiteral lit)
            return lit.Value;
        return null;
    }

    private static Ast.ValidationMode? ParseValidationMode(XAttribute? attr)
    {
        return attr?.Value switch
        {
            "strict" => Ast.ValidationMode.Strict,
            "lax" => Ast.ValidationMode.Lax,
            "preserve" => Ast.ValidationMode.Preserve,
            "strip" => Ast.ValidationMode.Strip,
            _ => null
        };
    }

    /// <summary>
    /// Parses a yes/no/true/false/1/0 attribute value with XTSE0020 validation.
    /// Returns true for "yes"/"true"/"1", false for "no"/"false"/"0"/null.
    /// </summary>
    private static bool ParseYesNoBoolean(XAttribute? attr, string attrName, SourceLocation? location)
    {
        if (attr == null) return false;
        var val = attr.Value.Trim();
        return val switch
        {
            "yes" or "true" or "1" => true,
            "no" or "false" or "0" => false,
            _ => throw new XsltException($"XTSE0020: Invalid value '{attr.Value}' for {attrName} attribute: must be 'yes', 'no', 'true', 'false', '1', or '0'", location)
        };
    }

    /// <summary>
    /// Formats a QName for display in error messages. Prefers <c>prefix:local</c>, falls back
    /// to <c>Q{uri}local</c> for prefix-less names that carry a non-default namespace, and uses
    /// the bare local name otherwise.
    /// </summary>
    private static string FormatVariableName(QName name)
    {
        if (!string.IsNullOrEmpty(name.Prefix))
            return name.PrefixedName;
        var ns = name.ResolvedNamespace;
        if (!string.IsNullOrEmpty(ns))
            return $"Q{{{ns}}}{name.LocalName}";
        return name.LocalName;
    }

    private static SourceLocation? GetSourceLocation(XElement element)
    {
        if (element is IXmlLineInfo lineInfo && lineInfo.HasLineInfo())
        {
            return new SourceLocation(lineInfo.LineNumber, lineInfo.LinePosition, 0, 0)
            {
                Module = string.IsNullOrEmpty(element.BaseUri) ? null : element.BaseUri,
            };
        }
        // Even without line info, BaseUri is still useful diagnostic context.
        return string.IsNullOrEmpty(element.BaseUri)
            ? null
            : new SourceLocation(0, 0, 0, 0) { Module = element.BaseUri };
    }

    private XsltWherePopulated ParseWherePopulated(XElement element, SourceLocation? location)
    {
        return new XsltWherePopulated
        {
            Location = location,
            Content = ParseSequenceConstructor(element)
        };
    }

    private XsltOnEmpty ParseOnEmpty(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select");
        return new XsltOnEmpty
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = element.Nodes().Any() ? ParseSequenceConstructor(element) : null
        };
    }

    private XsltEvaluate ParseEvaluate(XElement element, SourceLocation? location)
    {
        var xpathAttr = element.Attribute("xpath")
            ?? throw new XsltException("XTSE0010: xsl:evaluate requires an xpath attribute", location);
        var contextItemAttr = element.Attribute("context-item");
        var baseUriAttr = element.Attribute("base-uri");
        var nsContextAttr = element.Attribute("namespace-context");
        var withParamsAttr = element.Attribute("with-params");
        var asAttr = element.Attribute("as");
        var collationAttr = element.Attribute("default-collation");

        // Parse with-param children
        var withParams = new List<XsltWithParam>();
        foreach (var child in element.Elements())
        {
            if (child.Name == XsltNs + "with-param")
                withParams.Add(ParseWithParam(child));
            else if (child.Name == XsltNs + "fallback")
            { /* handled below */ }
        }

        var fallbackElem = element.Elements()
            .FirstOrDefault(e => e.Name == XsltNs + "fallback");

        // Collect in-scope namespace bindings from the xsl:evaluate element
        // These serve as default namespace context when namespace-context is not specified
        var defaultNsBindings = new Dictionary<string, string>();
        {
            // Walk up from the element to collect all in-scope namespace declarations
            var current = element;
            while (current != null)
            {
                foreach (var attr in current.Attributes())
                {
                    if (attr.IsNamespaceDeclaration)
                    {
                        var prefix = attr.Name.LocalName == "xmlns" ? "" : attr.Name.LocalName;
                        var uri = attr.Value;
                        if (!defaultNsBindings.ContainsKey(prefix) && prefix != "xml" && uri != XsltNs.NamespaceName)
                            defaultNsBindings[prefix] = uri;
                    }
                }
                current = current.Parent as XElement;
            }
        }

        return new XsltEvaluate
        {
            Location = location,
            Xpath = ParseExpr(xpathAttr.Value, xpathAttr),
            ContextItem = contextItemAttr != null ? ParseExpr(contextItemAttr.Value, contextItemAttr) : null,
            BaseUri = baseUriAttr != null ? ParseAvt(baseUriAttr.Value, element, baseUriAttr) : null,
            NamespaceContext = nsContextAttr != null ? ParseExpr(nsContextAttr.Value, nsContextAttr) : null,
            WithParamsExpr = withParamsAttr != null ? ParseExpr(withParamsAttr.Value, withParamsAttr) : null,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            EvaluateDefaultCollation = collationAttr?.Value,
            WithParams = withParams,
            Fallback = fallbackElem != null ? ParseSequenceConstructor(fallbackElem) : null,
            DefaultNamespaceBindings = defaultNsBindings,
            XpathDefaultNamespace = GetXpathDefaultNamespace(element),
        };
    }

    private XsltOnNonEmpty ParseOnNonEmpty(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select");
        return new XsltOnNonEmpty
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = element.Nodes().Any() ? ParseSequenceConstructor(element) : null
        };
    }

    /// <summary>
    /// Resolves XSLT 3.0 shadow attributes (section 3.6.2).
    /// Shadow attributes use the form _foo="{$param}" where the value is evaluated using
    /// static parameters, and the result replaces the real attribute foo.
    /// </summary>
    private static void ResolveShadowAttributes(XElement root, Dictionary<string, string>? externalStaticParams = null, Uri? explicitBaseUri = null)
    {
        // Collect static params AND variables from top-level elements
        var staticParams = new Dictionary<string, string>();

        // Also collect from imported stylesheets (process xsl:import/xsl:include first)
        var baseUri = root.BaseUri;
        Uri? baseUriObj = explicitBaseUri;
        if (baseUriObj == null && !string.IsNullOrEmpty(baseUri))
            Uri.TryCreate(baseUri, UriKind.Absolute, out baseUriObj);
        if (baseUriObj != null)
        {
            foreach (var child in root.Elements())
            {
                if (child.Name == XsltNs + "import" || child.Name == XsltNs + "include")
                {
                    var href = child.Attribute("href")?.Value;
                    if (href == null) continue;
                    try
                    {
                        var resolvedUri = new Uri(baseUriObj, href);
                        if (resolvedUri.IsFile && File.Exists(resolvedUri.LocalPath))
                        {
                            var importedDoc = XDocument.Load(resolvedUri.LocalPath, LoadOptions.SetBaseUri | LoadOptions.SetLineInfo);
                            if (importedDoc.Root != null)
                                CollectStaticDeclarations(importedDoc.Root, staticParams);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or XmlException or UriFormatException or UnauthorizedAccessException or FileNotFoundException)
                    {
                        // If import fails here, skip — it will be handled properly during parsing
                    }
                }
            }
        }

        CollectStaticDeclarations(root, staticParams, checkConsistency: true);

        // External static params override defaults (higher precedence)
        if (externalStaticParams != null)
        {
            foreach (var (name, value) in externalStaticParams)
            {
                var val = value.Trim();
                if (val.StartsWith('\'') && val.EndsWith('\''))
                    staticParams[name] = val[1..^1].Replace("''", "'", StringComparison.Ordinal);
                else if (val.StartsWith('"') && val.EndsWith('"'))
                    staticParams[name] = val[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
                else if (val is "true()" or "false()")
                    staticParams[name] = val == "true()" ? "yes" : "no";
                else
                    staticParams[name] = val;
            }
        }

        // Walk all elements and resolve shadow attributes
        // (even with no static params, shadow attributes need validation for XPST0017)
        ResolveShadowAttributesRecursive(root, staticParams);
    }

    /// <summary>
    /// Collects static param and variable declarations from a stylesheet root element.
    /// </summary>
    private static void CollectStaticDeclarations(XElement root, Dictionary<string, string> staticParams, bool checkConsistency = false)
    {
        // Track names seen in THIS module for same-precedence consistency check
        HashSet<string>? seenInModule = checkConsistency ? new() : null;
        foreach (var child in root.Elements())
        {
            if (child.Name != XsltNs + "param" && child.Name != XsltNs + "variable")
                continue;

            var staticAttr = child.Attribute("static");
            var isStatic = staticAttr?.Value?.Trim() is "yes" or "true" or "1";

            // Also check for _static shadow attribute (e.g., _static="{if ...}")
            if (!isStatic)
            {
                var shadowStatic = child.Attribute("_static");
                if (shadowStatic != null)
                {
                    var resolved = ResolveShadowValue(shadowStatic.Value, staticParams);
                    isStatic = resolved.Trim() is "yes" or "true" or "1";
                }
            }

            if (!isStatic) continue;

            var nameAttr = child.Attribute("name")?.Value;
            if (nameAttr == null) continue;

            var selectAttr = child.Attribute("select")?.Value;
            string? resolvedValue = null;
            if (selectAttr != null)
            {
                var val = selectAttr.Trim();
                if (val.StartsWith('\'') && val.EndsWith('\''))
                    resolvedValue = val[1..^1].Replace("''", "'", StringComparison.Ordinal);
                else if (val.StartsWith('"') && val.EndsWith('"'))
                    resolvedValue = val[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
                else if (val is "true()" or "false()")
                    resolvedValue = val == "true()" ? "yes" : "no";
                else
                    resolvedValue = val;
            }

            if (staticParams.TryGetValue(nameAttr, out var existing))
            {
                // XTSE3450: Only check consistency for same import precedence (same module)
                if (seenInModule != null && seenInModule.Contains(nameAttr) && resolvedValue != null && existing != resolvedValue)
                    throw new XsltException($"XTSE3450: Static variable '{nameAttr}' has value '{resolvedValue}' which is inconsistent with the value '{existing}' at the same import precedence");
                // Higher precedence wins — override
                if (resolvedValue != null)
                    staticParams[nameAttr] = resolvedValue;
                seenInModule?.Add(nameAttr);
                continue;
            }

            seenInModule?.Add(nameAttr);
            if (resolvedValue != null)
                staticParams[nameAttr] = resolvedValue;
        }
    }

    private static void ResolveShadowAttributesRecursive(XElement element, Dictionary<string, string> staticParams)
    {
        // Shadow attributes only apply to XSLT elements, not LREs (§3.9.3)
        if (element.Name.Namespace == XsltNs)
        {
            // Find shadow attributes (unprefixed attributes starting with underscore)
            var shadowAttrs = element.Attributes()
                .Where(a => a.Name.Namespace == XNamespace.None && a.Name.LocalName.StartsWith('_') && a.Name.LocalName.Length > 1)
                .ToList();

            foreach (var shadow in shadowAttrs)
            {
                var realName = shadow.Name.LocalName[1..]; // Remove leading underscore
                var value = ResolveShadowValue(shadow.Value, staticParams);

                // Set the real attribute (overriding any existing value)
                element.SetAttributeValue(realName, value);

                // Remove the shadow attribute
                shadow.Remove();
            }
        }

        // Recurse into children
        foreach (var child in element.Elements())
        {
            ResolveShadowAttributesRecursive(child, staticParams);
        }
    }

    private static string ResolveShadowValue(string template, Dictionary<string, string> staticParams)
    {
        // Process static AVT: {$name} → variable value, {{/}} → literal {/}, {...} → expression result
        var result = new System.Text.StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                // {{ → literal {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    result.Append('{');
                    i += 2;
                    continue;
                }

                // Find closing brace (skipping braces inside string literals)
                var end = FindClosingBrace(template, i + 1);
                if (end > 0)
                {
                    var expr = template[(i + 1)..end];
                    if (expr.Length == 0)
                    {
                        // {} → empty expression, produces empty string
                    }
                    else if (expr.StartsWith('$'))
                    {
                        // {$name} → variable reference
                        var paramName = expr[1..];
                        if (staticParams.TryGetValue(paramName, out var paramValue))
                            result.Append(paramValue);
                    }
                    else
                    {
                        // Try to evaluate the expression statically
                        var evaluated = EvaluateShadowExpression(expr, staticParams);
                        if (evaluated != null)
                            result.Append(evaluated);
                    }
                    i = end + 1;
                    continue;
                }
            }
            else if (template[i] == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                // }} → literal }
                result.Append('}');
                i += 2;
                continue;
            }
            result.Append(template[i]);
            i++;
        }
        return result.ToString();
    }

    /// <summary>
    /// Finds the closing '}' brace, skipping braces inside string literals.
    /// </summary>
    private static int FindClosingBrace(string template, int start)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        for (var j = start; j < template.Length; j++)
        {
            var c = template[j];
            if (c == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
            else if (c == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;
            else if (c == '}' && !inSingleQuote && !inDoubleQuote) return j;
        }
        return -1;
    }

    /// <summary>
    /// Evaluates a simple static expression in a shadow attribute.
    /// Supports string literals, numeric literals, $variables, || concatenation, and QName().
    /// </summary>
    private static string? EvaluateShadowExpression(string expr, Dictionary<string, string> staticParams)
    {
        expr = expr.Trim();

        // String concatenation: split on || and evaluate each side
        // Must handle cases like 'a' || 'b' || $var
        if (expr.Contains("||", StringComparison.Ordinal))
        {
            var parts = SplitOnOperator(expr, "||");
            if (parts != null)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var part in parts)
                {
                    var partVal = EvaluateShadowExpression(part.Trim(), staticParams);
                    if (partVal == null) return null;
                    sb.Append(partVal);
                }
                return sb.ToString();
            }
        }

        // Variable reference
        if (expr.StartsWith('$'))
        {
            var paramName = expr[1..];
            return staticParams.TryGetValue(paramName, out var val) ? val : null;
        }

        // String literal
        if ((expr.StartsWith('\'') && expr.EndsWith('\'')) ||
            (expr.StartsWith('"') && expr.EndsWith('"')))
        {
            return expr[1..^1];
        }

        // Numeric literal
        if (double.TryParse(expr, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return expr;
        }

        // QName function: QName('ns', 'prefix:local') → prefix:local
        if (expr.StartsWith("QName(", StringComparison.Ordinal) && expr.EndsWith(')'))
        {
            var inner = expr[6..^1];
            var commaIdx = FindCommaOutsideQuotes(inner);
            if (commaIdx > 0)
            {
                var localPart = inner[(commaIdx + 1)..].Trim();
                return EvaluateShadowExpression(localPart, staticParams);
            }
        }

        // system-property('xsl:version') → '3.0', etc.
        if (expr.StartsWith("system-property(", StringComparison.Ordinal) && expr.EndsWith(')'))
        {
            var argExpr = expr[16..^1].Trim();
            var argVal = EvaluateShadowExpression(argExpr, staticParams);
            if (argVal != null)
            {
                // Strip 'xsl:' prefix — system-property expects XSLT namespace properties
                var propName = argVal;
                if (propName.Contains(':', StringComparison.Ordinal))
                    propName = propName[(propName.IndexOf(':', StringComparison.Ordinal) + 1)..];
                return propName switch
                {
                    "version" => "3.0",
                    "vendor" => "PhoenixmlDb",
                    "vendor-url" => "https://endpointsystems.com",
                    "product-name" => "PhoenixmlDb XSLT",
                    "product-version" => "1.0",
                    "is-schema-aware" => "no",
                    "supports-serialization" => "yes",
                    "supports-backwards-compatibility" => "yes",
                    "supports-namespace-axis" => "yes",
                    "supports-streaming" => "yes",
                    "supports-dynamic-evaluation" => "yes",
                    "supports-higher-order-functions" => "yes",
                    "xpath-version" => "4.0",
                    "xsd-version" => "1.1",
                    _ => ""
                };
            }
        }

        // if (...) then ... else ... conditional expression
        if (expr.StartsWith("if", StringComparison.Ordinal) && expr.Length > 2 &&
            (expr[2] == ' ' || expr[2] == '('))
        {
            // Find the condition in parentheses
            var condStart = expr.IndexOf('(', StringComparison.Ordinal);
            if (condStart >= 0)
            {
                var condEnd = FindMatchingParen(expr, condStart);
                if (condEnd > condStart)
                {
                    var condExpr = expr[(condStart + 1)..condEnd].Trim();
                    var rest = expr[(condEnd + 1)..].Trim();

                    // Find 'then' and 'else' keywords
                    var thenIdx = FindKeyword(rest, "then");
                    if (thenIdx >= 0)
                    {
                        var afterThen = rest[(thenIdx + 4)..].Trim();
                        var elseIdx = FindKeyword(afterThen, "else");
                        if (elseIdx >= 0)
                        {
                            var thenExpr = afterThen[..elseIdx].Trim();
                            var elseExpr = afterThen[(elseIdx + 4)..].Trim();

                            var condResult = EvaluateStaticCondition(condExpr, staticParams);
                            if (condResult.HasValue)
                            {
                                return condResult.Value
                                    ? EvaluateShadowExpression(thenExpr, staticParams)
                                    : EvaluateShadowExpression(elseExpr, staticParams);
                            }
                        }
                    }
                }
            }
        }

        // Check for XSLT runtime functions that are NOT available in static expressions (XPST0017)
        var parenIdx = expr.IndexOf('(', StringComparison.Ordinal);
        if (parenIdx > 0 && expr.EndsWith(')'))
        {
            var funcName = expr[..parenIdx].Trim();
            var localName = funcName.Contains(':', StringComparison.Ordinal)
                ? funcName[(funcName.IndexOf(':', StringComparison.Ordinal) + 1)..] : funcName;
            if (localName.Length > 0 && !localName.Contains(' ', StringComparison.Ordinal))
            {
                // XSLT runtime-only functions that cannot appear in static expressions
                var isRuntimeOnly = localName is "current" or "current-group" or "current-grouping-key"
                    or "current-merge-group" or "current-merge-key" or "current-output-uri"
                    or "regex-group" or "unparsed-entity-uri" or "unparsed-entity-public-id"
                    or "accumulator-before" or "accumulator-after" or "snapshot" or "copy-of";
                if (isRuntimeOnly)
                    throw new XsltException($"XPST0017: Function '{funcName}' is not available in a static expression (shadow attribute)");
            }
        }

        // For unrecognized expressions (including XPath function calls we can't evaluate inline),
        // return null — the shadow attribute value will use whatever was already accumulated.
        return null; // Cannot evaluate
    }

    /// <summary>
    /// Finds a matching closing parenthesis, respecting nesting and string literals.
    /// </summary>
    private static int FindMatchingParen(string expr, int openPos)
    {
        var depth = 0;
        var inSingle = false;
        var inDouble = false;
        for (var i = openPos; i < expr.Length; i++)
        {
            var c = expr[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (!inSingle && !inDouble)
            {
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth == 0) return i; }
            }
        }
        return -1;
    }

    /// <summary>
    /// Finds a keyword in an expression, ensuring it's a word boundary (not inside a string or parentheses).
    /// </summary>
    private static int FindKeyword(string expr, string keyword)
    {
        var inSingle = false;
        var inDouble = false;
        var parenDepth = 0;
        for (var i = 0; i <= expr.Length - keyword.Length; i++)
        {
            var c = expr[i];
            if (c == '\'' && !inDouble) { inSingle = !inSingle; continue; }
            if (c == '"' && !inSingle) { inDouble = !inDouble; continue; }
            if (inSingle || inDouble) continue;
            if (c == '(') { parenDepth++; continue; }
            if (c == ')') { parenDepth--; continue; }
            if (parenDepth > 0) continue;
            if (expr.AsSpan(i, keyword.Length).SequenceEqual(keyword.AsSpan()) &&
                (i == 0 || !char.IsLetterOrDigit(expr[i - 1])) &&
                (i + keyword.Length >= expr.Length || !char.IsLetterOrDigit(expr[i + keyword.Length])))
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Evaluates a simple static condition (e.g., "system-property('xsl:version') = '3.0'").
    /// </summary>
    private static bool? EvaluateStaticCondition(string condExpr, Dictionary<string, string> staticParams)
    {
        condExpr = condExpr.Trim();

        // Handle comparison: lhs = rhs or lhs != rhs
        var eqIdx = FindKeyword(condExpr, "=");
        if (eqIdx > 0)
        {
            // Check for != (the char before = is !)
            var isNotEqual = eqIdx > 0 && condExpr[eqIdx - 1] == '!';
            var lhsStr = isNotEqual ? condExpr[..(eqIdx - 1)].Trim() : condExpr[..eqIdx].Trim();
            var rhsStr = condExpr[(eqIdx + 1)..].Trim();

            var lhs = EvaluateShadowExpression(lhsStr, staticParams);
            var rhs = EvaluateShadowExpression(rhsStr, staticParams);

            if (lhs != null && rhs != null)
                return isNotEqual ? lhs != rhs : lhs == rhs;
        }

        // Handle boolean function result
        var val = EvaluateShadowExpression(condExpr, staticParams);
        if (val != null)
            return val is "yes" or "true" or "1";

        return null;
    }

    /// <summary>
    /// Splits an expression on a binary operator, respecting string literal boundaries.
    /// </summary>
    private static List<string>? SplitOnOperator(string expr, string op)
    {
        var parts = new List<string>();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var parenDepth = 0;
        var lastSplit = 0;

        for (var j = 0; j < expr.Length; j++)
        {
            var c = expr[j];
            if (c == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
            else if (c == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;
            else if (c == '(' && !inSingleQuote && !inDoubleQuote) parenDepth++;
            else if (c == ')' && !inSingleQuote && !inDoubleQuote) parenDepth--;
            else if (!inSingleQuote && !inDoubleQuote && parenDepth == 0 &&
                     j + op.Length <= expr.Length && expr.AsSpan(j, op.Length).SequenceEqual(op.AsSpan()))
            {
                parts.Add(expr[lastSplit..j]);
                lastSplit = j + op.Length;
                j += op.Length - 1;
            }
        }
        parts.Add(expr[lastSplit..]);
        return parts.Count > 1 ? parts : null;
    }

    private static int FindCommaOutsideQuotes(string s)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        for (var j = 0; j < s.Length; j++)
        {
            var c = s[j];
            if (c == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
            else if (c == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;
            else if (c == ',' && !inSingleQuote && !inDoubleQuote) return j;
        }
        return -1;
    }

    /// <summary>
    /// Safely evaluates a static param/variable's select expression.
    /// Returns the evaluated value or null if evaluation fails (e.g., complex expressions).
    /// </summary>
    private object? EvaluateStaticSelectSafe(XQueryExpression expr, XElement context)
    {
        try
        {
            return EvaluateStaticExpression(expr, context);
        }
        catch (XsltException ex) when (ex.Message.StartsWith("XPST0008", StringComparison.Ordinal))
        {
            // Forward reference to undeclared static variable — must be a real error
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or ArgumentException or XsltException)
        {
            return null;
        }
    }

    // ── use-when static evaluation ──────────────────────────────────────

    /// <summary>
    /// Checks whether an element should be included based on its use-when attribute.
    /// XSLT elements use <c>use-when="expr"</c>; literal result elements use <c>xsl:use-when="expr"</c>.
    /// Returns true if the element should be included (no use-when attribute, or it evaluates to true).
    /// </summary>
    private bool ShouldIncludeElement(XElement element)
    {
        string? useWhenExpr;
        bool hasPrefixedUseWhen = false;
        if (element.Name.Namespace == XsltNs)
        {
            // On XSLT elements, only unprefixed use-when is processed.
            // xsl:use-when (prefixed) is an error (XTSE0090), but the check is deferred:
            // if the unprefixed use-when excludes the element, no error is raised.
            hasPrefixedUseWhen = element.Attribute(XsltNs + "use-when") != null;
            useWhenExpr = element.Attribute("use-when")?.Value;
            if (hasPrefixedUseWhen && useWhenExpr == null)
                throw new XsltException("XTSE0090: Attribute 'xsl:use-when' in the XSLT namespace is not permitted on an XSLT element (use unprefixed 'use-when' instead)",
                    GetSourceLocation(element));
        }
        else
        {
            useWhenExpr = element.Attribute(XsltNs + "use-when")?.Value;
        }

        if (useWhenExpr == null)
            return true;

        try
        {
            var expr = _expressionParser.Parse(useWhenExpr);
            ResolveExpressionNamespaces(expr, element);
            var result = EvaluateStaticExpression(expr, element);
            var include = CoerceToBoolean(result);
            // XTSE0090: If the element is included and has xsl:use-when (prefixed), raise error
            if (include && hasPrefixedUseWhen)
                throw new XsltException("XTSE0090: Attribute 'xsl:use-when' in the XSLT namespace is not permitted on an XSLT element (use unprefixed 'use-when' instead)",
                    GetSourceLocation(element));
            return include;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            // If we can't evaluate the use-when expression, include the element
            // (it will fail later with a proper error if the expression is actually needed)
            return true;
        }
        catch (XsltException)
        {
            // In forwards-compatible mode, if a use-when expression on an XSLT element
            // fails (e.g. unknown function, undeclared prefix), exclude the element
            if (element.Name.Namespace == XsltNs)
            {
                var ver = element.Attribute("version")?.Value ?? GetEffectiveVersion(element);
                if (ParseVersionNumber(ver) > 3.0m)
                    return false;
            }
            throw;
        }
    }

    /// <summary>
    /// Statically evaluates an XPath expression in the use-when context.
    /// Only a limited set of expressions are supported (literals, comparisons,
    /// boolean operators, and system functions like element-available, system-property, etc.).
    /// </summary>
    private object? EvaluateStaticExpression(XQueryExpression expr, XElement context)
    {
        switch (expr)
        {
            case BooleanLiteral bl:
                return bl.Value;

            case IntegerLiteral il:
                return il.Value is long lv ? (double)lv : Convert.ToDouble(il.Value, System.Globalization.CultureInfo.InvariantCulture);

            case DecimalLiteral dl:
                return (double)dl.Value;

            case DoubleLiteral dbl:
                return dbl.Value;

            case StringLiteral sl:
                return sl.Value;

            case EmptySequence:
                return null;

            case UnaryExpression ue:
                var operand = EvaluateStaticExpression(ue.Operand, context);
                if (ue.Operator == UnaryOperator.Not)
                    return !CoerceToBoolean(operand);
                return ue.Operator == UnaryOperator.Minus ? -ToDouble(operand) : ToDouble(operand);

            case BinaryExpression be:
                return EvaluateStaticBinary(be, context);

            case FunctionCallExpression fc:
                return EvaluateStaticFunction(fc, context);

            case SequenceExpression seq:
                var items = new List<object?>();
                foreach (var item in seq.Items)
                {
                    var val = EvaluateStaticExpression(item, context);
                    if (val is List<object?> subList)
                        items.AddRange(subList);
                    else
                        items.Add(val);
                }
                return items;

            case RangeExpression re:
                var start = (int)ToDouble(EvaluateStaticExpression(re.Start, context));
                var end = (int)ToDouble(EvaluateStaticExpression(re.End, context));
                var range = new List<object?>();
                for (int i = start; i <= end; i++)
                    range.Add((double)i);
                return range;

            case QuantifiedExpression qe:
                return EvaluateStaticQuantified(qe, context, _staticVariables);

            case IfExpression ie:
                return CoerceToBoolean(EvaluateStaticExpression(ie.Condition, context))
                    ? EvaluateStaticExpression(ie.Then, context)
                    : ie.Else != null ? EvaluateStaticExpression(ie.Else, context) : null;

            case VariableReference vr:
                if (_staticVariables.TryGetValue(vr.Name, out var varVal))
                    return varVal;
                // Show the variable's display form (prefix:local or Q{uri}local) so users can
                // tell a prefixed reference apart from an unprefixed one — critical when
                // diagnosing namespace mismatches (e.g. $v:debug vs $debug).
                throw new XsltException(
                    $"XPST0008: Variable ${FormatVariableName(vr.Name)} is not declared in the static use-when context",
                    GetSourceLocation(context));

            case InstanceOfExpression instOf:
            {
                var value = EvaluateStaticExpression(instOf.Expression, context);
                return StaticInstanceOf(value, instOf.TargetType);
            }

            case ContextItemExpression:
                throw new XsltException("XPDY0002: Context item (.) is not available in static use-when context");

            // Path/step expressions access the context item, which is absent in use-when
            case PathExpression:
            case StepExpression:
                throw new XsltException("XPDY0002: Context item is not available in static use-when context (path expressions require a context item)");

            default:
                throw new InvalidOperationException($"Cannot statically evaluate expression: {expr}");
        }
    }

    /// <summary>
    /// Static evaluation of "instance of" in use-when context.
    /// Only handles atomic type checks on static values (strings, numbers, booleans).
    /// </summary>
    private static bool StaticInstanceOf(object? value, XdmSequenceType targetType)
    {
        // Check occurrence: null = empty sequence
        if (value == null)
            return targetType.Occurrence is Occurrence.ZeroOrMore or Occurrence.ZeroOrOne;

        // Single item check
        return targetType.ItemType switch
        {
            ItemType.Item => true,
            ItemType.AnyAtomicType => value is string or double or bool or int or long or decimal,
            ItemType.String => value is string,
            ItemType.Boolean => value is bool,
            ItemType.Integer => value is int or long || (value is double d && d == Math.Floor(d) && !double.IsInfinity(d) && !double.IsNaN(d)),
            ItemType.Decimal => value is decimal or int or long || (value is double d2 && !double.IsInfinity(d2) && !double.IsNaN(d2)),
            ItemType.Double => value is double,
            ItemType.Float => value is float or double,
            ItemType.Node or ItemType.Element or ItemType.Attribute or ItemType.Document
                or ItemType.Text or ItemType.Comment or ItemType.ProcessingInstruction => false, // static values are never nodes
            _ => false
        };
    }

    private object? EvaluateStaticBinary(BinaryExpression be, XElement context)
    {
        // Short-circuit for and/or
        if (be.Operator == BinaryOperator.And)
        {
            var left = CoerceToBoolean(EvaluateStaticExpression(be.Left, context));
            return left && CoerceToBoolean(EvaluateStaticExpression(be.Right, context));
        }
        if (be.Operator == BinaryOperator.Or)
        {
            var left = CoerceToBoolean(EvaluateStaticExpression(be.Left, context));
            return left || CoerceToBoolean(EvaluateStaticExpression(be.Right, context));
        }

        var leftVal = EvaluateStaticExpression(be.Left, context);
        var rightVal = EvaluateStaticExpression(be.Right, context);

        return be.Operator switch
        {
            // Arithmetic
            BinaryOperator.Add => ToDouble(leftVal) + ToDouble(rightVal),
            BinaryOperator.Subtract => ToDouble(leftVal) - ToDouble(rightVal),
            BinaryOperator.Multiply => ToDouble(leftVal) * ToDouble(rightVal),
            BinaryOperator.Divide => ToDouble(leftVal) / ToDouble(rightVal),
            BinaryOperator.IntegerDivide => (double)(long)(ToDouble(leftVal) / ToDouble(rightVal)),
            BinaryOperator.Modulo => ToDouble(leftVal) % ToDouble(rightVal),

            // Value comparisons
            BinaryOperator.Equal => CompareValues(leftVal, rightVal) == 0,
            BinaryOperator.NotEqual => CompareValues(leftVal, rightVal) != 0,
            BinaryOperator.LessThan => CompareValues(leftVal, rightVal) < 0,
            BinaryOperator.LessOrEqual => CompareValues(leftVal, rightVal) <= 0,
            BinaryOperator.GreaterThan => CompareValues(leftVal, rightVal) > 0,
            BinaryOperator.GreaterOrEqual => CompareValues(leftVal, rightVal) >= 0,

            // General comparisons
            BinaryOperator.GeneralEqual => CompareValues(leftVal, rightVal) == 0,
            BinaryOperator.GeneralNotEqual => CompareValues(leftVal, rightVal) != 0,
            BinaryOperator.GeneralLessThan => CompareValues(leftVal, rightVal) < 0,
            BinaryOperator.GeneralLessOrEqual => CompareValues(leftVal, rightVal) <= 0,
            BinaryOperator.GeneralGreaterThan => CompareValues(leftVal, rightVal) > 0,
            BinaryOperator.GeneralGreaterOrEqual => CompareValues(leftVal, rightVal) >= 0,

            // String concat
            BinaryOperator.Concat => (leftVal?.ToString() ?? "") + (rightVal?.ToString() ?? ""),

            _ => throw new InvalidOperationException($"Cannot statically evaluate operator: {be.Operator}")
        };
    }

    private bool EvaluateStaticQuantified(QuantifiedExpression qe, XElement context, Dictionary<QName, object?> vars)
    {
        return EvaluateQuantifiedBinding(qe, context, vars, 0);
    }

    private bool EvaluateQuantifiedBinding(QuantifiedExpression qe, XElement context,
        Dictionary<QName, object?> vars, int bindingIndex)
    {
        if (bindingIndex >= qe.Bindings.Count)
            return CoerceToBoolean(EvaluateStaticExpression(qe.Satisfies, context));

        var binding = qe.Bindings[bindingIndex];
        var varName = binding.Variable;
        var sequenceVal = EvaluateStaticExpression(binding.Expression, context);

        // Convert to list of items
        var items = sequenceVal is List<object?> list ? list : [sequenceVal];

        var savedVal = vars.TryGetValue(varName, out var old) ? old : null;
        var hadVal = vars.ContainsKey(varName);

        try
        {
            foreach (var item in items)
            {
                vars[varName] = item;
                var result = EvaluateQuantifiedBinding(qe, context, vars, bindingIndex + 1);

                if (qe.Quantifier == Quantifier.Some && result)
                    return true;
                if (qe.Quantifier == Quantifier.Every && !result)
                    return false;
            }

            return qe.Quantifier == Quantifier.Every; // every: true if none failed; some: false if none matched
        }
        finally
        {
            if (hadVal) vars[varName] = savedVal;
            else vars.Remove(varName);
        }
    }

    private object? EvaluateStaticFunction(FunctionCallExpression fc, XElement context)
    {
        var localName = fc.Name.LocalName;

        switch (localName)
        {
            case "true":
                return true;
            case "false":
                return false;
            case "not":
                if (fc.Arguments.Count == 1)
                    return !CoerceToBoolean(EvaluateStaticExpression(fc.Arguments[0], context));
                break;
            case "empty":
                if (fc.Arguments.Count == 1)
                {
                    var val = EvaluateStaticExpression(fc.Arguments[0], context);
                    return val == null || (val is List<object?> emptyList && emptyList.Count == 0);
                }
                break;
            case "exists":
                if (fc.Arguments.Count == 1)
                {
                    var val = EvaluateStaticExpression(fc.Arguments[0], context);
                    return val != null && !(val is List<object?> existsList && existsList.Count == 0);
                }
                break;
            case "count":
                if (fc.Arguments.Count == 1)
                {
                    var val = EvaluateStaticExpression(fc.Arguments[0], context);
                    if (val == null) return 0.0;
                    if (val is List<object?> countList) return (double)countList.Count;
                    return 1.0;
                }
                break;
            case "concat":
                if (fc.Arguments.Count >= 2)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var arg in fc.Arguments)
                        sb.Append(EvaluateStaticExpression(arg, context)?.ToString() ?? "");
                    return sb.ToString();
                }
                break;
            case "string-length":
                if (fc.Arguments.Count == 1)
                    return (double)(EvaluateStaticExpression(fc.Arguments[0], context)?.ToString()?.Length ?? 0);
                break;
            case "substring":
                if (fc.Arguments.Count >= 2)
                {
                    var str = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    var startPos = (int)Math.Round(ToDouble(EvaluateStaticExpression(fc.Arguments[1], context))) - 1;
                    if (startPos < 0) startPos = 0;
                    if (startPos >= str.Length) return "";
                    if (fc.Arguments.Count == 3)
                    {
                        var len = (int)Math.Round(ToDouble(EvaluateStaticExpression(fc.Arguments[2], context)));
                        return str.Substring(startPos, Math.Min(len, str.Length - startPos));
                    }
                    return str[startPos..];
                }
                break;
            case "boolean":
                if (fc.Arguments.Count == 1)
                    return CoerceToBoolean(EvaluateStaticExpression(fc.Arguments[0], context));
                break;
            case "string":
                if (fc.Arguments.Count == 1)
                    return EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                break;
            case "number":
                if (fc.Arguments.Count == 1)
                    return ToDouble(EvaluateStaticExpression(fc.Arguments[0], context));
                break;

            case "system-property":
                if (fc.Arguments.Count == 1)
                {
                    var propName = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    return EvaluateSystemProperty(propName, context);
                }
                break;

            case "element-available":
                if (fc.Arguments.Count == 1)
                {
                    var elemName = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    return EvaluateElementAvailable(elemName, context);
                }
                break;

            case "function-available":
                if (fc.Arguments.Count >= 1)
                {
                    var funcName = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    var arity = fc.Arguments.Count > 1
                        ? (int)ToDouble(EvaluateStaticExpression(fc.Arguments[1], context))
                        : -1;
                    return EvaluateFunctionAvailable(funcName, context, arity);
                }
                break;

            case "type-available":
                if (fc.Arguments.Count == 1)
                {
                    var typeName = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    return EvaluateTypeAvailable(typeName, context);
                }
                break;

            case "available-environment-variables":
                if (fc.Arguments.Count == 0)
                {
                    var envVars = Environment.GetEnvironmentVariables();
                    var names = new List<object?>();
                    foreach (string key in envVars.Keys)
                        names.Add(key);
                    return names;
                }
                break;

            case "environment-variable":
                if (fc.Arguments.Count == 1)
                {
                    var envName = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    return Environment.GetEnvironmentVariable(envName) ?? null;
                }
                break;

            case "contains":
                if (fc.Arguments.Count == 2)
                {
                    var str = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    var sub = EvaluateStaticExpression(fc.Arguments[1], context)?.ToString() ?? "";
                    return str.Contains(sub, StringComparison.Ordinal);
                }
                break;

            case "starts-with":
                if (fc.Arguments.Count == 2)
                {
                    var str = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    var prefix = EvaluateStaticExpression(fc.Arguments[1], context)?.ToString() ?? "";
                    return str.StartsWith(prefix, StringComparison.Ordinal);
                }
                break;

            case "upper-case":
                if (fc.Arguments.Count == 1)
                    return EvaluateStaticExpression(fc.Arguments[0], context)?.ToString()?.ToUpperInvariant() ?? "";
                break;

            case "lower-case":
                if (fc.Arguments.Count == 1)
                {
                    var lcVal = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
#pragma warning disable CA1308 // lower-case() XPath function requires ToLowerInvariant
                    return lcVal.ToLowerInvariant();
#pragma warning restore CA1308
                }
                break;

            case "static-base-uri":
                if (fc.Arguments.Count == 0)
                {
                    var baseUri = ResolveEffectiveBaseUri(context);
                    return baseUri?.ToString();
                }
                break;
        }

        // Eagerly evaluate arguments to detect forward references (XPST0008) even for unsupported functions
        foreach (var arg in fc.Arguments)
            EvaluateStaticExpression(arg, context);

        // Functions with a non-standard namespace (user-defined functions) are not available in use-when
        if (fc.Name.Namespace != NamespaceId.None)
            throw new XsltException($"XPST0017: Function {fc.Name.Prefix}:{localName} is not available in the static use-when context");
        // Known XPath functions not available in use-when: doc, doc-available, collection, etc.
        if (localName is "doc" or "doc-available" or "collection" or "uri-collection" or "unparsed-text"
            or "unparsed-text-lines" or "unparsed-text-available")
            throw new XsltException($"FODC0002: Function {localName}() is not available in the static use-when context");
        throw new InvalidOperationException($"Cannot statically evaluate function: {localName}/{fc.Arguments.Count}");
    }

    private static string EvaluateSystemProperty(string name, XElement context)
    {
        // Resolve prefix
        var localName = name;
        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var braceClose = name.IndexOf('}', StringComparison.Ordinal);
            if (braceClose > 1)
            {
                var ns = name[2..braceClose];
                localName = name[(braceClose + 1)..];
                if (ns != "http://www.w3.org/1999/XSL/Transform")
                    return "";
            }
        }
        else if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            var prefix = parts[0];
            localName = parts[1];
            // Verify prefix maps to XSLT namespace
            var ns = context.GetNamespaceOfPrefix(prefix);
            if (ns?.NamespaceName != "http://www.w3.org/1999/XSL/Transform")
                return "";
        }

        return localName switch
        {
            "version" => "3.0",
            "vendor" => "PhoenixmlDb",
            "vendor-url" => "https://endpointsystems.com",
            "product-name" => "PhoenixmlDb XSLT",
            "product-version" => "1.0",
            "is-schema-aware" => "no",
            "supports-serialization" => "yes",
            "supports-backwards-compatibility" => "yes",
            "supports-namespace-axis" => "yes",
            "supports-streaming" => "yes",
            "supports-dynamic-evaluation" => "yes",
            "supports-higher-order-functions" => "yes",
            "xpath-version" => "4.0",
            "xsd-version" => "1.1",
            _ => ""
        };
    }

    private static bool EvaluateElementAvailable(string name, XElement context)
    {
        var localName = name;
        string? namespaceUri = null;

        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var braceClose = name.IndexOf('}', StringComparison.Ordinal);
            if (braceClose > 1)
            {
                namespaceUri = name[2..braceClose];
                localName = name[(braceClose + 1)..];
            }
        }
        else if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            var prefix = parts[0];
            localName = parts[1];
            namespaceUri = context.GetNamespaceOfPrefix(prefix)?.NamespaceName;
        }

        if (namespaceUri != null && namespaceUri != "http://www.w3.org/1999/XSL/Transform")
            return false;

        return localName is
            "apply-templates" or "call-template" or "choose" or "copy" or "copy-of" or
            "element" or "attribute" or "text" or "value-of" or "variable" or "param" or
            "if" or "for-each" or "for-each-group" or "sort" or "message" or "number" or
            "comment" or "processing-instruction" or "sequence" or "iterate" or
            "try" or "catch" or "next-match" or "apply-imports" or "result-document" or
            "analyze-string" or "matching-substring" or "non-matching-substring" or
            "where-populated" or "on-empty" or "on-non-empty" or "fallback" or
            "namespace" or "output" or "strip-space" or "preserve-space" or
            "stylesheet" or "transform" or "template" or "function" or
            "import" or "include" or "import-schema" or "decimal-format" or
            "character-map" or "output-character" or "key" or
            "document" or "source-document" or "with-param" or
            "when" or "otherwise" or "break" or "next-iteration" or
            "accumulator" or "accumulator-rule" or
            "context-item" or "global-context-item" or
            "map" or "map-entry" or "array" or "assert" or
            "merge" or "merge-source" or "merge-action" or "merge-key" or
            "fork" or "accept" or "expose" or "override" or "use-package" or
            "attribute-set" or "perform-sort";
    }

    private static bool EvaluateFunctionAvailable(string name, XElement context, int arity)
    {
        // XTDE1400: Validate name is a valid lexical EQName
        if (string.IsNullOrEmpty(name))
            throw new XsltException("XTDE1400: The argument to function-available() is a zero-length string");

        // Resolve the function name
        QName qname;
        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var braceClose = name.IndexOf('}', StringComparison.Ordinal);
            if (braceClose > 1)
            {
                var ns = name[2..braceClose];
                var localName = name[(braceClose + 1)..];
                qname = new QName(NamespaceId.None, localName) { ExpandedNamespace = ns };
            }
            else
            {
                qname = new QName(NamespaceId.None, name);
            }
        }
        else if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            // XTDE1400: Validate both prefix and local name are valid NCNames
            try
            {
                System.Xml.XmlConvert.VerifyNCName(parts[0]);
                System.Xml.XmlConvert.VerifyNCName(parts[1]);
            }
            catch (System.Xml.XmlException)
            {
                throw new XsltException($"XTDE1400: The argument to function-available() ('{name}') is not a valid QName");
            }
            // XTDE1400: Check prefix has a namespace binding in scope
            var prefix = parts[0];
            var nsObj = context.GetNamespaceOfPrefix(prefix);
            if (nsObj == null || string.IsNullOrEmpty(nsObj.NamespaceName))
                throw new XsltException($"XTDE1400: The prefix '{prefix}' in the argument to function-available() has no namespace binding");
            qname = new QName(NamespaceId.None, parts[1], parts[0]) { ExpandedNamespace = nsObj.NamespaceName };
        }
        else
        {
            // XTDE1400: Validate unprefixed name is a valid NCName
            try
            {
                System.Xml.XmlConvert.VerifyNCName(name);
            }
            catch (System.Xml.XmlException)
            {
                throw new XsltException($"XTDE1400: The argument to function-available() ('{name}') is not a valid QName");
            }
            qname = new QName(NamespaceId.None, name);
        }

        var lib = PhoenixmlDb.XQuery.Functions.FunctionLibrary.Standard;
        if (arity >= 0)
            return lib.Resolve(qname, arity) != null;

        // Try common arities 0-3
        for (int i = 0; i <= 3; i++)
        {
            if (lib.Resolve(qname, i) != null)
                return true;
        }
        return false;
    }

    private static bool EvaluateTypeAvailable(string name, XElement context)
    {
        // Strip prefix
        var localName = name;
        if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            localName = parts[1];
        }

        // Basic XSD types are always available
        return localName is
            "string" or "boolean" or "decimal" or "float" or "double" or
            "integer" or "long" or "int" or "short" or "byte" or
            "nonNegativeInteger" or "positiveInteger" or "nonPositiveInteger" or "negativeInteger" or
            "unsignedLong" or "unsignedInt" or "unsignedShort" or "unsignedByte" or
            "duration" or "dateTime" or "date" or "time" or
            "yearMonthDuration" or "dayTimeDuration" or
            "gYearMonth" or "gYear" or "gMonthDay" or "gDay" or "gMonth" or
            "hexBinary" or "base64Binary" or
            "anyURI" or "QName" or "NOTATION" or "normalizedString" or "token" or
            "language" or "NMTOKEN" or "Name" or "NCName" or "ID" or "IDREF" or "ENTITY" or
            "untypedAtomic" or "anyAtomicType" or "anySimpleType" or "anyType" or
            "numeric";
    }

    private static bool CoerceToBoolean(object? value) =>
        value switch
        {
            bool b => b,
            double d => d != 0.0 && !double.IsNaN(d),
            string s => s.Length > 0,
            null => false,
            _ => true
        };

    private static double ToDouble(object? value) =>
        value switch
        {
            double d => d,
            bool b => b ? 1.0 : 0.0,
            string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
            null => 0.0,
            _ => double.NaN
        };

    private static int CompareValues(object? left, object? right)
    {
        // If both are numeric, compare numerically
        if (left is double ld && right is double rd)
            return ld.CompareTo(rd);

        // If one is numeric and other is string, convert string to number
        if (left is double || right is double)
        {
            var l = ToDouble(left);
            var r = ToDouble(right);
            return l.CompareTo(r);
        }

        // If both are boolean, compare as boolean
        if (left is bool lb && right is bool rb)
            return lb.CompareTo(rb);

        // Compare as strings
        var ls = left?.ToString() ?? "";
        var rs = right?.ToString() ?? "";
        return string.Compare(ls, rs, StringComparison.Ordinal);
    }
}

/// <summary>
/// Interface for parsing XPath/XQuery expressions.
/// </summary>
public interface IExpressionParser
{
    XQueryExpression Parse(string expression);
}

/// <summary>
/// Represents an error that occurred during XSLT stylesheet compilation or transformation.
/// </summary>
/// <remarks>
/// <para>
/// <c>XsltException</c> is thrown for both compile-time errors (malformed XSLT, invalid
/// attribute values, unresolved imports) and runtime errors (type mismatches, failed
/// assertions, dynamic evaluation errors). Check <see cref="Location"/> to determine
/// where in the stylesheet the error originated.
/// </para>
/// <para>
/// When an error occurs during transformation, the <see cref="Exception.Message"/> contains
/// a description of the XSLT error condition (e.g., <c>XTDE0540</c> for conflicting
/// <c>xsl:result-document</c> URIs), and <see cref="Location"/> pinpoints the stylesheet
/// instruction that caused the failure.
/// </para>
/// </remarks>
/// <seealso cref="XsltTransformer"/>
public class XsltException : Exception
{
    /// <summary>
    /// Gets the source location in the XSLT stylesheet where the error occurred,
    /// or <c>null</c> if the location is not available.
    /// </summary>
    /// <remarks>
    /// The location includes the line number and column number within the stylesheet,
    /// which is useful for diagnostic messages and IDE integration.
    /// </remarks>
    public SourceLocation? Location { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="XsltException"/> class.
    /// </summary>
    public XsltException()
        : base()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XsltException"/> class with a
    /// specified error message.
    /// </summary>
    /// <param name="message">A description of the XSLT error.</param>
    public XsltException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XsltException"/> class with a
    /// specified error message and inner exception.
    /// </summary>
    /// <param name="message">A description of the XSLT error.</param>
    /// <param name="innerException">The exception that caused this XSLT error.</param>
    public XsltException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XsltException"/> class with a
    /// specified error message and source location.
    /// </summary>
    /// <param name="message">A description of the XSLT error.</param>
    /// <param name="location">
    /// The location in the stylesheet where the error occurred, or <c>null</c>.
    /// </param>
    public XsltException(string message, SourceLocation? location)
        : base(message)
    {
        Location = location;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XsltException"/> class with a
    /// specified error message, source location, and inner exception.
    /// </summary>
    /// <param name="message">A description of the XSLT error.</param>
    /// <param name="location">
    /// The location in the stylesheet where the error occurred, or <c>null</c>.
    /// </param>
    /// <param name="innerException">The exception that caused this XSLT error.</param>
    public XsltException(string message, SourceLocation? location, Exception innerException)
        : base(message, innerException)
    {
        Location = location;
    }
}
