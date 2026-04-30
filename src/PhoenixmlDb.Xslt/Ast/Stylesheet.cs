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

/// <summary>
/// Validation mode for schema validation.
/// </summary>
public enum ValidationMode
{
    Strip,
    Preserve,
    Strict,
    Lax
}

/// <summary>
/// Type annotations handling.
/// </summary>
public enum TypeAnnotations
{
    Unspecified,
    Strip,
    Preserve
}

/// <summary>
/// Represents an XSLT template (xsl:template).
/// </summary>
public sealed class XsltTemplate
{
    /// <summary>
    /// Template name (optional, for named templates).
    /// </summary>
    public QName? Name { get; init; }

    /// <summary>
    /// Match pattern (required for template rules).
    /// </summary>
    public XsltPattern? Match { get; init; }

    /// <summary>
    /// Template priority.
    /// </summary>
    public double? Priority { get; init; }

    /// <summary>
    /// Template mode(s).
    /// </summary>
    public List<QName> Modes { get; init; } = new();

    /// <summary>
    /// As type (return type).
    /// </summary>
    public XdmSequenceType? As { get; init; }

    /// <summary>
    /// Template parameters.
    /// </summary>
    public List<XsltParam> Parameters { get; init; } = new();

    /// <summary>
    /// Template body (sequence of instructions).
    /// </summary>
    public required XsltSequenceConstructor Body { get; init; }

    /// <summary>
    /// Visibility (XSLT 3.0 packages).
    /// </summary>
    public Visibility Visibility { get; init; } = Visibility.Private;

    /// <summary>
    /// Shared identity for templates expanded from the same union pattern.
    /// All alternatives of a union match share this reference so next-match
    /// can skip all of them, not just the one that matched.
    /// </summary>
    public object? UnionGroupId { get; init; }

    /// <summary>
    /// Explicit version attribute on this template element (e.g., "1.0").
    /// When set, overrides the stylesheet version for backwards-compatible mode
    /// within this template's scope.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Effective base URI from xml:base on this template element.
    /// Used for static-base-uri() within the template scope.
    /// </summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// Default collation URI from the default-collation attribute on this template.
    /// </summary>
    public string? DefaultCollation { get; init; }

    /// <summary>
    /// Context item use constraint from xsl:context-item.
    /// Optional = no constraint (default), Required = context item must exist,
    /// Absent = context item must not be used.
    /// </summary>
    public ContextItemUse ContextItemUse { get; init; } = ContextItemUse.Optional;

    /// <summary>
    /// Required type for the context item (from xsl:context-item as="...").
    /// </summary>
    public XdmSequenceType? ContextItemAs { get; init; }

    /// <summary>
    /// When this template overrides a package component, stores the original template
    /// for xsl:original resolution at runtime.
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read only - needs post-init assignment
    public XsltTemplate? OriginalTemplate { get; set; }
#pragma warning restore CA2227

    /// <summary>
    /// Reference to the originating package's stylesheet (for package-local declarations).
    /// </summary>
    public XsltStylesheet? PackageStylesheet { get; set; }
}

/// <summary>
/// Constraint on the context item for a template.
/// </summary>
public enum ContextItemUse
{
    Optional,
    Required,
    Absent
}

/// <summary>
/// Visibility for package components.
/// </summary>
public enum Visibility
{
    Private,
    Public,
    Final,
    Abstract,
    Hidden
}

/// <summary>
/// Represents an XSLT pattern (used in match attributes).
/// </summary>
public abstract class XsltPattern
{
    /// <summary>
    /// Tests if a node matches this pattern.
    /// </summary>
    public abstract bool Matches(object node, XsltContext context);

    /// <summary>
    /// Computes the default priority per XSLT 3.0 spec section 6.5.
    /// </summary>
    public abstract double DefaultPriority { get; }

    /// <summary>
    /// Tests if a node matches the pattern's node test (excluding predicates).
    /// This is useful for computing position() context in pattern predicates.
    /// Default implementation returns the same as Matches with position=1.
    /// </summary>
    public virtual bool MatchesNodeTest(object node) => false;
}

/// <summary>
/// Path pattern (e.g., "para", "chapter/title", "/").
/// </summary>
public sealed class PathPattern : XsltPattern
{
    public required IReadOnlyList<PatternStep> Steps { get; init; }

    /// <summary>
    /// Default priority per XSLT 3.0 section 6.5:
    /// - Multi-step patterns (a/b, a//b): 0.5
    /// - Patterns starting with // (e.g., //*): 0.5
    /// - Patterns with predicates: 0.5
    /// - node(), text(), comment(), processing-instruction(): -0.5
    /// - * (wildcard): -0.5
    /// - ns:* (namespace wildcard): -0.25
    /// - foo (specific name test): 0
    /// - Single "/" (document root): -0.5
    /// </summary>
    public override double DefaultPriority
    {
        get
        {
            // Multi-step patterns always get 0.5
            if (Steps.Count > 1)
                return 0.5;

            if (Steps.Count == 0)
                return -0.5;

            var step = Steps[0];

            // Patterns starting with // (DescendantSeparator on first step) get 0.5
            // Per XSLT 3.0 spec 6.5: "If the pattern has the form //S [...] its priority is 0.5"
            if (step.DescendantSeparator)
                return 0.5;

            // Predicates → 0.5
            if (step.Predicates.Count > 0)
                return 0.5;

            return step.NodeTest switch
            {
                // KindTest with specific name: element(name) or attribute(name) → 0
                KindTest kt when kt.Name is { LocalName: not "*" } => 0,
                // KindTest with type only: element(*, type) or attribute(*, type) → -0.25
                KindTest kt when kt.TypeName != null => -0.25,
                // KindTest: node(), text(), comment(), element(), attribute(), etc.
                KindTest => -0.5,
                // NameTest with specific local name AND specific (or no) namespace → 0
                NameTest nt when nt.LocalName != "*" && nt.NamespaceUri != "*" => 0,
                // NameTest with wildcard local name but specific namespace (ns:*) → -0.25
                NameTest nt when nt.LocalName == "*" && nt.NamespaceUri is not null and not "*" => -0.25,
                // NameTest with wildcard namespace but specific local name (*:NCName) → -0.25
                NameTest nt when nt.NamespaceUri == "*" && nt.LocalName != "*" => -0.25,
                // NameTest with just * → -0.5
                NameTest => -0.5,
                _ => -0.5
            };
        }
    }

    public override string ToString()
    {
        return string.Join("/", Steps.Select(s =>
        {
            var prefix = s.DescendantSeparator ? "/" : "";
            var test = s.NodeTest switch
            {
                NameTest nt => nt.LocalName == "*" ? "*" : nt.LocalName,
                KindTest kt => kt.Kind switch
                {
                    XdmNodeKind.Document => "/",
                    XdmNodeKind.None => "node()",
                    XdmNodeKind.Text => "text()",
                    XdmNodeKind.Comment => "comment()",
                    XdmNodeKind.ProcessingInstruction => "processing-instruction()",
                    XdmNodeKind.Element when kt.Name?.LocalName != null => kt.Name.LocalName,
                    XdmNodeKind.Attribute when kt.Name?.LocalName != null => $"@{kt.Name.LocalName}",
                    _ => $"{kt.Kind}()"
                },
                _ => "?"
            };
            var axis = s.Axis == Axis.Attribute ? "@" : "";
            return $"{prefix}{axis}{test}";
        }));
    }

