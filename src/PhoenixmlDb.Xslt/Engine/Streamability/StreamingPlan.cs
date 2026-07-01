using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine.Streamability;

/// <summary>
/// The single buffering decision for an XSLT instruction under streaming, derived from the
/// §19.8 posture/sweep classification. This one decision is intended to SUPERSEDE the two
/// divergent legacy detectors (<see cref="StreamingSubtreeBufferDetector.RequiresSubtreeBuffer"/>
/// and <see cref="StreamingSubtreeBufferDetector.RequiresWholeInputBuffer"/>) — one
/// classification driving one plan, with no in-template / document-level divergence.
/// </summary>
public enum StreamingPlan
{
    /// <summary>
    /// Run the instruction directly against the live streaming reader — nothing needs to be
    /// materialised. Only ever chosen for a <see cref="PostureSweep.IsGuaranteedStreamable"/>
    /// construct (§19.8.6). Streaming a non-streamable construct would produce the wrong
    /// output, so this plan is NEVER returned unless streamability is guaranteed.
    /// </summary>
    StreamInline,

    /// <summary>
    /// Materialise the MATCHED element's subtree (the current streaming context node) and run
    /// the instruction against that buffered tree. Chosen for guaranteed-streamable constructs
    /// that nonetheless need the matched subtree whole — a <c>snapshot(.)</c> / <c>copy-of(.)</c>
    /// of the context item, or a body that re-traverses the matched node's descendants.
    /// </summary>
    BufferMatchedSubtree,

    /// <summary>
    /// Materialise the WHOLE input document and run the instruction non-streaming. The safety
    /// net for a construct that is NOT guaranteed-streamable but still navigates the streamed
    /// input (a roaming/free-ranging select, group-by, a crawling for-each select, an
    /// input-navigating operand in an absorbing position, …). Correct output, just not streamed.
    /// </summary>
    BufferWholeInput,

    /// <summary>
    /// The instruction is not a streaming construct and does not navigate the streamed input —
    /// purely grounded work. Nothing to buffer and nothing to stream.
    /// </summary>
    NotStreaming,
}

/// <summary>
/// Derives a single <see cref="StreamingPlan"/> for an XSLT instruction from its §19.8
/// posture/sweep classification (via <see cref="StreamabilityClassifier"/>), replacing the two
/// legacy, independently-maintained buffer detectors with one posture-driven decision.
/// </summary>
/// <remarks>
/// <para>
/// This is a Task 1.1 <b>shadow</b> component: it is additive and wired into nothing (Task 1.2
/// does the runtime wiring). The legacy detectors remain intact as the coverage contract.
/// </para>
/// <para>
/// The overriding correctness invariant: <see cref="StreamingPlan.StreamInline"/> is only ever
/// returned for a <see cref="PostureSweep.IsGuaranteedStreamable"/> instruction. Under-buffering
/// is a correctness bug (wrong output); over-buffering is only a performance loss, so when in
/// doubt the safe buffer plan is preferred.
/// </para>
/// </remarks>
public static class StreamingPlanner
{
    /// <summary>
    /// Computes the single buffering decision for <paramref name="insn"/> in the streaming
    /// environment <paramref name="ctx"/>, per the §19.8 derivation rules.
    /// </summary>
    /// <param name="insn">The XSLT instruction to plan.</param>
    /// <param name="ctx">The streaming context (context-item posture and streamed-scope flag).</param>
    /// <returns>The single buffering plan that supersedes both legacy detectors.</returns>
    public static StreamingPlan Plan(XsltInstruction insn, StreamingContext ctx)
    {
        ArgumentNullException.ThrowIfNull(insn);

        var ps = StreamabilityClassifier.Classify(insn, ctx);

        if (ps.IsGuaranteedStreamable)
        {
            // Rule 1 & 2: guaranteed-streamable. Stream inline UNLESS the instruction needs the
            // matched subtree materialised (snapshot(.)/copy-of(.) of the context item, or a
            // body that re-traverses the matched subtree). A grounded+motionless construct that
            // reads no input trivially streams inline; this is subsumed by the same test (it
            // touches no subtree).
            return NeedsMatchedSubtree(insn)
                ? StreamingPlan.BufferMatchedSubtree
                : StreamingPlan.StreamInline;
        }

        // Rule 3 & 4: NOT guaranteed-streamable. If it navigates the streamed input, materialise
        // the whole input and run non-streaming (the safety net — correct output). Otherwise it
        // is purely grounded work that simply isn't a streaming construct.
        return InstructionNavigatesStreamedInput(insn, ctx)
            ? StreamingPlan.BufferWholeInput
            : StreamingPlan.NotStreaming;
    }

