using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Represents an XSLT stylesheet (xsl:stylesheet or xsl:transform).
/// </summary>
public sealed class XsltStylesheet
{
    /// <summary>
    /// XSLT version (e.g., "3.0", "4.0").
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Namespace declarations.
    /// </summary>
    public Dictionary<string, string> Namespaces { get; init; } = new();

    /// <summary>
    /// Exclude-result-prefixes.
    /// </summary>
    public HashSet<string> ExcludeResultPrefixes { get; init; } = new();

    /// <summary>
    /// Extension-element-prefixes.
    /// </summary>
    public HashSet<string> ExtensionElementPrefixes { get; init; } = new();

    /// <summary>
    /// Default namespace for unprefixed element names in XPath expressions (xpath-default-namespace).
    /// </summary>
    public string? XpathDefaultNamespace { get; init; }

    /// <summary>
    /// Default collation URI from the stylesheet element's default-collation attribute.
    /// </summary>
    public string? DefaultCollation { get; init; }

    /// <summary>
    /// Default mode for apply-templates.
    /// </summary>
    public QName? DefaultMode { get; init; }

    /// <summary>
    /// Default validation mode.
    /// </summary>
    public ValidationMode DefaultValidation { get; init; } = ValidationMode.Strip;

    /// <summary>
    /// Input type annotations.
    /// </summary>
    public TypeAnnotations InputTypeAnnotations { get; init; } = TypeAnnotations.Unspecified;

    /// <summary>
    /// Global variables (xsl:variable at top level).
    /// </summary>
    public List<XsltVariable> Variables { get; init; } = new();

    /// <summary>
    /// Global parameters (xsl:param at top level).
    /// </summary>
    public List<XsltParam> Parameters { get; init; } = new();

    /// <summary>
    /// Template rules.
    /// </summary>
    public List<XsltTemplate> Templates { get; init; } = new();

    /// <summary>
    /// Named templates.
    /// </summary>
    public Dictionary<QName, XsltTemplate> NamedTemplates { get; init; } = new();

    /// <summary>
    /// Attribute sets.
    /// </summary>
    public Dictionary<QName, XsltAttributeSet> AttributeSets { get; init; } = new();

    /// <summary>
    /// Names of attribute-sets from used packages that remain abstract (not overridden and
    /// not accepted with a concrete implementation). Referencing one at runtime is XTDE3052.
    /// </summary>
    public HashSet<QName> AbstractAttributeSetNames { get; init; } = new();

    /// <summary>
    /// Names of variables from used packages that remain abstract (not overridden with a
    /// concrete definition). A global variable of the used package that references one of
    /// these has no evaluable value, so it must be deferred (bound lazily) rather than
    /// eagerly evaluated; evaluating it — i.e. actually referencing it — is XTDE3052.
    /// (accept-042/043: an unreferenced public proxy over an abstract variable must not fail.)
    /// </summary>
    public HashSet<QName> AbstractVariableNames { get; init; } = new();

    /// <summary>
    /// Named templates from used packages that remain abstract (or were hidden by xsl:accept),
    /// so they exist as components but have no body to run. Calling one is XTDE3052; they are
    /// not merged into <see cref="NamedTemplates"/>, where they would be callable.
    /// </summary>
    public HashSet<QName> AbstractTemplateNames { get; init; } = new();

    /// <summary>
    /// Functions from used packages that remain abstract, keyed as <see cref="Functions"/> is.
    /// Calling one is XTDE3052. A stub is registered for each at function-library construction,
    /// but only where no concrete function of that name and arity exists.
    /// </summary>
    public HashSet<(QName Name, int Arity)> AbstractFunctionKeys { get; init; } = new();

    /// <summary>
    /// True when an xsl:accept in this package accepts a component with visibility="abstract".
    /// That makes this package abstract in turn, so it cannot be executed: XTSE3080.
    /// </summary>
    public bool HasAcceptedAbstractComponent { get; set; }

    /// <summary>
    /// Package-local global variables that could not take the principal QName-keyed slot
    /// because a same-named global from another package already claimed it. Each is stamped
    /// with its owning package (<see cref="XsltVariable.PackageStylesheet"/>) and resolved
    /// per calling package at runtime, so a diamond override / different-version import
    /// (use-package-175 / use-package-176) sees each package's own value rather than one
    /// shared binding. Empty for non-package stylesheets.
    /// </summary>
    public List<XsltVariable> PackageLocalShadowVariables { get; init; } = new();

    /// <summary>
    /// Global variables/parameters that a used package declares WITHOUT exposing across the
    /// xsl:use-package boundary (private — explicitly, or by the package default). Each maps
    /// the component's QName to its owning used package. Such a component is merged (so the
    /// used package's own components may still reference it) but must not be resolvable from
    /// the using package: a reference from any other package raises XPST0008
    /// (use-package-006 / use-package-007). Empty for non-package stylesheets.
    /// </summary>
    public Dictionary<QName, XsltStylesheet> PackagePrivateGlobals { get; init; } = new();

