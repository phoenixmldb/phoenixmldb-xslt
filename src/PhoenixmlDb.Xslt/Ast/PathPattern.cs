using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Path pattern (e.g., "para", "chapter/title", "/").
/// </summary>
public sealed class PathPattern : XsltPattern
{
    public required IReadOnlyList<PatternStep> Steps { get; init; }

    /// <summary>
    /// When true, this path came from a parenthesized pattern such as <c>(*/a)</c>. A
    /// parenthesized pattern is evaluated as an expression rooted in the tree, so the
    /// "child-or-top" rule (XSLT 3.0 §5.5.3 — which lets a parentless node vacuously satisfy
    /// leading ancestor steps of <c>A/B</c>) does NOT apply: every ancestor step must match a
    /// real ancestor. See W3C match-215.
    /// </summary>
    public bool DisableChildOrTop { get; init; }

    /// <summary>
    /// Default priority per XSLT 3.0 section 6.5:
    /// - Multi-step patterns (a/b, a//b): 0.5
    /// - Patterns starting with // (e.g., //*): 0.5
    /// - Patterns with predicates: 0.5
    /// - node(), text(), comment(), processing-instruction(): -0.5
    /// - * (wildcard): -0.5
    /// - ns:* (namespace wildcard): -0.25
    /// - foo (specific name test): 0
    /// - Single "/" (document root): -0.5
    /// </summary>
    public override double DefaultPriority
    {
        get
        {
            // Multi-step patterns always get 0.5
            if (Steps.Count > 1)
                return 0.5;

            if (Steps.Count == 0)
                return -0.5;

            var step = Steps[0];

            // Patterns starting with // (DescendantSeparator on first step) get 0.5
            // Per XSLT 3.0 spec 6.5: "If the pattern has the form //S [...] its priority is 0.5"
            if (step.DescendantSeparator)
                return 0.5;

            // Predicates → 0.5
            if (step.Predicates.Count > 0)
                return 0.5;

            return step.NodeTest switch
            {
                // KindTest with specific name: element(name) or attribute(name) → 0
                KindTest kt when kt.Name is { LocalName: not "*" } => 0,
                // KindTest with type only: element(*, type) or attribute(*, type) → -0.25
                KindTest kt when kt.TypeName != null => -0.25,
                // KindTest: node(), text(), comment(), element(), attribute(), etc.
                KindTest => -0.5,
                // NameTest with specific local name AND specific (or no) namespace → 0
                NameTest nt when nt.LocalName != "*" && nt.NamespaceUri != "*" => 0,
                // NameTest with wildcard local name but specific namespace (ns:*) → -0.25
                NameTest nt when nt.LocalName == "*" && nt.NamespaceUri is not null and not "*" => -0.25,
                // NameTest with wildcard namespace but specific local name (*:NCName) → -0.25
                NameTest nt when nt.NamespaceUri == "*" && nt.LocalName != "*" => -0.25,
                // NameTest with just * → -0.5
                NameTest => -0.5,
                _ => -0.5
            };
        }
    }

    public override string ToString()
    {
        return string.Join("/", Steps.Select(s =>
        {
            var prefix = s.DescendantSeparator ? "/" : "";
            var test = s.NodeTest switch
            {
                NameTest nt => nt.LocalName == "*" ? "*" : nt.LocalName,
                KindTest kt => kt.Kind switch
                {
                    XdmNodeKind.Document => "/",
                    XdmNodeKind.None => "node()",
                    XdmNodeKind.Text => "text()",
                    XdmNodeKind.Comment => "comment()",
                    XdmNodeKind.ProcessingInstruction => "processing-instruction()",
                    XdmNodeKind.Element when kt.Name?.LocalName != null => kt.Name.LocalName,
                    XdmNodeKind.Attribute when kt.Name?.LocalName != null => $"@{kt.Name.LocalName}",
                    _ => $"{kt.Kind}()"
                },
                _ => "?"
            };
            var axis = s.Axis == Axis.Attribute ? "@" : "";
            return $"{prefix}{axis}{test}";
        }));
    }

