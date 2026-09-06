using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xdm.Serialization;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
// XPath 4.0 ordered map: insertion-order iteration as a structural guarantee.
// xslt keeps its existing default key-equality (pass EqualityComparer<object>.Default
// at each construction site) — this change is about iteration order only.
using OrderedXdmMap = PhoenixmlDb.XQuery.Execution.OrderedXdmMap;

namespace PhoenixmlDb.Xslt.Engine;

internal sealed partial class DefaultXsltExecutionContext
{

#pragma warning disable
    // Opt-in diagnostic, off unless PHXDIAG_XTDE0420=1 is set in the environment.
    //
    // XTDE0420 is raised from five places and is almost always a symptom: the instruction
    // that trips it is legal, and the real fault is the construction scope whose rules are
    // being applied to it (that is exactly how the 1.6.2 typed-variable bug presented). This
    // dumps the construction state and the filtered managed stack at the raise site so the
    // owning scope is identifiable without a debugger — see the `engine-repro` skill.
    //
    // The blanket pragma is deliberate: this is diagnostic-only code under an
    // AnalysisLevel=latest-all / TreatWarningsAsErrors build, and the console writes,
    // literal strings, and stack walk each trip a different analyzer. It is scoped to this
    // one method by the matching restore below.
    // TEMPORARY DIAGNOSTIC — remove before commit. Enable with PHXDIAG_DOCDEPTH=1.
    // Traces every mutation of _documentNodeDepth so an unbalanced ++ (a scope that raised
    // the depth and never restored it) is visible as a `+` with no matching `-`.





    private void DiagXtde0420(string site, string? attrName = null)
    {
        if (Environment.GetEnvironmentVariable("PHXDIAG_XTDE0420") != "1") return;
        var accum = _sequenceAccumulator is null ? "null" : $"count={_sequenceAccumulator.Count}";
        Console.Error.WriteLine(
            $"\n[XTDE0420 @{site}] attr={attrName ?? "?"} "
            + $"docDepth={_documentNodeDepth} attrCollecting={_attributeCollecting} "
            + $"attrStack={_collectedAttributesStack.Count} seqAccum={accum} "
            + $"outLen={_output.Length} logicalStart={_outputLogicalStart} "
            + $"wherePop={_wherePopulatedDepth} serElemDepth={_serializingElementDepth} "
            + $"textDepth={_textContentDepth} loc={_currentInstructionLocation}");

        // The bytes between the logical start of the current scope and the end of _output are
        // "children already emitted here". If this is non-empty while docDepth>0 we are almost
        // certainly inside a scope whose _documentNodeDepth leaked in from an enclosing one.
        var start = Math.Clamp(_outputLogicalStart, 0, _output.Length);
        var scoped = _output.ToString(start, _output.Length - start);
        var tail = _output.ToString(Math.Max(0, _output.Length - 160), Math.Min(160, _output.Length));
        Console.Error.WriteLine($"    scopedLen={scoped.Length} scoped={Trunc(scoped, 160)}");
        Console.Error.WriteLine($"    outTail={Trunc(tail, 160)}");

        // StackTrace(true) carries file+line, so the frame that did the live _documentNodeDepth++
        // is identifiable directly — it is still on the stack (every ++ sits in a try/finally
        // wrapped around body execution). Look for the innermost of lines 10652/15331/15423/
        // 18097/18218/18290/21097 in the dump below.
        var frames = new System.Diagnostics.StackTrace(true).ToString()
            .Split('\n')
            .Where(f => f.Contains("PhoenixmlDb", StringComparison.Ordinal))
            .Where(f => f.Contains(".cs:line", StringComparison.Ordinal)) // drop the duplicate async-shim frames
            .Take(120);
        foreach (var f in frames) Console.Error.WriteLine("    " + f.TrimEnd());

        static string Trunc(string s, int n)
        {
            s = s.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
            return s.Length <= n ? s : s[..n] + "…";
        }
    }


    /// <summary>
    /// True when attributes are being collected for an element that was opened OUTSIDE the
    /// typed body currently executing — so a node constructed now is part of that body's
    /// return value, not content of an element the body is building.
    /// </summary>
    private bool IsAttributeScopeOutsideAsBody()
        => _currentAsBodyCapture != null
           && _collectedAttributesStack.Count <= _currentAsBodyCapture.AttrDepthAtStart;


    private static bool IsGeneralComparison(BinaryOperator op)
        => op is BinaryOperator.GeneralEqual or BinaryOperator.GeneralNotEqual
              or BinaryOperator.GeneralLessThan or BinaryOperator.GeneralLessOrEqual
              or BinaryOperator.GeneralGreaterThan or BinaryOperator.GeneralGreaterOrEqual;


    private static bool IsCheapNumericArithmetic(BinaryOperator op) => op is
        BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply
        or BinaryOperator.Divide or BinaryOperator.IntegerDivide or BinaryOperator.Modulo;


    private static bool IsCheapNumericFunction(FunctionCallExpression fc)
    {
        var ns = fc.Name.Namespace;
        if (ns != NamespaceId.None
            && ns != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn
            && ns != PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Xs)
            return false;
        return fc.Name.LocalName is "abs" or "ceiling" or "floor" or "round" or "number"
            or "decimal" or "double" or "integer" or "float";
    }


    private static bool IsNumericLiteral(XQueryExpression e) =>
        e is IntegerLiteral or DecimalLiteral or DoubleLiteral;


    private static bool IsWatcherNumeric(object? v)
        => v is int or long or double or float or decimal;


    /// <summary>
    /// Constructs an <see cref="XsltException"/> tagged with <see cref="CurrentInstructionLocation"/>.
    /// Use as <c>throw context.Error("XTTE0570: ...");</c> to auto-attach location info.
    /// </summary>
    internal XsltException Error(string message)
        => new(message, _currentInstructionLocation);


    /// <summary>
    /// Same as <see cref="Error(string)"/> but wraps an inner exception.
    /// </summary>
    internal XsltException Error(string message, Exception innerException)
        => new(message, _currentInstructionLocation, innerException);


    public override bool IsBackwardsCompatibleMode => IsBackwardsCompatible;


    /// <summary>
    /// Checks if a namespace binding (prefix → uri) is already in scope from an ancestor element.
    /// </summary>
    private bool IsNamespaceInScope(string prefix, string uri)
    {
        foreach (var scope in _outputNsScopes)
        {
            if (scope.TryGetValue(prefix, out var scopedUri))
                return scopedUri == uri;
        }
        return false;
    }


    /// <summary>
    /// Checks if a prefix is already bound in any output namespace scope.
    /// </summary>
    private bool IsPrefixInUse(string prefix)
    {
        foreach (var scope in _outputNsScopes)
        {
            if (scope.ContainsKey(prefix))
                return true;
        }
        return false;
    }


    /// <summary>
    /// Returns true if we're currently inside a where-populated tracking scope.
    /// </summary>
    private bool IsTrackingPopulated => _populatedTracking.Count > 0;


    /// <summary>
    /// Returns true if backwards-compatible mode is active (effective version is numerically less than 2.0).
    /// Per XSLT spec, the version attribute is interpreted as a number — values like " 001 ", "+0.5", "-29"
    /// are valid and determine backwards-compatible mode by numeric comparison.
    /// </summary>
    private bool IsBackwardsCompatible =>
        ParseVersionNumber(EffectiveVersion) < 2.0m;


    /// <summary>
    /// Checks whether the policy allows text access without throwing.
    /// </summary>
    internal bool IsUnparsedTextAvailableViaPolicy(string href)
    {
        if (_policyResolver == null)
            return true; // No policy — allow default check

        return _policyResolver.IsTextAvailable(href);
    }


