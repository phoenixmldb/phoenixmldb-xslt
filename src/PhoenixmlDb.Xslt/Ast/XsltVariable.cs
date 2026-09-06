using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

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
    /// <summary>Raw visibility attribute value (null when absent). Distinguishes an
    /// explicitly-declared public/final component from the parser's public default,
    /// which matters for XTSE3050 (package top-level components default to private).</summary>
    public string? VisibilityAttr { get; init; }
    /// <summary>
    /// True when this variable was declared visibility="abstract" and never given a concrete
    /// definition (no override). It has no evaluable value even after an xsl:accept changes
    /// its effective visibility (e.g. to hidden), so a global that references it must be
    /// deferred rather than eagerly evaluated (accept-042/043).
    /// </summary>
    public bool IsAbstract { get; init; }
    public Uri? BaseUri { get; init; }
    public string? Version { get; init; }

    /// <summary>
    /// When this variable overrides a package component, stores the original variable
    /// so that a <c>$xsl:original</c> reference in the overriding variable's select/content
    /// resolves to the overridden component's value at runtime.
    /// </summary>
    public XsltVariable? OriginalVariable { get; set; }

    /// <summary>
    /// The library package (used via xsl:use-package) that declared this global variable.
    /// Global variables are LOCAL to their declaring package (XSLT 3.0 §3.6): a reference
    /// from a component resolves the variable as seen by THAT component's package. When two
    /// packages contribute a same-named global with different values (diamond override or
    /// different used versions), only one can occupy the principal QName-keyed slot; the
    /// other is recorded as a package-local shadow and resolved per calling package
    /// (use-package-175 / use-package-176). Null for principal-package variables.
    /// </summary>
    public XsltStylesheet? PackageStylesheet { get; set; }

    /// <summary>
    /// True when the declaring used package exposes this global across its xsl:use-package
    /// boundary — its effective visibility (after xsl:expose, BEFORE the using package's
    /// xsl:accept) is public, final, or abstract. Captured at the boundary so a later
    /// xsl:accept that lowers the component to private keeps it usable inside the using
    /// package (a provided-then-accepted-private global is still visible), while a component
    /// the used package never exposed stays invisible to any other package
    /// (use-package-006 / use-package-007). Mirrors <see cref="XsltAttributeSet.ProvidedByPackage"/>.
    /// </summary>
    public bool ProvidedByPackage { get; set; }
}
