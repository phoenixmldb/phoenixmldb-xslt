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
            // A top-level xsl:for-each-group appearing directly in a streamable
            // source-document body (or a match="/" document-node template) has no
            // document-level streaming dispatch: ForEachGroupStreamingAsync only runs
            // when _isStreamingExecution is already set (i.e. inside a template the
            // streaming processor dispatched). At the document level the body executes
            // against the synthetic empty document node, so the for-each-group's select
            // (account/transaction, chapter/*, //Item/copy-of(), …) evaluates to the
            // empty sequence and the group body never runs — only an empty wrapper is
            // emitted. group-by is always absorbing (it must see the whole population);
            // group-adjacent / group-starting-with / group-ending-with at the document
            // level likewise need the real input. Materialize the whole input and run
            // the buffered xsl:for-each-group, which evaluates the population against the
            // real document root and groups correctly. Only a grounded select
            // (literal / range / variable, no input navigation) can stay on the
            // streaming path — buffering a grounded population would be wasteful and a
            // large input could time out.
            case XsltForEachGroup feg:
                return feg.GroupBy != null || NavigatesInput(feg.Select);

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
            // absorbing aggregation/reduction over a sequence that navigates the input has
            // no usable document-level streaming dispatch and would otherwise evaluate the
            // select against the synthetic empty document node. See SelectAbsorbsInput for
            // the full set of matched functions and the watcher-coexistence guard.
            //
            // The same empty-doc trap also strikes a select whose input-navigating operand
            // is wrapped in an EXPRESSION OPERATOR that the streaming pass cannot drive at
            // the document level — `treat as` / `instance of` / `castable as`, a
            // union/except/intersect, a square-array constructor, or a mixed comma sequence
            // (e.g. `(copy-of(//ITEM/PRICE), $insertion)`, `(.//node()) instance of
            // element()*`, `(account/transaction/@value) treat as attribute()+`). The
            // watcher-based dispatch only fires for a BARE path / simple-map; once the path
            // is an operand of one of these operators the select evaluates against the
            // empty synthetic node, dropping the navigating operand (or returning the wrong
            // instance-of/treat-cardinality result). Route those to the whole-input buffer.
            // See SelectNavigatesViaUnstreamableOperator. A bare path operand stays on the
            // streaming path (the operator helper returns false for it).
            case XsltValueOf vo:
                return vo.Select != null
                    && (SelectAbsorbsInput(vo.Select) || SelectNavigatesViaUnstreamableOperator(vo.Select));

            case XsltCopyOf cof:
                return SelectAbsorbsInput(cof.Select) || SelectNavigatesViaUnstreamableOperator(cof.Select);

            case XsltSequence sq:
                return sq.Select != null
                    && (SelectAbsorbsInput(sq.Select) || SelectNavigatesViaUnstreamableOperator(sq.Select));

            case XsltFork fk:
                // A for-each-group prong is itself absorbing at the document level
                // (see the standalone XsltForEachGroup case): group-by always, and the
                // other grouping modes when the population navigates the input.
                foreach (var feg in fk.ForEachGroups)
                    if (feg.GroupBy != null || NavigatesInput(feg.Select)) return true;
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
    internal static bool NavigatesInput(XQueryExpression expr)
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

            // A filtered primary (e.g. (1 to 5)[. gt year-from-date(current-date())])
            // navigates the input only when its base sequence does. The predicate's
            // context item is the primary's items, NOT the streamed input root — so a
            // bare `.` or relative step inside the predicate filters the grounded base,
            // it does not read the document. A grounded base (range, literal, variable)
            // therefore stays on the streaming path — the critical grounded-argument
            // guard: sum((1 to 5)[…], sum(//PRICE)) must NOT buffer on its first operand.
            case FilterExpression filt:
                return NavigatesInput(filt.Primary);

            // Conservative default: an expression shape we don't model might navigate
            // the input, so buffer rather than risk an empty-context evaluation.
            default:
                return true;
        }
    }

    /// <summary>
    /// True when <paramref name="expr"/> contains a call to an absorbing aggregation /
    /// reduction function anywhere in its tree whose operand navigates the input, where
    /// that call has no usable document-level streaming dispatch and would otherwise fold
    /// the empty synthetic document node. Such a call forces whole-input buffering. The
    /// call may be nested inside wrapping functions (<c>round(sum(…))</c>,
    /// <c>format-number(avg(…), …)</c>, <c>xs:integer(round(sum(…)))</c>) or boolean
    /// connectives, so the whole select tree is walked. An aggregation over a grounded
    /// sequence (literals, variables, ranges) stays on the streaming path.
    /// </summary>
    /// <remarks>
    /// Two function families are matched, distinguished by their streaming support:
    /// <list type="bullet">
    /// <item><b>No watcher at all</b> — <c>fold-left</c>/<c>fold-right</c> (higher-order),
    /// and the absorbing predicates <c>boolean</c>/<c>not</c>/<c>exists</c>/<c>empty</c>
    /// plus the cardinality functions <c>one-or-more</c>/<c>exactly-one</c>/<c>zero-or-one</c>.
    /// These are never registered by <see cref="StreamingExpressionScanner"/>, so ANY
    /// input-navigating operand falls to the empty-doc path. We buffer whenever the operand
    /// navigates the input.</item>
    /// <item><b>Watcher-backed aggregations</b> — <c>sum</c>/<c>count</c>/<c>avg</c>/
    /// <c>max</c>/<c>min</c>/<c>string-join</c>. The streaming pass handles these whenever the
    /// operand is a single <see cref="PathExpression"/> — either via a StreamWatcher
    /// (<c>sum(//PRICE)</c>) or via direct document-level path streaming, INCLUDING paths
    /// whose final step is a computed value or function call (<c>sum(account/transaction/(@value*2))</c>,
    /// <c>sum(account/transaction/abs(@value))</c>, <c>max(.../PUB-DATE/xs:date(.))</c>). We must
    /// NOT steal any of those — buffering a large input there would time out. So we buffer ONLY
    /// when the operand navigates the input AND is a COMPOSITE (non-path) shape the streaming
    /// pass cannot drive: a parenthesised sequence mixing paths and literals
    /// (<c>sum((path, 31, 32))</c>), a simple-map cast chain (<c>sum(path!xs:decimal(.))</c>),
    /// a FLWOR (<c>sum(for $d in … return …)</c>), a union (<c>count(a | b)</c>), or a
    /// function-wrapped operand (<c>count(remove(path, 3))</c>). See <see cref="IsBarePathOperand"/>.</item>
    /// </list>
    /// </remarks>
    private static bool SelectAbsorbsInput(XQueryExpression expr)
    {
        switch (expr)
        {
            case FunctionCallExpression fc:
                switch (ClassifyAbsorbing(fc))
                {
                    case AbsorbingKind.NoWatcher:
                        if (fc.Arguments.Count > 0 && NavigatesInput(fc.Arguments[0]))
                            return true;
                        break;
                    case AbsorbingKind.WatcherBacked:
                        if (fc.Arguments.Count > 0
                            && NavigatesInput(fc.Arguments[0])
                            && !IsBarePathOperand(fc.Arguments[0]))
                            return true;
                        break;
                }
                // A collation argument that navigates the input: distinct-values(seq, COLL),
                // index-of(seq, search, COLL), max(seq, COLL), min(seq, COLL) etc. carry the
                // collation URI in a NON-first argument. When that argument is an input-
                // navigating path (e.g. /special/unknownCollation), the document-level
                // streaming pass evaluates it against the synthetic empty node — the collation
                // resolves to empty and the default collation is silently used, so an unknown
                // collation never raises FOCH0002. Route to the whole-input buffer so the
                // collation argument resolves against the real (bounded) input.
                if (CollationArgNavigatesInput(fc))
                    return true;
                foreach (var arg in fc.Arguments)
                    if (SelectAbsorbsInput(arg)) return true;
                return false;

            case BinaryExpression bin:
                return SelectAbsorbsInput(bin.Left) || SelectAbsorbsInput(bin.Right);

            case UnaryExpression un:
                return SelectAbsorbsInput(un.Operand);

            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    if (SelectAbsorbsInput(item)) return true;
                return false;

            case SimpleMapExpression sm:
                return SelectAbsorbsInput(sm.Left) || SelectAbsorbsInput(sm.Right);

            // A filtered absorbing call — one-or-more(path)[position() mod 2 = 0],
            // one-or-more(path//text())[position() lt 4]. The absorbing call is the
            // filter's primary; descend into it (predicates only re-filter the buffered
            // result and don't add a separate document-level dispatch).
            case FilterExpression filt:
                return SelectAbsorbsInput(filt.Primary);

            default:
                return false;
        }
    }

    /// <summary>
    /// True when <paramref name="fc"/> is an fn:-namespace function that takes a collation
    /// URI as a trailing argument AND that collation argument navigates the streamed input.
    /// The recognised functions and their collation-argument position (0-based):
    /// <list type="bullet">
    /// <item><c>distinct-values(seq, collation)</c> — arg 1</item>
    /// <item><c>index-of(seq, search, collation)</c> — arg 2</item>
    /// <item><c>max(seq, collation)</c> / <c>min(seq, collation)</c> — arg 1</item>
    /// <item><c>compare(a, b, collation)</c> — arg 2; <c>deep-equal(a, b, collation)</c> — arg 2</item>
    /// </list>
    /// Only the collation argument is inspected here (the data arguments are handled by the
    /// absorbing-classification above). A collation argument that does not navigate the input
    /// (a literal URI, a variable, or absent) is left on the streaming path.
    /// </summary>
    private static bool CollationArgNavigatesInput(FunctionCallExpression fc)
    {
        if (fc.Name.Namespace != NamespaceId.None
            && fc.Name.Namespace != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn)
            return false;

        int collationArgIndex = fc.Name.LocalName switch
        {
            "distinct-values" or "max" or "min" => 1,
            "index-of" or "compare" or "deep-equal" => 2,
            _ => -1,
        };

        return collationArgIndex >= 0
            && fc.Arguments.Count > collationArgIndex
            && NavigatesInput(fc.Arguments[collationArgIndex]);
    }

    private enum AbsorbingKind { None, NoWatcher, WatcherBacked }

    /// <summary>
    /// Classifies an fn:-namespace function call by its absorbing-aggregation family.
    /// See <see cref="SelectAbsorbsInput"/> for why the two families are gated differently.
    /// </summary>
    private static AbsorbingKind ClassifyAbsorbing(FunctionCallExpression fc)
    {
        if (fc.Name.Namespace != NamespaceId.None
            && fc.Name.Namespace != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn)
            return AbsorbingKind.None;

        return fc.Name.LocalName switch
        {
            // Higher-order absorbing reducers and absorbing predicates / cardinality
            // checks: no StreamWatcher exists for any of these, so any input-navigating
            // operand falls to the empty-doc path.
            "fold-left" or "fold-right"
                or "boolean" or "not" or "exists" or "empty"
                or "one-or-more" or "exactly-one" or "zero-or-one"
                => AbsorbingKind.NoWatcher,

            // Aggregations the scanner streams via a watcher when the operand is a bare
            // downward path; buffer only the operand shapes the watcher rejects.
            "sum" or "count" or "avg" or "max" or "min" or "string-join"
                => AbsorbingKind.WatcherBacked,

            _ => AbsorbingKind.None,
        };
    }

    /// <summary>
    /// True when <paramref name="arg"/> is a single <see cref="PathExpression"/> — the shape
    /// the streaming pass already drives directly off the live reader for a watcher-backed
    /// aggregation, whether or not the scanner registers a watcher for it. This deliberately
    /// accepts paths with computed or function-call final steps (<c>account/transaction/(@value*2)</c>,
    /// <c>account/transaction/abs(@value)</c>, <c>.../PUB-DATE/xs:date(.)</c>): those stream
    /// correctly today and must NOT be diverted to whole-input buffering (which on a large
    /// input would time out). A bare context-item reference is likewise left on the streaming
    /// path. Everything else — parenthesised sequences, simple-maps, FLWORs, unions,
    /// function-wrapped operands — is a composite the streaming pass can't satisfy at the
    /// document level, so it must be buffered.
    /// </summary>
    private static bool IsBarePathOperand(XQueryExpression arg)
    {
        return arg switch
        {
            PathExpression => true,
            ContextItemExpression => true,
            // (path) — a parenthesised single path round-trips through a one-item sequence.
            SequenceExpression seq when seq.Items.Count == 1 => IsBarePathOperand(seq.Items[0]),
            _ => false,
        };
    }

    /// <summary>
    /// True when <paramref name="expr"/> is (or, after peeling a wrapping simple-map /
    /// filter, resolves to) an EXPRESSION OPERATOR node whose operand navigates the streamed
    /// input but which the document-level streaming pass cannot drive — so evaluating the
    /// select against the synthetic empty document node would drop the navigating operand or
    /// compute the wrong result. Such a shape forces whole-input buffering. The matched
    /// operators:
    /// <list type="bullet">
    /// <item><c>treat as</c> / <c>instance of</c> / <c>castable as</c>
    /// (<see cref="TreatExpression"/> / <see cref="InstanceOfExpression"/> /
    /// <see cref="CastableExpression"/>): the path operand evaluates to empty against the
    /// synthetic node, so <c>(.//node()) instance of element()*</c> returns the wrong boolean
    /// and <c>(EXPR treat as T+)</c> raises a spurious empty-cardinality error.</item>
    /// <item><c>union</c> / <c>except</c> / <c>intersect</c> (<see cref="BinaryExpression"/>):
    /// a navigating operand on either side is dropped.</item>
    /// <item>a square-array constructor <c>[ … ]</c> (<see cref="ArrayConstructor"/>) over a
    /// navigating member.</item>
    /// <item>a comma sequence <c>(A, B)</c> (<see cref="SequenceExpression"/>, two or more
    /// items) mixing a navigating operand with grounded operands — the navigating side is
    /// dropped, leaving only the grounded items (<c>(copy-of(//ITEM/PRICE), $insertion)</c>
    /// emits just <c>$insertion</c>).</item>
    /// </list>
    /// A BARE path / context-item operand (the shape the watcher dispatch already streams,
    /// e.g. <c>copy-of(//ITEM)</c>, <c>//x ! string(.)</c>) is NOT matched — the helper only
    /// fires for the operator wrappers above, leaving plain streaming selects untouched.
    /// </summary>
    private static bool SelectNavigatesViaUnstreamableOperator(XQueryExpression expr)
    {
        switch (expr)
        {
            // treat as / instance of / castable as — the operand is the navigating part.
            case TreatExpression t:
                return NavigatesInput(t.Expression);
            case InstanceOfExpression io:
                return NavigatesInput(io.Expression);
            case CastableExpression ca:
                return NavigatesInput(ca.Expression);

            // A | B / A except B / A intersect B — a navigating operand on either side
            // has no document-level dispatch once it is a union/except/intersect operand.
            case BinaryExpression bin
                when bin.Operator is BinaryOperator.Union
                    or BinaryOperator.Except
                    or BinaryOperator.Intersect:
                return NavigatesInput(bin.Left) || NavigatesInput(bin.Right);

            // [ a, b, c ] — a square-array constructor over a navigating member.
            case ArrayConstructor arr when arr.Kind == ArrayConstructorKind.Square:
                foreach (var member in arr.Members)
                    if (NavigatesInput(member)) return true;
                return false;

            // (A, B, …) — a comma sequence of two or more items where at least one item
            // navigates the input. A single-item parenthesised expression is not a real
            // sequence operator: peel it and re-test the inner expression (so a lone
            // `(EXPR treat as T)` or `(A union B)` is still recognised).
            case SequenceExpression seq when seq.Items.Count == 1:
                return SelectNavigatesViaUnstreamableOperator(seq.Items[0]);
            case SequenceExpression seq when seq.Items.Count >= 2:
                foreach (var item in seq.Items)
                    if (NavigatesInput(item)) return true;
                return false;

            // `OPERATOR ! name(.)` / `OPERATOR ! local-name()` — the streaming context flows
            // through the simple-map Left, so peel to the operator on the left. (A bare-path
            // Left returns false, so a plain `//x ! string(.)` stays streaming.)
            case SimpleMapExpression sm:
                return SelectNavigatesViaUnstreamableOperator(sm.Left);

            // `OPERATOR[predicate]` — the operator is the filter's primary.
            case FilterExpression filt:
                return SelectNavigatesViaUnstreamableOperator(filt.Primary);

            // `[ … ]?*` / `[ … ]?n` — an array/map lookup whose base is a square-array
            // constructor over a navigating member. The members flatten out of the lookup,
            // so a navigating member is dropped just as in the bare constructor case
            // (`[$insertion, //PRICE/number()]?* ! (.+1)` emits only $insertion). Peel to
            // the lookup base. A bare-path base returns false (left on the streaming path).
            case LookupExpression lk:
                return SelectNavigatesViaUnstreamableOperator(lk.Base);

            default:
                return false;
        }
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