    public override bool Matches(object node, XsltContext context)
    {
        if (Steps.Count == 0)
            return false;

        // Store the original matched node for current() in pattern predicates
        // Per XSLT 3.0 spec: "current() refers to the node that is being matched by the pattern"
        context.MatchedNode = node;

        // Single-step patterns (most common)
        var lastStep = Steps[^1];

        // root() pattern step: matches any root node (a node with no parent — document
        // nodes and free-standing parentless elements). See W3C match-233.
        if (Steps.Count == 1 && lastStep.IsRootFunction)
        {
            bool isRoot = node switch
            {
                XdmDocument => true,
                XdmNode xn => xn.Parent is not { } pid || pid == NodeId.None,
                _ => false
            };
            if (!isRoot)
                return false;
            return lastStep.Predicates.Count == 0 || EvaluatePredicates(lastStep, node, context);
        }

        // Match "/" (document root) — Steps contains a single step with axis self and KindTest for Document
        if (Steps.Count == 1 && lastStep.NodeTest is KindTest kt)
        {
            // A document node is only ever matched on the self axis (patterns "/" or a
            // lone "document-node()"). "child::document-node()" uses the child axis and
            // must never match, since a document node is never anyone's child (match-048).
            if (kt.Kind == XdmNodeKind.Document && lastStep.Axis == Axis.Child)
                return false;
            if (kt.Kind == XdmNodeKind.Document && node is XdmDocument doc)
            {
                // document-node(element(E)) — the document element must match first.
                if (kt.DocumentElementTest != null
                    && !MatchesDocumentElementTest(doc, kt.DocumentElementTest, context))
                    return false;
                // …and, as for root()/node() above, any predicates on the step
                // (match="document-node()[pred]") MUST still be evaluated. Both
                // returns here previously short-circuited on kind alone, silently
                // dropping the predicate.
                return lastStep.Predicates.Count == 0 || EvaluatePredicates(lastStep, node, context);
            }
            // node() on child axis doesn't match attributes (attributes are on the attribute axis)
            // node() on self axis matches any node except documents
            if (kt.Kind == XdmNodeKind.None)
            {
                // A bare node() kind test matches by node kind, but any predicates on
                // the step (e.g. match="node()[self::x]") MUST still be evaluated. The
                // previous predicate-less short-circuit dropped them, so node()[pred]
                // behaved like bare node() and matched nodes the predicate should reject
                // — then, at higher priority, wrongly pre-empted more specific templates.
                // Mirrors the root() handling above. Reported via XSpec gather-specs.xsl
                // (match="node()[x:is-user-content(.)]").
                bool kindMatches = lastStep.Axis == Axis.Child
                    ? node is XdmNode and not XdmDocument and not XdmAttribute
                    : node is XdmNode and not XdmDocument;
                if (!kindMatches)
                    return false;
                return lastStep.Predicates.Count == 0 || EvaluatePredicates(lastStep, node, context);
            }
        }

        // Match last step against the node itself
        if (!MatchesStep(lastStep, node))
            return false;

        // Evaluate predicates on the last step.
        // For multi-step patterns with descendant axis, defer predicate evaluation
        // until after finding the matching ancestor, so position() is relative to
        // all descendants of that ancestor (not just siblings).
        bool deferLastStepPredicates = Steps.Count > 1 && lastStep.Predicates.Count > 0
            && lastStep.Axis is Axis.Descendant or Axis.DescendantOrSelf;

        if (lastStep.Predicates.Count > 0 && !deferLastStepPredicates && !EvaluatePredicates(lastStep, node, context))
            return false;

        if (Steps.Count == 1)
        {
            // If the single step has DescendantSeparator (pattern "//S"), the node
            // must be in a tree rooted at a document node per XSLT 3.0 §5.5.3.
            if (lastStep.DescendantSeparator && node is XdmNode singleNode && context.NodeResolver != null)
            {
                XdmNode n = singleNode;
                while (n.Parent is { } pid && pid != NodeId.None)
                {
                    var parent = context.NodeResolver(pid);
                    if (parent == null) return false;
                    n = parent;
                }
                if (n is not XdmDocument)
                    return false;
            }
            return true;
        }

        // Multi-step patterns: walk up the tree right-to-left
        if (node is not XdmNode currentNode || context.NodeResolver == null)
            return false;

        return MatchAncestorSteps(currentNode, Steps.Count - 2, context,
            deferLastStepPredicates ? (currentNode, lastStep) : null);
    }

