using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

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
    /// <summary>Raw visibility attribute value (null when absent). Distinguishes an
    /// explicitly-declared public/final component from the parser's public default,
    /// which matters for XTSE3050 (package top-level components default to private).</summary>
    public string? VisibilityAttr { get; init; }
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
    /// Effective base URI of the module this function is declared in, honouring xml:base.
    /// Used for static-base-uri() within the function body, exactly as
    /// <see cref="XsltTemplate.BaseUri"/> is for a template. Without it a function declared in
    /// an imported module reported the PRINCIPAL stylesheet's URI, because the runtime falls
    /// back to that whenever nothing has been pushed.
    /// </summary>
    public Uri? BaseUri { get; init; }

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

    /// <summary>
    /// Declared in an xsl:override: it belongs to the package that uses the package it overrides
    /// in, not to that package, although it is stored with that package's components. The merge
    /// of that package leaves <see cref="PackageStylesheet"/> unset for it once, so the merge of
    /// the using package stamps it (or, in the principal, it stays null like its own components).
    /// </summary>
    internal bool DeclaredInOverride { get; set; }

    /// <summary>
    /// The visibility was given by an xsl:accept of the using package. A function accepted as
    /// private is usable in that package, unlike one that is private in its own (§3.5.3.2).
    /// </summary>
    internal bool VisibilityFromAccept { get; set; }

    /// <summary>
    /// The visibility of this component in the package that uses the package it belongs to, as
    /// XSLT 3.0 §3.5.3.2 gives it: from the xsl:accept that matches it, or else public and final
    /// become private and every other visibility hidden. Set at each xsl:use-package, so it is
    /// the visibility in the package being built; null for a component of that package itself.
    /// A hidden component cannot be referenced by the using package's own components, also when
    /// it is public where it is declared (xslt#544).
    /// </summary>
    internal Visibility? VisibilityInUsingPackage { get; set; }

    /// <summary>
    /// The visibility was stated: by the visibility attribute, or by an xsl:expose or xsl:accept.
    /// An unstated one reads as public, which is the default outside a package; in an xsl:package
    /// the default is private (§3.5).
    /// </summary>
    internal bool VisibilityStated { get; set; }
}
