using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Pre-scans the instruction tree inside xsl:source-document to identify
/// consuming sub-expressions and generate StreamWatchers for them.
/// </summary>
internal sealed class StreamingExpressionScanner
{
    private readonly List<StreamWatcher> _watchers = [];
    private readonly List<ForEachSubscription> _subscriptions = [];

    /// <summary>
    /// Result of <see cref="ScanWithSubscriptions"/> — watchers for consuming
    /// aggregations plus per-item subscriptions for streamable xsl:for-each.
    /// </summary>
    public readonly record struct ScanResult(
        IReadOnlyList<StreamWatcher> Watchers,
        IReadOnlyList<ForEachSubscription> Subscriptions);

    /// <summary>
    /// Scans the content body of an xsl:source-document instruction.
    /// Returns the list of watchers needed for the streaming pass.
    /// </summary>
    public IReadOnlyList<StreamWatcher> Scan(XsltSequenceConstructor? body)
        => ScanWithSubscriptions(body).Watchers;

    /// <summary>
    /// Scans the content body of an xsl:source-document instruction.
    /// Returns both the watchers for consuming aggregates and the
    /// subscriptions for streamable xsl:for-each instructions.
    /// </summary>
    public ScanResult ScanWithSubscriptions(XsltSequenceConstructor? body)
    {
        _watchers.Clear();
        _subscriptions.Clear();
        if (body != null)
        {
            ScanInstructions(body);
        }
        return new ScanResult(_watchers.ToArray(), _subscriptions.ToArray());
    }

    private void ScanInstruction(XsltInstruction instruction)
    {
        switch (instruction)
        {
            case XsltVariableInstruction variable:
                if (variable.Select != null)
                    ScanExpression(variable.Select);
                if (variable.Content != null)
                    ScanInstructions(variable.Content);
                break;

            case XsltValueOf valueOf:
                if (valueOf.Select != null)
                    ScanExpression(valueOf.Select);
                break;

            case XsltSequence seq:
                if (seq.Select != null)
                    ScanExpression(seq.Select);
                if (seq.Content != null)
                    ScanInstructions(seq.Content);
                break;

            case XsltCopyOf copyOf:
                ScanExpression(copyOf.Select);
                break;

            // xsl:for-each inside an xsl:source-document streamable body: if the
            // select is a simple absolute child-axis path and there are no sorts,
            // we can register a ForEachSubscription so the streaming processor
            // dispatches the body per matched start element. Anything more
            // complex falls back to the buffered path.
            case XsltForEach forEach:
                TryRegisterForEachSubscription(forEach);
                break;

            case XsltIf ifInsn:
                ScanExpression(ifInsn.Test);
                if (ifInsn.Then != null)
                    ScanInstructions(ifInsn.Then);
                break;

            case XsltChoose choose:
                foreach (var branch in choose.When)
                {
                    ScanExpression(branch.Test);
                    ScanInstructions(branch.Body);
                }
                if (choose.Otherwise != null)
                    ScanInstructions(choose.Otherwise);
                break;

            case XsltSequenceConstructor ctor:
                ScanInstructions(ctor);
                break;

            // Literal result elements have Content that may contain consuming instructions
            case XsltLiteralResultElement lre:
                ScanInstructions(lre.Content);
                break;

            // xsl:copy in streaming mode keeps its open tag deferred; recurse into
            // its content so consuming aggregates nested inside surface to the watcher
            // registry (Martin's pattern: <xsl:copy><xsl:fork><count/></xsl:fork></xsl:copy>).
            case XsltCopy copy:
                if (copy.Content != null) ScanInstructions(copy.Content);
                break;

            // xsl:fork: each prong (sequence body, for-each-group, result-document)
            // may contain consuming aggregates. Scanner walks them all so the
            // surrounding template body's deferred execution picks them up via
            // the watcher list; after children stream, prong bodies execute with
            // accumulated values substituted.
            case XsltFork fork:
                foreach (var seq in fork.Sequences)
                    ScanInstructions(seq);
                foreach (var feg in fork.ForEachGroups)
                {
                    if (feg.Body != null) ScanInstructions(feg.Body);
                }
                foreach (var rd in fork.ResultDocuments)
                {
                    if (rd.Content != null) ScanInstructions(rd.Content);
                }
                break;

            // xsl:where-populated / xsl:on-empty / xsl:on-non-empty have
            // conditional execution semantics (the body only fires depending on
            // whether sibling content is empty/non-empty). The streaming-pass
            // dispatch for ForEachSubscription is unconditional, so registering
            // a subscription for a for-each inside these wrappers would break
            // the gate. Skip descending here entirely — the for-each (and any
            // consuming aggregates inside) fall back to the buffered execution
            // path that honours the wrapper's semantics.
            case XsltWherePopulated:
            case XsltOnEmpty:
            case XsltOnNonEmpty:
                break;

            // xsl:attribute: the name and namespace AVTs may contain consuming expressions
            // (e.g., name="{translate(head(//AUTHOR), ' ', '_')}")
            case XsltAttribute attr:
                ScanAvt(attr.Name);
                if (attr.Namespace != null) ScanAvt(attr.Namespace);
                if (attr.Select != null) ScanExpression(attr.Select);
                if (attr.Content != null) ScanInstructions(attr.Content);
                break;

            // xsl:element: same shape as xsl:attribute — the name and namespace AVTs
            // may contain consuming expressions (e.g., name="{head(//AUTHOR)}"). The
            // body (Content) executes after the streaming pass with the AVT
            // resolved via watcher value substitution at EvaluateAvtAsync time.
            case XsltElement elem:
                ScanAvt(elem.Name);
                if (elem.Namespace != null) ScanAvt(elem.Namespace);
                ScanInstructions(elem.Content);
                break;

            // Skip xsl:apply-templates and xsl:iterate — handled by existing streaming
            case XsltApplyTemplates:
            case XsltIterate:
                break;

            default:
                // Scan any child content if the instruction is a sequence constructor
                if (instruction is XsltSequenceConstructor childCtor)
                    ScanInstructions(childCtor);
                break;
        }
    }

