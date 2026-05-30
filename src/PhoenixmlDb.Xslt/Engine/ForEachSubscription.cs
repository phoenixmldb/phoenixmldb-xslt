using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Pairs a streaming path matcher with an <c>xsl:for-each</c> body that should
/// execute every time the matcher fires a StartElement match on the input stream.
/// </summary>
/// <remarks>
/// Registered by the streaming scanner during compile, dispatched by the
/// streaming processor as the source document is read, and wired into a
/// for-each's source document by the streaming source-document handler.
/// </remarks>
internal sealed class ForEachSubscription
{
    /// <summary>The original <c>xsl:for-each</c> AST node this subscription was derived from.</summary>
    public required Ast.XsltForEach SourceInstruction { get; init; }

    /// <summary>Path matcher fired against the input stream to identify match events.</summary>
    public required StreamPathMatcher PathMatcher { get; init; }

    /// <summary>Sequence constructor body executed once per match.</summary>
    public required Ast.XsltSequenceConstructor Body { get; init; }

    /// <summary>
    /// Grounded operands appearing BEFORE the streamable path in the for-each select.
    /// Each is evaluated as a separate for-each iteration before the streaming pass starts.
    /// </summary>
    public IReadOnlyList<XQueryExpression> PrefixItems { get; init; }
        = System.Array.Empty<XQueryExpression>();

    /// <summary>
    /// Grounded operands appearing AFTER the streamable path. Each is evaluated as a
    /// separate for-each iteration after the streaming pass completes.
    /// </summary>
    public IReadOnlyList<XQueryExpression> SuffixItems { get; init; }
        = System.Array.Empty<XQueryExpression>();

    /// <summary>
    /// True when the streamable path's last step is <c>text()</c>. When set, the
    /// processor materializes the element matched by the earlier steps and dispatches
    /// the body once per text-node child (in document order) with the text node as the
    /// context item, instead of dispatching once per element snapshot.
    /// </summary>
    public bool TextNodeTail { get; init; }

    /// <summary>
    /// When set, the matched element's named attribute is pushed as the context item
    /// for the body (rather than the element itself). Maps to xsl:for-each select
    /// expressions ending in /@attrname.
    /// </summary>
    public string? AttributeName { get; init; }

    /// <summary>
    /// When non-empty, predicates to evaluate against the matched element's snapshot.
    /// Body only dispatches if ALL predicates evaluate to true.
    /// </summary>
    public IReadOnlyList<XQueryExpression> Predicates { get; init; }
        = System.Array.Empty<XQueryExpression>();
}
