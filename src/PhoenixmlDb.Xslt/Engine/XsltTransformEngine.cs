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
/// XSLT transformation engine.
/// </summary>
public sealed class XsltTransformEngine
{
    private readonly XsltStylesheet _stylesheet;
    private readonly TemplateIndex _templateIndex;
    private readonly PhoenixmlDb.XQuery.ISchemaProvider? _schemaProvider;

    // Character maps loaded at runtime from an xsl:result-document/@parameter-document (XSLT 3.0
    // §27.1). The parameter-document URI is an AVT resolved at runtime, so its inline character maps
    // cannot be registered in the compiled stylesheet's map table; they are registered here under a
    // synthetic GUID QName and merged in by FinalizeOutput. Cleared at the start of every transform,
    // so it only ever holds the current transform's maps (no cross-transform accumulation).
    private readonly Dictionary<QName, XsltCharacterMap> _runtimeCharacterMaps = new();

    /// <summary>
    /// Registers a character map loaded at runtime (from an xsl:result-document parameter-document)
    /// under a fresh synthetic QName and returns that name, for inclusion in a use-character-maps list.
    /// </summary>
    internal QName RegisterRuntimeCharacterMap(Dictionary<int, string> mappings)
    {
        var name = new QName(NamespaceId.None, "rd-param-doc-character-map" + Guid.NewGuid().ToString("N"));
        _runtimeCharacterMaps[name] = new XsltCharacterMap { Name = name, Mappings = mappings };
        return name;
    }

    // Temp-tree base-URI preservation sentinel. Emitted by the temp-tree serializer only
    // (gated by _tempTreeSerializeDepth) and always stripped by the reparse, so it never
    // appears in any user-visible output. See TryEmitBaseSentinel / ReadXdmElementFromReader
    // / ConvertXmlNode for the round-trip.
    internal const string BaseSentinelNs = "http://phoenixmldb/internal/base-uri";
    internal const string BaseSentinelPrefix = "_pxbase_";
    internal const string BaseSentinelLocalName = "base";

    /// <summary>
    /// Secondary result documents from the last transformation, keyed by href URI.
    /// </summary>
    public IReadOnlyDictionary<string, string> SecondaryResultDocuments { get; private set; }
        = new Dictionary<string, string>();

    public XsltTransformEngine(XsltStylesheet stylesheet,
        PhoenixmlDb.XQuery.ISchemaProvider? schemaProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stylesheet);
        _stylesheet = stylesheet;
        _templateIndex = new TemplateIndex(stylesheet);
        _schemaProvider = schemaProvider;
    }

    /// <summary>
    /// Converts a Uri to string, distinguishing two cases that look similar through .NET's
    /// <see cref="Uri"/> API but need opposite handling:
    /// <list type="bullet">
    ///   <item><b>Windows drive-letter path</b> (e.g. <c>C:\Users\…\file.xsl</c> or
    ///         <c>C:/Users/…/file.xsl</c>) — produces <see cref="Uri.Scheme"/> = "file"
    ///         and <see cref="Uri.OriginalString"/> as the path. We want the proper file
    ///         URI form (<c>file:///C:/Users/…</c>) so <c>doc()</c> on the other side
    ///         can recognize and read the file.</item>
    ///   <item><b>Non-standard scheme URI</b> (e.g. <c>d://tests/</c>) — .NET on Linux
    ///         mangles this to <c>file:///d://tests/</c> via <see cref="Uri.ToString"/>.
    ///         We want to preserve <see cref="Uri.OriginalString"/>.</item>
    /// </list>
    /// The distinguishing pattern: a Windows drive path starts with one ASCII letter
    /// then ":" then "\" or "/". A non-standard scheme URI has a multi-char scheme name
    /// before "://", or uses a single-letter scheme followed by content that doesn't
    /// look like a drive path. The earlier "any colon means scheme mangling" heuristic
    /// caught both cases and broke <c>doc()</c> on Windows (Martin Honnen's report:
    /// <c>localization-base-uri: C:/Users/marti/…/locale/</c> instead of <c>file:///C:/…</c>).
    /// </summary>
    internal static string? UriString(Uri? uri)
    {
        if (uri == null) return null;
        if (uri.Scheme == "file" && !uri.OriginalString.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            // Windows drive-letter path: ASCII letter + ":" + ("\" or "/").
            // Use the canonical file URI form so downstream parsers recognize it.
            if (LooksLikeWindowsDrivePath(uri.OriginalString))
                return uri.AbsoluteUri;
            // Otherwise the original looked like a non-standard URI scheme that .NET
            // mangled. Preserve OriginalString so the original semantics survive.
            if (uri.OriginalString.Contains(':', StringComparison.Ordinal))
                return uri.OriginalString;
        }
        return uri.ToString();
    }

    private static bool LooksLikeWindowsDrivePath(string s)
    {
        // C:\... or C:/... — exactly one ASCII letter, then ':', then '\' or '/'.
        if (s.Length < 3) return false;
        if (!char.IsAsciiLetter(s[0])) return false;
        if (s[1] != ':') return false;
        if (s[2] != '\\' && s[2] != '/') return false;
        // A "://" authority (e.g. "d://tests/") is a scheme-based URI whose single-letter
        // scheme .NET mis-parses as a drive letter — NOT a Windows drive path. Its
        // OriginalString must survive intact rather than be canonicalised to file:///.
        if (s[2] == '/' && s.Length > 3 && s[3] == '/') return false;
        return true;
    }

    /// <summary>
    /// Builds the xsl:with-param list for an initial template from
    /// <see cref="XsltTransformOptions.InitialTemplateParameters"/> and
    /// <see cref="XsltTransformOptions.InitialTunnelParameters"/>.
    /// </summary>
    /// <remarks>
    /// Shared because SIX call sites start a transform — TransformAsync and TransformRawAsync
    /// each reach an initial template via CallTemplateAsync and an initial mode via
    /// ApplyTemplatesAsync — and only TransformAsync passed any parameters. Every site in
    /// TransformRawAsync passed an empty list, so fn:transform's template-params and
    /// tunnel-params were silently dropped on every raw-delivery transform. The template ran and
    /// returned its default, which is why it looked like the options were unimplemented rather
    /// than undelivered.
    ///
    /// The initial-MODE half is the one that matters in practice: XSpec compiles a scenario-level
    /// x:param under an x:context/@mode into template-params, so an entire suite ran with its
    /// parameters missing and terminated on the first assertion that read one.
    /// </remarks>
    private static List<Ast.XsltWithParam> BuildInitialTemplateWithParams(XsltTransformOptions options)
    {
        var withParams = new List<Ast.XsltWithParam>();
        foreach (var (name, value) in options.InitialTemplateParameters)
        {
            withParams.Add(new Ast.XsltWithParam
            {
                Name = name,
                RuntimeValue = value,
                HasRuntimeValue = true,
                FromRuntimeOptions = true
            });
        }
        foreach (var (name, value) in options.InitialTunnelParameters)
        {
            withParams.Add(new Ast.XsltWithParam
            {
                Name = name,
                RuntimeValue = value,
                HasRuntimeValue = true,
                Tunnel = true,
                FromRuntimeOptions = true
            });
        }
        return withParams;
    }

    /// <summary>
    /// Transforms an XML document using the stylesheet.
    /// </summary>
    public Task<string> TransformAsync(XdmNode source, XsltTransformOptions? options = null)
        => TransformAsync(source, options, null);

    internal async Task<string> TransformAsync(XdmNode source, XsltTransformOptions? options, XdmInMemoryStore? nodeStore)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new XsltTransformOptions();

        var outputBuilder = new StringBuilder();
        var context = new DefaultXsltExecutionContext(
            _stylesheet,
            _templateIndex,
            source,
            outputBuilder,
            options,
            nodeStore,
            _schemaProvider);
        context.Owner = this;
        _runtimeCharacterMaps.Clear();

        // When there is no source document, the context item is absent during global variable
        // evaluation (XSLT 3.0 §5.4.1: global variables are evaluated with the initial focus).
        if (!options.HasSourceDocument)
            context.PushContextItem(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus, 0, 0);

        // Initialize global parameters and variables in dependency order
        await InitializeGlobalsInDependencyOrderAsync(context, outputBuilder).ConfigureAwait(false);

        if (!options.HasSourceDocument)
            context.PopContextItem();

        // Bind initial parameters as global variables (overriding stylesheet defaults)
        BindExternalParameters(context, options);

        // XTDE0050: Check that all required global parameters have been supplied
        foreach (var param in _stylesheet.Parameters)
        {
            if (param.Required && !options.InitialParameters.ContainsKey(param.Name))
                throw new XsltException($"XTDE0050: Required parameter ${param.Name.LocalName} was not supplied");
        }

        // xsl:global-context-item enforcement
        EnforceGlobalContextItem(options);

        // xsl:global-context-item type checking
        if (_stylesheet.GlobalContextItemAs != null && options.HasSourceDocument)
        {
            var typeMatches = XQuery.Execution.TypeCastHelper.MatchesSequenceItemType(source, _stylesheet.GlobalContextItemAs);

            // Additional check for document-node(element(name))
            if (typeMatches && _stylesheet.GlobalContextItemAs.DocumentElementName != null
                && source is XdmDocument gciDoc)
            {
                // Resolve document element via node store
                XdmElement? docElem = null;
                if (nodeStore != null)
                {
                    docElem = gciDoc.Children
                        .Select(id => nodeStore.GetNode(id))
                        .OfType<XdmElement>()
                        .FirstOrDefault();
                }

                if (docElem == null || docElem.LocalName != _stylesheet.GlobalContextItemAs.DocumentElementName)
                    typeMatches = false;
            }

            if (!typeMatches)
                throw new XsltException("XTTE0590: The global context item does not match the required type declared by xsl:global-context-item");
        }

        // Pre-compute accumulators on the source document if the initial mode specifies use-accumulators.
        // This happens after globals (accumulator initial values may reference parameters) but before
        // template application (templates and global variables may access accumulator values via
        // accumulator-before/after functions).
        if (source is XdmDocument sourceDoc && nodeStore != null && _stylesheet.Accumulators.Count > 0)
        {
            var initialModeKey = options.InitialMode ?? new QName(NamespaceId.None, "");
            if (_stylesheet.Modes.TryGetValue(initialModeKey, out var modeDecl))
            {
                List<XsltAccumulator>? accumulators = null;
                if (modeDecl.UseAllAccumulators)
                {
                    accumulators = _stylesheet.Accumulators.Values.ToList();
                }
                else if (modeDecl.UseAccumulatorNames.Count > 0)
                {
                    accumulators = new List<XsltAccumulator>();
                    foreach (var accName in modeDecl.UseAccumulatorNames)
                    {
                        if (_stylesheet.Accumulators.TryGetValue(accName, out var acc))
                            accumulators.Add(acc);
                    }
                }
                if (accumulators is { Count: > 0 })
                {
                    await context.PreComputeAccumulatorsAsync(sourceDoc, accumulators, nodeStore).ConfigureAwait(false);
                }
            }
        }

        // Check if output method is JSON or Adaptive — enable sequence collection
        var principalOutput = _stylesheet.Outputs.FirstOrDefault();
        var isJsonOutput = principalOutput?.EffectiveMethod is OutputMethod.Json or OutputMethod.Adaptive or OutputMethod.Csv;
        if (isJsonOutput)
            context.BeginSequenceCollection();

        // §5.7.2 sequence normalization: the item-separator serialization parameter
        // declared on the principal xsl:output governs the whole result sequence, so
        // seed the override before the entry point runs. Adjacent atomic items in the
        // result sequence are then joined with this string (no extra whitespace).
        // Absent (null / "#absent" merge sentinel) leaves the legacy single-space
        // default in place. Instruction-level item-separator save/restores this field
        // for its own content (see SequenceCore item-separator handling), so nested
        // overrides still win locally. See W3C decl/output output-0703/0709/0718/0719.
        // The item-separator that governs the whole principal result sequence comes ONLY from the
        // unnamed (principal) xsl:output declaration. A NAMED xsl:output is a named output definition
        // referenced by xsl:result-document/@format and must never seed the principal sequence, even
        // when it is the stylesheet's only output declaration (insn/result-document/result-document-0305).
        var principalUnnamedOutput = _stylesheet.Outputs.FirstOrDefault(o => o.Name == null);
        if (principalUnnamedOutput?.ItemSeparator is { } principalItemSeparator && principalItemSeparator != "#absent")
            context.SeedItemSeparatorOverride(principalItemSeparator);

        // If an initial function is specified, call it directly (XSLT 3.0 "call function" invocation)
        if (options.InitialFunction != null)
        {
            var funcName = options.InitialFunction.Value;
            var arity = options.InitialFunctionArguments.Count;
            var funcKey = (funcName, arity);
            if (!_stylesheet.Functions.TryGetValue(funcKey, out var func))
                throw new XsltException(
                    $"XTDE0041: No public stylesheet function '{funcName.LocalName}' with arity {arity} is available");
            if (func.Visibility is not (Ast.Visibility.Public or Ast.Visibility.Final))
                throw new XsltException(
                    $"XTDE0041: Stylesheet function '{funcName.LocalName}' is not public");
            // Mirror the TransformRawAsync path (line ~637): if any args arrived as
            // CrossStoreNodeRef wrappers (outer XQuery passed nodes across the engine
            // boundary), re-parse into this engine's node store so xsl:evaluate can
            // navigate children/attributes. Without this they reach the function as
            // opaque CrossStoreNodeRef items and xpath axis steps throw XPTY0020.
            IReadOnlyList<object?> initialFuncArgs = TranslateNodeArgumentsToLocalStore(
                options.InitialFunctionArguments, context._nodeStore);
            var result = await context.CallXsltFunctionAsync(func, initialFuncArgs).ConfigureAwait(false);
            if (options.ReturnRawXdm)
            {
                // delivery-format='raw' from fn:transform: callers want the typed XDM
                // value end-to-end (booleans, maps, nodes…). Skip the text serialization
                // path that would otherwise stringify and lose the type. Found in Martin
                // Honnen's testing of fn:transform with initial-function returning
                // xs:boolean from xsl:evaluate.
                // Wrap any node items as CrossStoreNodeRef so the caller can re-anchor
                // them in its store — inner-store NodeIds don't resolve outside.
                options.RawResult.Value = WrapNodesForCrossStoreTransport(result, context);
            }
            else
            {
                // Default path: serialize the function result to the output
                context.SerializeFunctionResult(result, outputBuilder);
            }
        }
        // If an initial template is specified, call it directly instead of applying templates
        // Also auto-detect xsl:initial-template from the parsed stylesheet
        else if (options.InitialTemplate != null || (!options.HasSourceDocument && _stylesheet.NamedTemplates.Keys.Any(k => k.LocalName == "initial-template")))
        {
            var initialTemplate = options.InitialTemplate;
            if (initialTemplate == null)
            {
                initialTemplate = _stylesheet.NamedTemplates.Keys.First(k => k.LocalName == "initial-template");
            }

            // XTDE0040: Check that initial template is public (in packages).
            // A package top-level named template defaults to PRIVATE visibility (the
            // parser's effective-Visibility default of public is a non-package
            // workaround), so only a template with an EXPLICIT visibility of public or
            // final — whether declared inline or raised by xsl:expose — is eligible as
            // an entry point. See W3C decl/package package-001a/001b, decl/accept
            // accept-001a.
            if (_stylesheet.IsPackage
                && _stylesheet.NamedTemplates.TryGetValue(initialTemplate.Value, out var tmpl)
                && tmpl.VisibilityAttr is not ("public" or "final"))
                throw new XsltException($"XTDE0040: Initial template '{initialTemplate.Value.LocalName}' is not public");

            // Convert initial template parameters to xsl:with-param entries.
            // Only pass explicitly-set template params; global InitialParameters are set as
            // stylesheet-level variables separately and should NOT be passed as with-params.
            var withParams = BuildInitialTemplateWithParams(options);
            // When calling an initial template with no source document, the context item
            // should be absent per XSLT 3.0 §2.3
            if (!options.HasSourceDocument)
                context.PushContextItem(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus, 0, 0);
            await context.CallTemplateAsync(initialTemplate.Value, withParams).ConfigureAwait(false);
            if (!options.HasSourceDocument)
                context.PopContextItem();
        }
        else
        {
            // XTDE0040: with no source document, no initial template (or auto-detected
            // xsl:initial-template), no initial function, and no initial mode / match
            // selection, there is nothing to apply templates to — the invocation has no
            // eligible entry point. An empty package invoked this way must be rejected
            // rather than silently producing empty output. See W3C decl/package
            // package-914a (any-of XTDE0040 / XTDE0044).
            if (!options.HasSourceDocument && !options.InitialMode.HasValue
                && options.InitialModeSelect == null && options.InitialModeSelectValue == null)
                throw new XsltException("XTDE0040: The invocation supplies no source document, initial template, initial function, or initial mode");

            // XTDE0044: It is a dynamic error if the invocation of the stylesheet specifies
            // an initial mode and no initial match selection is supplied.
            if (options.InitialMode.HasValue && !options.HasSourceDocument
                && options.InitialModeSelect == null && options.InitialModeSelectValue == null)
                throw new XsltException("XTDE0044: The invocation of the stylesheet specifies an initial mode but no initial match selection is supplied");

            // Apply templates to the document node itself (XSLT semantics: initial match is against "/")
            // If no explicit initial mode was requested, use the stylesheet's default-mode.
            var effectiveInitialMode = options.InitialMode ?? _stylesheet.DefaultMode;

            // XTDE0045: The initial mode must be a mode used in the stylesheet.
            // A mode is "used" if a template explicitly declares it (not just #all)
            // or if it is declared via xsl:mode.
            // Skip the check for the default mode (#unnamed / #default) — per spec,
            // the error only applies to modes "other than the default mode".
            if (options.InitialMode.HasValue)
            {
                var m = options.InitialMode.Value;
                var isDefaultMode = m.LocalName is "#unnamed" or "#default" or ""
                    || m.Equals(_stylesheet.DefaultMode);
                if (!isDefaultMode
                    && !_templateIndex.IsModeExplicitlyUsed(m)
                    && !_stylesheet.Modes.ContainsKey(m))
                    throw new XsltException($"XTDE0045: The initial mode '{m.LocalName}' is not a mode used in the stylesheet");

                // XTDE0045: A mode explicitly exposed as private (via xsl:expose) is not
                // eligible as an initial mode. This covers implicit modes (declared-modes=
                // "false") that never appear in _stylesheet.Modes but were matched by a
                // wildcard/name expose with visibility="private". An implicitly-private mode
                // (never named by an expose) remains eligible. See W3C package-001j.
                if (!isDefaultMode && _stylesheet.ExplicitlyExposedPrivateModes.Contains(m))
                    throw new XsltException($"XTDE0045: The initial mode '{m.LocalName}' is not eligible as an initial mode (it is explicitly exposed as private)");

                // XTDE0045: A mode is eligible as initial mode only if it has public or
                // final visibility, or it is the default mode of the package. In a package,
                // a top-level xsl:mode with no explicit visibility defaults to PRIVATE (the
                // shared ParseVisibility default of public covers only non-package modules),
                // so such modes are ineligible unless named in @default-mode. See W3C
                // mode-1705b and mode-1714err.
                if (!isDefaultMode
                    && _stylesheet.Modes.TryGetValue(m, out var modeDecl))
                {
                    var effectiveVisibility = modeDecl.VisibilityAttr == null && _stylesheet.IsPackage
                        ? Ast.Visibility.Private
                        : modeDecl.Visibility;
                    if (effectiveVisibility is Ast.Visibility.Private)
                        throw new XsltException($"XTDE0045: The initial mode '{m.LocalName}' is not eligible as an initial mode (it has private visibility)");
                }
            }

            // Build with-params from initial template/mode parameters
            var applyWithParams = BuildInitialTemplateWithParams(options);

            // If InitialModeSelect is specified, evaluate it and apply templates to the result
            if (options.InitialModeSelect != null || options.InitialModeSelectValue != null)
            {
                var selectExpr = BuildInitialModeSelectExpression(context, options);
                await context.ApplyTemplatesAsync(
                    selectExpr,
                    effectiveInitialMode,
                    [],
                    applyWithParams).ConfigureAwait(false);
            }
            else
            {
                await context.ApplyTemplatesAsync(
                    ContextItemExpression.Instance, // select="." yields the document node
                    effectiveInitialMode,
                    [],
                    applyWithParams).ConfigureAwait(false);
            }
        }

        // For JSON/Adaptive output: collect sequence items and serialize as JSON
        if (isJsonOutput)
        {
            FinalizeJsonOutput(context, outputBuilder, principalOutput, nodeStore, options);
        }

        // Capture secondary result documents from this transform
        SecondaryResultDocuments = context.SecondaryResults;

        var output = outputBuilder.ToString();

        // Apply output method post-processing
        // When result-document claimed primary output with a named format, use that declaration
        var outputDecl = context.PrimaryOutputMatchedDeclaration ?? _stylesheet.Outputs.FirstOrDefault();
        return FinalizeOutput(output, outputDecl, context.PrincipalOutputCharacterMaps, FinalizeKind.Primary);
    }

    /// <summary>
    /// Identifies which delivery path invoked <see cref="FinalizeOutput"/>. Later tasks may
    /// branch on this; the post-processing body is currently identical for all kinds.
    /// </summary>
    internal enum FinalizeKind { Primary, ResultDocument, StreamingSink, StreamingBuffered }

    /// <summary>
    /// Applies the shared serialization post-processing pipeline (text/html post-process,
    /// content-type meta, indentation, character maps, Unicode normalization, XML declaration,
    /// doctype, BOM, escape-uri-attributes, sentinel restore) to <paramref name="output"/>.
    /// </summary>
    /// <summary>
    /// Validates the serialization parameters of an <see cref="XsltOutput"/> declaration
    /// against the W3C XSLT/XQuery Serialization 4.0 constraints, raising the relevant
    /// serialization error before any output is delivered. Mirrors the checks performed by
    /// <c>PhoenixmlDb.XQuery.XQueryResultSerializer</c> so both engines behave identically.
    /// </summary>
    /// <param name="output">The serialized result tree (pre-declaration/doctype).</param>
    /// <param name="outputDecl">The effective output declaration.</param>
    private static void ValidateSerializationParameters(string output, XsltOutput outputDecl)
    {
        var method = outputDecl.EffectiveMethod;

        // SESU0007: an output encoding is requested that the serializer cannot produce.
        // UTF-8/UTF-16 are always supported; any other name is validated against the
        // runtime's encoding registry. An unknown name (e.g. "XXX-xx") is a serialization
        // error. (Serialization 4.0 §5.1.2.)
        if (!string.IsNullOrEmpty(outputDecl.Encoding))
        {
            var enc = outputDecl.Encoding.Trim();
            var isUtf = enc.Equals("UTF-8", StringComparison.OrdinalIgnoreCase)
                || enc.Equals("UTF-16", StringComparison.OrdinalIgnoreCase);
            if (!isUtf)
            {
                bool supported;
                try { supported = System.Text.Encoding.GetEncoding(enc) != null; }
                catch (ArgumentException) { supported = false; }
                if (!supported)
                    throw new XsltException(
                        $"SESU0007: The requested output encoding '{enc}' is not supported by the serializer");
            }
        }

        // SESU0011: an unsupported normalization-form is requested. NFC/NFD/NFKC/NFKD and
        // "none" are supported; anything else (including "fully-normalized") is an error.
        if (!string.IsNullOrEmpty(outputDecl.NormalizationForm))
        {
            var nf = outputDecl.NormalizationForm.Trim();
            var supported = nf.Equals("NFC", StringComparison.OrdinalIgnoreCase)
                || nf.Equals("NFD", StringComparison.OrdinalIgnoreCase)
                || nf.Equals("NFKC", StringComparison.OrdinalIgnoreCase)
                || nf.Equals("NFKD", StringComparison.OrdinalIgnoreCase)
                || nf.Equals("none", StringComparison.OrdinalIgnoreCase);
            if (!supported)
                throw new XsltException(
                    $"SESU0011: The requested normalization-form '{nf}' is not supported by the serializer");
        }

        // SEPM0009: omit-xml-declaration="yes" conflicts with a standalone value other than
        // "omit", OR with (version != 1.0 AND doctype-system specified).
        // The omit-xml-declaration and standalone serialization parameters apply only to the
        // xml and xhtml output methods; for text/html/json/adaptive no XML declaration is ever
        // emitted, so the conflict cannot arise and SEPM0009 must not be raised. When a named
        // xsl:output overrides another across import precedence (method="text" winning over an
        // imported method="html"), the surviving standalone/omit values are inert and must not
        // trip this check (insn/result-document/result-document-0239).
        if (outputDecl.OmitXmlDeclaration == true && method is OutputMethod.Xml or OutputMethod.Xhtml)
        {
            if (outputDecl.Standalone.HasValue)
                throw new XsltException(
                    "SEPM0009: omit-xml-declaration=\"yes\" cannot be combined with a standalone value other than omit");
            var version = outputDecl.Version ?? "1.0";
            if (version != "1.0" && outputDecl.DoctypeSystem != null)
                throw new XsltException(
                    "SEPM0009: omit-xml-declaration=\"yes\" cannot be combined with version other than 1.0 when doctype-system is specified");
        }

        // SEPM0010: undeclare-prefixes="yes" requires XML output version 1.1; combining it
        // with version 1.0 is a serialization error.
        if (outputDecl.UndeclarePrefixes == true
            && method is OutputMethod.Xml or OutputMethod.Xhtml
            && (outputDecl.Version ?? "1.0") == "1.0")
        {
            throw new XsltException(
                "SEPM0010: undeclare-prefixes=\"yes\" cannot be combined with output version 1.0");
        }

        // SEPM0004: standalone (yes/no) or doctype-system requires the result to be a single
        // well-formed document element. A result with zero, or more than one, top-level
        // element (or top-level non-whitespace text) cannot be serialized this way.
        if (method is OutputMethod.Xml or OutputMethod.Xhtml
            && (outputDecl.Standalone.HasValue || outputDecl.DoctypeSystem != null)
            && !ResultIsSingleDocumentElement(output))
        {
            throw new XsltException(
                "SEPM0004: standalone or doctype-system requires a result that is a single well-formed document element");
        }

        // SERE0015: the HTML output method terminates a processing instruction with a bare '>'
        // (not '?>'), so a '>' inside PI content cannot be represented and is a serialization
        // error (Serialization 4.0 §8.1 / output-0196). Checked here, before HTML post-processing
        // rewrites PIs, while they are still in <?name content?> form.
        if (method == OutputMethod.Html && ProcessingInstructionContentContainsGt(output))
        {
            throw new XsltException(
                "SERE0015: a '>' character appears within a processing instruction serialized with the HTML output method");
        }
    }

    /// <summary>
    /// Scans a serialized result-tree string for a processing instruction whose content
    /// (between the target name and the closing <c>?&gt;</c>) contains a <c>&gt;</c> character.
    /// CDATA sections and comments are skipped so a <c>&gt;</c> in ordinary data is not mistaken
    /// for PI content. Used to enforce SERE0015 for the HTML output method.
    /// </summary>
    private static bool ProcessingInstructionContentContainsGt(string output)
    {
        var i = 0;
        var n = output.Length;
        while (i < n)
        {
            var c = output[i];
            if (c != '<') { i++; continue; }
            var rest = output.AsSpan(i);
            if (rest.StartsWith("<!--".AsSpan(), StringComparison.Ordinal))
            {
                var end = output.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? n : end + 3;
                continue;
            }
            if (rest.StartsWith("<![CDATA[".AsSpan(), StringComparison.Ordinal))
            {
                var end = output.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                i = end < 0 ? n : end + 3;
                continue;
            }
            if (i + 1 < n && output[i + 1] == '?')
            {
                var end = output.IndexOf("?>", i + 2, StringComparison.Ordinal);
                if (end < 0) return false;
                // Content spans [i+2, end); a '>' anywhere in it is illegal for HTML PIs.
                if (output.AsSpan(i + 2, end - (i + 2)).IndexOf('>') >= 0)
                    return true;
                i = end + 2;
                continue;
            }
            i++;
        }
        return false;
    }

    /// <summary>
    /// Determines whether a serialized result-tree string consists of exactly one top-level
    /// element (with only whitespace, comments or processing instructions around it), i.e. it
    /// forms a well-formed XML document. Used to enforce SEPM0004.
    /// </summary>
    private static bool ResultIsSingleDocumentElement(string output)
    {
        int depth = 0;
        int topElements = 0;
        bool topLevelText = false;
        int i = 0;
        int n = output.Length;
        while (i < n)
        {
            char c = output[i];
            if (c == '<')
            {
                var rest = output.AsSpan(i);
                if (rest.StartsWith("<!--".AsSpan(), StringComparison.Ordinal))
                {
                    int end = output.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 3;
                    continue;
                }
                if (rest.StartsWith("<![CDATA[".AsSpan(), StringComparison.Ordinal))
                {
                    if (depth == 0) topLevelText = true;
                    int end = output.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 3;
                    continue;
                }
                if (i + 1 < n && output[i + 1] == '!') // DOCTYPE or other declaration
                {
                    int endg = output.IndexOf('>', i);
                    i = endg < 0 ? n : endg + 1;
                    continue;
                }
                if (i + 1 < n && output[i + 1] == '?') // PI / xml declaration
                {
                    int end = output.IndexOf("?>", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? n : end + 2;
                    continue;
                }
                if (i + 1 < n && output[i + 1] == '/') // end tag
                {
                    depth--;
                    int endg = output.IndexOf('>', i);
                    i = endg < 0 ? n : endg + 1;
                    continue;
                }
                // start tag
                if (depth == 0) topElements++;
                int te = i + 1;
                while (te < n && output[te] != '>')
                {
                    if (output[te] == '"' || output[te] == '\'')
                    {
                        char q = output[te];
                        te++;
                        while (te < n && output[te] != q) te++;
                    }
                    te++;
                }
                bool selfClose = te > 0 && te <= n && te - 1 >= 0 && te - 1 < n && output[te - 1] == '/';
                if (!selfClose) depth++;
                i = te < n ? te + 1 : n;
                continue;
            }
            if (depth == 0 && !char.IsWhiteSpace(c)) topLevelText = true;
            i++;
        }
        return topElements == 1 && !topLevelText;
    }

    internal string FinalizeOutput(string output, XsltOutput? outputDecl, IReadOnlyList<QName>? resultDocCharacterMaps, FinalizeKind kind)
    {
        // Default output method (Serialization 4.0 §Default Output Method): when no method was
        // specified on xsl:output, a serialized result whose document element is `html` resolves to
        // the html method (html element in no namespace) or the xhtml method (html element in the
        // XHTML namespace) instead of falling back to xml; that in turn applies the Content-Type
        // meta, HTML5 doctype, and html indentation default (output-0715 / output-0130). An explicit
        // method always wins, so only resolve when Method is absent.
        //
        // STREAMING SAFETY: this resolution is deliberately restricted to FinalizeKind.Primary — the
        // normal, fully-buffered non-streamed result. The streamed/LRE for-each-group path buffers
        // and finalizes under FinalizeKind.StreamingBuffered, and MUST NOT pick up the html/xhtml
        // treatment: doing so previously indented streamed <html>-rooted output and inserted a meta,
        // which regressed the su-absorbing streaming set and the for-each-group unit test. Streamed
        // results therefore keep their existing (xml-default) serialization byte-for-byte.
        if (kind == FinalizeKind.Primary
            && (outputDecl == null || outputDecl.Method == null)
            && ResolveDefaultOutputMethod(output) is OutputMethod defaultedMethod)
        {
            outputDecl = outputDecl?.CloneWithMethod(defaultedMethod)
                ?? new XsltOutput { Method = defaultedMethod };
        }

        // A document (root) element in a namespace other than XHTML is "foreign" to the XHTML
        // output method (Serialization 3.0 §XHTML): it is serialized by XML rules, so it gets no
        // HTML empty-element minimization, no Content-Type meta, and no HTML5 DOCTYPE. An element
        // in no namespace preserves the engine's existing lenient handling (treated as XHTML).
        bool foreignXhtmlRoot = false;
        if (outputDecl != null)
        {
            // W3C Serialization 4.0 parameter validation. These serialization errors are
            // raised before any output is produced, matching PhoenixmlDb.XQuery's serializer
            // (SEPM0004 / SESU0007 / SESU0011 / SEPM0009 / SEPM0010).
            ValidateSerializationParameters(output, outputDecl);

            if (outputDecl.EffectiveMethod == OutputMethod.Xhtml)
            {
                var rootNs = GetRootElementNamespace(output);
                foreignXhtmlRoot = rootNs.Length > 0 && rootNs != "http://www.w3.org/1999/xhtml";
            }

            if (outputDecl.EffectiveMethod == OutputMethod.Text)
            {
                // Text output: strip all markup, return only text content.
                // The secondary xsl:result-document path (FinalizeKind.ResultDocument) has already
                // stripped its text content WITHOUT sentinel protection, so re-stripping here would
                // re-process decoded markup characters and corrupt the output. Skip the strip for
                // that path; the primary result-document path uses FinalizeKind.Primary (and sentinel
                // escaping), so it still strips correctly here.
                if (kind != FinalizeKind.ResultDocument)
                    output = DefaultXsltExecutionContext.StripXmlMarkup(output);
            }
            else if ((outputDecl.EffectiveMethod == OutputMethod.Html || outputDecl.EffectiveMethod == OutputMethod.Xhtml)
                     && !foreignXhtmlRoot)
            {
                // HTML/XHTML methods: elements in the HTML5 content namespaces (XHTML, SVG, MathML)
                // that are bound via a prefix in the result tree are serialized in the conventional
                // default-namespace form — local element name + default xmlns declaration — and the
                // namespace declarations for those three namespaces are prefix-normalized away
                // (Serialization 3.0 XHTML output method; output-0211/0221/0225/0226, output-0602a/
                // 0602b/0603a). The HTML method performs the same prefix normalization as XHTML (W3C
                // decl/output MHK ruling 2019-04-11: the spec is inadequate for HTML here and the
                // XHTML rules are treated as definitive). Runs before PostProcessHtmlOutput/DOCTYPE
                // emission so void-element handling and the HTML5 DOCTYPE name see the stripped local
                // names. Foreign-namespace elements keep their prefixes (XML rules).
                output = DefaultXsltExecutionContext.RedefaultXhtmlNamespaces(output);
                output = DefaultXsltExecutionContext.PostProcessHtmlOutput(output, outputDecl.EffectiveMethod);
            }

            // Insert <meta http-equiv="Content-Type"> in <head> when include-content-type is yes (default)
            if ((outputDecl.EffectiveMethod == OutputMethod.Html || outputDecl.EffectiveMethod == OutputMethod.Xhtml) &&
                !foreignXhtmlRoot &&
                outputDecl.IncludeContentType != false)
            {
                output = DefaultXsltExecutionContext.InsertContentTypeMeta(output, outputDecl);
            }

            // Apply indentation. Per XSLT 3.0 §20: default is yes for html/xhtml, no for xml.
            var effectiveIndent = outputDecl.Indent
                ?? (outputDecl.EffectiveMethod is OutputMethod.Html or OutputMethod.Xhtml);
            if (effectiveIndent &&
                outputDecl.EffectiveMethod is OutputMethod.Xml or OutputMethod.Xhtml or OutputMethod.Html)
            {
                output = ApplyIndentation(output, outputDecl.EffectiveMethod, outputDecl.SuppressIndentation);
            }
        }

        // Apply character maps: merge xsl:output maps with xsl:result-document maps.
        // Per XSLT 3.0 §20: result-document maps supplement and take precedence.
        // Character maps are LOCAL to the package that declared the xsl:output referencing
        // them: when outputDecl came from a used package, resolve its use-character-maps in
        // that package's registry, not the principal's (use-package-108 / use-package-108b).
        var charMaps = outputDecl?.PackageStylesheet?.CharacterMaps ?? _stylesheet.CharacterMaps;
        // Merge in any character maps loaded at runtime from an xsl:result-document parameter-document
        // (§27.1). These live outside the compiled stylesheet's map table because their URI is an AVT
        // resolved at runtime (result-document-1406). The union is taken lazily so the common no-runtime
        // case keeps referencing the compiled table directly.
        if (_runtimeCharacterMaps.Count > 0)
        {
            var merged = new Dictionary<QName, XsltCharacterMap>(charMaps);
            foreach (var (k, v) in _runtimeCharacterMaps)
                merged[k] = v;
            charMaps = merged;
        }
        if (charMaps.Count > 0)
        {
            var outputMaps = outputDecl?.UseCharacterMaps;
            var rdMaps = resultDocCharacterMaps;
            if ((outputMaps != null && outputMaps.Count > 0) || (rdMaps != null && rdMaps.Count > 0))
            {
                // Build merged list: output maps first, then result-document maps (later ones override)
                var mergedMaps = new List<QName>();
                if (outputMaps != null) mergedMaps.AddRange(outputMaps);
                if (rdMaps != null) mergedMaps.AddRange(rdMaps);
                // When a normalization-form is in effect, bracket each replacement so the
                // normalization pass below leaves character-map output alone (character-map-025/028).
                output = ApplyCharacterMaps(output, mergedMaps, outputDecl?.EffectiveMethod ?? OutputMethod.Xml,
                    protectMappedOutput: outputDecl?.NormalizationForm != null, charMaps: charMaps);
            }
        }

        // Apply Unicode normalization if specified. Character-map replacement regions are
        // immune (they are bracketed by MapGuard sentinels, which NormalizeExceptMappedRegions
        // skips and strips).
        if (outputDecl?.NormalizationForm != null)
        {
            System.Text.NormalizationForm? form = outputDecl.NormalizationForm.ToUpperInvariant() switch
            {
                "NFC" => System.Text.NormalizationForm.FormC,
                "NFD" => System.Text.NormalizationForm.FormD,
                "NFKC" => System.Text.NormalizationForm.FormKC,
                "NFKD" => System.Text.NormalizationForm.FormKD,
                _ => null
            };
            if (form.HasValue)
                output = NormalizeContentRegions(output, form.Value);
        }

        // Characters that cannot be represented in the target encoding are emitted as numeric
        // character references (W3C Serialization 4.0 §7.2), matching PhoenixmlDb.XQuery's
        // encoding-aware WriteTextEscaped/WriteCDataEncodingAware. This runs after character
        // maps and normalization so replacement text is included, and before the XML/DOCTYPE
        // prolog is prepended so the scan sees only the serialized element tree.
        if (outputDecl != null &&
            outputDecl.EffectiveMethod is OutputMethod.Xml or OutputMethod.Xhtml or OutputMethod.Html &&
            !string.IsNullOrEmpty(outputDecl.Encoding))
        {
            output = EscapeUnrepresentableCharacters(output, outputDecl.Encoding);
        }

        // Emit XML declaration if requested
        if (outputDecl != null &&
            (outputDecl.EffectiveMethod == OutputMethod.Xml || outputDecl.EffectiveMethod == OutputMethod.Xhtml) &&
            outputDecl.OmitXmlDeclaration != true &&
            !output.StartsWith("<?xml", StringComparison.Ordinal))
        {
            var encoding = outputDecl.Encoding ?? "UTF-8";
            var version = outputDecl.Version ?? "1.0";
            var decl = $"<?xml version=\"{version}\" encoding=\"{encoding}\"";
            if (outputDecl.Standalone.HasValue)
                decl += outputDecl.Standalone.Value ? " standalone=\"yes\"" : " standalone=\"no\"";
            decl += "?>";
            output = decl + output;
        }

        // Emit DOCTYPE declaration if doctype-system (and optionally doctype-public) is set.
        // A zero-length doctype-system value is treated as if the parameter were absent, so no
        // DOCTYPE is emitted (Serialization erratum E31; XSLT tests output-0312/0313 use an empty
        // doctype-system to override an imported/named non-empty value back to "none").
        if (outputDecl != null && !string.IsNullOrEmpty(outputDecl.DoctypeSystem))
        {
            output = InsertDoctype(output, outputDecl.DoctypePublic, outputDecl.DoctypeSystem);
        }
        // HTML5 DOCTYPE: html/xhtml output with html-version >= 5 emits "<!DOCTYPE name>" using the
        // document element's serialized (case-preserving) name (Serialization 3.0 HTML/XHTML output
        // methods; W3C bug 20264 ruling — a DOCTYPE is emitted even when only doctype-public is set).
        // Skipped for a foreign-namespace XHTML root, which serializes by XML rules.
        else if (outputDecl != null && !foreignXhtmlRoot &&
                 outputDecl.EffectiveMethod is OutputMethod.Html or OutputMethod.Xhtml &&
                 Html5DoctypeApplies(outputDecl))
        {
            output = InsertHtml5Doctype(output);
        }

        // Prepend UTF-8 BOM when byte-order-mark="yes"
        if (outputDecl?.ByteOrderMark == true)
        {
            output = "\uFEFF" + output;
        }

        // escape-uri-attributes: in HTML/XHTML output, percent-encode non-ASCII characters
        // in URI-valued attributes (href, src, action, cite, data, formaction, poster, srcset, usemap)
        // when "yes" (the default). Even when "no", a URI-valued attribute still requires a literal
        // double quote to be emitted as a numeric character reference (&#34;) rather than the named
        // entity &quot; (output-0103c); the pass runs unconditionally for HTML/XHTML and the
        // percent-encoding half is gated on escape-uri-attributes.
        if (outputDecl != null &&
            (outputDecl.EffectiveMethod == OutputMethod.Html || outputDecl.EffectiveMethod == OutputMethod.Xhtml))
        {
            output = EscapeUriAttributes(output, percentEncode: outputDecl.EscapeUriAttributes != false);
        }

        // undeclare-prefixes="yes" only has effect for XML 1.1 output. Since this engine
        // produces XML 1.0 output, namespace undeclarations (xmlns:prefix="") are not
        // generated. This is correct per the serialization spec: undeclare-prefixes is a
        // no-op for XML 1.0 because namespace undeclarations are not permitted in XML 1.0.

        // Restore sentinel-escaped text content from method="text" result-documents.
        // Sentinels protect text node content (from xsl:sequence/xsl:value-of/xsl:text)
        // from StripXmlMarkup which only strips generated XML markup.
        if (output.Contains('\uFDD0', StringComparison.Ordinal))
        {
            output = output
                .Replace("\uFDD0", "<", StringComparison.Ordinal)
                .Replace("\uFDD1", ">", StringComparison.Ordinal)
                .Replace("\uFDD2", "&", StringComparison.Ordinal);
        }

        // HTML output method: script and style are CDATA (raw-text) elements \u2014 their content is
        // serialized verbatim with no XML escaping (Serialization 3.0, HTML output; output-0154/
        // 0159). The element tree is escaped as normal XML up to this point so every preceding
        // tag-scanning post-processor (PostProcessHtmlOutput, InsertContentTypeMeta, indentation,
        // EscapeUriAttributes) sees well-formed markup; only now, as the final step, is the
        // XML-escaping of script/style *content* reversed so <, >, & appear literally.
        if (outputDecl?.EffectiveMethod == OutputMethod.Html)
        {
            output = DefaultXsltExecutionContext.UnescapeHtmlRawTextElements(output);
        }

        return output;
    }

    /// <summary>
    /// Transforms with "raw" delivery: returns the raw XDM result value(s) instead of
    /// serialized output. Used by fn:transform with delivery-format='raw'.
    /// Function items, maps, and atomic values are preserved as-is.
    /// </summary>
    internal async Task<object?> TransformRawAsync(XdmNode source, XsltTransformOptions? options, XdmInMemoryStore? nodeStore)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new XsltTransformOptions();

        var outputBuilder = new StringBuilder();
        var context = new DefaultXsltExecutionContext(
            _stylesheet,
            _templateIndex,
            source,
            outputBuilder,
            options,
            nodeStore,
            _schemaProvider);
        context.Owner = this;

        // With no source document the focus for global variables is the supplied global context
        // item when there is one, and absent otherwise. Pushing AbsentFocus unconditionally made
        // a stylesheet with xsl:global-context-item use="required" fail on any global reading "."
        // even though fn:transform had been given the item.
        EnforceGlobalContextItem(options);

        var globalFocus = ResolveGlobalContextItem(options, nodeStore);
        if (!options.HasSourceDocument)
            context.PushContextItem(
                globalFocus ?? PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus,
                globalFocus != null ? 1 : 0,
                globalFocus != null ? 1 : 0);

        await InitializeGlobalsInDependencyOrderAsync(context, outputBuilder).ConfigureAwait(false);

        if (!options.HasSourceDocument)
            context.PopContextItem();

        BindExternalParameters(context, options);

        // Enable sequence collection to capture raw XDM values (function items, maps, etc.)
        context.BeginSequenceCollection();

        if (options.InitialFunction != null)
        {
            var funcName = options.InitialFunction.Value;
            var arity = options.InitialFunctionArguments.Count;
            var funcKey = (funcName, arity);
            if (!_stylesheet.Functions.TryGetValue(funcKey, out var func))
                throw new XsltException($"XTDE0041: No public stylesheet function '{funcName.LocalName}' with arity {arity}");
            // XTDE0041 has TWO halves - the function must exist AND be public - and this path
            // only had the first. TransformAsync (~line 318) carries both. Missing the
            // visibility half meant fn:transform happily invoked a PRIVATE package function
            // and returned its value, so a caller testing that private functions are
            // unreachable got a result instead of an error.
            if (func.Visibility is not (Ast.Visibility.Public or Ast.Visibility.Final))
                throw new XsltException(
                    $"XTDE0041: Stylesheet function '{funcName.LocalName}' is not public");
            // Translate XdmNode arguments into this engine's node store so XPath/xsl:evaluate
            // inside the function can navigate them (string-value, child axes, etc.).
            // Without this, a node passed across fn:transform's engine boundary reaches
            // the function as an opaque reference — name() works but string() returns ""
            // because the inner store can't resolve the foreign Children NodeIds
            // (Martin Honnen 2026-05-18, fn:transform with initial-function +
            // xsl:evaluate context-item passing a node from the outer engine).
            IReadOnlyList<object?> translatedArgs = TranslateNodeArgumentsToLocalStore(
                options.InitialFunctionArguments, context._nodeStore);
            var result = await context.CallXsltFunctionAsync(func, translatedArgs).ConfigureAwait(false);
            // Always preserve the raw function result through RawResult so callers can
            // surface the typed XDM value (xs:boolean, map, function, etc.) instead of
            // the text-serialized form (Martin Honnen 2026-05-18: fn:transform with
            // delivery-format='raw' and an XSLT-internal initial-function call was
            // returning the string "false" where the function returned xs:boolean true).
            options.RawResult.Value = WrapNodesForCrossStoreTransport(result, context);
            if (!options.ReturnRawXdm)
                context.SerializeFunctionResult(result, outputBuilder);
        }
        else if (options.InitialTemplate != null || (!options.HasSourceDocument && _stylesheet.NamedTemplates.Keys.Any(k => k.LocalName == "initial-template")))
        {
            var initialTemplate = options.InitialTemplate
                ?? _stylesheet.NamedTemplates.Keys.First(k => k.LocalName == "initial-template");
            // XTDE0040: Check that initial template is public (in packages)
            // In TransformRawAsync (used by fn:transform), check all templates since
            // fn:transform targets specific packages — xsl:expose visibility is enforced.
            if (_stylesheet.IsPackage
                && _stylesheet.NamedTemplates.TryGetValue(initialTemplate, out var rawTmpl)
                && rawTmpl.VisibilityAttr is not ("public" or "final"))
                throw new XsltException($"XTDE0040: Initial template '{initialTemplate.LocalName}' is not public");
            if (!options.HasSourceDocument)
                context.PushContextItem(
                    options.InitialContextItem ?? PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus,
                    options.InitialContextItem != null ? 1 : 0,
                    options.InitialContextItem != null ? 1 : 0);
            await context.CallTemplateAsync(initialTemplate, BuildInitialTemplateWithParams(options)).ConfigureAwait(false);
            if (!options.HasSourceDocument)
                context.PopContextItem();
        }
        else if (options.InitialModeSelect != null || options.InitialModeSelectValue != null)
        {
            var selectExpr = BuildInitialModeSelectExpression(context, options);
            var effectiveInitialMode = options.InitialMode ?? new QName(NamespaceId.None, "");
            await context.ApplyTemplatesAsync(
                selectExpr, effectiveInitialMode, [], BuildInitialTemplateWithParams(options)).ConfigureAwait(false);
        }
        else
        {
            var effectiveInitialMode = options.InitialMode ?? new QName(NamespaceId.None, "");
            await context.ApplyTemplatesAsync(
                ContextItemExpression.Instance,
                effectiveInitialMode, [], BuildInitialTemplateWithParams(options)).ConfigureAwait(false);
        }

        var rawItems = context.EndSequenceCollection();
        SecondaryResultDocuments = context.SecondaryResults;

        // If sequence items were collected, return them. Wrap any node items so they
        // survive the trip out of the inner store (caller re-anchors).
        if (rawItems.Count > 0)
        {
            object? raw = rawItems.Count == 1 ? rawItems[0] : rawItems.ToArray();
            return WrapNodesForCrossStoreTransport(raw, context);
        }

        // InitialFunction returned a single raw XDM value (booleans, maps, function items,
        // arrays, etc.) — surface it directly so type identity isn't lost to text
        // serialization. The InitialFunction branch sets RawResult unconditionally
        // (already wrapped above when set).
        if (options.InitialFunction != null && options.RawResult.Value != null)
            return options.RawResult.Value;

        // Fallback: if no sequence items but text was generated, return the text
        var text = outputBuilder.ToString();
        return !string.IsNullOrEmpty(text) ? text : null;
    }

    /// <summary>
    /// Wrapper for an XdmNode argument crossing fn:transform's engine boundary.
    /// The outer engine serializes the node to XML; the inner engine re-parses
    /// into its own node store so XPath/xsl:evaluate inside the called function
    /// can navigate children, compute string-value, etc.
    /// </summary>
    /// <summary>
    /// A node ferried out of the inner engine's store, as the serialized ROOT of its tree plus
    /// the child-index path from that root down to the node itself.
    /// </summary>
    /// <remarks>
    /// It used to carry the node's OWN serialization, which discards everything above it: a
    /// &lt;book&gt; came back as the root of a fresh one-element document, so fn:path() reported
    /// /Q{}book[1] for every result rather than /Q{}books[1]/Q{}book[1] and [2]. Reported by
    /// Martin Honnen via sxq, an XQuery Schematron implementation, calling fn:transform with
    /// delivery-format='raw'. Ancestors cannot survive a round-trip that never carried them.
    ///
    /// <see cref="ChildPath"/> is a chain of child indices, not an XPath: indices need no
    /// namespace context at the far end and cannot be ambiguous.
    /// </remarks>
    internal sealed record CrossStoreNodeRef(string Xml, bool IsElement, int[]? ChildPath = null);

    /// <summary>
    /// Returns a copy of <paramref name="args"/> with any <see cref="CrossStoreNodeRef"/>
    /// arguments re-parsed into <paramref name="store"/>. Non-wrapped arguments pass
    /// through unchanged.
    /// </summary>