    private bool IsTypedMode(QName? mode)
    {
        if (mode != null && _stylesheet.Modes.TryGetValue(mode.Value, out var modeDecl))
            return modeDecl.Typed;
        var unnamed = new QName(default, "");
        if (_stylesheet.Modes.TryGetValue(unnamed, out var unnamedMode))
            return unnamedMode.Typed;
        return false;
    }


    /// <summary>
    /// Renders a match pattern for an error message.
    /// </summary>
    /// <remarks>
    /// Patterns keep no copy of their source text, so a message that interpolated one printed
    /// whatever ToString() gave — for most pattern classes the CLR type name. An author reading
    /// <c>Template match="PhoenixmlDb.Xslt.Ast.DotPattern"</c> cannot map that back to anything
    /// they wrote. Render the shapes that have an obvious spelling, and for the rest fall back
    /// to a trimmed kind name rather than a fully-qualified type.
    /// </remarks>
    private static string DescribePattern(Ast.XsltPattern? pattern)
    {
        if (pattern == null) return "(none)";
        var text = pattern.ToString();
        // A pattern that overrides ToString() returns its own spelling; the default
        // implementation returns the type's full name, which always contains a dot-separated
        // namespace and never looks like a pattern.
        if (text != null && !text.StartsWith("PhoenixmlDb.", StringComparison.Ordinal))
            return text;
        return pattern switch
        {
            Ast.DotPattern => ".",
            Ast.UnionPattern => "a union pattern",
            Ast.ExceptPattern => "an except pattern",
            Ast.IntersectPattern => "an intersect pattern",
            Ast.KeyPattern => "a key() pattern",
            Ast.IdPattern => "an id() pattern",
            _ => pattern.GetType().Name.Replace("Pattern", "", StringComparison.Ordinal)
        };
    }


    /// <summary>
    /// Describes a sequence for a cardinality error: how many items, and what the first few are.
    /// </summary>
    private static string DescribeSequenceForDiagnostics(System.Collections.Generic.IEnumerable<object?> source)
    {
        var items = new System.Collections.Generic.List<object?>(source);
        if (items.Count == 0)
            return "no items";
        var shown = new System.Collections.Generic.List<string>();
        for (var i = 0; i < items.Count && i < 3; i++)
            shown.Add(DescribeNodeForDiagnostics(items[i]));
        var more = items.Count > 3 ? $", and {items.Count - 3} more" : "";
        return $"{items.Count} items ({string.Join(", ", shown)}{more})";
    }


    /// <summary>
    /// A short human-readable description of a node for error messages: its kind, and its name
    /// when it has one.
    /// </summary>
    private static string DescribeNodeForDiagnostics(object? node) => node switch
    {
        XdmElement e => $"element '{(string.IsNullOrEmpty(e.Prefix) ? e.LocalName : e.Prefix + ":" + e.LocalName)}'",
        XdmAttribute a => $"attribute '{(string.IsNullOrEmpty(a.Prefix) ? a.LocalName : a.Prefix + ":" + a.LocalName)}'",
        XdmDocument => "document node",
        XdmText => "text node",
        XdmComment => "comment node",
        XdmProcessingInstruction pi => $"processing instruction '{pi.Target}'",
        null => "an absent item",
        string str => $"the string '{(str.Length > 30 ? str[..30] + "..." : str)}'",
        Xdm.TextNodeItem t => $"a text item '{(t.Value.Length > 30 ? t.Value[..30] + "..." : t.Value)}'",
        _ => $"a {node.GetType().Name}"
    };


    /// <summary>
    /// #143 Task 1.3 — names the owning construct for the invariant diagnostic. Best-effort:
    /// the current template's match pattern text plus the first structural instruction of its
    /// body, so a tripped assert points at the exact stylesheet shape (e.g. the 013 family).
    /// </summary>
    private string DescribeOwningConstruct()
    {
        var t = _currentTemplate;
        if (t is null)
            return "<unknown construct>";
        var match = t.Match is not null ? $"match=\"{t.Match}\"" : "(named template)";
        var first = t.Body?.Instructions.Count > 0
            ? t.Body.Instructions[0].GetType().Name
            : "(empty body)";
        return $"template {match}, body starts with {first}";
    }


    /// <summary>
    /// Atomizes a sequence for general comparison: nodes and temporary trees become their string
    /// value (untypedAtomic, which CompareAtomic promotes to numeric when the other side is
    /// numeric), atomic values pass through untouched.
    /// </summary>
    private static List<object?> AtomizeForComparison(List<object?> items)
    {
        var atomized = new List<object?>(items.Count);
        foreach (var item in items)
        {
            atomized.Add(item is Xdm.Nodes.XdmNode or Xdm.TextNodeItem or ResultTreeFragment
                ? StringValueOf(item)
                : item);
        }
        return atomized;
    }


    /// <summary>
    /// Returns true when <paramref name="select"/> consumes the children of the current
    /// context node — i.e., it would, in non-streaming mode, evaluate to the result of
    /// the <c>child::</c> axis. Used to route streaming-aware operators
    /// (apply-templates, for-each, for-each-group, iterate) to a reader-driven
    /// implementation. Conservative: only matches the common cases (null = default
    /// children, single child-axis step, child step wrapped in a PathExpression).
    /// </summary>
    private static bool IsConsumingChildSelect(XQueryExpression? select)
    {
        if (select == null) return true; // null select = default children
        // Single child-axis step
        if (select is PhoenixmlDb.XQuery.Ast.StepExpression step
            && step.Axis == PhoenixmlDb.XQuery.Ast.Axis.Child
            && step.Predicates.Count == 0)
            return true;
        // PathExpression with single child-axis step and no initial expression
        if (select is PhoenixmlDb.XQuery.Ast.PathExpression path
            && path.InitialExpression == null
            && path.Steps.Count == 1
            && path.Steps[0] is PhoenixmlDb.XQuery.Ast.StepExpression s2
            && s2.Axis == PhoenixmlDb.XQuery.Ast.Axis.Child
            && s2.Predicates.Count == 0)
            return true;
        return false;
    }


    /// <summary>
    /// Returns true when <paramref name="select"/> is a bare context-item select
    /// (<c>select="."</c>) — either the singleton <see cref="ContextItemExpression"/> or a
    /// <see cref="PhoenixmlDb.XQuery.Ast.PathExpression"/> with a context-item initial
    /// expression and no trailing steps. At the document level (context = the streamed
    /// document node) <c>copy-of select="."</c> deep-copies the document node, whose
    /// serialization is exactly its children — identical to <c>select="child::node()"</c>.
    /// Recognizing it lets the streaming whole-subtree forward run for the "whole document
    /// unchanged" shape written with <c>.</c> instead of <c>child::node()</c>
    /// (si-copy-of-011).
    /// </summary>
    private static bool IsSelfContextSelect(XQueryExpression? select)
    {
        if (select is PhoenixmlDb.XQuery.Ast.ContextItemExpression)
            return true;
        return select is PhoenixmlDb.XQuery.Ast.PathExpression path
            && path.InitialExpression is PhoenixmlDb.XQuery.Ast.ContextItemExpression
            && path.Steps.Count == 0;
    }


