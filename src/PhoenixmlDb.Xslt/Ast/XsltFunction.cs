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
}
