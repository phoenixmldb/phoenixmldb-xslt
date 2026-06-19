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
    /// <summary>
    /// The original <c>xsl:for-each</c> AST node this subscription was derived from,
    /// or <c>null</c> for an SM-ctx subscription (a consuming simple-map
    /// <c>LEFT ! RIGHT</c> in an <c>xsl:value-of</c>/<c>xsl:sequence</c> select, which
    /// has no for-each instruction — see <see cref="PerItemSelect"/>).
    /// </summary>
    public Ast.XsltForEach? SourceInstruction { get; init; }

    /// <summary>Path matcher fired against the input stream to identify match events.</summary>
    public required StreamPathMatcher PathMatcher { get; init; }

    /// <summary>
    /// Sequence constructor body executed once per match. Always set for a
    /// for-each-derived subscription; <c>null</c> for an SM-ctx subscription, which
    /// evaluates <see cref="PerItemSelect"/> instead. A subscription has exactly one
    /// of <see cref="Body"/> or <see cref="PerItemSelect"/> populated.
    /// </summary>
    public Ast.XsltSequenceConstructor? Body { get; init; }

    /// <summary>
    /// The simple-map RIGHT expression to evaluate per matched item (SM-ctx, OP-bucket
    /// phase 1). When set, the per-match dispatch materializes the matched item, binds
    /// it as the context item with <c>_isStreamingExecution=false</c>, evaluates this
    /// expression in-memory against that one item (mirroring
    /// <c>ApplySimpleMapTailAsync</c>), and emits the result via the
    /// <c>xsl:sequence</c> emission path — instead of executing <see cref="Body"/>.
    /// A subscription has exactly one of <see cref="Body"/> or this populated.
    /// </summary>
    public XQueryExpression? PerItemSelect { get; init; }

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

    /// <summary>
    /// 1-based start index of a <c>subsequence(path, start [, length])</c> wrapper
    /// around the streamable path (e.g. <c>subsequence(account/transaction, 1, 4)</c>).
    /// 1 (the default) means "no leading skip". The dispatcher only iterates matched
    /// items whose path-match position is in <c>[Start, Start + Length)</c>.
    /// </summary>
    public int SubsequenceStart { get; init; } = 1;

    /// <summary>
    /// Length of the <c>subsequence()</c> slice, or <c>null</c> for "to the end".
    /// Paired with <see cref="SubsequenceStart"/>.
    /// </summary>
    public int? SubsequenceLength { get; init; }

    /// <summary>
    /// When set, the dispatcher skips the single matched item whose 1-based
    /// path-match position equals <c>RemoveIndex</c> (from <c>remove(path, N)</c>).
    /// Composes with the subsequence window.
    /// </summary>
    public int? RemoveIndex { get; init; }

    /// <summary>
    /// True when the for-each is lexically nested inside output construction
    /// (an LRE, xsl:element, xsl:copy, xsl:if, xsl:choose) rather than being a
    /// bare top-of-body instruction. An inline-driven for-each must run inside
    /// linear body execution and hand off to the live reader at its lexical
    /// position so the surrounding construction is emitted around its output
    /// (otherwise the wrapper is dropped). A bare for-each (this flag false)
    /// uses the forward-pass subscription-dispatch path unchanged.
    /// </summary>
    public bool InlineDriven { get; init; }
}