    public override bool Matches(object node, XsltContext context)
    {
        if (Steps.Count == 0)
            return false;

        // Store the original matched node for current() in pattern predicates
        // Per XSLT 3.0 spec: "current() refers to the node that is being matched by the pattern"
        context.MatchedNode = node;

        // Single-step patterns (most common)
        var lastStep = Steps[^1];

        // Match "/" (document root) — Steps contains a single step with axis self and KindTest for Document
        if (Steps.Count == 1 && lastStep.NodeTest is KindTest kt)
        {
            if (kt.Kind == XdmNodeKind.Document && node is XdmDocument doc)
            {
                if (kt.DocumentElementTest == null)
                    return true;
                // document-node(element(E)) — check document element name
                return MatchesDocumentElementTest(doc, kt.DocumentElementTest, context);
            }
            // node() on child axis doesn't match attributes (attributes are on the attribute axis)
            // node() on self axis matches any node except documents
            if (kt.Kind == XdmNodeKind.None)
            {
                if (lastStep.Axis == Axis.Child)
                    return node is XdmNode and not XdmDocument and not XdmAttribute;
                return node is XdmNode and not XdmDocument;
            }
        }

        // Match last step against the node itself
        if (!MatchesStep(lastStep, node))
            return false;

        // Evaluate predicates on the last step.
        // For multi-step patterns with descendant axis, defer predicate evaluation
        // until after finding the matching ancestor, so position() is relative to
        // all descendants of that ancestor (not just siblings).
        bool deferLastStepPredicates = Steps.Count > 1 && lastStep.Predicates.Count > 0
            && lastStep.Axis is Axis.Descendant or Axis.DescendantOrSelf;

        if (lastStep.Predicates.Count > 0 && !deferLastStepPredicates && !EvaluatePredicates(lastStep, node, context))
            return false;

        if (Steps.Count == 1)
        {
            // If the single step has DescendantSeparator (pattern "//S"), the node
            // must be in a tree rooted at a document node per XSLT 3.0 §5.5.3.
            if (lastStep.DescendantSeparator && node is XdmNode singleNode && context.NodeResolver != null)
            {
                XdmNode n = singleNode;
                while (n.Parent is { } pid && pid != NodeId.None)
                {
                    var parent = context.NodeResolver(pid);
                    if (parent == null) return false;
                    n = parent;
                }
                if (n is not XdmDocument)
                    return false;
            }
            return true;
        }

        // Multi-step patterns: walk up the tree right-to-left
        if (node is not XdmNode currentNode || context.NodeResolver == null)
            return false;

        return MatchAncestorSteps(currentNode, Steps.Count - 2, context,
            deferLastStepPredicates ? (currentNode, lastStep) : null);
    }

    /// <summary>
    /// Recursively matches remaining steps (right-to-left) by walking up the ancestor axis.
    /// </summary>
    private bool MatchAncestorSteps(XdmNode currentNode, int stepIndex, XsltContext context,
        (XdmNode originalNode, PatternStep step)? deferredDescendantPredicate = null)
    {
        if (stepIndex < 0)
        {
            // All ancestor steps matched. If we deferred the last step's predicates
            // (descendant axis), evaluate them now with position relative to this ancestor.
            if (deferredDescendantPredicate is var (origNode, defStep))
            {
                var savedAncestor = context.DescendantPositionAncestor;
                context.DescendantPositionAncestor = currentNode;
                try
                {
                    return EvaluatePredicates(defStep, origNode, context);
                }
                finally
                {
                    context.DescendantPositionAncestor = savedAncestor;
                }
            }
            return true;
        }

        var step = Steps[stepIndex];
        var nextStep = Steps[stepIndex + 1];

        // Get parent node
        var parentId = currentNode.Parent;
        if (parentId is null || parentId.Value == NodeId.None)
        {
            // XSLT 3.0 §5.5.3: "child-or-top" matching — parentless nodes
            // match A/B if they match B, regardless of A ancestor steps.
            // BUT: absolute patterns (starting with /) require a document ancestor,
            // so parentless nodes should NOT match /B or //B patterns.
            var firstStep = Steps[0];
            if (firstStep.Axis == Axis.Self && firstStep.NodeTest is KindTest { Kind: XdmNodeKind.Document })
                return false; // Absolute pattern — needs document root
            if (firstStep.DescendantSeparator)
                return false; // //B pattern — needs document root

            if (deferredDescendantPredicate is var (origNodeTop, defStepTop))
            {
                var savedAncestorTop = context.DescendantPositionAncestor;
                context.DescendantPositionAncestor = currentNode;
                try { return EvaluatePredicates(defStepTop, origNodeTop, context); }
                finally { context.DescendantPositionAncestor = savedAncestorTop; }
            }
            return true;
        }

        var parent = context.NodeResolver!(parentId.Value);
        if (parent == null)
            return false;

        // descendant/descendant-or-self axis on the next step means the current node
        // can be at any depth under the parent step, similar to '//' separator
        var walkAncestors = nextStep.DescendantSeparator
            || nextStep.Axis is Axis.Descendant or Axis.DescendantOrSelf;

        if (walkAncestors)
        {
            // Walk up any number of ancestors to find a match
            var ancestor = parent;
            while (ancestor != null)
            {
                if (MatchesStep(step, ancestor) && EvaluatePredicates(step, ancestor, context) && MatchAncestorSteps(ancestor, stepIndex - 1, context, deferredDescendantPredicate))
                    return true;

                var aParentId = ancestor.Parent;
                if (aParentId is null || aParentId.Value == NodeId.None)
                    break;
                ancestor = context.NodeResolver(aParentId.Value);
            }
            return false;
        }
        else
        {
            // '/' separator: must match direct parent
            if (!MatchesStep(step, parent) || !EvaluatePredicates(step, parent, context))
                return false;

            return MatchAncestorSteps(parent, stepIndex - 1, context, deferredDescendantPredicate);
        }
    }

