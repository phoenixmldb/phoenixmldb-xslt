using System.Linq;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Static streamability checker for XSLT 3.0 (section 19).
/// Detects non-streamable expressions in streaming contexts and raises XTSE3430.
/// This is a conservative checker: it detects common non-streamable patterns
/// but does not implement the full posture/sweep classification from the spec.
/// </summary>
internal static class StreamabilityChecker
{
    /// <summary>
    /// Checks the body of an xsl:source-document streamable="yes" instruction.
    /// Throws XsltException with XTSE3430 if the body contains non-streamable expressions.
    /// </summary>
    public static void CheckSourceDocumentBody(
        XsltSequenceConstructor? body,
        SourceLocation? location,
        Dictionary<QName, XsltAttributeSet>? attributeSets = null,
        Dictionary<(QName Name, int Arity), XsltFunction>? functions = null)
    {
        if (body == null) return;

        var walker = new StreamingBodyWalker(attributeSets, functions);
        walker.Walk(body);

        if (walker.NonStreamableReason != null)
        {
            throw new XsltException(
                $"XTSE3430: The body of xsl:source-document is not guaranteed streamable: {walker.NonStreamableReason}",
                location);
        }

        // Check if a variable captures the streaming context item and is used
        // with child/descendant navigation inside a loop (for-each/iterate).
        if (HasStreamingVariableNavigatedInLoop(body))
        {
            throw new XsltException(
                "XTSE3430: A variable captures the streaming context item (.) and is used with child/descendant navigation inside a loop — multiple consumption is not streamable",
                location);
        }
    }

    /// <summary>
    /// Checks a template body used in a streamable mode.
    /// Throws XsltException with XTSE3430 if the body contains non-streamable expressions.
    /// </summary>
    public static void CheckStreamableTemplateBody(
        XsltSequenceConstructor? body,
        SourceLocation? location,
        Dictionary<QName, XsltAttributeSet>? attributeSets = null,
        Dictionary<(QName Name, int Arity), XsltFunction>? functions = null)
    {
        if (body == null) return;

        var walker = new StreamingBodyWalker(attributeSets, functions);
        walker.Walk(body);

        if (walker.NonStreamableReason != null)
        {
            throw new XsltException(
                $"XTSE3430: The body of this template in a streamable mode is not guaranteed streamable: {walker.NonStreamableReason}",
                location);
        }

        // current-group()/current-grouping-key() in a streamable mode template body
        // (outside of any xsl:for-each-group) is not streamable — the group context
        // from the caller doesn't propagate through apply-templates in streaming.
        if (ContainsCurrentGroupCallOutsideGroup(body))
        {
            throw new XsltException(
                "XTSE3430: The body of this template in a streamable mode uses current-group() or current-grouping-key(), which is not available in applied templates during streaming",
                location);
        }
    }

    /// <summary>
    /// Checks a function body declared with a streamability attribute.
    /// Validates that the function body conforms to its declared streamability category.
    /// </summary>
    public static void CheckStreamableFunctionBody(
        XsltSequenceConstructor? body,
        string streamability,
        List<XsltParam> parameters,
        SourceLocation? location,
        QName? functionName = null)
    {
        if (body == null) return;

        // shallow-descent requires at least one parameter (the consumed node)
        if (streamability == "shallow-descent" && parameters.Count == 0)
        {
            throw new XsltException(
                "XTSE3430: A function with streamability=\"shallow-descent\" must have at least one parameter",
                location);
        }

        // For filter, inspection, ascent, shallow-descent (NOT absorbing):
        // The first parameter must accept at most one node (not a sequence).
        // Absorbing functions CAN accept sequences — they consume the nodes.
        if (parameters.Count > 0 && streamability is "filter" or "inspection" or "ascent"
            or "shallow-descent")
        {
            var firstParam = parameters[0];
            if (firstParam.As != null)
            {
                // If declared as node()* or item()* etc — allows multiple items
                if (firstParam.As.Occurrence is Occurrence.ZeroOrMore or Occurrence.OneOrMore)
                {
                    throw new XsltException(
                        $"XTSE3430: The first parameter of a function with streamability=\"{streamability}\" must accept at most one item, but is declared as allowing a sequence",
                        location);
                }
            }
            else
            {
                // No type declaration — defaults to item()* which allows sequences
                if (streamability is "shallow-descent")
                {
                    throw new XsltException(
                        $"XTSE3430: The first parameter of a function with streamability=\"{streamability}\" must have an 'as' type restricting it to a single node",
                        location);
                }
            }
        }

        // For functions with streamability declarations, check the body
        var walker = new StreamingFunctionBodyWalker(streamability, parameters, functionName);
        walker.Walk(body);

        if (walker.NonStreamableReason != null)
        {
            throw new XsltException(
                $"XTSE3430: The body of this function with streamability=\"{streamability}\" is not guaranteed streamable: {walker.NonStreamableReason}",
                location);
        }
    }

    /// <summary>
    /// Checks that an attribute set declared as streamable="yes" actually has streamable content.
    /// In this context, last() and descendant axis are non-streamable anywhere in the expression,
    /// not just in predicates.
    /// </summary>
    public static void CheckStreamableAttributeSet(List<XsltAttribute> attributes, SourceLocation? location)
    {
        foreach (var attr in attributes)
        {
            if (attr.Select == null) continue;

            // Check for last() or descendant axis anywhere
            var checker = new NonStreamableExpressionChecker();
            checker.Walk(attr.Select);
            if (checker.Reason != null)
            {
                throw new XsltException(
                    $"XTSE3430: Attribute set declared streamable=\"yes\" contains non-streamable expression: {checker.Reason}",
                    location);
            }

            // Also check for last() at top level (not just in predicates)
            if (ContainsLastFunction(attr.Select))
            {
                throw new XsltException(
                    "XTSE3430: Attribute set declared streamable=\"yes\" uses last(), which requires knowing the total count of items",
                    location);
            }

            // Check for descendant axis (crawling)
            if (ContainsDescendantAxis(attr.Select))
            {
                throw new XsltException(
                    "XTSE3430: Attribute set declared streamable=\"yes\" uses descendant axis, which is not streamable",
                    location);
            }
        }
    }

    /// <summary>
    /// Checks that a match pattern in a streamable mode template is motionless.
    /// Per XSLT 3.0 §19.8.5, patterns in streamable mode templates must be motionless:
    /// no positional predicates, no last(), no context item access on element-selecting steps.
    /// </summary>
    public static void CheckStreamablePattern(XsltPattern? pattern, SourceLocation? location)
    {
        if (pattern == null) return;

        var reason = CheckPatternMotionless(pattern);
        if (reason != null)
        {
            throw new XsltException(
                $"XTSE3430: Match pattern in a streamable mode is not motionless: {reason}",
                location);
        }
    }

    private static string? CheckPatternMotionless(XsltPattern pattern)
    {
        switch (pattern)
        {
            case PathPattern pp:
                foreach (var step in pp.Steps)
                {
                    var reason = CheckStepPredicatesMotionless(step.Predicates, step.NodeTest);
                    if (reason != null) return reason;
                }
                return null;

            case DotPattern dp:
                // DotPattern matches any node; predicates accessing '.' are non-motionless
                // on element/document nodes since string value requires child content
                return CheckDotPatternPredicatesMotionless(dp.Predicates);

            case UnionPattern up:
                foreach (var alt in up.Patterns)
                {
                    var r = CheckPatternMotionless(alt);
                    if (r != null) return r;
                }
                return null;

            case ExceptPattern ep:
                return CheckPatternMotionless(ep.Left) ?? CheckPatternMotionless(ep.Right);

            case IntersectPattern ip:
                return CheckPatternMotionless(ip.Left) ?? CheckPatternMotionless(ip.Right);

            default:
                return null;
        }
    }

    private static string? CheckStepPredicatesMotionless(
        List<XQueryExpression> predicates, NodeTest nodeTest)
    {
        // Determine if this step selects element nodes (where '.' accesses child content)
        bool selectsElements = nodeTest switch
        {
            KindTest kt => kt.Kind is XdmNodeKind.Element or XdmNodeKind.None or XdmNodeKind.Document,
            NameTest => true, // NameTest on child axis selects elements
            _ => false
        };

        foreach (var pred in predicates)
        {
            // Positional predicates: numeric literal [1], [2] etc.
            if (pred is IntegerLiteral or DecimalLiteral or DoubleLiteral)
                return "positional predicate is not motionless in streaming";

            // last() anywhere in predicate
            if (ContainsLastFunction(pred))
                return "last() in predicate is not motionless in streaming";

            // position() in predicate
            if (ContainsPositionFunction(pred))
                return "position() in predicate is not motionless in streaming";

            // Context item access in predicate on element-selecting step is non-motionless
            // because accessing '.' on an element requires consuming child text content
            if (selectsElements && ContainsContextItemAccess(pred))
                return "context item access in predicate of element-selecting step is not motionless in streaming";
        }
        return null;
    }

    private static string? CheckDotPatternPredicatesMotionless(IReadOnlyList<XQueryExpression> predicates)
    {
        foreach (var pred in predicates)
        {
            // last() in predicate
            if (ContainsLastFunction(pred))
                return "last() in predicate is not motionless in streaming";

            // position() in predicate
            if (ContainsPositionFunction(pred))
                return "position() in predicate is not motionless in streaming";

            // Context item access ('.') in predicate — non-motionless because
            // the dot pattern can match element nodes where '.' accesses string value
            if (ContainsContextItemAccess(pred))
                return "context item access in predicate of '.' pattern is not motionless in streaming";
        }
        return null;
    }

    private static bool ContainsPositionFunction(XQueryExpression expr)
    {
        var checker = new PositionFunctionDetector();
        checker.Walk(expr);
        return checker.Found;
    }

    private static bool ContainsContextItemAccess(XQueryExpression expr)
    {
        var checker = new ContextItemDetector();
        checker.Walk(expr);
        return checker.Found;
    }

