using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

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

    /// <summary>Raw visibility attribute value (null when absent). Distinguishes an
    /// explicitly-declared public/final component from the parser's public default,
    /// which matters for XTDE0040: a package top-level named template defaults to
    /// private and is therefore ineligible as an initial (entry-point) template.</summary>
    public string? VisibilityAttr { get; init; }

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

    /// <summary>
    /// Import precedence of this template rule for conflict resolution (XSLT 3.0 §6.6.2).
    /// Higher wins. Defaults to 0. A template rule brought in via xsl:use-package has a
    /// LOWER precedence than the using package's own declarations (including rules supplied
    /// inside xsl:override), so import precedence dominates priority: when merging a used
    /// package the parser shifts its rules down one level per use-package boundary. Regular
    /// xsl:import precedence is handled structurally via nested template indexes, so this
    /// field stays 0 for non-package template rules.
    /// </summary>
    public int ImportPrecedence { get; set; }
}
