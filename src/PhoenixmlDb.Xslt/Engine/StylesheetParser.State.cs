using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

public sealed partial class StylesheetParser
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
    /// Pre-populates _staticVariables and _externalStaticParamNames from externally-provided
    /// static param values. This allows ParseStylesheet to skip select evaluation and
    /// forward-reference error checking for params whose values come from the calling processor.
    /// </summary>
    /// <summary>
    /// The external static parameters exactly as supplied, kept so an IMPORTED or INCLUDED
    /// module can resolve its own shadow attributes against them.
    /// </summary>
    /// <remarks>
    /// LoadExternalStylesheet called ResolveShadowAttributes(stylesheetRoot) with no params, so
    /// a shadow attribute inside an imported module saw only that module's own defaults. W3C
    /// copy-0617..0627 drive a shared stylesheet through <c>xsl:import</c> with
    /// <c>_inherit-namespaces="{$INHERIT}"</c> in the IMPORTED file; INHERIT=false never
    /// reached it, so namespaces were inherited when the test said they must not be.
    /// </remarks>
    private Dictionary<string, string>? _externalStaticParams;


    private readonly record struct AcceptRule(
        XElement Element, string Component, string Token, Visibility Visibility, int Order);


    private static readonly XNamespace SerializationParamsNs = "http://www.w3.org/2010/xslt-xquery-serialization";


    // Deliberately conservative: real (human- or machine-authored) stylesheets nest only a handful
    // of levels deep, while every recursive pass over the instruction tree — including the executor
    // at transform time, whose heavy async frames are the limiting factor — must survive a tree at
    // this depth on a normal ~1 MB thread stack. The executor was measured to transform a 50-deep
    // tree safely but overflow well before 100; 30 is far above any legitimate stylesheet and holds
    // a comfortable margin below that. (Raising the executor's own usable depth is a separate,
    // larger hardening — a large-stack transform worker — tracked outside this cap.)
    internal const int MaxNestingDepth = 30;

    private int _sequenceConstructorDepth;


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

}