    /// <summary>
    /// Recursively matches remaining steps (right-to-left) by walking up the ancestor axis.
    /// </summary>
    private bool MatchAncestorSteps(XdmNode currentNode, int stepIndex, XsltContext context,
        (XdmNode originalNode, PatternStep step)? deferredDescendantPredicate = null)
    {
        if (stepIndex < 0)
        {
            // All ancestor steps matched. If we deferred the last step's predicates
            // (descendant axis), evaluate them now with position relative to this ancestor.
            if (deferredDescendantPredicate is var (origNode, defStep))
            {
                var savedAncestor = context.DescendantPositionAncestor;
                context.DescendantPositionAncestor = currentNode;
                try
                {
                    return EvaluatePredicates(defStep, origNode, context);
                }
                finally
                {
                    context.DescendantPositionAncestor = savedAncestor;
                }
            }
            return true;
        }

        var step = Steps[stepIndex];
        var nextStep = Steps[stepIndex + 1];

        // Get parent node
        var parentId = currentNode.Parent;
        if (parentId is null || parentId.Value == NodeId.None)
        {
            // A parenthesized pattern is an expression rooted in the tree: it never gets
            // the child-or-top escape, so a missing ancestor is a definite non-match (match-215).
            if (DisableChildOrTop)
                return false;

            // We have run out of ancestors while an ancestor step (Steps[stepIndex]) is still
            // unmatched. Per XSLT 3.0 §5.5.3 a relative path pattern A/B matches N iff N matches B
            // AND N's parent matches A — equivalently N is selected by root(N)/descendant-or-self::
            // node()/A/B.
            var firstStep = Steps[0];
            if (firstStep.Axis == Axis.Self && firstStep.NodeTest is KindTest { Kind: XdmNodeKind.Document })
                return false; // Absolute pattern — needs document root
            if (firstStep.DescendantSeparator)
                return false; // //B pattern — needs document root

            if (deferredDescendantPredicate is var (origNodeTop, defStepTop))
            {
                var savedAncestorTop = context.DescendantPositionAncestor;
                context.DescendantPositionAncestor = currentNode;
                try { return EvaluatePredicates(defStepTop, origNodeTop, context); }
                finally { context.DescendantPositionAncestor = savedAncestorTop; }
            }

            // The node has no reachable parent for the still-unmatched ancestor step. Two cases:
            //  • An ELEMENT (or document) that is genuinely rooted here — a free-standing
            //    constructed element / temporary-tree root. Its ancestors really do not exist, so
            //    it does NOT match a multi-step pattern: <x/> passed to `match="ITEM/*"` must not
            //    be selected (sx-square-array-018/019, sx-union-018).
            //  • An ATTRIBUTE. In XDM an attribute always has an owner element, so a null parent
            //    here means the owner was not threaded onto the node — e.g. a streaming pass that
            //    materialises the attribute detached from its element (si-apply-templates-008,
            //    `match="w/@id"`). The ancestor step cannot be verified, so match optimistically;
            //    the streaming dispatch only offered this attribute because its owner was in scope.
            // (currentNode is the still-unmatched node; an attribute only ever appears here as the
            // original matched node — attributes are leaves, so the ancestor walk never climbs to
            // one.)
            return currentNode is XdmAttribute;
        }

        var parent = context.NodeResolver!(parentId.Value);
        if (parent == null)
            return false;

        // descendant/descendant-or-self axis on the next step means the current node
        // can be at any depth under the parent step, similar to '//' separator
        var walkAncestors = nextStep.DescendantSeparator
            || nextStep.Axis is Axis.Descendant or Axis.DescendantOrSelf;

        if (walkAncestors)
        {
            // Walk up any number of ancestors to find a match
            var ancestor = parent;
            while (ancestor != null)
            {
                if (MatchesStep(step, ancestor) && EvaluatePredicates(step, ancestor, context) && MatchAncestorSteps(ancestor, stepIndex - 1, context, deferredDescendantPredicate))
                    return true;

                var aParentId = ancestor.Parent;
                if (aParentId is null || aParentId.Value == NodeId.None)
                    break;
                ancestor = context.NodeResolver(aParentId.Value);
            }
            return false;
        }
        else
        {
            // '/' separator: must match direct parent
            if (!MatchesStep(step, parent) || !EvaluatePredicates(step, parent, context))
                return false;

            return MatchAncestorSteps(parent, stepIndex - 1, context, deferredDescendantPredicate);
        }
    }