    /// <summary>
    /// Returns true when <paramref name="select"/> is a document-level "striding" select
    /// — a child-axis name-test path evaluated from the document node that selects one or
    /// more top-level elements (e.g. <c>account</c>, <c>./account</c>, <c>/account</c>).
    /// Such a select on a streamable <c>xsl:apply-templates</c> whose context is the
    /// document node is driven through the streaming processor: its forward pass offers
    /// each top-level element to template matching, and only templates whose pattern
    /// matches the selected element name fire — reproducing the striding select without
    /// materializing the document. Conservative: rejects predicates, non-child axes,
    /// descendant hops, attribute tails, and multi-step descent so nothing but a
    /// top-level element striding select routes here.
    /// </summary>
    private static bool IsDocumentLevelStridingSelect(XQueryExpression? select)
    {
        if (select is not PhoenixmlDb.XQuery.Ast.PathExpression path)
            return false;
        // A leading '.' parses as ContextItemExpression InitialExpression; a leading '/'
        // sets IsAbsolute. Both anchor at the document node when the context IS the
        // document node. Any other initial expression means the path is not rooted at
        // the streamed document root.
        if (path.InitialExpression != null
            && path.InitialExpression is not PhoenixmlDb.XQuery.Ast.ContextItemExpression)
            return false;
        if (path.Steps.Count != 1) return false;
        var step = path.Steps[0];
        if (step.Axis != PhoenixmlDb.XQuery.Ast.Axis.Child) return false;
        if (step.Predicates.Count > 0) return false;
        // Require a name test (or *) — a kind test / text() tail is not a striding
        // element select.
        return step.NodeTest is PhoenixmlDb.XQuery.Ast.NameTest;
    }


    /// <summary>
    /// Validates that a string is a plausible BCP 47 language tag (e.g., "en", "de-AT").
    /// Must start with a letter and contain only letters, digits, and hyphens.
    /// </summary>
    private static bool IsValidLanguageTag(string tag)
    {
        if (tag.Length == 0 || !char.IsLetter(tag[0]))
            return false;
        for (var i = 1; i < tag.Length; i++)
        {
            if (!char.IsLetterOrDigit(tag[i]) && tag[i] != '-')
                return false;
        }
        return true;
    }


    /// <summary>
    /// SP-C slice 3 scope guard: returns <c>false</c> when the constructor's fragment
    /// <paramref name="roots"/> would NOT reparse byte-identically under the legacy
    /// serialize-then-reparse reference, so the flip must fall back to the RTF. Two Task-6
    /// model differences are excluded:
    /// <list type="bullet">
    /// <item><b>Preserved copy-source base URI:</b> any node carrying <c>CopySourceBaseUri</c>
    /// (or an entity <c>BaseUri</c>) — the plain untyped-RTF serialize path emits no base
    /// sentinel, so the reparse would recover <c>null</c> there.</item>
    /// <item><b>Inherited namespace over-declaration:</b> any element with a non-empty in-scope
    /// namespace set that has an element child — the reparse records the child's FULL in-scope
    /// set as its namespace declarations, where the constructor records only local ones.</item>
    /// </list>
    /// </summary>
    private bool IsUntypedRtfFlipByteParitySafe(IReadOnlyList<NodeId> roots, TreeConstructor bodyTc)
    {
        foreach (var id in roots)
            if (!FlipSafeNode(id, bodyTc))
                return false;
        return true;
    }


    /// <summary>True when <paramref name="s"/> is empty or only XML whitespace (space, tab, CR, LF).</summary>
    private static bool IsXmlWhitespace(string s)
    {
        foreach (var c in s)
            if (c is not (' ' or '\t' or '\r' or '\n'))
                return false;
        return true;
    }


    private bool IsInCdataSectionElement()
    {
        if (_outputElementStack.Count == 0)
            return false;
        var outputDecl = _activeResultDocumentOutput ?? _stylesheet.Outputs.FirstOrDefault();
        if (outputDecl?.CdataSectionElements == null || outputDecl.CdataSectionElements.Count == 0)
            return false;
        var currentElem = _outputElementStack.Peek();
        return outputDecl.CdataSectionElements.Contains(currentElem);
    }


    private void ValidateResultDocumentContent(XsltResultDocument instruction, string content)
        => RunValidation(instruction.Validation, content, ValidationKind.Document,
            "xsl:result-document", instruction.Location);


    /// <summary>
    /// Best-effort schema check for <c>xsl:attribute validation="strict"</c>. The full XSLT
    /// 3.0 contract validates an attribute in the context of its parent element; in that
    /// context lookup we only know the attribute name, namespace, and value. We confirm a
    /// global schema-attribute declaration exists (and otherwise raise XQDY0027). Value-vs-
    /// type checking (typed casts, facet enforcement) is deferred — when the parent element
    /// also carries <c>validation="strict|lax"</c>, the element-level check covers the value.
    /// </summary>
    private void ValidateAttributeIfRequested(XsltAttribute instruction, string name, string? explicitNsUri)
    {
        if (!ShouldRunValidation(instruction.Validation)) return;
        if (_schemaProvider is null)
        {
            throw new XsltException(
                "XTTE1545: xsl:attribute validation requires a registered ISchemaProvider on the XsltTransformer",
                instruction.Location);
        }

        var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
        var localName = colonIdx >= 0 ? name[(colonIdx + 1)..] : name;
        string nsUri = explicitNsUri ?? "";
        if (string.IsNullOrEmpty(nsUri) && colonIdx > 0)
        {
            var prefix = name[..colonIdx];
            if (instruction.InScopeNamespaces.TryGetValue(prefix, out var bound))
                nsUri = bound;
        }

        if (!_schemaProvider.HasAttributeDeclaration(nsUri, localName))
        {
            if (instruction.Validation == Ast.ValidationMode.Strict)
            {
                throw new XsltException(
                    $"XQDY0027: xsl:attribute validation=\"strict\" requires a global schema-attribute declaration for '{name}' (namespace: '{nsUri}'); none found in the loaded schemas",
                    instruction.Location);
            }
            // Lax: silently skip when no declaration is found.
        }
    }


    internal static object? CoerceAccumulatorValue(object? value, XdmSequenceType declaredType, QName accName)
    {
        // Unwrap single-item lists/arrays to scalar values (may be nested)
        if (declaredType.Occurrence is not (Occurrence.ZeroOrMore or Occurrence.OneOrMore))
        {
            while (true)
            {
                if (value is object[] arr2)
                {
                    if (arr2.Length == 1)
                    { value = arr2[0]; continue; }
                    if (arr2.Length == 0)
                    { value = null; break; }
                    break;
                }
                if (value is IReadOnlyList<object?> rl)
                {
                    if (rl.Count == 1)
                    { value = rl[0]; continue; }
                    if (rl.Count == 0)
                    { value = null; break; }
                    break;
                }
                if (value is System.Collections.IList il)
                {
                    if (il.Count == 1)
                    { value = il[0]; continue; }
                    if (il.Count == 0)
                    { value = null; break; }
                    break;
                }
                break;
            }
        }

        // Handle sequence types (xs:integer*, xs:string+, etc.)
        if (declaredType.Occurrence is Occurrence.ZeroOrMore or Occurrence.OneOrMore)
        {
            // Value may be a list — coerce each item
            if (value is IReadOnlyList<object?> list)
            {
                var coerced = new List<object?>(list.Count);
                foreach (var item in list)
                    coerced.Add(CoerceAtomicValue(item, declaredType.ItemType, accName));
                // Return object[], NOT List<object?>. The engine distinguishes an XDM array
                // from a sequence purely by CLR type - PhysicalOperators has
                // `ItemType.Array => item is List<object?>` - so returning the List here
                // reclassified every sequence-valued accumulator as a one-item array.
                return coerced.ToArray();
            }
            // Single item is fine for * or +
            return CoerceAtomicValue(value, declaredType.ItemType, accName);
        }

        if (declaredType.ItemType == ItemType.Map && value is IDictionary<object, object?> map)
        {
            foreach (var key in map.Keys)
            {
                if (declaredType.MapKeyType != null && !MatchesAtomicType(key, declaredType.MapKeyType.Value))
                    throw new XsltException($"XPTY0004: Accumulator {accName.LocalName} declared as map({declaredType.MapKeyType}, ...) but key is {key?.GetType().Name ?? "null"}");
            }
            foreach (var val in map.Values)
            {
                if (declaredType.MapValueType != null && !MatchesAtomicType(val, declaredType.MapValueType.Value))
                    throw new XsltException($"XPTY0004: Accumulator {accName.LocalName} declared as map(..., {declaredType.MapValueType}) but value is {val?.GetType().Name ?? "null"}");
            }
            return value;
        }

