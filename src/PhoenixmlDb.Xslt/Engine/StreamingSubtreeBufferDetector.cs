using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Detects whether a template body needs the matched element's full subtree
/// materialized before execution. Triggered by snapshot()/copy-of() over the
/// context node or descendants — operations that demand a buffered XdmElement
/// and therefore cannot run against the streaming reader directly.
/// </summary>
/// <remarks>
/// Narrowed: only triggers when the snapshot()/copy-of() argument touches the
/// matched subtree — bare <c>.</c>, or a relative path whose first step uses
/// a downward axis (child / descendant / descendant-or-self / attribute / self).
/// Absolute paths (<c>/foo</c>), <c>doc()</c> calls, and external variable
/// references don't need buffering — they read from in-memory trees the
/// streaming pass never touches. <see cref="XsltCopyOf"/> with a context-only
/// or downward select still triggers buffering; absolute selects do not.
/// </remarks>
internal static class StreamingSubtreeBufferDetector
{
    // Compile-time-stable AST nodes — same body reference recurs across every
    // element match. Cache the scan so we pay it once per body, not per element.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<XsltSequenceConstructor, object> _cache = new();
    private static readonly object _trueBox = true;
    private static readonly object _falseBox = false;

    public static bool RequiresSubtreeBuffer(XsltSequenceConstructor? body)
    {
        if (body == null) return false;
        if (_cache.TryGetValue(body, out var cached))
            return ReferenceEquals(cached, _trueBox);

        var result = false;
        foreach (var insn in body.Instructions)
        {
            if (InstructionRequiresBuffer(insn)) { result = true; break; }
        }
        _cache.Add(body, result ? _trueBox : _falseBox);
        return result;
    }