    /// <summary>
    /// A global variable of a used package is visible across the xsl:use-package boundary only
    /// when its visibility is EXPLICITLY public, final, or abstract (xsl:expose sets
    /// <see cref="XsltVariable.ProvidedByPackage"/> directly instead). A variable whose
    /// visibility merely defaulted to public — the parser's workaround so plain
    /// xsl:import/xsl:include modules stay mutually visible — is private under xsl:package and
    /// must not leak across the boundary. Callers pass <see cref="XsltVariable.VisibilityAttr"/>
    /// (the raw attribute string, null when absent).
    /// </summary>
    public static bool VisibleAcrossPackageBoundary(string? visibilityAttr) =>
        visibilityAttr is "public" or "final" or "abstract";

    /// <summary>
    /// Functions (xsl:function).
    /// </summary>
    public Dictionary<(QName Name, int Arity), XsltFunction> Functions { get; init; } = new();

    /// <summary>
    /// Keys (xsl:key).
    /// </summary>
    public Dictionary<QName, XsltKey> Keys { get; init; } = new();

    /// <summary>
    /// Output declarations (xsl:output).
    /// </summary>
    public List<XsltOutput> Outputs { get; init; } = new();

    /// <summary>
    /// Imported stylesheets (lower precedence).
    /// </summary>
    public List<XsltStylesheet> Imports { get; init; } = new();

    /// <summary>
    /// Included stylesheets (same precedence).
    /// </summary>
    public List<XsltStylesheet> Includes { get; init; } = new();

    /// <summary>
    /// Character maps (xsl:character-map).
    /// </summary>
    public Dictionary<QName, XsltCharacterMap> CharacterMaps { get; init; } = new();

    /// <summary>
    /// Decimal formats (xsl:decimal-format).
    /// </summary>
    public Dictionary<QName, XsltDecimalFormat> DecimalFormats { get; init; } = new();

    /// <summary>
    /// Strip-space elements.
    /// </summary>
    public List<NameTest> StripSpace { get; init; } = new();

    /// <summary>
    /// Preserve-space elements.
    /// </summary>
    public List<NameTest> PreserveSpace { get; init; } = new();

    /// <summary>
    /// xsl:import-schema declarations. Each entry records a target namespace and any
    /// schema-location hints. Loaded against the runtime <c>ISchemaProvider</c> when the
    /// stylesheet is bound to an <see cref="XsltTransformer"/>, after which
    /// <c>schema-element(...)</c> / <c>schema-attribute(...)</c> references and
    /// <c>validation="strict|lax"</c> attributes resolve against the schema set.
    /// </summary>
    public List<XsltSchemaImport> SchemaImports { get; init; } = new();

    /// <summary>
    /// Accumulator definitions (XSLT 3.0).
    /// </summary>
    public Dictionary<QName, XsltAccumulator> Accumulators { get; init; } = new();

    /// <summary>
    /// Accumulator names that had duplicates within this module (before import merge).
    /// </summary>
    public HashSet<QName> DuplicateAccumulatorNames { get; init; } = new();

    /// <summary>
    /// Accumulator names that were merged in from a used package (xsl:use-package).
    /// Accumulators are package-local, so an accumulator declared in the using package
    /// with the same name as one from a used package is NOT a duplicate (XTSE3350).
    /// </summary>
    public HashSet<QName> PackageMergedAccumulatorNames { get; init; } = new();

    /// <summary>
    /// When a used package and the using package (or two used packages) declare accumulators
    /// with the same name, the used package's copy is relocated in the merged registry under
    /// a synthetic internal key. This maps (owning package, original accumulator name) to that
    /// synthetic key so a component of the owning package resolves accumulator-before/after to
    /// its own accumulator rather than the merged one (override-misc-005).
    /// </summary>
    public Dictionary<(XsltStylesheet Package, QName Name), QName> PackageAccumulatorRemap { get; init; } = new();

    /// <summary>
    /// Symbolic names (kind, name, arity) of public/final components exposed by packages
    /// referenced via xsl:use-package. A component declared in the using package (outside
    /// xsl:override) whose symbolic name matches one of these is a static error (XTSE3050).
    /// Kinds: "V" variable/param, "F" function, "M" mode.
    /// </summary>
    public HashSet<(string Kind, QName Name, int Arity)> UsedComponentSymbols { get; init; } = new();

    /// <summary>
    /// Symbolic names (kind, name, arity) of components declared locally in this package
    /// at the top level (not inside xsl:override, not merged from a used package).
    /// Checked against <see cref="UsedComponentSymbols"/> for XTSE3050.
    /// </summary>
    public HashSet<(string Kind, QName Name, int Arity)> LocalComponentSymbols { get; init; } = new();

