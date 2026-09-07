using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

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
    /// <summary>
    /// True when declared visibility="abstract" and never overridden with a concrete
    /// definition. It has no concrete implementation even after an xsl:accept changes its
    /// effective visibility (e.g. to hidden), so applying it is XTDE3052 (accept-047b/c).
    /// </summary>
    public bool IsAbstract { get; init; }
    public Uri? BaseUri { get; init; }

    /// <summary>
    /// When multiple xsl:attribute-set elements share the same name, each definition
    /// is stored as a separate part. Per XSLT spec section 10.2.2, evaluation must
    /// interleave each part's use-attribute-sets with its local attributes in document order.
    /// </summary>
    public List<XsltAttributeSetPart>? Parts { get; internal set; }

    /// <summary>
    /// When this attribute-set overrides a package component, stores the original
    /// attribute-set so that <c>use-attribute-sets="xsl:original"</c> in the overriding
    /// declaration resolves to the overridden component.
    /// </summary>
    public XsltAttributeSet? OriginalAttributeSet { get; set; }

    /// <summary>
    /// The library package (used via xsl:use-package) that declared this attribute-set.
    /// Set when the attribute-set is merged into a consuming stylesheet, so that a nested
    /// <c>use-attribute-sets</c> reference resolves within this attribute-set's OWN package
    /// scope (including that package's private/abstract attribute-sets) rather than against
    /// the consuming package's merged registry. Null for components declared locally.
    /// </summary>
    public XsltStylesheet? PackageStylesheet { get; set; }

    /// <summary>
    /// True when the used package exposes this attribute-set at its package boundary
    /// (declared/exposed public, final, or abstract) — i.e. it is "provided" to the using
    /// package. Captured before the using package's xsl:accept lowers the effective
    /// visibility, so a component the using package accepts as "private" is still usable
    /// (accept-002), while a component declared private in the USED package is not provided
    /// and must not leak into the using package (override-as-005).
    /// </summary>
    public bool ProvidedByPackage { get; set; }
}