    // -----------------------------------------------------------------------
    // Matched-subtree detection (Rule 2).
    //
    // Reuses the snapshot/copy-of-of-context concept proven in the legacy
    // StreamingSubtreeBufferDetector, but is only consulted AFTER the posture
    // classifier has already declared the instruction guaranteed-streamable —
    // so it distinguishes "streams inline" from "streams but needs the subtree
    // buffered", never "is streamable at all".
    // -----------------------------------------------------------------------

    /// <summary>
    /// True when a guaranteed-streamable instruction needs the matched element's subtree
    /// materialised: it applies <c>snapshot(.)</c> / <c>copy-of(.)</c> of the context item, or
    /// re-traverses the matched subtree (an inner <c>xsl:copy-of</c> / <c>xsl:for-each</c> /
    /// <c>xsl:iterate</c> / <c>xsl:apply-templates</c> that reads the context node's children /
    /// descendants after the node is needed whole).
    /// </summary>
    private static bool NeedsMatchedSubtree(XsltInstruction insn)
    {
        switch (insn)
        {
            // xsl:copy-of select=. / select=descendant → the matched subtree is deep-copied.
            case XsltCopyOf cof:
                return TouchesMatchedSubtree(cof.Select);

            // snapshot()/copy-of() of the context in a transmitting/absorbing select.
            case XsltVariableInstruction v:
                return (v.Select is not null && ExpressionUsesContextSnapshot(v.Select))
                    || NeedsMatchedSubtreeBody(v.Content);
            case XsltValueOf vo:
                return vo.Select is not null && ExpressionUsesContextSnapshot(vo.Select);
            case XsltSequence s:
                return (s.Select is not null && ExpressionUsesContextSnapshot(s.Select))
                    || NeedsMatchedSubtreeBody(s.Content);

            case XsltIf i:
                return ExpressionUsesContextSnapshot(i.Test) || NeedsMatchedSubtreeBody(i.Then);
            case XsltChoose c:
                foreach (var w in c.When)
                    if (ExpressionUsesContextSnapshot(w.Test) || NeedsMatchedSubtreeBody(w.Body))
                        return true;
                return NeedsMatchedSubtreeBody(c.Otherwise);

            // xsl:for-each / xsl:iterate / xsl:for-each-group REBIND the context item to the
            // per-item node of their population select. So a snapshot(.)/copy-of(.) inside their
            // body refers to that per-item (child) node — already delivered per-item by the
            // stream — NOT the matched subtree. We therefore do NOT descend into their bodies for
            // matched-subtree detection: doing so is exactly the 013-family over-buffering bug
            // (xsl:iterate select=child::* › copy-of(.) must stream inline). Only a
            // snapshot/copy-of of the matched subtree in the POPULATION SELECT (evaluated in the
            // outer context) still forces the subtree buffer.
            case XsltForEach fe:
                return ExpressionUsesContextSnapshot(fe.Select);
            case XsltIterate it:
                return ExpressionUsesContextSnapshot(it.Select);

            case XsltForEachGroup feg:
                return (feg.GroupBy is not null && TouchesMatchedSubtree(feg.Select))
                    || ExpressionUsesContextSnapshot(feg.Select);

            case XsltLiteralResultElement lre:
                return NeedsMatchedSubtreeBody(lre.Content);
            case XsltElement el:
                return NeedsMatchedSubtreeBody(el.Content);
            case XsltCopy cp:
                return NeedsMatchedSubtreeBody(cp.Content);
            case XsltDocument doc:
                return NeedsMatchedSubtreeBody(doc.Content);

            // xsl:result-document dispatched as a matched-template body: an href/method/etc. AVT
            // may CONSUME the matched subtree (href="{count(child::*)}.xml"), which is only
            // knowable once the subtree has been read — materialise the matched element.
            case XsltResultDocument rd:
                return AvtTouchesMatchedSubtree(rd.Href)
                    || AvtTouchesMatchedSubtree(rd.Method)
                    || AvtTouchesMatchedSubtree(rd.Format)
                    || AvtTouchesMatchedSubtree(rd.Indent)
                    || AvtTouchesMatchedSubtree(rd.OmitXmlDeclaration)
                    || AvtTouchesMatchedSubtree(rd.Encoding)
                    || NeedsMatchedSubtreeBody(rd.Content);

            case XsltMap m:
                return NeedsMatchedSubtreeBody(m.Content);
            case XsltMapEntry me:
                return (me.Select is not null && ExpressionUsesContextSnapshot(me.Select))
                    || NeedsMatchedSubtreeBody(me.Content);

            case XsltWherePopulated wp:
                return NeedsMatchedSubtreeBody(wp.Content);
            case XsltOnEmpty oe:
                return (oe.Select is not null && ExpressionUsesContextSnapshot(oe.Select))
                    || NeedsMatchedSubtreeBody(oe.Content);
            case XsltOnNonEmpty one:
                return (one.Select is not null && ExpressionUsesContextSnapshot(one.Select))
                    || NeedsMatchedSubtreeBody(one.Content);

            case XsltSequenceConstructor ctor:
                return NeedsMatchedSubtreeBody(ctor);

            default:
                return false;
        }
    }