    private void ScanInstructions(XsltSequenceConstructor body)
    {
        foreach (var insn in body.Instructions)
            ScanInstruction(insn);
    }

    private void ScanAvt(XsltAttributeValueTemplate avt)
    {
        foreach (var part in avt.Parts)
        {
            if (part is AvtExpression avtExpr)
                ScanExpression(avtExpr.Expression);
        }
    }

    /// <summary>
    /// Scans an XPath expression for consuming sub-expressions.
    /// </summary>
    private void ScanExpression(XQueryExpression expr)
    {
        switch (expr)
        {
            // Recognized aggregation patterns: count(path), sum(path), max(path), etc.
            case FunctionCallExpression fc when IsAggregationFunction(fc):
                var aggType = GetAggregationType(fc);
                var pathInfo = ExtractPathFromArgument(fc.Arguments[0]);
                if (pathInfo != null)
                {
                    _watchers.Add(new StreamWatcher
                    {
                        SourceExpression = expr,
                        PathMatcher = new StreamPathMatcher(pathInfo.Value.Path),
                        Aggregation = aggType,
                        ValueAttribute = pathInfo.Value.Attribute,
                        Separator = aggType == WatcherAggregation.StringJoin && fc.Arguments.Count > 1
                            ? ExtractStringLiteral(fc.Arguments[1])
                            : null
                    });
                    return; // Don't recurse into recognized aggregation
                }
                break;

            // fn:head(path) — capture only the first matched item as a scalar.
            // This handles consuming expressions like head(//AUTHOR) in AVT attribute
            // name/namespace positions where a single string value is required.
            case FunctionCallExpression fcHead when IsHeadFunction(fcHead):
                var headPathInfo = ExtractPathFromArgument(fcHead.Arguments[0]);
                if (headPathInfo != null)
                {
                    _watchers.Add(new StreamWatcher
                    {
                        SourceExpression = expr,
                        PathMatcher = new StreamPathMatcher(headPathInfo.Value.Path),
                        Aggregation = WatcherAggregation.Head,
                        ValueAttribute = headPathInfo.Value.Attribute
                    });
                    return;
                }
                break;

            // Bare downward path — collect as Sequence
            case PathExpression path when IsDownwardPath(path):
                var seqPath = ExtractPathFromExpression(path);
                if (seqPath != null)
                {
                    _watchers.Add(new StreamWatcher
                    {
                        SourceExpression = expr,
                        PathMatcher = new StreamPathMatcher(seqPath.Value.Path),
                        Aggregation = WatcherAggregation.Sequence,
                        ValueAttribute = seqPath.Value.Attribute
                    });
                    return;
                }
                break;

            // snapshot() / copy-of() wrapping a path
            case FunctionCallExpression fc2 when IsSnapshotFunction(fc2):
                var snapPath = ExtractPathFromArgument(fc2.Arguments[0]);
                if (snapPath != null)
                {
                    _watchers.Add(new StreamWatcher
                    {
                        SourceExpression = expr,
                        PathMatcher = new StreamPathMatcher(snapPath.Value.Path),
                        Aggregation = WatcherAggregation.Snapshot,
                        ValueAttribute = snapPath.Value.Attribute
                    });
                    return;
                }
                break;

            // Map constructor — scan each entry value
            case MapConstructor map:
                foreach (var entry in map.Entries)
                {
                    ScanExpression(entry.Value);
                }
                return;

            // Binary expressions — scan both sides
            case BinaryExpression bin:
                ScanExpression(bin.Left);
                ScanExpression(bin.Right);
                return;

            // SimpleMap (path!cast!cast …) — when the Left is a downward path
            // AND the Right consists only of per-item atomic-cast / function
            // applications on the context item (no navigation back into the
            // streamed node), register a Sequence watcher keyed on the whole
            // SimpleMap. The watcher rewriter then swaps the SimpleMap for
            // $__streaming_watcher_N (a sequence of leaf string values), and
            // the plan re-applies the casts per atomic item. Handles patterns
            // like /*/*/ITEM/DIMENSIONS!xs:NMTOKENS(.)!xs:decimal(.) where the
            // tail must surface XPTY0004 on incompatible comparison rather
            // than short-circuit to empty.
            case SimpleMapExpression sm when TryExtractSimpleMapWatcherShape(sm) is { } shape:
                _watchers.Add(new StreamWatcher
                {
                    SourceExpression = expr,
                    PathMatcher = new StreamPathMatcher(shape.Path),
                    Aggregation = WatcherAggregation.Sequence,
                    ValueAttribute = shape.Attribute
                });
                return;
        }

        // Recurse into child expressions for unrecognized patterns
        ScanChildExpressions(expr);
    }