    private static bool InstructionRequiresBuffer(XsltInstruction insn)
    {
        switch (insn)
        {
            case XsltCopyOf cof:
                return TouchesMatchedSubtree(cof.Select);

            case XsltVariableInstruction v:
                if (v.Select != null && ExpressionUsesSnapshot(v.Select)) return true;
                if (v.Content != null && RequiresSubtreeBuffer(v.Content)) return true;
                return false;

            case XsltValueOf vo:
                return vo.Select != null && ExpressionUsesSnapshot(vo.Select);

            case XsltSequence s:
                if (s.Select != null && ExpressionUsesSnapshot(s.Select)) return true;
                if (s.Content != null && RequiresSubtreeBuffer(s.Content)) return true;
                return false;

            case XsltIf i:
                if (ExpressionUsesSnapshot(i.Test)) return true;
                if (i.Then != null && RequiresSubtreeBuffer(i.Then)) return true;
                return false;

            case XsltChoose c:
                foreach (var w in c.When)
                {
                    if (ExpressionUsesSnapshot(w.Test)) return true;
                    if (RequiresSubtreeBuffer(w.Body)) return true;
                }
                if (c.Otherwise != null && RequiresSubtreeBuffer(c.Otherwise)) return true;
                return false;

            case XsltForEach fe:
                if (ExpressionUsesSnapshot(fe.Select)) return true;
                return RequiresSubtreeBuffer(fe.Body);

            case XsltForEachGroup feg:
                // group-starting-with / group-ending-with / group-adjacent have a
                // streaming dispatch in ForEachGroupStreamingAsync. group-by does
                // not, and falls through to the non-streaming branch which needs
                // the matched subtree materialized. Request a subtree buffer when
                // group-by is in play and the select expression navigates the
                // matched subtree (bare ., relative downward path, or copy-of()).
                if (feg.GroupBy != null && TouchesMatchedSubtree(feg.Select)) return true;
                if (ExpressionUsesSnapshot(feg.Select)) return true;
                return RequiresSubtreeBuffer(feg.Body);

            case XsltLiteralResultElement lre:
                return RequiresSubtreeBuffer(lre.Content);

            case XsltCopy cp:
                return cp.Content != null && RequiresSubtreeBuffer(cp.Content);

            case XsltFork fk:
                // xsl:fork runs each prong as a separate consumer over a single forward
                // pass of the input. The engine executes prongs sequentially (ForkAsync),
                // so a prong that re-traverses the matched subtree (apply-templates /
                // xsl:iterate / xsl:for-each over the context's children) cannot replay
                // the live reader — buffer the subtree so every prong runs against it.
                foreach (var seq in fk.Sequences)
                {
                    if (RequiresSubtreeBuffer(seq)) return true;
                    if (ConsumesMatchedSubtree(seq)) return true;
                }
                foreach (var feg in fk.ForEachGroups)
                {
                    // A for-each-group prong is itself a consuming construct: run the
                    // same group-by/select analysis applied to a standalone
                    // xsl:for-each-group (InstructionRequiresBuffer), not just its body.
                    // group-by over the matched subtree falls through to the
                    // non-streaming branch in ForEachGroupAsync, which needs the
                    // subtree materialized.
                    if (InstructionRequiresBuffer(feg)) return true;
                }
                foreach (var rd in fk.ResultDocuments)
                    if (rd.Content != null && RequiresSubtreeBuffer(rd.Content)) return true;
                return false;

            case XsltMap m:
                return m.Content != null && RequiresSubtreeBuffer(m.Content);

            case XsltMapEntry me:
                if (me.Select != null && ExpressionUsesSnapshot(me.Select)) return true;
                return me.Content != null && RequiresSubtreeBuffer(me.Content);

            case XsltWherePopulated wp:
                return RequiresSubtreeBuffer(wp.Content);
            case XsltOnEmpty oe:
                return (oe.Content != null && RequiresSubtreeBuffer(oe.Content))
                       || (oe.Select != null && ExpressionUsesSnapshot(oe.Select));
            case XsltOnNonEmpty one:
                return (one.Content != null && RequiresSubtreeBuffer(one.Content))
                       || (one.Select != null && ExpressionUsesSnapshot(one.Select));

            case XsltSequenceConstructor ctor:
                return RequiresSubtreeBuffer(ctor);

            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="body"/> (the content of an <c>xsl:source-document</c>
    /// or a <c>match="/"</c> template) contains a construct that cannot be driven
    /// directly off the streaming reader at the document level and therefore needs the
    /// whole input materialized first. Currently: an <c>xsl:fork</c> (or bare)
    /// <c>xsl:for-each-group</c> using <c>group-by</c> — <see cref="XsltTransformer"/>'s
    /// <c>ForEachGroupAsync</c> only streams group-starting-with / group-ending-with /
    /// group-adjacent; group-by falls through to a buffered evaluation that reads from
    /// the (empty) synthetic document node when run against the live reader.
    /// </summary>
    public static bool RequiresWholeInputBuffer(XsltSequenceConstructor? body)
    {
        if (body == null) return false;
        foreach (var insn in body.Instructions)
            if (InstructionRequiresWholeInput(insn)) return true;
        return false;
    }

    private static bool InstructionRequiresWholeInput(XsltInstruction insn)
    {
        switch (insn)
        {
            case XsltForEachGroup feg:
                return feg.GroupBy != null;

            // A top-level xsl:iterate appearing directly in a streamable
            // source-document body (or a match="/" document-node template) has no
            // document-level streaming dispatch: the engine's streaming xsl:iterate
            // path only runs when _isStreamingExecution is already set (i.e. inside a
            // template the streaming processor dispatched). At the document level the
            // body executes against the synthetic empty document node, so the iterate's
            // consuming select (account/transaction, /*/transaction, outermost(.//x),
            // descendant::x, …) evaluates to the empty sequence and the iterate body
            // never runs — only xsl:on-completion fires, with the initial param values.
            // Materialize the whole input and run the buffered xsl:iterate, which
            // evaluates the select against the real document and produces correct output
            // (xsl:break / xsl:next-iteration / xsl:on-completion all work on the
            // buffered sequence). Only a grounded select (literal / variable, no input
            // navigation) can stay on the streaming path.
            case XsltIterate it:
                return IterateConsumesInput(it.Select);

            // A top-level xsl:value-of / xsl:copy-of / xsl:sequence whose select calls an
            // absorbing higher-order function (fold-left, fold-right) over a sequence that
            // navigates the input has no document-level streaming dispatch: the streaming
            // pass evaluates the select against the synthetic empty document node, so the
            // folded sequence is empty and only the $zero seed survives (sf-fold-left-011/013),
            // the computed-value forms collapse to nothing (sf-fold-left-015), or the eager
            // fold over an unread streaming context produces wrong/typed-error results
            // (sf-fold-left-012/018/019/022/023) or loops on large input (sf-fold-left-016/020/021).
            // Materialize the whole input and run the buffered evaluation, which atomizes and
            // folds the real node sequence correctly. A grounded fold argument (literal /
            // variable, no input navigation) stays on the streaming path.
            case XsltValueOf vo:
                return vo.Select != null && SelectFoldsOverInput(vo.Select);

            case XsltCopyOf cof:
                return SelectFoldsOverInput(cof.Select);

            case XsltSequence sq:
                return sq.Select != null && SelectFoldsOverInput(sq.Select);

            case XsltFork fk:
                foreach (var feg in fk.ForEachGroups)
                    if (feg.GroupBy != null) return true;
                foreach (var seq in fk.Sequences)
                    if (RequiresWholeInputBuffer(seq)) return true;
                foreach (var rd in fk.ResultDocuments)
                    if (rd.Content != null && RequiresWholeInputBuffer(rd.Content)) return true;
                return false;

            case XsltResultDocument rd2:
                return rd2.Content != null && RequiresWholeInputBuffer(rd2.Content);

            case XsltLiteralResultElement lre:
                return RequiresWholeInputBuffer(lre.Content);

            case XsltCopy cp:
                return cp.Content != null && RequiresWholeInputBuffer(cp.Content);

            case XsltSequenceConstructor ctor:
                return RequiresWholeInputBuffer(ctor);

            default:
                return false;
        }
    }

    /// <summary>
    /// True when an <c>xsl:iterate</c> select navigates the input document (a path,
    /// or a function call wrapping a path such as <c>outermost(.//x)</c>) and therefore
    /// cannot be satisfied by the document-level streaming pass. A grounded select
    /// (literal sequence, a variable reference, or empty) iterates an in-memory value
    /// and does not need the input buffered; a null select defaults to the children of
    /// the context node, which at the document level is the (empty) synthetic node — so
    /// it too needs the real input.
    /// </summary>
    private static bool IterateConsumesInput(XQueryExpression? select)
    {
        if (select == null) return true; // default = children of context (the input root)
        return NavigatesInput(select);
    }

    /// <summary>
    /// True when <paramref name="expr"/> reads the streamed input document directly —
    /// i.e. it contains a path / axis step or a bare context-item reference somewhere in
    /// its tree. An iterate select built purely from grounded operands (literals, ranges,
    /// variable references, and functions/operators over those — e.g. <c>tail($words)</c>
    /// or <c>1 to 200</c>) iterates an already-materialized value and stays on the
    /// streaming path. A select that navigates the input (<c>account/transaction</c>,
    /// <c>/*/transaction</c>, <c>outermost(.//gml:posList)</c>, <c>descendant::x</c>) cannot
    /// be satisfied at the document level and forces whole-input buffering.
    /// </summary>
    private static bool NavigatesInput(XQueryExpression expr)
    {
        switch (expr)
        {
            case ContextItemExpression:
            case PathExpression:
            case StepExpression:
                return true;

            case IntegerLiteral or DecimalLiteral or DoubleLiteral or StringLiteral or BooleanLiteral:
            case VariableReference:
                return false;

            case RangeExpression range:
                return NavigatesInput(range.Start) || NavigatesInput(range.End);

            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    if (NavigatesInput(item)) return true;
                return false;

            case BinaryExpression bin:
                return NavigatesInput(bin.Left) || NavigatesInput(bin.Right);

            case UnaryExpression un:
                return NavigatesInput(un.Operand);

            case FunctionCallExpression fc:
                foreach (var arg in fc.Arguments)
                    if (NavigatesInput(arg)) return true;
                return false;

            case SimpleMapExpression sm:
                return NavigatesInput(sm.Left) || NavigatesInput(sm.Right);

            // Conservative default: an expression shape we don't model might navigate
            // the input, so buffer rather than risk an empty-context evaluation.
            default:
                return true;
        }
    }

    /// <summary>
    /// True when <paramref name="expr"/> contains a call to an absorbing higher-order
    /// function (<c>fold-left</c> / <c>fold-right</c>) anywhere in its tree whose folded
    /// sequence (the first argument) navigates the input. Such a call cannot be satisfied
    /// by the document-level streaming pass — it has no streaming dispatch and would fold
    /// over the empty synthetic document node — so it forces whole-input buffering. The
    /// fold may be nested inside wrapping functions (<c>round(fold-left(…))</c>,
    /// <c>format-number(fold-left(…), …)</c>, <c>xs:integer(round(fold-left(…)))</c>), so
    /// the whole select tree is walked. A fold over a grounded sequence (literals,
    /// variables, ranges) stays on the streaming path.
    /// </summary>
    private static bool SelectFoldsOverInput(XQueryExpression expr)
    {
        switch (expr)
        {
            case FunctionCallExpression fc:
                if (IsAbsorbingFold(fc) && fc.Arguments.Count > 0 && NavigatesInput(fc.Arguments[0]))
                    return true;
                foreach (var arg in fc.Arguments)
                    if (SelectFoldsOverInput(arg)) return true;
                return false;

            case BinaryExpression bin:
                return SelectFoldsOverInput(bin.Left) || SelectFoldsOverInput(bin.Right);

            case UnaryExpression un:
                return SelectFoldsOverInput(un.Operand);

            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    if (SelectFoldsOverInput(item)) return true;
                return false;

            case SimpleMapExpression sm:
                return SelectFoldsOverInput(sm.Left) || SelectFoldsOverInput(sm.Right);

            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="fc"/> is the <c>fn:fold-left</c> or <c>fn:fold-right</c>
    /// higher-order function. These have streamability <c>absorbing</c> on their sequence
    /// argument: they consume the whole sequence and cannot be driven off the live reader
    /// at the document level.
    /// </summary>
    private static bool IsAbsorbingFold(FunctionCallExpression fc)
    {
        return fc.Name.LocalName is "fold-left" or "fold-right"
            && (fc.Name.Namespace == NamespaceId.None
                || fc.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn);
    }

    /// <summary>
    /// True when <paramref name="body"/> contains an instruction that consumes the
    /// matched subtree's children — xsl:apply-templates / xsl:iterate / xsl:for-each
    /// with no select (defaults to the children) or a relative downward select.
    /// Used by the xsl:fork analysis: prongs that re-traverse the input cannot share a
    /// single live reader under the engine's sequential fork execution.
    /// </summary>
    private static bool ConsumesMatchedSubtree(XsltSequenceConstructor? body)
    {
        if (body == null) return false;
        foreach (var insn in body.Instructions)
        {
            switch (insn)
            {
                case XsltApplyTemplates at:
                    if (at.Select == null || TouchesMatchedSubtree(at.Select)) return true;
                    break;
                case XsltIterate it:
                    if (TouchesMatchedSubtree(it.Select)) return true;
                    break;
                case XsltForEach fe:
                    if (TouchesMatchedSubtree(fe.Select)) return true;
                    if (ConsumesMatchedSubtree(fe.Body)) return true;
                    break;
                case XsltLiteralResultElement lre:
                    if (ConsumesMatchedSubtree(lre.Content)) return true;
                    break;
                case XsltCopy cp:
                    if (ConsumesMatchedSubtree(cp.Content)) return true;
                    break;
                case XsltIf i:
                    if (ConsumesMatchedSubtree(i.Then)) return true;
                    break;
                case XsltChoose c:
                    foreach (var w in c.When)
                        if (ConsumesMatchedSubtree(w.Body)) return true;
                    if (ConsumesMatchedSubtree(c.Otherwise)) return true;
                    break;
                case XsltSequence s:
                    // xsl:sequence with content (the shape xsl:fork prongs take) — descend.
                    // A select expression that consumes children also counts.
                    if (s.Select != null && TouchesMatchedSubtree(s.Select)) return true;
                    if (ConsumesMatchedSubtree(s.Content)) return true;
                    break;
                case XsltSequenceConstructor ctor:
                    if (ConsumesMatchedSubtree(ctor)) return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="body"/> passes the matched subtree (bare <c>.</c>,
    /// <c>*</c>, or another relative downward path) into a user-declared stylesheet
    /// function whose streamability is <c>absorbing</c> (it consumes its node-set
    /// argument). The engine evaluates such a function call eagerly and would hand it
    /// an unread streaming context, so the subtree must be materialized first.
    /// Mirrors the shapes in si-fork-808 (<c>mf:nest(*, 1)</c> inside xsl:sequence).
    /// </summary>
    public static bool RequiresSubtreeBufferForAbsorbingFunctions(
        XsltSequenceConstructor? body,
        IReadOnlyDictionary<(QName Name, int Arity), XsltFunction> functions)
    {
        if (body == null) return false;
        foreach (var insn in body.Instructions)
            if (InstructionCallsAbsorbingFunction(insn, functions)) return true;
        return false;
    }

    private static bool InstructionCallsAbsorbingFunction(
        XsltInstruction insn,
        IReadOnlyDictionary<(QName, int), XsltFunction> functions)
    {
        switch (insn)
        {
            case XsltSequence s:
                if (s.Select != null && ExprCallsAbsorbingFunction(s.Select, functions)) return true;
                return s.Content != null && RequiresSubtreeBufferForAbsorbingFunctions(s.Content, functions);
            case XsltValueOf vo:
                return vo.Select != null && ExprCallsAbsorbingFunction(vo.Select, functions);
            case XsltVariableInstruction v:
                if (v.Select != null && ExprCallsAbsorbingFunction(v.Select, functions)) return true;
                return v.Content != null && RequiresSubtreeBufferForAbsorbingFunctions(v.Content, functions);
            case XsltCopy cp:
                return cp.Content != null && RequiresSubtreeBufferForAbsorbingFunctions(cp.Content, functions);
            case XsltLiteralResultElement lre:
                return RequiresSubtreeBufferForAbsorbingFunctions(lre.Content, functions);
            case XsltForEach fe:
                if (ExprCallsAbsorbingFunction(fe.Select, functions)) return true;
                return RequiresSubtreeBufferForAbsorbingFunctions(fe.Body, functions);
            case XsltIf i:
                return RequiresSubtreeBufferForAbsorbingFunctions(i.Then, functions);
            case XsltChoose c:
                foreach (var w in c.When)
                    if (RequiresSubtreeBufferForAbsorbingFunctions(w.Body, functions)) return true;
                return RequiresSubtreeBufferForAbsorbingFunctions(c.Otherwise, functions);
            case XsltSequenceConstructor ctor:
                return RequiresSubtreeBufferForAbsorbingFunctions(ctor, functions);
            default:
                return false;
        }
    }

    private static bool ExprCallsAbsorbingFunction(
        XQueryExpression expr,
        IReadOnlyDictionary<(QName, int), XsltFunction> functions)
    {
        switch (expr)
        {
            case FunctionCallExpression fc:
                if (functions.TryGetValue((fc.Name, fc.Arguments.Count), out var fn)
                    && fn.Streamability == "absorbing")
                {
                    foreach (var arg in fc.Arguments)
                        if (TouchesMatchedSubtree(arg)) return true;
                }
                // Even if this call isn't absorbing, an argument might itself be one.
                foreach (var arg in fc.Arguments)
                    if (ExprCallsAbsorbingFunction(arg, functions)) return true;
                return false;
            case BinaryExpression bin:
                return ExprCallsAbsorbingFunction(bin.Left, functions) || ExprCallsAbsorbingFunction(bin.Right, functions);
            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    if (ExprCallsAbsorbingFunction(item, functions)) return true;
                return false;
            default:
                return false;
        }
    }

    private static bool ExpressionUsesSnapshot(XQueryExpression expr)
    {
        switch (expr)
        {
            case FunctionCallExpression fc when IsSnapshotOrCopyOf(fc):
                return fc.Arguments.Count > 0 && TouchesMatchedSubtree(fc.Arguments[0]);

            case FunctionCallExpression fc:
                foreach (var arg in fc.Arguments)
                    if (ExpressionUsesSnapshot(arg)) return true;
                return false;

            case BinaryExpression bin:
                return ExpressionUsesSnapshot(bin.Left) || ExpressionUsesSnapshot(bin.Right);

            case UnaryExpression un:
                return ExpressionUsesSnapshot(un.Operand);

            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    if (ExpressionUsesSnapshot(item)) return true;
                return false;

            case PathExpression path:
                foreach (var step in path.Steps)
                    foreach (var pred in step.Predicates)
                        if (ExpressionUsesSnapshot(pred)) return true;
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="expr"/> evaluates relative to the matched
    /// element (the streaming context node) — the only case the streaming
    /// pass cannot satisfy without buffering the subtree. Bare <c>.</c>,
    /// relative paths whose first step uses a downward axis, and parenthesized
    /// versions of these all qualify. Absolute paths, <c>doc()</c>/<c>doc-available()</c>,
    /// and external variable refs read from already-materialized trees and
    /// don't need buffering.
    /// </summary>
    private static bool TouchesMatchedSubtree(XQueryExpression expr)
    {
        switch (expr)
        {
            case ContextItemExpression:
                return true;

            case PathExpression path:
                if (path.IsAbsolute) return false;
                if (path.Steps.Count == 0) return false;
                var firstAxis = path.Steps[0].Axis;
                return firstAxis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf
                    or Axis.Attribute or Axis.Self;

            // snapshot()/copy-of() of an already-grounded subtree (rare nesting)
            case FunctionCallExpression fc when IsSnapshotOrCopyOf(fc):
                return fc.Arguments.Count > 0 && TouchesMatchedSubtree(fc.Arguments[0]);

            // a ! b (or a / b parsed as SimpleMap) — the streaming context flows
            // through the left-hand operand, so the whole expression touches the
            // matched subtree iff the left side does. e.g. item!copy-of(),
            // item!(name) — common shapes inside xsl:for-each-group select.
            case SimpleMapExpression sm:
                return TouchesMatchedSubtree(sm.Left);

            // (expr) — parenthesised forms wrap into a single-item sequence.
            case SequenceExpression seq when seq.Items.Count == 1:
                return TouchesMatchedSubtree(seq.Items[0]);

            default:
                return false;
        }
    }

    private static bool IsSnapshotOrCopyOf(FunctionCallExpression fc)
    {
        return fc.Name.LocalName is "snapshot" or "copy-of"
            && (fc.Name.Namespace == NamespaceId.None
                || fc.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn);
    }
}