    private static bool NeedsMatchedSubtreeBody(XsltSequenceConstructor? body)
    {
        if (body is null)
            return false;
        foreach (var insn in body.Instructions)
            if (NeedsMatchedSubtree(insn))
                return true;
        return false;
    }

    /// <summary>
    /// True when <paramref name="expr"/> applies <c>snapshot(.)</c> / <c>copy-of(.)</c> (or of a
    /// downward path off the context) anywhere in its tree — the operations that demand the
    /// matched subtree be buffered. Mirrors the legacy detector's <c>ExpressionUsesSnapshot</c>.
    /// </summary>
    private static bool ExpressionUsesContextSnapshot(XQueryExpression expr)
    {
        switch (expr)
        {
            case FunctionCallExpression fc when IsSnapshotOrCopyOf(fc):
                return fc.Arguments.Count > 0 && TouchesMatchedSubtree(fc.Arguments[0]);

            case FunctionCallExpression fc:
                foreach (var arg in fc.Arguments)
                    if (ExpressionUsesContextSnapshot(arg)) return true;
                return false;

            case BinaryExpression bin:
                return ExpressionUsesContextSnapshot(bin.Left) || ExpressionUsesContextSnapshot(bin.Right);

            case UnaryExpression un:
                return ExpressionUsesContextSnapshot(un.Operand);

            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    if (ExpressionUsesContextSnapshot(item)) return true;
                return false;

            case PathExpression path:
                foreach (var step in path.Steps)
                    foreach (var pred in step.Predicates)
                        if (ExpressionUsesContextSnapshot(pred)) return true;
                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="expr"/> evaluates relative to the matched element (the
    /// streaming context node): bare <c>.</c>, a relative downward path, or parenthesised /
    /// simple-map / snapshot forms of those. Absolute paths and grounded refs do not.
    /// Mirrors the legacy detector's <c>TouchesMatchedSubtree</c>.
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

            case FunctionCallExpression fc when IsSnapshotOrCopyOf(fc):
                return fc.Arguments.Count > 0 && TouchesMatchedSubtree(fc.Arguments[0]);

            case SimpleMapExpression sm:
                return TouchesMatchedSubtree(sm.Left);

            case SequenceExpression seq when seq.Items.Count == 1:
                return TouchesMatchedSubtree(seq.Items[0]);

            default:
                return false;
        }
    }

    private static bool IsSnapshotOrCopyOf(FunctionCallExpression fc)
        => fc.Name.LocalName is "snapshot" or "copy-of"
            && (fc.Name.Namespace == PhoenixmlDb.Core.NamespaceId.None
                || fc.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn);

    /// <summary>
    /// True when an AVT (on an <c>xsl:result-document</c> attribute) has an embedded
    /// <c>{expr}</c> that navigates the matched element's children/descendants — the value is
    /// only knowable once the subtree has been read, forcing the subtree buffer. Mirrors the
    /// legacy detector's <c>AvtTouchesMatchedSubtree</c>. A start-tag-only AVT (<c>{@nr}</c>) or
    /// a literal AVT does not.
    /// </summary>
    private static bool AvtTouchesMatchedSubtree(XsltAttributeValueTemplate? avt)
    {
        if (avt is null)
            return false;
        foreach (var part in avt.Parts)
            if (part is AvtExpression ae && ExpressionNavigatesChildOrDescendant(ae.Expression))
                return true;
        return false;
    }

    /// <summary>
    /// True when <paramref name="expr"/> reads the matched element's children or descendants —
    /// a relative downward (child / descendant[-or-self]) first step or a bare context item,
    /// including inside function-call / binary / simple-map / sequence subexpressions.
    /// Attribute-axis and self-axis navigation are excluded (available at the start tag).
    /// Mirrors the legacy detector's <c>ExpressionNavigatesChildOrDescendant</c>.
    /// </summary>
    private static bool ExpressionNavigatesChildOrDescendant(XQueryExpression expr)
    {
        switch (expr)
        {
            case ContextItemExpression:
                return true;

            case PathExpression path:
                if (path.IsAbsolute) return false;
                if (path.InitialExpression is not null and not ContextItemExpression)
                    return ExpressionNavigatesChildOrDescendant(path.InitialExpression);
                if (path.Steps.Count == 0) return false;
                return path.Steps[0].Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf;

            case FunctionCallExpression fc:
                foreach (var arg in fc.Arguments)
                    if (ExpressionNavigatesChildOrDescendant(arg)) return true;
                return false;

            case BinaryExpression bin:
                return ExpressionNavigatesChildOrDescendant(bin.Left)
                    || ExpressionNavigatesChildOrDescendant(bin.Right);

            case UnaryExpression un:
                return ExpressionNavigatesChildOrDescendant(un.Operand);

            case SimpleMapExpression sm:
                return ExpressionNavigatesChildOrDescendant(sm.Left)
                    || ExpressionNavigatesChildOrDescendant(sm.Right);

            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    if (ExpressionNavigatesChildOrDescendant(item)) return true;
                return false;

            default:
                return false;
        }
    }

    // -----------------------------------------------------------------------
    // Streamed-input navigation detection (Rule 3 vs 4).
    //
    // For a NOT-guaranteed-streamable instruction we buffer the whole input iff
    // it actually reads the streamed input; otherwise it is inert grounded work.
    // Reuses the legacy detector's proven NavigatesInput expression test.
    // -----------------------------------------------------------------------

    /// <summary>
    /// True when a non-streamable instruction reads the streamed input (so materialising the
    /// whole input is required for correct output). Outside streamed scope nothing is streamed,
    /// so nothing needs buffering.
    /// </summary>
    private static bool InstructionNavigatesStreamedInput(XsltInstruction insn, StreamingContext ctx)
    {
        if (!ctx.InStreamedScope)
            return false;

        switch (insn)
        {
            case XsltValueOf vo:
                return (vo.Select is not null && StreamingSubtreeBufferDetector.NavigatesInput(vo.Select))
                    || BodyNavigatesStreamedInput(vo.Content, ctx);
            case XsltCopyOf cof:
                return StreamingSubtreeBufferDetector.NavigatesInput(cof.Select);
            case XsltSequence sq:
                return (sq.Select is not null && StreamingSubtreeBufferDetector.NavigatesInput(sq.Select))
                    || BodyNavigatesStreamedInput(sq.Content, ctx);
            case XsltVariableInstruction v:
                return (v.Select is not null && StreamingSubtreeBufferDetector.NavigatesInput(v.Select))
                    || BodyNavigatesStreamedInput(v.Content, ctx);

            case XsltForEach fe:
                return StreamingSubtreeBufferDetector.NavigatesInput(fe.Select)
                    || BodyNavigatesStreamedInput(fe.Body, ctx);
            case XsltIterate it:
                // A null select defaults to the children of the context node — that reads input.
                return it.Select is null || StreamingSubtreeBufferDetector.NavigatesInput(it.Select)
                    || BodyNavigatesStreamedInput(it.Body, ctx);
            case XsltForEachGroup feg:
                return StreamingSubtreeBufferDetector.NavigatesInput(feg.Select)
                    || (feg.GroupBy is not null && StreamingSubtreeBufferDetector.NavigatesInput(feg.GroupBy))
                    || (feg.GroupAdjacent is not null && StreamingSubtreeBufferDetector.NavigatesInput(feg.GroupAdjacent))
                    || BodyNavigatesStreamedInput(feg.Body, ctx);
            case XsltApplyTemplates at:
                // A null select means "children of the context node" — that reads the input.
                return at.Select is null || StreamingSubtreeBufferDetector.NavigatesInput(at.Select);

            case XsltIf iff:
                return StreamingSubtreeBufferDetector.NavigatesInput(iff.Test)
                    || BodyNavigatesStreamedInput(iff.Then, ctx);
            case XsltChoose ch:
                foreach (var w in ch.When)
                    if (StreamingSubtreeBufferDetector.NavigatesInput(w.Test)
                        || BodyNavigatesStreamedInput(w.Body, ctx))
                        return true;
                return BodyNavigatesStreamedInput(ch.Otherwise, ctx);

            case XsltOnEmpty oe:
                return (oe.Select is not null && StreamingSubtreeBufferDetector.NavigatesInput(oe.Select))
                    || BodyNavigatesStreamedInput(oe.Content, ctx);
            case XsltOnNonEmpty one:
                return (one.Select is not null && StreamingSubtreeBufferDetector.NavigatesInput(one.Select))
                    || BodyNavigatesStreamedInput(one.Content, ctx);
            case XsltWherePopulated wp:
                return BodyNavigatesStreamedInput(wp.Content, ctx);

            case XsltTry tr:
                if (tr.SelectExpression is not null
                    && StreamingSubtreeBufferDetector.NavigatesInput(tr.SelectExpression))
                    return true;
                if (BodyNavigatesStreamedInput(tr.Body, ctx)) return true;
                foreach (var cat in tr.Catches)
                    if (BodyNavigatesStreamedInput(cat.Body, ctx)) return true;
                return false;

            case XsltLiteralResultElement lre:
                if (AttributesNavigateInput(lre.Attributes)) return true;
                return BodyNavigatesStreamedInput(lre.Content, ctx);
            case XsltElement el:
                return BodyNavigatesStreamedInput(el.Content, ctx);
            case XsltCopy cp:
                return (cp.Select is not null && StreamingSubtreeBufferDetector.NavigatesInput(cp.Select))
                    || BodyNavigatesStreamedInput(cp.Content, ctx);
            case XsltDocument doc:
                return BodyNavigatesStreamedInput(doc.Content, ctx);
            case XsltResultDocument rd:
                return BodyNavigatesStreamedInput(rd.Content, ctx);
            case XsltComment cm:
                return (cm.Select is not null && StreamingSubtreeBufferDetector.NavigatesInput(cm.Select))
                    || BodyNavigatesStreamedInput(cm.Content, ctx);
            case XsltProcessingInstruction pi:
                return (pi.Select is not null && StreamingSubtreeBufferDetector.NavigatesInput(pi.Select))
                    || BodyNavigatesStreamedInput(pi.Content, ctx);

            case XsltFork fk:
                foreach (var seq in fk.Sequences)
                    if (BodyNavigatesStreamedInput(seq, ctx)) return true;
                foreach (var feg in fk.ForEachGroups)
                    if (StreamingSubtreeBufferDetector.NavigatesInput(feg.Select)
                        || BodyNavigatesStreamedInput(feg.Body, ctx))
                        return true;
                foreach (var rd in fk.ResultDocuments)
                    if (BodyNavigatesStreamedInput(rd.Content, ctx)) return true;
                return false;

            case XsltSequenceConstructor ctor:
                return BodyNavigatesStreamedInput(ctor, ctx);

            // A construct we do not model here (xsl:merge, xsl:analyze-string, …) that reached
            // this point is NOT guaranteed-streamable. Conservatively treat it as reading the
            // input so it is buffered whole rather than silently producing empty output — a
            // whole-input buffer is always correct (never under-buffers). This can only ever
            // over-buffer a genuinely inert construct, a performance loss not a correctness bug.
            default:
                return true;
        }
    }

    private static bool BodyNavigatesStreamedInput(XsltSequenceConstructor? body, StreamingContext ctx)
    {
        if (body is null)
            return false;
        foreach (var insn in body.Instructions)
            if (InstructionNavigatesStreamedInput(insn, ctx))
                return true;
        return false;
    }

    private static bool AttributesNavigateInput(
        IReadOnlyDictionary<PhoenixmlDb.Core.QName, XsltAttributeValueTemplate> attrs)
    {
        foreach (var avt in attrs.Values)
            foreach (var part in avt.Parts)
                if (part is AvtExpression ae && StreamingSubtreeBufferDetector.NavigatesInput(ae.Expression))
                    return true;
        return false;
    }
}