    private void ScanChildExpressions(XQueryExpression expr)
    {
        switch (expr)
        {
            case FunctionCallExpression fc:
                foreach (var arg in fc.Arguments)
                    ScanExpression(arg);
                break;
            case UnaryExpression unary:
                ScanExpression(unary.Operand);
                break;
            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    ScanExpression(item);
                break;
            case FlworExpression flwor:
                // The for clause source may be consuming
                foreach (var clause in flwor.Clauses)
                {
                    if (clause is ForClause forClause)
                    {
                        foreach (var binding in forClause.Bindings)
                            ScanExpression(binding.Expression);
                    }
                }
                break;
        }
    }

    private static bool IsAggregationFunction(FunctionCallExpression fc)
    {
        if (fc.Arguments.Count == 0) return false;
        return fc.Name.LocalName is "count" or "sum" or "max" or "min" or "avg" or "string-join"
            && (fc.Name.Namespace == NamespaceId.None || fc.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn);
    }

    private static WatcherAggregation GetAggregationType(FunctionCallExpression fc)
    {
        return fc.Name.LocalName switch
        {
            "count" => WatcherAggregation.Count,
            "sum" => WatcherAggregation.Sum,
            "max" => WatcherAggregation.Max,
            "min" => WatcherAggregation.Min,
            "avg" => WatcherAggregation.Avg,
            "string-join" => WatcherAggregation.StringJoin,
            _ => WatcherAggregation.Sequence
        };
    }

    private static bool IsSnapshotFunction(FunctionCallExpression fc)
    {
        return fc.Name.LocalName is "snapshot" or "copy-of"
            && fc.Arguments.Count >= 1;
    }

    private static bool IsHeadFunction(FunctionCallExpression fc)
    {
        return fc.Name.LocalName == "head"
            && fc.Arguments.Count == 1
            && (fc.Name.Namespace == NamespaceId.None || fc.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn);
    }

