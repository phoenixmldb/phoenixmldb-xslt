using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

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
    /// xsl:mode warning-on-no-match="yes": the processor reports a warning each time a node is
    /// processed in this mode with no matching template rule, so a stylesheet author can find the
    /// nodes falling through to the built-in rule. Parsed but never acted on before — the
    /// attribute was validated and dropped.
    /// </summary>
    public bool WarningOnNoMatch { get; init; }

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
