using PhoenixmlDb.Core;
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
    /// XPath <c>for</c> expression streaming (sx-ForExpr): when set, this subscription
    /// derives from <c>for $x in CONSUMING-PATH return EXPR</c> in an
    /// <c>xsl:value-of</c>/<c>xsl:sequence</c> select. The range variable bound per
    /// matched item; <see cref="PerItemSelect"/> carries the <c>return</c> expression.
    /// The matched striding path (the <c>in</c> operand minus its trailing grounding
    /// step) selects items; for each, the materialized snapshot is pushed as the context
    /// item, <see cref="RangeBindExpression"/> is evaluated against it to produce the
    /// range variable's value, the variable is bound, and the <c>return</c> expression is
    /// evaluated and emitted. <c>null</c> for a for-each / SM-ctx subscription.
    /// </summary>
    public QName? RangeVariable { get; init; }

    /// <summary>
    /// The trailing grounding step of a ForExpr <c>in</c> operand
    /// (<c>string()</c>/<c>data()</c>/<c>copy-of()</c>/<c>snapshot()</c>), evaluated
    /// against the matched element snapshot to produce the value bound to
    /// <see cref="RangeVariable"/>. Set iff <see cref="RangeVariable"/> is set.
    /// </summary>
    public XQueryExpression? RangeBindExpression { get; init; }

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
    /// Forward-countable-positional (<c>employee[1]</c>) / motionless
    /// (<c>department[@name='sales']</c>) predicates on intermediate (non-leaf)
    /// child-axis element steps of the streamable path; evaluated against the matched
    /// node's ancestor (located by <see cref="StreamingExpressionScanner.IntermediatePredicate.AncestorOffset"/>)
    /// before the body is dispatched. The body only dispatches if EVERY intermediate
    /// predicate passes. Mirrors <see cref="StreamWatcher.IntermediatePredicates"/>;
    /// the scanner only emits forward-countable-positional or motionless predicates
    /// (anything else rejects the whole path → buffered fallback, never silently dropped).
    /// </summary>
    public IReadOnlyList<StreamingExpressionScanner.IntermediatePredicate> IntermediatePredicates { get; init; }
        = System.Array.Empty<StreamingExpressionScanner.IntermediatePredicate>();

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
    /// B2 — a GROUNDED (non-streaming) start/length/remove-index expression for the
    /// window, used when the positional argument is a variable or other non-literal
    /// that is still forward-decidable because it does not navigate the input
    /// (e.g. <c>subsequence(path, $three)</c>, <c>$three</c> a grounded param). The
    /// processor evaluates these ONCE against the live XSLT scope when the subscription
    /// is activated and folds the result into the effective
    /// <see cref="SubsequenceStart"/> / <see cref="SubsequenceLength"/> /
    /// <see cref="RemoveIndex"/>. Null when the corresponding argument was a compile-time
    /// integer literal (already folded into the int fields) or absent. The scanner only
    /// sets these for arguments that provably do NOT navigate the input; a stream-derived
    /// window bound stays conservative (no subscription → buffer fallback).
    /// </summary>
    public XQueryExpression? SubsequenceStartExpression { get; init; }

    /// <summary>Grounded length expression; see <see cref="SubsequenceStartExpression"/>.</summary>
    public XQueryExpression? SubsequenceLengthExpression { get; init; }

    /// <summary>Grounded remove-index expression; see <see cref="SubsequenceStartExpression"/>.</summary>
    public XQueryExpression? RemoveIndexExpression { get; init; }

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

    /// <summary>
    /// Non-consuming dispatch (Group B): the matched node's body inspects
    /// ancestors/ancestor-or-self/parent/self/attributes only (plus atomize and
    /// set-ops over those) — it never descends into the matched node's subtree.
    /// The processor dispatches the body per match against an ancestor-synthesized
    /// snapshot WITHOUT materializing/skipping the subtree, so the forward pass
    /// continues into descendants where deeper <c>//X</c> matches still fire.
    /// Registered ONLY when the body is provably inspection-only
    /// (<c>BodyConsumptionDetector.Consumes == false</c>) — the soundness guard.
    /// </summary>
    public bool IsInspectionOnly { get; init; }

    /// <summary>
    /// From <c>outermost(...)</c>: a match is skipped when an ancestor on the live
    /// stack also matches the subscription's pattern (decided immediately from the
    /// ancestor stack — no buffering). For a bare descendant path (<c>//X</c>) this
    /// is false and every match dispatches.
    /// </summary>
    public bool Outermost { get; init; }

    /// <summary>
    /// Consuming <c>outermost(//X)</c> dispatch: the for-each select is
    /// <c>outermost(//X)</c> over a descendant-axis path but the body is NOT
    /// inspection-only — it atomizes the bare context item <c>.</c> (needs the
    /// matched leaf's own text value), so the empty-children inspection snapshot
    /// would be wrong. Because <c>outermost</c> matches never nest, materialize-and-skip
    /// is sound: the consuming dispatch materializes the matched subtree (capturing its
    /// text), synthesizes the ancestor chain (so <c>../@CAT</c> / <c>ancestor::</c>
    /// resolve), and executes the body against that snapshot. Registered by
    /// <c>TryRegisterInspectionForEach</c> when the inspection-only gate fails but the
    /// select is <c>outermost</c>; the consuming dispatch block honors the
    /// <see cref="Outermost"/> ancestor-dedup for these (unlike plain child-axis
    /// consuming subscriptions, which never nest by construction).
    /// <para>
    /// This carries a descendant-axis <see cref="StreamPathMatcher"/> (<c>**</c>),
    /// which the child-axis-only <c>TryBuildPathMatcher</c> gate rejects — hence it is
    /// registered directly rather than via the ordinary consuming path.
    /// </para>
    /// </summary>
    public bool ConsumingOutermost { get; init; }
}