        // Coerce atomic value to the declared type
        return CoerceAtomicValue(value, declaredType.ItemType, accName);
    }


    private static object? CoerceAtomicValue(object? value, ItemType targetType, QName accName)
    {
        if (value is null)
            return null;

        // Node-typed, item() and function/map/array accumulators are not atomized.
        if (PreservesItems(targetType))
            return value;

        // Atomize XDM nodes (attribute nodes → string value, element nodes → text content)
        value = PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(value);
        if (value is null)
            return null;

        if (MatchesAtomicType(value, targetType))
            return value;

        // XSLT 3.0 §18.2 applies the function conversion rules to an accumulator value, and
        // those CAST xs:untypedAtomic to the declared atomic type. Atomizing a node yields
        // XsUntypedAtomic — not string — so every `value is string` arm below missed it and an
        // untyped node value fell through to the throw at the bottom.
        //
        // The visible symptom was silence, not an error: the throw becomes an
        // AccumulatorDeferredError (correct per spec — accumulator errors are deferred), so
        //   <xsl:accumulator name="min" as="xs:double" initial-value="999999">
        //     <xsl:accumulator-rule match="transaction"
        //                           select="if (@amount lt $value) then @amount else $value"/>
        // simply never updated, reporting its seed as the answer. The sibling `sum` accumulator
        // worked throughout because `$value + @amount` atomizes through the arithmetic
        // operators, which handle untypedAtomic correctly — so the same stylesheet produced a
        // right sum and a wrong min.
        if (value is PhoenixmlDb.Xdm.XsUntypedAtomic untyped)
            value = untyped.Value;