    private static bool EvaluatePredicates(PatternStep step, object node, XsltContext context)
    {
        if (step.Predicates.Count == 0)
            return true;

        if (context.PredicateEvaluator == null)
            return true; // No evaluator available, skip predicate check

        // Compute position and size for predicates like [2] or [position() mod 2 = 1]
        // If Position/Last are already set (e.g., from xsl:number counting), use those values.
        // Otherwise, use the PositionComputer callback to compute on-demand.
        var position = context.Position;
        var size = context.Last;

        if (position == 0 && size == 0 && context.PositionComputer != null)
        {
            (position, size) = context.PositionComputer(node, step.NodeTest, context.DescendantPositionAncestor);
        }

        foreach (var predicate in step.Predicates)
        {
            // Pass MatchedNode for current() in pattern predicates
            if (!context.PredicateEvaluator(node, predicate, position, size, context.MatchedNode))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Tests if a node matches the last step's node test (excluding predicates).
    /// This is useful for computing position() context in pattern predicates.
    /// </summary>
    public override bool MatchesNodeTest(object node)
    {
        if (Steps.Count == 0)
            return false;
        return MatchesStep(Steps[^1], node);
    }

    /// <summary>
    /// Public accessor for MatchesStep, used by ExceptPattern/IntersectPattern for context-scoped matching.
    /// </summary>
    internal static bool MatchesStepPublic(PatternStep step, object node) => MatchesStep(step, node);

    private static bool MatchesStep(PatternStep step, object node)
    {
        return (step.Axis, node) switch
        {
            // Child axis: match elements, text, etc.
            // Per XPath spec, NameTest wildcards (*) on the child axis only match elements.
            // PIs and comments are only matched by their specific KindTests or node().
            (Axis.Child, XdmElement elem) => MatchNodeTest(step.NodeTest, XdmNodeKind.Element, elem.Namespace, elem.LocalName),
            (Axis.Child, XdmText) => step.NodeTest is KindTest { Kind: XdmNodeKind.Text or XdmNodeKind.None },
            (Axis.Child, XdmComment) => step.NodeTest is KindTest { Kind: XdmNodeKind.Comment or XdmNodeKind.None },
            (Axis.Child, XdmProcessingInstruction pi) => step.NodeTest is KindTest
                ? MatchNodeTest(step.NodeTest, XdmNodeKind.ProcessingInstruction, NamespaceId.None, pi.Target)
                : false, // NameTest (*) does not match PIs on child axis

            // Descendant / descendant-or-self axis: same node test matching as child
            (Axis.Descendant, XdmElement elem) => MatchNodeTest(step.NodeTest, XdmNodeKind.Element, elem.Namespace, elem.LocalName),
            (Axis.Descendant, XdmText) => step.NodeTest is KindTest { Kind: XdmNodeKind.Text or XdmNodeKind.None },
            (Axis.Descendant, XdmComment) => step.NodeTest is KindTest { Kind: XdmNodeKind.Comment or XdmNodeKind.None },
            (Axis.Descendant, XdmProcessingInstruction pi) => step.NodeTest is KindTest
                ? MatchNodeTest(step.NodeTest, XdmNodeKind.ProcessingInstruction, NamespaceId.None, pi.Target)
                : false,
            (Axis.DescendantOrSelf, XdmElement elem) => MatchNodeTest(step.NodeTest, XdmNodeKind.Element, elem.Namespace, elem.LocalName),
            (Axis.DescendantOrSelf, XdmText) => step.NodeTest is KindTest { Kind: XdmNodeKind.Text or XdmNodeKind.None },
            (Axis.DescendantOrSelf, XdmComment) => step.NodeTest is KindTest { Kind: XdmNodeKind.Comment or XdmNodeKind.None },
            (Axis.DescendantOrSelf, XdmProcessingInstruction pi) => step.NodeTest is KindTest
                ? MatchNodeTest(step.NodeTest, XdmNodeKind.ProcessingInstruction, NamespaceId.None, pi.Target)
                : false,

            // Attribute axis
            (Axis.Attribute, XdmAttribute attr) => MatchNodeTest(step.NodeTest, XdmNodeKind.Attribute, attr.Namespace, attr.LocalName),

            // Namespace axis
            (Axis.Namespace, XdmNamespace ns) => step.NodeTest is KindTest { Kind: XdmNodeKind.Namespace or XdmNodeKind.None }
                || step.NodeTest is NameTest { LocalName: "*" }
                || (step.NodeTest is NameTest nt && nt.LocalName == ns.Prefix),

            // Self axis - matches the node itself (for count="." in xsl:number, or "." pattern)
            (Axis.Self, XdmDocument doc) => step.NodeTest is KindTest { Kind: XdmNodeKind.Document or XdmNodeKind.None } kt
                && (kt.DocumentElementTest == null || MatchesDocumentElementTest(doc, kt.DocumentElementTest)),
            (Axis.Self, XdmElement elem) => MatchNodeTest(step.NodeTest, XdmNodeKind.Element, elem.Namespace, elem.LocalName),
            (Axis.Self, XdmText) => step.NodeTest is KindTest { Kind: XdmNodeKind.Text or XdmNodeKind.None }
                || step.NodeTest is NameTest { LocalName: "*" },
            (Axis.Self, XdmComment) => step.NodeTest is KindTest { Kind: XdmNodeKind.Comment or XdmNodeKind.None }
                || step.NodeTest is NameTest { LocalName: "*" },
            (Axis.Self, XdmProcessingInstruction pi) => step.NodeTest is NameTest { LocalName: "*" }
                || MatchNodeTest(step.NodeTest, XdmNodeKind.ProcessingInstruction, NamespaceId.None, pi.Target),
            (Axis.Self, XdmAttribute attr) => MatchNodeTest(step.NodeTest, XdmNodeKind.Attribute, attr.Namespace, attr.LocalName),
            (Axis.Self, XdmNamespace ns) => step.NodeTest is KindTest { Kind: XdmNodeKind.Namespace or XdmNodeKind.None }
                || step.NodeTest is NameTest { LocalName: "*" }
                || (step.NodeTest is NameTest snt && snt.LocalName == ns.Prefix),

            // child axis on document: only document-node() matches, NOT node()
            (Axis.Child, XdmDocument doc) => step.NodeTest is KindTest { Kind: XdmNodeKind.Document } kt
                && (kt.DocumentElementTest == null || MatchesDocumentElementTest(doc, kt.DocumentElementTest)),

            // XSLT 3.0: Self axis with atomic values - "." matches any item including atomics
            // This enables patterns like ".[. instance of xs:string]" in group-starting-with
            (Axis.Self, _) when step.NodeTest is KindTest { Kind: XdmNodeKind.None } => true,
            (Axis.Self, _) when step.NodeTest is NameTest { LocalName: "*" } => true,

            _ => false
        };
    }

    private static bool MatchNodeTest(NodeTest test, XdmNodeKind kind, NamespaceId ns, string? localName)
    {
        return test switch
        {
            NameTest nt => nt.Matches(kind, ns, localName),
            KindTest kt => kt.Matches(kind, ns, localName),
            _ => false
        };
    }

    /// <summary>
    /// Checks if a document node's document element matches the given element name test.
    /// Used for document-node(element(E)) pattern matching.
    /// </summary>
    private static bool MatchesDocumentElementTest(XdmDocument doc, NameTest elemTest, XsltContext? context = null)
    {
        if (!doc.DocumentElement.HasValue || doc.DocumentElement.Value == NodeId.None)
            return false;
        // Use the NodeResolver to look up the document element
        if (context?.NodeResolver != null)
        {
            var docElem = context.NodeResolver(doc.DocumentElement.Value);
            if (docElem is XdmElement elem)
                return elemTest.Matches(XdmNodeKind.Element, elem.Namespace, elem.LocalName);
        }
        // Without a node resolver, match optimistically (kind already matched)
        return true;
    }
}

/// <summary>
/// A step in a pattern.
/// </summary>
public sealed class PatternStep
{
    public required Axis Axis { get; init; }
    public required NodeTest NodeTest { get; init; }
    public List<XQueryExpression> Predicates { get; init; } = new();

    /// <summary>
    /// When true, this step was preceded by '//' in the pattern,
    /// meaning it can match any ancestor (not just the direct parent).
    /// </summary>
    public bool DescendantSeparator { get; init; }
}

/// <summary>
/// Union pattern (e.g., "para | title").
/// </summary>
public sealed class UnionPattern : XsltPattern
{
    public required IReadOnlyList<XsltPattern> Patterns { get; init; }

    /// <summary>
    /// For union patterns, default priority is computed per-alternative when used
    /// as a template match. However for sorting purposes, use the max of all alternatives.
    /// </summary>
    public override double DefaultPriority =>
        Patterns.Count > 0 ? Patterns.Max(p => p.DefaultPriority) : -0.5;

    public override bool Matches(object node, XsltContext context)
    {
        foreach (var pattern in Patterns)
        {
            if (pattern.Matches(node, context))
                return true;
        }
        return false;
    }

    public override bool MatchesNodeTest(object node)
    {
        foreach (var pattern in Patterns)
        {
            if (pattern.MatchesNodeTest(node))
                return true;
        }
        return false;
    }
}

/// <summary>
/// XSLT 3.0 "except" pattern (e.g., "* except q").
/// Matches node N if there exists a context node F such that N is selected by F/Left but not by F/Right.
/// </summary>
public sealed class ExceptPattern : XsltPattern
{
    public required XsltPattern Left { get; init; }
    public required XsltPattern Right { get; init; }

    public override double DefaultPriority => 0.5;

    public override bool Matches(object node, XsltContext context)
    {
        // Context-scoped matching: find a common ancestor context where
        // the node matches Left but not Right
        if (node is XdmNode xdmNode && context.NodeResolver != null)
        {
            // Try each ancestor as the context node
            var current = xdmNode;
            while (current.Parent is { } pid && pid != NodeId.None)
            {
                var parent = context.NodeResolver(pid);
                if (parent == null) break;

                if (MatchesFromContext(Left, node, parent, context)
                    && !MatchesFromContext(Right, node, parent, context))
                    return true;

                current = parent;
            }
        }

        // Fallback: try simple matching (works for same-axis patterns and parentless nodes)
        return Left.Matches(node, context) && !Right.Matches(node, context);
    }

    public override bool MatchesNodeTest(object node) => Left.MatchesNodeTest(node);

    /// <summary>
    /// Checks if a node matches a pattern when evaluated from a specific context (ancestor) node.
    /// For a PathPattern with a single step, this checks: does the axis relationship hold
    /// between contextNode and node, AND does the node match the step's node test?
    /// </summary>
    internal static bool MatchesFromContext(XsltPattern pattern, object node, XdmNode contextNode, XsltContext context)
    {
        if (pattern is PathPattern path && path.Steps.Count > 0)
        {
            // Absolute patterns (starting with '/') are context-independent —
            // the document root anchor makes them match the same nodes regardless
            // of the ancestor context. Use simple global matching.
            if (path.Steps[0].Axis == Axis.Self && path.Steps[0].NodeTest is KindTest { Kind: XdmNodeKind.Document })
                return path.Matches(node, context);

            // If any step has predicates, the optimized context-scoped matching
            // (which only checks node tests) would give incorrect results.
            // Fall back to full pattern matching which evaluates predicates.
            if (path.Steps.Any(s => s.Predicates.Count > 0))
                return path.Matches(node, context);

            var lastStep = path.Steps[^1];

            // Check if node matches the final step's node test
            if (!PathPattern.MatchesStepPublic(lastStep, node))
                return false;

            // For single-step patterns, check axis relationship between context and node
            if (path.Steps.Count == 1)
                return CheckAxisRelationship(lastStep.Axis, contextNode, node, context);

            // Multi-step: verify the path from context through intermediate steps to node
            // Walk from node up to context, checking each step
            if (node is not XdmNode xdmNode) return false;
            return MatchMultiStepFromContext(path, xdmNode, contextNode, context);
        }

        // For Except/Intersect patterns, recursively constrain both sides to the same context
        if (pattern is ExceptPattern ep)
            return MatchesFromContext(ep.Left, node, contextNode, context)
                && !MatchesFromContext(ep.Right, node, contextNode, context);
        if (pattern is IntersectPattern ip)
            return MatchesFromContext(ip.Left, node, contextNode, context)
                && MatchesFromContext(ip.Right, node, contextNode, context);

        // Variable reference and doc() patterns are context-independent
        if (pattern is VariableReferencePattern or DocFunctionPattern)
            return pattern.Matches(node, context);

        // For other pattern types (Union, etc.), use simple matching
        return pattern.Matches(node, context);
    }

    private static bool MatchMultiStepFromContext(PathPattern path, XdmNode node, XdmNode contextNode, XsltContext context)
    {
        if (context.NodeResolver == null) return false;

        // Walk from node upward, matching steps right to left
        var current = node;
        for (var i = path.Steps.Count - 2; i >= 0; i--)
        {
            var step = path.Steps[i];
            var nextStep = path.Steps[i + 1];
            var walkAncestors = nextStep.DescendantSeparator
                || nextStep.Axis is Axis.Descendant or Axis.DescendantOrSelf;

            if (walkAncestors)
            {
                // Walk up to find a matching ancestor
                var found = false;
                var ancestor = current;
                while (ancestor.Parent is { } pid && pid != NodeId.None)
                {
                    var parent = context.NodeResolver(pid);
                    if (parent == null) break;

                    if (PathPattern.MatchesStepPublic(step, parent))
                    {
                        if (i == 0)
                        {
                            // First step must match the context node
                            if (parent == contextNode) { found = true; current = parent; break; }
                        }
                        else
                        {
                            found = true; current = parent; break;
                        }
                    }
                    ancestor = parent;
                }
                if (!found) return false;
            }
            else
            {
                // Must match direct parent
                var parentId = current.Parent;
                if (parentId is null || parentId.Value == NodeId.None) return false;
                var parent = context.NodeResolver(parentId.Value);
                if (parent == null) return false;

                if (!PathPattern.MatchesStepPublic(step, parent)) return false;

                if (i == 0 && parent != contextNode) return false;

                current = parent;
            }
        }

        return true;
    }

    private static bool CheckAxisRelationship(Axis axis, XdmNode contextNode, object node, XsltContext context)
    {
        if (context.NodeResolver == null) return false;

        switch (axis)
        {
            case Axis.Child:
            {
                // node must be a direct child of contextNode
                if (node is XdmAttribute attr)
                    return attr.Parent is { } pid && pid != NodeId.None
                        && context.NodeResolver(pid) == contextNode;
                if (node is not XdmNode xdmNode) return false;
                return xdmNode.Parent is { } parentId && parentId != NodeId.None
                    && context.NodeResolver(parentId) == contextNode;
            }
            case Axis.Descendant:
            case Axis.DescendantOrSelf:
            {
                if (node is not XdmNode xdmNode) return false;
                if (axis == Axis.DescendantOrSelf && xdmNode == contextNode) return true;
                var current = xdmNode;
                while (current.Parent is { } pid && pid != NodeId.None)
                {
                    var parent = context.NodeResolver(pid);
                    if (parent == contextNode) return true;
                    if (parent == null) break;
                    current = parent;
                }
                return false;
            }
            case Axis.Self:
                return node == contextNode;
            case Axis.Attribute:
            {
                if (node is not XdmAttribute attrNode) return false;
                return attrNode.Parent is { } pid && pid != NodeId.None
                    && context.NodeResolver(pid) == contextNode;
            }
            default:
                return false;
        }
    }
}

/// <summary>
/// XSLT 3.0 "intersect" pattern (e.g., "$a intersect $b").
/// Matches node N if there exists a context node F such that N is selected by both F/Left and F/Right.
/// </summary>
public sealed class IntersectPattern : XsltPattern
{
    public required XsltPattern Left { get; init; }
    public required XsltPattern Right { get; init; }

    public override double DefaultPriority => 0.5;

    public override bool Matches(object node, XsltContext context)
    {
        // Context-scoped matching: find a common ancestor context where
        // the node matches both Left and Right
        if (node is XdmNode xdmNode && context.NodeResolver != null)
        {
            var current = xdmNode;
            while (current.Parent is { } pid && pid != NodeId.None)
            {
                var parent = context.NodeResolver(pid);
                if (parent == null) break;

                if (ExceptPattern.MatchesFromContext(Left, node, parent, context)
                    && ExceptPattern.MatchesFromContext(Right, node, parent, context))
                    return true;

                current = parent;
            }
        }

        // Fallback: simple matching
        return Left.Matches(node, context) && Right.Matches(node, context);
    }

    public override bool MatchesNodeTest(object node) => Left.MatchesNodeTest(node) && Right.MatchesNodeTest(node);
}

/// <summary>
/// XSLT 3.0 "." pattern — matches any item (node, atomic value, etc.).
/// Used in count="." for xsl:number and in group-starting-with=".[predicate]" patterns.
/// </summary>
public sealed class DotPattern : XsltPattern
{
    public IReadOnlyList<XQueryExpression> Predicates { get; init; } = [];

    public override double DefaultPriority
    {
        get
        {
            if (Predicates.Count == 0)
                return -2.0; // XSLT 3.0 §6.4 Table 2: bare "." pattern has priority -2

            // XSLT 3.0 §6.4: If the first predicate has the form ". instance of T",
            // the priority is determined by the ItemType T.
            if (Predicates[0] is PhoenixmlDb.XQuery.Ast.InstanceOfExpression inst
                && inst.Expression is PhoenixmlDb.XQuery.Ast.ContextItemExpression)
            {
                return GetItemTypePriority(inst.TargetType.ItemType);
            }

            return 0.25;
        }
    }

    /// <summary>
    /// Returns the default priority for an ItemType per XSLT 3.0 §6.4 Table 2.
    /// </summary>
    private static double GetItemTypePriority(PhoenixmlDb.XQuery.Ast.ItemType itemType)
    {
        return itemType switch
        {
            PhoenixmlDb.XQuery.Ast.ItemType.Item => -2,
            PhoenixmlDb.XQuery.Ast.ItemType.Node => -1,
            PhoenixmlDb.XQuery.Ast.ItemType.Element => -0.5,
            PhoenixmlDb.XQuery.Ast.ItemType.Attribute => -0.5,
            PhoenixmlDb.XQuery.Ast.ItemType.Text => -0.5,
            PhoenixmlDb.XQuery.Ast.ItemType.Comment => -0.5,
            PhoenixmlDb.XQuery.Ast.ItemType.ProcessingInstruction => -0.5,
            PhoenixmlDb.XQuery.Ast.ItemType.Document => -0.5,
            PhoenixmlDb.XQuery.Ast.ItemType.AnyAtomicType => 0,
            PhoenixmlDb.XQuery.Ast.ItemType.Map => -1,
            PhoenixmlDb.XQuery.Ast.ItemType.Array => -1,
            PhoenixmlDb.XQuery.Ast.ItemType.Function => -1,
            // XSLT 3.0 §6.4 Table 2 row K: named atomic types have priority +1
            _ => 1.0
        };
    }

    public override bool Matches(object node, XsltContext context)
    {
        if (Predicates.Count == 0)
            return true; // "." matches everything

        // ".[predicate]" — evaluate predicates using the PredicateEvaluator callback
        context.MatchedNode = node;
        if (context.PredicateEvaluator == null)
            return true; // No evaluator available, match without predicates

        foreach (var pred in Predicates)
        {
            if (!context.PredicateEvaluator(node, pred, 1, 1, node))
                return false;
        }
        return true;
    }

    public override bool MatchesNodeTest(object node) => true;
}

/// <summary>
/// XSLT pattern for key() function calls in match patterns.
/// Matches nodes that are in the result of key(keyName, value).
/// Supports patterns like: key('k', 'v'), key('k', $var), key('k', 'v')//child
/// </summary>
public sealed class KeyPattern : XsltPattern
{
    /// <summary>The key name (first argument to key()).</summary>
    public required string KeyName { get; init; }

    /// <summary>The value expression (second argument to key()).</summary>
    public required XQueryExpression ValueExpression { get; init; }

    /// <summary>
    /// Optional continuation pattern for descendant/child steps after key().
    /// For example, in key('k','v')//p, the continuation is the path pattern for "p".
    /// </summary>
    public XsltPattern? Continuation { get; init; }

    /// <summary>Whether the separator to the continuation is '//' (descendant) vs '/' (child).</summary>
    public bool DescendantSeparator { get; init; }

    public override double DefaultPriority => 0.5;

    public override bool Matches(object node, XsltContext context)
    {
        if (context.KeyPatternEvaluator == null)
            return false;

        if (Continuation == null)
        {
            // Simple key pattern: node must be in key() result
            return context.KeyPatternEvaluator(KeyName, ValueExpression, node);
        }

        // key('k','v')//child or key('k','v')/child pattern:
        // 1. Node must match the continuation pattern's node test
        if (!Continuation.MatchesNodeTest(node))
            return false;

        // 2. Walk up ancestors to find one that's in key() result
        if (node is not XdmNode xdmNode || context.NodeResolver == null)
            return false;

        if (DescendantSeparator)
        {
            // '//' separator: any ancestor can be in key() result
            var ancestor = xdmNode.Parent is { } pid && pid != NodeId.None ? context.NodeResolver(pid) : null;
            while (ancestor != null)
            {
                if (context.KeyPatternEvaluator(KeyName, ValueExpression, ancestor))
                {
                    // Also check continuation predicates
                    if (Continuation.Matches(node, context))
                        return true;
                }
                ancestor = ancestor is XdmNode anc && anc.Parent is { } ancPid && ancPid != NodeId.None
                    ? context.NodeResolver(ancPid) : null;
            }
        }
        else
        {
            // '/' separator: parent must be in key() result
            var parent = xdmNode.Parent is { } ppid && ppid != NodeId.None ? context.NodeResolver(ppid) : null;
            if (parent != null && context.KeyPatternEvaluator(KeyName, ValueExpression, parent))
            {
                if (Continuation.Matches(node, context))
                    return true;
            }
        }

        return false;
    }

    public override bool MatchesNodeTest(object node)
    {
        if (Continuation != null)
            return Continuation.MatchesNodeTest(node);
        return true; // key pattern without continuation could match any node
    }
}

/// <summary>
/// XSLT pattern for id() function calls in match patterns.
/// Matches nodes that are in the result of id(value).
/// Supports patterns like: id('v'), id($var), id('v')//child
/// </summary>
public sealed class IdPattern : XsltPattern
{
    /// <summary>The value expression (argument to id()).</summary>
    public required XQueryExpression ValueExpression { get; init; }

    /// <summary>
    /// Optional continuation pattern for descendant/child steps after id().
    /// For example, in id('v')//p, the continuation is the path pattern for "p".
    /// </summary>
    public XsltPattern? Continuation { get; init; }

    /// <summary>Whether the separator to the continuation is '//' (descendant) vs '/' (child).</summary>
    public bool DescendantSeparator { get; init; }

    public override double DefaultPriority => 0.5;

    public override bool Matches(object node, XsltContext context)
    {
        if (context.IdPatternEvaluator == null)
            return false;

        if (Continuation == null)
        {
            // Simple id pattern: node must be in id() result
            return context.IdPatternEvaluator(ValueExpression, node);
        }

        // id('v')//child or id('v')/child pattern:
        // 1. Node must match the continuation pattern's node test
        if (!Continuation.MatchesNodeTest(node))
            return false;

        // 2. Walk up ancestors to find one that's in id() result
        if (node is not XdmNode xdmNode || context.NodeResolver == null)
            return false;

        if (DescendantSeparator)
        {
            // '//' separator: any ancestor can be in id() result
            var ancestor = xdmNode.Parent is { } pid && pid != NodeId.None ? context.NodeResolver(pid) : null;
            while (ancestor != null)
            {
                if (context.IdPatternEvaluator(ValueExpression, ancestor))
                {
                    if (Continuation.Matches(node, context))
                        return true;
                }
                ancestor = ancestor is XdmNode anc && anc.Parent is { } ancPid && ancPid != NodeId.None
                    ? context.NodeResolver(ancPid) : null;
            }
        }
        else
        {
            // '/' separator: the id-identified element is N ancestors up,
            // where N = number of steps in the continuation pattern.
            // For id('x')/a/b/c matching node c: a=parent, b=grandparent, x=great-grandparent.
            int depth = Continuation is PathPattern pp ? pp.Steps.Count : 1;
            XdmNode? ancestor = xdmNode;
            for (int d = 0; d < depth && ancestor != null; d++)
            {
                ancestor = ancestor.Parent is { } pid && pid != NodeId.None ? context.NodeResolver(pid) as XdmNode : null;
            }
            if (ancestor != null && context.IdPatternEvaluator(ValueExpression, ancestor))
            {
                if (Continuation.Matches(node, context))
                    return true;
            }
        }

        return false;
    }

    public override bool MatchesNodeTest(object node)
    {
        if (Continuation != null)
            return Continuation.MatchesNodeTest(node);
        return true; // id pattern without continuation could match any node
    }
}

/// <summary>
/// XSLT 3.0 variable reference pattern (e.g., "$nodes", "$x//baz").
/// Matches nodes that are members of the variable's value sequence.
/// Supports optional path continuation: $var/path or $var//path.
/// </summary>
public sealed class VariableReferencePattern : XsltPattern
{
    /// <summary>The variable name (QName).</summary>
    public required QName VariableName { get; init; }

    /// <summary>
    /// Optional continuation pattern for descendant/child steps after the variable.
    /// For example, in $x//baz, the continuation is the path pattern for "baz".
    /// </summary>
    public XsltPattern? Continuation { get; init; }

    /// <summary>Whether the separator to the continuation is '//' (descendant) vs '/' (child).</summary>
    public bool DescendantSeparator { get; init; }

    public override double DefaultPriority => 0.5;

    public override bool Matches(object node, XsltContext context)
    {
        if (context.VariablePatternEvaluator == null)
            return false;

        var variableValue = context.VariablePatternEvaluator(VariableName);
        if (variableValue == null)
            return false;

        if (Continuation == null)
        {
            // Simple variable pattern: node must be a member of the variable's value
            return IsMemberOf(node, variableValue);
        }

        // $var//child or $var/child pattern:
        // 1. Node must match the continuation pattern
        if (!Continuation.MatchesNodeTest(node))
            return false;

        // 2. Walk up ancestors to find one that's in the variable's value
        if (node is not XdmNode xdmNode || context.NodeResolver == null)
            return false;

        if (DescendantSeparator)
        {
            // '//' separator: any ancestor-or-self can be in variable value
            // Check self first (for $var//descendant-or-self::*)
            var ancestor = xdmNode.Parent is { } pid && pid != NodeId.None ? context.NodeResolver(pid) : null;
            while (ancestor != null)
            {
                if (IsMemberOf(ancestor, variableValue))
                {
                    if (Continuation.Matches(node, context))
                        return true;
                }
                ancestor = ancestor is XdmNode anc && anc.Parent is { } ancPid && ancPid != NodeId.None
                    ? context.NodeResolver(ancPid) : null;
            }
        }
        else
        {
            // '/' separator: parent must be in variable value
            var parent = xdmNode.Parent is { } ppid && ppid != NodeId.None ? context.NodeResolver(ppid) : null;
            if (parent != null && IsMemberOf(parent, variableValue))
            {
                if (Continuation.Matches(node, context))
                    return true;
            }
        }

        return false;
    }

    public override bool MatchesNodeTest(object node)
    {
        if (Continuation != null)
            return Continuation.MatchesNodeTest(node);
        return true; // variable pattern without continuation could match any node
    }

    /// <summary>
    /// Checks if a node is a member (by node identity) of a variable's value sequence.
    /// </summary>
    private static bool IsMemberOf(object node, object variableValue)
    {
        // Single node
        if (variableValue is XdmNode singleNode)
            return ReferenceEquals(node, singleNode) || (node is XdmNode n && n.Id == singleNode.Id && n.Id != NodeId.None);

        // Sequence of items (object?[])
        if (variableValue is object?[] seq)
        {
            foreach (var item in seq)
            {
                if (item == null) continue;
                if (ReferenceEquals(node, item)) return true;
                if (node is XdmNode nodeN && item is XdmNode itemN && nodeN.Id == itemN.Id && nodeN.Id != NodeId.None)
                    return true;
            }
            return false;
        }

        // IList (arrays, other collections)
        if (variableValue is System.Collections.IList list)
        {
            foreach (var item in list)
            {
                if (item == null) continue;
                if (ReferenceEquals(node, item)) return true;
                if (node is XdmNode nodeN && item is XdmNode itemN && nodeN.Id == itemN.Id && nodeN.Id != NodeId.None)
                    return true;
            }
            return false;
        }

        // Single non-node item — direct equality
        return ReferenceEquals(node, variableValue);
    }
}

/// <summary>
/// XSLT 3.0 doc() function pattern (e.g., "doc('file.xml')", "doc('file.xml')//foo").
/// Matches nodes that belong to the document at the specified URI.
/// </summary>
public sealed class DocFunctionPattern : XsltPattern
{
    /// <summary>The URI argument to doc().</summary>
#pragma warning disable CA1056 // URI stored as resolved string for pattern matching
    public required string DocumentUri { get; init; }
#pragma warning restore CA1056

    /// <summary>
    /// Optional continuation pattern for descendant/child steps after doc().
    /// For example, in doc('file.xml')//foo, the continuation matches "foo".
    /// </summary>
    public XsltPattern? Continuation { get; init; }

    /// <summary>Whether the separator to the continuation is '//' (descendant) vs '/' (child).</summary>
    public bool DescendantSeparator { get; init; }

    public override double DefaultPriority => 0.5;

    public override bool Matches(object node, XsltContext context)
    {
        if (context.DocPatternEvaluator == null)
            return false;

        var docNode = context.DocPatternEvaluator(DocumentUri);
        if (docNode == null)
            return false;

        if (Continuation == null)
        {
            // Bare doc('uri') — match the document node itself
            if (node is XdmNode n)
                return ReferenceEquals(node, docNode) || (n.Id == docNode.Id && n.Id != NodeId.None);
            return false;
        }

        // doc('uri')/path or doc('uri')//path:
        // Node must match continuation AND be in the document tree of doc('uri')
        if (!Continuation.MatchesNodeTest(node))
            return false;

        if (node is not XdmNode xdmNode || context.NodeResolver == null)
            return false;

        // Walk up to the root and verify it's the doc() document
        var current = xdmNode;
        while (current.Parent is { } pid && pid != NodeId.None)
        {
            var parent = context.NodeResolver(pid);
            if (parent == null) break;
            current = parent;
        }

        // The root must be the doc() document node
        if (current is not XdmDocument)
            return false;
        if (!ReferenceEquals(current, docNode) && (current.Id != docNode.Id || current.Id == NodeId.None))
            return false;

        // Now check the continuation path
        return Continuation.Matches(node, context);
    }

    public override bool MatchesNodeTest(object node)
    {
        if (Continuation != null)
            return Continuation.MatchesNodeTest(node);
        return node is XdmDocument; // bare doc() matches document nodes
    }
}

/// <summary>
/// Represents an xsl:variable declaration.
/// </summary>
public sealed class XsltVariable
{
    public required QName Name { get; init; }
    public XdmSequenceType? As { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public bool Static { get; init; }
    public Visibility Visibility { get; init; } = Visibility.Private;
    public Uri? BaseUri { get; init; }
    public string? Version { get; init; }
}

/// <summary>
/// Represents an xsl:param declaration.
/// </summary>
public sealed class XsltParam
{
    public required QName Name { get; init; }
    public XdmSequenceType? As { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public bool Required { get; init; }
    public bool Tunnel { get; init; }
    public bool Static { get; init; }
    public string? Version { get; init; }
}

/// <summary>
/// Represents an xsl:function declaration.
/// </summary>
public sealed class XsltFunction
{
    public required QName Name { get; init; }
    public XdmSequenceType? As { get; init; }
    public required List<XsltParam> Parameters { get; init; }
    public required XsltSequenceConstructor Body { get; init; }
    public bool Override { get; init; } = true;
    public Visibility Visibility { get; init; } = Visibility.Private;
    public bool Cache { get; init; }
    /// <summary>
    /// new-each-time attribute: "yes" (default), "no", or "maybe".
    /// "no" means the function is deterministic (same args → same result).
    /// </summary>
    public string? NewEachTime { get; init; }
    /// <summary>
    /// Streaming category: null (default), "absorbing", "filter", "inspection",
    /// "shallow-descent", "deep-descent", or "ascent".
    /// </summary>
    public string? Streamability { get; init; }

    /// <summary>
    /// When this function overrides a package component, stores the original function
    /// for xsl:original resolution at runtime.
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read only - needs post-init assignment
    public XsltFunction? OriginalFunction { get; set; }
#pragma warning restore CA2227

    /// <summary>
    /// Reference to the originating package's stylesheet (for package-local declarations
    /// like decimal formats, keys, character maps, and outputs).
    /// Null for functions defined in the consuming stylesheet.
    /// </summary>
    public XsltStylesheet? PackageStylesheet { get; set; }
}

/// <summary>
/// Represents an xsl:key declaration.
/// </summary>
public sealed class XsltKey
{
    public required QName Name { get; init; }
    public required XsltPattern Match { get; init; }
    public XQueryExpression? Use { get; init; }
    public XsltSequenceConstructor? UseContent { get; init; }
    public string? Collation { get; init; }
    public bool Composite { get; init; }

    /// <summary>
    /// Additional key definitions with the same name.
    /// Per XSLT spec, multiple xsl:key declarations with the same name
    /// all contribute to the same key index (union of matches).
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read only - needs post-init assignment
    public List<XsltKey>? OtherDefinitions { get; set; }
#pragma warning restore CA2227

    /// <summary>
    /// Returns all definitions for this key name (including this one).
    /// </summary>
    public IEnumerable<XsltKey> AllDefinitions
    {
        get
        {
            yield return this;
            if (OtherDefinitions != null)
                foreach (var other in OtherDefinitions)
                    yield return other;
        }
    }
}

/// <summary>
/// Represents an xsl:output declaration.
/// </summary>
public sealed class XsltOutput
{
    public QName? Name { get; init; }
    /// <summary>Import precedence level: 0 = highest (main stylesheet), higher = lower precedence.</summary>
    public int ImportPrecedence { get; set; }
    public OutputMethod? Method { get; init; }
    public string? Version { get; init; }
    public string? Encoding { get; init; }
    public bool? OmitXmlDeclaration { get; init; }
    public bool? Standalone { get; init; }
    public string? DoctypePublic { get; init; }
    public string? DoctypeSystem { get; init; }
#pragma warning disable CA2227 // Collection properties should be read only - needs post-init assignment
    public HashSet<QName>? CdataSectionElements { get; set; }
#pragma warning restore CA2227
    public bool? Indent { get; init; }
    public string? MediaType { get; init; }
    public bool? IncludeContentType { get; init; }
    public bool? EscapeUriAttributes { get; init; }
    public bool? UndeclarePrefixes { get; init; }
    public string? NormalizationForm { get; init; }
    public string? ItemSeparator { get; init; }
    public string? HtmlVersion { get; init; }
    public string? BuildTree { get; init; }
    public bool? AllowDuplicateNames { get; init; }
    public List<QName> UseCharacterMaps { get; init; } = new();
#pragma warning disable CA2227 // Collection properties should be read only - needs post-init assignment
    /// <summary>
    /// Space-separated list of element QNames whose content should NOT be indented
    /// even when indent="yes" is specified on xsl:output.
    /// </summary>
    public HashSet<QName>? SuppressIndentation { get; set; }
#pragma warning restore CA2227
    /// <summary>
    /// When true, a UTF-8 BOM (U+FEFF) is prepended to the serialized output.
    /// </summary>
    public bool? ByteOrderMark { get; init; }
    /// <summary>
    /// Controls how XML/HTML nodes are serialized when they appear inside JSON output (method="json").
    /// Default is "xml".
    /// </summary>
    public string? JsonNodeOutputMethod { get; init; }

    /// <summary>
    /// Returns the effective output method, defaulting to Xml if not explicitly specified.
    /// </summary>
    public OutputMethod EffectiveMethod => Method ?? OutputMethod.Xml;
}

/// <summary>
/// Output method.
/// </summary>
public enum OutputMethod
{
    Xml,
    Html,
    Xhtml,
    Text,
    Json,
    Adaptive,
    Csv     // XSLT 4.0
}

/// <summary>
/// Represents an xsl:attribute-set.
/// </summary>
public sealed class XsltAttributeSet
{
    public required QName Name { get; init; }
    public List<QName> UseAttributeSets { get; init; } = new();
    public required List<XsltAttribute> Attributes { get; init; }
    public Visibility Visibility { get; init; } = Visibility.Private;
    public bool Streamable { get; init; }
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// When multiple xsl:attribute-set elements share the same name, each definition
    /// is stored as a separate part. Per XSLT spec section 10.2.2, evaluation must
    /// interleave each part's use-attribute-sets with its local attributes in document order.
    /// </summary>
    public List<XsltAttributeSetPart>? Parts { get; internal set; }
}

public sealed class XsltAttributeSetPart
{
    public required List<QName> UseAttributeSets { get; init; }
    public required List<XsltAttribute> Attributes { get; init; }
    public Uri? BaseUri { get; init; }
}

/// <summary>
/// Represents an xsl:character-map.
/// </summary>
public sealed class XsltCharacterMap
{
    public required QName Name { get; init; }
    public List<QName> UseCharacterMaps { get; init; } = new();
    public required Dictionary<char, string> Mappings { get; init; }
}

/// <summary>
/// Represents an xsl:decimal-format.
/// </summary>
public sealed class XsltDecimalFormat
{
    public QName? Name { get; init; }
    public string DecimalSeparator { get; init; } = ".";
    public string GroupingSeparator { get; init; } = ",";
    public string Infinity { get; init; } = "Infinity";
    public string MinusSign { get; init; } = "-";
    public string NaN { get; init; } = "NaN";
    public string Percent { get; init; } = "%";
    public string PerMille { get; init; } = "\u2030";
    public string ZeroDigit { get; init; } = "0";
    public string Digit { get; init; } = "#";
    public string PatternSeparator { get; init; } = ";";
    public string ExponentSeparator { get; init; } = "e";
    /// <summary>Tracks which attributes were explicitly set (vs defaulted) for merge conflict detection.</summary>
    public HashSet<string> ExplicitAttributes { get; init; } = [];
    /// <summary>
    /// Indicates a same-precedence conflict was detected during merging.
    /// Will be resolved if a higher-precedence declaration overrides it.
    /// </summary>
    public bool HasConflict { get; set; }
    /// <summary>Description of the conflict for error reporting.</summary>
    public string? ConflictDescription { get; set; }
}

/// <summary>
/// Represents an xsl:accumulator (XSLT 3.0).
/// </summary>
public sealed class XsltAccumulator
{
    public required QName Name { get; init; }
    public XdmSequenceType? As { get; init; }
    public required XQueryExpression InitialValue { get; init; }
    public required List<XsltAccumulatorRule> Rules { get; init; }
    public bool Streamable { get; init; }
    /// <summary>Original lexical name from the name attribute (for XTSE3350 duplicate detection).</summary>
    public string SourceName { get; init; } = "";
}

/// <summary>
/// Accumulator rule.
/// </summary>
public sealed class XsltAccumulatorRule
{
    public required XsltPattern Match { get; init; }
    public AccumulatorPhase Phase { get; init; } = AccumulatorPhase.Start;
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
}

/// <summary>
/// Represents an xsl:expose declaration (XSLT 3.0).
/// Changes visibility of components within the declaring package.
/// </summary>
public sealed class ExposeDeclaration
{
    public string? Component { get; init; }
    public string? Names { get; init; }
    public Visibility Visibility { get; init; }
    public System.Xml.Linq.XElement? Element { get; init; }
}

/// <summary>
/// Represents xsl:use-package (XSLT 3.0).
/// </summary>
public sealed class XsltUsePackage
{
    public required string Name { get; init; }
    public string? PackageVersion { get; init; }
    public List<XsltAccept> Accepts { get; init; } = new();
    public List<XsltOverride> Overrides { get; init; } = new();
}

/// <summary>
/// Accept declaration in use-package.
/// </summary>
public sealed class XsltAccept
{
    public required string Component { get; init; }
    public required string Names { get; init; }
    public Visibility Visibility { get; init; }
}

/// <summary>
/// Override declaration in use-package.
/// </summary>
public sealed class XsltOverride
{
    public List<XsltTemplate> Templates { get; init; } = new();
    public List<XsltFunction> Functions { get; init; } = new();
    public List<XsltVariable> Variables { get; init; } = new();
    public List<XsltParam> Parameters { get; init; } = new();
    public List<XsltAttributeSet> AttributeSets { get; init; } = new();
}

/// <summary>
/// Context for XSLT pattern matching.
/// </summary>
public class XsltContext
{
    public object? CurrentNode { get; set; }
    public int Position { get; set; }
    public int Last { get; set; }

    /// <summary>
    /// Resolves a NodeId to its XdmNode. Required for multi-step pattern matching.
    /// </summary>
    public Func<NodeId, XdmNode?>? NodeResolver { get; set; }

    /// <summary>
    /// Evaluates predicates during pattern matching.
    /// Parameters: (stepNode, expression, position, size, matchedNode) → bool
    /// - stepNode: The node being checked at the current step (may be an ancestor for multi-step patterns)
    /// - expression: The predicate expression to evaluate
    /// - position, size: For position() and last() functions
    /// - matchedNode: The node being matched by the entire pattern (for current() function)
    /// </summary>
    public Func<object, XQueryExpression, int, int, object?, bool>? PredicateEvaluator { get; set; }

    /// <summary>
    /// Computes the position of a node among its siblings matching a given node test.
    /// Returns (position, size) where position is 1-based and size is the total count.
    /// Used for evaluating positional predicates like [2] or [position() mod 2 = 1].
    /// Third parameter is optional descendant axis ancestor (for descendant axis patterns).
    /// </summary>
    public Func<object, NodeTest, object?, (int position, int size)>? PositionComputer { get; set; }

    /// <summary>
    /// When set, position computation should be relative to descendants of this ancestor
    /// rather than siblings of the parent. Used for descendant axis patterns like
    /// doc/descendant::*[position() mod 2 = 0].
    /// </summary>
    public object? DescendantPositionAncestor { get; set; }

    /// <summary>
    /// The node being matched by the entire pattern. Used for current() in multi-step patterns.
    /// Per XSLT 3.0 spec section 5.5.4: "current() refers to the node that is being matched by the pattern."
    /// This is set by PathPattern.Matches and should be used by the predicate evaluator for current().
    /// </summary>
    public object? MatchedNode { get; set; }

    /// <summary>
    /// Evaluates key() patterns during pattern matching.
    /// Parameters: (keyName, valueExpression, node) → bool
    /// Returns true if the given node is in the result of key(keyName, valueExpression).
    /// </summary>
    public Func<string, XQueryExpression, object, bool>? KeyPatternEvaluator { get; set; }

    /// <summary>
    /// Evaluates id() patterns during pattern matching.
    /// Parameters: (valueExpression, node) → bool
    /// Returns true if the given node is in the result of id(valueExpression).
    /// </summary>
    public Func<XQueryExpression, object, bool>? IdPatternEvaluator { get; set; }

    /// <summary>
    /// Evaluates variable reference patterns during pattern matching.
    /// Parameters: (variableName) → variable value (sequence or single item)
    /// Returns the current value of the named variable, or null if not found.
    /// </summary>
    public Func<QName, object?>? VariablePatternEvaluator { get; set; }

    /// <summary>
    /// Evaluates doc() function patterns during pattern matching.
    /// Parameters: (uri) → document node at that URI, or null if not available.
    /// </summary>
    public Func<string, XdmNode?>? DocPatternEvaluator { get; set; }
}

/// <summary>
/// Represents an xsl:mode declaration (XSLT 3.0).
/// </summary>
public sealed class XsltMode
{
    /// <summary>
    /// Mode name.
    /// </summary>
    public QName? Name { get; init; }

    /// <summary>
    /// Whether this mode is streamable.
    /// </summary>
    public bool Streamable { get; init; }

    /// <summary>
    /// Behavior when no template matches.
    /// </summary>
    public OnNoMatchBehavior? OnNoMatch { get; init; }

    /// <summary>
    /// Behavior when multiple templates match.
    /// </summary>
    public OnMultipleMatchBehavior OnMultipleMatch { get; init; } = OnMultipleMatchBehavior.UseLast;

    /// <summary>
    /// Whether this mode uses all accumulators (#all).
    /// </summary>
    public bool UseAllAccumulators { get; init; }

    /// <summary>
    /// Specific accumulator names referenced by use-accumulators.
    /// </summary>
    public List<QName> UseAccumulatorNames { get; init; } = new();

    /// <summary>
    /// Raw use-accumulators attribute value for XTSE0545 conflict detection across includes.
    /// Null means the attribute was not explicitly set.
    /// </summary>
    public string? UseAccumulatorsAttr { get; init; }

    /// <summary>
    /// Visibility for package components.
    /// </summary>
    public Visibility Visibility { get; init; } = Visibility.Private;

    /// <summary>
    /// Raw visibility attribute value for XTSE0545 conflict detection across includes.
    /// Null means the attribute was not explicitly set.
    /// </summary>
    public string? VisibilityAttr { get; init; }

    /// <summary>
    /// Warning behavior for typed values.
    /// </summary>
    public string? TypedValueWarnings { get; init; }

    /// <summary>
    /// Whether this mode requires typed (schema-validated) nodes.
    /// When true/yes/strict/lax, built-in templates raise XTTE3100 for untyped nodes.
    /// </summary>
    public bool Typed { get; init; }
}

/// <summary>
/// Behavior when no template matches in a mode.
/// </summary>
public enum OnNoMatchBehavior
{
    /// <summary>Perform a deep copy of the node.</summary>
    DeepCopy,
    /// <summary>Perform a shallow copy of the node.</summary>
    ShallowCopy,
    /// <summary>Skip the node entirely.</summary>
    DeepSkip,
    /// <summary>Skip only the node (not descendants).</summary>
    ShallowSkip,
    /// <summary>Output text nodes, skip other nodes.</summary>
    TextOnlyCopy,
    /// <summary>Raise an error.</summary>
    Fail
}

/// <summary>
/// Behavior when multiple templates match in a mode.
/// </summary>
public enum OnMultipleMatchBehavior
{
    /// <summary>Use the last matching template (highest import precedence, then last in document order).</summary>
    UseLast,
    /// <summary>Raise an error when multiple templates match.</summary>
    Fail
}

/// <summary>
/// Phase for accumulator rule execution.
/// </summary>
public enum AccumulatorPhase
{
    /// <summary>Execute when entering the node (start-tag).</summary>
    Start,
    /// <summary>Execute when leaving the node (end-tag).</summary>
    End
}

/// <summary>
/// Represents xsl:source-document instruction (XSLT 3.0 streaming).
/// </summary>
public sealed class XsltSourceDocument : XsltInstruction
{
    /// <summary>
    /// Source document URI.
    /// </summary>
    public required XsltAttributeValueTemplate Href { get; init; }

    /// <summary>
    /// Whether to stream the document.
    /// </summary>
    public bool Streamable { get; init; }

    /// <summary>
    /// Validation mode.
    /// </summary>
    public ValidationMode Validation { get; init; } = ValidationMode.Strip;

    /// <summary>
    /// Content to execute with the source document.
    /// </summary>
    public XsltSequenceConstructor? Content { get; init; }

    /// <summary>
    /// Effective base URI for resolving the href attribute (may differ from stylesheet base URI due to xml:base).
    /// </summary>
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// List of accumulator names to apply when processing the source document.
    /// </summary>
    public List<QName> UseAccumulators { get; init; } = new();

    /// <summary>
    /// Deferred XTSE3430 streamability error message. When set, the instruction
    /// throws at runtime instead of parse time, allowing shared stylesheets
    /// with multiple templates to compile even if some templates are non-streamable.
    /// </summary>
    public string? StreamabilityError { get; init; }

    public override T Accept<T>(IXsltInstructionVisitor<T> visitor) => visitor.VisitSourceDocument(this);

    public override ValueTask ExecuteAsync(XsltExecutionContext context)
        => context.SourceDocumentAsync(this);
}

/// <summary>
/// Captures an <c>xsl:import-schema</c> declaration. Resolved against the runtime
/// <c>ISchemaProvider</c> when the stylesheet is loaded into an <c>XsltTransformer</c>.
/// </summary>
public sealed class XsltSchemaImport
{
    /// <summary>The target namespace URI of the schema to import. Empty string for the no-namespace schema.</summary>
    public required string TargetNamespace { get; init; }

    /// <summary>Optional namespace prefix declared by the import. Null when the import has no namespace= attribute.</summary>
    public string? Prefix { get; init; }

    /// <summary>Schema-location hints from the schema-location attribute (space-separated URIs).</summary>
    public IReadOnlyList<string> SchemaLocations { get; init; } = Array.Empty<string>();

    /// <summary>Source location of the xsl:import-schema element (for diagnostic messages).</summary>
    public SourceLocation? Location { get; init; }
}