    /// <summary>
    /// Symbolic names (kind, name, arity) of components that have become visible (accepted,
    /// not hidden) components of THIS package from an xsl:use-package. Each xsl:use-package is a
    /// distinct package instance, so two such contributions of the same symbolic name — whether
    /// from two different used packages (decl/accept-020) or the same package reached by two
    /// routes in a diamond (decl/package-022err) — are two distinct components with the same
    /// name and are a static error (XTSE3050), unless one is resolved by xsl:override. Populated
    /// per use-package and propagated across xsl:include boundaries (MergeStylesheet) so a
    /// conflict spanning two included modules is detected. Kinds: "T" template, "V" variable,
    /// "F" function, "A" attribute-set, "M" mode.
    /// </summary>
    public HashSet<(string Kind, QName Name, int Arity)> AcceptedComponentSymbols { get; init; } = new();

    /// <summary>
    /// Modes referenced by local top-level template rules (outside xsl:override). Adding a
    /// template rule to a mode declared in a used package is only permitted inside
    /// xsl:override (XTSE3050).
    /// </summary>
    public HashSet<QName> LocalTemplateRuleModes { get; init; } = new();

    /// <summary>
    /// Mode names with conflicting use-accumulators at same import precedence.
    /// Deferred until import merge can check if higher-precedence declaration resolves the conflict.
    /// </summary>
    public HashSet<QName> ConflictingModeAccumulators { get; init; } = new();

    /// <summary>
    /// Mode names with conflicting visibility at same import precedence.
    /// Deferred until import merge can check if higher-precedence declaration resolves the conflict.
    /// </summary>
    public HashSet<QName> ConflictingModeVisibility { get; init; } = new();

    /// <summary>
    /// Mode names that were EXPLICITLY given private visibility by an xsl:expose
    /// declaration (as opposed to being implicitly private by the package default).
    /// An implicitly-private mode is still eligible as an initial mode, but a mode
    /// explicitly exposed as private is not — it raises XTDE0045 when requested as the
    /// initial mode. This is tracked separately so implicit modes (declared-modes="false",
    /// never appearing in <see cref="Modes"/>) can be recognised. See W3C decl/package
    /// package-001j.
    /// </summary>
    public HashSet<QName> ExplicitlyExposedPrivateModes { get; init; } = new();

    /// <summary>
    /// Use-package declarations (XSLT 3.0).
    /// </summary>
    public List<XsltUsePackage> UsePackages { get; init; } = new();

    /// <summary>
    /// Expose declarations (XSLT 3.0) — collected during parsing, applied post-parse.
    /// </summary>
    public List<ExposeDeclaration> ExposeDeclarations { get; init; } = new();

    /// <summary>
    /// Named type declarations (XSLT 4.0 xsl:item-type).
    /// Maps type QName → sequence type definition.
    /// </summary>
    public Dictionary<QName, PhoenixmlDb.XQuery.Ast.XdmSequenceType> NamedTypes { get; init; } = new();

    /// <summary>
    /// Mode declarations (XSLT 3.0).
    /// </summary>
    public Dictionary<QName, XsltMode> Modes { get; init; } = new();

    /// <summary>
    /// Namespace aliases (xsl:namespace-alias). Maps stylesheet namespace URI → (result namespace URI, preferred prefix).
    /// </summary>
    public Dictionary<string, (string ResultUri, string ResultPrefix)> NamespaceAliases { get; init; } = new();

    /// <summary>
    /// Whether undeclared modes are allowed. When false (the default for xsl:package),
    /// all modes used in templates and apply-templates must be declared via xsl:mode.
    /// </summary>
    public bool DeclaredModes { get; init; }

    /// <summary>
    /// Whether this stylesheet was declared as xsl:package.
    /// </summary>
    public bool IsPackage { get; init; }

    /// <summary>
    /// Base URI of the stylesheet (for resolving relative URIs at runtime).
    /// </summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// Global context item use constraint from xsl:global-context-item.
    /// Null = no xsl:global-context-item declaration.
    /// </summary>
    public ContextItemUse? GlobalContextItemUse { get; set; }

    /// <summary>
    /// Required type for the global context item (from xsl:global-context-item as="...").
    /// </summary>
    public XdmSequenceType? GlobalContextItemAs { get; set; }

    /// <summary>
    /// Package catalog for resolving xsl:use-package and fn:transform package-name references.
    /// Set during parsing when a package catalog is available.
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read only - needs post-init assignment
    public Dictionary<string, List<(string? Version, string FilePath)>>? PackageCatalog { get; set; }
#pragma warning restore CA2227
}
