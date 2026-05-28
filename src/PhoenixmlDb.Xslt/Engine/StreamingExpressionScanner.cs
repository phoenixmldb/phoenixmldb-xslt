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

    /// <summary>
    /// Scans the content body of an xsl:source-document instruction.
    /// Returns the list of watchers needed for the streaming pass.
    /// </summary>
    public IReadOnlyList<StreamWatcher> Scan(XsltSequenceConstructor? body)
    {
        _watchers.Clear();
        if (body == null) return _watchers;

        foreach (var instruction in body.Instructions)
        {
            ScanInstruction(instruction);
        }

        return _watchers;
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

            // xsl:for-each drives its own streaming via ForEachStreamingAsync; don't
            // register its select as a Sequence watcher. Body may still contain
            // consuming aggregates over non-consuming subexpressions, but in
            // streaming mode the body executes per-item with the current child as
            // context — leave that to the operator.
            case XsltForEach:
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

            // xsl:where-populated and on-empty/on-non-empty wrap bodies that may
            // include consuming sub-expressions.
            case XsltWherePopulated wp:
                ScanInstructions(wp.Content);
                break;
            case XsltOnEmpty oe:
                if (oe.Content != null) ScanInstructions(oe.Content);
                if (oe.Select != null) ScanExpression(oe.Select);
                break;
            case XsltOnNonEmpty one:
                if (one.Content != null) ScanInstructions(one.Content);
                if (one.Select != null) ScanExpression(one.Select);
                break;

            // xsl:attribute: the name and namespace AVTs may contain consuming expressions
            // (e.g., name="{translate(head(//AUTHOR), ' ', '_')}")
            case XsltAttribute attr:
                ScanAvt(attr.Name);
                if (attr.Namespace != null) ScanAvt(attr.Namespace);
                if (attr.Select != null) ScanExpression(attr.Select);
                if (attr.Content != null) ScanInstructions(attr.Content);
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
                                parts.Add(nameTest.LocalName);
                        }
                        else if (step.Axis == Axis.DescendantOrSelf)
                        {
                            // For // (descendant-or-self::node()/child::X),
                            // we use the child step name only
                            continue;
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
}