    private sealed class PositionFunctionDetector : XQueryExpressionWalker
    {
        public bool Found { get; private set; }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (Found) return null;
            if (expr.Name.LocalName == "position" && expr.Arguments.Count == 0)
            {
                Found = true;
                return null;
            }
            foreach (var arg in expr.Arguments) Walk(arg);
            return null;
        }
    }

    private sealed class ContextItemDetector : XQueryExpressionWalker
    {
        public bool Found { get; private set; }
        private bool _inMotionlessContext;

        public override object? VisitContextItem(ContextItemExpression expr)
        {
            if (!_inMotionlessContext)
                Found = true;
            return null;
        }

        // instance-of is a type check — doesn't access content
        public override object? VisitInstanceOfExpression(InstanceOfExpression expr)
        {
            if (Found) return null;
            var old = _inMotionlessContext;
            _inMotionlessContext = true;
            Walk(expr.Expression);
            _inMotionlessContext = old;
            return null;
        }

        // Motionless functions: name(), local-name(), namespace-uri(), etc.
        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (Found) return null;
            var localName = expr.Name.LocalName;
            if (localName is "name" or "local-name" or "namespace-uri" or "node-name"
                or "generate-id" or "nilled" or "has-children" or "empty" or "exists"
                or "count" or "boolean" or "not" or "exactly-one" or "zero-or-one" or "one-or-more")
            {
                var old = _inMotionlessContext;
                _inMotionlessContext = true;
                foreach (var arg in expr.Arguments) Walk(arg);
                _inMotionlessContext = old;
                return null;
            }
            foreach (var arg in expr.Arguments) Walk(arg);
            return null;
        }
    }

    private static bool ContainsLastFunction(XQueryExpression expr)
    {
        var checker = new LastFunctionDetector();
        checker.Walk(expr);
        return checker.Found;
    }

    private static bool ContainsDescendantAxis(XQueryExpression expr)
    {
        var checker = new DescendantAxisDetector();
        checker.Walk(expr);
        return checker.Found;
    }

    private sealed class LastFunctionDetector : XQueryExpressionWalker
    {
        public bool Found { get; private set; }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (Found) return null;
            if (expr.Name.LocalName == "last" && expr.Arguments.Count == 0)
            {
                Found = true;
                return null;
            }
            foreach (var arg in expr.Arguments) Walk(arg);
            return null;
        }
    }

    /// <summary>
    /// Checks if a variable captures '.' (the streaming context item) and is then used
    /// with child/descendant navigation inside a loop (for-each/iterate).
    /// This would cause multiple consumption of the streaming content.
    /// </summary>
    private static bool HasStreamingVariableNavigatedInLoop(XsltSequenceConstructor body)
    {
        // Step 1: Find top-level variables that capture '.' ungrounded
        var capturedVarNames = new HashSet<string>();
        foreach (var insn in body.Instructions)
        {
            if (insn is XsltVariableInstruction vi && vi.Select != null)
            {
                if (vi.Select is ContextItemExpression)
                    capturedVarNames.Add(vi.Name.LocalName);
            }
        }
        if (capturedVarNames.Count == 0) return false;

        // Step 2: Check if any for-each/iterate body navigates those variables
        var checker = new VariableNavigationInLoopDetector(capturedVarNames);
        checker.Walk(body);
        return checker.Found;
    }

    /// <summary>
    /// Checks if a template body contains current-group()/current-grouping-key() calls
    /// outside of any xsl:for-each-group. In streaming mode templates, these functions
    /// are not available because group context doesn't propagate through apply-templates.
    /// Also checks AVT expressions in LREs which the main StreamingBodyWalker doesn't visit.
    /// </summary>
    private static bool ContainsCurrentGroupCallOutsideGroup(XsltSequenceConstructor body)
    {
        var detector = new CurrentGroupInBodyDetector();
        detector.Walk(body);
        return detector.Found;
    }

    /// <summary>
    /// Walks XSLT instructions to find current-group()/current-grouping-key() calls
    /// outside of xsl:for-each-group. Visits AVT expressions in LREs.
    /// </summary>
    private sealed class CurrentGroupInBodyDetector : XsltInstructionWalker
    {
        public bool Found { get; private set; }

        private bool ExprContainsCurrentGroup(XQueryExpression? expr)
        {
            if (expr == null || Found) return false;
            var counter = new CurrentGroupCounter();
            counter.Walk(expr);
            return counter.Count > 0;
        }

        private void CheckExpr(XQueryExpression? expr)
        {
            if (Found || expr == null) return;
            if (ExprContainsCurrentGroup(expr))
                Found = true;
        }

        public override object? VisitForEachGroup(XsltForEachGroup insn)
        {
            // current-group() is valid inside xsl:for-each-group — skip the body
            // But still check the select/group-by/group-adjacent expressions
            // (those are outside the group context)
            return null;
        }

        public override object? VisitLiteralResultElement(XsltLiteralResultElement insn)
        {
            if (Found) return null;
            foreach (var avt in insn.Attributes.Values)
            {
                foreach (var part in avt.Parts)
                {
                    if (part is AvtExpression avtExpr)
                        CheckExpr(avtExpr.Expression);
                }
            }
            Walk(insn.Content);
            return null;
        }

        public override object? VisitValueOf(XsltValueOf insn) { CheckExpr(insn.Select); return null; }
        public override object? VisitSequence(XsltSequence insn) { CheckExpr(insn.Select); return null; }
        public override object? VisitCopyOf(XsltCopyOf insn) { CheckExpr(insn.Select); return null; }
        public override object? VisitApplyTemplates(XsltApplyTemplates insn) { CheckExpr(insn.Select); return null; }

        public override object? VisitIf(XsltIf insn)
        {
            CheckExpr(insn.Test);
            Walk(insn.Then);
            return null;
        }

        public override object? VisitElement(XsltElement insn)
        {
            if (Found) return null;
            Walk(insn.Content);
            return null;
        }

        public override object? VisitAttribute(XsltAttribute insn)
        {
            CheckExpr(insn.Select);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitTry(XsltTry insn)
        {
            if (insn.Body != null) Walk(insn.Body);
            foreach (var c in insn.Catches)
            {
                if (c.Body != null) Walk(c.Body);
            }
            return null;
        }

        public override object? VisitSequenceConstructor(XsltSequenceConstructor insn)
        {
            if (Found) return null;
            foreach (var child in insn.Instructions)
                Walk(child);
            return null;
        }

        public override object? VisitNumber(XsltNumber insn) { CheckExpr(insn.Value); return null; }
    }

    /// <summary>
    /// Walks an XSLT instruction tree collecting XPath expressions,
    /// then checks each for non-streamable patterns.
    /// </summary>
    private sealed class StreamingBodyWalker : XsltInstructionWalker
    {
        private readonly Dictionary<QName, XsltAttributeSet>? _attributeSets;
        private readonly Dictionary<(QName Name, int Arity), XsltFunction>? _functions;
        public string? NonStreamableReason { get; private set; }
        // Tracks whether we're inside a for-each/iterate whose select navigates
        // streaming elements (not atomic values). When true, xsl:sequence select="."
        // would return a streaming node reference, which is non-streamable.
        private bool _insideStreamingElementLoop;
        // Tracks whether we're inside an element constructor (LRE, xsl:element, xsl:copy).
        // When true, xsl:sequence select="." is OK because the node gets serialized.
        private bool _insideElementConstructorBody;

        // True when inside xsl:fork. For-each-group inside fork has stricter rules:
        // population must be striding, body can only consume current-group() once total,
        // no sort allowed (XSLT 3.0 §20.3.3).
        private bool _insideFork;
        // True when there's an element constructor between the streaming for-each and the
        // current instruction. Used to determine if fork prongs' xsl:sequence results are
        // consumed by serialization (grounded) or leaked as raw streamed nodes.
        private bool _hasElementConstructorAboveFork;

        public StreamingBodyWalker(
            Dictionary<QName, XsltAttributeSet>? attributeSets = null,
            Dictionary<(QName Name, int Arity), XsltFunction>? functions = null)
        {
            _attributeSets = attributeSets;
            _functions = functions;
        }

        /// <summary>
        /// Checks if use-attribute-sets references any non-streamable attribute sets.
        /// Per XSLT 3.0 §10.2, using a non-streamable attribute set in a streaming context
        /// is XTSE3430. An attribute set without streamable="yes" defaults to non-streamable.
        /// </summary>
        private void CheckUseAttributeSets(List<QName> useAttributeSets)
        {
            if (NonStreamableReason != null || _attributeSets == null || useAttributeSets.Count == 0) return;

            foreach (var name in useAttributeSets)
            {
                if (_attributeSets.TryGetValue(name, out var attrSet) && !attrSet.Streamable)
                {
                    NonStreamableReason = $"use-attribute-sets references non-streamable attribute set '{name.LocalName}'";
                    return;
                }
            }
        }

        private void CheckExpression(XQueryExpression? expr)
        {
            if (expr == null || NonStreamableReason != null) return;

            var checker = new NonStreamableExpressionChecker(_functions);
            checker.Walk(expr);
            if (checker.Reason != null)
                NonStreamableReason = checker.Reason;
        }

        /// <summary>
        /// Checks if a select expression is "crawling" (uses descendant axis).
        /// Instructions like apply-templates and for-each require "striding" input —
        /// descendant axis makes the expression "crawling" which is non-streamable.
        /// </summary>
        private void CheckCrawlingSelect(XQueryExpression? expr, string instruction)
        {
            if (expr == null || NonStreamableReason != null) return;

            if (ContainsDescendantAxis(expr))
            {
                NonStreamableReason = $"{instruction} uses a crawling select expression (descendant axis), which is not streamable";
                return;
            }

            // Union of two striding expressions is crawling (XSLT 3.0 §19.8.8.3):
            // e.g. /BOOKLIST/ITEM | /BOOKLIST/MAGAZINE — both navigate via child axis,
            // and their union interleaves results in document order, requiring buffering.
            if (ContainsStridingUnion(expr))
            {
                NonStreamableReason = $"{instruction} uses a union of striding expressions, which creates crawling — not streamable";
            }
        }

        private static bool ContainsDescendantAxis(XQueryExpression expr)
        {
            var checker = new DescendantAxisDetector();
            checker.Walk(expr);
            return checker.Found;
        }

        private static bool ContainsStridingUnion(XQueryExpression expr)
        {
            var checker = new StridingUnionDetector();
            checker.Walk(expr);
            return checker.Found;
        }

        private static bool ContainsDownwardNavigation(XQueryExpression expr)
        {
            var checker = new DownwardAxisDetector();
            checker.Walk(expr);
            return checker.Found;
        }

        /// <summary>
        /// Checks if a select expression navigates to element nodes (child/descendant steps),
        /// meaning the loop iterates over streaming nodes rather than atomic values.
        /// </summary>
        private static bool SelectNavigatesElements(XQueryExpression expr)
        {
            // Path expressions with child/descendant/self steps navigate elements
            if (expr is PathExpression path)
            {
                foreach (var step in path.Steps)
                {
                    if (step.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf or Axis.Self)
                        return true;
                }
            }
            // A bare context item (select=".") or step expression
            if (expr is ContextItemExpression)
                return true;
            if (expr is StepExpression se && se.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf)
                return true;
            return false;
        }

        /// <summary>
        /// Checks if an instruction tree contains current-group() calls in any expression.
        /// Used to detect higher-order uses where current-group() is accessed from
        /// within a non-streaming focus context (e.g., xsl:copy select="$var").
        /// </summary>
        private static bool InstructionContainsCurrentGroup(XsltInstruction insn)
        {
            var detector = new CurrentGroupInInstructionDetector();
            detector.Walk(insn);
            return detector.Found;
        }

        /// <summary>
        /// Checks if a select expression produces grounded items.
        /// Paths ending with copy-of(), snapshot(), or data() ground their results,
        /// making subsequent group key expressions free to navigate children.
        /// current-group() returns items from the current group which are already
        /// materialized (buffered by the outer for-each-group).
        /// </summary>
        private static bool SelectExpressionGroundsItems(XQueryExpression? expr)
        {
            if (expr == null) return false;

            // Direct grounding function: copy-of(path), snapshot(path), data(path)
            if (expr is FunctionCallExpression fce
                && fce.Name.LocalName is "copy-of" or "snapshot" or "data"
                    or "current-group" or "current-grouping-key")
                return true;

            // SimpleMapExpression: path ! copy-of(), path ! data()
            if (expr is SimpleMapExpression sme)
            {
                if (sme.Right is FunctionCallExpression rightFce
                    && rightFce.Name.LocalName is "copy-of" or "snapshot" or "data")
                    return true;
            }

            // Binary expression: current-group() except ., etc.
            if (expr is BinaryExpression be)
                return SelectExpressionGroundsItems(be.Left);

            return false;
        }

        private static bool PatternHasConsumingPredicates(XsltPattern pattern)
        {
            if (pattern is PathPattern pp)
            {
                foreach (var step in pp.Steps)
                {
                    foreach (var pred in step.Predicates)
                    {
                        if (ContainsDownwardNavigation(pred))
                            return true;
                        // position() in pattern predicates requires counting
                        // siblings — not motionless in streaming context
                        if (ContainsPositionOrLast(pred))
                            return true;
                    }
                }
            }
            else if (pattern is UnionPattern up)
            {
                return up.Patterns.Any(PatternHasConsumingPredicates);
            }
            return false;
        }

        private static bool ContainsPositionOrLast(XQueryExpression expr)
        {
            var checker = new PositionLastDetector();
            checker.Walk(expr);
            return checker.Found;
        }

        /// <summary>
        /// Checks if a for-each body consumes the context item — i.e., accesses
        /// its string value or navigates child/descendant axis from it.
        /// Used to determine if crawling for-each is non-streamable.
        /// </summary>
        private static bool BodyConsumesContext(XsltInstruction? body)
        {
            if (body == null) return false;
            var detector = new BodyConsumptionDetector();
            detector.Walk(body);
            return detector.Consumes;
        }

        /// <summary>
        /// Checks if any single expression in the body uses child axis navigation
        /// multiple times — e.g., count(*) + count(*/*)  which requires traversing
        /// children twice and is not streamable.
        /// </summary>
        private static bool BodyHasMultiConsumingExpression(XsltInstruction? body)
        {
            if (body == null) return false;
            var checker = new MultiConsumingExpressionDetector();
            checker.Walk(body);
            return checker.Found;
        }

        public override object? VisitSequenceConstructor(XsltSequenceConstructor insn)
        {
            foreach (var instruction in insn.Instructions)
            {
                if (NonStreamableReason != null) return null;
                Walk(instruction);
            }
            return null;
        }

        public override object? VisitForEach(XsltForEach insn)
        {
            CheckExpression(insn.Select);
            if (NonStreamableReason != null) return null;

            // xsl:for-each with descendant-axis crawling select is only streamable
            // if the body is motionless (inspection-only). If the body consumes the
            // context item (accesses its string value or navigates child axis), it's
            // not streamable — unbounded crawling requires buffering the entire document.
            // Note: union-of-striding crawling is NOT checked here — it can be streamed
            // with bounded buffering even when the body is consuming.
            if (insn.Select != null && ContainsDescendantAxis(insn.Select))
            {
                if (BodyConsumesContext(insn.Body))
                {
                    NonStreamableReason = "xsl:for-each with crawling select expression (descendant axis) has a consuming body — not streamable";
                    return null;
                }
            }

            // xsl:for-each body: check if any single expression uses
            // child axis navigation multiple times (e.g., count(*) + count(*/*)
            // requires traversing children twice — not streamable).
            if (BodyHasMultiConsumingExpression(insn.Body))
            {
                NonStreamableReason = "xsl:for-each body has an expression with multiple downward selections — not streamable";
                return null;
            }

            // Track if this for-each iterates over streaming elements
            var oldStreamingLoop = _insideStreamingElementLoop;
            var oldHasElemAboveFork = _hasElementConstructorAboveFork;
            if (insn.Select != null && SelectNavigatesElements(insn.Select))
            {
                _insideStreamingElementLoop = true;
                _hasElementConstructorAboveFork = false; // Reset for this loop scope
            }
            Walk(insn.Body);
            _insideStreamingElementLoop = oldStreamingLoop;
            _hasElementConstructorAboveFork = oldHasElemAboveFork;
            return null;
        }

        public override object? VisitForEachGroup(XsltForEachGroup insn)
        {
            CheckExpression(insn.Select);
            CheckExpression(insn.GroupBy);
            CheckExpression(insn.GroupAdjacent);
            if (NonStreamableReason != null) return null;

            // XSLT 3.0 §19.8.8.2: The group-by/group-adjacent expression must be motionless
            // — it cannot navigate into children/descendants of the selected items.
            // E.g. group-adjacent="PRICE/text()" accesses child content → consuming → XTSE3430.
            // Exception: if the select expression grounds items (e.g. record/copy-of()),
            // the items are materialized and any key expression is valid.
            bool selectIsGrounded = SelectExpressionGroundsItems(insn.Select);
            if (!selectIsGrounded && insn.GroupBy != null && ContainsDownwardNavigation(insn.GroupBy))
            {
                NonStreamableReason = "xsl:for-each-group group-by expression navigates into children (not motionless) — not streamable";
                return null;
            }
            if (!selectIsGrounded && insn.GroupAdjacent != null && ContainsDownwardNavigation(insn.GroupAdjacent))
            {
                NonStreamableReason = "xsl:for-each-group group-adjacent expression navigates into children (not motionless) — not streamable";
                return null;
            }

            // XSLT 3.0 §19.8.8.2: The group-starting-with/group-ending-with pattern
            // must be motionless — predicates cannot navigate into children.
            // E.g. group-starting-with="record[foo = 'a']" accesses child foo → XTSE3430.
            if (!selectIsGrounded && insn.GroupStartingWith != null && PatternHasConsumingPredicates(insn.GroupStartingWith))
            {
                NonStreamableReason = "xsl:for-each-group group-starting-with pattern has consuming predicates (not motionless) — not streamable";
                return null;
            }
            if (!selectIsGrounded && insn.GroupEndingWith != null && PatternHasConsumingPredicates(insn.GroupEndingWith))
            {
                NonStreamableReason = "xsl:for-each-group group-ending-with pattern has consuming predicates (not motionless) — not streamable";
                return null;
            }

            // Check if any single expression in the body has multiple current-group() calls.
            // E.g., "count(current-group()), current-group()" has two references in one expression,
            // requiring the group buffer to be materialized twice — not streamable.
            // Note: current-group() in separate instructions or choose branches is OK.
            if (BodyHasMultiCurrentGroupExpression(insn.Body))
            {
                NonStreamableReason = "xsl:for-each-group body has an expression with multiple current-group() calls — not streamable";
                return null;
            }

            // XSLT 3.0 §20.3.3: xsl:for-each-group inside xsl:fork has stricter rules.
            // Each fork branch processes the stream independently with no buffering, so:
            if (_insideFork)
            {
                // 1. Sort is not allowed (would require buffering all groups)
                // Exception: if the population is grounded (copy-of/snapshot), sorting is OK
                if (insn.Sorts is { Count: > 0 } && !selectIsGrounded)
                {
                    NonStreamableReason = "xsl:for-each-group inside xsl:fork cannot have xsl:sort — groups must be processed in input order";
                    return null;
                }

                // 2. Population must be striding (child axis), not crawling (descendant axis)
                if (!selectIsGrounded && insn.Select != null && ContainsDescendantAxis(insn.Select))
                {
                    NonStreamableReason = "xsl:for-each-group inside xsl:fork has crawling population (descendant axis) — not streamable";
                    return null;
                }

                // 3. Total current-group() uses across entire body must be ≤ 1.
                // Outside fork, separate instructions can each use current-group() because
                // the group is buffered. Inside fork, no buffering — only one traversal.
                // Skip when population is grounded — grounded items are materialized.
                if (!selectIsGrounded && BodyHasTotalMultiCurrentGroupUses(insn.Body))
                {
                    NonStreamableReason = "xsl:for-each-group inside xsl:fork uses current-group() multiple times across the body — only one consuming traversal is allowed";
                    return null;
                }

                // 4. Body cannot use both current-group() AND context item navigation
                // in AVTs. The context item (.) is the first item of the group —
                // accessing both . and current-group() means two consumptions.
                if (!selectIsGrounded && BodyAvtUsesCurrentGroupAndContextNavigation(insn.Body))
                {
                    NonStreamableReason = "xsl:for-each-group inside xsl:fork uses both current-group() and context item navigation — not streamable";
                    return null;
                }

                // 5. current-group() path/map with multiple consuming child navigations.
                // E.g., current-group()/(AUTHOR||TITLE) — two child axis reads per member.
                if (!selectIsGrounded && BodyHasCurrentGroupMultiChildPath(insn.Body))
                {
                    NonStreamableReason = "xsl:for-each-group inside xsl:fork uses current-group() with multiple consuming child navigations — not streamable";
                    return null;
                }
            }

            Walk(insn.Body);
            return null;
        }

        private static bool BodyHasMultiCurrentGroupExpression(XsltInstruction? body)
        {
            if (body == null) return false;
            var checker = new MultiCurrentGroupDetector();
            checker.Walk(body);
            return checker.Found;
        }

        /// <summary>
        /// Checks if the body uses current-group() in more than one distinct
        /// instruction/AVT. For fork context only — each separate instruction
        /// that uses current-group() represents a separate consumption.
        /// Multiple uses within a SINGLE expression (e.g., map constructor)
        /// are already checked by BodyHasMultiCurrentGroupExpression.
        /// </summary>
        private static bool BodyHasTotalMultiCurrentGroupUses(XsltInstruction? body)
        {
            if (body == null) return false;
            var detector = new TotalCurrentGroupCounter();
            detector.Walk(body);
            return detector.DistinctExpressionCount > 1;
        }

        /// <summary>
        /// Checks if the body's AVTs use both current-group() and context item navigation.
        /// For fork context: context item is the first item of the group. If AVTs contain
        /// both current-group() and bare name steps like TITLE (= ./TITLE), that's two
        /// consumptions from the stream. Only checks AVT expressions in LREs since those
        /// are the typical pattern (si-fork-954: a="{count(current-group())}" b="{TITLE}").
        /// </summary>
        private static bool BodyAvtUsesCurrentGroupAndContextNavigation(XsltInstruction? body)
        {
            if (body == null) return false;
            var detector = new ForkAvtConsumptionDetector();
            detector.Walk(body);
            return detector.HasCurrentGroup && detector.HasContextNavigation;
        }

        /// <summary>
        /// Checks if any expression in the body uses current-group() in a path/map
        /// where the mapped expression contains multiple consuming child navigations.
        /// E.g., current-group()/(AUTHOR||TITLE) has two child axis reads per member.
        /// </summary>
        private static bool BodyHasCurrentGroupMultiChildPath(XsltInstruction? body)
        {
            if (body == null) return false;
            var detector = new CurrentGroupMultiChildPathDetector();
            detector.Walk(body);
            return detector.Found;
        }

        public override object? VisitIterate(XsltIterate insn)
        {
            CheckExpression(insn.Select);
            if (NonStreamableReason != null) return null;

            // When the select GROUNDS each item to an atomic value — e.g.
            // .//*/name(), //x/string(), descendant::*/data() — the per-iteration
            // context item is a string/atomic, NOT a streaming node. The crawl is
            // consumed by the grounding step (each node atomized as it is seen,
            // motionless per item), so a body that reads '.' (as a map-lookup key)
            // or accumulates a grounded param is streamable. This is the canonical
            // streaming histogram (si-iterate-134/135). Only genuinely consuming
            // bodies over a STREAMING-NODE crawl (.//* with child/descendant
            // navigation of the per-item node) are non-streamable — those retain a
            // non-grounded '.' and are caught below.
            bool selectGroundsItems = insn.Select != null && IterateSelectGroundsItems(insn.Select);

            // Same body checks as for-each: crawling select + consuming body,
            // and multi-consuming expressions in the body.
            if (!selectGroundsItems && insn.Select != null && ContainsDescendantAxis(insn.Select))
            {
                if (BodyConsumesContext(insn.Body))
                {
                    NonStreamableReason = "xsl:iterate with crawling select expression (descendant axis) has a consuming body — not streamable";
                    return null;
                }
            }

            if (BodyHasMultiConsumingExpression(insn.Body))
            {
                NonStreamableReason = "xsl:iterate body has an expression with multiple downward selections — not streamable";
                return null;
            }

            // Check if the iterate retains streaming nodes across iterations:
            // if '.' flows (ungrounded) into xsl:next-iteration/xsl:with-param,
            // the streaming node is retained across iterations — not streamable.
            if (CheckIterateRetainsStreamingNode(insn))
            {
                NonStreamableReason = "xsl:iterate passes the streaming context item (or a variable derived from it) to xsl:next-iteration — the streaming node would be retained across iterations, which is not streamable";
                return null;
            }

            if (insn.OnCompletion != null) Walk(insn.OnCompletion);

            // Track if this iterate processes streaming elements. A select that
            // grounds each item (atomizing crawl like .//*/name()) yields atomic
            // values, not streaming nodes — the body is not a streaming-element loop.
            var oldStreamingLoop = _insideStreamingElementLoop;
            if (!selectGroundsItems && insn.Select != null && SelectNavigatesElements(insn.Select))
                _insideStreamingElementLoop = true;
            Walk(insn.Body);
            _insideStreamingElementLoop = oldStreamingLoop;
            return null;
        }

        /// <summary>
        /// Names of functions that, applied per-node, GROUND the node to an atomic
        /// value (string/QName/number/boolean/id) — consuming the node in place and
        /// producing a motionless grounded result. When an iterate/for-each select
        /// ends in one of these applied per item (e.g. .//*/name()), the crawl is
        /// consumed by the grounding step and the per-item context is atomic, not a
        /// streaming node.
        /// </summary>
        private static readonly HashSet<string> PerItemGroundingFunctions = new()
        {
            "name", "local-name", "namespace-uri", "node-name",
            "string", "data", "number", "boolean",
            "generate-id", "string-length", "normalize-space",
            "copy-of", "snapshot",
        };

        /// <summary>
        /// True when an xsl:iterate/for-each select produces GROUNDED atomic items
        /// per iteration — i.e. it ends in a per-item grounding step such as
        /// name()/string()/data(). Recognised shapes:
        ///   * SimpleMapExpression path-step whose Right is a grounding function
        ///     (e.g. .//*/name(), descendant::*/string()), and
        ///   * a bare grounding FunctionCallExpression (e.g. data(.//*)).
        /// In these cases the per-item context item is an atomic value, not a
        /// streaming node, so a body reading '.' does not consume the stream.
        /// </summary>
        private static bool IterateSelectGroundsItems(XQueryExpression? select)
        {
            if (select == null) return false;

            // path/functioncall() step: .//*/name(), descendant::x/string()
            if (select is SimpleMapExpression sme
                && sme.Right is FunctionCallExpression rightFce
                && PerItemGroundingFunctions.Contains(rightFce.Name.LocalName))
                return true;

            // Direct grounding call over a crawl: data(.//*), copy-of(.//*)
            if (select is FunctionCallExpression fce
                && PerItemGroundingFunctions.Contains(fce.Name.LocalName))
                return true;

            return false;
        }

        /// <summary>
        /// Checks if an xsl:iterate body retains the streaming context item across iterations.
        /// Walks the body to find variables that reference '.' (ungrounded), then checks
        /// if xsl:next-iteration/xsl:with-param references those tainted variables.
        /// Variables grounded by copy-of()/snapshot() are NOT tainted.
        /// </summary>
        private static bool CheckIterateRetainsStreamingNode(XsltIterate insn)
        {
            // If the iterate's select doesn't navigate streaming elements (e.g., tail($words)
            // where $words is xs:string*, or "1 to 200"), then '.' in the body is an atomic
            // value, not a streaming node — retention is safe.
            if (insn.Select != null && !SelectNavigatesElements(insn.Select))
                return false;

            // Step 1: Collect variable definitions and next-iteration with-params from the body
            var collector = new IterateBodyCollector();
            collector.Walk(insn.Body);

            if (collector.NextIterationWithParams.Count == 0)
                return false; // No next-iteration → no retention

            // Step 2: Build taint set — variables whose select contains ungrounded '.'
            var tainted = new HashSet<string>();
            foreach (var (name, expr) in collector.VariableDefinitions)
            {
                if (ExprContainsUngroundedContextItem(expr))
                    tainted.Add(name);
            }

            if (tainted.Count == 0)
                return false; // No tainted variables → safe

            // Step 3: Propagate taint through variable references
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var (name, expr) in collector.VariableDefinitions)
                {
                    if (tainted.Contains(name)) continue;
                    if (ExprReferencesAny(expr, tainted))
                    {
                        tainted.Add(name);
                        changed = true;
                    }
                }
            }

            // Step 4: Check if any with-param references a tainted variable
            foreach (var (_, expr) in collector.NextIterationWithParams)
            {
                if (ExprContainsUngroundedContextItem(expr))
                    return true;
                if (ExprReferencesAny(expr, tainted))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if an expression contains '.' (ContextItemExpression) that is NOT
        /// inside a grounding function (copy-of, snapshot).
        /// </summary>
        private static bool ExprContainsUngroundedContextItem(XQueryExpression? expr)
        {
            if (expr == null) return false;
            var detector = new UngroundedContextItemDetector();
            detector.Walk(expr);
            return detector.Found;
        }

        /// <summary>
        /// Checks if an expression references any variable in the given set.
        /// </summary>
        private static bool ExprReferencesAny(XQueryExpression? expr, HashSet<string> varNames)
        {
            if (expr == null || varNames.Count == 0) return false;
            var detector = new VariableReferenceDetector(varNames);
            detector.Walk(expr);
            return detector.Found;
        }

        public override object? VisitIf(XsltIf insn)
        {
            CheckExpression(insn.Test);
            if (NonStreamableReason != null) return null;
            Walk(insn.Then);
            return null;
        }

        public override object? VisitChoose(XsltChoose insn)
        {
            foreach (var when in insn.When)
            {
                CheckExpression(when.Test);
                if (NonStreamableReason != null) return null;
                Walk(when.Body);
                if (NonStreamableReason != null) return null;
            }
            if (insn.Otherwise != null) Walk(insn.Otherwise);
            return null;
        }

        public override object? VisitValueOf(XsltValueOf insn)
        {
            CheckExpression(insn.Select);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitCopyOf(XsltCopyOf insn)
        {
            CheckExpression(insn.Select);
            return null;
        }

        public override object? VisitSequence(XsltSequence insn)
        {
            CheckExpression(insn.Select);

            // xsl:sequence select="." inside a for-each/iterate that processes streaming
            // elements returns the streaming node reference without copying — non-streamable.
            // Exception: when inside an element constructor (LRE, xsl:element), the node
            // gets serialized as content, so the reference is consumed, not leaked.
            if (NonStreamableReason == null && _insideStreamingElementLoop
                && !_insideElementConstructorBody
                && insn.Select is ContextItemExpression)
            {
                NonStreamableReason = "xsl:sequence select=\".\" inside a streaming loop returns a streaming node reference without copying — use xsl:copy-of instead";
            }

            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitLiteralResultElement(XsltLiteralResultElement insn)
        {
            // Check AVT expressions
            foreach (var avt in insn.Attributes.Values)
            {
                foreach (var part in avt.Parts)
                {
                    if (part is AvtExpression avtExpr)
                        CheckExpression(avtExpr.Expression);
                }
            }

            // XTSE3430: non-streamable attribute set in streaming context
            CheckUseAttributeSets(insn.UseAttributeSets);

            var old = _insideElementConstructorBody;
            _insideElementConstructorBody = true;
            if (_insideStreamingElementLoop)
                _hasElementConstructorAboveFork = true;
            Walk(insn.Content);
            _insideElementConstructorBody = old;
            return null;
        }

        public override object? VisitCopy(XsltCopy insn)
        {
            CheckExpression(insn.Select);
            CheckUseAttributeSets(insn.UseAttributeSets);

            // xsl:copy select="$var" changes the focus to a non-streaming node.
            // If the body uses current-group(), the streaming nodes are consumed from
            // within a non-streaming focus context — a higher-order non-streamable pattern
            // (see W3C bug 29482).
            if (NonStreamableReason == null
                && insn.Select != null && insn.Select is not ContextItemExpression
                && insn.Content != null && InstructionContainsCurrentGroup(insn.Content))
            {
                NonStreamableReason = "xsl:copy with non-context select uses current-group() in its body — the streaming group items are consumed from a non-streaming focus context, which is not streamable";
                return null;
            }

            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitApplyTemplates(XsltApplyTemplates insn)
        {
            // §19.8.1: an instruction processed in 1.0 compatibility mode is roaming/free-ranging
            if (insn.Version is "1.0" or "1")
            {
                NonStreamableReason = "xsl:apply-templates with version=\"1.0\" is roaming and not streamable";
                return null;
            }
            CheckCrawlingSelect(insn.Select, "xsl:apply-templates");
            CheckExpression(insn.Select);
            return null;
        }

        public override object? VisitElement(XsltElement insn)
        {
            CheckUseAttributeSets(insn.UseAttributeSets);
            var old = _insideElementConstructorBody;
            _insideElementConstructorBody = true;
            Walk(insn.Content);
            _insideElementConstructorBody = old;
            return null;
        }

        public override object? VisitAttribute(XsltAttribute insn)
        {
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitTry(XsltTry insn)
        {
            if (insn.Body != null) Walk(insn.Body);
            foreach (var c in insn.Catches)
            {
                if (c.Body != null) Walk(c.Body);
            }
            return null;
        }

        public override object? VisitSourceDocument(XsltSourceDocument insn)
        {
            // Nested source-document: don't check, it has its own streaming context
            return null;
        }

        public override object? VisitNumber(XsltNumber insn)
        {
            // xsl:number without value="" computes position from context (non-motionless)
            if (insn.Value == null)
            {
                NonStreamableReason = "xsl:number without value attribute requires sibling counting, which is not streamable";
                return null;
            }
            CheckExpression(insn.Value);
            CheckExpression(insn.Select);
            return null;
        }

        public override object? VisitResultDocument(XsltResultDocument insn)
        {
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitMessage(XsltMessage insn)
        {
            CheckExpression(insn.Select);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitAssert(XsltAssert insn)
        {
            CheckExpression(insn.Test);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitVariableInstruction(XsltVariableInstruction insn)
        {
            // Per XSLT 3.0 §19.8.6.3, a variable with a grounded result type
            // (atomic, map, array, function) is a "grounding" operation — it can
            // consume the stream via crawling but produces grounded values.
            if (!IsGroundedType(insn.As))
                CheckCrawlingSelect(insn.Select, "xsl:variable");
            CheckExpression(insn.Select);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        private static bool IsGroundedType(XdmSequenceType? seqType)
        {
            if (seqType == null) return false;
            // Only atomic types are guaranteed grounded. Container types (map, array,
            // function) can hold node references, so they're not guaranteed grounded.
            return seqType.ItemType is ItemType.String or ItemType.Boolean or ItemType.Integer
                or ItemType.Decimal or ItemType.Double or ItemType.Float
                or ItemType.Date or ItemType.DateTime or ItemType.Time
                or ItemType.Duration or ItemType.YearMonthDuration or ItemType.DayTimeDuration
                or ItemType.QName or ItemType.AnyUri or ItemType.UntypedAtomic
                or ItemType.AnyAtomicType or ItemType.GYearMonth or ItemType.GYear
                or ItemType.GMonthDay or ItemType.GDay or ItemType.GMonth
                or ItemType.HexBinary or ItemType.Base64Binary;
        }

        public override object? VisitParamInstruction(XsltParamInstruction insn)
        {
            CheckExpression(insn.Select);
            return null;
        }

        public override object? VisitFork(XsltFork insn)
        {
            // XTSE3430: If the fork is inside a streaming for-each but NOT wrapped in an
            // element constructor, fork prongs that return streamed nodes are non-streamable.
            if (NonStreamableReason == null && _insideStreamingElementLoop && !_hasElementConstructorAboveFork)
            {
                foreach (var seq in insn.Sequences)
                {
                    foreach (var instr in seq.Instructions)
                    {
                        if (instr is XsltSequence xseq && xseq.Select != null
                            && (xseq.Select is PhoenixmlDb.XQuery.Ast.StepExpression
                                || xseq.Select is PhoenixmlDb.XQuery.Ast.PathExpression))
                        {
                            NonStreamableReason = "xsl:sequence inside xsl:fork returns streamed nodes without an element wrapper — fork prongs must not pass through streaming node references";
                            break;
                        }
                    }
                    if (NonStreamableReason != null) break;
                }
            }

            foreach (var seq in insn.Sequences)
                Walk(seq);
            var oldInsideFork = _insideFork;
            _insideFork = true;
            foreach (var feg in insn.ForEachGroups)
                Walk(feg);
            _insideFork = oldInsideFork;
            return null;
        }

        public override object? VisitMerge(XsltMerge insn)
        {
            Walk(insn.Action);
            return null;
        }

        public override object? VisitDocument(XsltDocument insn)
        {
            Walk(insn.Content);
            return null;
        }

        public override object? VisitPerformSort(XsltPerformSort insn)
        {
            CheckExpression(insn.Select);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitComment(XsltComment insn)
        {
            CheckExpression(insn.Select);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitProcessingInstruction(XsltProcessingInstruction insn)
        {
            CheckExpression(insn.Select);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitNamespace(XsltNamespace insn)
        {
            CheckExpression(insn.Select);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitMap(XsltMap insn)
        {
            if (insn.Content != null)
            {
                // Implicit fork for xsl:map requires all children to be xsl:map-entry.
                // If any child is not xsl:map-entry (e.g., xsl:if, xsl:choose), implicit fork
                // cannot be applied. If multiple entries then independently consume the stream
                // (e.g., each with outermost(//X)), it's not streamable.
                bool allMapEntries = insn.Content.Instructions.All(i => i is XsltMapEntry);

                if (!allMapEntries)
                {
                    int consumingEntries = CountConsumingMapEntries(insn.Content);
                    if (consumingEntries > 1)
                    {
                        NonStreamableReason = "xsl:map contains non-xsl:map-entry children — " +
                            "implicit fork is required for multiple consuming entries " +
                            "but cannot be applied when children include conditional instructions";
                        return null;
                    }
                }

                Walk(insn.Content);
            }
            return null;
        }

        /// <summary>
        /// Counts xsl:map-entry instructions (recursively through conditionals) that
        /// have consuming select expressions (descendant axis, even inside grounding functions).
        /// </summary>
        private static int CountConsumingMapEntries(XsltSequenceConstructor content)
        {
            int count = 0;
            foreach (var instruction in content.Instructions)
                CountConsumingEntriesIn(instruction, ref count);
            return count;
        }

        private static void CountConsumingEntriesIn(XsltInstruction instruction, ref int count)
        {
            if (instruction is XsltMapEntry entry)
            {
                if (ContainsDescendantAxisIgnoringGrounding(entry.Select))
                    count++;
            }
            else if (instruction is XsltIf ifInsn)
            {
                foreach (var child in ifInsn.Then.Instructions)
                    CountConsumingEntriesIn(child, ref count);
            }
            else if (instruction is XsltChoose choose)
            {
                foreach (var when in choose.When)
                    foreach (var child in when.Body.Instructions)
                        CountConsumingEntriesIn(child, ref count);
                if (choose.Otherwise != null)
                    foreach (var child in choose.Otherwise.Instructions)
                        CountConsumingEntriesIn(child, ref count);
            }
            else if (instruction is XsltForEach forEach)
            {
                if (forEach.Body != null)
                    foreach (var child in forEach.Body.Instructions)
                        CountConsumingEntriesIn(child, ref count);
            }
        }

        /// <summary>
        /// Checks if an expression contains a descendant axis regardless of grounding context.
        /// Unlike ContainsDescendantAxis(), this does NOT suppress for grounding functions
        /// like outermost() — because even grounded expressions consume the document stream.
        /// </summary>
        private static bool ContainsDescendantAxisIgnoringGrounding(XQueryExpression? expr)
        {
            if (expr == null) return false;
            var detector = new RawDescendantDetector();
            detector.Walk(expr);
            return detector.Found;
        }

        public override object? VisitMapEntry(XsltMapEntry insn)
        {
            CheckCrawlingSelect(insn.Select, "xsl:map-entry");
            CheckExpression(insn.Select);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitArray(XsltArray insn)
        {
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitArrayMember(XsltArrayMember insn)
        {
            CheckExpression(insn.Select);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitAnalyzeString(XsltAnalyzeString insn)
        {
            CheckExpression(insn.Select);
            if (insn.MatchingSubstring != null) Walk(insn.MatchingSubstring);
            if (insn.NonMatchingSubstring != null) Walk(insn.NonMatchingSubstring);
            return null;
        }

        public override object? VisitBreak(XsltBreak insn)
        {
            CheckExpression(insn.Select);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitNextIteration(XsltNextIteration insn)
        {
            return null;
        }
    }

    /// <summary>
    /// Walks an XSLT instruction tree for functions with streamability declarations.
    /// </summary>
    private sealed class StreamingFunctionBodyWalker : XsltInstructionWalker
    {
        private readonly string _streamability;
        private readonly List<XsltParam> _parameters;
        private readonly QName? _functionName;
        private bool _insideElementConstructor;
        // Accumulate consuming reference count across ALL expressions in the function body,
        // not just per-expression. Multiple consuming refs across different instructions
        // (e.g., head($param) in xsl:copy + tail($param) in xsl:sequence) still require
        // multiple sweeps.
        private int _totalConsumingRefCount;
        public string? NonStreamableReason { get; private set; }

        public StreamingFunctionBodyWalker(string streamability, List<XsltParam> parameters, QName? functionName = null)
        {
            _streamability = streamability;
            _parameters = parameters;
            _functionName = functionName;
        }

        private void CheckExpression(XQueryExpression? expr, bool isReturnPosition = true)
        {
            if (expr == null || NonStreamableReason != null) return;

            var checker = new FunctionStreamabilityExpressionChecker(_streamability, _parameters, _functionName);
            checker.Walk(expr);
            if (checker.Reason != null)
                NonStreamableReason = checker.Reason;

            // Accumulate consuming references across all expressions in the body.
            _totalConsumingRefCount += checker.FirstParamConsumingRefCount;

            // For absorbing: the first parameter can only be consumed once.
            // Multiple consuming references (filter/path on param) create multiple sweeps.
            // Motionless references (namespace-uri, local-name, exists, has-children) don't count.
            // Check both per-expression count AND accumulated total across all body expressions.
            if (NonStreamableReason == null && _streamability == "absorbing"
                && (checker.FirstParamConsumingRefCount > 1 || _totalConsumingRefCount > 1))
            {
                NonStreamableReason = "the body has multiple consuming references to the streamed parameter, " +
                    "which requires multiple sweeps — not allowed for streamability=\"absorbing\"";
            }

            // For inspection/absorbing/ascent: check if the expression could return
            // the raw first parameter (streaming node) without grounding.
            // Only check at return positions (xsl:sequence, xsl:value-of), not at
            // non-return positions (xsl:copy select, xsl:apply-templates select, test conditions).
            // Exception: recursive ascent functions can return $param in the base case
            // because recursive calls navigate UP (ancestor axis), so $param at the
            // base case is actually an ancestor, not the original streaming node.
            if (isReturnPosition && NonStreamableReason == null && !_insideElementConstructor
                && _parameters.Count > 0 && _streamability is "inspection" or "absorbing" or "ascent")
            {
                if (ExpressionCanReturnFirstParam(expr, _parameters[0].Name.LocalName))
                {
                    // For ascent functions, skip if the expression contains a recursive self-call
                    // (the returned $param is actually an ancestor navigated by recursive calls)
                    bool isRecursiveAscent = _streamability == "ascent" && _functionName != null
                        && ExpressionContainsSelfCall(expr, _functionName.Value);
                    if (!isRecursiveAscent)
                    {
                        NonStreamableReason = $"the body can return the first parameter directly, returning streaming nodes — not allowed for streamability=\"{_streamability}\"";
                    }
                }
            }
        }

        /// <summary>
        /// Checks if an expression can return the first parameter directly
        /// (without grounding via snapshot/copy-of/string/etc).
        /// Traverses if/then/else branches recursively.
        /// </summary>
        private static bool ExpressionCanReturnFirstParam(XQueryExpression expr, string firstParamName)
        {
            // Direct variable reference to first param
            if (expr is VariableReference vr && vr.Name.LocalName == firstParamName)
                return true;

            // if/then/else — check both branches (not the condition)
            if (expr is IfExpression ifExpr)
            {
                if (ExpressionCanReturnFirstParam(ifExpr.Then, firstParamName))
                    return true;
                if (ifExpr.Else != null && ExpressionCanReturnFirstParam(ifExpr.Else, firstParamName))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if an expression contains a call to the given function (self-call).
        /// Used to detect recursive ascent functions where returning $param is valid.
        /// </summary>
        private static bool ExpressionContainsSelfCall(XQueryExpression expr, QName functionName)
        {
            if (expr is FunctionCallExpression fce
                && fce.Name.LocalName == functionName.LocalName
                && fce.Name.Namespace == functionName.Namespace)
                return true;

            if (expr is IfExpression ifExpr)
            {
                if (ExpressionContainsSelfCall(ifExpr.Condition, functionName))
                    return true;
                if (ExpressionContainsSelfCall(ifExpr.Then, functionName))
                    return true;
                if (ifExpr.Else != null && ExpressionContainsSelfCall(ifExpr.Else, functionName))
                    return true;
            }

            return false;
        }

        public override object? VisitElement(XsltElement insn)
        {
            // Inside an element constructor, xsl:sequence values become children, not function returns
            var old = _insideElementConstructor;
            _insideElementConstructor = true;
            if (insn.Content != null) Walk(insn.Content);
            _insideElementConstructor = old;
            return null;
        }

        public override object? VisitSequenceConstructor(XsltSequenceConstructor insn)
        {
            foreach (var instruction in insn.Instructions)
            {
                if (NonStreamableReason != null) return null;
                Walk(instruction);
            }
            return null;
        }

        public override object? VisitSequence(XsltSequence insn)
        {
            CheckExpression(insn.Select);
            return null;
        }

        public override object? VisitCopy(XsltCopy insn)
        {
            // select is not a return position — it selects the context node for copying
            CheckExpression(insn.Select, isReturnPosition: false);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        public override object? VisitCopyOf(XsltCopyOf insn)
        {
            CheckExpression(insn.Select, isReturnPosition: false);
            return null;
        }

        public override object? VisitApplyTemplates(XsltApplyTemplates insn)
        {
            // select is not a return position — it selects nodes for template processing
            CheckExpression(insn.Select, isReturnPosition: false);
            return null;
        }

        public override object? VisitValueOf(XsltValueOf insn)
        {
            CheckExpression(insn.Select);
            return null;
        }

        public override object? VisitIf(XsltIf insn)
        {
            CheckExpression(insn.Test, isReturnPosition: false);
            Walk(insn.Then);
            return null;
        }

        public override object? VisitChoose(XsltChoose insn)
        {
            foreach (var when in insn.When)
            {
                CheckExpression(when.Test, isReturnPosition: false);
                Walk(when.Body);
            }
            if (insn.Otherwise != null) Walk(insn.Otherwise);
            return null;
        }
    }

    /// <summary>
    /// Checks an XPath expression for non-streamable patterns.
    /// Used for expressions within xsl:source-document streamable="yes" bodies
    /// and templates in streamable modes.
    /// </summary>
    private sealed class NonStreamableExpressionChecker : XQueryExpressionWalker
    {
        private readonly Dictionary<(QName Name, int Arity), XsltFunction>? _functions;

        public NonStreamableExpressionChecker(
            Dictionary<(QName Name, int Arity), XsltFunction>? functions = null)
        {
            _functions = functions;
        }

        public string? Reason { get; private set; }

        // Track if we're inside a predicate of a path step
        private bool _inStepPredicate;
        // Track if the predicate's step selects element nodes (where . accesses child content)
        private bool _predicateOnElementStep;
        // Track if we're inside a grounding expression (snapshot, copy-of, etc.)
        private bool _inGroundingContext;
        // Track if we're inside a function that accesses node properties without consuming content
        private bool _inMotionlessFunctionArg;

        /// <summary>
        /// Determines if a step expression selects element nodes (as opposed to text, attribute, etc.).
        /// When true, accessing '.' in predicates requires consuming element content (non-motionless).
        /// For text(), attribute, comment, PI steps, '.' just reads the leaf value (motionless).
        /// </summary>
        private static bool StepSelectsElements(StepExpression step)
        {
            // Attribute axis never selects elements
            if (step.Axis is Axis.Attribute or Axis.Namespace)
                return false;

            // Kind tests for non-element types: text(), comment(), processing-instruction()
            if (step.NodeTest is KindTest kt)
            {
                return kt.Kind is XdmNodeKind.Element or XdmNodeKind.None or XdmNodeKind.Document;
            }

            // NameTest with child/descendant axis → selects elements
            if (step.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf
                or Axis.Self or Axis.Parent or Axis.Ancestor or Axis.AncestorOrSelf)
            {
                return true;
            }

            return false;
        }

        public override object? VisitStepExpression(StepExpression expr)
        {
            if (Reason != null) return null;

            // Check predicates for non-motionless patterns
            if (expr.Predicates.Count > 0 && !_inGroundingContext)
            {
                var oldInPredicate = _inStepPredicate;
                var oldOnElement = _predicateOnElementStep;
                _inStepPredicate = true;
                _predicateOnElementStep = StepSelectsElements(expr);
                foreach (var pred in expr.Predicates)
                {
                    Walk(pred);
                    if (Reason != null) return null;
                }
                _inStepPredicate = oldInPredicate;
                _predicateOnElementStep = oldOnElement;
            }
            return null;
        }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (Reason != null) return null;

            var localName = expr.Name.LocalName;

            // last() in a step predicate is non-motionless
            if (_inStepPredicate && localName == "last" && expr.Arguments.Count == 0)
            {
                Reason = "the predicate uses last(), which requires knowing the total count of items";
                return null;
            }

            // snapshot() and copy-of() ground their results
            if (localName is "snapshot" or "copy-of")
            {
                var oldGrounding = _inGroundingContext;
                _inGroundingContext = true;
                foreach (var arg in expr.Arguments)
                    Walk(arg);
                _inGroundingContext = oldGrounding;
                return null;
            }

            // Functions that access node properties without consuming child content.
            // Passing '.' to these functions is motionless even on element nodes.
            if (localName is "namespace-uri" or "name" or "local-name" or "node-name"
                or "generate-id" or "nilled" or "has-children" or "base-uri"
                or "namespace-uri-for-prefix" or "in-scope-prefixes"
                or "document-uri" or "lang" or "root" or "path"
                or "exists" or "empty" or "count" or "boolean" or "not"
                or "exactly-one" or "zero-or-one" or "one-or-more"
                or "deep-equal" or "current" or "current-group" or "current-grouping-key"
                or "position" or "type-available" or "function-available"
                or "system-property" or "element-available" or "available-system-properties")
            {
                var oldMotionless = _inMotionlessFunctionArg;
                _inMotionlessFunctionArg = true;
                foreach (var arg in expr.Arguments)
                    Walk(arg);
                _inMotionlessFunctionArg = oldMotionless;
                return null;
            }

            // reverse()/innermost() are not grounding — their arguments must not be crawling.
            // Unlike outermost() (which can stream by outputting outermost nodes incrementally),
            // innermost() requires seeing all nodes to determine which are deepest, and
            // reverse() needs all items to reverse.
            if (localName is "reverse" or "innermost"
                && expr.Arguments.Count == 1 && !_inGroundingContext)
            {
                if (ContainsDescendantAxis(expr.Arguments[0]))
                {
                    Reason = $"{localName}() with a crawling argument (descendant axis) is not streamable — {localName}() does not ground its input";
                    return null;
                }
            }

            // Higher-order functions (filter, for-each, fold-left, fold-right, sort)
            // that apply a function argument to streaming items are consuming —
            // the applied function may access child content of each item, which
            // is not safe in a streaming context. If the first argument is a path
            // expression navigating the streaming document, the HOF is non-streamable.
            if (localName is "filter" or "for-each" or "fold-left" or "fold-right" or "sort"
                && expr.Arguments.Count >= 2)
            {
                var firstArg = expr.Arguments[0];
                if (firstArg is PhoenixmlDb.XQuery.Ast.PathExpression)
                {
                    Reason = $"{localName}() applies a function to streaming items — " +
                             $"the applied function may consume child content, making it non-streamable";
                    return null;
                }
            }

            // Check call-site rules for streaming functions
            if (_functions != null)
            {
                var key = (expr.Name, expr.Arguments.Count);
                if (_functions.TryGetValue(key, out var func) && func.Streamability != null)
                {
                    // First argument must not be climbing (parent/ancestor axes make it climbing)
                    if (expr.Arguments.Count > 0 && ContainsClimbingAxis(expr.Arguments[0]))
                    {
                        Reason = $"first argument of function with streamability=\"{func.Streamability}\" " +
                                 $"navigates to parent/ancestor, producing climbing nodes that are not streamable";
                        return null;
                    }

                    // Non-first arguments must not contain streaming expressions (parent/descendant/child axes)
                    if (expr.Arguments.Count > 1)
                    {
                        for (int i = 1; i < expr.Arguments.Count; i++)
                        {
                            if (ContainsStreamingAxis(expr.Arguments[i]))
                            {
                                Reason = $"non-first argument of function with streamability=\"{func.Streamability}\" " +
                                         $"contains a streaming expression (parent/descendant axis), which is not allowed";
                                return null;
                            }
                        }
                    }
                }
            }

            // Walk arguments normally
            foreach (var arg in expr.Arguments)
                Walk(arg);
            return null;
        }

        /// <summary>
        /// Checks if an expression contains parent, ancestor, or descendant axes
        /// (which make it a streaming/climbing/crawling expression, not grounded).
        /// </summary>
        private static bool ContainsStreamingAxis(XQueryExpression expr)
        {
            var detector = new StreamingAxisDetector();
            detector.Walk(expr);
            return detector.Found;
        }

        /// <summary>
        /// Checks if an expression contains parent or ancestor axes only
        /// (which make it a climbing expression — not valid as first arg of streaming function).
        /// </summary>
        private static bool ContainsClimbingAxis(XQueryExpression expr)
        {
            var detector = new ClimbingAxisDetector();
            detector.Walk(expr);
            return detector.Found;
        }

        public override object? VisitPathExpression(PathExpression expr)
        {
            if (Reason != null || _inGroundingContext) return null;

            // Check if the initial expression is a grounding function (snapshot, copy-of).
            // If so, the entire path result is grounded — subsequent steps operate on
            // materialized nodes and don't need streaming checks.
            bool initialIsGrounding = expr.InitialExpression is FunctionCallExpression fce
                && fce.Name.LocalName is "snapshot" or "copy-of";

            if (initialIsGrounding)
            {
                // Walk the grounding function's arguments in grounding context
                var oldGrounding = _inGroundingContext;
                _inGroundingContext = true;
                Walk(expr.InitialExpression!);
                foreach (var step in expr.Steps)
                    Walk(step);
                _inGroundingContext = oldGrounding;
                return null;
            }

            // Check if we're in a step predicate and the path accesses child elements
            if (_inStepPredicate)
            {
                // A path in a predicate that uses child/descendant axis is non-motionless
                // (it accesses child content of the filtered item)
                foreach (var step in expr.Steps)
                {
                    if (step.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf)
                    {
                        // Check it's not an attribute-like access
                        if (step.NodeTest is not KindTest kt ||
                            kt.Kind != XdmNodeKind.Attribute)
                        {
                            Reason = "the predicate accesses child or descendant content, which is non-motionless";
                            return null;
                        }
                    }
                }
            }

            // Check if the initial expression of a path is a composite (union/comma)
            // containing a descendant axis. This creates "mixed posture" or crawling
            // that cannot be streamed. E.g., ($var | //ITEM)/PRICE or ($var, //ITEM)/PRICE
            if (expr.InitialExpression != null && expr.Steps.Count > 0)
            {
                if (expr.InitialExpression is BinaryExpression be
                    && be.Operator is BinaryOperator.Union or BinaryOperator.Intersect or BinaryOperator.Except)
                {
                    if (ContainsCrawlingInComposite(be.Left) || ContainsCrawlingInComposite(be.Right))
                    {
                        Reason = "union/intersect/except expression with descendant axis creates mixed-posture crawling that is not streamable";
                        return null;
                    }

                    // Union of two striding expressions = crawling (§19.8.8.3)
                    if (be.Operator == BinaryOperator.Union
                        && HasChildAxisNavigation(be.Left) && HasChildAxisNavigation(be.Right))
                    {
                        Reason = "union of two striding expressions creates crawling, which is not streamable";
                        return null;
                    }
                }
                else if (expr.InitialExpression is SequenceExpression se)
                {
                    foreach (var item in se.Items)
                    {
                        if (ContainsCrawlingInComposite(item))
                        {
                            Reason = "comma expression with descendant axis creates mixed-posture crawling that is not streamable";
                            return null;
                        }
                    }
                }

                // Array constructor with mixed posture: [$var, //ITEM]?*/PRICE
                // The lookup unwraps the array, creating a mixed-posture sequence
                // with both grounded ($var) and crawling (//ITEM) members.
                var arrayExpr = expr.InitialExpression is LookupExpression le ? le.Base
                              : expr.InitialExpression;
                if (arrayExpr is ArrayConstructor ac && ac.Kind == ArrayConstructorKind.Square)
                {
                    foreach (var member in ac.Members)
                    {
                        if (ContainsCrawlingInComposite(member))
                        {
                            Reason = "array constructor with descendant axis creates mixed-posture crawling that is not streamable";
                            return null;
                        }
                    }
                }
            }

            // Walk initial expression and steps for nested checks
            if (expr.InitialExpression != null)
                Walk(expr.InitialExpression);
            foreach (var step in expr.Steps)
                Walk(step);
            return null;
        }

        /// <summary>
        /// Checks if an expression navigates into the document via child axis,
        /// making it a "striding" expression. Used for union-of-striding = crawling detection.
        /// </summary>
        private static bool HasChildAxisNavigation(XQueryExpression expr)
        {
            if (expr is StepExpression se && se.Axis == Axis.Child)
                return true;
            if (expr is PathExpression pe)
            {
                foreach (var step in pe.Steps)
                    if (step.Axis == Axis.Child)
                        return true;
            }
            return false;
        }

        /// <summary>
        /// Checks if an expression contains a descendant axis (crawling), but NOT inside a grounding function.
        /// Used for detecting mixed-posture composites (union/comma) in path initial expressions.
        /// </summary>
        private static bool ContainsCrawlingInComposite(XQueryExpression expr)
        {
            var detector = new DescendantAxisDetector();
            detector.Walk(expr);
            return detector.Found;
        }

        public override object? VisitContextItem(ContextItemExpression expr)
        {
            if (Reason != null) return null;

            // ContextItemExpression (.) in a predicate on an element-selecting step is non-motionless.
            // Accessing '.' on an element requires consuming its child text content.
            // For text(), attribute, comment, PI steps, '.' just reads the leaf value (motionless).
            if (_inStepPredicate && _predicateOnElementStep && !_inGroundingContext && !_inMotionlessFunctionArg)
            {
                Reason = "the predicate accesses the string value of the context item (.), which is non-motionless";
                return null;
            }

            return null;
        }

        public override object? VisitFilterExpression(FilterExpression expr)
        {
            if (Reason != null) return null;

            // If the primary is a grounding function, predicates operate on grounded results
            bool primaryIsGrounding = expr.Primary is FunctionCallExpression fce
                && fce.Name.LocalName is "snapshot" or "copy-of";

            if (primaryIsGrounding)
            {
                var oldGrounding = _inGroundingContext;
                _inGroundingContext = true;
                Walk(expr.Primary);
                foreach (var pred in expr.Predicates)
                    Walk(pred);
                _inGroundingContext = oldGrounding;
                return null;
            }

            // Walk the primary expression
            Walk(expr.Primary);

            // For filter expression predicates (on non-path expressions like function results),
            // DON'T set _inStepPredicate since the primary is grounded
            // (e.g., tokenize($x)[last()] is fine, current-group()[last()] is fine)
            foreach (var pred in expr.Predicates)
                Walk(pred);

            return null;
        }

        public override object? VisitTreatExpression(TreatExpression expr)
        {
            if (Reason != null || _inGroundingContext) return null;

            // "treat as document-node(element(name))" requires inspecting document children
            // to verify the root element — this is non-streamable on the streaming context
            if (expr.TargetType.ItemType == ItemType.Document
                && expr.TargetType.DocumentElementName != null)
            {
                Reason = "treat as document-node(element(...)) requires inspecting document children, which is not streamable";
                return null;
            }

            Walk(expr.Expression);
            return null;
        }

        public override object? VisitFlworExpression(FlworExpression expr)
        {
            if (Reason != null) return null;

            // Check for clauses: if a for binding uses descendant axis (crawling),
            // the variable binds to streamed nodes requiring random access — not streamable.
            // E.g., "for $x in .//parlist return $x/listitem" requires buffering all
            // descendant parlist nodes to iterate them (XTSE3430).
            foreach (var clause in expr.Clauses)
            {
                if (clause is ForClause fc)
                {
                    foreach (var binding in fc.Bindings)
                    {
                        if (ContainsDescendantAxis(binding.Expression))
                        {
                            Reason = "for expression binds variable to a crawling expression (descendant axis), which is not streamable";
                            return null;
                        }
                        Walk(binding.Expression);
                    }
                }
                else if (clause is LetClause lc)
                {
                    foreach (var binding in lc.Bindings)
                        Walk(binding.Expression);
                }
            }

            // Walk the return expression
            Walk(expr.ReturnExpression);
            return null;
        }
    }

    /// <summary>
    /// Detects descendant or descendant-or-self axis usage in an XPath expression.
    /// Used to identify "crawling" expressions that aren't streamable in contexts
    /// that require "striding" input (apply-templates, for-each).
    /// Wrapping in outermost()/innermost() converts crawling to striding (OK).
    /// </summary>
    private sealed class DescendantAxisDetector : XQueryExpressionWalker
    {
        public bool Found { get; private set; }
        private bool _inGrounding;

        public override object? VisitStepExpression(StepExpression expr)
        {
            if (Found || _inGrounding) return null;

            if (expr.Axis is Axis.Descendant or Axis.DescendantOrSelf)
            {
                Found = true;
                return null;
            }

            // Walk predicates too
            foreach (var pred in expr.Predicates)
                Walk(pred);
            return null;
        }

        public override object? VisitPathExpression(PathExpression expr)
        {
            if (Found || _inGrounding) return null;

            // If the initial expression is a grounding function, all subsequent
            // steps operate on grounded results — no crawling concern
            bool initialIsGrounding = expr.InitialExpression is FunctionCallExpression fce
                && fce.Name.LocalName is "outermost" or "innermost" or "snapshot" or "copy-of";

            if (initialIsGrounding)
            {
                var old = _inGrounding;
                _inGrounding = true;
                Walk(expr.InitialExpression!);
                foreach (var step in expr.Steps)
                    Walk(step);
                _inGrounding = old;
                return null;
            }

            if (expr.InitialExpression != null)
                Walk(expr.InitialExpression);
            foreach (var step in expr.Steps)
            {
                if (Found) return null;
                Walk(step);
            }
            return null;
        }

        public override object? VisitFilterExpression(FilterExpression expr)
        {
            if (Found || _inGrounding) return null;

            // If the primary is a grounding function, predicates operate on grounded results
            bool primaryIsGrounding = expr.Primary is FunctionCallExpression fce
                && fce.Name.LocalName is "outermost" or "innermost" or "snapshot" or "copy-of";

            if (primaryIsGrounding)
            {
                var old = _inGrounding;
                _inGrounding = true;
                Walk(expr.Primary);
                foreach (var pred in expr.Predicates)
                    Walk(pred);
                _inGrounding = old;
                return null;
            }

            Walk(expr.Primary);
            foreach (var pred in expr.Predicates)
                Walk(pred);
            return null;
        }

        public override object? VisitSimpleMapExpression(SimpleMapExpression expr)
        {
            if (Found || _inGrounding) return null;

            // If the right side (mapping function) is a grounding function like copy-of(.),
            // the entire expression produces grounded results regardless of crawling in the left.
            // e.g., .//school/copy-of(.) — descendant axis is OK because each result is grounded.
            bool rightIsGrounding = expr.Right is FunctionCallExpression fce
                && fce.Name.LocalName is "snapshot" or "copy-of";

            if (rightIsGrounding)
            {
                var old = _inGrounding;
                _inGrounding = true;
                Walk(expr.Left);
                Walk(expr.Right);
                _inGrounding = old;
                return null;
            }

            Walk(expr.Left);
            Walk(expr.Right);
            return null;
        }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (Found || _inGrounding) return null;

            var localName = expr.Name.LocalName;
            // snapshot(), copy-of(), and outermost() ground their results — crawling arguments are OK.
            // innermost() requires seeing all nodes to determine deepest, so it does NOT ground.
            if (localName is "outermost" or "snapshot" or "copy-of")
            {
                var old = _inGrounding;
                _inGrounding = true;
                foreach (var arg in expr.Arguments)
                    Walk(arg);
                _inGrounding = old;
                return null;
            }

            foreach (var arg in expr.Arguments)
                Walk(arg);
            return null;
        }
    }

    /// <summary>
    /// Detects parent, ancestor, or descendant axes in an expression — which indicate
    /// the expression accesses streaming content (not grounded). Used for validating
    /// non-first arguments to streaming functions.
    /// </summary>
    private sealed class StreamingAxisDetector : XQueryExpressionWalker
    {
        public bool Found { get; private set; }
        private bool _inGrounding;
        private bool _inPathFromVariable;

        public override object? VisitStepExpression(StepExpression expr)
        {
            if (Found || _inGrounding) return null;

            if (expr.Axis is Axis.Parent or Axis.Ancestor or Axis.AncestorOrSelf
                or Axis.Descendant or Axis.DescendantOrSelf)
            {
                Found = true;
            }
            // Child axis on the implicit context (bare *, child::node(), etc.)
            // is consuming — but only when it's a standalone step (not $var/*)
            else if (!_inPathFromVariable && expr.Axis is Axis.Child
                or Axis.Following or Axis.FollowingSibling
                or Axis.Preceding or Axis.PrecedingSibling or Axis.Namespace)
            {
                Found = true;
            }

            return null;
        }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (Found || _inGrounding) return null;

            // Grounding functions: their arguments operate on grounded results
            if (expr.Name.LocalName is "outermost" or "innermost" or "snapshot" or "copy-of")
            {
                var old = _inGrounding;
                _inGrounding = true;
                foreach (var arg in expr.Arguments)
                    Walk(arg);
                _inGrounding = old;
                return null;
            }

            foreach (var arg in expr.Arguments)
                Walk(arg);
            return null;
        }

        public override object? VisitVariableReference(VariableReference expr)
        {
            // Variable references are grounded — don't flag
            return null;
        }

        public override object? VisitPathExpression(PathExpression expr)
        {
            if (Found || _inGrounding) return null;

            // Walk the initial expression normally
            if (expr.InitialExpression != null)
                Walk(expr.InitialExpression);

            // Steps after a variable/grounding-function initial expression are grounded
            bool stepsGrounded = expr.InitialExpression is VariableReference
                || (expr.InitialExpression is FunctionCallExpression fce
                    && fce.Name.LocalName is "outermost" or "innermost" or "snapshot" or "copy-of");

            if (stepsGrounded)
            {
                var old = _inPathFromVariable;
                _inPathFromVariable = true;
                foreach (var step in expr.Steps)
                    Walk(step);
                _inPathFromVariable = old;
            }
            else
            {
                foreach (var step in expr.Steps)
                    Walk(step);
            }

            return null;
        }
    }

    /// <summary>
    /// Checks XPath expressions in functions with streamability declarations.
    /// Validates that the function body conforms to the rules for its declared streamability category.
    /// </summary>
    private sealed class FunctionStreamabilityExpressionChecker : XQueryExpressionWalker
    {
        private readonly string _streamability;
        private readonly HashSet<string> _paramNames;
        private readonly string? _firstParamName;
        private readonly QName? _functionName;
        public string? Reason { get; private set; }

        // Track whether we're inside a consuming context (string(), data(), [predicate])
        private bool _inConsumingContext;
        // Track whether we're inside a motionless function argument (namespace-uri, local-name, etc.)
        private bool _inMotionlessFunctionArg;
        // Track variable references to first param to detect return-of-streaming-node
        public int FirstParamRefCount { get; private set; }
        // Track consuming references (filter/path on param, not inside motionless functions)
        public int FirstParamConsumingRefCount { get; private set; }
        // Flag to prevent double-counting when $param is walked as part of a path/map
        // that already counted it as consuming
        private bool _paramAlreadyCounted;
        // Track if we're inside a loop body (for/quantified) — param refs in loops count double
        private bool _inLoopContext;

        public FunctionStreamabilityExpressionChecker(string streamability, List<XsltParam> parameters, QName? functionName = null)
        {
            _streamability = streamability;
            _paramNames = new HashSet<string>();
            foreach (var p in parameters)
                _paramNames.Add(p.Name.LocalName);
            _firstParamName = parameters.Count > 0 ? parameters[0].Name.LocalName : null;
            _functionName = functionName;
        }

        public override object? VisitVariableReference(VariableReference expr)
        {
            if (Reason != null) return null;

            var varName = expr.Name.LocalName;
            if (!_paramNames.Contains(varName)) return null;

            bool isFirstParam = varName == _firstParamName;

            if (isFirstParam)
            {
                FirstParamRefCount++;

                // For absorbing functions, any non-motionless reference to the first param
                // is a consuming reference. Motionless functions (exists, empty, count, etc.)
                // don't consume — they inspect properties without reading content.
                // Skip if already counted by path ($param/step) or map ($param!expr) handling.
                if (_streamability == "absorbing" && !_inMotionlessFunctionArg && !_paramAlreadyCounted)
                {
                    FirstParamConsumingRefCount++;
                }

                // For filter/inspection/ascent: consuming the first parameter is not allowed.
                // Consuming = using in a context that reads child content (string(), predicate on content, etc.)
                if (_streamability is "filter" or "inspection" or "ascent" && _inConsumingContext)
                {
                    Reason = $"the body consumes the first parameter, which is not allowed for streamability=\"{_streamability}\"";
                    return null;
                }

                // For shallow-descent: bare $param reference in a loop context means the streaming
                // node would be accessed multiple times (e.g., (1 to 5) ! $n), which is not allowed.
                if (_streamability == "shallow-descent" && _inLoopContext && !_inMotionlessFunctionArg)
                {
                    Reason = "the body references the first parameter inside a loop, which would access the streaming node multiple times";
                    return null;
                }

                // Note: returning the raw first parameter (streaming node) is checked
                // separately via CheckReturnsStreamingNode at the expression level.
            }

            return null;
        }

        public override object? VisitContextItem(ContextItemExpression expr)
        {
            if (Reason != null) return null;

            // In a consuming context (e.g. predicate on $param), context item (.) accesses content
            if (_inConsumingContext && _streamability is "filter" or "inspection" or "ascent")
            {
                Reason = $"the predicate accesses content of the first parameter via '.', which is not allowed for streamability=\"{_streamability}\"";
                return null;
            }

            return null;
        }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (Reason != null) return null;

            var name = expr.Name.LocalName;

            // path() is not streamable in absorbing functions
            if (_streamability == "absorbing" && name == "path" && expr.Arguments.Count <= 1)
            {
                Reason = "path() is not streamable in an absorbing function";
                return null;
            }

            // string(), data(), string-join() are consuming operations on nodes
            if (name is "string" or "data" or "string-join" or "string-length" or "normalize-space"
                && expr.Arguments.Count >= 1)
            {
                // Check if the argument references the first parameter directly
                if (IsFirstParamRef(expr.Arguments[0]))
                {
                    if (_streamability is "filter" or "inspection" or "ascent")
                    {
                        Reason = $"{name}() consumes the first parameter, which is not allowed for streamability=\"{_streamability}\"";
                        return null;
                    }
                }
            }

            // Motionless functions: accessing node properties without consuming content.
            // References to the streamed parameter inside these are NOT consuming.
            if (name is "namespace-uri" or "name" or "local-name" or "node-name"
                or "generate-id" or "nilled" or "has-children" or "base-uri"
                or "exists" or "empty" or "count" or "boolean" or "not"
                or "exactly-one" or "zero-or-one" or "one-or-more")
            {
                var oldMotionless = _inMotionlessFunctionArg;
                _inMotionlessFunctionArg = true;
                foreach (var arg in expr.Arguments)
                    Walk(arg);
                _inMotionlessFunctionArg = oldMotionless;
                return null;
            }

            // Recursive self-call detection: if the function calls itself with an argument
            // derived from the first parameter (e.g., f:count($input/*)), this compounds
            // consuming — each recursive level does an additional sweep of the streaming node.
            if (_functionName != null && _streamability == "absorbing"
                && expr.Name.LocalName == _functionName.Value.LocalName
                && expr.Name.Namespace == _functionName.Value.Namespace)
            {
                // Check if any argument derives from the first parameter
                foreach (var arg in expr.Arguments)
                {
                    if (ArgumentDerivesFromFirstParam(arg))
                    {
                        Reason = "recursive call passes an expression derived from the streaming parameter, " +
                            "causing compounded consuming — not allowed for streamability=\"absorbing\"";
                        return null;
                    }
                }
            }

            // Walk arguments
            foreach (var arg in expr.Arguments)
                Walk(arg);
            return null;
        }

        /// <summary>
        /// Checks if an expression derives from (references) the first streaming parameter.
        /// E.g., $input/*, $input[pred], head($input), tail($input).
        /// </summary>
        private bool ArgumentDerivesFromFirstParam(XQueryExpression expr)
        {
            if (_firstParamName == null) return false;
            if (expr is VariableReference vr)
                return vr.Name.LocalName == _firstParamName;
            if (expr is PathExpression pe)
                return pe.InitialExpression != null && ArgumentDerivesFromFirstParam(pe.InitialExpression);
            if (expr is FilterExpression fe)
                return ArgumentDerivesFromFirstParam(fe.Primary);
            if (expr is FunctionCallExpression fce && fce.Arguments.Count > 0)
            {
                // head($input), tail($input), etc.
                var fnName = fce.Name.LocalName;
                if (fnName is "head" or "tail" or "subsequence" or "remove" or "reverse")
                    return ArgumentDerivesFromFirstParam(fce.Arguments[0]);
            }
            return false;
        }

        public override object? VisitFilterExpression(FilterExpression expr)
        {
            if (Reason != null) return null;

            // Predicates on the first parameter are consuming
            if (IsFirstParamRef(expr.Primary))
            {
                // $param[predicate] — consuming reference (subscribes to sequence with filter)
                // In loop context, this means multiple sweeps.
                // Always counts as consuming, even inside motionless functions — the motionless
                // exemption is about not consuming node content, not about not filtering the sequence.
                FirstParamConsumingRefCount += _inLoopContext ? 2 : 1;

                if (_streamability is "filter" or "inspection" or "ascent")
                {
                    // $param[predicate] — predicate accesses content
                    foreach (var pred in expr.Predicates)
                    {
                        var oldConsuming = _inConsumingContext;
                        _inConsumingContext = true;
                        Walk(pred);
                        _inConsumingContext = oldConsuming;
                    }
                    // Don't walk primary again (already counted)
                    return null;
                }
            }

            // Mark to prevent double-counting by VisitVariableReference
            bool isCountedParam = IsFirstParamRef(expr.Primary);
            if (isCountedParam)
                _paramAlreadyCounted = true;
            Walk(expr.Primary);
            if (isCountedParam)
                _paramAlreadyCounted = false;
            foreach (var pred in expr.Predicates)
                Walk(pred);
            return null;
        }

        public override object? VisitPathExpression(PathExpression expr)
        {
            if (Reason != null) return null;

            // $param/step — path from first param is a consuming reference
            if (expr.InitialExpression != null && expr.Steps.Count > 0
                && IsFirstParamRef(expr.InitialExpression) && !_inMotionlessFunctionArg)
            {
                FirstParamConsumingRefCount++;
            }

            // For shallow-descent: $param//* (descendant axis) is not allowed, only $param/* (child)
            if (_streamability == "shallow-descent" && expr.InitialExpression != null)
            {
                if (IsFirstParamRef(expr.InitialExpression))
                {
                    foreach (var step in expr.Steps)
                    {
                        if (step.Axis is Axis.Descendant or Axis.DescendantOrSelf)
                        {
                            Reason = "the body uses descendant axis on the first parameter, which is not allowed for streamability=\"shallow-descent\" (only child axis is permitted)";
                            return null;
                        }
                    }
                }
            }

            // For filter/inspection/ascent: navigating from $param to children is consuming (not allowed).
            // For filter only: navigating to parent/ancestor returns climbing nodes (not allowed).
            // Inspection and ascent CAN navigate to ancestors.
            if (_streamability is "filter" or "inspection" or "ascent" && expr.InitialExpression != null)
            {
                if (IsFirstParamRef(expr.InitialExpression))
                {
                    foreach (var step in expr.Steps)
                    {
                        if (step.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf)
                        {
                            if (step.NodeTest is not KindTest kt || kt.Kind != XdmNodeKind.Attribute)
                            {
                                Reason = $"the body accesses child/descendant content of the first parameter, which is not allowed for streamability=\"{_streamability}\"";
                                return null;
                            }
                        }
                        // Filter functions cannot navigate to parent/ancestor (returns climbing/streaming nodes)
                        if (_streamability == "filter" && step.Axis is Axis.Parent or Axis.Ancestor or Axis.AncestorOrSelf)
                        {
                            Reason = "the body navigates to parent/ancestor of the first parameter, returning climbing nodes that are not allowed for streamability=\"filter\"";
                            return null;
                        }
                    }
                }
            }

            if (expr.InitialExpression != null)
            {
                // If we already counted this $param/step as consuming, mark to prevent
                // VisitVariableReference from double-counting the $param ref
                bool isCountedParam = IsFirstParamRef(expr.InitialExpression) && expr.Steps.Count > 0;
                if (isCountedParam)
                    _paramAlreadyCounted = true;
                Walk(expr.InitialExpression);
                if (isCountedParam)
                    _paramAlreadyCounted = false;
            }
            foreach (var step in expr.Steps)
                Walk(step);
            return null;
        }

        public override object? VisitSimpleMapExpression(SimpleMapExpression expr)
        {
            if (Reason != null) return null;

            // $param!expr — simple map from first param is consuming
            if (IsFirstParamRef(expr.Left) && !_inMotionlessFunctionArg)
                FirstParamConsumingRefCount++;

            // Mark to prevent double-counting by VisitVariableReference
            bool isCountedParam = IsFirstParamRef(expr.Left);
            if (isCountedParam)
                _paramAlreadyCounted = true;
            Walk(expr.Left);
            if (isCountedParam)
                _paramAlreadyCounted = false;

            // The right side of ! is evaluated once per item from the left side,
            // so it's effectively a loop context (like for $x in left return right)
            var oldLoop = _inLoopContext;
            _inLoopContext = true;
            Walk(expr.Right);
            _inLoopContext = oldLoop;
            return null;
        }

        public override object? VisitFlworExpression(FlworExpression expr)
        {
            if (Reason != null) return null;

            // Check for clauses: if a for binding uses descendant axis (crawling),
            // the variable binds to streamed nodes requiring random access — not streamable.
            // E.g., "for $x in .//parlist return $x/listitem" requires buffering all
            // descendant parlist nodes to iterate them (XTSE3430).
            foreach (var clause in expr.Clauses)
            {
                if (clause is ForClause fc)
                {
                    foreach (var binding in fc.Bindings)
                    {
                        if (ContainsDescendantAxis(binding.Expression))
                        {
                            Reason = "for expression binds variable to a crawling expression (descendant axis), which is not streamable";
                            return null;
                        }
                        Walk(binding.Expression);
                    }
                }
                else if (clause is LetClause lc)
                {
                    foreach (var binding in lc.Bindings)
                        Walk(binding.Expression);
                }
            }

            // Return expression is inside a loop if there's a for clause
            bool hasForClause = expr.Clauses.Any(c => c is ForClause);
            if (hasForClause)
            {
                var oldLoop = _inLoopContext;
                _inLoopContext = true;
                Walk(expr.ReturnExpression);
                _inLoopContext = oldLoop;
            }
            else
            {
                Walk(expr.ReturnExpression);
            }

            return null;
        }

        private bool IsFirstParamRef(XQueryExpression expr)
        {
            return _firstParamName != null
                && expr is VariableReference vr
                && vr.Name.LocalName == _firstParamName;
        }
    }

    /// <summary>
    /// Detects parent/ancestor axes in expressions (climbing posture).
    /// Used to validate first arguments of streaming function calls.
    /// </summary>
    private sealed class ClimbingAxisDetector : XQueryExpressionWalker
    {
        public bool Found { get; private set; }

        public override object? VisitStepExpression(StepExpression expr)
        {
            if (Found) return null;

            if (expr.Axis is Axis.Parent or Axis.Ancestor or Axis.AncestorOrSelf)
                Found = true;

            return null;
        }

        public override object? VisitVariableReference(VariableReference expr) => null;
    }

    /// <summary>
    /// Simple detector that finds descendant/descendant-or-self axis usage
    /// without suppressing for grounding functions. Used for implicit fork detection
    /// where even grounded expressions (outermost(//X)) consume the stream.
    /// </summary>
    private sealed class RawDescendantDetector : XQueryExpressionWalker
    {
        public bool Found { get; private set; }

        public override object? VisitStepExpression(StepExpression expr)
        {
            if (!Found && expr.Axis is Axis.Descendant or Axis.DescendantOrSelf)
                Found = true;
            return null;
        }
    }

    /// <summary>
    /// Detects union expressions where both operands navigate via child axis (striding).
    /// Per XSLT 3.0 §19.8.8.3, union of two striding expressions is crawling.
    /// </summary>
    private sealed class StridingUnionDetector : XQueryExpressionWalker
    {
        public bool Found { get; private set; }

        public override object? VisitBinaryExpression(BinaryExpression expr)
        {
            if (Found) return null;

            if (expr.Operator == BinaryOperator.Union)
            {
                if (HasChildAxisNavigation(expr.Left) && HasChildAxisNavigation(expr.Right))
                {
                    Found = true;
                    return null;
                }
            }

            Walk(expr.Left);
            Walk(expr.Right);
            return null;
        }

        private static bool HasChildAxisNavigation(XQueryExpression expr)
        {
            if (expr is StepExpression se && se.Axis == Axis.Child)
                return true;
            if (expr is PathExpression pe)
            {
                foreach (var step in pe.Steps)
                    if (step.Axis == Axis.Child)
                        return true;
            }
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="body"/> is INSPECTION-ONLY: it never consumes the
    /// matched context node's descendant content — i.e. it does not read the bare
    /// context item's string value and does not navigate child/descendant/
    /// descendant-or-self axis from it. Ancestor/ancestor-or-self/parent/self/
    /// attribute navigation, atomization, and set-ops over those are all allowed.
    /// <para>
    /// This is the soundness gate for Group B non-consuming inspection
    /// subscriptions (<c>outermost(//X)</c> / <c>//X</c> for-each): a body that
    /// passes may be dispatched per match WITHOUT materializing/skipping the
    /// subtree, so the forward pass continues into descendants. Reuses the existing
    /// <see cref="BodyConsumptionDetector"/> (which combines
    /// <see cref="ContextItemDetector"/> + <see cref="DownwardAxisDetector"/>).
    /// </para>
    /// </summary>
    internal static bool IsInspectionOnlyBody(XsltSequenceConstructor? body)
    {
        if (body == null) return false;
        var detector = new BodyConsumptionDetector();
        detector.Walk(body);
        return !detector.Consumes;
    }

    /// <summary>
    /// True when <paramref name="expr"/> is INSPECTION-ONLY as a per-item simple-map
    /// RIGHT expression evaluated against a matched node: no bare-context-item
    /// string-value read and no child/descendant-axis navigation from it. Mirrors
    /// <see cref="IsInspectionOnlyBody"/> for the simple-map (sx-bang) shape, reusing
    /// the same two detectors directly on the expression.
    /// </summary>
    internal static bool IsInspectionOnlyExpression(XQueryExpression? expr)
    {
        if (expr == null) return false;
        var ctxDetector = new ContextItemDetector();
        ctxDetector.Walk(expr);
        if (ctxDetector.Found) return false;
        var downward = new DownwardAxisDetector();
        downward.Walk(expr);
        return !downward.Found;
    }

    /// <summary>
    /// Walks a for-each body (XSLT instructions) and checks if any expression
    /// consumes the context item — i.e., accesses its string value (bare ".")
    /// or navigates child/descendant axis from it.
    /// </summary>
    private sealed class BodyConsumptionDetector : XsltInstructionWalker
    {
        public bool Consumes { get; private set; }

        private void CheckExpr(XQueryExpression? expr)
        {
            if (expr == null || Consumes) return;

            // Check for bare context item access (consuming — accesses string value)
            var ctxDetector = new ContextItemDetector();
            ctxDetector.Walk(expr);
            if (ctxDetector.Found) { Consumes = true; return; }

            // Check for child/descendant axis navigation from context
            if (HasDownwardNavigation(expr)) { Consumes = true; }
        }

        private static bool HasDownwardNavigation(XQueryExpression expr)
        {
            var checker = new DownwardAxisDetector();
            checker.Walk(expr);
            return checker.Found;
        }

        public override object? VisitValueOf(XsltValueOf insn)
        {
            CheckExpr(insn.Select);
            return null;
        }

        public override object? VisitSequence(XsltSequence insn)
        {
            CheckExpr(insn.Select);
            return null;
        }

        public override object? VisitCopyOf(XsltCopyOf insn)
        {
            CheckExpr(insn.Select);
            return null;
        }

        public override object? VisitIf(XsltIf insn)
        {
            if (Consumes) return null;
            CheckExpr(insn.Test);
            if (insn.Then != null) Walk(insn.Then);
            return null;
        }

        public override object? VisitApplyTemplates(XsltApplyTemplates insn)
        {
            CheckExpr(insn.Select);
            return null;
        }

        // HOLE 1: xsl:choose / xsl:when test predicates. The base VisitChoose walks
        // only the when bodies + otherwise, never when.Test — so <xsl:when test="."/>
        // or test="child::*" would be missed and the body misclassified inspection-only.
        public override object? VisitChoose(XsltChoose insn)
        {
            foreach (var when in insn.When)
            {
                if (Consumes) return null;
                CheckExpr(when.Test);
                Walk(when.Body);
            }
            if (insn.Otherwise != null) Walk(insn.Otherwise);
            return null;
        }

        // HOLE 2: literal-result-element AVT attributes (and the element name AVT).
        // The base VisitLiteralResultElement walks only .Content, never the {…}
        // attribute value templates — so <v count="{count(child::*)}"/> would be missed.
        public override object? VisitLiteralResultElement(XsltLiteralResultElement insn)
        {
            // insn.Name is a static QName (LRE names are not AVTs); only the attribute
            // value templates carry embedded {…} expressions that can consume.
            foreach (var attr in insn.Attributes)
            {
                if (Consumes) return null;
                CheckAvt(attr.Value);
            }
            Walk(insn.Content);
            return null;
        }

        // HOLE 3a: xsl:variable select. The base VisitVariableInstruction walks only
        // .Content, never .Select — so <xsl:variable select="*"/> would be missed.
        public override object? VisitVariableInstruction(XsltVariableInstruction insn)
        {
            CheckExpr(insn.Select);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        // HOLE 3b: xsl:attribute select (and its name AVT). The base VisitAttribute
        // walks only .Content, never .Select — so <xsl:attribute select="child::x"/>
        // would be missed. xsl:attribute can compute a value from the matched subtree.
        public override object? VisitAttribute(XsltAttribute insn)
        {
            CheckAvt(insn.Name);
            CheckExpr(insn.Select);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        // xsl:element computes its name via an AVT and may carry consuming content via
        // an AVT name. The base VisitElement walks only .Content; check the name AVT too.
        public override object? VisitElement(XsltElement insn)
        {
            CheckAvt(insn.Name);
            Walk(insn.Content);
            return null;
        }

        // xsl:copy can carry a select (XSLT 4.0 select on copy) and content. The base
        // walks only .Content; a select reading the matched subtree must be checked.
        public override object? VisitCopy(XsltCopy insn)
        {
            CheckExpr(insn.Select);
            if (insn.Content != null) Walk(insn.Content);
            return null;
        }

        // A nested xsl:for-each whose select navigates child/descendant from the
        // matched node consumes the subtree. The base walks only .Body, not .Select.
        public override object? VisitForEach(XsltForEach insn)
        {
            CheckExpr(insn.Select);
            Walk(insn.Body);
            return null;
        }

        // Checks every embedded {…} expression of an attribute value template.
        private void CheckAvt(XsltAttributeValueTemplate? avt)
        {
            if (avt == null || Consumes) return;
            foreach (var part in avt.Parts)
            {
                if (Consumes) return;
                if (part is AvtExpression avtExpr) CheckExpr(avtExpr.Expression);
            }
        }

        public override object? VisitSequenceConstructor(XsltSequenceConstructor insn)
        {
            foreach (var instruction in insn.Instructions)
            {
                if (Consumes) return null;
                Walk(instruction);
            }
            return null;
        }
    }

    /// <summary>
    /// Detects child or descendant axis navigation in an expression.
    /// Unlike DescendantAxisDetector, this also detects child axis steps
    /// (which are "striding" — consume one level of children).
    /// </summary>
    private sealed class DownwardAxisDetector : XQueryExpressionWalker
    {
        public bool Found { get; private set; }

        public override object? VisitStepExpression(StepExpression expr)
        {
            if (Found) return null;
            if (expr.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf)
            {
                Found = true;
                return null;
            }
            foreach (var pred in expr.Predicates) Walk(pred);
            return null;
        }

        public override object? VisitPathExpression(PathExpression expr)
        {
            if (Found) return null;
            if (expr.InitialExpression != null) Walk(expr.InitialExpression);
            foreach (var step in expr.Steps)
            {
                if (Found) return null;
                Walk(step);
            }
            return null;
        }
    }

    /// <summary>
    /// Detects position() or last() function calls in an expression.
    /// </summary>
    private sealed class PositionLastDetector : XQueryExpressionWalker
    {
        public bool Found { get; private set; }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (Found) return null;
            if (expr.Name.LocalName is "position" or "last" && expr.Arguments.Count == 0)
            {
                Found = true;
                return null;
            }
            foreach (var arg in expr.Arguments) Walk(arg);
            return null;
        }
    }

    /// <summary>
    /// Walks a for-each body (XSLT instructions) and checks if any SINGLE expression
    /// uses child/descendant axis navigation multiple times. E.g., count(*) + count(*/*)
    /// requires traversing children twice — not streamable.
    /// </summary>
    private sealed class MultiConsumingExpressionDetector : XsltInstructionWalker
    {
        public bool Found { get; private set; }

        private void CheckExpr(XQueryExpression? expr)
        {
            if (expr == null || Found) return;
            var counter = new ChildAxisCounter();
            counter.Walk(expr);
            if (counter.Count > 1) Found = true;
        }

        public override object? VisitValueOf(XsltValueOf insn)
        {
            CheckExpr(insn.Select);
            return null;
        }

        public override object? VisitSequence(XsltSequence insn)
        {
            CheckExpr(insn.Select);
            return null;
        }

        public override object? VisitCopyOf(XsltCopyOf insn)
        {
            CheckExpr(insn.Select);
            return null;
        }

        public override object? VisitIf(XsltIf insn)
        {
            if (Found) return null;
            CheckExpr(insn.Test);
            if (insn.Then != null) Walk(insn.Then);
            return null;
        }

        public override object? VisitApplyTemplates(XsltApplyTemplates insn)
        {
            CheckExpr(insn.Select);
            return null;
        }

        public override object? VisitSequenceConstructor(XsltSequenceConstructor insn)
        {
            foreach (var instruction in insn.Instructions)
            {
                if (Found) return null;
                Walk(instruction);
            }
            return null;
        }
    }

    /// <summary>
    /// Counts the number of independent child/descendant axis navigations
    /// within a single expression. E.g., count(*) + count(*/*) has 2.
    /// Union/intersect/except operands count as ONE navigation (unified operation).
    /// </summary>
    private sealed class ChildAxisCounter : XQueryExpressionWalker
    {
        public int Count { get; private set; }

        public override object? VisitStepExpression(StepExpression expr)
        {
            if (expr.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf)
                Count++;
            foreach (var pred in expr.Predicates) Walk(pred);
            return null;
        }

        public override object? VisitPathExpression(PathExpression expr)
        {
            // A path like */* only navigates from context ONCE
            // (the second step is relative to the first step's result).
            // Only count the FIRST downward step in a path.
            bool foundFirst = false;
            if (expr.InitialExpression != null)
            {
                if (expr.InitialExpression is StepExpression se
                    && se.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf)
                    foundFirst = true;
                else
                    Walk(expr.InitialExpression);
            }
            if (foundFirst)
                Count++;
            else
            {
                foreach (var step in expr.Steps)
                {
                    if (step.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf)
                    {
                        Count++;
                        break; // Only count once per path
                    }
                }
            }
            return null;
        }

        public override object? VisitBinaryExpression(BinaryExpression expr)
        {
            // Union/intersect/except is a single unified selection operation —
            // the child axis navigations in operands are NOT independent.
            if (expr.Operator is BinaryOperator.Union or BinaryOperator.Intersect or BinaryOperator.Except)
            {
                // Count the entire union as at most ONE child axis navigation
                var leftChecker = new DownwardAxisDetector();
                leftChecker.Walk(expr.Left);
                var rightChecker = new DownwardAxisDetector();
                rightChecker.Walk(expr.Right);
                if (leftChecker.Found || rightChecker.Found)
                    Count++;
                return null;
            }

            // For other operators (arithmetic, comparison, etc.),
            // each operand's navigations are independent
            Walk(expr.Left);
            Walk(expr.Right);
            return null;
        }

        public override object? VisitIfExpression(IfExpression expr)
        {
            // if/then/else — only one branch executes, so take the MAX count
            var thenCounter = new ChildAxisCounter();
            thenCounter.Walk(expr.Then);
            int elseCount = 0;
            if (expr.Else != null)
            {
                var elseCounter = new ChildAxisCounter();
                elseCounter.Walk(expr.Else);
                elseCount = elseCounter.Count;
            }
            Count += Math.Max(thenCounter.Count, elseCount);
            // Also count condition (though unlikely to use child axis)
            Walk(expr.Condition);
            return null;
        }
    }

    /// <summary>
    /// Walks XSLT instructions in a for-each-group body, checking if any single
    /// select expression contains multiple current-group() calls.
    /// </summary>
    private sealed class MultiCurrentGroupDetector : XsltInstructionWalker
    {
        public bool Found { get; private set; }

        private void CheckExpr(XQueryExpression? expr)
        {
            if (expr == null || Found) return;
            var counter = new CurrentGroupCounter();
            counter.Walk(expr);
            if (counter.Count > 1) Found = true;
        }

        public override object? VisitValueOf(XsltValueOf insn) { CheckExpr(insn.Select); return null; }
        public override object? VisitSequence(XsltSequence insn) { CheckExpr(insn.Select); return null; }
        public override object? VisitCopyOf(XsltCopyOf insn) { CheckExpr(insn.Select); return null; }
        public override object? VisitApplyTemplates(XsltApplyTemplates insn) { CheckExpr(insn.Select); return null; }
        public override object? VisitIf(XsltIf insn)
        {
            if (Found) return null;
            CheckExpr(insn.Test);
            if (insn.Then != null) Walk(insn.Then);
            return null;
        }
        public override object? VisitSequenceConstructor(XsltSequenceConstructor insn)
        {
            foreach (var instruction in insn.Instructions)
            {
                if (Found) return null;
                Walk(instruction);
            }
            return null;
        }
    }

    /// <summary>
    /// Counts current-group() calls within a single XQuery expression.
    /// </summary>
    private sealed class CurrentGroupCounter : XQueryExpressionWalker
    {
        public int Count { get; private set; }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (expr.Name.LocalName == "current-group" && expr.Arguments.Count == 0)
                Count++;
            foreach (var arg in expr.Arguments)
                Walk(arg);
            return null;
        }
    }

    /// <summary>
    /// Walks XSLT instructions to detect any current-group() call in any expression.
    /// Unlike CurrentGroupInBodyDetector, this does NOT skip for-each-group bodies —
    /// it detects ALL current-group() calls in the instruction tree.
    /// </summary>
    private sealed class CurrentGroupInInstructionDetector : XsltInstructionWalker
    {
        public bool Found { get; private set; }

        private void CheckExpr(XQueryExpression? expr)
        {
            if (Found || expr == null) return;
            var counter = new CurrentGroupCounter();
            counter.Walk(expr);
            if (counter.Count > 0) Found = true;
        }

        public override object? VisitSequenceConstructor(XsltSequenceConstructor insn)
        {
            foreach (var instruction in insn.Instructions)
            {
                if (Found) return null;
                Walk(instruction);
            }
            return null;
        }

        public override object? VisitValueOf(XsltValueOf insn) { CheckExpr(insn.Select); return null; }
        public override object? VisitSequence(XsltSequence insn) { CheckExpr(insn.Select); return null; }
        public override object? VisitCopyOf(XsltCopyOf insn) { CheckExpr(insn.Select); return null; }

        public override object? VisitIf(XsltIf insn)
        {
            CheckExpr(insn.Test);
            Walk(insn.Then);
            return null;
        }

        public override object? VisitChoose(XsltChoose insn)
        {
            foreach (var when in insn.When)
            {
                if (Found) return null;
                CheckExpr(when.Test);
                Walk(when.Body);
            }
            if (insn.Otherwise != null) Walk(insn.Otherwise);
            return null;
        }

        public override object? VisitLiteralResultElement(XsltLiteralResultElement insn) { Walk(insn.Content); return null; }
        public override object? VisitElement(XsltElement insn) { Walk(insn.Content); return null; }
        public override object? VisitAttribute(XsltAttribute insn) { if (insn.Content != null) Walk(insn.Content); return null; }
        public override object? VisitCopy(XsltCopy insn) { if (insn.Content != null) Walk(insn.Content); return null; }
        public override object? VisitResultDocument(XsltResultDocument insn) { Walk(insn.Content); return null; }
        public override object? VisitMessage(XsltMessage insn) { if (insn.Content != null) Walk(insn.Content); return null; }
        public override object? VisitVariableInstruction(XsltVariableInstruction insn) { CheckExpr(insn.Select); if (insn.Content != null) Walk(insn.Content); return null; }
    }

    /// <summary>
    /// Detects if AVT expressions in a for-each-group body contain both current-group()
    /// and context item navigation (implicit . steps like TITLE = child::TITLE).
    /// Used for fork/for-each-group where both would mean two consumptions.
    /// </summary>
    private sealed class ForkAvtConsumptionDetector : XsltInstructionWalker
    {
        public bool HasCurrentGroup { get; private set; }
        public bool HasContextNavigation { get; private set; }

        private void CheckExprForCurrentGroupAndContext(XQueryExpression? expr)
        {
            if (expr == null) return;
            var cgCounter = new CurrentGroupCounter();
            cgCounter.Walk(expr);
            if (cgCounter.Count > 0) HasCurrentGroup = true;

            // Check for context item navigation: bare steps like TITLE (= child::TITLE)
            // that don't start from current-group() or a variable.
            // A StepExpression at the top level or in a path starting from implicit context.
            var ctxNavDetector = new ImplicitContextNavigationDetector();
            ctxNavDetector.Walk(expr);
            if (ctxNavDetector.Found) HasContextNavigation = true;
        }

        public override object? VisitSequenceConstructor(XsltSequenceConstructor insn)
        {
            foreach (var child in insn.Instructions)
            {
                if (HasCurrentGroup && HasContextNavigation) return null;
                Walk(child);
            }
            return null;
        }

        public override object? VisitLiteralResultElement(XsltLiteralResultElement insn)
        {
            foreach (var attr in insn.Attributes)
            {
                foreach (var part in attr.Value.Parts)
                {
                    if (part is AvtExpression avtExpr)
                        CheckExprForCurrentGroupAndContext(avtExpr.Expression);
                }
            }
            Walk(insn.Content);
            return null;
        }

        public override object? VisitValueOf(XsltValueOf insn) { CheckExprForCurrentGroupAndContext(insn.Select); return null; }
        public override object? VisitSequence(XsltSequence insn) { CheckExprForCurrentGroupAndContext(insn.Select); return null; }
        public override object? VisitCopyOf(XsltCopyOf insn) { CheckExprForCurrentGroupAndContext(insn.Select); return null; }
        public override object? VisitElement(XsltElement insn) { Walk(insn.Content); return null; }
        public override object? VisitAttribute(XsltAttribute insn) { CheckExprForCurrentGroupAndContext(insn.Select); if (insn.Content != null) Walk(insn.Content); return null; }
        public override object? VisitIf(XsltIf insn) { Walk(insn.Then); return null; }
        public override object? VisitChoose(XsltChoose insn)
        {
            foreach (var when in insn.When) Walk(when.Body);
            if (insn.Otherwise != null) Walk(insn.Otherwise);
            return null;
        }
        public override object? VisitCopy(XsltCopy insn) { if (insn.Content != null) Walk(insn.Content); return null; }
        public override object? VisitVariableInstruction(XsltVariableInstruction insn) { CheckExprForCurrentGroupAndContext(insn.Select); if (insn.Content != null) Walk(insn.Content); return null; }
    }

    /// <summary>
    /// Detects if an expression contains implicit context item navigation — bare name steps
    /// like TITLE (= child::TITLE) that don't start from current-group() or a variable.
    /// This represents consumption of the context item in a for-each-group.
    /// </summary>
    private sealed class ImplicitContextNavigationDetector : XQueryExpressionWalker
    {
        public bool Found { get; private set; }

        public override object? VisitStepExpression(StepExpression expr)
        {
            if (Found) return null;
            // A bare step like TITLE at the top-level expression navigates
            // from the implicit context item (consuming). Attribute axis (@CAT)
            // is motionless and doesn't consume — exclude it.
            if (expr.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf)
                Found = true;
            return null;
        }

        public override object? VisitPathExpression(PathExpression expr)
        {
            if (Found) return null;
            // A path like ./TITLE or child::TITLE/... starts from context.
            // InitialExpression is null for bare axis paths like TITLE (= child::TITLE)
            // which also navigate from the implicit context item.
            // Absolute paths (/a/b) also have null InitialExpression — exclude those.
            if (expr.InitialExpression is ContextItemExpression or StepExpression)
                Found = true;
            else if (expr.InitialExpression is null && !expr.IsAbsolute &&
                     expr.Steps.Any(s => s.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf))
                Found = true;
            // Don't walk into paths starting from current-group() — those navigate
            // from the group, not from the context item
            return null;
        }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (Found) return null;
            // Don't walk into function arguments — we only care about top-level navigation
            // Functions like count(current-group()) don't consume the context
            return null;
        }

        public override object? VisitSimpleMapExpression(SimpleMapExpression expr)
        {
            if (Found) return null;
            // Only walk the left side. The right side executes in a new context
            // (each item from the left), so bare paths there don't navigate
            // from the outer context item. E.g., current-group()!PRICE — PRICE
            // navigates from each group member, not from the context item.
            Walk(expr.Left);
            return null;
        }
    }

    /// <summary>
    /// Counts total current-group() uses across all expressions in the instruction tree.
    /// Used for fork/for-each-group streaming analysis where only one consuming
    /// traversal of current-group() is allowed across the entire body.
    /// </summary>
    /// <summary>
    /// Counts distinct instructions/AVTs that contain at least one current-group() call.
    /// Unlike TotalCount of individual calls, this counts how many SEPARATE expressions
    /// use current-group(). Multiple calls within a single expression (e.g., map constructor)
    /// count as one expression — those are already handled by BodyHasMultiCurrentGroupExpression.
    /// </summary>
    private sealed class TotalCurrentGroupCounter : XsltInstructionWalker
    {
        /// <summary>Number of distinct expressions/AVTs containing current-group().</summary>
        public int DistinctExpressionCount { get; private set; }

        private void CheckExprForCurrentGroup(XQueryExpression? expr)
        {
            if (expr == null) return;
            var counter = new CurrentGroupCounter();
            counter.Walk(expr);
            if (counter.Count > 0) DistinctExpressionCount++;
        }

        private void CheckAvtForCurrentGroup(XsltAttributeValueTemplate? avt)
        {
            if (avt == null) return;
            foreach (var part in avt.Parts)
            {
                if (part is AvtExpression avtExpr)
                    CheckExprForCurrentGroup(avtExpr.Expression);
            }
        }

        public override object? VisitSequenceConstructor(XsltSequenceConstructor insn)
        {
            foreach (var child in insn.Instructions) Walk(child);
            return null;
        }

        public override object? VisitValueOf(XsltValueOf insn) { CheckExprForCurrentGroup(insn.Select); return null; }
        public override object? VisitSequence(XsltSequence insn) { CheckExprForCurrentGroup(insn.Select); return null; }
        public override object? VisitCopyOf(XsltCopyOf insn) { CheckExprForCurrentGroup(insn.Select); return null; }

        public override object? VisitIf(XsltIf insn)
        {
            CheckExprForCurrentGroup(insn.Test);
            Walk(insn.Then);
            return null;
        }

        public override object? VisitChoose(XsltChoose insn)
        {
            foreach (var when in insn.When)
            {
                CheckExprForCurrentGroup(when.Test);
                Walk(when.Body);
            }
            if (insn.Otherwise != null) Walk(insn.Otherwise);
            return null;
        }

        public override object? VisitLiteralResultElement(XsltLiteralResultElement insn)
        {
            foreach (var attr in insn.Attributes)
                CheckAvtForCurrentGroup(attr.Value);
            Walk(insn.Content);
            return null;
        }

        public override object? VisitElement(XsltElement insn) { Walk(insn.Content); return null; }
        public override object? VisitAttribute(XsltAttribute insn) { CheckExprForCurrentGroup(insn.Select); if (insn.Content != null) Walk(insn.Content); return null; }
        public override object? VisitCopy(XsltCopy insn) { CheckExprForCurrentGroup(insn.Select); if (insn.Content != null) Walk(insn.Content); return null; }
        public override object? VisitResultDocument(XsltResultDocument insn) { Walk(insn.Content); return null; }
        public override object? VisitMessage(XsltMessage insn) { if (insn.Content != null) Walk(insn.Content); return null; }
        public override object? VisitVariableInstruction(XsltVariableInstruction insn) { CheckExprForCurrentGroup(insn.Select); if (insn.Content != null) Walk(insn.Content); return null; }
        public override object? VisitForEach(XsltForEach insn) { CheckExprForCurrentGroup(insn.Select); if (insn.Body != null) Walk(insn.Body); return null; }
    }

    /// <summary>
    /// Collects variable definitions and xsl:next-iteration with-params from an xsl:iterate body.
    /// </summary>
    private sealed class IterateBodyCollector : XsltInstructionWalker
    {
        public List<(string Name, XQueryExpression? Select)> VariableDefinitions { get; } = new();
        public List<(string Name, XQueryExpression? Select)> NextIterationWithParams { get; } = new();

        public override object? VisitVariableInstruction(XsltVariableInstruction insn)
        {
            VariableDefinitions.Add((insn.Name.LocalName, insn.Select));
            return null;
        }

        public override object? VisitNextIteration(XsltNextIteration insn)
        {
            foreach (var wp in insn.WithParams)
                NextIterationWithParams.Add((wp.Name.LocalName, wp.Select));
            return null;
        }

        public override object? VisitSequenceConstructor(XsltSequenceConstructor insn)
        {
            foreach (var child in insn.Instructions)
                Walk(child);
            return null;
        }

        // Walk into control flow to find nested next-iteration and variables
        public override object? VisitIf(XsltIf insn) { Walk(insn.Then); return null; }
        public override object? VisitChoose(XsltChoose insn)
        {
            foreach (var when in insn.When) Walk(when.Body);
            if (insn.Otherwise != null) Walk(insn.Otherwise);
            return null;
        }
    }

    /// <summary>
    /// Detects ContextItemExpression ('.') that retains a node reference (not atomized).
    /// Function calls on '.' are treated as value extraction (atomization) — the function
    /// produces an atomic result, not a node reference. Only bare '.' or '.' in
    /// sequences/conditionals is considered "retaining" the node.
    /// </summary>
    private sealed class UngroundedContextItemDetector : XQueryExpressionWalker
    {
        public bool Found { get; private set; }
        private bool _inFunctionArg; // inside any function call argument

        public override object? VisitContextItem(ContextItemExpression expr)
        {
            if (!_inFunctionArg)
                Found = true;
            return null;
        }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            if (Found) return null;
            // All function calls extract/transform the value — '.' inside a function
            // call argument is not retained as a node reference.
            var old = _inFunctionArg;
            _inFunctionArg = true;
            foreach (var arg in expr.Arguments) Walk(arg);
            _inFunctionArg = old;
            return null;
        }

        public override object? VisitDynamicFunctionCallExpression(DynamicFunctionCallExpression expr)
        {
            if (Found) return null;
            // Dynamic function calls (e.g., $map(.)) also extract values.
            var old = _inFunctionArg;
            _inFunctionArg = true;
            Walk(expr.FunctionExpression);
            foreach (var arg in expr.Arguments) Walk(arg);
            _inFunctionArg = old;
            return null;
        }

        public override object? VisitStepExpression(StepExpression expr)
        {
            // Step expressions like './@attr' or './child::x' navigate away from '.'
            // — the result is a different node or value, not the context item itself.
            // Don't walk into steps (they don't retain '.').
            return null;
        }
    }

    /// <summary>
    /// Detects if an expression references any variable from a given set of names.
    /// </summary>
    private sealed class VariableReferenceDetector : XQueryExpressionWalker
    {
        private readonly HashSet<string> _varNames;
        public bool Found { get; private set; }

        public VariableReferenceDetector(HashSet<string> varNames)
        {
            _varNames = varNames;
        }

        public override object? VisitVariableReference(VariableReference expr)
        {
            if (_varNames.Contains(expr.Name.LocalName))
                Found = true;
            return null;
        }
    }

    /// <summary>
    /// Walks XSLT instructions to detect if captured streaming variables are used
    /// with child/descendant navigation inside loop constructs (for-each/iterate).
    /// </summary>
    private sealed class VariableNavigationInLoopDetector : XsltInstructionWalker
    {
        private readonly HashSet<string> _capturedVarNames;
        private bool _insideLoop;
        public bool Found { get; private set; }

        public VariableNavigationInLoopDetector(HashSet<string> capturedVarNames)
        {
            _capturedVarNames = capturedVarNames;
        }

        private void CheckExprInLoop(XQueryExpression? expr)
        {
            if (Found || expr == null || !_insideLoop) return;
            // Check if expr uses $captured/child or $captured/descendant navigation
            if (ExprNavigatesCapturedVariable(expr))
                Found = true;
        }

        private bool ExprNavigatesCapturedVariable(XQueryExpression expr)
        {
            // Look for path expressions like $var/*, $var/child::*, $var//*, etc.
            if (expr is PathExpression pe)
            {
                // Check if the initial expression is a captured variable reference
                if (pe.Steps.Count > 0 && pe.InitialExpression is VariableReference vr
                    && _capturedVarNames.Contains(vr.Name.LocalName))
                {
                    // Check if any step navigates children/descendants
                    foreach (var step in pe.Steps)
                    {
                        if (step.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf)
                            return true;
                    }
                }
            }
            // Check sub-expressions recursively
            var detector = new PathFromVariableDetector(_capturedVarNames);
            detector.Walk(expr);
            return detector.Found;
        }

        public override object? VisitForEach(XsltForEach insn)
        {
            if (Found) return null;
            var old = _insideLoop;
            _insideLoop = true;
            Walk(insn.Body);
            _insideLoop = old;
            return null;
        }

        public override object? VisitIterate(XsltIterate insn)
        {
            if (Found) return null;
            var old = _insideLoop;
            _insideLoop = true;
            Walk(insn.Body);
            _insideLoop = old;
            return null;
        }

        public override object? VisitValueOf(XsltValueOf insn) { CheckExprInLoop(insn.Select); return null; }
        public override object? VisitSequence(XsltSequence insn) { CheckExprInLoop(insn.Select); return null; }
        public override object? VisitCopyOf(XsltCopyOf insn) { CheckExprInLoop(insn.Select); return null; }

        public override object? VisitSequenceConstructor(XsltSequenceConstructor insn)
        {
            if (Found) return null;
            foreach (var child in insn.Instructions) Walk(child);
            return null;
        }

        public override object? VisitIf(XsltIf insn)
        {
            CheckExprInLoop(insn.Test);
            Walk(insn.Then);
            return null;
        }

        public override object? VisitChoose(XsltChoose insn)
        {
            foreach (var when in insn.When)
            {
                CheckExprInLoop(when.Test);
                Walk(when.Body);
            }
            if (insn.Otherwise != null) Walk(insn.Otherwise);
            return null;
        }

        public override object? VisitLiteralResultElement(XsltLiteralResultElement insn)
        {
            if (Found) return null;
            // Check AVT attributes for variable navigation
            if (_insideLoop)
            {
                foreach (var avt in insn.Attributes.Values)
                {
                    foreach (var part in avt.Parts)
                    {
                        if (part is AvtExpression avtExpr)
                            CheckExprInLoop(avtExpr.Expression);
                    }
                }
            }
            Walk(insn.Content);
            return null;
        }
    }

    /// <summary>
    /// Detects path expressions that navigate children/descendants from a captured variable.
    /// E.g., $var/*, $var/child::name, $var/descendant::*, count($var/*), etc.
    /// </summary>
    private sealed class PathFromVariableDetector : XQueryExpressionWalker
    {
        private readonly HashSet<string> _capturedVarNames;
        public bool Found { get; private set; }

        public PathFromVariableDetector(HashSet<string> capturedVarNames)
        {
            _capturedVarNames = capturedVarNames;
        }

        public override object? VisitPathExpression(PathExpression expr)
        {
            if (Found) return null;
            // Check if path starts from a captured variable
            if (expr.InitialExpression is VariableReference vr
                && _capturedVarNames.Contains(vr.Name.LocalName))
            {
                foreach (var step in expr.Steps)
                {
                    if (step.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf)
                    {
                        Found = true;
                        return null;
                    }
                }
            }
            // Walk sub-expressions
            if (expr.InitialExpression != null) Walk(expr.InitialExpression);
            foreach (var step in expr.Steps)
            {
                foreach (var pred in step.Predicates) Walk(pred);
            }
            return null;
        }

        public override object? VisitFilterExpression(FilterExpression expr)
        {
            if (Found) return null;
            Walk(expr.Primary);
            foreach (var pred in expr.Predicates) Walk(pred);
            return null;
        }
    }

    /// <summary>
    /// Walks XSLT instructions looking for expressions where current-group()
    /// feeds into a path/map and the mapped expression has multiple consuming
    /// child navigations. E.g., current-group()/(AUTHOR||TITLE).
    /// </summary>
    private sealed class CurrentGroupMultiChildPathDetector : XsltInstructionWalker
    {
        public bool Found { get; private set; }

        private void CheckExpr(XQueryExpression? expr)
        {
            if (Found || expr == null) return;
            var checker = new CurrentGroupMultiChildExprChecker();
            checker.Walk(expr);
            if (checker.Found) Found = true;
        }

        public override object? VisitSequenceConstructor(XsltSequenceConstructor insn)
        {
            foreach (var child in insn.Instructions) { if (Found) return null; Walk(child); }
            return null;
        }
        public override object? VisitLiteralResultElement(XsltLiteralResultElement insn)
        {
            foreach (var attr in insn.Attributes)
                foreach (var part in attr.Value.Parts)
                    if (part is AvtExpression avtExpr) CheckExpr(avtExpr.Expression);
            Walk(insn.Content);
            return null;
        }
        public override object? VisitValueOf(XsltValueOf insn) { CheckExpr(insn.Select); return null; }
        public override object? VisitSequence(XsltSequence insn) { CheckExpr(insn.Select); return null; }
        public override object? VisitCopyOf(XsltCopyOf insn) { CheckExpr(insn.Select); return null; }
        public override object? VisitElement(XsltElement insn) { Walk(insn.Content); return null; }
        public override object? VisitAttribute(XsltAttribute insn) { CheckExpr(insn.Select); if (insn.Content != null) Walk(insn.Content); return null; }
        public override object? VisitIf(XsltIf insn) { Walk(insn.Then); return null; }
        public override object? VisitChoose(XsltChoose insn)
        {
            foreach (var when in insn.When) Walk(when.Body);
            if (insn.Otherwise != null) Walk(insn.Otherwise);
            return null;
        }
        public override object? VisitCopy(XsltCopy insn) { if (insn.Content != null) Walk(insn.Content); return null; }
        public override object? VisitVariableInstruction(XsltVariableInstruction insn) { CheckExpr(insn.Select); if (insn.Content != null) Walk(insn.Content); return null; }
    }

    /// <summary>
    /// Checks if an expression contains a current-group() path/map where the
    /// mapped expression has multiple consuming child navigations.
    /// E.g., current-group()/(AUTHOR||TITLE) → SimpleMapExpression with
    /// StringConcatExpression containing two child paths.
    /// </summary>
    private sealed class CurrentGroupMultiChildExprChecker : XQueryExpressionWalker
    {
        public bool Found { get; private set; }

        public override object? VisitSimpleMapExpression(SimpleMapExpression expr)
        {
            if (Found) return null;
            // Check if left side contains current-group()
            var cgCounter = new CurrentGroupCounter();
            cgCounter.Walk(expr.Left);
            if (cgCounter.Count > 0)
            {
                // Count consuming child navigations in the right side
                var counter = new ConsumingChildNavigationCounter();
                counter.Walk(expr.Right);
                if (counter.Count > 1) Found = true;
            }
            // Don't walk further — we've checked this SimpleMapExpression
            return null;
        }

        public override object? VisitPathExpression(PathExpression expr)
        {
            if (Found) return null;
            // Check if InitialExpression contains current-group() and
            // steps have multiple consuming navigations
            if (expr.InitialExpression != null)
            {
                var cgCounter = new CurrentGroupCounter();
                cgCounter.Walk(expr.InitialExpression);
                if (cgCounter.Count > 0 && expr.Steps.Count(s => s.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf) > 1)
                    Found = true;
            }
            return null;
        }
    }

    /// <summary>
    /// Counts distinct consuming (child/descendant) axis navigations
    /// from the implicit context in an expression.
    /// </summary>
    private sealed class ConsumingChildNavigationCounter : XQueryExpressionWalker
    {
        public int Count { get; private set; }

        public override object? VisitStepExpression(StepExpression expr)
        {
            if (expr.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf)
                Count++;
            return null;
        }

        public override object? VisitPathExpression(PathExpression expr)
        {
            // A path from implicit context with consuming steps counts as one navigation
            if (expr.InitialExpression is null && !expr.IsAbsolute &&
                expr.Steps.Any(s => s.Axis is Axis.Child or Axis.Descendant or Axis.DescendantOrSelf))
                Count++;
            // Don't walk into steps — the path as a whole is one navigation
            return null;
        }

        public override object? VisitFunctionCallExpression(FunctionCallExpression expr)
        {
            // Walk into function arguments to find navigations
            foreach (var arg in expr.Arguments) Walk(arg);
            return null;
        }

        public override object? VisitSimpleMapExpression(SimpleMapExpression expr)
        {
            // Only count left side — right side has different context
            Walk(expr.Left);
            return null;
        }
    }
}