        // Attempt type coercion (untypedAtomic/string → target type)
        return targetType switch
        {
            ItemType.Double when value is string s =>
                double.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)
                    ? d
                    : throw new XsltException($"XPTY0004: Cannot cast '{s}' to xs:double for accumulator '{accName.LocalName}'"),
            ItemType.Double when value is int i => (double)i,
            ItemType.Double when value is long l => (double)l,
            ItemType.Double when value is decimal m => (double)m,
            ItemType.Integer when value is string s =>
                long.TryParse(s, out var l)
                    ? l
                    : throw new XsltException($"XPTY0004: Cannot cast '{s}' to xs:integer for accumulator '{accName.LocalName}'"),
            ItemType.Integer when value is double d => (long)d,
            ItemType.Integer when value is decimal m => (long)m,
            // A BigInteger already IS an xs:integer and is matched above; these two arms carry it
            // into the other numeric types rather than falling through to the throw.
            ItemType.Double when value is System.Numerics.BigInteger bi => (double)bi,
            ItemType.Decimal when value is System.Numerics.BigInteger bi => (decimal)bi,
            ItemType.Decimal when value is string s =>
                decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var m)
                    ? m
                    : throw new XsltException($"XPTY0004: Cannot cast '{s}' to xs:decimal for accumulator '{accName.LocalName}'"),
            ItemType.Decimal when value is int i => (decimal)i,
            ItemType.Decimal when value is long l => (decimal)l,
            ItemType.Decimal when value is double d => (decimal)d,
            ItemType.String => value.ToString()!,
            ItemType.AnyAtomicType => value, // any value is acceptable
            _ => throw new XsltException($"XPTY0004: Accumulator '{accName.LocalName}' declared as {targetType} but value is {value.GetType().Name}")
        };
    }


    /// <summary>
    /// Lazily computes accumulators for the document containing the given node,
    /// if they haven't been computed yet. Per XSLT 3.0 §6.5, accumulators are
    /// applicable to any document, not just the principal source document.
    /// </summary>
    /// <summary>
    /// Checks if the given accumulator is applicable in the current mode per §6.5.
    /// Returns true if the mode has use-accumulators="#all" or includes the accumulator name.
    /// Returns true if the mode has no explicit use-accumulators attribute (unspecified = all applicable).
    /// Returns false if the mode has use-accumulators="" (empty = none).
    /// </summary>
    internal bool IsAccumulatorApplicable(QName accumulatorName)
    {
        var modeKey = _currentMode ?? new QName(NamespaceId.None, "");
        if (_stylesheet.Modes.TryGetValue(modeKey, out var modeDecl))
        {
            if (modeDecl.UseAccumulatorsAttr != null)
            {
                // Explicit use-accumulators: check if this accumulator is included
                if (modeDecl.UseAllAccumulators)
                    return true;
                return modeDecl.UseAccumulatorNames.Any(n => n == accumulatorName);
            }
        }
        // No explicit use-accumulators → accumulators are applicable (default behavior for
        // accumulator-before/after access). For copy-accumulators, use the stricter check.
        return true;
    }


    /// <summary>
    /// True when <paramref name="node"/> belongs to the principal source tree (the tree the
    /// initial mode was applied to). Used to distinguish the two accumulator-access errors:
    /// accessing an accumulator that the initial mode excluded, on the principal source
    /// document, is XTDE3362 (§18.2 — the accumulator was never evaluated for that tree);
    /// the same access on any OTHER tree is the general XTDE3340.
    /// </summary>
    internal bool IsPrincipalSourceNode(object? node)
    {
        if (!_principalSourceDocId.HasValue) return false;
        var docId = FindDocumentIdForInput(node);
        return docId.HasValue && docId.Value == _principalSourceDocId.Value;
    }


    /// <summary>
    /// Strict accumulator applicability check for copy-accumulators="yes" (XTDE3362).
    /// Per §18.2.2 #5: for the principal source document, accumulators are applicable only if
    /// the initial mode's xsl:mode includes them in use-accumulators.
    /// Without any xsl:mode declaration, no accumulators are applicable.
    /// </summary>
    private bool IsAccumulatorApplicableForCopy(QName accumulatorName)
    {
        var modeKey = _currentMode ?? new QName(NamespaceId.None, "");
        if (_stylesheet.Modes.TryGetValue(modeKey, out var modeDecl))
        {
            if (modeDecl.UseAccumulatorsAttr != null)
            {
                if (modeDecl.UseAllAccumulators)
                    return true;
                return modeDecl.UseAccumulatorNames.Any(n => n == accumulatorName);
            }
            // xsl:mode exists but no use-accumulators → default is #all
            return true;
        }
        // No xsl:mode declaration for this mode → no accumulators applicable (§18.2.2 #5)
        return false;
    }


    private static bool IsFormatToken(char c)
    {
        // Standard alphanumeric
        if (char.IsLetterOrDigit(c))
            return true;

        // Unicode number sequences (circled, parenthesized, full stop digits)
        // Circled digits: ① (U+2460) through ⑳ (U+2473)
        if (c >= '\u2460' && c <= '\u2473')
            return true;
        // Parenthesized digits: ⑴ (U+2474) through ⒇ (U+2487)
        if (c >= '\u2474' && c <= '\u2487')
            return true;
        // Full stop digits: ⒈ (U+2488) through ⒛ (U+249B)
        if (c >= '\u2488' && c <= '\u249B')
            return true;
        // Circled zero: ⓪ (U+24EA)
        if (c == '\u24EA')
            return true;
        // Negative circled zero: ⓿ (U+24FF)
        if (c == '\u24FF')
            return true;
        // Dingbat negative circled 11-20: ⓫ (U+24EB) to ⓴ (U+24F4)
        if (c >= '\u24EB' && c <= '\u24F4')
            return true;
        // Double circled digits: ⓵ (U+24F5) to ⓾ (U+24FE)
        if (c >= '\u24F5' && c <= '\u24FE')
            return true;
        // Dingbat negative circled digits: ❶ (U+2776) to ❿ (U+277F)
        if (c >= '\u2776' && c <= '\u277F')
            return true;
        // Dingbat circled sans-serif digits: ➀ (U+2780) to ➉ (U+2789)
        if (c >= '\u2780' && c <= '\u2789')
            return true;
        // Dingbat negative circled sans-serif digits: ➊ (U+278A) to ➓ (U+2793)
        if (c >= '\u278A' && c <= '\u2793')
            return true;
        // Parenthesized Ideograph: ㈠ (U+3220) to ㈩ (U+3229)
        if (c >= '\u3220' && c <= '\u3229')
            return true;
        // Circled Ideograph: ㊀ (U+3280) to ㊉ (U+3289)
        if (c >= '\u3280' && c <= '\u3289')
            return true;

        return false;
    }


    private static bool IsNumericFormat(string token)
    {
        return token.Length > 0 && char.IsDigit(token[0]);
    }


    private static bool IsMergeKeyNumeric(object? v) => v is long or int or double or float or decimal;


    private static string AtomizeMergeKeyToString(object v) => v switch
    {
        Xdm.Nodes.XdmNode n => n.StringValue,
        XsUntypedAtomic ua => ua.Value,
        _ => v.ToString() ?? string.Empty
    };


    /// <summary>
    /// Coerce and validate the result of xsl:evaluate against the 'as' type.
    /// Follows XSLT 3.0 function coercion: atomize nodes → cast untypedAtomic → check type.
    /// Returns the coerced result, or throws XPTY0004 on type mismatch.
    /// </summary>
    private static object? CoerceEvaluateResult(object? result, XdmSequenceType targetType, SourceLocation? location)
    {
        // Check cardinality for empty
        if (result == null || (result is object?[] arr0 && arr0.Length == 0))
        {
            if (targetType.Occurrence == Occurrence.ExactlyOne || targetType.Occurrence == Occurrence.OneOrMore)
                throw new XsltException($"XPTY0004: xsl:evaluate result is empty but required type is {targetType}", location);
            return result;
        }

        var items = result is object?[] arr ? arr : new[] { result };
        if (items.Length > 1 && (targetType.Occurrence == Occurrence.ExactlyOne || targetType.Occurrence == Occurrence.ZeroOrOne))
            throw new XsltException($"XPTY0004: xsl:evaluate result has {items.Length} items but required type is {targetType}", location);

        // For item() type, accept everything
        if (targetType.ItemType == ItemType.Item)
            return result;

        // Coerce each item per XSLT function coercion rules
        var coerced = new object?[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            if (item == null) { coerced[i] = null; continue; }

            // For atomic target types: atomize nodes, then cast untypedAtomic
            var isAtomicTarget = targetType.ItemType >= ItemType.AnyAtomicType;
            if (isAtomicTarget && !IsNodeType(targetType.ItemType))
            {
                // Step 1: Atomize nodes → produces xs:untypedAtomic (for non-schema-aware)
                if (item is Xdm.Nodes.XdmNode or Xdm.Nodes.XdmAttribute or Xdm.Nodes.XdmElement
                    or System.Xml.XmlNode or System.Xml.Linq.XNode)
                    item = PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AtomizeTyped(item);

                // Step 2: Cast xs:untypedAtomic to target type
                if (item is Xdm.XsUntypedAtomic)
                {
                    item = CoerceToType(item, targetType);
                }

                // Step 3: Validate the (possibly coerced) item matches the target type
                if (item != null && !PhoenixmlDb.XQuery.Execution.TypeCastHelper.MatchesItemType(item, targetType.ItemType))
                    throw new XsltException($"XPTY0004: xsl:evaluate result of type {item.GetType().Name} does not match required type {targetType}", location);

                coerced[i] = item;
            }
            else
            {
                // For node/function/map/array target types: validate directly
                if (!PhoenixmlDb.XQuery.Execution.TypeCastHelper.MatchesItemType(item, targetType.ItemType))
                    throw new XsltException($"XPTY0004: xsl:evaluate result of type {item.GetType().Name} does not match required type {targetType}", location);
                coerced[i] = item;
            }
        }

        return coerced.Length == 1 ? coerced[0] : coerced;
    }


    /// <summary>
    /// Validates the return value of a template against its declared 'as' type (XTTE0505).
    /// </summary>
    private static void ValidateCollation(string? collationUri, string errorCode)
    {
        if (string.IsNullOrEmpty(collationUri))
            return;
        // Accept known collation URIs
        if (collationUri!.StartsWith("http://www.w3.org/2013/collation/UCA", StringComparison.Ordinal))
            return;
        if (collationUri == "http://www.w3.org/2005/xpath-functions/collation/codepoint")
            return;
        if (collationUri == "http://www.w3.org/2005/xpath-functions/collation/html-ascii-case-insensitive")
            return;
        if (collationUri == "http://saxon.sf.net/collation")
            return; // Saxon compatibility
        throw new XsltException($"{errorCode}: Unknown collation URI '{collationUri}'");
    }


    private void ValidateTemplateReturnType(XsltTemplate template, List<object?> resultItems)
    {
        if (template.As == null)
            return;

        var targetType = template.As.ItemType;
        var occurrence = template.As.Occurrence;
        // Include template identity in error messages for diagnostics
        var templateId = template.Name != null ? $" '{template.Name.Value.LocalName}'" :
            template.Match != null ? $" match=\"{DescribePattern(template.Match)}\"" : "";
        var locInfo = templateId;

        // Apply function conversion rules: atomize nodes for atomic target types
        var isAtomicTarget = targetType is not ItemType.Item and not ItemType.Node
            and not ItemType.Element and not ItemType.Text and not ItemType.Comment
            and not ItemType.ProcessingInstruction and not ItemType.Attribute
            and not ItemType.Document and not ItemType.Function and not ItemType.Map
            and not ItemType.Array;

        if (isAtomicTarget)
        {
            for (int i = 0; i < resultItems.Count; i++)
            {
                if (resultItems[i] is XdmNode node)
                    resultItems[i] = node.StringValue; // atomize: node → string
            }
        }

        var nonNullCount = resultItems.Count(i => i != null);

        // Cardinality check
        if (occurrence == Occurrence.Zero && nonNullCount != 0)
            throw Error($"XTTE0505: Template{locInfo} return value does not match declared type empty-sequence(): expected zero items, got {nonNullCount}");
        if (occurrence == Occurrence.Zero)
            return; // empty-sequence() — no item type check needed
        if (occurrence == Occurrence.ExactlyOne && nonNullCount != 1)
            throw Error($"XTTE0505: Template{locInfo} return value does not match declared type {template.As.ItemType}: expected exactly one item, got {nonNullCount}");
        if (occurrence == Occurrence.ZeroOrOne && nonNullCount > 1)
            throw Error($"XTTE0505: Template{locInfo} return value does not match declared type {template.As.ItemType}: expected zero or one item, got {nonNullCount}");
        if (occurrence == Occurrence.OneOrMore && nonNullCount == 0)
            throw Error($"XTTE0505: Template{locInfo} return value does not match declared type {template.As.ItemType}: expected one or more items, got 0");

        // Item type check — for atomic types, attempt coercion from string (function conversion rules)
        if (isAtomicTarget)
        {
            for (int i = 0; i < resultItems.Count; i++)
            {
                var item = resultItems[i];
                if (item == null)
                    continue;

                // Already the correct type?
                var alreadyMatches = (targetType, item) switch
                {
                    (ItemType.String, string) => true,
                    (ItemType.UntypedAtomic, string) => true,
                    (ItemType.UntypedAtomic, Xdm.XsUntypedAtomic) => true,
                    (ItemType.Integer, long) => true,
                    (ItemType.Integer, int) => true,
                    (ItemType.Double, double) => true,
                    (ItemType.Float, float) => true,
                    (ItemType.Decimal, decimal) => true,
                    (ItemType.Boolean, bool) => true,
                    (ItemType.AnyAtomicType, _) when item is not XdmNode => true,
                    (ItemType.Duration, TimeSpan or Xdm.DayTimeDuration or Xdm.YearMonthDuration or Xdm.XsDuration) => true,
                    (ItemType.YearMonthDuration, Xdm.YearMonthDuration) => true,
                    (ItemType.DayTimeDuration, TimeSpan or Xdm.DayTimeDuration) => true,
                    (ItemType.Date, Xdm.XsDate) => true,
                    (ItemType.DateTime, Xdm.XsDateTime) => true,
                    (ItemType.Time, Xdm.XsTime) => true,
                    (ItemType.AnyUri, Xdm.XsAnyUri) => true,
                    (ItemType.QName, QName) => true,
                    _ => false
                };
                if (alreadyMatches)
                    continue;

                // Attempt function conversion: cast string/untypedAtomic to target type
                var sv = item is string s ? s : item.ToString()!;
                var coerced = TryCoerceStringToType(sv, targetType);
                if (coerced != null)
                {
                    resultItems[i] = coerced;
                }
                else if (IsStrictlyIncompatible(sv, targetType))
                {
                    // Only raise error for clearly invalid conversions (e.g., "hello" → xs:double)
                    throw Error($"XTTE0505: Template{locInfo} return value '{sv}' cannot be cast to declared type {template.As.ItemType}");
                }
                // else: conversion failed but might be due to unsupported features (e.g., negative years);
                // pass the string value through to maintain previous behavior
            }
        }
        else if (targetType is not ItemType.Item and not ItemType.Node)
        {
            // Node type check
            foreach (var item in resultItems)
            {
                if (item == null)
                    continue;
                var matches = (targetType, item) switch
                {
                    (ItemType.Element, XdmElement el) =>
                        MatchesElementName(el, template.As.ElementName, template.As.ElementNamespace),
                    (ItemType.Text, XdmText or string or Xdm.TextNodeItem) => true,
                    (ItemType.Comment, XdmComment) => true,
                    (ItemType.ProcessingInstruction, XdmProcessingInstruction) => true,
                    (ItemType.Attribute, XdmAttribute) => true,
                    (ItemType.Document, XdmDocument doc) =>
                        template.As.DocumentElementName == null ||
                        doc.DocumentElementLocalName == template.As.DocumentElementName,
                    (ItemType.Function, XQueryFunction) => true,
                    (ItemType.Map, IDictionary<object, object?>) => true,
                    (ItemType.Array, List<object?>) => true,
                    _ => false
                };
                if (!matches)
                {
                    var valuePreview = item is string vs ? $"\"{(vs.Length > 80 ? vs[..80] + "…" : vs)}\"" : item.ToString() ?? "(null)";
                    if (valuePreview.Length > 100) valuePreview = valuePreview[..100] + "…";
                    throw Error($"XTTE0505: Template{locInfo} return value item of type {item.GetType().Name} does not match declared type {template.As.ItemType}; value={valuePreview}");
                }
            }
        }
    }


    /// <summary>
    /// Returns true if the string value is clearly incompatible with the target type
    /// (not just failing due to unsupported features like negative years).
    /// </summary>
    private static bool IsStrictlyIncompatible(string value, ItemType targetType)
    {
        var v = value.Trim();
        return targetType switch
        {
            // Numeric types: check if value looks like a number at all
            ItemType.Integer or ItemType.Decimal or ItemType.Double or ItemType.Float
                => !v.All(c => char.IsDigit(c) || c is '.' or '-' or '+' or 'e' or 'E' or 'N' or 'a' or 'I' or 'F')
                   || v.Length == 0,
            ItemType.Boolean => v is not ("true" or "false" or "1" or "0"),
            // For date/time/duration types, don't be strict — parsing can fail for many reasons
            _ => false
        };
    }


    /// <summary>
    /// Coerces a value to match the specified XDM sequence type.
    /// </summary>
    private static object? CoerceToType(object? value, XdmSequenceType targetType)
    {
        if (value == null)
            return null;
        // The empty sequence has two representations here — null and a zero-length array —
        // and coercing it is a no-op for either. Without this, a zero-length array missed the
        // per-item branch below (guarded on Length > 1) and fell through to the scalar arms,
        // where ItemType.String => StringValueOf(array) manufactured a single zero-length
        // string: "no items" silently became "one empty item". The two representations are not
        // interchangeable at the source level, so this is reachable only from callers that
        // produce the array form — a typed with-param whose sequence-constructor body yields
        // nothing returns Array.Empty, while the same param written select="()" returns null.
        if (value is object?[] { Length: 0 })
            return value;
        // For sequence types (*, +), coerce each item individually rather than
        // joining the whole array into a single value (e.g., xs:string* should
        // produce an array of strings, not one space-joined string).
        if (value is object?[] arr && arr.Length > 1 &&
            targetType.Occurrence is Occurrence.ZeroOrMore or Occurrence.OneOrMore)
        {
            var coerced = new object?[arr.Length];
            for (int i = 0; i < arr.Length; i++)
                coerced[i] = CoerceToType(arr[i], targetType);
            return coerced;
        }
        var itemType = targetType.ItemType;

        // Subtype substitution (XSLT 3.0 §9.3 / XPath sequence-type matching): when the value
        // already matches the declared type — including as a MORE specific subtype, e.g. an
        // xs:dayTimeDuration value satisfying as="xs:duration" — keep it as-is. Down-casting to
        // the declared base type would strip the subtype and break `instance of xs:dayTimeDuration`
        // (attr/as-0118). Only applies to atomic targets; node/item targets are handled below.
        if (IsCastableAtomicType(itemType)
            && XQuery.Execution.TypeCastHelper.MatchesItemType(value, itemType))
        {
            return value;
        }

        // Atomize a temporary tree (RTF) or node before atomic coercion. Per XSLT 3.0 §9.3,
        // a sequence-constructor body (or a select= expression yielding nodes) bound to an
        // atomic `as=` type is atomized to its string/typed value, not left as the tree.
        // For the numeric/G*/untypedAtomic cases below this is a no-op (they already went
        // through StringValueOf); it matters for the date/time/duration/anyURI/float/QName/
        // binary types handled by the fall-through cast arm, which otherwise left the raw tree.
        if (IsCastableAtomicType(itemType)
            && value is ResultTreeFragment or Xdm.Nodes.XdmNode or Xdm.TextNodeItem)
        {
            value = StringValueOf(value);
        }

        return itemType switch
        {
            ItemType.String => StringValueOf(value),
            ItemType.Integer => value is long ? value : long.TryParse(StringValueOf(value), out var l) ? l : value,
            // xs:integer is a subtype of xs:decimal — accept int/long without conversion
            ItemType.Decimal => value is decimal or int or long ? value : decimal.TryParse(StringValueOf(value), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : value,
            // Delegate double/float to the canonical caster so INF/-INF/NaN lexical forms and
            // numeric promotion are handled (attr/as-0113 func3: xs:double('INF')).
            ItemType.Double => value is double ? value : TryCastToAtomicLenient(value, ItemType.Double),
            ItemType.Float => value is float ? value : TryCastToAtomicLenient(value, ItemType.Float),
            ItemType.Boolean => CoerceToBoolean(value),
            ItemType.GYear => value is Xdm.XsGYear ? value : new Xdm.XsGYear(StringValueOf(value)),
            ItemType.GYearMonth => value is Xdm.XsGYearMonth ? value : new Xdm.XsGYearMonth(StringValueOf(value)),
            ItemType.GMonthDay => value is Xdm.XsGMonthDay ? value : new Xdm.XsGMonthDay(StringValueOf(value)),
            ItemType.GDay => value is Xdm.XsGDay ? value : new Xdm.XsGDay(StringValueOf(value)),
            ItemType.GMonth => value is Xdm.XsGMonth ? value : new Xdm.XsGMonth(StringValueOf(value)),
            ItemType.UntypedAtomic => value is Xdm.XsUntypedAtomic ? value : new Xdm.XsUntypedAtomic(StringValueOf(value)),
            // Remaining atomic types (date/time/dateTime/duration/dayTimeDuration/
            // yearMonthDuration/anyURI/float/QName/hexBinary/base64Binary): delegate to the
            // canonical XQuery caster so the value carries the proper typed representation that
            // `instance of xs:TYPE` recognizes. Lenient on failure (return the value unchanged)
            // to match the existing numeric arms; a genuine type mismatch is caught downstream
            // by ValidateValueMatchesType.
            _ when IsCastableAtomicType(itemType) => TryCastToAtomicLenient(value, itemType),
            _ => value
        };
    }


    /// <summary>
    /// True for atomic item types that <see cref="CoerceToType"/> can atomize-and-cast toward.
    /// Deliberately excludes node/item/map/array/function/record/union types (which must keep
    /// their nodes) and the schema-only pseudo-types.
    /// </summary>
    private static bool IsCastableAtomicType(ItemType type) => type is
        ItemType.String or ItemType.Boolean or ItemType.Integer or ItemType.Decimal
        or ItemType.Double or ItemType.Float or ItemType.Date or ItemType.DateTime
        or ItemType.Time or ItemType.Duration or ItemType.YearMonthDuration
        or ItemType.DayTimeDuration or ItemType.QName or ItemType.AnyUri
        or ItemType.UntypedAtomic or ItemType.GYearMonth or ItemType.GYear
        or ItemType.GMonthDay or ItemType.GDay or ItemType.GMonth
        or ItemType.HexBinary or ItemType.Base64Binary or ItemType.AnyAtomicType;


    /// <summary>
    /// Applies an <c>as=</c> atomic-type declaration to a <c>select=</c> (or otherwise computed)
    /// value: atomizes nodes/RTFs and casts each item to the declared atomic type. Node/item/map/
    /// array/function target types are returned unchanged so their nodes are preserved. Used by the
    /// global-variable <c>select</c> path, which previously stored the raw node/typed value and so
    /// failed <c>instance of xs:TYPE</c> for e.g. <c>select="/doc/item/@a" as="xs:untypedAtomic"</c>
    /// or <c>select="/doc/item" as="xs:duration"</c>.
    /// </summary>
    internal static object? CoerceSelectValueToDeclaredType(object? value, XdmSequenceType? asType)
    {
        if (asType == null || value == null || !IsCastableAtomicType(asType.ItemType))
            return value;
        return CoerceToType(value, asType);
    }


    /// <summary>
    /// Coerces an arbitrary value to xs:boolean. Recognizes the lexical forms accepted
    /// by xs:boolean (<c>"true"</c>, <c>"false"</c>, <c>"1"</c>, <c>"0"</c>) regardless
    /// of whether the source is xs:string, xs:untypedAtomic, or already a .NET bool.
    /// Previously only xs:untypedAtomic was converted, which broke
    /// <c>&lt;xsl:variable as="xs:boolean"&gt;...&lt;xsl:sequence select="false()"/&gt;...&lt;/xsl:variable&gt;</c>:
    /// the body's xs:boolean was serialized to text output as <c>"false"</c>, then
    /// failed to coerce back. Found in Docbook TNG <c>$process</c> evaluation.
    /// </summary>
    private static object CoerceToBoolean(object? value)
    {
        return value switch
        {
            bool b => b,
            Xdm.XsUntypedAtomic ua => StringValueOf(ua) is "true" or "1",
            string s => s is "true" or "1" ? true
                       : s is "false" or "0" ? (object)false
                       : value!, // unrecognized lexical form — leave alone, downstream check will catch
            _ => value!
        };
    }


    private static bool IsStrictAtomicType(ItemType type) => type is
        ItemType.Integer or ItemType.Double or ItemType.Float or ItemType.Decimal
        or ItemType.Date or ItemType.DateTime or ItemType.Time
        or ItemType.Duration or ItemType.YearMonthDuration or ItemType.DayTimeDuration
        or ItemType.Boolean or ItemType.QName;


    /// <summary>Public alias used by the engine's global-variable initialization path.</summary>
    internal static bool IsStrictAtomicTypePublic(ItemType type) => IsStrictAtomicType(type);


    /// <summary>Public alias used by the engine's global-variable initialization path.</summary>
    internal static bool IsCastableAtomicTypePublic(ItemType type) => IsCastableAtomicType(type);


    /// <summary>
    /// Returns true for any atomic type (strict + string/anyURI/untypedAtomic).
    /// Used for TextNodeItem coercion in function return values — atomizing a text
    /// node always produces a string, which can then be cast to the target type.
    /// </summary>
    private static bool IsAtomicReturnType(ItemType type) =>
        IsStrictAtomicType(type) || type is ItemType.String or ItemType.AnyUri
            or ItemType.UntypedAtomic or ItemType.AnyAtomicType;


    private static bool IsNodeType(ItemType type) => type is
        ItemType.Node or ItemType.Element or ItemType.Attribute or ItemType.Text
        or ItemType.Comment or ItemType.ProcessingInstruction or ItemType.Document;


    /// <summary>
    /// Validates that function item(s) passed as arguments are coercible to the required
    /// typed function type. Per XSLT 3.0 §5.4.3, function coercion checks arity but
    /// allows broader declared types — the wrapper validates at invocation time.
    /// Raises XPTY0004 only for arity mismatches (non-coercible).
    /// </summary>
    private static void ValidateFunctionTypeArgument(object? value, XdmSequenceType targetType,
        string paramName, string funcName)
    {
        if (value == null) return;

        var requiredArity = targetType.FunctionParameterTypes!.Count;
        var items = value is object?[] arr ? arr : new[] { value };
        foreach (var item in items)
        {
            if (item is PhoenixmlDb.XQuery.Ast.XQueryFunction fn)
            {
                // Function coercion only fails for arity mismatch (XSLT 3.0 §5.4.11)
                // Type incompatibilities are detected later when the coerced function is invoked
                if (fn.Arity != requiredArity)
                {
                    throw new XsltException(
                        $"XPTY0004: Function item passed to parameter ${paramName} in function {funcName} " +
                        $"has arity {fn.Arity} but required type expects arity {requiredArity}");
                }
            }
            else if (item != null)
            {
                // Non-function item where function expected
                throw new XsltException(
                    $"XPTY0004: Non-function item passed to parameter ${paramName} in function {funcName} " +
                    $"where function type is required");
            }
        }
    }


    /// <summary>
    /// Checks if a value is compatible with a target type after applying
    /// XSLT function conversion rules (including numeric promotion and untypedAtomic casting).
    /// </summary>
    private static bool IsTypeCompatible(object? value, ItemType targetType)
    {
        if (value == null)
            return false;
        if (XQuery.Execution.TypeCastHelper.MatchesItemType(value, targetType))
            return true;

        // UntypedAtomic → any atomic type (per XSLT function conversion rules)
        if (value is Xdm.XsUntypedAtomic)
            return true;

        // Numeric promotion: integer → double, integer → decimal, float → double
        return targetType switch
        {
            ItemType.Double => value is int or long or float or decimal,
            ItemType.Decimal => value is int or long,
            ItemType.Float => value is int or long,
            _ => false
        };
    }


    /// <summary>
    /// Validates that a value matches the declared type after coercion.
    /// Only validates strict atomic types to avoid false positives with
    /// string/node/anyURI/untypedAtomic coercion paths.
    /// </summary>
    /// <summary>
    /// True when a bound value carries no items — either a null or an array holding only nulls.
    /// A zero-length string is a single item, not an empty sequence, and is excluded.
    /// </summary>
    private static bool IsEmptySequenceValue(object? value)
        => value switch
        {
            null => true,
            object?[] items => Array.TrueForAll(items, static i => i == null),
            _ => false,
        };


    /// <summary>Renders an occurrence indicator as the phrase used in an XTTE0570 message.</summary>
    private static string DescribeRequiredCardinality(Occurrence occurrence)
        => occurrence == Occurrence.OneOrMore ? "one or more items" : "exactly one item";


    internal void ValidateValueMatchesType(object? value, XdmSequenceType targetType, string errorCode, string contextName)
    {
        // Validate node types: when the target is a node type, non-node values must fail.
        // Only apply to with-param/tunnel-param paths (XTTE0590), not default value paths
        // (XTTE0600) since content constructors may not properly capture all node types.
        if (IsNodeType(targetType.ItemType) && errorCode is "XTTE0590" or "XTTE0780")
        {
            if (value == null)
            {
                if (targetType.Occurrence is Occurrence.ExactlyOne or Occurrence.OneOrMore)
                    throw Error($"{errorCode}: {contextName} requires a node but the empty sequence was supplied");
                return;
            }
            // Check single value
            if (value is not object?[])
            {
                ValidateSingleNodeAgainstType(value, targetType, errorCode, contextName);
                return;
            }
            // For sequences, check each item
            if (value is object?[] nodeArr)
            {
                foreach (var item in nodeArr)
                    if (item != null)
                        ValidateSingleNodeAgainstType(item, targetType, errorCode, contextName);
                return;
            }
            return;
        }

        // Only validate strict atomic types — string, anyURI, etc. have
        // complex coercion rules that can't be validated simply after CoerceToType
        if (!IsStrictAtomicType(targetType.ItemType))
            return;

        if (value == null)
        {
            if (targetType.Occurrence == Occurrence.ExactlyOne || targetType.Occurrence == Occurrence.OneOrMore)
                throw Error($"{errorCode}: {contextName} requires a value of type {targetType.ItemType} but the empty sequence was supplied");
            return;
        }

        // For arrays (sequences), check each item
        if (value is object?[] arr)
        {
            if (arr.Length == 0 && (targetType.Occurrence == Occurrence.ExactlyOne || targetType.Occurrence == Occurrence.OneOrMore))
                throw Error($"{errorCode}: {contextName} requires a value of type {targetType.ItemType} but the empty sequence was supplied");

            foreach (var item in arr)
            {
                if (item != null && !IsTypeCompatible(item, targetType.ItemType))
                    throw Error($"{errorCode}: {contextName} requires type {targetType.ItemType} but got {item.GetType().Name}");
            }
            return;
        }

        // Single item — check type with promotion rules
        if (!IsTypeCompatible(value, targetType.ItemType))
            throw Error($"{errorCode}: {contextName} requires type {targetType.ItemType} but got {value.GetType().Name}");
    }


    /// <summary>
    /// Per XSLT 3.0 §3.7 (sequence-type matching): validates a single value against a node-typed
    /// declaration, including element/attribute name and namespace constraints (element(QName),
    /// attribute(QName), document-node(element(QName)), processing-instruction("name")).
    ///
    /// The node-type branch of <see cref="ValidateValueMatchesType"/> previously only checked
    /// "is it a node?" — it accepted strings, ResultTreeFragments, and elements in the wrong
    /// namespace. That gap was the reason several recent typed-shape bugs surfaced only as
    /// downstream XPTY0020 axis-step errors instead of XTTE0780 at the construction boundary.
    /// </summary>
    private void ValidateSingleNodeAgainstType(object value, XdmSequenceType targetType, string errorCode, string contextName)
    {
        // ResultTreeFragment is opaque markup — we accept it for any node type since the
        // caller will (re)parse on first axis access. Refining ResultTreeFragment further
        // would force eager parsing of every variable body for the validator's sake.
        if (value is ResultTreeFragment)
            return;

        if (value is not XdmNode node)
            throw Error($"{errorCode}: {contextName} requires type {targetType.ItemType} but got {value.GetType().Name}");

        var matches = (targetType.ItemType, node) switch
        {
            (ItemType.Node, _) => true,
            (ItemType.Element, XdmElement el) =>
                MatchesElementName(el, targetType.ElementName, targetType.ElementNamespace),
            (ItemType.Attribute, XdmAttribute attr) =>
                (targetType.AttributeName == null || attr.LocalName == targetType.AttributeName)
                && (targetType.AttributeNamespace == null
                    || (_nodeStore?.GetNamespaceUri(attr.Namespace) ?? "") == targetType.AttributeNamespace),
            (ItemType.Text, XdmText) => true,
            (ItemType.Comment, XdmComment) => true,
            (ItemType.ProcessingInstruction, XdmProcessingInstruction pi) =>
                targetType.PIName == null || pi.Target == targetType.PIName,
            (ItemType.Document, XdmDocument doc) =>
                targetType.DocumentElementName == null
                || doc.DocumentElementLocalName == targetType.DocumentElementName,
            _ => false
        };

        if (!matches)
        {
            var what = node switch
            {
                XdmElement el => el.Prefix != null
                    ? $"element {el.Prefix}:{el.LocalName} (Q{{{_nodeStore?.GetNamespaceUri(el.Namespace) ?? ""}}}{el.LocalName})"
                    : $"element {el.LocalName} in namespace \"{_nodeStore?.GetNamespaceUri(el.Namespace) ?? ""}\"",
                XdmAttribute attr => $"attribute {attr.LocalName}",
                XdmText => "text node",
                XdmComment => "comment",
                XdmProcessingInstruction pi => $"processing-instruction({pi.Target})",
                XdmDocument doc => $"document-node(element({doc.DocumentElementLocalName ?? "?"}))",
                _ => node.GetType().Name
            };
            var declared = targetType.ElementName != null
                ? $"{targetType.ItemType}({(targetType.ElementNamespace != null ? "Q{" + targetType.ElementNamespace + "}" : "")}{targetType.ElementName})"
                : targetType.ItemType.ToString();
            throw Error($"{errorCode}: {contextName} requires type {declared} but got {what}");
        }
    }


    private static bool IsNumeric(object? value)
    {
        return value is byte or sbyte or short or ushort or int or uint
            or long or ulong or float or double or decimal or System.Numerics.BigInteger;
    }


    private static bool IsXmlWhitespaceOnly(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return true;
        foreach (var c in value)
        {
            if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                return false;
        }
        return true;
    }


    /// <summary>
    /// Validates the return value of an xsl:function against its declared as type (XTTE0780).
    /// </summary>
    private void ValidateFunctionReturnType(object? result, XsltFunction func)
    {
        if (func.As == null)
            return;
        ValidateValueMatchesType(result, func.As, "XTTE0780", $"Function {func.Name.LocalName} return value");
    }


    /// <summary>
    /// Produces a concise node description for trace output.
    /// </summary>
    private static string DescribeTraceNode(object? node)
    {
        return node switch
        {
            XdmDocument => "document-node()",
            XdmElement elem => elem.Prefix != null ? $"{elem.Prefix}:{elem.LocalName}" : elem.LocalName,
            XdmAttribute attr => $"@{attr.LocalName}",
            XdmText t => $"text(\"{Truncate(t.Value, 20)}\")",
            XdmComment => "comment()",
            XdmProcessingInstruction pi => $"processing-instruction({pi.Target})",
            System.Xml.Linq.XElement xe => xe.Name.LocalName,
            System.Xml.Linq.XDocument => "document-node()",
            string s => $"\"{Truncate(s, 20)}\"",
            null => "(empty)",
            _ => node.GetType().Name
        };

        static string Truncate(string s, int max)
        {
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "...");
        }
    }

}