    /// <summary>
    /// Recognizes a SimpleMap whose deep-left source is a downward PathExpression
    /// and whose every subsequent step is a per-item atomic operation on the
    /// context item. Handles chains like <c>path!xs:NMTOKENS(.)!xs:decimal(.)</c>
    /// where the parser builds left-associative SimpleMap nodes:
    /// SimpleMap(SimpleMap(path, NMTOKENS(.)), decimal(.)).
    /// </summary>
    private static (string Path, string? Attribute)? TryExtractSimpleMapWatcherShape(SimpleMapExpression sm)
    {
        // Walk down the Left chain, accumulating tail steps. Every step we
        // encounter (the Right of each SimpleMap we descend through) must be
        // a per-item atomic operation on the context item.
        XQueryExpression current = sm;
        while (current is SimpleMapExpression mapNode)
        {
            if (!IsPerItemAtomicTail(mapNode.Right)) return null;
            current = mapNode.Left;
        }

        // The deep-left must be a downward path we can register a watcher on
        if (current is not PathExpression path || !IsDownwardPath(path))
            return null;

        return ExtractPathFromExpression(path);
    }

    /// <summary>
    /// Returns true when <paramref name="tail"/> is safe to re-apply per atomic
    /// item from a SimpleMap source — i.e. it operates only on the context item
    /// (<c>.</c>) via atomic casts or built-in fn:/xs: function calls, possibly
    /// chained through further SimpleMap operators. Rejects anything that
    /// navigates (PathExpression, attribute access, etc.) because the captured
    /// items are strings, not nodes.
    /// </summary>
    private static bool IsPerItemAtomicTail(XQueryExpression tail)
    {
        switch (tail)
        {
            case ContextItemExpression:
                return true;
            case CastExpression cast:
                return IsPerItemAtomicTail(cast.Expression);
            case CastableExpression castable:
                return IsPerItemAtomicTail(castable.Expression);
            case TreatExpression treat:
                return IsPerItemAtomicTail(treat.Expression);
            case InstanceOfExpression inst:
                return IsPerItemAtomicTail(inst.Expression);
            case FunctionCallExpression fc:
                // Allow fn:/xs: constructor and library function calls whose
                // arguments are themselves per-item atomic. xs:NMTOKENS(.),
                // xs:decimal(.), fn:upper-case(.), etc.
                var ns = fc.Name.Namespace;
                if (ns != NamespaceId.None
                    && ns != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn
                    && ns != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Xs)
                    return false;
                foreach (var arg in fc.Arguments)
                {
                    if (!IsPerItemAtomicTail(arg)) return false;
                }
                return true;
            case SimpleMapExpression sm:
                return IsPerItemAtomicTail(sm.Left) && IsPerItemAtomicTail(sm.Right);
            case StringLiteral:
            case IntegerLiteral:
            case DoubleLiteral:
            case DecimalLiteral:
            case BooleanLiteral:
                return true;
            default:
                return false;
        }
    }