#pragma warning disable CA1859
    private static List<object?> TranslateNodeArgumentsToLocalStore(
        IReadOnlyList<object?> args, XdmInMemoryStore? store)
#pragma warning restore CA1859
    {
        if (store == null || args.Count == 0)
            return args is List<object?> list ? list : new List<object?>(args);
        // Recurse via UnwrapCrossStoreValue rather than testing only the top level: an argument
        // may be a SEQUENCE of nodes, which arrives as an array of wrappers. Unwrapping only a
        // bare CrossStoreNodeRef left those arrays untouched, so a single-node argument worked
        // while a two-node one came through empty.
        var translated = new List<object?>(args.Count);
        var anyChanged = false;
        foreach (var arg in args)
        {
            var one = UnwrapCrossStoreValue(arg, store);
            if (!ReferenceEquals(one, arg)) anyChanged = true;
            translated.Add(one);
        }
        if (!anyChanged) return args is List<object?> list ? list : new List<object?>(args);
        return translated;
    }

    /// <summary>Child node ids of an element or document; empty for anything else.</summary>
    private static IReadOnlyList<NodeId> ChildIdsOf(Xdm.Nodes.XdmNode node) => node switch
    {
        Xdm.Nodes.XdmElement e => e.Children,
        Xdm.Nodes.XdmDocument d => d.Children,
        _ => System.Array.Empty<NodeId>(),
    };

    /// <summary>
    /// Wraps any <see cref="Xdm.Nodes.XdmElement"/> / <see cref="Xdm.Nodes.XdmDocument"/>
    /// items in <paramref name="value"/> as <see cref="CrossStoreNodeRef"/>s, serialized
    /// via <paramref name="context"/> so the inner engine's store can be walked while it
    /// is still live. The caller (e.g. <c>XsltTransformProvider</c>) re-anchors into its
    /// own store before handing the result back to the user.
    ///
    /// Without this wrap, raw-delivery node results carry Children/Parent NodeIds from
    /// the now-dying inner store: outer-side serialization yields stripped subtrees
    /// (Martin Honnen: <c>&lt;root/&gt;</c> instead of <c>&lt;root&gt;text&lt;/root&gt;</c>),
    /// and <c>path()</c> walks the wrong ancestor chain when Parent NodeIds collide with
    /// unrelated nodes in the outer store.
    /// </summary>
    internal static object? WrapNodesForCrossStoreTransport(object? value, DefaultXsltExecutionContext context)
    {
        if (value is null) return null;
        if (value is Xdm.Nodes.XdmElement or Xdm.Nodes.XdmDocument)
        {
            var node = (Xdm.Nodes.XdmNode)value;
            // Climb to the root, recording which child we came up through at each step, then
            // serialize the ROOT. Serializing the node alone is what stripped its ancestors.
            var path = new List<int>();
            var current = node;
            while (current.Parent is { } parentId
                   && context._nodeStore?.GetNode(parentId) is Xdm.Nodes.XdmNode parent)
            {
                var kids = ChildIdsOf(parent);
                var idx = -1;
                for (var k = 0; k < kids.Count; k++)
                {
                    if (kids[k] == current.Id) { idx = k; break; }
                }
                if (idx < 0) break;   // detached mid-climb; keep what we have
                path.Insert(0, idx);
                current = parent;
            }
            var xml = context.SerializeXdmNodeToXml(current);
            return new CrossStoreNodeRef(xml, IsElement: node is Xdm.Nodes.XdmElement,
                ChildPath: path.Count > 0 ? path.ToArray() : null);
        }
        if (value is object?[] arr)
        {
            var wrapped = new object?[arr.Length];
            for (var i = 0; i < arr.Length; i++)
                wrapped[i] = WrapNodesForCrossStoreTransport(arr[i], context);
            return wrapped;
        }
        return value;
    }

    /// <summary>
    /// Enforces xsl:global-context-item (XSLT 3.0 §3.7.2): a stylesheet declaring
    /// <c>use="required"</c> must be given a global context item, and one declaring
    /// <c>use="absent"</c> must not be.
    /// </summary>
    /// <remarks>
    /// Shared by every entry point. Two of them — the fn:transform paths — had no check at all,
    /// so a stylesheet that required a global context item and was given none ran on to fail as
    /// XPDY0002 when a global variable evaluated ".", reporting the downstream symptom instead
    /// of the spec-defined error for exactly this condition. The other two were also incomplete:
    /// they tested only for a source document, so an item supplied through fn:transform's
    /// global-context-item option did not count as satisfying "required" and, conversely, could
    /// be handed to a stylesheet declaring "absent" without complaint.
    /// </remarks>
    private void EnforceGlobalContextItem(XsltTransformOptions options)
    {
        var supplied = options.HasSourceDocument || options.GlobalContextItem != null;
        if (_stylesheet.GlobalContextItemUse == Ast.ContextItemUse.Required && !supplied)
            throw new XsltException("XTDE3086: The stylesheet requires a global context item (use=\"required\"), but none was supplied");
        if (_stylesheet.GlobalContextItemUse == Ast.ContextItemUse.Absent && supplied)
            throw new XsltException("XTSE3088: The stylesheet specifies that no global context item should be supplied (use=\"absent\"), but a global context item was provided");
    }

    /// <summary>
    /// The global context item to use as the focus while global variables are evaluated,
    /// re-anchoring a cross-store node into <paramref name="store"/> first.
    /// </summary>
    private static object? ResolveGlobalContextItem(XsltTransformOptions options, XdmInMemoryStore? store)
        => options.GlobalContextItem switch
        {
            CrossStoreNodeRef wrapped when store != null => ReparseCrossStoreNode(wrapped, store),
            var other => other,
        };

    /// <summary>
    /// Re-anchors any <see cref="CrossStoreNodeRef"/> in a parameter value into
    /// <paramref name="store"/>, recursing through sequences. Values carrying no wrapper pass
    /// through untouched.
    /// </summary>
    internal static object? UnwrapCrossStoreValue(object? value, XdmInMemoryStore? store)
    {
        if (store == null) return value;
        switch (value)
        {
            case CrossStoreNodeRef wrapped:
                return ReparseCrossStoreNode(wrapped, store);
            case object?[] items:
            {
                object?[]? rebuilt = null;
                for (var i = 0; i < items.Length; i++)
                {
                    var one = UnwrapCrossStoreValue(items[i], store);
                    if (!ReferenceEquals(one, items[i]))
                        (rebuilt ??= (object?[])items.Clone())[i] = one;
                }
                return rebuilt ?? items;
            }
            default:
                return value;
        }
    }

    private static object? ReparseCrossStoreNode(CrossStoreNodeRef wrapped, XdmInMemoryStore store)
    {
        if (string.IsNullOrEmpty(wrapped.Xml)) return null;
        try
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            doc.LoadXml(wrapped.Xml);
            var localDoc = ConvertToXdm(doc, store);
            if (wrapped.IsElement && localDoc.DocumentElement.HasValue)
            {
                var rootId = localDoc.DocumentElement.Value;
                return (object?)(store.GetNode(rootId) as Xdm.Nodes.XdmElement) ?? localDoc;
            }
            return localDoc;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Transforms a string XML source with raw delivery.
    /// </summary>
    /// <summary>
    /// Raw transform whose initial context item is an arbitrary XDM item — typically
    /// a map or array produced by a previous transform's <c>parse-json</c> call,
    /// chained back in via <see cref="XsltTransformer.TransformToSequenceAsync"/>.
    /// The XSLT data model allows the context item to be any item, but the engine
    /// surface previously required a source node, which silently lost non-node
    /// inputs (Martin Honnen 2026-05-21 JSON-chaining repro: lookup on
    /// XdmDocument because the map was replaced with a synthetic <c>&lt;empty/&gt;</c>).
    /// </summary>
    internal async Task<object?> TransformRawWithInitialContextItemAsync(
        object initialContextItem,
        XsltTransformOptions? options,
        XdmInMemoryStore nodeStore)
    {
        ArgumentNullException.ThrowIfNull(initialContextItem);
        ArgumentNullException.ThrowIfNull(nodeStore);
        options ??= new XsltTransformOptions();

        // Synthetic empty XdmDocument satisfies the executor's constructor (it always
        // wants *some* node to seed the context stack); the real focus is pushed below.
        var syntheticDocId = nodeStore.NextId();
        var syntheticDoc = new XdmDocument
        {
            Id = syntheticDocId,
            Document = new DocumentId(0),
            Children = [],
            BaseUri = options.SourceDocumentUri?.AbsoluteUri
        };
        nodeStore.Register(syntheticDoc);

        var outputBuilder = new StringBuilder();
        var context = new DefaultXsltExecutionContext(
            _stylesheet,
            _templateIndex,
            syntheticDoc,
            outputBuilder,
            options,
            nodeStore,
            _schemaProvider);
        context.Owner = this;

        // Globals see the global context item when one was supplied, absent otherwise — the
        // same rule as the no-source path above. A global variable declared select="." is
        // evaluated here, long before the initial focus below is established.
        EnforceGlobalContextItem(options);

        var globalFocus = ResolveGlobalContextItem(options, nodeStore);
        context.PushContextItem(
            globalFocus ?? PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus,
            globalFocus != null ? 1 : 0,
            globalFocus != null ? 1 : 0);
        await InitializeGlobalsInDependencyOrderAsync(context, outputBuilder).ConfigureAwait(false);
        context.PopContextItem();

        BindExternalParameters(context, options);

        // Replace the constructor's synthetic-doc focus with the caller's actual item.
        // PopContextItem first drops the synthetic doc that the constructor seeded.
        context.PopContextItem();
        context.PushContextItem(initialContextItem, 1, 1);

        context.BeginSequenceCollection();
        try
        {
            if (options.InitialTemplate != null
                || (!options.HasSourceDocument && _stylesheet.NamedTemplates.Keys.Any(k => k.LocalName == "initial-template")))
            {
                var initialTemplate = options.InitialTemplate
                    ?? _stylesheet.NamedTemplates.Keys.First(k => k.LocalName == "initial-template");
                if (_stylesheet.IsPackage
                    && _stylesheet.NamedTemplates.TryGetValue(initialTemplate, out var rawTmpl)
                    && rawTmpl.VisibilityAttr is not ("public" or "final"))
                    throw new XsltException($"XTDE0040: Initial template '{initialTemplate.LocalName}' is not public");
                await context.CallTemplateAsync(initialTemplate, BuildInitialTemplateWithParams(options)).ConfigureAwait(false);
            }
            else
            {
                var effectiveInitialMode = options.InitialMode ?? new QName(NamespaceId.None, "");
                // ContextItemExpression.Instance evaluates to '.' — the supplied item.
                await context.ApplyTemplatesAsync(
                    ContextItemExpression.Instance,
                    effectiveInitialMode, [], BuildInitialTemplateWithParams(options)).ConfigureAwait(false);
            }
        }
        finally
        {
            context.PopContextItem();
        }

        var rawItems = context.EndSequenceCollection();
        SecondaryResultDocuments = context.SecondaryResults;

        if (rawItems.Count > 0)
        {
            // When the caller wants a serialized string (TransformAsync, i.e. not the
            // raw-XDM chaining path used by TransformToSequenceAsync) and the principal
            // output is a JSON-family method, serialize the collected map / array / atomic
            // items as JSON rather than returning them raw — otherwise the string facade
            // ToString()s the result into a CLR type name like
            // "PhoenixmlDb.XQuery.Execution.OrderedXdmMap" (Martin Honnen 2026-06-14,
            // JSON input fed through TransformAsync(XdmSequence)).
            if (!options.ReturnRawXdm)
            {
                var jsonOutDecl = context.PrimaryOutputMatchedDeclaration ?? _stylesheet.Outputs.FirstOrDefault();
                if (jsonOutDecl?.EffectiveMethod is OutputMethod.Json or OutputMethod.Adaptive or OutputMethod.Csv)
                {
                    var jsonBuilder = new StringBuilder();
                    AppendJsonItems(rawItems, jsonBuilder, jsonOutDecl, nodeStore);
                    var jsonOut = jsonBuilder.ToString();
                    // Route JSON-family output through full finalization so character-maps /
                    // normalization / sentinel restore apply. FinalizeOutput only runs the
                    // indentation step for XML/HTML/XHTML methods, so already-emitted JSON is
                    // not re-indented or corrupted.
                    return FinalizeOutput(jsonOut, jsonOutDecl, context.PrincipalOutputCharacterMaps, FinalizeKind.Primary);
                }
            }

            object? raw = rawItems.Count == 1 ? rawItems[0] : rawItems.ToArray();
            return WrapNodesForCrossStoreTransport(raw, context);
        }

        if (outputBuilder.Length == 0)
            return null;

        var serialized = outputBuilder.ToString();

        // The raw-XDM chaining caller (TransformToSequenceAsync, ReturnRawXdm) re-parses
        // this serialized markup back into a navigable node downstream, so it must receive
        // the bare markup — full finalization would prepend an XML declaration / DOCTYPE /
        // BOM that breaks that re-parse. Leave it raw, matching the rawItems branches above.
        if (options.ReturnRawXdm)
            return serialized;

        // Route serialized output through the same full finalization the main TransformAsync
        // path applies — otherwise serialized output produced from a non-node (e.g. JSON-map)
        // initial context item ignored xsl:output indent="yes", character maps, normalization,
        // etc. (Martin Honnen 2026-06-12).
        var rawOutputDecl = context.PrimaryOutputMatchedDeclaration ?? _stylesheet.Outputs.FirstOrDefault();
        return FinalizeOutput(serialized, rawOutputDecl, context.PrincipalOutputCharacterMaps, FinalizeKind.Primary);
    }

    internal async Task<object?> TransformRawAsync(string xmlSource, XsltTransformOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(xmlSource);
        if (xmlSource.Length > 0 && xmlSource[0] == '\uFEFF')
            xmlSource = xmlSource[1..];

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(xmlSource);

        var nodeStore = new XdmInMemoryStore();
        var xdmDoc = ConvertToXdm(doc, nodeStore);

        _templateIndex.ResolvePatternNamespaces(nodeStore.InternNamespace);
        foreach (var keyDef in _stylesheet.Keys.Values)
        {
            TemplateIndex.ResolveNamespacesInPattern(keyDef.Match, nodeStore.InternNamespace);
            if (keyDef.OtherDefinitions != null)
                foreach (var other in keyDef.OtherDefinitions)
                    TemplateIndex.ResolveNamespacesInPattern(other.Match, nodeStore.InternNamespace);
        }
        foreach (var acc in _stylesheet.Accumulators.Values)
            foreach (var rule in acc.Rules)
                TemplateIndex.ResolveNamespacesInPattern(rule.Match, nodeStore.InternNamespace);
        foreach (var nt in _stylesheet.StripSpace)
            nt.ResolveNamespace(nodeStore.InternNamespace);
        foreach (var nt in _stylesheet.PreserveSpace)
            nt.ResolveNamespace(nodeStore.InternNamespace);
        if (_stylesheet.StripSpace.Count > 0)
            StripWhitespaceNodes(xdmDoc, _stylesheet.StripSpace, _stylesheet.PreserveSpace, nodeStore);

        return await TransformRawAsync(xdmDoc, options, nodeStore).ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes a single XDM item as JSON (for method="json") or adaptive output.
    /// </summary>
    /// <summary>
    /// Serializes XDM items as CSV (XSLT 4.0 output method="csv").
    /// Each array/sequence is a row; atomic values are cells.
    /// </summary>
    internal static string SerializeAsCsv(List<object?> items)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var item in items)
        {
            if (item is List<object?> row)
            {
                for (var i = 0; i < row.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendCsvField(sb, row[i]?.ToString() ?? "");
                }
                sb.Append('\n');
            }
            else if (item is object?[] arrRow)
            {
                for (var i = 0; i < arrRow.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendCsvField(sb, arrRow[i]?.ToString() ?? "");
                }
                sb.Append('\n');
            }
            else
            {
                AppendCsvField(sb, item?.ToString() ?? "");
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    private static void AppendCsvField(System.Text.StringBuilder sb, string value)
    {
        if (value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal) || value.Contains('\r', StringComparison.Ordinal))
        {
            sb.Append('"');
            sb.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
            sb.Append('"');
        }
        else
        {
            sb.Append(value);
        }
    }

    /// <summary>
    /// Drains the JSON sequence-collection accumulator on <paramref name="context"/> and
    /// serializes its items into <paramref name="outputBuilder"/> per the principal
    /// xsl:output declaration's method/indent/allow-duplicate-names settings. Used by
    /// both the non-streaming and streaming transform entry points so they share
    /// identical JSON/Adaptive/CSV emission — without this shared path, the streaming
    /// path fell through to <c>CreateMapAsync</c>'s no-accumulator branch which serialized
    /// in adaptive mode (unquoted strings, no indent). Reported by Martin Honnen.
    /// </summary>
    internal static void FinalizeJsonOutput(
        DefaultXsltExecutionContext context,
        StringBuilder outputBuilder,
        XsltOutput? principalOutput,
        XdmInMemoryStore? nodeStore,
        XsltTransformOptions options)
    {
        var jsonItems = context.EndSequenceCollection();
        if (options.ReturnRawXdm)
        {
            object? rawValue = jsonItems.Count switch
            {
                0 => null,
                1 => jsonItems[0],
                _ => jsonItems.ToArray()
            };
            options.RawResult.Value = WrapNodesForCrossStoreTransport(rawValue, context);
        }
        var textContent = outputBuilder.ToString();
        outputBuilder.Clear();
        if (!string.IsNullOrEmpty(textContent) && jsonItems.Count == 0)
        {
            outputBuilder.Append(textContent);
            return;
        }
        if (jsonItems.Count == 0)
            return;

        AppendJsonItems(jsonItems, outputBuilder, principalOutput, nodeStore);
    }

    /// <summary>
    /// Serializes a non-empty list of JSON-family items (map / array / atomic) into
    /// <paramref name="outputBuilder"/> per the principal output's method, indent, and
    /// allow-duplicate-names settings. Shared by <see cref="FinalizeJsonOutput"/> and the
    /// initial-context-item transform path so both emit identical JSON / Adaptive / CSV.
    /// </summary>
    internal static void AppendJsonItems(
        List<object?> jsonItems,
        StringBuilder outputBuilder,
        XsltOutput? principalOutput,
        XdmInMemoryStore? nodeStore)
    {
        var effectiveMethod = principalOutput?.EffectiveMethod ?? OutputMethod.Json;
        if (effectiveMethod == OutputMethod.Csv)
        {
            outputBuilder.Append(SerializeAsCsv(jsonItems));
            return;
        }

        var allowDupNames = principalOutput?.AllowDuplicateNames == true;
        var jsonNodeMethod = principalOutput?.JsonNodeOutputMethod;
        var jsonIndent = effectiveMethod == OutputMethod.Json
            && (principalOutput?.Indent == true);
        if (jsonItems.Count == 1)
        {
            outputBuilder.Append(SerializeItemAsJson(jsonItems[0], effectiveMethod == OutputMethod.Adaptive, allowDupNames, nodeStore, jsonNodeMethod, jsonIndent));
            return;
        }
        if (effectiveMethod == OutputMethod.Adaptive)
        {
            for (int i = 0; i < jsonItems.Count; i++)
            {
                if (i > 0) outputBuilder.Append('\n');
                outputBuilder.Append(SerializeItemAsJson(jsonItems[i], true, allowDupNames, nodeStore, jsonNodeMethod));
            }
            return;
        }
        outputBuilder.Append('[');
        for (int i = 0; i < jsonItems.Count; i++)
        {
            if (i > 0) outputBuilder.Append(',');
            if (jsonIndent) { outputBuilder.Append('\n'); outputBuilder.Append("  "); }
            outputBuilder.Append(SerializeItemAsJson(jsonItems[i], false, allowDupNames, nodeStore, jsonNodeMethod, jsonIndent, 1));
        }
        if (jsonIndent && jsonItems.Count > 0) outputBuilder.Append('\n');
        outputBuilder.Append(']');
    }

    /// <summary>
    /// Appends a JSON newline followed by <paramref name="depth"/> levels of two-space
    /// indentation. Single-sourced so the JSON output method (<see cref="SerializeItemAsJson"/>)
    /// and the xml-to-json emitter (<c>XsltXmlToJsonFunction.SerializeJsonElement</c>) produce
    /// byte-identical indented layouts.
    /// </summary>
    internal static void AppendJsonNewlineIndent(StringBuilder sb, int depth)
    {
        sb.Append('\n');
        sb.Append(' ', depth * 2);
    }

    /// <summary>Records a JSON object key for duplicate detection. Returns false if the key
    /// was already present (the caller raises its own error: SERE0022 for the json output
    /// method, FOJS0006 for fn:xml-to-json).</summary>
    internal static bool TryAddJsonKey(HashSet<string> seen, string key) => seen.Add(key);

    internal static string SerializeItemAsJson(object? item, bool adaptive, bool allowDuplicateNames = false, XdmInMemoryStore? store = null, string? jsonNodeOutputMethod = null, bool indent = false, int depth = 0)
    {
        if (item == null) return "null";

        // Indent only applies to JSON method (not adaptive). When indenting, expand
        // braces/brackets across lines with two-space indentation per depth level.
        var doIndent = indent && !adaptive;
        var colonSep = doIndent ? ": " : ":";

        switch (item)
        {
            case IDictionary<object, object?> map:
            {
                var sb = new StringBuilder();
                sb.Append('{');
                bool first = true;
                var seenKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (key, value) in map)
                {
                    var keyStr = DefaultXsltExecutionContext.StringValueOf(key);
                    // SERE0022: duplicate key names when allow-duplicate-names is no (default for JSON)
                    if (!adaptive && !allowDuplicateNames && !TryAddJsonKey(seenKeys, keyStr))
                        throw new XsltException($"SERE0022: Duplicate key '{keyStr}' in JSON map serialization");
                    if (!first) sb.Append(',');
                    if (doIndent) AppendJsonNewlineIndent(sb, depth + 1);
                    first = false;
                    sb.Append('"');
                    sb.Append(JsonEscapeString(keyStr));
                    sb.Append('"');
                    sb.Append(colonSep);
                    sb.Append(SerializeItemAsJson(value, adaptive, allowDuplicateNames, store, jsonNodeOutputMethod, indent, depth + 1));
                }
                if (!first && doIndent)
                    AppendJsonNewlineIndent(sb, depth);
                sb.Append('}');
                return sb.ToString();
            }
            case List<object?> array:
            {
                var sb = new StringBuilder();
                sb.Append('[');
                for (int i = 0; i < array.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (doIndent) AppendJsonNewlineIndent(sb, depth + 1);
                    // Array members that are sequences (object?[]) need flattening
                    if (array[i] is object?[] memberSeq)
                    {
                        if (memberSeq.Length == 1)
                            sb.Append(SerializeItemAsJson(memberSeq[0], adaptive, allowDuplicateNames, store, jsonNodeOutputMethod, indent, depth + 1));
                        else
                        {
                            sb.Append('[');
                            for (int j = 0; j < memberSeq.Length; j++)
                            {
                                if (j > 0) sb.Append(',');
                                if (doIndent) AppendJsonNewlineIndent(sb, depth + 2);
                                sb.Append(SerializeItemAsJson(memberSeq[j], adaptive, allowDuplicateNames, store, jsonNodeOutputMethod, indent, depth + 2));
                            }
                            if (memberSeq.Length > 0 && doIndent)
                                AppendJsonNewlineIndent(sb, depth + 1);
                            sb.Append(']');
                        }
                    }
                    else
                        sb.Append(SerializeItemAsJson(array[i], adaptive, allowDuplicateNames, store, jsonNodeOutputMethod, indent, depth + 1));
                }
                if (array.Count > 0 && doIndent)
                    AppendJsonNewlineIndent(sb, depth);
                sb.Append(']');
                return sb.ToString();
            }
            case bool b:
                return b ? "true" : "false";
            case int i:
                return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case long l:
                return l.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case double d:
                if (double.IsNaN(d) || double.IsInfinity(d))
                    return "null"; // JSON doesn't support NaN/Infinity
                return DefaultXsltExecutionContext.FormatDouble(d);
            case decimal m:
                return m.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case float f:
                if (float.IsNaN(f) || float.IsInfinity(f))
                    return "null";
                return DefaultXsltExecutionContext.FormatFloat(f);
            case string s:
                if (adaptive) return s; // Adaptive mode: strings output as-is (no quotes)
                return $"\"{JsonEscapeString(s)}\"";
            case object?[] seq:
            {
                // Sequence — serialize items
                if (seq.Length == 1) return SerializeItemAsJson(seq[0], adaptive, allowDuplicateNames, store, jsonNodeOutputMethod, indent, depth);
                var sb = new StringBuilder();
                sb.Append('[');
                for (int i = 0; i < seq.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (doIndent) AppendJsonNewlineIndent(sb, depth + 1);
                    sb.Append(SerializeItemAsJson(seq[i], adaptive, allowDuplicateNames, store, jsonNodeOutputMethod, indent, depth + 1));
                }
                if (seq.Length > 0 && doIndent)
                    AppendJsonNewlineIndent(sb, depth);
                sb.Append(']');
                return sb.ToString();
            }
            case PhoenixmlDb.XQuery.Ast.XQueryFunction func:
            {
                // Adaptive mode: function items are implementation-defined (XSLT 3.0 §26.3)
                // JSON mode: function items cannot be serialized (SERE0021)
                if (!adaptive)
                    throw new XsltException("SERE0021: Function items cannot be serialized using JSON output method");
                var funcName = func.Name.LocalName ?? "anonymous";
                return $"function#{funcName}/{func.Arity}";
            }
            case Xdm.Nodes.XdmNode node:
            {
                // Adaptive mode always serializes nodes as XML
                if (adaptive)
                    return SerializeXdmNodeAsXml(node, store);
                // JSON mode: json-node-output-method controls how nodes are serialized
                // Default is "xml" per XSLT 3.0 spec §26.1. When "html"/"xhtml", the node is
                // serialized using that output method (markup, Content-Type meta, HTML void-element
                // handling) and the resulting markup becomes the JSON string value — NOT reduced to
                // the node's string-value (output-0702 / output-0716 / output-0717).
                var nodeMethod = jsonNodeOutputMethod ?? "xml";
                if (nodeMethod == "xml")
                {
                    var xml = SerializeXdmNodeAsXml(node, store, omitXmlDeclaration: true);
                    return $"\"{JsonEscapeString(xml)}\"";
                }
                if (nodeMethod == "html" || nodeMethod == "xhtml")
                {
                    var htmlMethod = nodeMethod == "xhtml" ? OutputMethod.Xhtml : OutputMethod.Html;
                    var markup = SerializeXdmNodeAsXml(node, store, omitXmlDeclaration: true);
                    // Reuse the primary html/xhtml serialization: void-element minimization and the
                    // injected Content-Type <meta> as the first child of <head> (same treatment the
                    // non-streaming primary path applies).
                    markup = DefaultXsltExecutionContext.PostProcessHtmlOutput(markup, htmlMethod);
                    markup = DefaultXsltExecutionContext.InsertContentTypeMeta(
                        markup, new XsltOutput { Method = htmlMethod });
                    return $"\"{JsonEscapeString(markup)}\"";
                }
                // text method: use string value
                var nodeStr = node.StringValue ?? "";
                return $"\"{JsonEscapeString(nodeStr)}\"";
            }
            default:
                // Other atomic values: serialize as JSON string
                var strVal = DefaultXsltExecutionContext.StringValueOf(item);
                if (adaptive) return strVal;
                return $"\"{JsonEscapeString(strVal)}\"";
        }
    }

    /// <summary>
    /// Serializes an XDM node as XML markup for adaptive output.
    /// For documents and elements, uses the node store for full tree serialization.
    /// </summary>
    private static string SerializeXdmNodeAsXml(Xdm.Nodes.XdmNode node, XdmInMemoryStore? store = null, bool omitXmlDeclaration = false)
    {
        switch (node)
        {
            case Xdm.Nodes.XdmAttribute attr:
                return $"{(!string.IsNullOrEmpty(attr.Prefix) ? attr.Prefix + ":" : "")}{attr.LocalName}=\"{attr.Value}\"";
            case Xdm.Nodes.XdmComment comment:
                return $"<!--{comment.StringValue}-->";
            case Xdm.Nodes.XdmProcessingInstruction pi:
                return $"<?{pi.Target} {pi.StringValue}?>";
            case Xdm.Nodes.XdmText text:
                return text.Value ?? "";
            case Xdm.Nodes.XdmDocument:
            case Xdm.Nodes.XdmElement:
                if (store != null)
                    return SerializeNodeTreeAsXml(node, store, omitXmlDeclaration);
                return node.StringValue ?? "";
            default:
                return node.StringValue ?? "";
        }
    }

    /// <summary>
    /// Serializes an XDM document or element node to XML markup using the node store.
    /// </summary>
    private static string SerializeNodeTreeAsXml(Xdm.Nodes.XdmNode node, XdmInMemoryStore store, bool omitXmlDeclaration = false)
    {
        var sb = new System.Text.StringBuilder();

        if (node is Xdm.Nodes.XdmDocument doc)
        {
            if (!omitXmlDeclaration)
                sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            foreach (var childId in doc.Children)
            {
                var child = store.GetNode(childId);
                if (child != null)
                    SerializeNodeTreeRecursive(child, store, sb);
            }
        }
        else
        {
            SerializeNodeTreeRecursive(node, store, sb);
        }
        return sb.ToString();
    }

    private static void SerializeNodeTreeRecursive(Xdm.Nodes.XdmNode node, XdmInMemoryStore store, System.Text.StringBuilder sb)
    {
        switch (node)
        {
            case Xdm.Nodes.XdmElement elem:
            {
                var ns = store.GetNamespaceUri(elem.Namespace) ?? "";
                var name = !string.IsNullOrEmpty(elem.Prefix) ? $"{elem.Prefix}:{elem.LocalName}" : elem.LocalName;
                sb.Append('<').Append(name);

                // Namespace declarations
                foreach (var nsDecl in elem.NamespaceDeclarations)
                {
                    var declUri = store.GetNamespaceUri(nsDecl.Namespace) ?? "";
                    if (string.IsNullOrEmpty(nsDecl.Prefix))
                        sb.Append(" xmlns=\"").Append(declUri).Append('"');
                    else
                        sb.Append(" xmlns:").Append(nsDecl.Prefix).Append("=\"").Append(declUri).Append('"');
                }

                // Attributes
                foreach (var attrId in elem.Attributes)
                {
                    if (store.GetNode(attrId) is Xdm.Nodes.XdmAttribute attr)
                    {
                        var attrName = !string.IsNullOrEmpty(attr.Prefix) ? $"{attr.Prefix}:{attr.LocalName}" : attr.LocalName;
                        sb.Append(' ').Append(attrName).Append("=\"");
                        AppendEscapedXmlAttribute(sb, attr.Value);
                        sb.Append('"');
                    }
                }

                if (elem.Children.Count == 0)
                {
                    sb.Append("/>");
                }
                else
                {
                    sb.Append('>');
                    foreach (var childId in elem.Children)
                    {
                        var child = store.GetNode(childId);
                        if (child != null)
                            SerializeNodeTreeRecursive(child, store, sb);
                    }
                    sb.Append("</").Append(name).Append('>');
                }
                break;
            }
            case Xdm.Nodes.XdmText text:
                AppendEscapedXmlText(sb, text.Value ?? "");
                break;
            case Xdm.Nodes.XdmComment comment:
                sb.Append("<!--").Append(comment.Value).Append("-->");
                break;
            case Xdm.Nodes.XdmProcessingInstruction pi:
                sb.Append("<?").Append(pi.Target).Append(' ').Append(pi.Value).Append("?>");
                break;
        }
    }

    private static void AppendEscapedXmlAttribute(System.Text.StringBuilder sb, string value)
        => CharacterEscaper.AppendXmlAttribute(sb, value);

    private static void AppendEscapedXmlText(System.Text.StringBuilder sb, string value)
        => CharacterEscaper.AppendXmlText(sb, value);

    internal static string JsonEscapeString(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        XsltXmlToJsonFunction.AppendJsonString(s, sb);
        return sb.ToString();
    }

    /// <summary>
    /// Serializes an XDM item in adaptive output mode.
    /// Maps use XPath map{} notation, arrays use [...], strings are unquoted,
    /// and orphan attributes use name="value" format.
    /// </summary>
    internal static string SerializeItemAdaptive(object? item, XdmInMemoryStore? store = null)
    {
        if (item == null) return "";

        switch (item)
        {
            case Xdm.Nodes.XdmAttribute attr:
            {
                // Orphan attribute: name="value" format
                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(attr.Prefix))
                {
                    sb.Append(attr.Prefix);
                    sb.Append(':');
                }
                sb.Append(attr.LocalName);
                sb.Append("=\"");
                sb.Append(attr.Value);
                sb.Append('"');
                return sb.ToString();
            }
            case IDictionary<object, object?> map:
            {
                var sb = new StringBuilder();
                sb.Append("map{");
                bool first = true;
                foreach (var (key, value) in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    var keyStr = DefaultXsltExecutionContext.StringValueOf(key);
                    // Key is always quoted in map{} notation
                    sb.Append('"');
                    sb.Append(keyStr);
                    sb.Append("\":");
                    sb.Append(SerializeItemAdaptive(value, store));
                }
                sb.Append('}');
                return sb.ToString();
            }
            case List<object?> array:
            {
                var sb = new StringBuilder();
                sb.Append('[');
                for (int i = 0; i < array.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(SerializeItemAdaptive(array[i], store));
                }
                sb.Append(']');
                return sb.ToString();
            }
            case bool b:
                return b ? "true" : "false";
            case int i:
                return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case long l:
                return l.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case double d:
                return DefaultXsltExecutionContext.FormatDouble(d);
            case decimal m:
                return m.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case float f:
                return DefaultXsltExecutionContext.FormatFloat(f);
            case PhoenixmlDb.XQuery.Ast.XQueryFunction func:
            {
                var funcName = func.Name.LocalName ?? "anonymous";
                return $"function#{funcName}/{func.Arity}";
            }
            case Xdm.Nodes.XdmNode node when node is not Xdm.Nodes.XdmAttribute:
                return SerializeXdmNodeAsXml(node, store);
            default:
                return DefaultXsltExecutionContext.StringValueOf(item);
        }
    }

    /// <summary>
    /// Block-level HTML elements that should be preceded by a newline when indenting HTML output.
    /// </summary>
    private static readonly HashSet<string> HtmlBlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "head", "body", "div", "p", "table", "thead", "tbody", "tfoot", "tr", "th", "td",
        "ul", "ol", "li", "dl", "dt", "dd", "h1", "h2", "h3", "h4", "h5", "h6",
        "section", "article", "header", "footer", "nav", "main", "aside", "details", "summary",
        "form", "fieldset", "legend", "blockquote", "pre", "figure", "figcaption", "hr", "br",
        "script", "style", "link", "meta", "title", "base", "noscript"
    };

    /// <summary>
    /// Inserts a DOCTYPE declaration into the output, after any XML declaration and before the root element.
    /// Per the serialization spec:
    ///   - If both public and system: &lt;!DOCTYPE root PUBLIC "public" "system"&gt;
    ///   - If only system: &lt;!DOCTYPE root SYSTEM "system"&gt;
    /// </summary>
    internal static string InsertDoctype(string output, string? doctypePublic, string doctypeSystem)
    {
        // Find the root element name
        var searchStart = 0;
        if (output.StartsWith("<?xml", StringComparison.Ordinal))
        {
            var declEnd = output.IndexOf("?>", StringComparison.Ordinal);
            if (declEnd >= 0)
                searchStart = declEnd + 2;
        }

        // Skip the BOM if present
        if (searchStart < output.Length && output[searchStart] == '\uFEFF')
            searchStart++;

        // Find the first element start tag
        var elemStart = output.IndexOf('<', searchStart);
        while (elemStart >= 0 && elemStart < output.Length - 1)
        {
            var nextChar = output[elemStart + 1];
            // Skip processing instructions, comments
            if (nextChar == '?' || nextChar == '!')
            {
                elemStart = output.IndexOf('<', elemStart + 1);
                continue;
            }
            break;
        }

        if (elemStart < 0 || elemStart >= output.Length - 1)
            return output;

        // Extract the root element name
        var nameStart = elemStart + 1;
        var nameEnd = nameStart;
        while (nameEnd < output.Length && output[nameEnd] != ' ' && output[nameEnd] != '>'
               && output[nameEnd] != '/' && output[nameEnd] != '\t' && output[nameEnd] != '\n'
               && output[nameEnd] != '\r')
            nameEnd++;

        if (nameEnd <= nameStart)
            return output;

        var rootName = output[nameStart..nameEnd];

        // Build the DOCTYPE declaration. Each identifier is wrapped in double quotes,
        // unless it contains a '"' character, in which case single quotes are used so the
        // output stays well-formed (Serialization 3.0; XSLT test output-0311). An identifier
        // containing both quote characters is a serialization error, but is not emitted here.
        string doctype;
        if (doctypePublic != null)
            doctype = $"<!DOCTYPE {rootName} PUBLIC {QuoteDoctypeId(doctypePublic)} {QuoteDoctypeId(doctypeSystem)}>";
        else
            doctype = $"<!DOCTYPE {rootName} SYSTEM {QuoteDoctypeId(doctypeSystem)}>";

        // Insert before the root element, with a newline separator
        return output[..elemStart] + doctype + "\n" + output[elemStart..];
    }

    /// <summary>
    /// Wraps a doctype public/system identifier in quotes for the DOCTYPE declaration.
    /// Uses double quotes normally, but single quotes when the identifier contains a '"'
    /// character so the declaration remains well-formed (Serialization 3.0).
    /// </summary>
    private static string QuoteDoctypeId(string id)
        => id.Contains('"', StringComparison.Ordinal) ? $"'{id}'" : $"\"{id}\"";

    /// <summary>
    /// True when the html/xhtml output method should emit an HTML5 DOCTYPE, i.e. the html-version
    /// serialization parameter is present and parses to a decimal &gt;= 5.0 (leading/trailing
    /// whitespace tolerated per attribute-value normalization).
    /// </summary>
    private static bool Html5DoctypeApplies(XsltOutput outputDecl)
    {
        var hv = outputDecl.HtmlVersion?.Trim();
        if (string.IsNullOrEmpty(hv))
            return false;
        return decimal.TryParse(hv, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 5.0m;
    }

    /// <summary>
    /// Inserts an HTML5 DOCTYPE declaration ("&lt;!DOCTYPE name&gt;") immediately before the
    /// document element, after any XML declaration/BOM and any leading comments or processing
    /// instructions. The doctype name is the serialized qualified name of the document element
    /// (case-preserving) so "&lt;HTML&gt;" yields "&lt;!DOCTYPE HTML&gt;" per the serialization spec.
    /// </summary>
    private static string InsertHtml5Doctype(string output)
    {
        var searchStart = 0;
        if (output.StartsWith("<?xml", StringComparison.Ordinal))
        {
            var declEnd = output.IndexOf("?>", StringComparison.Ordinal);
            if (declEnd >= 0)
                searchStart = declEnd + 2;
        }
        if (searchStart < output.Length && output[searchStart] == '\uFEFF')
            searchStart++;

        var elemStart = FindDocumentElementStart(output, searchStart);
        if (elemStart < 0 || elemStart >= output.Length - 1)
            return output;

        var nameStart = elemStart + 1;
        var nameEnd = nameStart;
        while (nameEnd < output.Length && output[nameEnd] != ' ' && output[nameEnd] != '>'
               && output[nameEnd] != '/' && output[nameEnd] != '\t' && output[nameEnd] != '\n'
               && output[nameEnd] != '\r')
            nameEnd++;
        if (nameEnd <= nameStart)
            return output;

        var rootName = output[nameStart..nameEnd];

        // The HTML5 DOCTYPE is emitted only when the document element is the "html" element
        // (Serialization 3.0; output-0213 expects no DOCTYPE when the root is e.g. <body>, and
        // output-0724 when the root is <input>). The match is on the local name, case-insensitively
        // (<HTML>/<HtMl> still qualify), and the emitted name preserves the element's exact spelling.
        var localName = rootName;
        var colon = rootName.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
            localName = rootName[(colon + 1)..];
        if (!localName.Equals("html", StringComparison.OrdinalIgnoreCase))
            return output;

        return output[..elemStart] + $"<!DOCTYPE {rootName}>\n" + output[elemStart..];
    }

    /// <summary>
    /// Returns the index of the '&lt;' that opens the document (root) element, skipping any
    /// leading processing instructions and comments, starting the scan at <paramref name="from"/>.
    /// Returns -1 if no element start tag is found.
    /// </summary>
    private static int FindDocumentElementStart(string output, int from)
    {
        var elemStart = output.IndexOf('<', from);
        while (elemStart >= 0 && elemStart < output.Length - 1)
        {
            var nextChar = output[elemStart + 1];
            if (nextChar == '?' || nextChar == '!')
            {
                elemStart = output.IndexOf('<', elemStart + 1);
                continue;
            }
            break;
        }
        return elemStart;
    }

    /// <summary>
    /// Returns the namespace URI of the document (root) element as declared in the serialized
    /// output — the value of its default <c>xmlns</c> declaration, or the <c>xmlns:prefix</c>
    /// declaration matching its prefix — or the empty string when the root is in no namespace.
    /// Used to distinguish a genuine XHTML-namespace document from a foreign-namespace one.
    /// </summary>
    /// <summary>
    /// Resolves the Serialization 4.0 default output method from a serialized result tree, for use
    /// only when <c>xsl:output</c> specified no explicit method. Returns <see cref="OutputMethod.Html"/>
    /// when the document element is named <c>html</c> (matched case-sensitively, lowercase) in no
    /// namespace, <see cref="OutputMethod.Xhtml"/> when it is <c>html</c> in the XHTML namespace, and
    /// <c>null</c> (keep the xml default) for any other document element or a foreign-namespace
    /// <c>html</c>.
    /// </summary>
    private static OutputMethod? ResolveDefaultOutputMethod(string output)
    {
        var searchStart = 0;
        if (output.StartsWith("<?xml", StringComparison.Ordinal))
        {
            var declEnd = output.IndexOf("?>", StringComparison.Ordinal);
            if (declEnd >= 0)
                searchStart = declEnd + 2;
        }
        if (searchStart < output.Length && output[searchStart] == '\uFEFF')
            searchStart++;

        var elemStart = FindDocumentElementStart(output, searchStart);
        if (elemStart < 0 || elemStart >= output.Length - 1)
            return null;

        var nameStart = elemStart + 1;
        var nameEnd = nameStart;
        while (nameEnd < output.Length)
        {
            var c = output[nameEnd];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '>' || c == '/')
                break;
            nameEnd++;
        }
        if (nameEnd <= nameStart)
            return null;

        var qname = output[nameStart..nameEnd];
        var colon = qname.IndexOf(':', StringComparison.Ordinal);
        var localName = colon >= 0 ? qname[(colon + 1)..] : qname;
        // Match the document-element local name `html` CASE-SENSITIVELY (lowercase only). The
        // default output-method resolution promotes an html-rooted result to the html/xhtml method,
        // which injects the Content-Type meta, the HTML5 DOCTYPE, and the html indentation default.
        // The W3C conformance suite pins this to lowercase: a lowercase <html> root defaults to the
        // html method with an injected meta (decl/output output-0715/0130), while an UPPERCASE
        // <HTML> document element keeps the xml default and gets NO injected meta (insn/sequence
        // sequence-0601, a copy transform of an uppercase-HTML source). A case-insensitive match
        // wrongly promoted <HTML> to the html method and corrupted that assert-xml result tree.
        // Note: this is only the default-method *selection*. Once a method is chosen, the html/xhtml
        // method's own element handling (void elements, script/style raw text, the HTML5 DOCTYPE
        // name) remains case-insensitive as the serialization spec requires.
        if (!localName.Equals("html", StringComparison.Ordinal))
            return null;

        var rootNs = GetRootElementNamespace(output);
        if (rootNs.Length == 0)
            return OutputMethod.Html;
        if (rootNs == "http://www.w3.org/1999/xhtml")
            return OutputMethod.Xhtml;
        return null; // foreign-namespace <html> is not an HTML/XHTML result — keep xml.
    }

    private static string GetRootElementNamespace(string output)
    {
        var searchStart = 0;
        if (output.StartsWith("<?xml", StringComparison.Ordinal))
        {
            var declEnd = output.IndexOf("?>", StringComparison.Ordinal);
            if (declEnd >= 0)
                searchStart = declEnd + 2;
        }
        var elemStart = FindDocumentElementStart(output, searchStart);
        if (elemStart < 0)
            return "";
        var tagEnd = output.IndexOf('>', elemStart);
        if (tagEnd < 0)
            return "";
        var tag = output.Substring(elemStart, tagEnd - elemStart + 1);

        var nameEnd = 1;
        while (nameEnd < tag.Length && tag[nameEnd] != ' ' && tag[nameEnd] != '>' && tag[nameEnd] != '/'
               && tag[nameEnd] != '\t' && tag[nameEnd] != '\n' && tag[nameEnd] != '\r')
            nameEnd++;
        var qname = tag[1..nameEnd];
        var colon = qname.IndexOf(':', StringComparison.Ordinal);
        var declName = colon >= 0 ? "xmlns:" + qname[..colon] : "xmlns";
        return ExtractStartTagAttributeValue(tag, declName);
    }

    /// <summary>
    /// Extracts the quoted value of the attribute named <paramref name="attrName"/> from the
    /// serialized start tag <paramref name="tag"/>, matching whole attribute names only. Returns
    /// the empty string when the attribute is absent.
    /// </summary>
    private static string ExtractStartTagAttributeValue(string tag, string attrName)
    {
        var from = 0;
        while (true)
        {
            var idx = tag.IndexOf(attrName, from, StringComparison.Ordinal);
            if (idx < 0)
                return "";
            var before = idx == 0 ? ' ' : tag[idx - 1];
            var afterIdx = idx + attrName.Length;
            var after = afterIdx < tag.Length ? tag[afterIdx] : '\0';
            var boundedBefore = before == ' ' || before == '\t' || before == '\n' || before == '\r';
            var boundedAfter = after == '=' || after == ' ' || after == '\t';
            if (boundedBefore && boundedAfter)
            {
                var eq = tag.IndexOf('=', afterIdx);
                if (eq < 0)
                    return "";
                var q = eq + 1;
                while (q < tag.Length && (tag[q] == ' ' || tag[q] == '\t'))
                    q++;
                if (q >= tag.Length || (tag[q] != '"' && tag[q] != '\''))
                    return "";
                var quote = tag[q];
                var end = tag.IndexOf(quote, q + 1);
                return end < 0 ? "" : tag.Substring(q + 1, end - q - 1);
            }
            from = idx + attrName.Length;
        }
    }

    /// <summary>
    /// URI-valued HTML attributes whose values should be percent-encoded when
    /// escape-uri-attributes is enabled (default for HTML/XHTML output).
    /// </summary>
    private static readonly HashSet<string> HtmlUriAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "action", "cite", "data", "formaction", "poster", "srcset", "usemap"
    };

    /// <summary>
    /// Applies URI escaping to URI-valued HTML attributes: percent-encodes non-ASCII characters
    /// as required by the serialization spec when escape-uri-attributes="yes".
    /// </summary>
    internal static string EscapeUriAttributes(string output, bool percentEncode = true)
    {
        // Process attribute values in URI-valued attributes
        var sb = new StringBuilder(output.Length);
        int pos = 0;
        while (pos < output.Length)
        {
            if (output[pos] == '<' && pos + 1 < output.Length && output[pos + 1] != '/' && output[pos + 1] != '!')
            {
                // Find end of tag
                var tagEnd = output.IndexOf('>', pos);
                if (tagEnd < 0) { sb.Append(output, pos, output.Length - pos); break; }

                var tag = output.AsSpan(pos, tagEnd - pos + 1);

                // Check for attributes within the tag
                var tagStr = output[pos..(tagEnd + 1)];
                var processed = EscapeUriAttributesInTag(tagStr, percentEncode);
                sb.Append(processed);
                pos = tagEnd + 1;
            }
            else
            {
                sb.Append(output[pos]);
                pos++;
            }
        }
        return sb.ToString();
    }

    private static string EscapeUriAttributesInTag(string tag, bool percentEncode)
    {
        // Simple attribute pattern: attrname="value" or attrname='value'
        var sb = new StringBuilder(tag.Length);
        int i = 0;

        // Skip past the element name
        while (i < tag.Length && tag[i] != ' ' && tag[i] != '\t' && tag[i] != '\n'
               && tag[i] != '\r' && tag[i] != '>' && tag[i] != '/')
        {
            sb.Append(tag[i]);
            i++;
        }

        while (i < tag.Length)
        {
            if (tag[i] == '>' || (tag[i] == '/' && i + 1 < tag.Length && tag[i + 1] == '>'))
            {
                sb.Append(tag, i, tag.Length - i);
                break;
            }

            // Skip whitespace
            if (char.IsWhiteSpace(tag[i]))
            {
                sb.Append(tag[i]);
                i++;
                continue;
            }

            // Try to parse attribute name
            var nameStart = i;
            while (i < tag.Length && tag[i] != '=' && tag[i] != ' ' && tag[i] != '>'
                   && tag[i] != '/' && tag[i] != '\t')
                i++;

            var attrName = tag[nameStart..i];

            // Skip whitespace before =
            while (i < tag.Length && char.IsWhiteSpace(tag[i])) { sb.Append(tag[nameStart..i]); nameStart = i; i++; }

            if (i >= tag.Length || tag[i] != '=')
            {
                sb.Append(tag, nameStart, i - nameStart);
                continue;
            }

            sb.Append(tag, nameStart, i - nameStart);
            sb.Append('=');
            i++; // skip =

            // Skip whitespace after =
            while (i < tag.Length && char.IsWhiteSpace(tag[i])) { sb.Append(tag[i]); i++; }

            if (i >= tag.Length) break;

            var quote = tag[i];
            if (quote != '"' && quote != '\'')
            {
                sb.Append(tag[i]);
                i++;
                continue;
            }

            sb.Append(quote);
            i++; // skip opening quote

            var valueStart = i;
            while (i < tag.Length && tag[i] != quote) i++;
            var attrValue = tag[valueStart..i];

            if (HtmlUriAttributes.Contains(attrName))
            {
                // Percent-encode non-ASCII characters in URI attribute values when
                // escape-uri-attributes="yes". When "no", leave the value otherwise untouched but
                // still emit a double quote as a numeric character reference (&#34;) rather than the
                // named entity &quot; (output-0103c).
                sb.Append(percentEncode
                    ? EscapeUriAttributeValue(attrValue)
                    : attrValue.Replace("&quot;", "&#34;", StringComparison.Ordinal));
            }
            else
            {
                sb.Append(attrValue);
            }

            if (i < tag.Length)
            {
                sb.Append(quote); // closing quote
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Serializes a URI-valued HTML/XHTML attribute value under escape-uri-attributes="yes".
    /// The input is the already-serialized attribute text, so it may contain numeric character
    /// references (e.g. control chars the XML serializer emitted as &amp;#x96;) and XML entity
    /// escapes. It is first decoded to its logical characters (NFC-normalized), then re-emitted:
    /// each character outside printable ASCII is percent-encoded as its UTF-8 octets; the
    /// XML-significant characters &lt; &gt; &amp; " are (re-)escaped; and ASCII characters —
    /// including an existing '%' from a %xx sequence — pass through literally so they are never
    /// double-encoded. Matches the W3C Serialization URI-escaping rule for HTML/XHTML output
    /// (output-0102b/0102c).
    /// </summary>
    private static string EscapeUriAttributeValue(string rawValue)
    {
        var logical = DecodeXmlReferences(rawValue);
        // Normalize to Unicode NFC before mapping to UTF-8 octets. Source documents may hold
        // combining sequences (e.g. "a" + U+030A) that must be percent-encoded as the composed
        // codepoint (U+00E5 → %C3%A5), matching the serialization spec and the XQuery serializer
        // (output-0101/a/b).
        logical = logical.Normalize(System.Text.NormalizationForm.FormC);

        var sb = new StringBuilder(logical.Length * 2);
        for (int i = 0; i < logical.Length; i++)
        {
            char c = logical[i];
            switch (c)
            {
                case '<': sb.Append("&lt;"); continue;
                case '>': sb.Append("&gt;"); continue;
                case '&': sb.Append("&amp;"); continue;
                case '"': sb.Append("&#34;"); continue;
            }
            if (c <= 127)
            {
                sb.Append(c);
                continue;
            }
            // Non-ASCII: percent-encode the codepoint's UTF-8 octets (combining surrogate pairs).
            int cp;
            if (char.IsHighSurrogate(c) && i + 1 < logical.Length && char.IsLowSurrogate(logical[i + 1]))
            {
                cp = char.ConvertToUtf32(c, logical[i + 1]);
                i++;
            }
            else
            {
                cp = c;
            }
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(char.ConvertFromUtf32(cp)))
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"%{b:X2}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Decodes the XML entity references (&amp;lt; &amp;gt; &amp;amp; &amp;quot; &amp;apos;) and
    /// numeric character references (&amp;#nnn; / &amp;#xhh;) that appear in a serialized attribute
    /// value back to their logical characters. Used before URI-escaping so a control character the
    /// serializer wrote as a character reference is percent-encoded rather than left as a reference.
    /// </summary>
    private static string DecodeXmlReferences(string s)
    {
        if (s.IndexOf('&', StringComparison.Ordinal) < 0)
            return s;

        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '&')
            {
                int semi = s.IndexOf(';', i + 1);
                if (semi > i)
                {
                    var ent = s.Substring(i + 1, semi - i - 1);
                    string? decoded = ent switch
                    {
                        "lt" => "<",
                        "gt" => ">",
                        "amp" => "&",
                        "quot" => "\"",
                        "apos" => "'",
                        _ => null
                    };
                    if (decoded == null && ent.Length > 1 && ent[0] == '#')
                    {
                        var num = ent.AsSpan(1);
                        bool ok;
                        int cp;
                        if (num.Length > 1 && (num[0] == 'x' || num[0] == 'X'))
                            ok = int.TryParse(num[1..], System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out cp);
                        else
                            ok = int.TryParse(num, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out cp);
                        if (ok && cp >= 0 && cp <= 0x10FFFF && !(cp >= 0xD800 && cp <= 0xDFFF))
                            decoded = char.ConvertFromUtf32(cp);
                    }
                    if (decoded != null)
                    {
                        sb.Append(decoded);
                        i = semi + 1;
                        continue;
                    }
                }
            }
            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Applies indentation to serialized output when xsl:output indent="yes" is specified.
    /// For XML/XHTML, re-parses and re-serializes with XmlWriter indentation.
    /// For HTML, adds newlines and indentation before block-level elements.
    /// </summary>
    internal static string ApplyIndentation(string output, OutputMethod method, HashSet<QName>? suppressIndentation = null)
    {
        if (method is OutputMethod.Xml or OutputMethod.Xhtml)
        {
            var result = ApplyXmlIndentation(output);
            if (suppressIndentation != null && suppressIndentation.Count > 0)
                result = RemoveSuppressedIndentation(result, suppressIndentation);
            return result;
        }
        else if (method == OutputMethod.Html)
        {
            var result = ApplyHtmlIndentation(output);
            if (suppressIndentation != null && suppressIndentation.Count > 0)
                result = RemoveSuppressedIndentation(result, suppressIndentation);
            return result;
        }
        return output;
    }

    /// <summary>
    /// Post-processes indented output to remove indentation whitespace inside elements
    /// listed in suppress-indentation. Collapses added whitespace-only text nodes
    /// between child elements back to no whitespace.
    /// </summary>
    private static string RemoveSuppressedIndentation(string output, HashSet<QName> suppressedElements)
    {
        // Build a simple set of local names for fast matching (most stylesheets use unprefixed names)
        var suppressedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var qn in suppressedElements)
            suppressedNames.Add(qn.LocalName);

        var sb = new StringBuilder(output.Length);
        int suppressDepth = 0; // nesting depth inside suppressed elements
        int pos = 0;
        while (pos < output.Length)
        {
            if (output[pos] == '<')
            {
                var tagEnd = output.IndexOf('>', pos);
                if (tagEnd < 0) { sb.Append(output, pos, output.Length - pos); break; }

                bool isClosing = pos + 1 < output.Length && output[pos + 1] == '/';
                bool isSelfClosing = output[tagEnd - 1] == '/';
                bool isComment = pos + 3 < output.Length && output[pos + 1] == '!' && output[pos + 2] == '-';
                bool isPI = pos + 1 < output.Length && output[pos + 1] == '?';
                bool isDoctype = pos + 1 < output.Length && output[pos + 1] == '!';

                // Extract element name
                string? elemName = null;
                if (!isComment && !isPI && !isDoctype)
                {
                    int nameStart = isClosing ? pos + 2 : pos + 1;
                    int nameEnd2 = nameStart;
                    while (nameEnd2 <= tagEnd && nameEnd2 < output.Length
                           && output[nameEnd2] != ' ' && output[nameEnd2] != '>'
                           && output[nameEnd2] != '/' && output[nameEnd2] != '\t'
                           && output[nameEnd2] != '\n' && output[nameEnd2] != '\r')
                        nameEnd2++;
                    if (nameEnd2 > nameStart)
                        elemName = output[nameStart..nameEnd2];
                }

                // Strip the prefix for matching (e.g., "ns:elem" -> "elem")
                var localName = elemName;
                if (localName != null && localName.Contains(':', StringComparison.Ordinal))
                    localName = localName[(localName.IndexOf(':', StringComparison.Ordinal) + 1)..];

                if (isClosing && localName != null && suppressedNames.Contains(localName))
                {
                    // Closing a suppressed element
                    suppressDepth = Math.Max(0, suppressDepth - 1);
                }

                sb.Append(output, pos, tagEnd - pos + 1);
                pos = tagEnd + 1;

                if (!isClosing && !isSelfClosing && localName != null && suppressedNames.Contains(localName))
                {
                    // Opening a suppressed element
                    suppressDepth++;
                }
            }
            else if (suppressDepth > 0)
            {
                // Inside a suppressed element.
                if (output[pos] == ' ' || output[pos] == '\t' || output[pos] == '\n' || output[pos] == '\r')
                {
                    // Whitespace run. The indentation the indenter inserted before a suppressed
                    // element's closing tag is always a newline followed by spaces ('\n' + indent).
                    // When a whitespace run both contains a newline and precedes a tag, only the
                    // part from the FIRST newline onward is inserted indentation to drop; anything
                    // before that newline is significant text whitespace (e.g. a trailing space in
                    // the source content) and must be preserved. A run with no newline, or one not
                    // followed by a tag, is entirely significant text whitespace.
                    int wsStart = pos;
                    int firstNewline = -1;
                    while (pos < output.Length && (output[pos] == ' ' || output[pos] == '\t'
                           || output[pos] == '\n' || output[pos] == '\r'))
                    {
                        if (firstNewline < 0 && (output[pos] == '\n' || output[pos] == '\r'))
                            firstNewline = pos;
                        pos++;
                    }
                    bool precedesTag = pos < output.Length && output[pos] == '<';
                    if (firstNewline >= 0 && precedesTag)
                    {
                        // Preserve the significant prefix before the inserted indentation; drop the
                        // '\n' + indent that the indenter added.
                        if (firstNewline > wsStart)
                            sb.Append(output, wsStart, firstNewline - wsStart);
                    }
                    else
                    {
                        sb.Append(output, wsStart, pos - wsStart);
                    }
                }
                else
                {
                    // Non-whitespace text content inside a suppressed element: preserve it
                    // verbatim and advance. (The previous code did not advance pos here, so the
                    // first text character inside a suppressed element — e.g. the 'L' of a
                    // <p>Lorem…</p> under suppress-indentation="p" — spun the loop forever:
                    // the decl/output-0725/0726 hang.)
                    sb.Append(output[pos]);
                    pos++;
                }
            }
            else
            {
                sb.Append(output[pos]);
                pos++;
            }
        }
        return sb.ToString();
    }

    private static string ApplyXmlIndentation(string output)
    {
        try
        {
            // Check for and strip XML declaration before parsing, then re-add it after
            string? xmlDecl = null;
            var parseInput = output;
            if (parseInput.StartsWith("<?xml", StringComparison.Ordinal))
            {
                var declEnd = parseInput.IndexOf("?>", StringComparison.Ordinal);
                if (declEnd >= 0)
                {
                    xmlDecl = parseInput[..(declEnd + 2)];
                    parseInput = parseInput[(declEnd + 2)..].TrimStart('\r', '\n');
                }
            }

            var xdoc = System.Xml.Linq.XDocument.Parse(parseInput, System.Xml.Linq.LoadOptions.PreserveWhitespace);
            var settings = new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                OmitXmlDeclaration = true, // We handle the declaration separately
                NewLineHandling = NewLineHandling.Replace
            };
            using var sw = new System.IO.StringWriter();
            using (var xw = XmlWriter.Create(sw, settings))
            {
                xdoc.WriteTo(xw);
            }
            var indented = sw.ToString();
            return xmlDecl != null ? xmlDecl + "\n" + indented : indented;
        }
        catch (XmlException)
        {
            // If the output isn't well-formed XML (e.g., multiple root elements or fragments),
            // fall back to the unindented output rather than crashing
            return output;
        }
    }

    private static string ApplyHtmlIndentation(string output)
    {
        // Simple HTML indentation: add newlines and indentation before block-level elements
        var sb = new StringBuilder(output.Length + output.Length / 10);
        int depth = 0;
        int i = 0;
        while (i < output.Length)
        {
            if (output[i] == '<')
            {
                // Find the end of the tag
                var tagEnd = output.IndexOf('>', i);
                if (tagEnd < 0)
                {
                    sb.Append(output, i, output.Length - i);
                    break;
                }

                var tag = output.AsSpan(i, tagEnd - i + 1);
                bool isClosing = tag.Length > 1 && tag[1] == '/';
                bool isSelfClosing = tag[^2] == '/' || tag.EndsWith("/>".AsSpan());
                bool isComment = tag.StartsWith("<!--".AsSpan());
                bool isDoctype = tag.StartsWith("<!DOCTYPE".AsSpan(), StringComparison.OrdinalIgnoreCase);
                bool isProcessingInstruction = tag.Length > 1 && tag[1] == '?';

                // Extract the element name
                string? elemName = null;
                if (!isComment && !isDoctype && !isProcessingInstruction)
                {
                    int nameStart = isClosing ? 2 : 1;
                    int nameEnd = nameStart;
                    while (nameEnd < tag.Length && tag[nameEnd] != ' ' && tag[nameEnd] != '>'
                           && tag[nameEnd] != '/' && tag[nameEnd] != '\t' && tag[nameEnd] != '\n')
                        nameEnd++;
                    if (nameEnd > nameStart)
                        elemName = tag[nameStart..nameEnd].ToString();
                }

                bool isBlock = elemName != null && HtmlBlockElements.Contains(elemName);

                // An empty block element (start tag immediately followed by its end tag, e.g.
                // <title></title> produced by PostProcessHtmlOutput from <title/>) must serialize
                // inline: the HTML serializer does not insert indentation between a start tag and
                // its matching end tag when there is no content (result-document-0209/0214/0223/
                // 0224 assert an exact <title></title>). Detect this by checking that the tag
                // immediately preceding this closing tag in the source is the matching opening tag.
                bool emptyBlockClose = false;
                if (isBlock && isClosing && elemName != null && i > 0 && output[i - 1] == '>')
                {
                    var prevStart = output.LastIndexOf('<', i - 1);
                    if (prevStart >= 0)
                    {
                        var prevTag = output.AsSpan(prevStart, i - prevStart);
                        bool prevClosing = prevTag.Length > 1 && prevTag[1] == '/';
                        bool prevSelfClosing = prevTag.Length > 1 && prevTag[^2] == '/';
                        if (!prevClosing && !prevSelfClosing)
                        {
                            int pn = 1, pe = 1;
                            while (pe < prevTag.Length && prevTag[pe] != ' ' && prevTag[pe] != '>'
                                   && prevTag[pe] != '/' && prevTag[pe] != '\t' && prevTag[pe] != '\n')
                                pe++;
                            emptyBlockClose = pe > pn
                                && prevTag[pn..pe].Equals(elemName.AsSpan(), StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }

                // A block element whose closing tag is immediately preceded by character data
                // (e.g. <body>hello</body>, <h1>Section 1 of 3</h1>, <p>x</p>) serializes inline:
                // the HTML serializer must not insert a newline between an element's text content
                // and its closing tag, which would inject insignificant whitespace into the
                // element's string value (result-document-0701 serialization match; result-
                // document-1301 assert-xml). Only a '>' (child-element boundary) or existing '\n'
                // immediately before the close keeps the normal block indentation.
                bool textContentClose = isBlock && isClosing && !emptyBlockClose && i > 0
                    && output[i - 1] != '>' && !char.IsWhiteSpace(output[i - 1]);

                if (isBlock)
                {
                    if (isClosing)
                        depth = Math.Max(0, depth - 1);

                    // Add newline and indentation before block elements, except when closing an
                    // empty element inline (emptyBlockClose) or an element with text content
                    // (textContentClose).
                    if (!emptyBlockClose && !textContentClose)
                    {
                        if (sb.Length > 0 && sb[^1] != '\n')
                            sb.Append('\n');
                        sb.Append(' ', depth * 2);
                    }

                    if (!isClosing && !isSelfClosing)
                        depth++;
                }

                sb.Append(output, i, tagEnd - i + 1);
                i = tagEnd + 1;
            }
            else
            {
                sb.Append(output[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Private-use sentinels bracketing a character-map replacement string so that a
    /// later Unicode-normalization pass leaves the replacement untouched
    /// (character-map-025/028: "characters produced by a character map are immune to
    /// Unicode normalization"). Only emitted when <c>protectMappedOutput</c> is set,
    /// i.e. when a normalization-form is in effect; stripped by
    /// <see cref="NormalizeExceptMappedRegions"/>.
    /// </summary>
    private const char MapGuardStart = '\uE000';
    private const char MapGuardEnd = '\uE001';

    private string ApplyCharacterMaps(string output, List<QName> useCharacterMaps, OutputMethod method = OutputMethod.Xml, bool protectMappedOutput = false, Dictionary<QName, XsltCharacterMap>? charMaps = null)
    {
        // Character maps resolve in the declaring package's registry (package-local per
        // XSLT 3.0 §3.6.7); callers pass the used package's map when applicable.
        charMaps ??= _stylesheet.CharacterMaps;
        // Collect character mappings from referenced maps in declaration order.
        // Later maps in the list override earlier ones (XSLT 3.0 §25.1). Keyed by Unicode
        // code point so astral characters participate (character-map-007/010).
        var allMappings = new Dictionary<int, string>();
        var visited = new HashSet<QName>();
        foreach (var mapName in useCharacterMaps)
        {
            if (charMaps.TryGetValue(mapName, out var map))
                CollectCharacterMappings(map, allMappings, visited, charMaps);
        }

        if (allMappings.Count == 0)
            return output;
        var sb = new StringBuilder(output.Length);
        var inTag = false;
        var inCdata = false;
        var inAttrValue = false;
        var attrQuote = '"';
        var mapThisAttr = true;
        var isHtmlLike = method is OutputMethod.Html or OutputMethod.Xhtml;
        var attrNameToken = new StringBuilder();

        void EmitReplacement(string replacement)
        {
            if (protectMappedOutput)
            {
                sb.Append(MapGuardStart).Append(replacement).Append(MapGuardEnd);
            }
            else
            {
                sb.Append(replacement);
            }
        }

        for (var i = 0; i < output.Length; i++)
        {
            var c = output[i];
            // Track CDATA sections — character maps do not apply inside CDATA
            if (!inTag && i + 8 < output.Length && output[i] == '<' && output[i + 1] == '!' &&
                output[i + 2] == '[' && output[i + 3] == 'C' && output[i + 4] == 'D' &&
                output[i + 5] == 'A' && output[i + 6] == 'T' && output[i + 7] == 'A' &&
                output[i + 8] == '[')
            {
                inCdata = true;
                sb.Append(c);
                continue;
            }
            if (inCdata)
            {
                if (i + 2 < output.Length && c == ']' && output[i + 1] == ']' && output[i + 2] == '>')
                {
                    sb.Append("]]>");
                    i += 2;
                    inCdata = false;
                }
                else
                {
                    sb.Append(c);
                }
                continue;
            }
            // Track XML tags. Character maps do not apply to markup delimiters, element
            // names, or attribute names — but per XSLT 3.0 §20 they DO apply to attribute
            // VALUES (as well as text content). Track attribute-value regions inside a tag.
            if (c == '<' && !inTag)
            {
                inTag = true;
                attrNameToken.Clear();
                sb.Append(c);
                continue;
            }
            if (inTag)
            {
                if (inAttrValue)
                {
                    if (c == attrQuote)
                    {
                        inAttrValue = false;
                        sb.Append(c);
                    }
                    else if (mapThisAttr && TryMapLogicalChar(output, ref i, allMappings, out var attrRepl))
                    {
                        EmitReplacement(attrRepl);
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    continue;
                }
                if (c == '>')
                {
                    inTag = false;
                    attrNameToken.Clear();
                    sb.Append(c);
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    inAttrValue = true;
                    attrQuote = c;
                    // Character maps apply to attribute VALUES, except URI-valued attributes
                    // in HTML/XHTML output where escape-uri-attributes escaping applies instead
                    // (character-map-009). Names are accumulated in attrNameToken up to '='.
                    var attrName = attrNameToken.ToString();
                    mapThisAttr = !(isHtmlLike && HtmlUriAttributes.Contains(attrName));
                    attrNameToken.Clear();
                    sb.Append(c);
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    attrNameToken.Clear();
                }
                else if (c != '=' && c != '/')
                {
                    attrNameToken.Append(c);
                }
                sb.Append(c);
                continue;
            }
            // Apply character map to text content
            if (TryMapLogicalChar(output, ref i, allMappings, out var replacement))
                EmitReplacement(replacement);
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// At position <paramref name="i"/> in the already-escaped serialized string, determine
    /// the <em>logical source character</em> (the character as it appeared in the result
    /// tree, before XML escaping) and look it up in <paramref name="mappings"/>. This lets
    /// character maps key on the original character even though the serializer has already
    /// turned it into an entity reference — character maps are, per the serialization spec,
    /// applied to the pre-escaped characters, and their replacement is emitted verbatim
    /// (character-map-023/026). The logical character may be:
    /// <list type="bullet">
    /// <item>a predefined or numeric entity reference (<c>&amp;lt;</c>, <c>&amp;#100000;</c>, …)</item>
    /// <item>an astral character encoded as a UTF-16 surrogate pair (character-map-007)</item>
    /// <item>an ordinary BMP character</item>
    /// </list>
    /// On a hit, <paramref name="i"/> is advanced to the last consumed code unit (the caller's
    /// loop increment steps past it) and the replacement is returned. On a miss, <paramref name="i"/>
    /// is left unchanged so the caller emits the single code unit verbatim.
    /// </summary>
    private static bool TryMapLogicalChar(string s, ref int i, Dictionary<int, string> mappings, out string replacement)
    {
        var c = s[i];
        // Entity reference: decode to its code point for the lookup, but only consume it
        // (advance i to the ';') when a mapping actually fires. A miss leaves the '&' to be
        // emitted verbatim and the remaining entity characters handled on later iterations.
        if (c == '&')
        {
            var semi = s.IndexOf(';', i + 1);
            if (semi > i && semi - i <= 10 && TryDecodeEntity(s, i, semi, out var entityCp)
                && mappings.TryGetValue(entityCp, out var entityRep))
            {
                i = semi;
                replacement = entityRep;
                return true;
            }
        }
        // Astral character as a surrogate pair.
        else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            var cp = char.ConvertToUtf32(c, s[i + 1]);
            if (mappings.TryGetValue(cp, out var astralRep))
            {
                i++; // consume the low surrogate; caller's loop steps past it
                replacement = astralRep;
                return true;
            }
        }
        // Ordinary BMP character.
        else if (mappings.TryGetValue(c, out var bmpRep))
        {
            replacement = bmpRep;
            return true;
        }

        replacement = string.Empty;
        return false;
    }

    /// <summary>
    /// Decodes the entity reference occupying <c>s[start..end]</c> (where <c>s[start]=='&amp;'</c>
    /// and <c>s[end]==';'</c>) to its Unicode code point. Handles the five predefined XML
    /// entities and decimal / hexadecimal numeric character references. Returns false for
    /// anything else.
    /// </summary>
    private static bool TryDecodeEntity(string s, int start, int end, out int codePoint)
    {
        codePoint = 0;
        var inner = s.AsSpan(start + 1, end - start - 1);
        if (inner.Length == 0)
            return false;
        if (inner[0] == '#')
        {
            if (inner.Length >= 2 && (inner[1] == 'x' || inner[1] == 'X'))
                return int.TryParse(inner[2..], System.Globalization.NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out codePoint) && codePoint > 0;
            return int.TryParse(inner, System.Globalization.NumberStyles.None,
                CultureInfo.InvariantCulture, out codePoint) && codePoint > 0;
        }
        switch (inner)
        {
            case "amp": codePoint = '&'; return true;
            case "lt": codePoint = '<'; return true;
            case "gt": codePoint = '>'; return true;
            case "quot": codePoint = '"'; return true;
            case "apos": codePoint = '\''; return true;
            default: return false;
        }
    }

    /// <summary>
    /// Applies Unicode normalization to <paramref name="output"/> while leaving character-map
    /// replacement regions — bracketed by <see cref="MapGuardStart"/>/<see cref="MapGuardEnd"/> —
    /// untouched, then strips the guards. Characters produced by a character map are immune to
    /// normalization (character-map-025/028).
    /// </summary>
    /// <summary>
    /// Applies Unicode normalization to the CHARACTER DATA of a serialized result — text nodes,
    /// attribute values and CDATA content — leaving element and attribute names, and all other
    /// markup, untouched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to normalize the entire serialized string, which rewrote NAMES too. With
    /// <c>normalization-form="NFD"</c> an attribute written <c>üss="üß"</c> in the source came
    /// out as
    /// </para>
    /// <code>
    /// u&#x308;ss="u&amp;#776;ß"      (name decomposed as well as the value)
    /// </code>
    /// <para>
    /// where W3C normalize-unicode-011/013/015/016 require the name to stay <c>üss</c>. A name is
    /// an identity compared by code point, so normalizing it silently renames the attribute and
    /// the document no longer says what it said.
    /// </para>
    /// <para>
    /// The walk mirrors <see cref="EscapeUnrepresentableCharacters"/>, which already had to make
    /// exactly this content-versus-markup distinction; keeping the two the same shape means a
    /// fix to the notion of "content" lands in both.
    /// </para>
    /// </remarks>
    private static string NormalizeContentRegions(string output, System.Text.NormalizationForm form)
    {
        var sb = new StringBuilder(output.Length);
        var content = new StringBuilder();

        void FlushContent()
        {
            if (content.Length == 0) return;
            sb.Append(content.ToString().Normalize(form));
            content.Clear();
        }

        var inTag = false;
        var inAttrValue = false;
        var attrQuote = '"';

        for (var i = 0; i < output.Length; i++)
        {
            var c = output[i];

            // Character-map replacement regions are immune, as before: guards bracket text the
            // user asked for verbatim, and the guards themselves are dropped.
            if (c == MapGuardStart)
            {
                FlushContent();
                var close = output.IndexOf(MapGuardEnd, i + 1);
                if (close < 0)
                {
                    sb.Append(output.AsSpan(i + 1));   // unbalanced (should not happen)
                    return sb.ToString();
                }
                sb.Append(output.AsSpan(i + 1, close - i - 1));
                i = close;
                continue;
            }

            if (inTag)
            {
                if (inAttrValue)
                {
                    if (c == attrQuote) { FlushContent(); inAttrValue = false; sb.Append(c); continue; }
                    content.Append(c);          // attribute value IS character data
                    continue;
                }
                if (c == '>') { inTag = false; sb.Append(c); continue; }
                if (c is '"' or '\'') { inAttrValue = true; attrQuote = c; sb.Append(c); continue; }
                sb.Append(c);                   // element/attribute name and other markup
                continue;
            }

            if (c == '<')
            {
                // CDATA content is character data and is normalized; the delimiters are not.
                const string cdataOpen = "<![CDATA[";
                if (string.CompareOrdinal(output, i, cdataOpen, 0, cdataOpen.Length) == 0)
                {
                    FlushContent();
                    sb.Append(cdataOpen);
                    var contentStart = i + cdataOpen.Length;
                    var close = output.IndexOf("]]>", contentStart, StringComparison.Ordinal);
                    if (close < 0)
                    {
                        sb.Append(output.AsSpan(contentStart).ToString().Normalize(form));
                        return sb.ToString();
                    }
                    sb.Append(output.AsSpan(contentStart, close - contentStart).ToString().Normalize(form));
                    sb.Append("]]>");
                    i = close + 2;
                    continue;
                }
                FlushContent();
                inTag = true;
                sb.Append(c);
                continue;
            }

            content.Append(c);                  // text node
        }

        FlushContent();
        return sb.ToString();
    }

    private static string NormalizeExceptMappedRegions(string output, System.Text.NormalizationForm form)
    {
        if (output.IndexOf(MapGuardStart, StringComparison.Ordinal) < 0)
            return output.Normalize(form);

        var sb = new StringBuilder(output.Length);
        var segStart = 0;
        for (var i = 0; i < output.Length; i++)
        {
            if (output[i] == MapGuardStart)
            {
                if (i > segStart)
                    sb.Append(output.Substring(segStart, i - segStart).Normalize(form));
                var close = output.IndexOf(MapGuardEnd, i + 1);
                if (close < 0)
                {
                    // Unbalanced guard (should not happen) — emit the remainder verbatim.
                    sb.Append(output.AsSpan(i + 1));
                    return sb.ToString();
                }
                sb.Append(output.AsSpan(i + 1, close - i - 1)); // mapped region: immune, guards dropped
                i = close;
                segStart = close + 1;
            }
        }
        if (segStart < output.Length)
            sb.Append(output.Substring(segStart).Normalize(form));
        return sb.ToString();
    }

    /// <summary>
    /// Replaces characters that the declared output <paramref name="encodingName"/> cannot
    /// represent with decimal numeric character references, in text and attribute-value content
    /// only (never in element/attribute names or other markup). Handles astral characters
    /// (surrogate pairs) as a single code point (character-map-010 att3). No-op when the encoding
    /// can represent all of Unicode (UTF-8/16/32) or is unknown.
    /// </summary>
    private static string EscapeUnrepresentableCharacters(string output, string encodingName)
    {
        System.Text.Encoding enc;
        try { enc = System.Text.Encoding.GetEncoding(encodingName); }
        catch (ArgumentException) { return output; }
        // UTF encodings represent all of Unicode — nothing to escape.
        if (enc.CodePage is 65001 or 1200 or 1201 or 12000 or 12001)
            return output;

        var sb = new StringBuilder(output.Length);
        var inTag = false;
        var inAttrValue = false;
        var attrQuote = '"';
        var changed = false;
        for (var i = 0; i < output.Length; i++)
        {
            var c = output[i];
            if (inTag)
            {
                if (inAttrValue)
                {
                    if (c == attrQuote) { inAttrValue = false; sb.Append(c); continue; }
                }
                else
                {
                    if (c == '>') { inTag = false; sb.Append(c); continue; }
                    if (c == '"' || c == '\'') { inAttrValue = true; attrQuote = c; sb.Append(c); continue; }
                    // Element/attribute names and other in-tag markup: leave verbatim.
                    sb.Append(c);
                    continue;
                }
                // fall through: attribute-value content is escaped like text
            }
            else if (c == '<')
            {
                // A CDATA section is literal: an unrepresentable character inside it cannot be
                // written as-is, so the section splits around it and the character is emitted as
                // a numeric character reference OUTSIDE the CDATA — e.g.
                // "<![CDATA[foo ]]>&#170;<![CDATA[ bar]]>" (W3C Serialization 4.0 §7.2;
                // mirrors PhoenixmlDb.XQuery's WriteCDataEncodingAware). Any other "<" is markup.
                const string cdataOpen = "<![CDATA[";
                if (string.CompareOrdinal(output, i, cdataOpen, 0, cdataOpen.Length) == 0)
                {
                    var contentStart = i + cdataOpen.Length;
                    var close = output.IndexOf("]]>", contentStart, StringComparison.Ordinal);
                    if (close < 0) close = output.Length; // malformed; treat rest as content
                    if (SplitCdataForEncoding(output, contentStart, close, enc, sb))
                        changed = true;
                    // Skip past the closing "]]>" (or to end when unterminated).
                    i = close < output.Length ? close + 2 : output.Length - 1;
                    continue;
                }
                inTag = true;
                sb.Append(c);
                continue;
            }

            // Content region (text node or attribute value): NCR-escape unrepresentable chars.
            int codePoint;
            var consumedLow = false;
            if (char.IsHighSurrogate(c) && i + 1 < output.Length && char.IsLowSurrogate(output[i + 1]))
            {
                codePoint = char.ConvertToUtf32(c, output[i + 1]);
                consumedLow = true;
            }
            else
            {
                codePoint = c;
            }

            if (codePoint > 0x7F && !IsEncodable(codePoint, enc))
            {
                sb.Append("&#").Append(codePoint.ToString(CultureInfo.InvariantCulture)).Append(';');
                if (consumedLow) i++;
                changed = true;
            }
            else
            {
                sb.Append(c);
                if (consumedLow) { sb.Append(output[i + 1]); i++; }
            }
        }
        return changed ? sb.ToString() : output;
    }

    /// <summary>
    /// Re-serializes the CDATA content <c>output[contentStart..contentEnd)</c> into
    /// <paramref name="sb"/>, keeping representable characters inside <c>&lt;![CDATA[…]]&gt;</c> runs
    /// and emitting each character the target encoding cannot represent as a decimal numeric
    /// character reference OUTSIDE the section (the section splits around it). Astral characters
    /// (surrogate pairs) are handled as a single code point. Returns true if any character was
    /// split out (i.e. the output differs from a single verbatim CDATA run).
    /// </summary>
    private static bool SplitCdataForEncoding(string output, int contentStart, int contentEnd,
        System.Text.Encoding enc, StringBuilder sb)
    {
        // Preserve a genuinely-empty CDATA section verbatim (nothing to split).
        if (contentStart >= contentEnd)
        {
            sb.Append("<![CDATA[]]>");
            return false;
        }
        var run = new StringBuilder();
        var changed = false;
        void FlushRun()
        {
            if (run.Length == 0) return;
            sb.Append("<![CDATA[").Append(run).Append("]]>");
            run.Clear();
        }
        for (var i = contentStart; i < contentEnd; i++)
        {
            var c = output[i];
            int codePoint;
            var consumedLow = false;
            if (char.IsHighSurrogate(c) && i + 1 < contentEnd && char.IsLowSurrogate(output[i + 1]))
            {
                codePoint = char.ConvertToUtf32(c, output[i + 1]);
                consumedLow = true;
            }
            else
            {
                codePoint = c;
            }

            if (codePoint > 0x7F && !IsEncodable(codePoint, enc))
            {
                FlushRun();
                sb.Append("&#").Append(codePoint.ToString(CultureInfo.InvariantCulture)).Append(';');
                if (consumedLow) i++;
                changed = true;
            }
            else
            {
                run.Append(c);
                if (consumedLow) { run.Append(output[i + 1]); i++; }
            }
        }
        FlushRun();
        return changed;
    }

    /// <summary>Returns true if <paramref name="codePoint"/> round-trips through
    /// <paramref name="enc"/> without loss (mirrors PhoenixmlDb.XQuery's IsEncodable).</summary>
    private static bool IsEncodable(int codePoint, System.Text.Encoding enc)
    {
        var s = char.ConvertFromUtf32(codePoint);
        try { return enc.GetString(enc.GetBytes(s)) == s; }
        catch (System.Text.EncoderFallbackException) { return false; }
    }

    private void CollectCharacterMappings(XsltCharacterMap map, Dictionary<int, string> target, HashSet<QName> visited, Dictionary<QName, XsltCharacterMap>? charMaps = null)
    {
        charMaps ??= _stylesheet.CharacterMaps;
        if (!visited.Add(map.Name))
            return;

        // First apply referenced character maps (resolved in the same package registry)
        foreach (var refName in map.UseCharacterMaps)
        {
            if (charMaps.TryGetValue(refName, out var refMap))
                CollectCharacterMappings(refMap, target, visited, charMaps);
        }

        // Then apply this map's own mappings (later declarations override earlier ones)
        foreach (var (ch, replacement) in map.Mappings)
        {
            target[ch] = replacement;
        }
    }

    /// <summary>
    /// True when <paramref name="elem"/> carries its own explicit xml:base attribute.
    /// Used by the base-uri-046 fixup to distinguish a copy that keeps a relative xml:base
    /// (whose base must be resolved against the construction base) from a plain source copy
    /// (whose base is its preserved CopySourceBaseUri).
    /// </summary>
    private static bool ElementHasExplicitXmlBaseAttr(XdmInMemoryStore nodeStore, XdmElement elem)
    {
        foreach (var attrId in elem.Attributes)
        {
            if (nodeStore.GetNode(attrId) is Xdm.Nodes.XdmAttribute attr
                && attr.Namespace == NamespaceId.Xml && attr.LocalName == "base")
                return true;
        }
        return false;
    }

    /// <summary>
    /// Initializes global parameters and variables in dependency order.
    /// This ensures that if param $x references $y, then $y is initialized first.
    /// Also handles transitive dependencies through function calls.
    /// </summary>
    /// <summary>
    /// Decides whether an error raised while eagerly initializing a global variable may be
    /// deferred to the point of reference.
    /// </summary>
    /// <remarks>
    /// XSLT 3.0 §2.3.2 (Priming a Stylesheet) addresses this case directly: "If the
    /// initialization of any global variables or parameter depends on the context item, a
    /// dynamic error can occur if the context item is absent. It is implementation-defined
    /// whether this error occurs during priming of the stylesheet or subsequently when the
    /// variable is referenced; and it is implementation-defined whether the error occurs at
    /// all if the variable or parameter is never referenced." Deferring is therefore a choice,
    /// not a correction — but it is the choice Saxon makes, and real stylesheets rely on it.
    /// Priming eagerly would otherwise turn an unevaluatable declaration in an
    /// *imported* module into a fatal error for stylesheets that never reference it — the
    /// common case being <c>&lt;xsl:variable select="/"/&gt;</c> in a module imported by a
    /// stylesheet invoked with an initial template and no source document (XSpec's generated
    /// test stylesheets import the stylesheet under test and run it via a named template).
    /// Static errors are excluded: they are signalled whether or not the variable is used.
    /// </remarks>
    private static bool IsDeferrableGlobalError(Exception ex)
    {
        var code = ex switch
        {
            XsltException xe => xe.ErrorCode,
            PhoenixmlDb.XQuery.Execution.XQueryRuntimeException qe => qe.ErrorCode,
            _ => null,
        };
        if (string.IsNullOrEmpty(code))
            return false;
        // Circularity is detected by the dependency analysis rather than by evaluation, so
        // deferring it would lose the diagnostic rather than postpone it.
        if (code == "XTDE0640")
            return false;
        return !(code.StartsWith("XTSE", StringComparison.Ordinal)
            || code.StartsWith("XPST", StringComparison.Ordinal)
            || code.StartsWith("XQST", StringComparison.Ordinal));
    }

    /// <summary>
    /// Binds externally-supplied parameter values over the stylesheet's globals.
    /// </summary>
    /// <remarks>
    /// Only an <c>xsl:param</c> may be overridden this way. A name the stylesheet declares as a
    /// global <c>xsl:variable</c> is not a parameter at all, so a supplied value for it must have
    /// no effect (XSLT 3.0 §9.5) — the variable keeps its computed value.
    ///
    /// <para>
    /// These four call sites previously wrote EVERY supplied name straight into the global scope,
    /// so a caller could silently replace a variable's value with one of a different type, or
    /// with nothing. It stayed invisible for as long as fn:transform's stylesheet-params were not
    /// delivered at all. The moment they were, XSpec's global-override suite — whose entire
    /// purpose is asserting that an x:param never overrides an xsl:variable — stopped completing:
    /// the empty value it deliberately supplies replaced a variable declared as="xs:string",
    /// and the template returning it raised XTTE0505.
    /// </para>
    ///
    /// <para>
    /// Declared parameters are applied here rather than skipped, because
    /// InitializeGlobalsInDependencyOrderAsync only binds the ones it evaluates; this pass is
    /// what guarantees a supplied value wins over a default regardless of evaluation order.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The reserved name the initial match selection is bound to when it is supplied as a value.
    /// Namespaced so it cannot collide with a stylesheet's own global.
    /// </summary>
    private static readonly QName InitialMatchSelectionVariable =
        new(QNameNamespaces.InternedIdFor("urn:x-phoenixml:internal"), "initial-match-selection");

    /// <summary>
    /// Produces the select expression for an initial-mode apply-templates, binding the supplied
    /// sequence first when the selection arrived as a value rather than as an expression.
    /// </summary>
    private static XQueryExpression BuildInitialModeSelectExpression(
        DefaultXsltExecutionContext context, XsltTransformOptions options)
    {
        if (options.InitialModeSelectValue != null)
        {
            // Bind, then reference. Applying templates to the sequence through an ordinary
            // variable reference keeps the real apply-templates semantics — position(), last()
            // and sorting all see the whole selection — which iterating item by item here
            // would quietly lose.
            context.GlobalVariables[InitialMatchSelectionVariable] = options.InitialModeSelectValue;
            return new VariableReference { Name = InitialMatchSelectionVariable };
        }
        return new XQuery.Parser.XQueryParserFacade { AllowNamespaceAxis = true }
            .Parse(options.InitialModeSelect!);
    }

    private void BindExternalParameters(DefaultXsltExecutionContext context, XsltTransformOptions options)
    {
        if (options.InitialParameters.Count == 0)
            return;

        var paramNames = new HashSet<QName>();
        foreach (var p in _stylesheet.Parameters)
            paramNames.Add(p.Name);

        // A name declared BOTH ways keeps the parameter's meaning: CollectGlobalDeclarations
        // adds parameters first and lets the first declaration of a name win.
        var variableOnly = new HashSet<QName>();
        foreach (var v in _stylesheet.Variables)
            if (!paramNames.Contains(v.Name))
                variableOnly.Add(v.Name);

        foreach (var (name, value) in options.InitialParameters)
        {
            if (variableOnly.Contains(name))
                continue;
            // A node handed in from another engine arrives wrapped for cross-store transport;
            // re-parse it into THIS store so its children resolve. Unwrapped, its Children
            // NodeIds refer to the caller's store and the subtree reads as empty — a parameter
            // <kid>text</kid> arrived as <kid/>.
            context.GlobalVariables[name] = UnwrapCrossStoreValue(value, context._nodeStore);
        }
    }

    private async Task InitializeGlobalsInDependencyOrderAsync(
        DefaultXsltExecutionContext context,
        StringBuilder outputBuilder)
    {
        // Collect all global declarations (including from imports)
        var globals = new List<GlobalDeclaration>();
        var seenNames = new HashSet<QName>();
        CollectGlobalDeclarations(_stylesheet, globals, seenNames);

        // Build dependency graph: which globals does each global depend on?
        var globalNames = new HashSet<QName>(globals.Select(g => g.Name));
        var dependencies = new Dictionary<QName, HashSet<QName>>();
        var functionDeps = BuildFunctionDependencies();

        // Globals whose select/content references an abstract variable of a used package
        // (a variable with no concrete definition). Such a global cannot be evaluated
        // eagerly without error, and per XSLT lazy global evaluation it must not be
        // evaluated at all unless actually referenced — at which point evaluating an
        // abstract component is XTDE3052 (accept-042/043a: unreferenced proxy = no error;
        // accept-043b: referenced proxy = XTDE3052).
        var abstractVarNames = _stylesheet.AbstractVariableNames;
        var deferredAbstractGlobals = new Dictionary<QName, QName>(); // global -> abstract var it depends on

        foreach (var global in globals)
        {
            var deps = new HashSet<QName>();

            // Collect direct variable references
            if (global.Select != null)
            {
                var collector = new DependencyCollector();
                collector.Walk(global.Select);

                // Add direct variable dependencies (only those that are globals)
                foreach (var varRef in collector.VariableRefs)
                {
                    // ResolveGlobalRef, not a bare Contains: the reference comes from the XPath
                    // parser and the declaration from the stylesheet parser, so their QNames can
                    // carry different interned NamespaceIds for the same URI. A miss here is
                    // silent and does real damage — the dependency is simply not recorded, the
                    // topological sort never orders the two, and a forward-declared global is
                    // evaluated before the one it selects from.
                    var resolvedRef = ResolveGlobalRef(varRef, globalNames);
                    if (resolvedRef is { } gname)
                        deps.Add(gname);
                    else if (abstractVarNames.Contains(varRef) && !deferredAbstractGlobals.ContainsKey(global.Name))
                        deferredAbstractGlobals[global.Name] = varRef;
                }

                // Add transitive dependencies through function calls
                AddTransitiveFunctionDependencies(collector.FunctionRefs, functionDeps, globalNames, deps);
            }

            // Also analyze Content for dependencies (xsl:value-of, etc.)
            if (global.Content != null)
            {
                var collector = new DependencyCollector();
                foreach (var instruction in global.Content.Instructions)
                {
                    CollectInstructionDependencies(instruction, collector);
                }

                // Add direct variable dependencies
                foreach (var varRef in collector.VariableRefs)
                {
                    if (ResolveGlobalRef(varRef, globalNames) is { } resolvedContentRef)
                    {
                        deps.Add(resolvedContentRef);
                    }
                    else if (globalNames.Contains(varRef))
                        deps.Add(varRef);
                    else if (abstractVarNames.Contains(varRef) && !deferredAbstractGlobals.ContainsKey(global.Name))
                        deferredAbstractGlobals[global.Name] = varRef;
                }

                // Add transitive dependencies through function calls
                AddTransitiveFunctionDependencies(collector.FunctionRefs, functionDeps, globalNames, deps);
            }

            dependencies[global.Name] = deps;
        }

        // Topologically sort globals
        var sorted = TopologicalSort(globals, dependencies);

        // Collect externally-supplied parameter names so we can skip evaluating their defaults.
        // External params must be bound first so that global variables depending on them see the
        // supplied values rather than the stylesheet defaults.
        var externalParams = context._options.InitialParameters;

        // Pre-populate pending globals map for lazy evaluation.
        // When a global variable's select expression references another global that hasn't been
        // initialized yet (and wasn't detected by the dependency analysis), GetVariable can
        // trigger lazy initialization instead of throwing "not bound".
        context._pendingGlobals = new Dictionary<QName, GlobalDeclaration>();
        foreach (var global in sorted)
            context._pendingGlobals[global.Name] = global;

        // Bind globals that reference an abstract variable to a deferred value that throws
        // XTDE3052 only when actually accessed. Skipping their eager evaluation keeps an
        // unreferenced public proxy over an abstract variable from failing at load time
        // (accept-042/043a), while a genuine reference surfaces XTDE3052 (accept-043b).
        foreach (var (globalName, abstractVar) in deferredAbstractGlobals)
        {
            var capturedAbstract = abstractVar;
            context.GlobalVariables[globalName] = new LazyValue(() =>
                throw new XsltException(
                    $"XTDE3052: Cannot evaluate abstract variable ${capturedAbstract.LocalName} " +
                    "(no concrete definition was supplied by the using package)"));
            context._pendingGlobals.Remove(globalName);
        }

        // Initialize in dependency order
        foreach (var global in sorted)
        {
            if (deferredAbstractGlobals.ContainsKey(global.Name))
                continue; // bound above as a deferred XTDE3052 value
            // Push per-element version for backwards-compatible mode propagation
            if (global.Version != null)
                context.PushVersion(global.Version);
            // Push the global declaration's own module URI as the static base URI so
            // static-base-uri() inside the global's select/body returns the module that
            // declared it, not the principal stylesheet. (XSLT 3.0 §5.4.2 / fn:static-base-uri.)
            // Without this, a global xsl:variable in modules/x.xsl saw the principal
            // stylesheet's URI, causing resolve-uri('templates.xml', sbu) to look one
            // directory above the module — Martin's Docbook templates.xml report.
            var globalSbu = XsltTransformEngine.UriString(global.BaseUri);
            if (globalSbu != null)
                context.PushStaticBaseUri(globalSbu);
            // Report this global's owning package as the current component package while it is
            // evaluated so an intra-package reference to a private sibling global resolves
            // (private-across-boundary enforcement in GetVariable). null for the principal.
            // Set BEFORE binding $xsl:original: that binding evaluates the OVERRIDDEN original,
            // whose select may reference a private sibling of the same (used) package
            // (override-v-001/003/004).
            var savedGlobalPkg = context._currentGlobalPackage;
            context._currentGlobalPackage = global.PackageStylesheet;
            // If this global is an overriding xsl:variable that references $xsl:original,
            // evaluate the overridden variable and bind $xsl:original for its select/content.
            var xslOrigScopePushed = await context.TryPushXslOriginalVariableBindingAsync(global, outputBuilder).ConfigureAwait(false);
            try
            {
            // If this is a param with an externally-supplied value, use that instead of the default
            if (global.IsParam && externalParams.TryGetValue(global.Name, out var extValue))
            {
                // Validate externally-supplied value against the param's as type
                if (global.As != null)
                    context.ValidateValueMatchesType(extValue, global.As, "XTTE0590",
                        $"Parameter ${global.Name.LocalName}");
                context.GlobalVariables[global.Name] = extValue;
            }
            else if (global.Select != null)
            {
                context._globalsBeingEvaluated.Add(global.Name);
                try
                {
                    var value = await context.EvaluateAsync(global.Select).ConfigureAwait(false);
                    context.GlobalVariables[global.Name] = DefaultXsltExecutionContext.CoerceSelectValueToDeclaredType(value, global.As);
                }
                finally { context._globalsBeingEvaluated.Remove(global.Name); }
            }
            else if (global.Content != null)
            {
                context._globalsBeingEvaluated.Add(global.Name);
                try
                {
                // Check if this is a sequence type that needs accumulator.
                // Include element() and attribute() types regardless of occurrence — and
                // regardless of whether the type was named (element(foo)) or unconstrained
                // (element()) — since they need proper XDM node creation via the sequence
                // accumulator. The earlier `ElementName == null` guard skipped the accumulator
                // path for *named* element types, which then fell to the no-as RTF path and
                // bound the variable to a ResultTreeFragment (a document wrapper). Downstream
                // path steps like `$locale/l:group` then saw a document-node and looked for
                // `l:group` children of the document — which there are none of, so the lookup
                // returned the empty sequence. Found in Docbook chunk-cleanup name-style
                // localization (`as="element(l:l10n)"` global).
                // Comment and ProcessingInstruction belong here for the same reason Element and
                // Attribute do: without them isSequenceType is false, the sequence-collection
                // path below is skipped entirely, and the body falls through to the legacy route
                // that yields a DOCUMENT wrapping the node instead of the node itself:
                //
                //   <xsl:variable as="comment()" name="c"><xsl:comment>ct</xsl:comment></...>
                //   $c instance of comment()        ->  false
                //   $c instance of document-node()  ->  true, and string($c) is "" — content lost
                //
                // The LOCAL variable path already gets this right; its equivalent list
                // (isNodeItemType, ~line 21520) covers nine item types where this one covered
                // three. Only the two kinds proven broken are added — text() and document-node()
                // already behave correctly through the legacy route, so widening the list to
                // match local exactly would change working behaviour on no evidence.
                var needsNodeOrphan = global.As != null &&
                    global.As.ItemType is ItemType.Element or ItemType.Attribute or ItemType.Node
                                        or ItemType.Comment or ItemType.ProcessingInstruction;
                // Map / array / function / record items must come through the accumulator so
                // xsl:map / xsl:array constructions land as the live Dictionary or List object,
                // not as their JSON-serialized text form (the default top-level fallback path).
                // Without this, `<xsl:variable as="map(*)"><xsl:map>…</xsl:map></xsl:variable>`
                // ends up as a JSON string and downstream `map:contains($v, …)` calls fail with
                // XPTY0004 "must be a single map".
                var isContainerItem = global.As != null &&
                    global.As.ItemType is ItemType.Map or ItemType.Array
                                        or ItemType.Function or ItemType.Record;
                var isSequenceType = global.As != null &&
                    (global.As.Occurrence == Occurrence.ZeroOrMore
                    || global.As.Occurrence == Occurrence.OneOrMore
                    || needsNodeOrphan
                    || isContainerItem);

                if (isSequenceType)
                {
                    // Use sequence accumulator to collect individual items from xsl:sequence
                    context.BeginSequenceCollection();
                    // When the target type is text(), collect each text write as a separate
                    // accumulator item so they remain individual text nodes.
                    var needsTextCollection = global.As != null &&
                        global.As.ItemType is ItemType.Text;
                    var savedCollectText = context._collectTextAsSequenceItems;
                    var savedElemDepth = context._serializingElementDepth;
                    if (needsTextCollection)
                    {
                        context._collectTextAsSequenceItems = true;
                        context._serializingElementDepth = 0;
                    }
                    var savedLen = outputBuilder.Length;
                    try
                    { await global.Content.ExecuteAsync(context).ConfigureAwait(false); }
                    finally
                    {
                        context._collectTextAsSequenceItems = savedCollectText;
                        context._serializingElementDepth = savedElemDepth;
                    }
                    var textContent = outputBuilder.ToString(savedLen, outputBuilder.Length - savedLen);
                    outputBuilder.Length = savedLen;

                    var collected = context.EndSequenceCollection();

                    // Combine accumulated items with serialized literal elements
                    var seqItems = new List<object?>();
                    var wrapAsTextNode = context._nodeStore != null && global.As != null
                        && global.As.ItemType is ItemType.Text or ItemType.Node;
                    foreach (var item in collected)
                    {
                        if (item != null)
                        {
                            if (wrapAsTextNode && item is string strItem)
                            {
                                var textId = context._nodeStore!.NextId();
                                seqItems.Add(new Xdm.Nodes.XdmText
                                {
                                    Id = textId,
                                    Document = DocumentId.None,
                                    Parent = NodeId.None,
                                    Value = strItem
                                });
                            }
                            else if (wrapAsTextNode && item is Xdm.TextNodeItem tni)
                            {
                                var textId = context._nodeStore!.NextId();
                                seqItems.Add(new Xdm.Nodes.XdmText
                                {
                                    Id = textId,
                                    Document = DocumentId.None,
                                    Parent = NodeId.None,
                                    Value = tni.Value
                                });
                            }
                            else
                            {
                                seqItems.Add(item);
                            }
                        }
                    }
                    // base-uri-046: a parentless deep xsl:copy-of ROOT that KEEPS a RELATIVE
                    // xml:base attribute arrives here via the sequence accumulator (textContent
                    // is empty, so the reparse fixup below is bypassed). fn:base-uri needs the
                    // construction base stamped on BaseUri: ComputeBaseUri's explicit-xml:base
                    // branch resolves the relative xml:base against elem.BaseUri when the node is
                    // parentless. Scope strictly to roots that (a) have an explicit xml:base and
                    // (b) have no BaseUri yet, so no-xml:base source copies — which must keep
                    // returning their preserved CopySourceBaseUri — are untouched.
                    if (context._nodeStore != null)
                    {
                        var rootBaseUri = XsltTransformEngine.UriString(global.BaseUri)
                            ?? XsltTransformEngine.UriString(_stylesheet.BaseUri);
                        if (rootBaseUri != null)
                        {
                            foreach (var it in seqItems)
                            {
                                if (it is XdmElement rootEl && rootEl.BaseUri == null
                                    && (!rootEl.Parent.HasValue || rootEl.Parent.Value == NodeId.None)
                                    && ElementHasExplicitXmlBaseAttr(context._nodeStore, rootEl))
                                {
                                    rootEl.BaseUri = rootBaseUri;
                                }
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(textContent) && context._nodeStore != null && textContent.Contains('<', StringComparison.Ordinal))
                    {
                        try
                        {
                            var globalSeqBaseUri = XsltTransformEngine.UriString(global.BaseUri) ?? XsltTransformEngine.UriString(_stylesheet.BaseUri);

                            // Stream-parse rather than allocating a full XmlDocument.
                            var initSettings = new System.Xml.XmlReaderSettings
                            {
                                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                                IgnoreWhitespace = false,
                                IgnoreComments = false,
                                IgnoreProcessingInstructions = false,
                            };
                            using var initStringReader = new System.IO.StringReader($"<_seq_root_>{textContent}</_seq_root_>");
                            using var initReader = System.Xml.XmlReader.Create(initStringReader, initSettings);
                            var initParsed = new List<object?>();
                            context.ReadAsBodyChunkChildren(initReader, initParsed);

                            // When as="document-node()", create a proper XdmDocument node
                            // wrapping the content, rather than extracting individual children
                            if (global.As?.ItemType == ItemType.Document)
                            {
                                var docId = context._nodeStore.NextId();
                                var children = new List<NodeId>();
                                NodeId docElementId = NodeId.None;
                                foreach (var item in initParsed)
                                {
                                    if (item is XdmNode cn)
                                    {
                                        cn.Parent = docId;
                                        children.Add(cn.Id);
                                        if (cn is XdmElement && docElementId == NodeId.None)
                                            docElementId = cn.Id;
                                    }
                                }
                                string? docElemLocalName = docElementId != NodeId.None
                                    ? (context._nodeStore.GetNode(docElementId) as XdmElement)?.LocalName : null;
                                var docNode = new XdmDocument
                                {
                                    Id = docId,
                                    Document = new DocumentId(1),
                                    Parent = NodeId.None,
                                    DocumentElement = docElementId,
                                    Children = children,
                                    DocumentElementLocalName = docElemLocalName,
                                };
                                docNode.BaseUri = globalSeqBaseUri;
                                context._nodeStore.Register(docNode);
                                seqItems.Add(docNode);
                            }
                            else
                            {
                                foreach (var item in initParsed)
                                {
                                    // Detach children from the synthetic _seq_root_ wrapper
                                    // so they become parentless nodes per XSLT as="node()*" semantics
                                    if (item is XdmNode childNode)
                                    {
                                        childNode.Parent = null;
                                        // Set construction context base URI so parentless
                                        // nodes can resolve relative xml:base attributes
                                        if (globalSeqBaseUri != null)
                                            childNode.BaseUri = globalSeqBaseUri;
                                    }
                                    seqItems.Add(item);
                                }
                            }
                        }
                        catch (System.Xml.XmlException)
                        {
                            var seqBaseUri = XsltTransformEngine.UriString(global.BaseUri) ?? XsltTransformEngine.UriString(_stylesheet.BaseUri);
                            seqItems.Add(new ResultTreeFragment(textContent, seqBaseUri));
                        }
                    }
                    else if (!string.IsNullOrEmpty(textContent))
                    {
                        // When as="text()" or as="node()", create actual text nodes
                        if (context._nodeStore != null && global.As != null
                            && global.As.ItemType is ItemType.Text or ItemType.Node or ItemType.Item)
                        {
                            var textId = context._nodeStore.NextId();
                            var textNode = new Xdm.Nodes.XdmText
                            {
                                Id = textId,
                                Document = DocumentId.None,
                                Parent = NodeId.None,
                                Value = textContent
                            };
                            seqItems.Add(textNode);
                        }
                        else
                        {
                            seqItems.Add(textContent);
                        }
                    }

                    // Apply type coercion from the as="" declaration
                    if (global.As != null && global.As.ItemType != ItemType.Item && global.As.ItemType != ItemType.Node)
                    {
                        for (int si = 0; si < seqItems.Count; si++)
                            seqItems[si] = PhoenixmlDb.XQuery.Execution.TypeCastHelper.CastValue(seqItems[si], global.As.ItemType);
                    }

                    // For ExactlyOne/ZeroOrOne: unwrap to single item
                    var occurrence = global.As!.Occurrence;
                    if (occurrence == Occurrence.ExactlyOne || occurrence == Occurrence.ZeroOrOne)
                        context.GlobalVariables[global.Name] = seqItems.Count == 1 ? seqItems[0] : null;
                    else
                        context.GlobalVariables[global.Name] = seqItems.Count > 0 ? seqItems.ToArray() : Array.Empty<object?>();
                }
                else
                {
                    await BindGlobalFromContentAsync(context, global, outputBuilder,
                        XsltTransformEngine.UriString(_stylesheet.BaseUri)).ConfigureAwait(false);
                }
                }
                finally { context._globalsBeingEvaluated.Remove(global.Name); }
            }
            else
            {
                // Per XSLT spec: variable with no select and no content defaults to empty
                // value. The exact "empty" semantics depend on the declared `as=` type:
                //   - cardinality allows empty (?, *) → empty sequence (null)
                //   - cardinality requires ≥1 (1, +) → XTDE0700 for param, "" for variable
                //     (the legacy "empty string" default for untyped variables)
                //
                // The previous code treated the null/"" choice based on `IsParam` rather
                // than on the type's cardinality, so an `xsl:variable as="element()*"`
                // with no value got "" — and downstream `<xsl:sequence select="$x"/>`
                // emitted the empty string as a stray item, breaking sequence
                // construction (Docbook TNG `$v:user-title-groups` → `$v:title-groups`
                // got an extra empty-string item, then iterate failed XPTY0020 trying
                // to navigate `@xpath` on the string).
                  // Occurrence.Zero is empty-sequence(): binding ANY item to it is a type error by
                  // definition, so the empty sequence is the only correct value. It fell through to
                  // the empty-string default below, which made <xsl:variable as="empty-sequence()"/>
                  // hold one item. XSpec declares $x:saxon-config exactly that way and guards on
                  // `=> exists()`, so 47 of its suites terminated on a bogus Saxon-config error.
                if (global.As != null
                    && (global.As.Occurrence == Occurrence.Zero
                        || global.As.Occurrence == Occurrence.ZeroOrOne
                        || global.As.Occurrence == Occurrence.ZeroOrMore))
                    context.GlobalVariables[global.Name] = null;
                else if (global.IsParam && global.As != null
                    && (global.As.Occurrence == Occurrence.ExactlyOne
                        || global.As.Occurrence == Occurrence.OneOrMore))
                {
                    // XTDE0700: param requires a typed value but none supplied and no default
                    throw new XsltException(
                        $"XTDE0700: Parameter ${global.Name.LocalName} requires type {global.As} but no value was supplied");
                }
                else
                    context.GlobalVariables[global.Name] = "";
            }
            }
            catch (Exception ex) when (IsDeferrableGlobalError(ex))
            {
                // Bind the global to a value that re-raises this error if — and only if —
                // something actually reads it. See IsDeferrableGlobalError. Without this the
                // failure is fatal at load time; with the earlier "skip and continue" shape a
                // later reference reported XPST0008 "not defined", naming the wrong problem.
                if (ex is XsltException deferred)
                    deferred.IsDeferredGlobalError = true;
                // The rethrow fires wherever the global is first READ, which may be inside an
                // xsl:try. XSLT 3.0 evaluates globals outside any try's dynamic scope, so the
                // error must not become catchable just because the read happened there
                // (try-028). Marked type-agnostically: FOAR0001 arrives as an XQuery exception,
                // which carries no IsDeferredGlobalError flag to set.
                DeferredGlobalError.Mark(ex);
                var captured = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);
                context.GlobalVariables[global.Name] = new LazyValue(() =>
                {
                    captured.Throw();
                    return ValueTask.FromResult<object?>(null);
                });
            }
            finally
            {
                context._currentGlobalPackage = savedGlobalPkg;
                if (xslOrigScopePushed)
                    context.PopScope();
                if (globalSbu != null)
                    context.PopStaticBaseUri();
                if (global.Version != null)
                    context.PopVersion();
            }
            // Remove from pending now that it's been initialized
            context._pendingGlobals?.Remove(global.Name);
        }
        // Clear pending map — all globals should be initialized now
        context._pendingGlobals = null;

        // Initialize package-local shadow globals (same-named globals from a used package that
        // could not take the principal QName slot). Each is evaluated in its OWN package's
        // context and stored per (package, name) so a reference from that package resolves it
        // ahead of the principal binding (use-package-175 / use-package-176).
        await InitializePackageShadowGlobalsAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Eagerly evaluates each package-local shadow global (see
    /// <see cref="XsltStylesheet.PackageLocalShadowVariables"/>) in its declaring package's
    /// context and records the value in the per-package overlay
    /// (<see cref="DefaultXsltExecutionContext._packageShadowGlobals"/>). Non-package
    /// stylesheets have no shadow variables, so this is a no-op for them.
    /// </summary>
    private static async Task InitializePackageShadowGlobalsAsync(DefaultXsltExecutionContext context)
    {
        var shadows = context._stylesheet.PackageLocalShadowVariables;
        if (shadows.Count == 0)
            return;

        foreach (var variable in shadows)
        {
            var pkg = variable.PackageStylesheet;
            if (pkg == null)
                continue;
            var key = (pkg, variable.Name);
            if (context._packageShadowGlobals.ContainsKey(key))
                continue;

            // Report this package as the current one while evaluating so nested package-local
            // resolution (this variable's select/content, decimal formats, etc.) sees it.
            var savedGlobalPackage = context._currentGlobalPackage;
            context._currentGlobalPackage = pkg;
            try
            {
                object? value;
                if (variable.Select != null)
                    value = DefaultXsltExecutionContext.CoerceSelectValueToDeclaredType(
                        await context.EvaluateAsync(variable.Select).ConfigureAwait(false), variable.As);
                else if (variable.Content != null)
                    value = await context.EvaluateSequenceConstructorAsync(variable.Content).ConfigureAwait(false);
                else
                    value = "";
                context._packageShadowGlobals[key] = value;
            }
            finally
            {
                context._currentGlobalPackage = savedGlobalPackage;
            }
        }
    }

    /// <summary>
    /// Initializes a single pending global variable on demand (lazy evaluation).
    /// Called by GetVariable when a variable isn't in GlobalVariables but is in _pendingGlobals.
    /// </summary>
    internal static async Task InitializePendingGlobalAsync(
        DefaultXsltExecutionContext context,
        GlobalDeclaration global,
        StringBuilder outputBuilder)
    {
        // Remove from pending to prevent re-entrant initialization
        context._pendingGlobals!.Remove(global.Name);

        // Push per-element version for backwards-compatible mode propagation
        if (global.Version != null)
            context.PushVersion(global.Version);
        // Push the global declaration's own module URI as the static base URI — see
        // matching comment in InitializeGlobalsAsync. Required for static-base-uri()
        // inside a global declared in an imported/included module to return the
        // module's own URI rather than the principal stylesheet's.
        var globalSbu = XsltTransformEngine.UriString(global.BaseUri);
        if (globalSbu != null)
            context.PushStaticBaseUri(globalSbu);
        // Report this global's owning package as the current component package while it is
        // evaluated (private-across-boundary enforcement in GetVariable). null for principal.
        // Set BEFORE binding $xsl:original (which evaluates the overridden original whose
        // select may reference a private sibling of the used package — override-v-001/003/004).
        var savedGlobalPkg = context._currentGlobalPackage;
        context._currentGlobalPackage = global.PackageStylesheet;
        // Bind $xsl:original if this is an overriding xsl:variable referencing it.
        var xslOrigScopePushed = await context.TryPushXslOriginalVariableBindingAsync(global, outputBuilder).ConfigureAwait(false);
        try
        {
            var externalParams = context._options.InitialParameters;
            if (global.IsParam && externalParams.TryGetValue(global.Name, out var extValue))
            {
                if (global.As != null)
                    context.ValidateValueMatchesType(extValue, global.As, "XTTE0590",
                        $"Parameter ${global.Name.LocalName}");
                context.GlobalVariables[global.Name] = extValue;
            }
            else if (global.Select != null)
            {
                context._globalsBeingEvaluated.Add(global.Name);
                try
                {
                    var value = await context.EvaluateAsync(global.Select).ConfigureAwait(false);
                    context.GlobalVariables[global.Name] = DefaultXsltExecutionContext.CoerceSelectValueToDeclaredType(value, global.As);
                }
                finally { context._globalsBeingEvaluated.Remove(global.Name); }
            }
            else if (global.Content != null)
            {
                // Same binding rules as the eager pass — see BindGlobalFromContentAsync. This
                // used to store the serialized content as a raw xs:string, ignoring `as`, so a
                // global reached lazily bound a string where the eager pass bound a document
                // node. Which pass reached it depended on whether the dependency analysis had
                // spotted the reference, making the TYPE of a variable depend on how another
                // expression happened to name it.
                context._globalsBeingEvaluated.Add(global.Name);
                try
                {
                    // No principal-stylesheet fallback here: the lazy pass is static and the
                    // global's own BaseUri (used first inside) is what mattered before.
                    await BindGlobalFromContentAsync(context, global, outputBuilder, null)
                        .ConfigureAwait(false);
                }
                finally { context._globalsBeingEvaluated.Remove(global.Name); }
            }
            else
            {
                // A declared type that permits or requires emptiness takes the empty sequence,
                // not the empty string. Two faults here: Occurrence.Zero (empty-sequence()) was
                // not covered at all, and the whole branch was restricted to params - a VARIABLE
                // declared as="empty-sequence()" or as="item()?" needs it just as much, since the
                // empty string is one item and satisfies neither type.
                if (global.As != null
                    && (global.As.Occurrence == Occurrence.Zero
                        || global.As.Occurrence == Occurrence.ZeroOrOne
                        || global.As.Occurrence == Occurrence.ZeroOrMore))
                    context.GlobalVariables[global.Name] = null;
                else
                    context.GlobalVariables[global.Name] = "";
            }
        }
        finally
        {
            context._currentGlobalPackage = savedGlobalPkg;
            if (xslOrigScopePushed)
                context.PopScope();
            if (globalSbu != null)
                context.PopStaticBaseUri();
            if (global.Version != null)
                context.PopVersion();
        }
    }

    /// <summary>
    /// Adds transitive dependencies from function calls to the dependency set.
    /// </summary>
    /// <summary>
    /// Binds a global variable/parameter whose value comes from a CONTENT sequence constructor,
    /// honouring its declared <c>as</c> type.
    /// </summary>
    /// <remarks>
    /// Shared by the eager dependency-ordered pass and the lazy on-demand pass. It was not:
    /// the lazy path had its own two-line version that stored the serialized content as a raw
    /// xs:string and ignored <c>as</c> entirely. So the SAME declaration bound a document node
    /// or a string depending only on which pass happened to reach it first.
    ///
    /// The eager path already carried a fix for exactly this, from Martin Honnen's DocBook
    /// report — "falling through to the RTF/string path produced an xs:string, which then
    /// failed axis-step evaluation with XPTY0020". That fix was applied to one of the two
    /// paths. XSpec then hit the other one, with the same error code, on 20 suites.
    /// </remarks>
    private static async ValueTask BindGlobalFromContentAsync(
        DefaultXsltExecutionContext context, GlobalDeclaration global, StringBuilder outputBuilder,
        string? stylesheetBaseUri)
    {
        if (global.Content == null)
            return;
                var savedLen = outputBuilder.Length;
                await global.Content.ExecuteAsync(context).ConfigureAwait(false);
                var content = outputBuilder.ToString(savedLen, outputBuilder.Length - savedLen);
                outputBuilder.Length = savedLen;
                var globalBaseUri = XsltTransformEngine.UriString(global.BaseUri) ?? stylesheetBaseUri;
                // XSLT 2.0: variables with content and no 'as' always create a temporary tree
                if (global.As == null)
                    context.GlobalVariables[global.Name] = new ResultTreeFragment(content, globalBaseUri);
                else if (context._nodeStore != null
                    && global.As.ItemType is ItemType.Text or ItemType.Node
                    && !content.Contains('<', StringComparison.Ordinal))
                {
                    // as="text()" or as="node()": create proper XDM text node.
                    // REGISTER it. Taking an id from the store without registering leaves a node
                    // the store cannot resolve, and node identity is resolved through the store —
                    // so `except`, `union` and `intersect` reported "An operand of the except
                    // operator is not a node" for a variable that is one. The document-node()
                    // branch directly below has always registered; this one did not.
                    var textId = context._nodeStore.NextId();
                    var globalText = new Xdm.Nodes.XdmText
                    {
                        Id = textId,
                        Document = DocumentId.None,
                        Parent = NodeId.None,
                        Value = content
                    };
                    context._nodeStore.Register(globalText);
                    context.GlobalVariables[global.Name] = globalText;
                }
                else if (context._nodeStore != null && global.As.ItemType == ItemType.Document)
                {
                    // as="document-node()": parse content into a proper XdmDocument so
                    // downstream `/` and `*` axis steps work. Falling through to the RTF/string
                    // path produced an xs:string "" for empty content, which then failed
                    // axis-step evaluation with XPTY0020 (Martin Honnen's Docbook TNG
                    // $v:templates → fp:construct-templates path).
                    var docId = context._nodeStore.NextId();
                    var docChildren = new List<NodeId>();
                    NodeId docElementId = NodeId.None;
                    string? docElemLocalName = null;
                    var sb = new StringBuilder();
                    if (content.Length > 0)
                    {
                        try
                        {
                            var settings = new System.Xml.XmlReaderSettings
                            {
                                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                                IgnoreWhitespace = false,
                                IgnoreComments = false,
                                IgnoreProcessingInstructions = false,
                            };
                            using var sr = new System.IO.StringReader($"<_doc_root_>{content}</_doc_root_>");
                            using var rdr = System.Xml.XmlReader.Create(sr, settings);
                            var children = new List<object?>();
                            context.ReadAsBodyChunkChildren(rdr, children);
                            foreach (var item in children)
                            {
                                if (item is XdmNode cn)
                                {
                                    cn.Parent = docId;
                                    docChildren.Add(cn.Id);
                                    if (cn is XdmElement xe)
                                    {
                                        if (docElementId == NodeId.None)
                                            docElementId = cn.Id;
                                        docElemLocalName ??= xe.LocalName;
                                    }
                                    sb.Append(cn.StringValue);
                                }
                            }
                        }
                        catch (System.Xml.XmlException)
                        {
                            // Malformed content (e.g. raw text). Empty document; the
                            // failure will surface elsewhere if it matters.
                        }
                    }
                    var docNode = new XdmDocument
                    {
                        Id = docId,
                        Document = new DocumentId(1),
                        Parent = NodeId.None,
                        DocumentElement = docElementId,
                        Children = docChildren,
                        DocumentElementLocalName = docElemLocalName,
                        _stringValue = sb.ToString(),
                    };
                    // Carry the variable's base URI so base-uri()/document()/relative-URI
                    // resolution inside the temp tree works (mirrors the as="item()*" sibling
                    // above). Without it the document-node temp tree had a null base URI.
                    docNode.BaseUri = globalBaseUri;
                    context._nodeStore.Register(docNode);
                    context.GlobalVariables[global.Name] = docNode;
                }
                else
                {
                    // An executing-but-empty body (e.g. an xsl:if with a false test, or an
                    // xsl:for-each over an empty sequence) produces no content. For a declared
                    // type that permits the empty sequence (?, *) the value is the EMPTY
                    // SEQUENCE — NOT an empty string. Binding "" here made a value comparison
                    // against the variable atomize it as xs:string: XSpec's x:saxon-version
                    // (as="xs:integer?", empty for a non-Saxon processor) raised a spurious
                    // XPTY0004 on `$x:saxon-version lt x:pack-version((11,0))`. Mirrors the
                    // local-variable guard (see BindVariableAsync).
                    if (content.Length == 0
                        && global.As.Occurrence is Occurrence.ZeroOrOne or Occurrence.ZeroOrMore)
                    {
                        context.GlobalVariables[global.Name] = null;
                    }
                    // For global xsl:variable with as="atomic-type" body, coerce the body's
                    // text content to the declared type. Without this, the variable was
                    // bound to the raw STRING (e.g. "2147483647") and downstream comparisons
                    // like `$depth gt 1` failed with XPTY0004 ("Cannot compare xs:string with
                    // numeric type"). Found in Docbook xslTNG `vp:section-toc-depth`
                    // (as="xs:integer" with xsl:choose+xsl:sequence body) — minimal repro:
                    // `<xsl:variable as="xs:integer"><xsl:sequence select="2147483647"/></xsl:variable>`.
                    else if (DefaultXsltExecutionContext.IsCastableAtomicTypePublic(global.As.ItemType)
                        && !content.Contains('<', StringComparison.Ordinal))
                    {
                        // Cast the body's text value to the declared atomic type via the
                        // canonical caster (covers untypedAtomic/anyURI/G*/date-time/duration
                        // that the narrower TryCoerceStringToType/IsStrictAtomicType gate missed,
                        // so `instance of xs:TYPE` now holds — attr/as-0105, as-0111).
                        object? coerced;
                        try { coerced = PhoenixmlDb.XQuery.Execution.TypeCastHelper.CastValue(content, global.As.ItemType); }
                        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                        { _ = ex; coerced = null; }
                        context.GlobalVariables[global.Name] = coerced ?? (object)content;
                    }
                    else
                    {
                        context.GlobalVariables[global.Name] = content.Contains('<', StringComparison.Ordinal)
                            ? new ResultTreeFragment(content)
                            : (object)content;
                    }
                }
    }

    private static void AddTransitiveFunctionDependencies(
        HashSet<QName> functionRefs,
        Dictionary<QName, HashSet<QName>> functionDeps,
        HashSet<QName> globalNames,
        HashSet<QName> deps)
    {
        foreach (var funcRef in functionRefs)
        {
            HashSet<QName>? funcVarDeps = null;

            // Direct lookup (works when namespace IDs match)
            if (!functionDeps.TryGetValue(funcRef, out funcVarDeps) && funcRef.Namespace == default && funcRef.Prefix != null)
            {
                // XPath parser doesn't resolve namespaces at parse time, so function calls
                // have NamespaceId.None with a prefix. Match by prefix + localname against
                // the resolved function names in the dependency map.
                foreach (var (key, value) in functionDeps)
                {
                    if (key.LocalName == funcRef.LocalName && key.Prefix == funcRef.Prefix)
                    {
                        funcVarDeps = value;
                        break;
                    }
                }
            }

            if (funcVarDeps != null)
            {
                foreach (var varDep in funcVarDeps)
                {
                    if (globalNames.Contains(varDep))
                        deps.Add(varDep);
                }
            }
        }
    }

    /// <summary>
    /// Recursively collects global declarations from a stylesheet and its imports.
    /// Main stylesheet declarations take precedence over imported ones (first seen wins).
    /// </summary>
    private static void CollectGlobalDeclarations(
        XsltStylesheet stylesheet,
        List<GlobalDeclaration> globals,
        HashSet<QName> seenNames)
    {
        foreach (var param in stylesheet.Parameters)
        {
            if (seenNames.Add(param.Name))
            {
                globals.Add(new GlobalDeclaration
                {
                    Name = param.Name,
                    IsParam = true,
                    Select = param.Select,
                    Content = param.Content,
                    As = param.As,
                    OriginalDeclaration = param,
                    BaseUri = param.BaseUri,
                    Version = param.Version
                });
            }
        }

        foreach (var variable in stylesheet.Variables)
        {
            if (seenNames.Add(variable.Name))
            {
                globals.Add(new GlobalDeclaration
                {
                    Name = variable.Name,
                    IsParam = false,
                    Select = variable.Select,
                    Content = variable.Content,
                    As = variable.As,
                    OriginalDeclaration = variable,
                    BaseUri = variable.BaseUri,
                    Version = variable.Version,
                    PackageStylesheet = variable.PackageStylesheet
                });
            }
        }

        // Recursively collect from imports (lower precedence than importing stylesheet).
        // Later imports have higher precedence than earlier ones, so iterate in reverse.
        for (var i = stylesheet.Imports.Count - 1; i >= 0; i--)
        {
            CollectGlobalDeclarations(stylesheet.Imports[i], globals, seenNames);
        }
    }

    /// <summary>
    /// Builds a map of function names to the global variables they reference (transitively).
    /// </summary>
    private Dictionary<QName, HashSet<QName>> BuildFunctionDependencies()
    {
        var result = new Dictionary<QName, HashSet<QName>>();

        // First pass: collect direct dependencies for each function
        foreach (var func in _stylesheet.Functions.Values)
        {
            var collector = new DependencyCollector();

            // Walk the function body to find variable references
            foreach (var instruction in func.Body.Instructions)
            {
                CollectInstructionDependencies(instruction, collector);
            }

            result[func.Name] = new HashSet<QName>(collector.VariableRefs);
        }

        // Second pass: resolve transitive dependencies through function calls
        // (This is a simplified version - a full implementation would use fixed-point iteration)
        var changed = true;
        var maxIterations = 10; // Prevent infinite loops
        while (changed && maxIterations-- > 0)
        {
            changed = false;
            foreach (var func in _stylesheet.Functions.Values)
            {
                var collector = new DependencyCollector();
                foreach (var instruction in func.Body.Instructions)
                {
                    CollectInstructionDependencies(instruction, collector);
                }

                var currentDeps = result[func.Name];
                var originalCount = currentDeps.Count;

                // Add dependencies from called functions
                foreach (var calledFunc in collector.FunctionRefs)
                {
                    if (result.TryGetValue(calledFunc, out var calledDeps))
                    {
                        foreach (var dep in calledDeps)
                            currentDeps.Add(dep);
                    }
                }

                if (currentDeps.Count > originalCount)
                    changed = true;
            }
        }

        return result;
    }

    /// <summary>
    /// Collects variable and function references from an XSLT instruction.
    /// </summary>
    private static void CollectInstructionDependencies(XsltInstruction instruction, DependencyCollector collector)
    {
        switch (instruction)
        {
            case XsltValueOf valueOf when valueOf.Select != null:
                collector.Walk(valueOf.Select);
                break;
            case XsltCopyOf copyOf:
                collector.Walk(copyOf.Select);
                break;
            case XsltIf ifInstr:
                collector.Walk(ifInstr.Test);
                foreach (var child in ifInstr.Then.Instructions)
                    CollectInstructionDependencies(child, collector);
                break;
            case XsltForEach forEach:
                collector.Walk(forEach.Select);
                foreach (var child in forEach.Body.Instructions)
                    CollectInstructionDependencies(child, collector);
                break;
            case XsltChoose choose:
                foreach (var when in choose.When)
                {
                    collector.Walk(when.Test);
                    foreach (var child in when.Body.Instructions)
                        CollectInstructionDependencies(child, collector);
                }
                if (choose.Otherwise != null)
                {
                    foreach (var child in choose.Otherwise.Instructions)
                        CollectInstructionDependencies(child, collector);
                }
                break;
            case XsltSequence seq when seq.Select != null:
                collector.Walk(seq.Select);
                break;
                // Add more instruction types as needed
        }
    }

    /// <summary>
    /// Topologically sorts global declarations based on their dependencies.
    /// </summary>
    /// <summary>
    /// Matches a variable REFERENCE against the set of declared global names, rescuing the case
    /// where the two carry different interned <c>NamespaceId</c>s for the same namespace URI.
    /// </summary>
    /// <remarks>
    /// The reference is produced by the XPath parser and the declaration by the stylesheet
    /// parser. For an unprefixed name both sides use <c>NamespaceId.None</c> and the fast path
    /// matches; for a NAMESPACED name the ids can differ, the set lookup misses, and the
    /// dependency is never recorded. Nothing errors — the topological sort simply does not order
    /// the pair, so a global declared BEFORE the one it selects from is evaluated first and
    /// reads an unbound value. Observed as node kinds collapsing: comment() and element() came
    /// back as documents, attribute() as an empty string, namespace-node() as text.
    ///
    /// See QNameNamespaces, which exists because this identity trap had already cost several
    /// separate bugs.
    /// </remarks>
    private QName? ResolveGlobalRef(QName varRef, HashSet<QName> globalNames)
    {
        if (globalNames.Contains(varRef))
            return varRef;

        string? ResolveViaStylesheet(QName q)
            => !string.IsNullOrEmpty(q.Prefix)
               && _stylesheet.Namespaces.TryGetValue(q.Prefix, out var declared) ? declared : null;

        foreach (var candidate in globalNames)
        {
            if (QNameNamespaces.SameExpandedName(candidate, varRef, ResolveViaStylesheet))
                return candidate;
        }
        return null;
    }

    private static List<GlobalDeclaration> TopologicalSort(
        List<GlobalDeclaration> globals,
        Dictionary<QName, HashSet<QName>> dependencies)
    {
        var result = new List<GlobalDeclaration>();
        var visited = new HashSet<QName>();
        var visiting = new HashSet<QName>(); // For cycle detection
        // Use last-wins for any remaining duplicates (importing stylesheet takes precedence)
        var globalMap = new Dictionary<QName, GlobalDeclaration>();
        foreach (var g in globals)
            globalMap[g.Name] = g;

        void Visit(GlobalDeclaration global)
        {
            if (visited.Contains(global.Name))
                return;

            if (visiting.Contains(global.Name))
            {
                // Circular dependency detected - just continue
                // XSLT spec says this is a static error, but we'll handle it gracefully
                return;
            }

            visiting.Add(global.Name);

            // Visit dependencies first
            if (dependencies.TryGetValue(global.Name, out var deps))
            {
                foreach (var depName in deps)
                {
                    if (globalMap.TryGetValue(depName, out var depGlobal))
                        Visit(depGlobal);
                }
            }

            visiting.Remove(global.Name);
            visited.Add(global.Name);
            result.Add(global);
        }

        foreach (var global in globals)
            Visit(global);

        return result;
    }

    /// <summary>
    /// Reader settings for parsing a principal source document.
    /// </summary>
    /// <remarks>
    /// XSLT needs the internal DTD subset: entity references, and the attribute declarations
    /// that make <c>id()</c> and IDREF work. So DTDs are parsed rather than prohibited — but
    /// on two leashes, because parsing a DTD is where XML's two classic attacks live:
    /// <list type="bullet">
    ///   <item><c>XmlResolver = null</c> — no external DTD or entity is ever fetched, which is
    ///   what closes XXE. Only the internal subset is honoured.</item>
    ///   <item><c>MaxCharactersFromEntities</c> — caps total entity expansion, which is what
    ///   closes the billion-laughs / quadratic-blowup family.</item>
    /// </list>
    /// Both were already applied on the with-URI path; this exists so the without-URI path
    /// cannot drift from it again.
    /// </remarks>
    private static XmlReaderSettings CreateSourceReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Parse,
        MaxCharactersFromEntities = 1_000_000,
        XmlResolver = null,
    };

    /// <summary>
    /// Transforms an XML string using the stylesheet.
    /// </summary>

    public async Task<string> TransformAsync(string xmlSource, XsltTransformOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(xmlSource);
        // Strip leading BOM character (U+FEFF) — XmlDocument.LoadXml rejects it
        if (xmlSource.Length > 0 && xmlSource[0] == '\uFEFF')
            xmlSource = xmlSource[1..];

        // Check if initial mode is streamable — if so, use streaming execution.
        // Exception: when the document-node template (match="/") that would drive the
        // streaming pass contains content we cannot drive off the live reader at the
        // document level (xsl:fork / xsl:for-each-group with group-by — no streaming
        // dispatch), the forward pass would evaluate group-by against an empty synthetic
        // document and emit nothing. Materialize the whole input and run the in-memory
        // engine instead, which evaluates group-by correctly.
        //
        // Exception 2: when the invocation entry point is an initial template (or an
        // auto-detected xsl:initial-template invoked with no source document), the streamed
        // input does NOT belong to the document-node dispatch at all — it belongs to an
        // xsl:source-document instruction INSIDE the called template, which SourceDocumentAsync
        // streams (and where a deferred XTSE3430 streamability error is surfaced). Taking the
        // document-level streaming fast path here would silently skip the initial template,
        // shallow-copy the synthetic empty document, and drop any deferred streamability error.
        // Fall through to the XdmNode dispatch so the initial template runs.
        bool entryIsInitialTemplate = options?.InitialTemplate != null
            || options?.InitialFunction != null
            || (options?.HasSourceDocument == false
                && _stylesheet.NamedTemplates.Keys.Any(k => k.LocalName == "initial-template"));
        var initialModeKey = options?.InitialMode ?? new QName(NamespaceId.None, "");
        if (_stylesheet.Modes.TryGetValue(initialModeKey, out var initialModeDecl) && initialModeDecl.Streamable
            && !entryIsInitialTemplate
            // XInclude expansion needs the whole input buffered into a DOM before ConvertToXdm;
            // the streaming forward pass bypasses that, so fall through to the buffered path.
            && options?.ExpandXInclude != true
            && !DocNodeTemplateRequiresWholeInputBuffer(options))
        {
            return await TransformStreamingAsync(xmlSource, options).ConfigureAwait(false);
        }

        // (see CreateSourceReaderSettings for the DTD/entity posture)
        // Parse the XML to an XdmNode tree
        // PreserveWhitespace must be true BEFORE LoadXml to preserve whitespace-only text nodes
        var doc = new XmlDocument { PreserveWhitespace = true };
        // Use XmlReader with base URI when a source document URI is provided,
        // so XmlNode.BaseURI is populated for document() relative URI resolution.
        {
            using var sr = new System.IO.StringReader(xmlSource);
            // Both branches must read the DTD. They did not: with a source URI the document
            // parsed its internal subset, without one it went through XmlDocument.LoadXml,
            // which prohibits DTDs outright — so the same document succeeded or failed on
            // whether a URI happened to be supplied. 12 conformance cases, including the two
            // DocBook ones, failed with "For security reasons DTD is prohibited".
            using var reader = XmlReader.Create(sr, CreateSourceReaderSettings(),
                options?.SourceDocumentUri?.AbsoluteUri ?? string.Empty);
            doc.Load(reader);
        }

        // XInclude 1.0 (opt-in): expand xi:include in the principal source before it becomes
        // XDM. A source base URI is required so relative hrefs resolve; the fixup stamps
        // xml:base on included content so base-uri() reflects the origin file (W3C base-uri-052).
        if (options?.ExpandXInclude == true)
        {
            var xiBaseUri = options.SourceDocumentUri ?? options.BaseUri
                ?? throw new XsltException(
                    "ExpandXInclude requires a source base URI. Provide SourceDocumentUri "
                    + "(XsltTransformer.SetSourceDocumentUri) so relative xi:include hrefs resolve.");
            try
            {
                PhoenixmlDb.Core.Xml.XIncludeProcessor.Expand(doc, xiBaseUri,
                    new PhoenixmlDb.Core.Xml.XIncludeOptions
                    {
                        Enabled = true,
                        AllowRemote = options.AllowRemoteXInclude,
                        Resolver = options.XIncludeResolver,
                    });
            }
            catch (PhoenixmlDb.Core.Xml.XIncludeException xie)
            {
                throw new XsltException(
                    $"XInclude expansion of the source document failed: {xie.Message}", xie);
            }
        }

        var nodeStore = new XdmInMemoryStore();
        var xdmDoc = ConvertToXdm(doc, nodeStore, options?.SourceDocumentUri?.ToString());

        // Resolve namespace URIs in patterns to NamespaceIds
        _templateIndex.ResolvePatternNamespaces(nodeStore.InternNamespace);

        // Resolve namespaces in xsl:key match patterns
        foreach (var keyDef in _stylesheet.Keys.Values)
        {
            TemplateIndex.ResolveNamespacesInPattern(keyDef.Match, nodeStore.InternNamespace);
            if (keyDef.OtherDefinitions != null)
            {
                foreach (var other in keyDef.OtherDefinitions)
                    TemplateIndex.ResolveNamespacesInPattern(other.Match, nodeStore.InternNamespace);
            }
        }

        // Resolve namespaces in accumulator rule match patterns
        foreach (var acc in _stylesheet.Accumulators.Values)
        {
            foreach (var rule in acc.Rules)
                TemplateIndex.ResolveNamespacesInPattern(rule.Match, nodeStore.InternNamespace);
        }

        // Resolve namespaces in strip-space/preserve-space NameTests
        foreach (var nt in _stylesheet.StripSpace)
            nt.ResolveNamespace(nodeStore.InternNamespace);
        foreach (var nt in _stylesheet.PreserveSpace)
            nt.ResolveNamespace(nodeStore.InternNamespace);

        // Apply xsl:strip-space declarations
        if (_stylesheet.StripSpace.Count > 0)
        {
            StripWhitespaceNodes(xdmDoc, _stylesheet.StripSpace, _stylesheet.PreserveSpace, nodeStore);
        }

        // If SourceSelect is specified, navigate to the selected node
        XdmNode sourceNode = xdmDoc;
        if (options?.SourceSelect != null)
        {
            var selected = NavigateSourceSelect(xdmDoc, options.SourceSelect, nodeStore);
            if (selected != null)
            {
                sourceNode = selected;
            }
            else
            {
                // Selected node not found (e.g., stripped whitespace text node) — treat as absent context
                options.HasSourceDocument = false;
            }
        }

        return await TransformAsync(sourceNode, options, nodeStore).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns true when <paramref name="initialMode"/> (or the default mode, when null)
    /// resolves to a declared streamable mode in the loaded stylesheet. Used by the facade
    /// to decide whether the streaming fast path applies.
    /// </summary>
    internal bool IsInitialModeStreamable(QName? initialMode = null)
    {
        var key = initialMode ?? new QName(NamespaceId.None, "");
        return _stylesheet.Modes.TryGetValue(key, out var mode) && mode.Streamable;
    }

    /// <summary>
    /// Streams an XML input from an <see cref="XmlReader"/> through the stylesheet and
    /// writes the serialized result incrementally to <paramref name="output"/>. Only valid
    /// when the stylesheet's initial mode is streamable; otherwise throws.
    /// </summary>
    /// <remarks>
    /// Cancellation is not transactional: if the caller cancels mid-stream, partial output
    /// already delivered to <paramref name="output"/> is not recoverable. Post-processing
    /// of the result (indentation, XML declaration, doctype, BOM, escape-uri-attributes)
    /// requires the full output buffer and is NOT applied on this path — the raw
    /// per-event serialized output is what reaches the caller's writer.
    /// </remarks>
    public async Task TransformAsync(XmlReader input, TextWriter output, XsltTransformOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var initialModeKey = options?.InitialMode ?? new QName(NamespaceId.None, "");
        if (!_stylesheet.Modes.TryGetValue(initialModeKey, out var initialModeDecl) || !initialModeDecl.Streamable)
            throw new XsltException(
                "TransformAsync(XmlReader, TextWriter) requires a streamable initial mode. " +
                "For non-streaming transforms use TransformAsync(string) or TransformAsync(XdmNode).");

        var outputBuilder = new StringBuilder();
        await TransformStreamingCoreAsync(input, outputBuilder, output, options).ConfigureAwait(false);
    }

    /// <summary>
    /// Transforms an XML string using streaming execution (XmlReader-based forward pass).
    /// Used when the initial mode is declared as streamable="yes".
    /// </summary>
    private async Task<string> TransformStreamingAsync(string xmlSource, XsltTransformOptions? options)
    {
        _runtimeCharacterMaps.Clear();
        using var stringReader = new System.IO.StringReader(xmlSource);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, Async = true, MaxCharactersFromEntities = 1_000_000 };
        using var xmlReader = XmlReader.Create(stringReader, settings,
            options?.SourceDocumentUri?.AbsoluteUri ?? "");
        var sb = new StringBuilder();
        await TransformStreamingCoreAsync(xmlReader, sb, outputSink: null, options).ConfigureAwait(false);
        return sb.ToString();
    }

    /// <summary>
    /// Core streaming pass shared by <see cref="TransformStreamingAsync(string, XsltTransformOptions?)"/>
    /// and <see cref="TransformAsync(XmlReader, TextWriter, XsltTransformOptions?)"/>. The caller
    /// owns the <see cref="XmlReader"/> lifetime and the result <see cref="StringBuilder"/>.
    /// When <paramref name="outputSink"/> is non-null, the execution context drains the builder
    /// to the sink at each event boundary; result post-processing (which requires the full
    /// buffer) is then skipped because it cannot apply to an incrementally-delivered stream.
    /// </summary>
    private async Task TransformStreamingCoreAsync(
        XmlReader inputReader,
        StringBuilder outputBuilder,
        TextWriter? outputSink,
        XsltTransformOptions? options)
    {
        options ??= new XsltTransformOptions();

        // Streaming is invoked with an XML source document, so mark it as present.
        // This ensures xsl:global-context-item use="required" validation passes
        // and use="absent" correctly rejects the invocation.
        options.HasSourceDocument = true;

        var nodeStore = new XdmInMemoryStore();
        _templateIndex.ResolvePatternNamespaces(nodeStore.InternNamespace);

        // Resolve namespaces in key match patterns
        foreach (var keyDef in _stylesheet.Keys.Values)
        {
            TemplateIndex.ResolveNamespacesInPattern(keyDef.Match, nodeStore.InternNamespace);
            if (keyDef.OtherDefinitions != null)
            {
                foreach (var other in keyDef.OtherDefinitions)
                    TemplateIndex.ResolveNamespacesInPattern(other.Match, nodeStore.InternNamespace);
            }
        }

        // Resolve namespaces in accumulator rule match patterns
        foreach (var acc in _stylesheet.Accumulators.Values)
        {
            foreach (var rule in acc.Rules)
                TemplateIndex.ResolveNamespacesInPattern(rule.Match, nodeStore.InternNamespace);
        }

        // Resolve namespaces in strip-space/preserve-space NameTests
        foreach (var nt in _stylesheet.StripSpace)
            nt.ResolveNamespace(nodeStore.InternNamespace);
        foreach (var nt in _stylesheet.PreserveSpace)
            nt.ResolveNamespace(nodeStore.InternNamespace);

        // Create a synthetic document node for the context
        var syntheticDocId = nodeStore.NextId();
        var syntheticDoc = new XdmDocument
        {
            Id = syntheticDocId,
            Document = new DocumentId(0),
            Children = [],
            BaseUri = options.SourceDocumentUri?.AbsoluteUri
        };
        nodeStore.Register(syntheticDoc);

        // TODO(#143 Task 1.3 — synthetic-empty sink guard, deferred): this synthetic empty
        // document node is the LEGITIMATE striding context for the streaming pass; the document
        // node template body drives the stream via subscriptions/watchers/apply-templates rather
        // than by evaluating a select against this node, so an empty evaluation result here is
        // normally CORRECT, not a collapse. The 013-sibling "silent empty" bug is a
        // guaranteed-streamable docNodeTemplate body that is NOT dispatched and instead evaluates
        // its select against this empty node. Distinguishing that regression from the many
        // correct empty-evaluations requires threading the docNodeTemplate's StreamingPlanner.Plan
        // classification into every EvaluateAsync-against-syntheticDoc call site (a broad change).
        // The document-level whole-input-buffer dispatch (DocNodeTemplateRequiresWholeInputBuffer,
        // gated below) already routes such bodies away from this node. The invariant is enforced
        // at the in-template text-only-copy sink (OwningConstructIsGuaranteedStreamable guard); the
        // document-level synthetic-empty guard is deferred to the executor-parity phase where Plan
        // becomes authoritative at the document level and the empty-eval sites collapse to one.

        var context = new DefaultXsltExecutionContext(
            _stylesheet,
            _templateIndex,
            syntheticDoc,
            outputBuilder,
            options,
            nodeStore);
        context.Owner = this;

        // Attach the optional external sink BEFORE global initialization and the streaming
        // pass so DrainStreamingOutputAsync at every event boundary flushes incrementally
        // and bounds peak buffered output.
        context._streamingOutputSink = outputSink;

        // For sink-driven streaming, post-processing (indentation, XML declaration, doctype,
        // BOM, escape-uri-attributes) cannot be applied because it requires the full buffer.
        // We emit an XML declaration up-front (when the output declaration requires one) so
        // callers consuming the writer get a well-formed prologue.
        if (outputSink != null)
        {
            var prologDecl = _stylesheet.Outputs.FirstOrDefault();
            if (prologDecl != null &&
                (prologDecl.EffectiveMethod == OutputMethod.Xml || prologDecl.EffectiveMethod == OutputMethod.Xhtml) &&
                prologDecl.OmitXmlDeclaration != true)
            {
                var encoding = prologDecl.Encoding ?? "UTF-8";
                var version = prologDecl.Version ?? "1.0";
                var decl = $"<?xml version=\"{version}\" encoding=\"{encoding}\"";
                if (prologDecl.Standalone.HasValue)
                    decl += prologDecl.Standalone.Value ? " standalone=\"yes\"" : " standalone=\"no\"";
                decl += "?>";
                await outputSink.WriteAsync(decl).ConfigureAwait(false);
            }
        }

        // In streaming mode, global variables are initialized with absent focus because
        // the source document has not been read yet. The streaming processor provides
        // per-node context during execution. This mirrors the call-template invocation
        // path in the non-streaming TransformAsync (XSLT 3.0 §5.4.1).
        context.PushContextItem(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus, 0, 0);

        await InitializeGlobalsInDependencyOrderAsync(context, outputBuilder).ConfigureAwait(false);

        context.PopContextItem();

        // Bind initial parameters as global variables
        BindExternalParameters(context, options);

        // XTDE0050: Check that all required global parameters have been supplied
        foreach (var param in _stylesheet.Parameters)
        {
            if (param.Required && !options.InitialParameters.ContainsKey(param.Name))
                throw new XsltException($"XTDE0050: Required parameter ${param.Name.LocalName} was not supplied");
        }

        // xsl:global-context-item enforcement (same as non-streaming path)
        EnforceGlobalContextItem(options);

        // Resolve applicable accumulators for the streaming pass
        var streamingAccumulators = _stylesheet.Accumulators.Count > 0
            ? _stylesheet.Accumulators.Values.ToList()
            : null;

        // For JSON/Adaptive/CSV output, mirror the non-streaming TransformAsync path:
        // collect top-level items via the sequence accumulator so xsl:map/xsl:array
        // build typed XDM values that FinalizeJsonOutput serializes once at the end.
        // Without this, top-level CreateMapAsync sees a null accumulator and falls
        // through to its adaptive-text branch (unquoted string values, no indent).
        // Reported by Martin Honnen against streamed JSON output.
        var streamingPrincipalOutput = _stylesheet.Outputs.FirstOrDefault();
        var isStreamingJsonOutput = streamingPrincipalOutput?.EffectiveMethod is OutputMethod.Json or OutputMethod.Adaptive or OutputMethod.Csv;
        if (isStreamingJsonOutput)
            context.BeginSequenceCollection();

        // Document-node template dispatch (XSLT 3.0 §6.6.1): before the streaming
        // crawl reaches the root element, the document node (/) must be offered to
        // user templates. The streaming processor's event loop starts at the first
        // ELEMENT event and so never dispatches the document node — without this,
        // a global <xsl:template match="/"> never fires and the built-in document
        // rule (recurse into children → text-only output) runs instead.
        //
        // If a user template matches the synthetic document node, execute its body
        // using the same subscription mechanism the xsl:source-document streamable
        // path uses: scan the body for streamable xsl:for-each subscriptions /
        // consuming watchers, build the processor with them, and (when the body has
        // no xsl:apply-templates) run the forward pass in subscription-dispatch-only
        // mode so the for-each bodies dispatch per matching element while the default
        // template machinery stays inert. When NO user template matches, fall through
        // to the original behavior so existing streaming stylesheets are unaffected.
        XsltTemplate? docNodeTemplate;
        using (var mc = context.AcquireMatchContext())
            docNodeTemplate = _templateIndex.FindMatchingTemplate(syntheticDoc, options.InitialMode, mc.Value);

        if (docNodeTemplate != null)
        {
            var scanner = new StreamingExpressionScanner();
            var scanResult = scanner.ScanWithSubscriptions(docNodeTemplate.Body);
            var docWatchers = scanResult.Watchers.Count > 0 ? scanResult.Watchers : null;
            var docSubscriptions = scanResult.Subscriptions.Count > 0 ? scanResult.Subscriptions : null;

            bool bodyHasApplyTemplates = ContentContainsApplyTemplatesStreaming(docNodeTemplate.Body);

            // Subscription-dispatch-only: the body drives the stream through scanned
            // for-each subscriptions (or carries no streaming consumption at all, e.g.
            // a body that only constructs literal result elements). In either case the
            // default per-element template machinery must stay inert during the pass.
            bool subscriptionOnly = !bodyHasApplyTemplates;

            var docProcessor = new StreamingXmlProcessor(
                _stylesheet, _templateIndex, context, nodeStore, options.InitialMode,
                streamingAccumulators, docWatchers, docSubscriptions, subscriptionOnly);

            // Expose the document node as the focus so the body's grounded expressions
            // (and the subscription path matchers, which are relative to /) resolve
            // against the document being streamed.
            context.PushContextItem(syntheticDoc, 1, 1);
            var savedDocProcessor = context._activeStreamingProcessor;
            var savedDocReader = context._activeStreamingReader;
            var savedDocCt = context._activeStreamingCancellationToken;
            var savedDocWatchers = context._activeStreamWatchers;
            context._activeStreamingProcessor = docProcessor;
            context._activeStreamingReader = inputReader;
            context._activeStreamingCancellationToken = options.CancellationToken;
            context._activeStreamWatchers = docWatchers;
            try
            {
                if (subscriptionOnly)
                {
                    // The body's for-each subscriptions are dispatched per matching
                    // element during the forward pass; bodies with no streaming
                    // consumption (literal-only) execute once before the drain. Mirror
                    // the source-document subscription-only path: clear the active
                    // processor/reader so the body's own evaluation doesn't re-trigger
                    // streaming, run any grounded body instructions, then stream.
                    context._activeStreamingProcessor = null;
                    context._activeStreamingReader = null;

                    if (docSubscriptions == null)
                    {
                        // Literal-only body (e.g. match="/" producing <ROOTRAN/>):
                        // execute once; the forward pass below merely drains the reader
                        // in dispatch-only mode without firing built-in templates.
                        await docNodeTemplate.Body.ExecuteAsync(context).ConfigureAwait(false);
                    }

                    await docProcessor.ProcessAsync(inputReader, options.CancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // The body contains xsl:apply-templates: executing it triggers the
                    // streaming processor through the active-processor handle, exactly
                    // as the source-document apply-templates path does.
                    await docNodeTemplate.Body.ExecuteAsync(context).ConfigureAwait(false);
                }
            }
            finally
            {
                context._activeStreamingProcessor = savedDocProcessor;
                context._activeStreamingReader = savedDocReader;
                context._activeStreamingCancellationToken = savedDocCt;
                context._activeStreamWatchers = savedDocWatchers;
                context.PopContextItem();
            }

            await context.DrainStreamingOutputAsync(options.CancellationToken).ConfigureAwait(false);

            if (isStreamingJsonOutput)
                FinalizeJsonOutput(context, outputBuilder, streamingPrincipalOutput, nodeStore, options);

            SecondaryResultDocuments = context.SecondaryResults;

            if (outputSink != null)
                return;

            var docOutput = outputBuilder.ToString();
            var docOutDecl = context.PrimaryOutputMatchedDeclaration ?? _stylesheet.Outputs.FirstOrDefault();
            docOutput = FinalizeOutput(docOutput, docOutDecl, context.PrincipalOutputCharacterMaps, FinalizeKind.StreamingBuffered);
            outputBuilder.Clear();
            outputBuilder.Append(docOutput);
            return;
        }

        var processor = new StreamingXmlProcessor(
            _stylesheet, _templateIndex, context, nodeStore, options.InitialMode,
            streamingAccumulators);
        await processor.ProcessAsync(inputReader, options.CancellationToken)
            .ConfigureAwait(false);

        // Final drain — anything emitted after the last event-boundary drain.
        await context.DrainStreamingOutputAsync(options.CancellationToken).ConfigureAwait(false);

        if (isStreamingJsonOutput)
            FinalizeJsonOutput(context, outputBuilder, streamingPrincipalOutput, nodeStore, options);

        // Surface secondary results to the engine regardless of sink mode.
        SecondaryResultDocuments = context.SecondaryResults;

        // Sink path (FinalizeKind.StreamingSink): the unified FinalizeOutput post-processing
        // (indentation / decl / doctype / BOM / escape-uri / character maps / normalization)
        // operates on a fully-buffered string and so cannot apply to output that was already
        // delivered incrementally to the TextWriter sink. This is the one delivery path that
        // intentionally does NOT route through FinalizeOutput; secondary results were already
        // surfaced above, so we leave the builder empty and return.
        if (outputSink != null)
            return;

        var output = outputBuilder.ToString();

        // Route buffered streaming output through the same full finalization the non-streaming
        // delivery paths use (TransformAsync, node-source, initial-context-item). Previously this
        // path applied a bespoke subset (indentation / xml-decl / doctype / BOM / escape-uri) and
        // omitted text-strip, html method handling, content-type, character maps, and
        // normalization \u2014 that divergence was the bug. FinalizeOutput only runs the indentation
        // step for XML/HTML/XHTML methods, so JSON already serialized via FinalizeJsonOutput above
        // is not re-indented or corrupted.
        var streamOutputDecl = context.PrimaryOutputMatchedDeclaration ?? _stylesheet.Outputs.FirstOrDefault();
        output = FinalizeOutput(output, streamOutputDecl, context.PrincipalOutputCharacterMaps, FinalizeKind.StreamingBuffered);

        // Replace the builder's contents with the post-processed output so the calling
        // wrapper sees the same string the original method returned.
        outputBuilder.Clear();
        outputBuilder.Append(output);
    }

    /// <summary>
    /// Recursively checks whether a sequence constructor contains an
    /// <c>xsl:apply-templates</c> instruction. Used by the streaming document-node
    /// template dispatch to decide whether the body drives the forward pass itself
    /// (apply-templates) or whether the processor must run in subscription-dispatch-only
    /// mode. Mirrors the local <c>ContentContainsApplyTemplates</c> helper used by the
    /// xsl:source-document streamable path.
    /// </summary>
    /// <summary>
    /// Returns true when a streamable transform would dispatch through a document-node
    /// template (<c>match="/"</c>) whose body cannot be driven off the live reader at the
    /// document level (see <see cref="StreamingSubtreeBufferDetector.RequiresWholeInputBuffer"/>).
    /// Such a body must run against a fully materialized input, so the caller routes to the
    /// in-memory engine instead of the streaming pass.
    /// </summary>
    private bool DocNodeTemplateRequiresWholeInputBuffer(XsltTransformOptions? options)
    {
        // Named-initial-template invocations don't dispatch through match="/".
        if (options?.InitialTemplate != null) return false;

        var probeStore = new XdmInMemoryStore();
        var probeDoc = new XdmDocument
        {
            Id = probeStore.NextId(),
            Document = new DocumentId(0),
            Children = []
        };
        probeStore.Register(probeDoc);

        XsltTemplate? docNodeTemplate;
        var ctx = new DefaultXsltExecutionContext(
            _stylesheet, _templateIndex, probeDoc, new StringBuilder(),
            options ?? new XsltTransformOptions(), probeStore);
        ctx.Owner = this;
        using (var mc = ctx.AcquireMatchContext())
            docNodeTemplate = _templateIndex.FindMatchingTemplate(probeDoc, options?.InitialMode, mc.Value);

        if (docNodeTemplate?.Body == null) return false;
        return DocLevelWholeInputBuffer(docNodeTemplate.Body);
    }

    /// <summary>
    /// #143 Phase 1.5 — the document-level whole-input-buffer decision, unified across the two
    /// document-level sites (<see cref="DocNodeTemplateRequiresWholeInputBuffer"/> and the
    /// xsl:source-document streamable path). ADDITIVE: it buffers when EITHER the proven legacy
    /// executor-capability detector <see cref="StreamingSubtreeBufferDetector.RequiresWholeInputBuffer"/>
    /// demands it, OR the posture/sweep classifier's <see cref="StreamingPlanner.Plan"/> derives
    /// <see cref="StreamingPlan.BufferWholeInput"/>. Because it only ever ORs in more buffering, a
    /// body the classifier declares guaranteed-streamable (<see cref="StreamingPlan.StreamInline"/>)
    /// never triggers it. This closes the doc-level cases the legacy detector alone misses.
    /// <para>
    /// (#143 Phase 1.5) The former <c>xsl:iterate</c> carve-out is RETIRED: the classifier now
    /// models the map:/array:/math: function libraries and fn:random-number-generator as grounded
    /// (namespace-aware), map/array lookups and grounded-target dynamic calls, and no longer lets an
    /// in-body xsl:variable binding's posture widen the body result posture. si-iterate-037's
    /// map-accumulating iterate body therefore classifies guaranteed-streamable
    /// (<see cref="StreamingPlan.StreamInline"/>) — it is never buffered, so no 100K-iteration hang.
    /// </para>
    /// </summary>
    internal static bool DocLevelWholeInputBuffer(Ast.XsltSequenceConstructor? body)
    {
        if (StreamingSubtreeBufferDetector.RequiresWholeInputBuffer(body))
            return true;
        if (body is null)
            return false;
        return StreamingPlanner.Plan(body, new StreamingContext(Posture.Striding, InStreamedScope: true))
            == StreamingPlan.BufferWholeInput;
    }

    private static bool ContentContainsApplyTemplatesStreaming(Ast.XsltSequenceConstructor? body)
    {
        if (body == null) return false;
        foreach (var insn in body.Instructions)
        {
            switch (insn)
            {
                case Ast.XsltApplyTemplates:
                    return true;
                // apply-templates is commonly wrapped in literal result elements
                // (e.g. <html>…<section><xsl:apply-templates/></section></html>) —
                // descend into every container so the doc-node dispatch correctly
                // recognizes the body as driving the stream via apply-templates
                // (and does NOT run in subscription-dispatch-only mode, which would
                // leave child elements unmatched). xsl:result-document is one such
                // container: a match="/" body of <xsl:result-document><xsl:apply-templates/>
                // </xsl:result-document> must keep the active streaming processor live
                // so the inner apply-templates streams into the redirected secondary
                // output rather than running against the empty synthetic document.
                case Ast.XsltLiteralResultElement lre when ContentContainsApplyTemplatesStreaming(lre.Content):
                case Ast.XsltCopy cp when ContentContainsApplyTemplatesStreaming(cp.Content):
                case Ast.XsltForEach fe when ContentContainsApplyTemplatesStreaming(fe.Body):
                case Ast.XsltResultDocument rd when ContentContainsApplyTemplatesStreaming(rd.Content):
                case Ast.XsltSequenceConstructor nested when ContentContainsApplyTemplatesStreaming(nested):
                    return true;
                case Ast.XsltIf i when ContentContainsApplyTemplatesStreaming(i.Then):
                    return true;
                case Ast.XsltChoose c:
                    foreach (var w in c.When)
                        if (ContentContainsApplyTemplatesStreaming(w.Body)) return true;
                    if (ContentContainsApplyTemplatesStreaming(c.Otherwise)) return true;
                    break;
                // xsl:try is transparent to the streaming crawl: a body-form
                // <xsl:try><xsl:apply-templates/><xsl:catch/></xsl:try> at match="/"
                // drives the stream through the wrapped apply-templates exactly as a
                // bare apply-templates would (the catch only supplies a grounded fallback
                // on error). Descend into the try body and each catch body so the
                // doc-node dispatch keeps the active streaming processor live rather than
                // running in subscription-dispatch-only mode — which would leave the
                // wrapped apply-templates evaluating against the empty synthetic document
                // and emit nothing (si-try-200).
                case Ast.XsltTry tr:
                    if (tr.Body != null && ContentContainsApplyTemplatesStreaming(tr.Body)) return true;
                    foreach (var cat in tr.Catches)
                        if (cat.Body != null && ContentContainsApplyTemplatesStreaming(cat.Body)) return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// Navigates from a document root to the node specified by a simple XPath expression.
    /// Supports expressions like "/doc", "/doc/child", "/*".
    /// </summary>
    private static XdmNode? NavigateSourceSelect(XdmDocument doc, string select, XdmInMemoryStore store)
    {
        var trimmed = select.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed == "/")
            return doc;

        // Parse simple path: /name1/name2/...
        if (!trimmed.StartsWith('/'))
            return null;

        XdmNode current = doc;
        var segments = trimmed[1..].Split('/');
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment))
                continue;

            // Find matching child node
            XdmNode? found = null;
            var children = current switch
            {
                XdmDocument docNode => docNode.Children,
                XdmElement elemNode => elemNode.Children,
                _ => []
            };

            if (segment == "text()")
            {
                // Select first text child
                foreach (var childId in children)
                {
                    var child = store.GetNode(childId);
                    if (child is XdmText)
                    {
                        found = child;
                        break;
                    }
                }
            }
            else
            {
                // Select first matching element child
                foreach (var childId in children)
                {
                    var child = store.GetNode(childId);
                    if (child is XdmElement elem && (segment == "*" || elem.LocalName == segment))
                    {
                        found = child;
                        break;
                    }
                }
            }

            if (found == null)
                return null;
            current = found;
        }

        return current;
    }

    internal static XdmDocument ConvertToXdm(XmlDocument doc, XdmInMemoryStore store, string? documentUri = null)
    {
        var docId = store.NextId();
        var docElementId = NodeId.None;

        var children = new List<NodeId>();
        foreach (XmlNode child in doc.ChildNodes)
        {
            // Skip text nodes (including whitespace) at the document level.
            // While the XDM spec allows text children of document nodes, in practice
            // XML parsers may add whitespace artifacts. Exclude them from children but
            // still include in string value (computed from doc.InnerText).
            if (child.NodeType is XmlNodeType.Text or XmlNodeType.Whitespace
                or XmlNodeType.SignificantWhitespace or XmlNodeType.CDATA)
                continue;

            var effectiveDocUri = documentUri ?? doc.BaseURI;
            var childNode = ConvertXmlNode(child, store, docId, new DocumentId(1), effectiveDocUri);
            if (childNode != null)
            {
                children.Add(childNode.Id);
                if (childNode is XdmElement && docElementId == NodeId.None)
                    docElementId = childNode.Id;
            }
        }

        var docNode = new XdmDocument
        {
            Id = docId,
            Document = new DocumentId(1),
            Parent = NodeId.None,
            DocumentElement = docElementId,
            DocumentUri = documentUri ?? doc.BaseURI,
            Children = children,
            DocumentElementLocalName = doc.DocumentElement?.LocalName
        };
        // Compute string value (concatenation of all descendant text nodes).
        // Use DocumentElement.InnerText to exclude document-level whitespace text nodes
        // that aren't modeled as XDM children (we skip text at document level above).
        docNode._stringValue = doc.DocumentElement?.InnerText ?? "";

        store.Register(docNode);
        return docNode;
    }

    private static XdmNode? ConvertXmlNode(XmlNode xmlNode, XdmInMemoryStore store, NodeId parentId, DocumentId docId, string? documentBaseUri = null)
    {
        switch (xmlNode.NodeType)
        {
            case XmlNodeType.Element:
            {
                var elemId = store.NextId();
                var elemNsId = store.InternNamespace(xmlNode.NamespaceURI ?? "");

                // RECOVER (temp-tree base-URI preservation): capture and drop the sentinel
                // namespace declaration + attribute, stamping CopySourceBaseUri below.
                string? recoveredBaseUri = null;

                // Collect all in-scope namespace declarations (including inherited ones)
                var nsDecls = new List<NamespaceBinding>();
                var seenPrefixes = new HashSet<string>();
                if (xmlNode is XmlElement xmlElem)
                {
                    var nav = xmlElem.CreateNavigator()!;
                    foreach (var kvp in nav.GetNamespacesInScope(System.Xml.XmlNamespaceScope.All))
                    {
                        if (kvp.Key == "xml")
                            continue; // Skip xml namespace
                        if (string.Equals(kvp.Value, BaseSentinelNs, StringComparison.Ordinal))
                            continue; // drop the sentinel namespace declaration
                        nsDecls.Add(new NamespaceBinding(kvp.Key, store.InternNamespace(kvp.Value)));
                        seenPrefixes.Add(kvp.Key);
                    }

                    // GetNamespacesInScope reports BINDINGS, and an undeclared default namespace
                    // is the absence of one — so xmlns="" is simply omitted and left no trace in
                    // the node model. Every consumer that walks ancestors looking for the
                    // nearest default then found the one this element had explicitly undeclared:
                    //
                    //   <outer xmlns="urn:o"><undeclared xmlns=""/></outer>
                    //   in-scope-prefixes($undeclared)  ->  ("xml", "")   should be ("xml")
                    //   $undeclared/namespace::*        ->  [=urn:o]      should be xml only
                    //
                    // namespace-uri() was right the whole time (it reads the element's own
                    // NamespaceURI), so the model disagreed with itself and only the
                    // namespace-set consumers were wrong.
                    //
                    // Recording it explicitly gives GatherInScopeNamespaces the ("", None) entry
                    // it already knows how to honour — it marks "" undeclared and stops
                    // inheriting. Keyed off the literal attribute, so documents that never
                    // undeclare anything are untouched.
                    if (!seenPrefixes.Contains("") && xmlElem.HasAttribute("xmlns")
                        && xmlElem.GetAttribute("xmlns").Length == 0)
                    {
                        nsDecls.Add(new NamespaceBinding("", NamespaceId.None));
                        seenPrefixes.Add("");
                    }
                }
                else if (xmlNode.Attributes != null)
                {
                    foreach (XmlAttribute attr in xmlNode.Attributes)
                    {
                        if (attr.Name == "xmlns")
                        {
                            nsDecls.Add(new NamespaceBinding("", store.InternNamespace(attr.Value)));
                        }
                        else if (attr.Name.StartsWith("xmlns:", StringComparison.Ordinal))
                        {
                            if (string.Equals(attr.Value, BaseSentinelNs, StringComparison.Ordinal))
                                continue; // drop the sentinel namespace declaration
                            var prefix = attr.Name[6..];
                            nsDecls.Add(new NamespaceBinding(prefix, store.InternNamespace(attr.Value)));
                        }
                    }
                }

                // Convert attributes
                var attrIds = new List<NodeId>();
                if (xmlNode.Attributes != null)
                {
                    foreach (XmlAttribute attr in xmlNode.Attributes)
                    {
                        // Skip xmlns declarations
                        if (attr.Name == "xmlns" || attr.Name.StartsWith("xmlns:", StringComparison.Ordinal))
                            continue;
                        // Capture + drop the sentinel attribute (never a real XDM attribute)
                        if (string.Equals(attr.NamespaceURI, BaseSentinelNs, StringComparison.Ordinal)
                            && attr.LocalName == BaseSentinelLocalName)
                        {
                            recoveredBaseUri = attr.Value;
                            continue;
                        }

                        var attrId = store.NextId();
                        var isId = (attr.LocalName == "id" && attr.Prefix == "xml")
                            || attr.SchemaInfo?.SchemaType?.TypeCode == System.Xml.Schema.XmlTypeCode.Id
                            || (attr.OwnerDocument?.GetElementById(attr.Value) == attr.OwnerElement
                                && attr.OwnerElement != null);
                        var xdmAttr = new XdmAttribute
                        {
                            Id = attrId,
                            Document = docId,
                            Parent = elemId,
                            Namespace = store.InternNamespace(attr.NamespaceURI ?? ""),
                            LocalName = attr.LocalName,
                            Prefix = string.IsNullOrEmpty(attr.Prefix) ? null : attr.Prefix,
                            Value = attr.Value,
                            IsId = isId
                        };
                        store.Register(xdmAttr);
                        attrIds.Add(attrId);
                    }
                }

                // Convert children
                var childIds = new List<NodeId>();
                foreach (XmlNode child in xmlNode.ChildNodes)
                {
                    var childNode = ConvertXmlNode(child, store, elemId, docId, documentBaseUri);
                    if (childNode != null)
                        childIds.Add(childNode.Id);
                }

                // Compute string value (concatenation of all descendant text)
                var stringValue = xmlNode.InnerText;

                // Capture entity-derived base URI when it differs from the document's base URI
                string? entityBaseUri = null;
                if (!string.IsNullOrEmpty(xmlNode.BaseURI) && documentBaseUri != null
                    && !string.Equals(xmlNode.BaseURI, documentBaseUri, StringComparison.Ordinal))
                {
                    entityBaseUri = xmlNode.BaseURI;
                }

                var elem = new XdmElement
                {
                    Id = elemId,
                    Document = docId,
                    Parent = parentId,
                    Namespace = elemNsId,
                    LocalName = xmlNode.LocalName,
                    Prefix = string.IsNullOrEmpty(xmlNode.Prefix) ? null : xmlNode.Prefix,
                    BaseUri = entityBaseUri,
                    CopySourceBaseUri = recoveredBaseUri,
                    Attributes = attrIds,
                    Children = childIds,
                    NamespaceDeclarations = nsDecls.Count > 0
                        ? nsDecls.ToArray()
                        : XdmElement.EmptyNamespaceDeclarations
                };
                elem._stringValue = stringValue;
                store.Register(elem);
                return elem;
            }

            case XmlNodeType.Text:
            case XmlNodeType.CDATA:
            case XmlNodeType.Whitespace:
            case XmlNodeType.SignificantWhitespace:
            {
                var textId = store.NextId();
                var text = new XdmText
                {
                    Id = textId,
                    Document = docId,
                    Parent = parentId,
                    Value = xmlNode.Value ?? ""
                };
                store.Register(text);
                return text;
            }

            case XmlNodeType.Comment:
            {
                var commentId = store.NextId();
                var comment = new XdmComment
                {
                    Id = commentId,
                    Document = docId,
                    Parent = parentId,
                    Value = xmlNode.Value ?? ""
                };
                store.Register(comment);
                return comment;
            }

            case XmlNodeType.ProcessingInstruction:
            {
                var piId = store.NextId();
                // Capture entity-derived base URI for PIs
                string? piBaseUri = null;
                if (!string.IsNullOrEmpty(xmlNode.BaseURI) && documentBaseUri != null
                    && !string.Equals(xmlNode.BaseURI, documentBaseUri, StringComparison.Ordinal))
                {
                    piBaseUri = xmlNode.BaseURI;
                }
                var pi = new XdmProcessingInstruction
                {
                    Id = piId,
                    Document = docId,
                    Parent = parentId,
                    Target = xmlNode.Name,
                    Value = xmlNode.Value ?? "",
                    BaseUri = piBaseUri
                };
                store.Register(pi);
                return pi;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Strips whitespace-only text nodes from elements matching xsl:strip-space declarations.
    /// Per XSLT spec: a whitespace text node is removed if its parent element matches
    /// strip-space and does NOT match preserve-space (preserve takes priority when both match).
    /// </summary>
    internal static void StripWhitespaceNodes(XdmDocument doc, List<NameTest> stripSpace, List<NameTest> preserveSpace, XdmInMemoryStore store)
    {
        StripWhitespaceRecursive(doc, stripSpace, preserveSpace, store);

        // Recompute document string value once after all stripping is complete
        var sb = new StringBuilder();
        foreach (var childId in doc.Children)
        {
            var child = store.GetNode(childId);
            if (child is XdmText t)
                sb.Append(t.Value);
            else if (child is XdmElement e)
                sb.Append(e.StringValue);
        }
        doc._stringValue = sb.ToString();
    }

    private static void StripWhitespaceRecursive(XdmNode parent, List<NameTest> stripSpace, List<NameTest> preserveSpace, XdmInMemoryStore store)
    {
        var children = parent switch
        {
            XdmDocument d => d.Children,
            XdmElement e => e.Children,
            _ => null
        };
        if (children == null || children.Count == 0)
            return;

        // Recurse into child elements FIRST (bottom-up processing ensures
        // children have correct string values before parent recomputation)
        foreach (var childId in children)
        {
            var childNode = store.GetNode(childId);
            if (childNode is XdmElement or XdmDocument)
            {
                StripWhitespaceRecursive(childNode, stripSpace, preserveSpace, store);
            }
        }

        // If this is an element, check whether to strip whitespace text children
        if (parent is XdmElement elem)
        {
            // Find best-matching strip and preserve declarations by priority.
            // Per XSLT spec: QName=0, prefix:*=-0.25, *=-0.5
            var bestStripPriority = double.NegativeInfinity;
            var hasStripMatch = false;
            foreach (var test in stripSpace)
            {
                if (test.Matches(XdmNodeKind.Element, elem.Namespace, elem.LocalName))
                {
                    var p = NameTestDefaultPriority(test);
                    if (p > bestStripPriority)
                        bestStripPriority = p;
                    hasStripMatch = true;
                }
            }
            var bestPreservePriority = double.NegativeInfinity;
            var hasPreserveMatch = false;
            if (hasStripMatch)
            {
                foreach (var test in preserveSpace)
                {
                    if (test.Matches(XdmNodeKind.Element, elem.Namespace, elem.LocalName))
                    {
                        var p = NameTestDefaultPriority(test);
                        if (p > bestPreservePriority)
                            bestPreservePriority = p;
                        hasPreserveMatch = true;
                    }
                }
            }
            // Strip wins if it matched and either no preserve matched or strip has higher priority
            var shouldStrip = hasStripMatch &&
                (!hasPreserveMatch || bestStripPriority >= bestPreservePriority);

            if (shouldStrip && children is List<NodeId> childList)
            {
                var countBefore = childList.Count;
                childList.RemoveAll(id =>
                {
                    var node = store.GetNode(id);
                    return node is XdmText text && string.IsNullOrWhiteSpace(text.Value);
                });
                // Recompute only this element's string value (children already have
                // correct values from bottom-up processing above)
                if (childList.Count != countBefore)
                    RecomputeStringValueLocal(elem, store);
            }
        }
    }

    /// <summary>
    /// Recomputes only this element's cached _stringValue after strip-space.
    /// Does NOT walk ancestors — callers should handle ancestor recomputation separately.
    /// </summary>
    private static void RecomputeStringValueLocal(XdmElement elem, XdmInMemoryStore store)
    {
        var sb = new StringBuilder();
        CollectTextContent(elem, store, sb);
        elem._stringValue = sb.ToString();
    }

    /// <summary>
    /// Recomputes the cached _stringValue of an element after children have been modified
    /// (e.g., after strip-space removes whitespace-only text nodes).
    /// </summary>
    private static void RecomputeStringValue(XdmElement elem, XdmInMemoryStore store)
    {
        var sb = new StringBuilder();
        CollectTextContent(elem, store, sb);
        elem._stringValue = sb.ToString();

        // Also recompute ancestors up to the document root
        static void RecomputeAncestors(XdmNode node, XdmInMemoryStore s)
        {
            if (!node.Parent.HasValue || node.Parent.Value == NodeId.None)
                return;
            var parent = s.GetNode(node.Parent.Value);
            if (parent is XdmElement parentElem)
            {
                var parentSb = new StringBuilder();
                CollectTextContent(parentElem, s, parentSb);
                parentElem._stringValue = parentSb.ToString();
                RecomputeAncestors(parentElem, s);
            }
            else if (parent is XdmDocument parentDoc)
            {
                var docSb = new StringBuilder();
                foreach (var childId in parentDoc.Children)
                {
                    var child = s.GetNode(childId);
                    if (child is XdmText t)
                        docSb.Append(t.Value);
                    else if (child is XdmElement e)
                    { CollectTextContent(e, s, docSb); }
                }
                parentDoc._stringValue = docSb.ToString();
            }
        }
        RecomputeAncestors(elem, store);
    }

    private static void CollectTextContent(XdmElement elem, XdmInMemoryStore store, StringBuilder sb)
    {
        foreach (var childId in elem.Children)
        {
            var child = store.GetNode(childId);
            if (child is XdmText text)
                sb.Append(text.Value);
            else if (child is XdmElement childElem)
                CollectTextContent(childElem, store, sb);
        }
    }

    /// <summary>
    /// Computes default priority for a NameTest used in strip-space/preserve-space.
    /// QName = 0, prefix:* = -0.25, * = -0.5
    /// </summary>
    private static double NameTestDefaultPriority(NameTest test)
    {
        if (!test.IsLocalNameWildcard)
            return 0; // QName — specific element name
        if (!string.IsNullOrEmpty(test.NamespaceUri) && test.NamespaceUri != "*")
            return -0.25; // prefix:* — specific namespace, wildcard name
        return -0.5; // * — matches everything
    }

    /// <summary>
    /// Scoped capture of writes to a <see cref="StringBuilder"/> via a length cursor,
    /// not via a string copy of the existing content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaces the historical pattern:
    /// </para>
    /// <code>
    /// var savedOutput = _output.ToString();   // O(N) alloc of whole buffer
    /// _output.Clear();
    /// ... write inner content ...
    /// var inner = _output.ToString();         // O(M) alloc of inner
    /// _output.Clear();
    /// _output.Append(savedOutput);            // O(N) copy back
    /// </code>
    /// <para>
    /// with the cursor-based equivalent that allocates only the inner string and
    /// truncates the builder back to its saved length on disposal:
    /// </para>
    /// <code>
    /// var saved = new XsltTransformEngine.ScopedOutputBuffer(_output);
    /// ... write inner content ...
    /// var inner = saved.GetWritten();         // O(M) alloc — the only one
    /// saved.Dispose();                        // O(1) truncate
    /// </code>
    /// <para>
    /// At <c>N = 100 MB</c> outer / <c>M = 1 KB</c> inner, the historical pattern
    /// allocates ~200 MB and copies the buffer twice; the cursor-based form
    /// allocates ~1 KB. This shows up most acutely in deeply-nested or
    /// many-iteration XSLT constructs over Dataverse-scale source documents.
    /// </para>
    /// <para>
    /// Used as a regular struct (not <c>using var</c> ref-struct) so it can be
    /// stored in a try/finally block for code that wants to do the truncate
    /// after a possibly-throwing body — many of the existing save/restore sites
    /// have that shape.
    /// </para>
    /// </remarks>
    internal struct ScopedOutputBuffer
    {
        private readonly System.Text.StringBuilder _buffer;
        private readonly int _savedLength;

        public ScopedOutputBuffer(System.Text.StringBuilder buffer)
        {
            _buffer = buffer;
            _savedLength = buffer.Length;
        }

        /// <summary>The length of <c>_output</c> at the time this scope was opened.</summary>
        public readonly int SavedLength => _savedLength;

        /// <summary>The number of characters written to <c>_output</c> since this scope was opened.</summary>
        /// <remarks>
        /// Clamped to be non-negative: the buffer can be shorter than <c>_savedLength</c> if it was
        /// <c>Clear()</c>ed underneath an open scope (e.g. an xsl:result-document or a finalize/flush
        /// path resets <c>_output</c>). In that case nothing this scope wrote survives, so the written
        /// length is 0 — without the clamp <c>GetWritten</c> would pass a negative count to
        /// <c>StringBuilder.ToString</c> and throw <c>ArgumentOutOfRangeException</c>.
        /// </remarks>
        public readonly int WrittenLength => System.Math.Max(0, _buffer.Length - _savedLength);

        /// <summary>Returns the slice of <c>_output</c> written since this scope was opened, as a new string.</summary>
        public readonly string GetWritten()
            => WrittenLength == 0 ? string.Empty : _buffer.ToString(_savedLength, WrittenLength);

        /// <summary>Truncates <c>_output</c> back to its saved length, discarding anything written in this scope.</summary>
        /// <remarks>
        /// Only shrinks — never grows. If the buffer was already cleared/truncated below
        /// <c>_savedLength</c> (see <see cref="WrittenLength"/>), setting <c>Length = _savedLength</c>
        /// would pad it back out with NUL characters; guarding avoids that corruption.
        /// </remarks>
        public readonly void Dispose()
        {
            if (_buffer.Length > _savedLength)
                _buffer.Length = _savedLength;
        }
    }

}