    private static bool EvaluatePredicates(PatternStep step, object node, XsltContext context)
    {
        if (step.Predicates.Count == 0)
            return true;

        if (context.PredicateEvaluator == null)
            return true; // No evaluator available, skip predicate check

        // Two or more predicates require true XPath filter semantics: each predicate is
        // applied to the sequence that survived the earlier predicates, so position()/last()
        // in a later predicate re-index against the survivors. Delegate to the sequence-aware
        // evaluator when one is wired (non-streaming match context); otherwise fall through to
        // the single-position AND behaviour below (streaming accumulator context, or no
        // node store — chained positional predicates are out of scope there).
        if (step.Predicates.Count >= 2 && context.SequencePredicateEvaluator != null)
        {
            return context.SequencePredicateEvaluator(node, step.Predicates, step.NodeTest, context.DescendantPositionAncestor);
        }

        // Compute position and size for predicates like [2] or [position() mod 2 = 1]
        // If Position/Last are already set (e.g., from xsl:number counting), use those values.
        // Otherwise, use the PositionComputer callback to compute on-demand.
        var position = context.Position;
        var size = context.Last;

        if (position == 0 && size == 0 && context.PositionComputer != null)
        {
            (position, size) = context.PositionComputer(node, step.NodeTest, context.DescendantPositionAncestor);
        }

        foreach (var predicate in step.Predicates)
        {
            // Pass MatchedNode for current() in pattern predicates
            if (!context.PredicateEvaluator(node, predicate, position, size, context.MatchedNode))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Tests if a node matches the last step's node test (excluding predicates).
    /// This is useful for computing position() context in pattern predicates.
    /// </summary>
    public override bool MatchesNodeTest(object node)
    {
        if (Steps.Count == 0)
            return false;
        return MatchesStep(Steps[^1], node);
    }

    /// <summary>
    /// Public accessor for MatchesStep, used by ExceptPattern/IntersectPattern for context-scoped matching.
    /// </summary>
    internal static bool MatchesStepPublic(PatternStep step, object node) => MatchesStep(step, node);

    /// <summary>
    /// Public accessor for EvaluatePredicates, used by ExceptPattern/IntersectPattern so
    /// context-scoped matching honours step predicates (e.g. <c>div[@id='a']</c>). See match-278.
    /// </summary>
    internal static bool EvaluatePredicatesPublic(PatternStep step, object node, XsltContext context)
        => EvaluatePredicates(step, node, context);

    private static bool MatchesStep(PatternStep step, object node)
    {
        return (step.Axis, node) switch
        {
            // Child axis: match elements, text, etc.
            // Per XPath spec, NameTest wildcards (*) on the child axis only match elements.
            // PIs and comments are only matched by their specific KindTests or node().
            (Axis.Child, XdmElement elem) => MatchNodeTest(step.NodeTest, XdmNodeKind.Element, elem.Namespace, elem.LocalName),
            (Axis.Child, XdmText) => step.NodeTest is KindTest { Kind: XdmNodeKind.Text or XdmNodeKind.None },
            (Axis.Child, XdmComment) => step.NodeTest is KindTest { Kind: XdmNodeKind.Comment or XdmNodeKind.None },
            (Axis.Child, XdmProcessingInstruction pi) => step.NodeTest is KindTest
                ? MatchNodeTest(step.NodeTest, XdmNodeKind.ProcessingInstruction, NamespaceId.None, pi.Target)
                : false, // NameTest (*) does not match PIs on child axis

            // Descendant / descendant-or-self axis: same node test matching as child
            (Axis.Descendant, XdmElement elem) => MatchNodeTest(step.NodeTest, XdmNodeKind.Element, elem.Namespace, elem.LocalName),
            (Axis.Descendant, XdmText) => step.NodeTest is KindTest { Kind: XdmNodeKind.Text or XdmNodeKind.None },
            (Axis.Descendant, XdmComment) => step.NodeTest is KindTest { Kind: XdmNodeKind.Comment or XdmNodeKind.None },
            (Axis.Descendant, XdmProcessingInstruction pi) => step.NodeTest is KindTest
                ? MatchNodeTest(step.NodeTest, XdmNodeKind.ProcessingInstruction, NamespaceId.None, pi.Target)
                : false,
            (Axis.DescendantOrSelf, XdmElement elem) => MatchNodeTest(step.NodeTest, XdmNodeKind.Element, elem.Namespace, elem.LocalName),
            (Axis.DescendantOrSelf, XdmText) => step.NodeTest is KindTest { Kind: XdmNodeKind.Text or XdmNodeKind.None },
            (Axis.DescendantOrSelf, XdmComment) => step.NodeTest is KindTest { Kind: XdmNodeKind.Comment or XdmNodeKind.None },
            (Axis.DescendantOrSelf, XdmProcessingInstruction pi) => step.NodeTest is KindTest
                ? MatchNodeTest(step.NodeTest, XdmNodeKind.ProcessingInstruction, NamespaceId.None, pi.Target)
                : false,

            // Attribute axis
            (Axis.Attribute, XdmAttribute attr) => MatchNodeTest(step.NodeTest, XdmNodeKind.Attribute, attr.Namespace, attr.LocalName),

            // Namespace axis
            (Axis.Namespace, XdmNamespace ns) => step.NodeTest is KindTest { Kind: XdmNodeKind.Namespace or XdmNodeKind.None }
                || step.NodeTest is NameTest { LocalName: "*" }
                || (step.NodeTest is NameTest nt && nt.LocalName == ns.Prefix),

            // Self axis - matches the node itself (for count="." in xsl:number, or "." pattern)
            (Axis.Self, XdmDocument doc) => step.NodeTest is KindTest { Kind: XdmNodeKind.Document or XdmNodeKind.None } kt
                && (kt.DocumentElementTest == null || MatchesDocumentElementTest(doc, kt.DocumentElementTest)),
            (Axis.Self, XdmElement elem) => MatchNodeTest(step.NodeTest, XdmNodeKind.Element, elem.Namespace, elem.LocalName),
            (Axis.Self, XdmText) => step.NodeTest is KindTest { Kind: XdmNodeKind.Text or XdmNodeKind.None }
                || step.NodeTest is NameTest { LocalName: "*" },
            (Axis.Self, XdmComment) => step.NodeTest is KindTest { Kind: XdmNodeKind.Comment or XdmNodeKind.None }
                || step.NodeTest is NameTest { LocalName: "*" },
            (Axis.Self, XdmProcessingInstruction pi) => step.NodeTest is NameTest { LocalName: "*" }
                || MatchNodeTest(step.NodeTest, XdmNodeKind.ProcessingInstruction, NamespaceId.None, pi.Target),
            (Axis.Self, XdmAttribute attr) => MatchNodeTest(step.NodeTest, XdmNodeKind.Attribute, attr.Namespace, attr.LocalName),
            (Axis.Self, XdmNamespace ns) => step.NodeTest is KindTest { Kind: XdmNodeKind.Namespace or XdmNodeKind.None }
                || step.NodeTest is NameTest { LocalName: "*" }
                || (step.NodeTest is NameTest snt && snt.LocalName == ns.Prefix),

            // child axis on document: only document-node() matches, NOT node()
            (Axis.Child, XdmDocument doc) => step.NodeTest is KindTest { Kind: XdmNodeKind.Document } kt
                && (kt.DocumentElementTest == null || MatchesDocumentElementTest(doc, kt.DocumentElementTest)),

            // XSLT 3.0: Self axis with atomic values - "." matches any item including atomics
            // This enables patterns like ".[. instance of xs:string]" in group-starting-with
            (Axis.Self, _) when step.NodeTest is KindTest { Kind: XdmNodeKind.None } => true,
            (Axis.Self, _) when step.NodeTest is NameTest { LocalName: "*" } => true,

            _ => false
        };
    }

    private static bool MatchNodeTest(NodeTest test, XdmNodeKind kind, NamespaceId ns, string? localName)
    {
        return test switch
        {
            NameTest nt => nt.Matches(kind, ns, localName),
            KindTest kt => kt.Matches(kind, ns, localName),
            _ => false
        };
    }

    /// <summary>
    /// Checks if a document node's document element matches the given element name test.
    /// Used for document-node(element(E)) pattern matching.
    /// </summary>
    private static bool MatchesDocumentElementTest(XdmDocument doc, NameTest elemTest, XsltContext? context = null)
    {
        if (!doc.DocumentElement.HasValue || doc.DocumentElement.Value == NodeId.None)
            return false;
        // Use the NodeResolver to look up the document element
        if (context?.NodeResolver != null)
        {
            var docElem = context.NodeResolver(doc.DocumentElement.Value);
            if (docElem is XdmElement elem)
                return elemTest.Matches(XdmNodeKind.Element, elem.Namespace, elem.LocalName);
        }
        // Without a node resolver, match optimistically (kind already matched)
        return true;
    }
}