    private static bool IsDownwardPath(PathExpression path)
    {
        // A path is "downward" if it uses only child/descendant/descendant-or-self/attribute axes
        foreach (var step in path.Steps)
        {
            if (step.Axis is not (Axis.Child or Axis.Descendant or Axis.DescendantOrSelf or Axis.Attribute))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Extracts a simple path string from an XPath expression.
    /// Returns null if the expression is too complex to decompose.
    /// </summary>
    private static (string Path, string? Attribute)? ExtractPathFromArgument(XQueryExpression expr)
    {
        return ExtractPathFromExpression(expr);
    }

    private static (string Path, string? Attribute)? ExtractPathFromExpression(XQueryExpression expr)
    {
        // Walk the path expression to build a simple "a/b/c" or "a/b/@attr" string.
        // This handles the common cases: child steps and attribute access.
        // Complex expressions (predicates, descendant-or-self) fall through to null.
        var parts = new List<string>();
        string? attribute = null;

        var current = expr;
        while (current != null)
        {
            switch (current)
            {
                case PathExpression pathExpr:
                    // PathExpression has steps — iterate
                    bool pendingDescendant = false;
                    foreach (var step in pathExpr.Steps)
                    {
                        if (step.Axis == Axis.Attribute)
                        {
                            if (step.NodeTest is NameTest attrNameTest)
                                attribute = attrNameTest.LocalName;
                        }
                        else if (step.Axis == Axis.Child)
                        {
                            if (step.NodeTest is NameTest nameTest)
                            {
                                if (pendingDescendant)
                                {
                                    parts.Add("**");
                                    pendingDescendant = false;
                                }
                                parts.Add(nameTest.LocalName);
                            }
                        }
                        else if (step.Axis == Axis.DescendantOrSelf)
                        {
                            // For // (descendant-or-self::node()/child::X),
                            // mark that the following child step is at any depth.
                            pendingDescendant = true;
                            continue;
                        }
                        else if (step.Axis == Axis.Descendant)
                        {
                            // descendant::X — match X at any depth below the
                            // current context. Encode the "any-depth" marker
                            // before the name step so the path matcher walks
                            // ancestors loosely for this segment.
                            if (step.NodeTest is NameTest descNameTest)
                            {
                                parts.Add("**");
                                parts.Add(descNameTest.LocalName);
                                pendingDescendant = false;
                            }
                            else
                            {
                                return null;
                            }
                        }
                        else
                        {
                            return null; // Unsupported axis
                        }
                    }
                    current = null;
                    break;

                case ContextItemExpression:
                    current = null;
                    break;

                default:
                    return null; // Can't decompose
            }
        }

        if (parts.Count == 0) return null;
        return (string.Join("/", parts), attribute);
    }

    private static string? ExtractStringLiteral(XQueryExpression expr)
    {
        return expr is StringLiteral sl ? sl.Value : null;
    }

    /// <summary>
    /// Registers a <see cref="ForEachSubscription"/> for an <c>xsl:for-each</c>
    /// whose select expression is either a simple absolute child-axis path or
    /// a comma sequence of grounded operands surrounding a single streamable
    /// path. Otherwise leaves the for-each to the buffered execution path.
    /// </summary>
    private void TryRegisterForEachSubscription(XsltForEach forEach)
    {
        if (forEach.Sorts.Count > 0) return;

        if (!TryDecomposeForEachSelect(forEach.Select, out var prefix, out var path, out var textNodeTail, out var suffix, out var attributeName, out var predicates))
            return;

        _subscriptions.Add(new ForEachSubscription
        {
            SourceInstruction = forEach,
            PathMatcher = path,
            Body = forEach.Body,
            PrefixItems = prefix,
            SuffixItems = suffix,
            TextNodeTail = textNodeTail,
            AttributeName = attributeName,
            Predicates = predicates,
        });
    }

    /// <summary>
    /// Recognizes <paramref name="select"/> as either a single streamable absolute
    /// child-axis path (with optional text() KindTest at the last step) OR a comma
    /// sequence with exactly one streamable path operand surrounded by zero or more
    /// "grounded" operands (literals, variable refs).
    /// </summary>
    private static bool TryDecomposeForEachSelect(
        XQueryExpression select,
        out IReadOnlyList<XQueryExpression> prefix,
        out StreamPathMatcher path,
        out bool textNodeTail,
        out IReadOnlyList<XQueryExpression> suffix,
        out string? attributeName,
        out IReadOnlyList<XQueryExpression> predicates)
    {
        prefix = Array.Empty<XQueryExpression>();
        suffix = Array.Empty<XQueryExpression>();
        path = null!;
        textNodeTail = false;
        attributeName = null;
        predicates = Array.Empty<XQueryExpression>();

        var singleMatcher = TryBuildPathMatcher(select, out textNodeTail, out attributeName, out predicates);
        if (singleMatcher != null)
        {
            path = singleMatcher;
            return true;
        }
        textNodeTail = false;
        attributeName = null;
        predicates = Array.Empty<XQueryExpression>();

        // AST uses SequenceExpression with Items property for comma sequences.
        if (select is not SequenceExpression seq) return false;
        var operands = seq.Items;
        if (operands == null || operands.Count < 2) return false;

        int streamableIndex = -1;
        for (int i = 0; i < operands.Count; i++)
        {
            var inner = TryBuildPathMatcher(operands[i], out var innerTail, out var innerAttr, out var innerPreds);
            if (inner == null) continue;
            if (streamableIndex >= 0) return false; // more than one streamable operand
            streamableIndex = i;
            path = inner;
            textNodeTail = innerTail;
            attributeName = innerAttr;
            predicates = innerPreds;
        }
        if (streamableIndex < 0) return false;

        for (int i = 0; i < operands.Count; i++)
        {
            if (i == streamableIndex) continue;
            if (!IsGroundedForStreaming(operands[i])) return false;
        }

        var prefixArr = new XQueryExpression[streamableIndex];
        for (int i = 0; i < streamableIndex; i++) prefixArr[i] = operands[i];
        var suffixArr = new XQueryExpression[operands.Count - streamableIndex - 1];
        for (int i = streamableIndex + 1; i < operands.Count; i++) suffixArr[i - streamableIndex - 1] = operands[i];
        prefix = prefixArr;
        suffix = suffixArr;
        return true;
    }

    /// <summary>
    /// True when <paramref name="expr"/> can be evaluated outside the streaming pass
    /// without consuming the source stream. Conservatively accepts literals and
    /// variable refs; rejects PathExpression and anything else.
    /// </summary>
    private static bool IsGroundedForStreaming(XQueryExpression expr)
    {
        return expr switch
        {
            IntegerLiteral or DecimalLiteral or DoubleLiteral or StringLiteral or BooleanLiteral => true,
            VariableReference => true,
            PathExpression => false,
            _ => false,
        };
    }

    /// <summary>
    /// Builds a <see cref="StreamPathMatcher"/> from an XPath expression when
    /// it is an absolute path consisting purely of child-axis name-test steps
    /// with no predicates, optionally ending in a <c>text()</c> KindTest
    /// (signaled via <paramref name="textNodeTail"/>). Returns null for any
    /// other shape so the buffered execution path can handle it.
    /// </summary>
    private static StreamPathMatcher? TryBuildPathMatcher(XQueryExpression select, out bool textNodeTail, out string? attributeName, out IReadOnlyList<XQueryExpression> predicates)
    {
        textNodeTail = false;
        attributeName = null;
        predicates = Array.Empty<XQueryExpression>();
        // Peek through fn:data(path) — for untyped nodes, data() returns the string value
        // which equals what value-of/sequence emits for the node directly, so unwrapping
        // is semantically safe when the body just reads the context item.
        if (select is FunctionCallExpression dataCall
            && dataCall.Name.LocalName == "data"
            && dataCall.Arguments.Count == 1
            && (dataCall.Name.Namespace == NamespaceId.None || dataCall.Name.Namespace == PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn))
        {
            select = dataCall.Arguments[0];
        }
        if (select is not PathExpression path) return null;
        // Accept either absolute (/path) or relative-from-root (path) when no initial
        // expression is set. Source-document body's implicit context item is the
        // document root, so relative paths have the same semantics as absolute.
        if (!path.IsAbsolute && path.InitialExpression != null) return null;
        if (path.Steps.Count == 0) return null;

        var parts = new List<string>(path.Steps.Count);
        for (int i = 0; i < path.Steps.Count; i++)
        {
            var step = path.Steps[i];
            bool isLastStep = i == path.Steps.Count - 1;

            // Allow Axis.Attribute on the LAST step with a NameTest, signaling the
            // processor to dispatch the matched element's attribute by that name.
            if (isLastStep && step.Axis == Axis.Attribute)
            {
                if (step.NodeTest is not NameTest attrName) return null;
                if (attrName.IsLocalNameWildcard || attrName.IsNamespaceWildcard) return null;
                if (step.Predicates.Count > 0) return null; // predicates on the attribute step itself — skip
                attributeName = attrName.LocalName;
                break;
            }

            if (step.Axis != Axis.Child) return null;

            // Predicates: only allowed on the step that supplies the matched element
            // context — i.e., the last step OR the step immediately before an
            // attribute-tail step. Reject predicates on any earlier step.
            if (step.Predicates.Count > 0)
            {
                bool isLastElementStep = isLastStep
                    || (i == path.Steps.Count - 2 && path.Steps[i + 1].Axis == Axis.Attribute);
                if (!isLastElementStep) return null;
                predicates = step.Predicates;
            }

            // text() KindTest is acceptable only as the LAST step.
            if (isLastStep
                && step.NodeTest is KindTest kindTest
                && kindTest.Kind == XdmNodeKind.Text
                && kindTest.Name == null
                && kindTest.TypeName == null)
            {
                textNodeTail = true;
                break;
            }

            if (step.NodeTest is not NameTest nameTest) return null;
            if (nameTest.IsLocalNameWildcard || nameTest.IsNamespaceWildcard) return null;
            parts.Add(nameTest.LocalName);
        }

        if (parts.Count == 0) return null;
        return new StreamPathMatcher(string.Join("/", parts));
    }
}
