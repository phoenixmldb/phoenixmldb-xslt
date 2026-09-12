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

/// <summary>
/// Default implementation of XSLT execution context.
/// </summary>
internal sealed partial class DefaultXsltExecutionContext : XsltExecutionContext
{
    /// <summary>
    /// The engine that created this context. Used by the xsl:result-document path to route
    /// secondary-document serialization through the shared <see cref="XsltTransformEngine.FinalizeOutput"/>
    /// pipeline (the engine owns the character-map application that depends on the stylesheet).
    /// </summary>
    internal XsltTransformEngine? Owner { get; set; }


    /// <summary>
    /// SP-B slice 4: opens <paramref name="name"/>/<paramref name="nsUri"/> as an element on the
    /// active constructor BEFORE its content executes, so child text/comment/PI/nested elements
    /// build natively into it in document order. The frame stays open (and
    /// <see cref="_activeTreeConstructor"/> stays active) through content; namespaces, attributes,
    /// and any prefix fixup are applied at <see cref="TcFinishElement"/> before the frame is
    /// sealed.
    /// </summary>
    private void TcOpenElement(TreeConstructor tc, string name, string? nsUri)
    {
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        var local = colon > 0 ? name[(colon + 1)..] : name;
        var prefix = colon > 0 ? name[..colon] : null;
        var ns = string.IsNullOrEmpty(nsUri) ? NamespaceId.None : _nodeStore!.InternNamespace(nsUri);
        tc.StartElement(ns, local, prefix);
    }


    /// <summary>
    /// SP-B slice 4: seals an element opened by <see cref="TcOpenElement"/> whose children have
    /// already built natively during content. Applies the string path's finalized namespace
    /// declarations and (deduplicated, last-wins) attributes to the still-open frame, mirrors any
    /// post-content prefix fixup, preserves an <c>xsl:copy</c> source base URI, then ends the
    /// element. <paramref name="abort"/> (an attribute prefix that couldn't be resolved) marks the
    /// whole body incomplete so the differential skips it rather than reporting a false divergence.
    /// </summary>
    private void TcFinishElement(
        TreeConstructor tc,
        string name,
        List<(string Prefix, NamespaceId Ns)> nsDecls,
        List<(NamespaceId Ns, string Local, string? Prefix, string Value)> attrs,
        bool abort,
        string? copySourceBaseUri = null)
    {
        // Namespace fixup (§11.7) may have renamed the element's prefix after content executed;
        // keep the open frame's name in sync (expanded name is unchanged).
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        tc.SetOpenElementPrefix(colon > 0 ? name[..colon] : null);
        foreach (var (prefix, ns) in nsDecls)
            tc.AddNamespace(prefix, ns);
        foreach (var (ns, l, p, v) in attrs)
            tc.AddAttribute(ns, l, p, v);
        if (copySourceBaseUri != null)
            tc.SetCopySourceBaseUri(copySourceBaseUri);
        tc.EndElement();
        if (abort)
            _tcFragmentIncomplete = true;
    }


    /// <summary>
    /// SP-B slice 4: marks the active constructor's body incomplete (see
    /// <see cref="_tcFragmentIncomplete"/>) when a node-producing construct emits to the string
    /// buffer without routing into the node tree. Called from the copy/copy-of paths so the
    /// differential skips bodies whose live tree cannot be complete. Inert when no constructor
    /// is active (production) or when nothing is open to be built.
    /// </summary>
    private void MarkTcIncompleteIfActive()
    {
        if (_activeTreeConstructor != null && !_suppressTcIncomplete)
            _tcFragmentIncomplete = true;
    }


    public Dictionary<QName, object?> GlobalVariables { get; } = new();


    private TypedBodyState EnterTypedBody(XdmSequenceType? declaredType)
    {
        var saved = new TypedBodyState
        {
            LogicalStart = _outputLogicalStart,
            DocumentNodeDepth = _documentNodeDepth,
            TextContentDepth = _textContentDepth,
            AttributeContentDepth = _attributeContentDepth,
            SerializingElementDepth = _serializingElementDepth,
            CollectTextAsSequenceItems = _collectTextAsSequenceItems,
            LastResultWasAtomic = _lastResultWasAtomic,
            CollectedAttributes = new List<StringBuilder>(_collectedAttributesStack),
            ActiveTreeConstructor = _activeTreeConstructor,
            TcFragmentIncomplete = _tcFragmentIncomplete,
            SuppressTcIncomplete = _suppressTcIncomplete,
            UntypedRtfFlipActive = _untypedRtfFlipActive,
            UntypedRtfFlipDivergent = _untypedRtfFlipDivergent,
        };

        // Declare this body's base: content below it belongs to an enclosing scope and must
        // never be sliced or truncated by anything running inside.
        _outputLogicalStart = _output.Length;

        if (declaredType?.ItemType != ItemType.Document)
            _documentNodeDepth = 0;

        // A body whose declared type admits multiple items INCLUDING text must keep each text
        // write as its own item so it stays a text node rather than being merged into the buffer.
        if (declaredType != null
            && declaredType.ItemType is ItemType.Text or ItemType.Node or ItemType.Item
            && declaredType.Occurrence is Occurrence.ZeroOrMore or Occurrence.OneOrMore)
        {
            _collectTextAsSequenceItems = true;
            _serializingElementDepth = 0;
        }

        // The body is not inside an attribute/comment/PI value, whatever the caller was doing.
        _textContentDepth = 0;
        _attributeContentDepth = 0;
        _lastResultWasAtomic = false;
        _collectedAttributesStack.Clear();

        // The body's value is assembled from its OWN channels, so it must not build into an
        // enclosing temp tree's constructor. Only the two xsl:variable seams installed a
        // constructor of their own; the function and typed-param seams inherited the caller's,
        // and xsl:copy-of inside a function body cloned straight into the tree the caller was
        // building — before the function's result was copied in again, so
        //   <xsl:variable name="v"><xsl:copy-of select="f:wrap($nodes)"/></xsl:variable>
        // held two <a> elements where f:wrap returns one. It also left _untypedRtfFlipActive
        // set inside a typed body, which that flag's contract rules out. A seam that wants a
        // constructor installs its own after this.
        _activeTreeConstructor = null;
        _tcFragmentIncomplete = false;
        _suppressTcIncomplete = false;
        _untypedRtfFlipActive = false;
        _untypedRtfFlipDivergent = false;

        return saved;
    }


    /// <summary>Restores the state captured by <see cref="EnterTypedBody"/>.</summary>
    private void ExitTypedBody(in TypedBodyState saved)
    {
        _outputLogicalStart = saved.LogicalStart;
        _documentNodeDepth = saved.DocumentNodeDepth;
        _textContentDepth = saved.TextContentDepth;
        _attributeContentDepth = saved.AttributeContentDepth;
        _serializingElementDepth = saved.SerializingElementDepth;
        _collectTextAsSequenceItems = saved.CollectTextAsSequenceItems;
        _lastResultWasAtomic = saved.LastResultWasAtomic;
        _collectedAttributesStack.Clear();
        for (var i = saved.CollectedAttributes.Count - 1; i >= 0; i--)
            _collectedAttributesStack.Push(saved.CollectedAttributes[i]);
        _activeTreeConstructor = saved.ActiveTreeConstructor;
        _tcFragmentIncomplete = saved.TcFragmentIncomplete;
        _suppressTcIncomplete = saved.SuppressTcIncomplete;
        _untypedRtfFlipActive = saved.UntypedRtfFlipActive;
        _untypedRtfFlipDivergent = saved.UntypedRtfFlipDivergent;
    }


    /// <summary>
    /// True when execution is DIRECTLY inside a typed `as="item()*"`/`node()*` (etc.) body
    /// whose capture accumulator is the live <c>_sequenceAccumulator</c> — the context that
    /// base-uri-053's doc-node / element deep-copy builders must engage in (copy results append
    /// into that typed sequence and are delivered as node items). It is FALSE for an untyped-RTF
    /// body — <em>whether flip-active or blocked</em> — because that seam installs its OWN fresh
    /// <c>_sequenceAccumulator</c> (so a leaked outer capture no longer reference-matches) and
    /// never installs a capture at top level; those bodies must keep the legacy serialize-reparse
    /// dispatch. This is the correct signal to gate the new builders on: <c>_untypedRtfFlipActive</c>
    /// is a flip-STATE flag that is also false in a BLOCKED untyped-RTF body, so it would wrongly
    /// let the new builders run there. Mirrors the <see cref="AppendToSeqAccumulator"/> guard.
    /// </summary>
    private bool InTypedAsBodyAccumulator =>
        _currentAsBodyCapture is { } cap && ReferenceEquals(_sequenceAccumulator, cap.Accumulator);


    /// <summary>
    /// Try to resolve an expression (or its sub-expressions) from watcher results.
    /// Handles direct matches and map constructors with watcher entry values.
    /// </summary>
    /// <summary>
    /// Group A: returns the watcher whose <c>SourceExpression</c> is exactly
    /// <paramref name="expr"/> (reference identity), or null. Used by the resolve
    /// path to surface a wrapped-aggregation watcher carrying an outer op
    /// (predicates / SimpleMap RIGHT) to apply to the grounded accumulated sequence.
    /// </summary>
    private static StreamWatcher? FindDirectWatcher(
        PhoenixmlDb.XQuery.Ast.XQueryExpression expr,
        IReadOnlyList<StreamWatcher> watchers)
    {
        foreach (var watcher in watchers)
            if (ReferenceEquals(expr, watcher.SourceExpression))
                return watcher;
        return null;
    }


    /// <summary>
    /// Rebuilds a (possibly chained) SimpleMap <c>path ! R1 ! R2 …</c> with its deepest
    /// LEFT operand (the striding base path) replaced by <paramref name="replacement"/>,
    /// preserving every per-item tail step. Used to substitute a SimpleMap-source watcher
    /// variable while keeping the tail (<c>$watcher ! R1 ! R2</c>).
    /// </summary>
    private static PhoenixmlDb.XQuery.Ast.SimpleMapExpression ReplaceSimpleMapLeftmost(
        PhoenixmlDb.XQuery.Ast.SimpleMapExpression sm,
        XQueryExpression replacement)
    {
        var newLeft = sm.Left is PhoenixmlDb.XQuery.Ast.SimpleMapExpression innerSm
            ? ReplaceSimpleMapLeftmost(innerSm, replacement)
            : replacement;
        return new PhoenixmlDb.XQuery.Ast.SimpleMapExpression
        {
            Left = newLeft,
            Right = sm.Right,
            IsPathStep = sm.IsPathStep,
        };
    }


    /// <summary>
    /// Reads the string value of a named no-namespace attribute off a materialized
    /// <see cref="Xdm.Nodes.XdmElement"/> via the streaming node store; null when absent.
    /// </summary>
    private string? GetElementAttributeValue(Xdm.Nodes.XdmElement el, string localName)
    {
        if (_nodeStore == null) return null;
        foreach (var attrId in el.Attributes)
        {
            if (_nodeStore.GetNode(attrId) is Xdm.Nodes.XdmAttribute attr
                && attr.LocalName == localName)
                return attr.Value;
        }
        return null;
    }


    private static bool NumericLiteralEqualsPosition(XQueryExpression lit, int position) => lit switch
    {
        IntegerLiteral i => i.LongValue is { } l && l == position,
        DecimalLiteral d => d.Value == position,
        DoubleLiteral db => db.Value == position,
        _ => false
    };


    private static List<object?> NormalizeToList(object? value)
    {
        var list = new List<object?>();
        AppendFlattened(list, value);
        return list;
    }


    private static void AppendFlattened(List<object?> list, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case string:
            case XdmNode:
                list.Add(value);
                return;
            case object?[] arr:
                foreach (var it in arr) AppendFlattened(list, it);
                return;
            // An XDM array is a single item, represented as List<object?>; an XDM map is a
            // single item, represented as IDictionary. Neither flattens — only a CLR
            // object?[] (the engine's sequence representation) does. Without these guards a
            // top-level JSON array/map round-tripped through xsl:sequence was spread into
            // its members (Martin Honnen 2026-06-14).
            case List<object?>:
            case System.Collections.IDictionary:
                list.Add(value);
                return;
            case System.Collections.IEnumerable seq:
                foreach (var it in seq) AppendFlattened(list, it);
                return;
            default:
                list.Add(value);
                return;
        }
    }


    /// <summary>
    /// Materializes the result of <c>EvaluateAsync(select)</c> into the items that an
    /// item-processing instruction iterates over (xsl:for-each, xsl:iterate,
    /// xsl:for-each-group, xsl:perform-sort, xsl:merge for-each-* — and, analogously,
    /// xsl:sequence / xsl:apply-templates which handle this inline).
    /// <para>
    /// An XDM array (<see cref="List{T}"/> of object?) and an XDM map
    /// (<see cref="System.Collections.IDictionary"/>) are SINGLE items and are NOT
    /// flattened into their members/entries; <c>object?[]</c> is the engine's sequence
    /// representation and is flattened (dropping empty/null slots); any other
    /// <c>IEnumerable&lt;object&gt;</c> (e.g. a node sequence) is returned as-is.
    /// </para>
    /// <para>
    /// This is ONLY for item-preserving contexts. Atomizing contexts (xsl:value-of,
    /// xsl:number, string-value) must flatten an array to its members and must not use
    /// this. Reported by Martin Honnen 2026-06 across xsl:sequence / apply-templates /
    /// for-each-group; centralized here so the same flatten-the-array bug can't recur.
    /// </para>
    /// </summary>
    internal static IEnumerable<object> SelectResultItems(object? result) => result switch
    {
        null => [],
        string or XdmNode or List<object?> or System.Collections.IDictionary => [result!],
        object?[] arr => arr.Where(static x => x != null).Cast<object>(),
        IEnumerable<object> seq => seq,
        _ => [result],
    };


    /// <summary>
    /// Runs XPath general comparison between two atomized item lists. Raises
    /// XPTY0004 when atomic types are incompatible (e.g. xs:decimal vs
    /// xs:string with non-numeric content). Empty operand ⇒ false per spec.
    /// </summary>
    private static bool RunGeneralComparison(BinaryOperator op, List<object?> left, List<object?> right)
    {
        if (left.Count == 0 || right.Count == 0) return false;
        foreach (var l in left)
        {
            foreach (var r in right)
            {
                if (CompareAtomic(op, l, r)) return true;
            }
        }
        return false;
    }


    private static double ToWatcherDouble(object v) => v switch
    {
        int i => i,
        long l => l,
        double d => d,
        float f => f,
        decimal dc => (double)dc,
        _ => throw new InvalidOperationException()
    };


    /// <summary>The ambient operand usage (§19.6/§19.7) currently in effect at this point in the descent.</summary>
    internal Usage CurrentUsage => _posture.Current;


    /// <summary>
    /// Executes a streamed for-each body with the subscription's operand usage pushed for the
    /// duration, so posture-driven serialization (e.g. document-node §5.7.1 transmission) can
    /// consult <see cref="CurrentUsage"/>. Restores the prior usage on exit.
    /// </summary>
    internal async System.Threading.Tasks.ValueTask RunBodyWithPostureAsync(
        Ast.XsltSequenceConstructor body, Usage usage)
    {
        var saved = _posture.Push(usage);
        try
        {
            await body.ExecuteAsync(this).ConfigureAwait(false);
        }
        finally
        {
            _posture.Pop(saved);
        }
    }


    /// <summary>
    /// True when output is being written directly into the top level of a result sequence that
    /// carries an explicit item-separator: an override is set, and we are not inside a constructed
    /// element, attribute, simple-text content, or a sequence-value accumulator. In this state the
    /// separator is inserted between all adjacent items regardless of node/atomic kind.
    /// </summary>
    private bool InTopLevelItemSeparatorSequence
        => _itemSeparatorOverride != null
           && _serializingElementDepth == 0
           && _attributeContentDepth == 0
           && _textContentDepth == 0
           && _sequenceAccumulator == null;


    /// <summary>
    /// Seeds the item-separator override used by §5.7.2 sequence normalization from the
    /// principal xsl:output's item-separator serialization parameter, before the entry
    /// point runs. Instruction-level item-separator save/restores this field for its own
    /// content, so nested overrides still win locally.
    /// </summary>
    internal void SeedItemSeparatorOverride(string separator) => _itemSeparatorOverride = separator;

    private string? EffectiveBaseUri => _baseUriStack.Count > 0 ? _baseUriStack.Peek() : XsltTransformEngine.UriString(_stylesheet.BaseUri);


    private string? ComputeSourceBaseUri(Xdm.Nodes.XdmElement elem)
    {
        // Check for xml:base attribute on the element
        string? xmlBase = null;
        foreach (var attrId in elem.Attributes)
        {
            var attr = _nodeStore!.GetNode(attrId) as Xdm.Nodes.XdmAttribute;
            if (attr != null && attr.Namespace == NamespaceId.Xml && attr.LocalName == "base")
            {
                xmlBase = attr.Value;
                break;
            }
        }
        if (xmlBase != null)
        {
            // Resolve against parent's base URI if relative
            if (Uri.TryCreate(xmlBase, UriKind.Absolute, out _))
                return xmlBase;
            var parentBase = GetAncestorBaseUri(elem);
            if (parentBase != null && Uri.TryCreate(parentBase, UriKind.Absolute, out var parentUri)
                && Uri.TryCreate(parentUri, xmlBase, out var resolved))
                return resolved.OriginalString;
            return xmlBase;
        }
        // Fall back to entity-derived base URI
        if (elem.BaseUri != null)
            return elem.BaseUri;
        // Inherit from parent
        return GetAncestorBaseUri(elem);
    }


    private string? GetAncestorBaseUri(Xdm.Nodes.XdmNode node)
    {
        if (!node.Parent.HasValue || node.Parent.Value == NodeId.None || _nodeStore == null)
            return null;
        var parent = _nodeStore.GetNode(node.Parent.Value);
        if (parent is Xdm.Nodes.XdmElement parentElem)
            return ComputeSourceBaseUri(parentElem);
        if (parent is Xdm.Nodes.XdmDocument doc)
            return doc.BaseUri;
        return parent?.BaseUri;
    }

    internal string? StaticBaseUri => _staticBaseUriStack.Count > 0 ? _staticBaseUriStack.Peek() : XsltTransformEngine.UriString(_stylesheet.BaseUri);


    /// <summary>
    /// The source location of the XSLT instruction or XPath expression currently being
    /// evaluated, or <c>null</c> when no executor has installed one. Pushed via
    /// <see cref="PushInstructionLocation"/>; consumed by <see cref="Error(string)"/>
    /// to auto-attach module/line/column to <see cref="XsltException"/>.
    /// </summary>
    /// <remarks>
    /// Phase A of the source-location audit: storage and helpers exist; Phase B
    /// wires push/pop into individual executors. Existing set-and-forget writes to
    /// <c>_currentInstructionLocation</c> (e.g. <c>CreateAttributeAsync</c> for
    /// XTDE0410/0420) remain valid — Phase B replaces them with scoped pushes.
    /// </remarks>
    internal SourceLocation? CurrentInstructionLocation => _currentInstructionLocation;

    private StringBuilder? _collectedAttributes => _collectedAttributesStack.Count > 0 ? _collectedAttributesStack.Peek() : null;

    private bool _attributeCollecting => _collectedAttributesStack.Count > 0;


    public override QName? CurrentMode => _currentMode;

    public override string? DefaultCollation =>
        _defaultCollationStack.Count > 0 ? _defaultCollationStack.Peek() : _stylesheet.DefaultCollation;


    /// <summary>
    /// Checks cancellation token and output size limit. Called at loop boundaries
    /// (apply-templates per-node, for-each per-item, iterate per-item) and template entry
    /// to ensure runaway transformations are terminated promptly.
    /// </summary>
    private void CheckResourceLimits()
    {
        _ct.ThrowIfCancellationRequested();
        if (_maxOutputSize > 0 && _output.Length > _maxOutputSize)
            throw Error(
                $"XTRE0000: Output size limit exceeded ({_output.Length:N0} characters > {_maxOutputSize:N0} limit). " +
                "The transformation may contain an infinite loop or produce excessively large output.");

        // Guard the native stack. Deep user recursion (recursive stylesheet functions,
        // nested apply-templates) expands into ~15 .NET async frames per logical call,
        // so the physical stack can overflow — an uncatchable StackOverflowException that
        // aborts the whole process with SIGABRT — long before _recursionDepth reaches
        // MaxRecursionDepth. Probe the remaining stack and convert exhaustion into a
        // catchable engine error, mirroring the XQuery engine's
        // QueryExecutionContext.EnterFunctionCall guard.
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        }
        catch (InsufficientExecutionStackException ex)
        {
            throw Error(
                "XTDE0000: The transformation exhausted the execution stack before reaching " +
                $"the recursion-depth limit ({MaxRecursionDepth}). It may contain unbounded " +
                "recursion; if the recursion is intentional, run the host on a thread with a larger stack.",
                ex);
        }
    }

    // Maximum secondary result documents allowed (0 = unlimited). Security limit.
    internal int MaxResultDocuments { get; init; } = 1000;


    /// <summary>Secondary result documents produced by xsl:result-document with non-empty href.</summary>
    internal IReadOnlyDictionary<string, string> SecondaryResults => _secondaryResultDocuments;

    internal XsltOutput? PrimaryOutputMatchedDeclaration => _primaryOutputMatchedDeclaration;


    /// <summary>Character maps from xsl:result-document targeting principal output (persists for serialization).</summary>
    internal List<QName>? PrincipalOutputCharacterMaps => _principalOutputCharacterMaps;


    /// <summary>
    /// Builds xmlns:prefix="uri" declarations for all in-scope namespaces, for use as
    /// attributes on a synthetic wrapper element when re-parsing serialized template/function output.
    /// </summary>
    private string BuildInScopeNamespaceDeclarations()
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>();
        foreach (var scope in _outputNsScopes)
        {
            foreach (var (prefix, uri) in scope)
            {
                if (string.IsNullOrEmpty(uri) || !seen.Add(prefix))
                    continue;
                if (prefix.Length > 0)
                {
                    sb.Append(" xmlns:");
                    sb.Append(prefix);
                    sb.Append("=\"");
                    sb.Append(EscapeAttributeValue(uri));
                    sb.Append('"');
                }
                else
                {
                    sb.Append(" xmlns=\"");
                    sb.Append(EscapeAttributeValue(uri));
                    sb.Append('"');
                }
            }
        }

        // _outputNsScopes holds the scopes live RIGHT NOW. The `as=` body capture reparses its
        // serialized chunk only after the body has finished, by which point the scopes that were
        // in force while the body ran have already been popped — so a prefix the chunk uses can
        // be absent from the wrapper, the reparse throws "'x' is an undeclared prefix", and
        // AddBodyOutputChunk falls back to appending the chunk as a raw STRING. The typed
        // template then fails its own return-type check with XTTE0505, reporting a String where
        // it declared an element, and nothing anywhere mentions namespaces.
        //
        // That is XSpec's x:like template: it copies x:-prefixed elements out of a constructed
        // tree, and the fragment carries no xmlns:x. It accounted for XTTE0505 in 34 of its 162
        // suites.
        //
        // Backstop with the stylesheet's statically-declared prefixes, which is where those
        // names come from. Live output scopes still win — this only fills prefixes nothing else
        // supplied. Over-declaring on the wrapper is harmless: the wrapper itself is discarded
        // and only the prefixes a node actually uses end up on it.
        foreach (var (prefix, uri) in _stylesheet.Namespaces)
        {
            if (string.IsNullOrEmpty(uri) || string.IsNullOrEmpty(prefix) || !seen.Add(prefix))
                continue;
            sb.Append(" xmlns:");
            sb.Append(prefix);
            sb.Append("=\"");
            sb.Append(EscapeAttributeValue(uri));
            sb.Append('"');
        }

        return sb.ToString();
    }


    /// <summary>
    /// Appends an item to <c>_sequenceAccumulator</c> while also recording the current
    /// <c>_output</c> offset into <see cref="AsBodyCapture.Positions"/> when an `as=` body
    /// capture is in progress. The parallel position list is what
    /// <see cref="AssembleAsBodyResultItems"/> uses to weave accumulator items back into
    /// document order at result assembly time.
    /// </summary>
    private void AppendToSeqAccumulator(object? item)
    {
        // Record the `_output` offset only when the item is going into the current
        // `as=` body's accumulator — not into some inner accumulator (e.g. an
        // xsl:variable's typed-sequence buffer) that just happens to be active.
        if (_currentAsBodyCapture is { } capture && ReferenceEquals(_sequenceAccumulator, capture.Accumulator))
        {
            var offset = _output.Length - capture.OutputBaseLen;
            capture.Positions.Add(offset);
            capture.ConsumedTo.Add(offset);   // no output consumed unless the caller says so
        }
        _sequenceAccumulator!.Add(item);
    }


    /// <summary>
    /// Reassembles the result items of an `as=`-typed template/function body in document order.
    /// </summary>
    /// <remarks>
    /// During body execution, two output channels run in parallel: <c>_output</c> captures
    /// LRE/text serialization, while <c>_sequenceAccumulator</c> captures distinct XDM items
    /// (xsl:attribute → XdmAttribute, xsl:sequence → arbitrary items, xsl:document → XdmDocument).
    /// <paramref name="accumulatorPositions"/> records, for each accumulator entry, the
    /// <c>_output</c> offset (relative to body start) at the moment the entry was added.
    /// We walk those offsets in order, parsing the <paramref name="bodyOutput"/> chunk preceding
    /// each one, then emitting the corresponding accumulator item at its recorded position.
    /// This restores creation order so a subsequent element constructor sees attributes
    /// before non-attribute children, avoiding spurious XTDE0410. Reported by Martin Honnen
    /// against Schxslt2 1.10.3 transpile.xsl.
    /// </remarks>
    private List<object?> AssembleAsBodyResultItems(string bodyOutput, List<object?> accumulatorItems,
        List<int> accumulatorPositions, List<int>? consumedTo = null)
    {
        var result = new List<object?>();
        var cursor = 0;

        for (var i = 0; i < accumulatorItems.Count; i++)
        {
            var item = accumulatorItems[i];
            if (item == null)
                continue;
            var position = i < accumulatorPositions.Count ? accumulatorPositions[i] : bodyOutput.Length;
            if (position > bodyOutput.Length)
                position = bodyOutput.Length;
            if (position > cursor)
            {
                AddBodyOutputChunk(bodyOutput.Substring(cursor, position - cursor), result);
                cursor = position;
            }
            result.Add(item);
            // An item that serialized its own text owns that region; skip past it so the
            // trailing chunk below does not repeat it.
            if (consumedTo is not null && i < consumedTo.Count && consumedTo[i] > cursor)
                cursor = Math.Min(consumedTo[i], bodyOutput.Length);
        }

        if (cursor < bodyOutput.Length)
            AddBodyOutputChunk(bodyOutput[cursor..], result);

        return result;
    }


    /// <summary>
    /// Parses a chunk of serialized body output into top-level XDM nodes (or a raw string
    /// when parsing fails) and appends them to <paramref name="result"/>.
    /// </summary>
    private void AddBodyOutputChunk(string chunk, List<object?> result)
    {
        if (string.IsNullOrEmpty(chunk))
            return;

        if (_nodeStore != null && chunk.Contains('<', StringComparison.Ordinal))
        {
            try
            {
                // Stream the chunk through XmlReader instead of allocating an XmlDocument
                // and walking its DOM. The previous path allocated O(N) XmlNode objects per
                // call where N = nodes in the chunk; for an `xsl:variable as="element(…)"`
                // body inside a deep for-each (Schxslt2 / Dataverse-shape transforms), that
                // adds up to many thousands of XmlDocuments built and discarded per second.
                // Direct reader → XDM eliminates the DOM intermediary entirely.
                var nsDecls = BuildInScopeNamespaceDeclarations();
                var wrapped = $"<_tmpl_wrap_{nsDecls}>{chunk}</_tmpl_wrap_>";
                var settings = new System.Xml.XmlReaderSettings
                {
                    DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                    IgnoreWhitespace = false,
                    IgnoreComments = false,
                    IgnoreProcessingInstructions = false,
                };
                using var stringReader = new System.IO.StringReader(wrapped);
                using var reader = System.Xml.XmlReader.Create(stringReader, settings);
                ReadAsBodyChunkChildren(reader, result);
                return;
            }
            catch (System.Xml.XmlException)
            {
                // Fall through to add as raw string.
                //
                // NOTE: this fallback is silent by design but expensive when it fires — the
                // caller receives a String where it expected nodes, and a typed template then
                // reports XTTE0505 naming a type mismatch with no hint that a namespace was the
                // cause. If that error appears with a serialized-markup value, suspect a prefix
                // missing from the wrapper before suspecting the type check.
            }
        }

        result.Add(chunk);
    }


    /// <summary>
    /// Generates a unique namespace prefix by appending _0, _1, etc. to the base prefix.
    /// Used during namespace fixup when xsl:namespace conflicts with an element's own prefix.
    /// </summary>
    private string GenerateUniquePrefix(string basePrefix)
    {
        var counter = 0;
        string candidate;
        do
        {
            candidate = $"{basePrefix}_{counter}";
            counter++;
        } while (IsPrefixInUse(candidate));
        return candidate;
    }


    /// <summary>
    /// Gets the in-scope default namespace URI (for prefix ""), or null if none is in scope.
    /// </summary>
    private string? GetInScopeDefaultNamespace()
    {
        foreach (var scope in _outputNsScopes)
        {
            if (scope.TryGetValue("", out var uri))
            {
                // Empty string means xmlns="" (undeclaration) — return null to indicate no default ns
                return string.IsNullOrEmpty(uri) ? null : uri;
            }
        }
        return null;
    }


    /// <summary>
    /// Call this when "real" content is produced (elements, non-whitespace text, etc.)
    /// This marks the current where-populated scope as having content.
    /// </summary>
    private void MarkContentProduced()
    {
        if (_populatedTracking.Count > 0)
        {
            // Pop the current value and push true
            _populatedTracking.Pop();
            _populatedTracking.Push(true);
        }
    }


    /// <summary>
    /// Gets the current effective XSLT version (from xsl:version on LRE or stylesheet version).
    /// </summary>
    private string EffectiveVersion =>
        _effectiveVersionStack.Count > 0 ? _effectiveVersionStack.Peek() : _stylesheet.Version;


    /// <summary>
    /// Drains <c>_output</c> to <see cref="_streamingOutputSink"/> and resets its
    /// length to zero. Caller is responsible for invoking this only at points where
    /// no <see cref="XsltTransformEngine.ScopedOutputBuffer"/> is open — typically at
    /// the top of each <see cref="StreamingXmlProcessor.ProcessAsync"/> loop iteration,
    /// before reading the next XML event. No-op when no sink is attached or the
    /// buffer is empty.
    /// </summary>
    internal async ValueTask DrainStreamingOutputAsync(CancellationToken ct)
    {
        if (_streamingOutputSink == null) return;
        if (_output.Length == 0) return;
        var chunk = _output.ToString();
        _output.Clear();
        _outputLogicalStart = 0;
        await _streamingOutputSink.WriteAsync(chunk.AsMemory(), ct).ConfigureAwait(false);
    }


    /// <summary>
    /// Checks the resource policy for text access and tries the custom resolver first.
    /// Returns null to indicate the caller should fall through to default file-based resolution.
    /// Throws <see cref="PhoenixmlDb.XQuery.Security.ResourceAccessDeniedException"/> if denied.
    /// </summary>
    internal string? ResolveUnparsedTextViaPolicy(string href, string? encoding)
    {
        if (_policyResolver == null)
            return null; // No policy — fall through to default

        return _policyResolver.ResolveText(href, encoding);
    }


    /// <summary>
    /// Checks the resource policy for write access (xsl:result-document).
    /// Throws <see cref="PhoenixmlDb.XQuery.Security.ResourceAccessDeniedException"/> if denied.
    /// </summary>
    internal void CheckResultDocumentPolicy(string href)
    {
        _policyResolver?.CheckWriteAccess(href);
    }


    private PhoenixmlDb.XQuery.Functions.FunctionLibrary BuildFunctionLibrary(XsltStylesheet stylesheet)
    {
        var lib = new PhoenixmlDb.XQuery.Functions.FunctionLibrary();

        // Copy all standard functions
        foreach (var func in PhoenixmlDb.XQuery.Functions.FunctionLibrary.Standard.GetAllFunctions())
            lib.Register(func);

        // Register all namespace URI → NamespaceId mappings so EQName function calls (Q{uri}local)
        // can resolve to user-defined functions (includes URIs from child elements like xsl:function)
        foreach (var (uri, nsId) in StylesheetParser.DynamicNamespaces)
            lib.RegisterNamespaceUri(uri, nsId);
        foreach (var (prefix, uri) in stylesheet.Namespaces)
        {
            var nsId = StylesheetParser.ResolveNamespaceUri(uri);
            if (nsId != NamespaceId.None)
                lib.RegisterPrefix(prefix, nsId);
        }

        // Register XSLT user-defined functions (xsl:function)
        foreach (var (key, xsltFunc) in stylesheet.Functions)
        {
            var adapter = new XsltUserFunctionAdapter(xsltFunc, this);
            lib.Register(adapter);
            // Register prefix-to-namespace mapping so XPath can resolve prefixed calls
            if (xsltFunc.Name.Prefix != null && xsltFunc.Name.Namespace != NamespaceId.None)
                lib.RegisterPrefix(xsltFunc.Name.Prefix, xsltFunc.Name.Namespace);
        }

        // An abstract function exists as a component but has no body. Registering nothing left
        // the call site reporting "function not found" — a name nothing declares — where the
        // answer is the dynamic XTDE3052 (accept-041b/c, accept-901..904).
        foreach (var key in stylesheet.AbstractFunctionKeys)
        {
            if (!stylesheet.Functions.ContainsKey(key))
                lib.Register(new XsltAbstractFunctionAdapter(key.Name, key.Arity));
        }

        // Register xsl:original function for package overrides — resolved dynamically
        // at runtime via _currentXsltFunctionStack
        lib.Register(new XsltOriginalFunctionAdapter(this));

        // Register XSLT-specific built-in functions
        var rootFunc = new XsltRootFunction(this);
        var root0Func = new XsltRoot0Function(this);
        root0Func.SetRootFunc(rootFunc);
        lib.Register(rootFunc);
        lib.Register(root0Func);
        lib.Register(new XsltGenerateIdFunction(this));
        lib.Register(new XsltGenerateId0Function(this));
        lib.Register(new XsltCurrentFunction(this));
        lib.Register(new XsltCurrentGroupFunction(this));
        lib.Register(new XsltCurrentGroupingKeyFunction(this));
        lib.Register(new XsltCurrentMergeGroupFunction(this));
        lib.Register(new XsltCurrentMergeGroup1Function(this));
        lib.Register(new XsltCurrentMergeKeyFunction(this));
        lib.Register(new XsltRegexGroupFunction(this));
        lib.Register(new XsltFunctionAvailableFunction(lib));
        lib.Register(new XsltElementAvailableFunction(_stylesheet.ExtensionElementPrefixes));
        lib.Register(new XsltSystemPropertyFunction());
        lib.Register(new XsltAvailableSystemPropertiesFunction());
        lib.Register(new XsltTypeAvailableFunction());
        lib.Register(new XsltUnparsedTextFunction(this));
        lib.Register(new XsltUnparsedText2Function(this));
        lib.Register(new XsltUnparsedTextAvailableFunction(this));
        lib.Register(new XsltUnparsedTextAvailable2Function(this));
        lib.Register(new XsltUnparsedTextLinesFunction(this));
        lib.Register(new XsltUnparsedTextLines2Function(this));
        lib.Register(new XsltCurrentOutputUriFunction(this));
        lib.Register(new XsltUnparsedEntityUriFunction(this));
        lib.Register(new XsltUnparsedEntityPublicIdFunction(this));
        lib.Register(new XsltStreamAvailableFunction(this));
        lib.Register(new XsltDocumentFunction(this));
        lib.Register(new XsltDocument2Function(this));
        lib.Register(new XsltKeyFunction(this));
        lib.Register(new XsltKey3Function(this));
        lib.Register(new XsltAccumulatorBeforeFunction(this));
        lib.Register(new XsltAccumulatorAfterFunction(this));
        lib.Register(new XsltLangFunction(this));
        lib.Register(new XsltLang2Function(this));
        lib.Register(new XsltDeepEqualFunction(this));
        lib.Register(new XsltOutermostFunction(this));
        lib.Register(new XsltInnermostFunction(this));
        lib.Register(new XsltSnapshotFunction(this));
        lib.Register(new XsltSnapshot0Function(this));
        lib.Register(new XsltFormatNumberFunction(this));
        lib.Register(new XsltFormatNumber3Function(this));
        lib.Register(new XsltCopyOfFunction(this));
        lib.Register(new XsltCopyOf0Function(this));
        lib.Register(new XsltPathFunction(this));
        lib.Register(new XsltPath0Function(this));
        lib.Register(new XsltNilledFunction());
        lib.Register(new XsltNilled0Function(this));
        lib.Register(new XsltDocumentUri0Function(this));
        lib.Register(new XsltHasChildrenFunction(this));
        lib.Register(new XsltHasChildren0Function(this));
        lib.Register(new XsltXmlToJsonFunction(this));
        lib.Register(new XsltXmlToJson2Function(this));
        lib.Register(new XsltJsonToXmlFunction(this));
        lib.Register(new XsltJsonToXml2Function(this));
        lib.Register(new XsltParseJsonFunction(this));
        lib.Register(new XsltParseJson2Function(this));
        lib.Register(new XsltParseXmlFunction(this));
        lib.Register(new XsltParseXmlFragmentFunction(this));
        lib.Register(new XsltStaticBaseUriFunction(this));
        lib.Register(new XsltIdFunction(this));
        lib.Register(new XsltId2Function(this));
        lib.Register(new XsltAnalyzeStringFunction());
        lib.Register(new XsltAnalyzeString3Function());

        // fn:transform — run XSLT transformation from within XPath
        lib.Register(new XsltTransformFunction(this));

        // EXSLT extension functions (used by XSLT 1.0 stylesheets like DocBook)
        lib.Register(new ExslNodeSetFunction(this));

        return lib;
    }


    public object? ContextItem => _contextItems.Count > 0 ? _contextItems.Peek() : null;

    public int Position => _contextPositions.Count > 0 ? _contextPositions.Peek().position : 0;

    public int Last => _contextPositions.Count > 0 ? _contextPositions.Peek().last : 0;


    /// <summary>
    /// Returns the "current" item for XSLT current() function.
    /// This is the outer XSLT context item, not the inner context used for pattern evaluation.
    /// </summary>
    public object? CurrentItem => _currentItems.Count > 0 ? _currentItems.Peek() : ContextItem;


    public object? GetVariable(QName name)
    {
        foreach (var scope in _scopes)
        {
            if (scope.VariablesOrNull is { } vars && vars.TryGetValue(name, out var value))
            {
                // Handle lazy evaluation: if the value is a LazyValue, evaluate it now
                if (value is LazyValue lazy)
                {
                    var evaluated = lazy.GetValueAsync().AsTask().GetAwaiter().GetResult();
                    // Cache the evaluated value for future accesses
                    vars[name] = evaluated;
                    return evaluated;
                }
                return value;
            }
        }

        // Package-local shadow globals: when executing inside a used package whose same-named
        // global lost the principal QName slot (diamond override / different version), resolve
        // that package's own value FIRST (use-package-175 / use-package-176). Only consulted
        // for package stylesheets; non-package code never populates this overlay.
        if (_packageShadowGlobals.Count > 0)
        {
            var pkg = CurrentComponentPackage();
            if (pkg != null && _packageShadowGlobals.TryGetValue((pkg, name), out var shadowValue))
                return shadowValue;
        }

        // Private-across-boundary enforcement: a global that a used package declares without
        // exposing it (private explicitly or by the package default) is invisible to any other
        // package. It is still merged so the used package's own components can reference it, so
        // only reject when the reference originates OUTSIDE the owning package
        // (use-package-006 / use-package-007 → XPST0008). Intra-package references — the used
        // package's own templates/functions/globals — carry a matching CurrentComponentPackage
        // and resolve normally.
        if (_stylesheet.PackagePrivateGlobals.Count > 0
            && _stylesheet.PackagePrivateGlobals.TryGetValue(name, out var owningPackage)
            && !ReferenceEquals(CurrentComponentPackage(), owningPackage))
            throw Error($"XPST0008: Variable ${name.LocalName} is not visible (private to its package)");

        if (GlobalVariables.TryGetValue(name, out var globalValue))
        {
            // Handle lazy evaluation for globals too (though they're typically not lazy)
            if (globalValue is LazyValue lazyGlobal)
            {
                var evaluated = lazyGlobal.GetValueAsync().AsTask().GetAwaiter().GetResult();
                GlobalVariables[name] = evaluated;
                return evaluated;
            }
            return globalValue;
        }

        // Lazy global initialization: if the variable is pending (not yet evaluated by the
        // topological sort), initialize it on demand. This handles dependencies the static
        // analysis missed (e.g., large stylesheets like DocBook with 300+ globals).
        if (_pendingGlobals != null)
        {
            GlobalDeclaration? pendingGlobal = null;
            if (_pendingGlobals.TryGetValue(name, out pendingGlobal))
            {
                // Found by exact QName match
            }
            else
            {
                // Try matching by local name only (for QName resolution mismatches)
                foreach (var (pName, pDecl) in _pendingGlobals)
                {
                    if (pName.LocalName == name.LocalName)
                    {
                        pendingGlobal = pDecl;
                        break;
                    }
                }
            }
            if (pendingGlobal != null)
            {
                XsltTransformEngine.InitializePendingGlobalAsync(this, pendingGlobal, _output).GetAwaiter().GetResult();
                if (GlobalVariables.TryGetValue(pendingGlobal.Name, out var lazyInitValue))
                    return lazyInitValue;
            }
        }

        // XTDE0640: Detect circular references — if this global is currently being evaluated,
        // accessing it means we have a circular dependency (e.g., param → key → param).
        if (_globalsBeingEvaluated.Contains(name))
            throw Error($"XTDE0640: Circular reference detected while evaluating global variable/parameter ${name.LocalName}");
        // Also check circularity via prefix fallback matching
        foreach (var beingEvaluated in _globalsBeingEvaluated)
        {
            if (VariableNameMatches(beingEvaluated, name))
                throw Error($"XTDE0640: Circular reference detected while evaluating global variable/parameter ${name.LocalName}");
        }

        // Fallback: XPath parser creates QNames with NamespaceId.None for prefixed/EQName names
        // because it doesn't have XSLT namespace context. Try matching by prefix+localname or EQName.
        if (name.Prefix != null || name.ExpandedNamespace != null)
        {
            if (TryFindVariableByPrefixFallback(name, out var result))
                return result;
        }

        throw Error($"XPST0008: Variable ${name.LocalName} not defined");
    }


    /// <summary>
    /// Fallback variable lookup when exact QName match fails. Matches by local name
    /// and prefix, handling the case where XPath-parsed QNames have unresolved namespaces.
    /// Also handles the case where different prefixes map to the same namespace.
    /// </summary>
    /// <remarks>
    /// Reports FOUND separately from the value. It returned the value, null when nothing
    /// matched, and null is also the empty sequence, so a variable whose value is () read as
    /// missing: <c>map{'k': $p:x}</c> with <c>$p:x := ()</c> failed with "XPST0008: Variable
    /// $p:x not bound", while a non-empty $p:x worked.
    /// </remarks>
    private bool TryFindVariableByPrefixFallback(QName name, out object? found)
    {
        // Search scoped variables
        foreach (var scope in _scopes)
        {
            foreach (var (varName, value) in scope.Variables)
            {
                if (VariableNameMatches(varName, name))
                {
                    if (value is LazyValue lazy)
                    {
                        var evaluated = lazy.GetValueAsync().AsTask().GetAwaiter().GetResult();
                        scope.Variables[varName] = evaluated;
                        found = evaluated;
                        return true;
                    }
                    found = value;
                    return true;
                }
            }
        }

        // Search global variables
        foreach (var (varName, value) in GlobalVariables)
        {
            if (VariableNameMatches(varName, name))
            {
                if (value is LazyValue lazyGlobal)
                {
                    var evaluated = lazyGlobal.GetValueAsync().AsTask().GetAwaiter().GetResult();
                    GlobalVariables[varName] = evaluated;
                    found = evaluated;
                    return true;
                }
                found = value;
                return true;
            }
        }

        found = null;
        return false;
    }


    /// <summary>
    /// Copies tunnel parameters from the caller's scopes into the current
    /// (just-pushed) scope, stopping at the nearest function boundary — tunnel
    /// parameters do not propagate through functions. The nearest scope wins: an
    /// entry already present in the current scope is never overwritten.
    /// </summary>
    /// <remarks>
    /// Single implementation shared by every template-invocation path
    /// (apply-templates, the built-in rules, both streaming variants,
    /// call-template, apply-imports, next-match). Each of those carried its own
    /// copy of this loop. Five of them read <c>scope.TunnelParameters</c>, which
    /// lazily ALLOCATES the dictionary, so an ordinary dispatch allocated one
    /// empty dictionary per ancestor scope; reading <c>TunnelParametersOrNull</c>
    /// and materialising the target only when there is something to copy keeps
    /// the common case (no tunnel parameters in scope) allocation-free.
    /// </remarks>
    private void InheritTunnelParameters()
    {
        Dictionary<QName, object?>? target = null;
        var skippedCurrent = false;
        foreach (var scope in _scopes)
        {
            // Skip the current (just-pushed) scope — it is the one being populated.
            if (!skippedCurrent)
            {
                skippedCurrent = true;
                continue;
            }
            if (scope.TunnelParametersOrNull is { Count: > 0 } inherited)
            {
                target ??= _scopes.Peek().TunnelParameters;
                foreach (var (name, value) in inherited)
                    target.TryAdd(name, value);
            }
            if (scope.IsTunnelBarrier)
                break;
        }
    }


    public void SetVariable(QName name, object? value)
    {
        _scopes.Peek().Variables[name] = value;
    }


    private void ClearMergeGroupContext()
    {
        SetVariable(new QName(NamespaceId.None, "current-merge-group"), null);
        SetVariable(new QName(NamespaceId.None, "current-merge-key"), null);
    }


    private OnNoMatchBehavior GetOnNoMatchBehavior(QName? mode)
    {
        if (mode != null && _stylesheet.Modes.TryGetValue(mode.Value, out var modeDecl))
            return modeDecl.OnNoMatch ?? OnNoMatchBehavior.TextOnlyCopy;
        // Check unnamed mode
        var unnamed = new QName(default, "");
        if (_stylesheet.Modes.TryGetValue(unnamed, out var unnamedMode))
            return unnamedMode.OnNoMatch ?? OnNoMatchBehavior.TextOnlyCopy;
        return OnNoMatchBehavior.TextOnlyCopy; // XSLT default
    }


    /// <summary>
    /// #143 Task 1.3 — the guaranteed-streamable invariant guard. Returns true when the
    /// construct that OWNS the current streaming execution (the matched streaming template
    /// whose body is being run against the live reader) is classified guaranteed-streamable
    /// per §19.8.6. Such a construct must be STREAMED or BUFFERED by StreamingPlanner.Plan —
    /// it must NEVER silently collapse to the built-in text-only copy (element structure lost)
    /// or be evaluated against the synthetic empty document node (empty output). The two sinks
    /// consult this to assert-loudly (Debug) / route-to-buffering (Release) instead of emitting
    /// the collapse.
    ///
    /// Conservatism: the guard is armed ONLY under an active streaming execution with a matched
    /// current template whose body is available to classify. A node that legitimately reaches the
    /// built-in text-only copy with NO guaranteed-streamable owning construct (a genuinely
    /// unmatched node in a non-streaming pass, or an owning body that is itself not
    /// guaranteed-streamable) returns false and takes the normal path. This never fires on the
    /// text/attribute/atomic copy of leaf content inside a legitimately-streamed body — the
    /// owning-construct classification is the same guaranteed-streamable body in both the correct
    /// (streamed/buffered, never reaching here) and regressed (reaching here) cases, so the guard
    /// is triggered strictly by REACHING a sink that a guaranteed-streamable construct must not.
    /// </summary>
    private bool OwningConstructIsGuaranteedStreamable()
    {
        if (!_isStreamingExecution)
            return false;
        var body = _currentTemplate?.Body;
        if (body is null)
            return false;
        // Classify the owning body in the matched-template streaming environment: the matched
        // element is a live-streamed striding context node (identical to the Task 1.2 dispatch
        // site in MatchAndExecuteStreamingNodeAsync).
        var ctx = new Streamability.StreamingContext(
            Streamability.Posture.Striding, InStreamedScope: true);
        return Streamability.StreamabilityClassifier.Classify(body, ctx).IsGuaranteedStreamable;
    }


    /// <summary>
    /// Execute a matched template against a single node with full context management.
    /// Used by built-in templates that need to apply templates to individual nodes
    /// (e.g., shallow-copy/shallow-skip processing element attributes).
    /// </summary>
    private async ValueTask ExecuteMatchedTemplateAsync(
        XsltTemplate template, object node, QName? mode, List<XsltWithParam> withParams)
    {
        PushContextItem(node, 1, 1);
        PushCurrentItem(node);
        PushScope();

        // XTDE3480: Clear merge-group context — not available in applied templates
        ClearMergeGroupContext();

        // Propagate tunnel parameters from parent scopes
        InheritTunnelParameters();
        foreach (var param in withParams.Where(p => p.Tunnel))
        {
            var value = await EvaluateWithParamAsync(param).ConfigureAwait(false);
            _scopes.Peek().TunnelParameters[param.Name] = value;
        }

        // Bind template parameters
        foreach (var param in withParams.Where(p => !p.Tunnel))
        {
            var templateParam = template.Parameters.FirstOrDefault(tp =>
                tp.Name.Equals(param.Name) && !tp.Tunnel);
            if (templateParam != null)
            {
                var value = await EvaluateWithParamAsync(param).ConfigureAwait(false);
                if (templateParam.As != null)
                {
                    value = CoerceToType(value, templateParam.As);
                    ValidateValueMatchesType(value, templateParam.As, "XTTE0590",
                        $"Parameter ${param.Name.LocalName}");
                }
                SetVariable(param.Name, value);
            }
        }
        foreach (var param in template.Parameters)
        {
            if (!_scopes.Peek().Variables.ContainsKey(param.Name))
            {
                if (param.Tunnel && TryGetTunnelParam(param.Name, out var tunnelValue))
                {
                    if (param.As != null)
                    {
                        tunnelValue = CoerceToType(tunnelValue, param.As);
                        ValidateValueMatchesType(tunnelValue, param.As, "XTTE0590",
                            $"Parameter ${param.Name.LocalName}");
                    }
                    SetVariable(param.Name, tunnelValue);
                }
                else if (param.Select != null)
                {
                    var defaultVal = await EvaluateAsync(param.Select).ConfigureAwait(false);
                    if (param.As != null)
                    {
                        defaultVal = CoerceToType(defaultVal, param.As);
                        ValidateValueMatchesType(defaultVal, param.As, "XTTE0600",
                            $"Parameter ${param.Name.LocalName} default value");
                    }
                    SetVariable(param.Name, defaultVal);
                }
                else if (param.Content != null)
                {
                    // Accumulator-isolating evaluation — see comment at the helper definition.
                    var defaultVal = await EvaluateBodyContentToValueAsync(param.Content).ConfigureAwait(false);
                    if (param.As != null)
                    {
                        defaultVal = CoerceToType(defaultVal, param.As);
                        ValidateValueMatchesType(defaultVal, param.As, "XTTE0600",
                            $"Parameter ${param.Name.LocalName} default value");
                    }
                    SetVariable(param.Name, defaultVal);
                }
                else
                {
                    // No select, no content, no caller value: default is empty sequence.
                    if (param.As != null && param.As.Occurrence is Occurrence.ExactlyOne or Occurrence.OneOrMore
                        && IsStrictAtomicType(param.As.ItemType))
                        throw Error($"XTDE0700: Required parameter ${param.Name.LocalName} not supplied (type {param.As.ItemType} requires a value)");
                    else if (param.As != null && param.As.Occurrence is Occurrence.ZeroOrOne or Occurrence.ZeroOrMore)
                        SetVariable(param.Name, null);
                    else
                        SetVariable(param.Name, "");
                }
            }
        }

        var savedTemplate = _currentTemplate;
        var savedMode = _currentMode;
        _currentTemplate = template;
        _currentMode = mode;

        if (template.Version != null)
            _effectiveVersionStack.Push(template.Version);
        if (template.DefaultCollation != null)
            _defaultCollationStack.Push(template.DefaultCollation);
        if (template.BaseUri != null)
            _staticBaseUriStack.Push(XsltTransformEngine.UriString(template.BaseUri)!);
        try
        {
            await template.Body.ExecuteAsync(this).ConfigureAwait(false);
        }
        finally
        {
            if (template.BaseUri != null)
                _staticBaseUriStack.Pop();
            if (template.DefaultCollation != null)
                _defaultCollationStack.Pop();
            if (template.Version != null)
                _effectiveVersionStack.Pop();
            _currentTemplate = savedTemplate;
            _currentMode = savedMode;
            PopScope();
            PopCurrentItem();
            PopContextItem();
        }
    }


    /// <summary>
    /// Streaming-mode fallback for templates whose body uses snapshot()/copy-of()
    /// of the matched subtree. Consumes the current element and all its descendants
    /// from <see cref="_activeStreamingReader"/> via <c>ReadSubtree()</c>, parses
    /// the events into an in-memory <see cref="XdmDocument"/> fragment, and runs
    /// the template body against the parsed root. The processor's main loop sees
    /// <see cref="_streamingSubtreeBufferConsumed"/> and skips the matching
    /// EndElement bookkeeping (already consumed by ReadSubtree).
    /// </summary>
    private async ValueTask ExecuteWithBufferedSubtreeAsync(
        Ast.XsltTemplate template, Xdm.Nodes.XdmElement element, QName? mode, int position)
    {
        var reader = _activeStreamingReader!;
        Xdm.Nodes.XdmElement bufferedRoot;
        if (_nodeStore != null)
        {
            // Walk reader events directly into XdmElement/XdmAttribute/XdmText
            // — no string serialize-then-parse round-trip. The materializer leaves
            // the reader positioned at the matched EndElement (or on the empty
            // element if self-closing); the processor's main loop reads past it
            // on the next iteration, so we do NOT set _streamingDeferReadOnNextIteration
            // here (unlike the prior ReadOuterXml path, which advanced too far).
            bufferedRoot = StreamingSubtreeMaterializer.Materialize(reader, _nodeStore, new DocumentId(0))
                           ?? element;
            // The buffered subtree is a FRESH set of nodes, so the accumulator values the
            // streaming pass recorded against the streamed element do not reach the body that
            // runs on the copy. accumulator-before() found nothing there and recomputed the
            // accumulator from its initial value over this one node — so every match reported
            // the same value, whatever the stream had accumulated (W3C accumulator-001s and its
            // siblings: "Figure 1" four times where the non-streamed run counts 1, 2, 1, 2).
            //
            // Only the origin is recorded, not the values: the pre-descent value is final at the
            // start tag, but the post-descent one is not — the element's end-phase rules have not
            // run yet — so accumulator-after() must still be answered by the walk over the
            // buffered subtree (accumulator-015s/036s/069s).
            if (!ReferenceEquals(bufferedRoot, element))
                (_bufferedSubtreeOrigin ??= new Dictionary<NodeId, NodeId>())[bufferedRoot.Id] = element.Id;
        }
        else
        {
            bufferedRoot = element;
        }

        var savedTemplate = _currentTemplate;
        var savedMode = _currentMode;
        _currentTemplate = template;
        _currentMode = mode;

        PushContextItem(bufferedRoot, position, 1);
        PushCurrentItem(bufferedRoot);
        PushScope();

        // Bind template parameters, honouring the forwarded with-params (tunnel and
        // non-tunnel) from the apply-templates that entered this streamed pass — mirrors
        // the live-path binding in MatchAndExecuteStreamingNodeAsync. Without this, a
        // matched template whose body needs the buffered subtree (e.g. copy-of(node()))
        // silently lost its params (si-apply-templates-005 tunnel $a).
        var forwardedParams = _streamingForwardedParams;
        InheritTunnelParameters();
        foreach (var param in forwardedParams.Where(p => p.Tunnel))
        {
            var value = await EvaluateWithParamAsync(param).ConfigureAwait(false);
            _scopes.Peek().TunnelParameters[param.Name] = value;
        }
        foreach (var param in forwardedParams.Where(p => !p.Tunnel))
        {
            var templateParam = template.Parameters.FirstOrDefault(tp =>
                tp.Name.Equals(param.Name) && !tp.Tunnel);
            if (templateParam != null)
            {
                var value = await EvaluateWithParamAsync(param).ConfigureAwait(false);
                if (templateParam.As != null)
                {
                    value = CoerceToType(value, templateParam.As);
                    ValidateValueMatchesType(value, templateParam.As, "XTTE0590",
                        $"Parameter ${param.Name.LocalName}");
                }
                SetVariable(param.Name, value);
            }
        }
        foreach (var param in template.Parameters)
        {
            if (_scopes.Peek().Variables.ContainsKey(param.Name))
                continue;
            if (param.Tunnel && TryGetTunnelParam(param.Name, out var tunnelValue))
            {
                if (param.As != null)
                    tunnelValue = CoerceToType(tunnelValue, param.As);
                SetVariable(param.Name, tunnelValue);
            }
            else if (param.Select != null)
            {
                var defaultVal = await EvaluateAsync(param.Select).ConfigureAwait(false);
                if (param.As != null)
                    defaultVal = CoerceToType(defaultVal, param.As);
                SetVariable(param.Name, defaultVal);
            }
            else
            {
                SetVariable(param.Name, "");
            }
        }

        if (template.Version != null) _effectiveVersionStack.Push(template.Version);
        if (template.DefaultCollation != null) _defaultCollationStack.Push(template.DefaultCollation);
        if (template.BaseUri != null) _staticBaseUriStack.Push(XsltTransformEngine.UriString(template.BaseUri)!);

        // The subtree is now fully buffered in memory. Clear the streaming flag and
        // the active reader so the body's downward navigation (xsl:apply-templates,
        // xsl:for-each, xsl:iterate — including those inside xsl:fork prongs) runs
        // against the buffered tree instead of trying to drive the already-consumed
        // live reader. Mirrors the subscription-dispatch path in StreamingXmlProcessor.
        var prevStreamingExec = _isStreamingExecution;
        var prevStreamingReader = _activeStreamingReader;
        _isStreamingExecution = false;
        _activeStreamingReader = null;
        try
        {
            await template.Body.ExecuteAsync(this).ConfigureAwait(false);
        }
        finally
        {
            _isStreamingExecution = prevStreamingExec;
            _activeStreamingReader = prevStreamingReader;
            if (template.BaseUri != null) _staticBaseUriStack.Pop();
            if (template.DefaultCollation != null) _defaultCollationStack.Pop();
            if (template.Version != null) _effectiveVersionStack.Pop();
            _currentTemplate = savedTemplate;
            _currentMode = savedMode;
            PopScope();
            PopCurrentItem();
            PopContextItem();
        }
    }


    /// <summary>
    /// Recursively walks an XsltSequenceConstructor to detect xsl:apply-templates.
    /// Used by <see cref="TryBuildDeferredExecution"/> to disqualify deferral when
    /// the body would consume children itself (deferral is only safe when the body
    /// is purely output + consuming-aggregate evaluations).
    /// </summary>
    private static bool BodyContainsApplyTemplates(Ast.XsltSequenceConstructor? body)
    {
        if (body == null) return false;
        foreach (var insn in body.Instructions)
        {
            if (insn is Ast.XsltApplyTemplates) return true;
            if (insn is Ast.XsltSequenceConstructor child && BodyContainsApplyTemplates(child)) return true;
            if (insn is Ast.XsltLiteralResultElement lre && BodyContainsApplyTemplates(lre.Content)) return true;
            if (insn is Ast.XsltIf ifInsn && BodyContainsApplyTemplates(ifInsn.Then)) return true;
            if (insn is Ast.XsltChoose choose)
            {
                foreach (var w in choose.When)
                    if (BodyContainsApplyTemplates(w.Body)) return true;
                if (BodyContainsApplyTemplates(choose.Otherwise)) return true;
            }
            if (insn is Ast.XsltCopy copy && BodyContainsApplyTemplates(copy.Content)) return true;
        }
        return false;
    }


    /// <summary>
    /// Executes a deferred template body at the parent's EndElement. Called by
    /// <see cref="StreamingXmlProcessor"/> after watcher accumulation completes.
    /// Restores context/scope as if the template were running fresh, executes
    /// the body (consuming-aggregate sub-expressions resolve to the watcher values
    /// via <see cref="TryResolveFromWatchers"/>), then restores the prior
    /// <see cref="_activeStreamWatchers"/> so nested deferred executions stack
    /// cleanly.
    /// </summary>
    internal async ValueTask ExecuteDeferredAsync(DeferredStreamingExecution entry)
    {
        PushContextItem(entry.Element, entry.Position, 0);
        PushCurrentItem(entry.Element);
        PushScope();
        var savedTemplate = _currentTemplate;
        var savedMode = _currentMode;
        _currentTemplate = entry.Template;
        _currentMode = entry.Mode;
        try
        {
            // Bind template parameters with defaults (mirrors MatchAndExecuteStreamingNodeAsync)
            foreach (var param in entry.Template.Parameters)
            {
                if (param.Select != null)
                {
                    var defaultVal = await EvaluateAsync(param.Select).ConfigureAwait(false);
                    if (param.As != null)
                        defaultVal = CoerceToType(defaultVal, param.As);
                    SetVariable(param.Name, defaultVal);
                }
                else
                {
                    SetVariable(param.Name, "");
                }
            }
            if (entry.Template.Version != null)
                _effectiveVersionStack.Push(entry.Template.Version);
            if (entry.Template.DefaultCollation != null)
                _defaultCollationStack.Push(entry.Template.DefaultCollation);
            if (entry.Template.BaseUri != null)
                _staticBaseUriStack.Push(XsltTransformEngine.UriString(entry.Template.BaseUri)!);
            try
            {
                await entry.Template.Body.ExecuteAsync(this).ConfigureAwait(false);
            }
            finally
            {
                if (entry.Template.BaseUri != null) _staticBaseUriStack.Pop();
                if (entry.Template.DefaultCollation != null) _defaultCollationStack.Pop();
                if (entry.Template.Version != null) _effectiveVersionStack.Pop();
            }
        }
        finally
        {
            _currentTemplate = savedTemplate;
            _currentMode = savedMode;
            PopScope();
            PopCurrentItem();
            PopContextItem();
            _activeStreamWatchers = entry.PriorActiveWatchers;
        }
    }


    /// <summary>
    /// Materialises the built-in shallow-/deep-copy of a DOCUMENT node into the active sequence
    /// accumulator as a real <see cref="XdmDocument"/> whose base URI is the SOURCE document's
    /// base URI. The built-in rules for a document node are
    /// <c>&lt;xsl:copy&gt;&lt;xsl:apply-templates mode="#current"/&gt;&lt;/xsl:copy&gt;</c>
    /// (shallow-copy) and <c>&lt;xsl:copy-of select="."/&gt;</c> (deep-copy); in both the copied
    /// node is a document node that preserves the source's base URI (dm:base-uri). The child
    /// content is produced by dispatching apply-templates in the current mode (which for
    /// deep-copy deep-copies each child, for shallow-copy applies the mode's rules), captured as
    /// serialized text, and reparsed into the new document's children. Mirrors
    /// <see cref="CreateDocumentAsync"/>'s doc-node build. (fn/base-uri 053:
    /// shallow-copy-doc2 / deep-copy-doc2.)
    /// </summary>
    private async ValueTask BuildBuiltInCopyDocNodeAsync(XdmDocument sourceDoc, QName? mode, List<XsltWithParam> withParams)
    {
        var scope = new XsltTransformEngine.ScopedOutputBuffer(_output);
        var savedAccumulator = _sequenceAccumulator;
        _sequenceAccumulator = null;
        var savedTextContentDepth = _textContentDepth;
        var savedCollectText = _collectTextAsSequenceItems;
        var savedAttrStack = new List<StringBuilder>(_collectedAttributesStack);
        _collectedAttributesStack.Clear();
        // Force a clean node-serialization context: the enclosing item()* body sets
        // _collectTextAsSequenceItems, which would fragment the copied child markup.
        _textContentDepth = 0;
        _collectTextAsSequenceItems = false;
        _documentNodeDepth++;
        try
        {
            await ApplyTemplatesAsync(null, mode, [], withParams).ConfigureAwait(false);
        }
        finally
        {
            _documentNodeDepth--;
            _sequenceAccumulator = savedAccumulator;
            _textContentDepth = savedTextContentDepth;
            _collectTextAsSequenceItems = savedCollectText;
            _collectedAttributesStack.Clear();
            // Restore BOTTOM-first: Stack<T> enumerates top-first, so the saved
            // List is in pop order. Pushing it in that order would reverse the
            // stack and make the enclosing element seal against its parent's
            // attribute buffer (see CollectedAttributeStackRestoreTests).
            for (var i = savedAttrStack.Count - 1; i >= 0; i--)
                _collectedAttributesStack.Push(savedAttrStack[i]);
        }

        var content = scope.GetWritten();
        scope.Dispose();

        var docId = _nodeStore!.NextId();
        var children = new List<NodeId>();
        NodeId docElemId = NodeId.None;
        if (content.Length > 0)
        {
            try
            {
                var settings = new System.Xml.XmlReaderSettings
                {
                    DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                    IgnoreWhitespace = false,
                    IgnoreComments = false,
                    IgnoreProcessingInstructions = false,
                };
                using var stringReader = new System.IO.StringReader($"<_seq_root_>{content}</_seq_root_>");
                using var reader = System.Xml.XmlReader.Create(stringReader, settings);
                var parsedChildren = new List<object?>();
                ReadAsBodyChunkChildren(reader, parsedChildren);
                foreach (var item in parsedChildren)
                {
                    if (item is XdmNode cn)
                    {
                        cn.Parent = docId;
                        children.Add(cn.Id);
                        if (cn is XdmElement && docElemId == NodeId.None)
                            docElemId = cn.Id;
                    }
                }
            }
            catch (System.Xml.XmlException)
            {
                // Fall through to an empty document node (base-uri still preserved).
                children.Clear();
                docElemId = NodeId.None;
            }
        }
        string? docElemLocalName = docElemId != NodeId.None
            ? (_nodeStore.GetNode(docElemId) as XdmElement)?.LocalName : null;
        var docNode = new XdmDocument
        {
            StringValueResolver = _nodeStore.StringValueResolver,
            Id = docId,
            Document = new DocumentId(1),
            Parent = NodeId.None,
            DocumentElement = docElemId,
            Children = children,
            DocumentElementLocalName = docElemLocalName,
            BaseUri = sourceDoc.BaseUri,
            // Base-URI-less source: mark the copy so the AddAccItem re-stamp doesn't apply
            // the construction base to the preserved null base (dm:base-uri = ()).
            CopySourceBaseUri = sourceDoc.BaseUri == null ? DocCopyNullSourceBaseSentinel : null,
        };
        _nodeStore.Register(docNode);
        AppendToSeqAccumulator(docNode);
    }


    /// <summary>
    /// Recursively collects text content from a node and its descendants.
    /// </summary>
    private void CollectStringValue(NodeId nodeId, System.Text.StringBuilder sb)
    {
        var node = _nodeStore?.GetNode(nodeId);
        if (node is XdmText text)
            sb.Append(text.Value);
        else if (node is XdmElement elem)
        {
            foreach (var child in _nodeStore!.GetChildren(elem))
                CollectStringValue(child.Id, sb);
        }
    }


    /// <summary>
    /// Converts ResultTreeFragment values to XDM documents for XQuery evaluation.
    /// This allows XPath expressions like $var/path to navigate into RTFs.
    /// </summary>
    internal object? ConvertRtfForXQuery(object? value)
    {
        if (value is ResultTreeFragment rtf)
        {
            var doc = ParseResultTreeFragment(rtf);
            return doc ?? value; // Return original if parsing fails
        }

        if (value is object?[] arr)
        {
            var converted = new object?[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                converted[i] = ConvertRtfForXQuery(arr[i]);
            }
            return converted;
        }

        if (value is LazyValue)
        {
            // Don't convert lazy values - they'll be converted when evaluated
            return value;
        }

        return value;
    }


    /// <summary>
    /// Tests whether the element the <paramref name="reader"/> is currently positioned on
    /// matches the striding-descent <paramref name="nameTest"/> (local name + namespace,
    /// honoring <c>*</c> / <c>*:name</c> / bare-name wildcards). Used only for the
    /// intermediate/final name matching of a reader-driven downward path.
    /// </summary>
    private static bool StridingNameTestMatchesReader(
        PhoenixmlDb.XQuery.Ast.NameTest nameTest, System.Xml.XmlReader reader)
    {
        if (!nameTest.IsLocalNameWildcard && reader.LocalName != nameTest.LocalName)
            return false;
        if (nameTest.IsNamespaceWildcard)
            return true; // *:name — any namespace
        var readerNs = reader.NamespaceURI ?? "";
        // Bare wildcard `*` (no explicit namespace) matches any namespace.
        if (nameTest is { IsLocalNameWildcard: true, NamespaceUri: null })
            return true;
        var wantNs = nameTest.NamespaceUri ?? "";
        return readerNs == wantNs;
    }


    /// <summary>
    /// Advances the reader past the current element's subtree, stopping after its
    /// matching EndElement (at <paramref name="startDepth"/>). The reader must be
    /// positioned on the element's start-tag; on return it sits on that EndElement.
    /// </summary>
    private static async ValueTask SkipStreamingSubtreeAsync(
        System.Xml.XmlReader reader, int startDepth, CancellationToken ct)
    {
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType == System.Xml.XmlNodeType.EndElement
                && reader.Depth == startDepth)
                return;
        }
    }


    /// <summary>
    /// Extracts a standard error code (e.g., "XTTE1000") from an exception message
    /// that starts with "XTTE1000: ..." or similar pattern.
    /// </summary>
    private static string? ExtractErrorCode(string message)
    {
        if (message.Length >= 8
            && char.IsUpper(message[0]) && char.IsUpper(message[1])
            && char.IsLetterOrDigit(message[2]) && char.IsLetterOrDigit(message[3])
            && char.IsDigit(message[4]) && char.IsDigit(message[5])
            && char.IsDigit(message[6]) && char.IsDigit(message[7])
            && (message.Length == 8 || message[8] == ':' || message[8] == ' '))
        {
            return message[..8];
        }
        return null;
    }


    /// <summary>
    /// Recovers the undecorated error description for binding to <c>$err:description</c>.
    /// The innermost XQuery exception in the chain is the original raise site, so its message
    /// is fn:error()'s description argument as written. Outer frames add source-location and
    /// expression-snippet decoration for diagnostics, which must not reach the stylesheet.
    /// Falls back to the outermost message when no XQuery frame is present.
    /// </summary>
    private static string ExtractErrorDescription(Exception ex)
    {
        string? innermost = null;
        for (var current = (Exception?)ex; current != null; current = current.InnerException)
        {
            if (current is XQuery.Functions.XQueryException or XQuery.Execution.XQueryRuntimeException)
                innermost = current.Message;
        }
        return innermost ?? ex.Message;
    }


    /// <summary>
    /// Rewrites the RFC 8089 §2 minimal form <c>file:/path</c> — a file URI with NO authority
    /// component — to the equivalent <c>file:///path</c> that .NET's Uri parser accepts.
    /// </summary>
    /// <remarks>
    /// Both spell the same URI and Saxon accepts either, but .NET rejects the single-slash form
    /// as an absolute URI, after which combining it against a base URI throws "The Authority/Host
    /// could not be parsed". XSpec's xsl-result-document suite writes to
    /// <c>file:/dev/null</c> precisely because it is the portable way to discard output.
    /// Anything already carrying an authority (<c>file://host/path</c>) is left alone.
    /// </remarks>
    private static string NormalizeNoAuthorityUri(string href)
    {
        const string fileScheme = "file:/";
        if (href.StartsWith(fileScheme, StringComparison.OrdinalIgnoreCase)
            && !href.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat("file:///", href.AsSpan(fileScheme.Length));
        }
        return href;
    }


    /// <summary>
    /// The stylesheet's prefix bindings as XPath sees them, which differs from the element
    /// namespace context in exactly one entry: the empty prefix.
    /// </summary>
    /// <remarks>
    /// <c>xmlns="..."</c> declares the default namespace for LITERAL RESULT ELEMENTS. The
    /// default namespace for unprefixed names in XPath comes from
    /// <c>[xsl:]xpath-default-namespace</c> (XSLT 3.0 §5.4.2), and the two are unrelated.
    /// The parser records both in one dictionary keyed by prefix, so handing it to the XQuery
    /// engine unchanged made the empty key mean "xmlns=" — and the xs:QName cast, which reads
    /// that key as the default element/type namespace, resolved <c>xs:QName('foo')</c> into
    /// whatever namespace the stylesheet happened to declare for its result elements.
    /// XSpec's catch_stylesheet is where this showed: its .xspec has no default namespace, but
    /// the COMPILED stylesheet does, so an expected error code came out in the XSpec namespace
    /// while the actual one had none. Built once; both inputs are fixed per stylesheet.
    /// </remarks>
    private IReadOnlyDictionary<string, string> XPathNamespaceBindings
    {
        get
        {
            if (_xpathNamespaceBindings != null) return _xpathNamespaceBindings;
            var xpathDefault = _stylesheet.XpathDefaultNamespace;
            if (!_stylesheet.Namespaces.ContainsKey("")
                && string.IsNullOrEmpty(xpathDefault))
            {
                return _xpathNamespaceBindings = _stylesheet.Namespaces;
            }
            var derived = new Dictionary<string, string>(_stylesheet.Namespaces, StringComparer.Ordinal);
            if (string.IsNullOrEmpty(xpathDefault))
                derived.Remove("");
            else
                derived[""] = xpathDefault;
            return _xpathNamespaceBindings = derived;
        }
    }


    /// <summary>
    /// Recovers fn:error()'s error object from an exception chain for binding to
    /// <c>$err:value</c> in xsl:catch. Both XQueryException and XQueryRuntimeException carry
    /// it; either may be wrapped by a diagnostic re-throw, so the whole chain is searched.
    /// Returns null when no frame carries one — the correct value for fn:error()'s 1- and
    /// 2-argument forms, which have no error object.
    /// </summary>
    private static object? ExtractErrorValue(Exception? ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            switch (current)
            {
                case XQuery.Functions.XQueryException { ErrorValue: { } fnValue }:
                    return fnValue;
                case XQuery.Execution.XQueryRuntimeException { ErrorValue: { } rtValue }:
                    return rtValue;
                default:
                    break;
            }
        }
        return null;
    }


    /// <summary>
    /// Checks if a range for-each body can have any observable effect.
    /// Returns false if the body consists entirely of xsl:if/xsl:choose with statically false tests.
    /// </summary>
    private static bool RangeBodyHasEffect(XsltSequenceConstructor body, int count)
    {
        foreach (var instruction in body.Instructions)
        {
            if (!InstructionIsNoOp(instruction, count)) return true;
        }
        return false;

        static bool InstructionIsNoOp(XsltInstruction instruction, int count)
        {
            // xsl:if with test="position() = N" where N < 0 or N > count is always false
            if (instruction is XsltIf xif && IsAlwaysFalsePositionTest(xif.Test, count))
                return true;
            return false;
        }

        static bool IsAlwaysFalsePositionTest(XQueryExpression test, int count)
        {
            if (test is not BinaryExpression { Operator: BinaryOperator.GeneralEqual or BinaryOperator.Equal } be)
                return false;
            // position() = N where N is out of range
            if (IsPositionCall(be.Left) && TryGetIntegerValue(be.Right) is { } val)
                return val < 1 || val > count;
            if (IsPositionCall(be.Right) && TryGetIntegerValue(be.Left) is { } val2)
                return val2 < 1 || val2 > count;
            return false;
        }

        static long? TryGetIntegerValue(XQueryExpression expr) => expr switch
        {
            IntegerLiteral il when il.Value is long lv => lv,
            // -N is parsed as UnaryExpression(Minus, IntegerLiteral(N))
            UnaryExpression { Operator: UnaryOperator.Minus, Operand: IntegerLiteral il2 } when il2.Value is long lv2 => -lv2,
            UnaryExpression { Operator: UnaryOperator.Plus, Operand: IntegerLiteral il3 } when il3.Value is long lv3 => lv3,
            _ => null
        };

        static bool IsPositionCall(XQueryExpression expr)
            => expr is FunctionCallExpression { Arguments.Count: 0 } fce
               && fce.Name.LocalName == "position";
    }


    /// <summary>
    /// Returns true when the instruction's <c>validation=</c> attribute is set to
    /// <c>strict</c> or <c>lax</c> (the modes that actually trigger schema processing).
    /// </summary>
    private static bool ShouldRunValidation(Ast.ValidationMode? mode)
        => mode is Ast.ValidationMode.Strict or Ast.ValidationMode.Lax;


    /// <summary>
    /// Executes <paramref name="body"/> and validates the XML fragment it appends to the
    /// primary output buffer. Captures via length-marker so that surrounding output (parent
    /// elements still being serialized) is preserved correctly.
    ///
    /// Validation is skipped when the body runs in text-content mode (xsl:attribute /
    /// xsl:comment / xsl:processing-instruction body, where output is atomized text) or
    /// when a sequence accumulator is active (xsl:variable, xsl:document XDM construction
    /// — those paths build XdmNode trees rather than serialized markup, and a separate
    /// validation hook on the constructing instruction handles them).
    /// </summary>
    private async ValueTask RunInstructionWithValidationAsync(Ast.ValidationMode? mode,
        string instructionName, SourceLocation? location, Func<ValueTask> body)
    {
        var shouldValidate = ShouldRunValidation(mode)
            && _textContentDepth == 0
            && _sequenceAccumulator is null;

        if (!shouldValidate)
        {
            await body().ConfigureAwait(false);
            return;
        }

        var startLength = _output.Length;
        await body().ConfigureAwait(false);
        if (_output.Length > startLength)
        {
            var fragment = _output.ToString(startLength, _output.Length - startLength);
            RunValidation(mode, fragment, ValidationKind.Fragment, instructionName, location);
        }
    }


    /// <summary>
    /// SP-B differential dual-run: compares the XDM element(s) a <see cref="TreeConstructor"/>
    /// built during an as="..."-typed body against a raw reparse of the same serialized body
    /// (<paramref name="serializedBody"/>), node-model to node-model. Only compares when the
    /// constructor accounted for EVERY top-level node — i.e. the body was built entirely by the
    /// migrated emitters (<c>xsl:element</c>, literal result elements, and <c>xsl:copy</c> of an
    /// element; their children are still reparse-recovered until slice 4). A partial capture
    /// (text, value-of, and other not-yet-routed constructs) is expected and inert, not a
    /// divergence. Any real
    /// structural divergence (not on the <see cref="TempTreeDifferential.IsExpectedDivergence"/>
    /// allowlist) throws, surfacing as a conformance test failure under <c>PXDB_TEMPTREE_DIFF=1</c>.
    /// </summary>
    private void RunTreeConstructorDifferential(IReadOnlyList<NodeId> nodeRoots, string serializedBody)
    {
        if (nodeRoots.Count == 0)
        {
            TempTreeDifferential.Skipped++;
            return;
        }

        var rawRoots = new List<object?>();
        if (!string.IsNullOrEmpty(serializedBody) && serializedBody.Contains('<', StringComparison.Ordinal))
        {
            try
            {
                var settings = new System.Xml.XmlReaderSettings
                {
                    DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                    IgnoreWhitespace = false,
                    IgnoreComments = false,
                    IgnoreProcessingInstructions = false,
                };
                using var stringReader = new System.IO.StringReader($"<_tc_diff_root_>{serializedBody}</_tc_diff_root_>");
                using var reader = System.Xml.XmlReader.Create(stringReader, settings);
                ReadAsBodyChunkChildren(reader, rawRoots);
            }
            catch (System.Xml.XmlException)
            {
                TempTreeDifferential.Skipped++;
                return; // body not self-contained for an isolated reparse: skip the check
            }
        }

        var rawNodeIds = new List<NodeId>();
        foreach (var r in rawRoots)
        {
            if (r is XdmNode rn)
                rawNodeIds.Add(rn.Id);
        }

        if (nodeRoots.Count != rawNodeIds.Count)
        {
            TempTreeDifferential.Skipped++;
            return; // constructor captured only some top-level nodes — not an all-migrated body
        }

        for (var i = 0; i < nodeRoots.Count; i++)
        {
            var diff = TempTreeDifferential.TreeEqual(nodeRoots[i], rawNodeIds[i], _nodeStore!);
            if (diff != null && !TempTreeDifferential.IsExpectedDivergence(diff))
                throw new System.InvalidOperationException($"TEMPTREE-DIFF (xsl:element): {diff}");
        }

        TempTreeDifferential.Compared++;
    }


    private bool FlipSafeNode(NodeId id, TreeConstructor bodyTc)
    {
        if (_nodeStore!.GetNode(id) is not XdmElement elem)
            return true; // text/comment/PI carry no namespace or copy-base divergence

        // (1) A preserved copy-source (or entity) base URI has no reparse counterpart here.
        if (elem.CopySourceBaseUri != null || elem.BaseUri != null)
            return false;

        // (1b) An xml:base attribute drives base-uri() resolution that the reparse performs
        // (XmlDocument resolves it) but the constructor stores only as a raw attribute — a
        // base-uri divergence. Excluded pending Task-6 base-uri resolution.
        foreach (var attrId in elem.Attributes)
            if (_nodeStore.GetNode(attrId) is XdmAttribute xa
                && xa.Namespace == NamespaceId.Xml && xa.LocalName == "base")
                return false;

        // (2) If this element declares/inherits any namespace AND has an element child, the
        // reparse re-declares the full in-scope set on that child — a namespace-set divergence.
        if (bodyTc.InScopeOf(id).Count > 0)
        {
            foreach (var childId in elem.Children)
                if (_nodeStore.GetNode(childId) is XdmElement)
                    return false;
        }

        foreach (var childId in elem.Children)
            if (!FlipSafeNode(childId, bodyTc))
                return false;
        return true;
    }


    /// <summary>
    /// SP-C slice 3: builds the delivered document node for a fully node-native untyped RTF
    /// body from the constructor's fragment roots, byte-identical to what
    /// <see cref="ParseResultTreeFragment"/> would produce by serialize-then-reparse.
    /// </summary>
    /// <remarks>
    /// The reparse takes one of two shapes, which this mirrors exactly:
    /// <list type="bullet">
    /// <item><b>Non-wrapped</b> (<c>XmlDocument.LoadXml(content)</c> succeeds — content is a
    /// single well-formed root element with only whitespace/comment/PI misc around it):
    /// <c>ConvertToXdm</c> DROPS document-level text nodes (which can only be whitespace, else
    /// LoadXml would have failed). We detect this shape as "exactly one element root and every
    /// text root is whitespace-only" and omit those whitespace text roots from the children.</item>
    /// <item><b>Wrapped</b> (LoadXml fails — a fragment: multiple element roots, or non-whitespace
    /// text at the top level): the reparse wraps in <c>_rtf_root_</c> and reparents ALL its
    /// children (text included) onto the document. We keep every root.</item>
    /// </list>
    /// The document string value is the concatenation of the kept children's string values,
    /// which matches the reparse in both shapes (the non-wrapped reparse uses
    /// <c>DocumentElement.InnerText</c> — equal to the sole element's string value once the
    /// dropped whitespace is excluded).
    /// </remarks>
    private XdmDocument BuildUntypedRtfFlipDocument(IReadOnlyList<NodeId> roots, string? varBaseUri)
    {
        var elementRootCount = 0;
        var allTextRootsWhitespace = true;
        foreach (var id in roots)
        {
            switch (_nodeStore!.GetNode(id))
            {
                case XdmElement:
                    elementRootCount++;
                    break;
                case XdmText t when !IsXmlWhitespace(t.Value):
                    allTextRootsWhitespace = false;
                    break;
            }
        }
        // The reparse's non-wrapped path (single root element) discards document-level text.
        var dropDocLevelText = elementRootCount == 1 && allTextRootsWhitespace;

        var docId = _nodeStore!.NextId();
        var docChildren = new List<NodeId>();
        var flipDocElementId = NodeId.None;
        foreach (var id in roots)
        {
            if (_nodeStore.GetNode(id) is not XdmNode cn)
                continue;
            if (dropDocLevelText && cn is XdmText)
                continue; // matches ConvertToXdm dropping document-level (whitespace) text
            cn.Parent = docId;
            docChildren.Add(cn.Id);
            if (cn is XdmElement && flipDocElementId == NodeId.None)
                flipDocElementId = cn.Id;
        }

        string? flipDocElemLocalName = flipDocElementId != NodeId.None
            ? (_nodeStore.GetNode(flipDocElementId) as XdmElement)?.LocalName : null;

        var svBuilder = new System.Text.StringBuilder();
        foreach (var childId in docChildren)
            CollectStringValue(childId, svBuilder);

        var flipDoc = new XdmDocument
        {
            StringValueResolver = _nodeStore.StringValueResolver,
            Id = docId,
            Document = new DocumentId(1),
            Parent = NodeId.None,
            DocumentElement = flipDocElementId,
            DocumentUri = null, // temp trees have no document URI (XSLT 3.0 §11.9.1)
            Children = docChildren,
            DocumentElementLocalName = flipDocElemLocalName,
        };
        flipDoc.BaseUri = varBaseUri;
        flipDoc._stringValue = svBuilder.ToString();
        _nodeStore.Register(flipDoc);
        return flipDoc;
    }


    /// <summary>
    /// SP-C slice 3 static scope guard: returns <c>true</c> when the untyped RTF body contains a
    /// construct whose node build would NOT be byte-identical to the legacy serialize-reparse
    /// path, so the constructor must NOT be installed (the body runs exactly as before the seam,
    /// no flip). Blocks copies of source nodes (<c>xsl:copy</c> / <c>xsl:copy-of</c>) and template
    /// dispatch that may copy out of view (<c>xsl:apply-templates</c> / <c>call-template</c> /
    /// <c>apply-imports</c> / <c>next-match</c>) — these reroute base-URI preservation off the
    /// legacy accumulator+drain path — and any <c>xml:base</c> in the body, whose base-uri()
    /// resolution the reparse performs but the node build does not (all Task-6 base-uri work).
    /// </summary>
    private static bool UntypedRtfFlipBlocked(XsltSequenceConstructor content)
    {
        var scanner = new UntypedRtfFlipBlockScanner();
        scanner.Walk(content);
        return scanner.Blocked;
    }


    /// <summary>
    /// SP-B: records one already-serialized attribute (name + XML-escaped value) as typed
    /// data for the <see cref="TreeConstructor"/> build. xmlns declarations are routed to the
    /// namespace-declaration list (they are namespace nodes, not attributes, in the node
    /// model); real attributes get their prefix resolved to a <see cref="NamespaceId"/> and
    /// their value decoded to the raw (unescaped) string the node model stores. Sets
    /// <paramref name="abort"/> if a prefixed attribute's prefix is not declared on this
    /// element (so the node build — which must be byte-identical — is skipped rather than
    /// guessed).
    /// </summary>
    private void RecordTreeAttribute(
        string attrName,
        string escapedValue,
        Dictionary<string, string> elemNsBindings,
        List<(string Prefix, NamespaceId Ns)> nsDecls,
        List<(NamespaceId Ns, string Local, string? Prefix, string Value)> attrs,
        ref bool abort)
    {
        var value = DecodeXmlEntitiesForTree(escapedValue);
        if (attrName == "xmlns")
        {
            nsDecls.Add(("", _nodeStore!.InternNamespace(value)));
            return;
        }
        if (attrName.StartsWith("xmlns:", StringComparison.Ordinal))
        {
            nsDecls.Add((attrName[6..], _nodeStore!.InternNamespace(value)));
            return;
        }
        var colon = attrName.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            attrs.Add((NamespaceId.None, attrName, null, value));
            return;
        }
        var prefix = attrName[..colon];
        var local = attrName[(colon + 1)..];
        NamespaceId ns;
        if (prefix == "xml")
        {
            ns = NamespaceId.Xml;
        }
        else
        {
            var resolved = ResolveTreeAttributeNs(prefix, nsDecls, elemNsBindings);
            if (resolved is not { } r)
            {
                abort = true;
                return;
            }
            ns = r;
        }
        attrs.Add((ns, local, prefix, value));
    }


    /// <summary>
    /// SP-B: resolves an attribute prefix to a <see cref="NamespaceId"/> using the namespace
    /// declarations physically emitted on THIS element (the same ones the serialize-reparse
    /// path would see on the start tag). Returns <c>null</c> when the prefix isn't declared
    /// here — ancestor-inherited prefixes are out of scope for slice 1 (the as="element()"
    /// body seam clears ancestor output namespaces, so a valid serialization declares them
    /// locally).
    /// </summary>
    private NamespaceId? ResolveTreeAttributeNs(
        string prefix,
        List<(string Prefix, NamespaceId Ns)> nsDecls,
        Dictionary<string, string> elemNsBindings)
    {
        for (var i = nsDecls.Count - 1; i >= 0; i--)
        {
            if (nsDecls[i].Prefix == prefix && nsDecls[i].Ns != NamespaceId.None)
                return nsDecls[i].Ns;
        }
        if (elemNsBindings.TryGetValue(prefix, out var uri) && !string.IsNullOrEmpty(uri))
            return _nodeStore!.InternNamespace(uri);
        return null;
    }


    /// <summary>
    /// SP-B: decodes the XML character/entity references an attribute value may carry after
    /// serialization (the node model stores raw, unescaped text) — the named entities plus
    /// decimal/hex numeric references, matching what an XML reader would produce.
    /// </summary>
    private static string DecodeXmlEntitiesForTree(string s)
    {
        if (s.IndexOf('&', StringComparison.Ordinal) < 0)
            return s;
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '&')
            {
                var semi = s.IndexOf(';', i + 1);
                if (semi > i)
                {
                    var ent = s.Substring(i + 1, semi - i - 1);
                    string? decoded = ent switch
                    {
                        "lt" => "<",
                        "gt" => ">",
                        "amp" => "&",
                        "quot" => "\"",
                        "apos" => "'",
                        _ => null,
                    };
                    if (decoded == null && ent.Length > 1 && ent[0] == '#')
                    {
                        var num = ent.AsSpan(1);
                        bool ok;
                        int cp;
                        if (num.Length > 1 && (num[0] == 'x' || num[0] == 'X'))
                            ok = int.TryParse(num[1..], System.Globalization.NumberStyles.HexNumber,
                                System.Globalization.CultureInfo.InvariantCulture, out cp);
                        else
                            ok = int.TryParse(num, System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out cp);
                        if (ok && cp >= 0 && cp <= 0x10FFFF && !(cp >= 0xD800 && cp <= 0xDFFF))
                            decoded = char.ConvertFromUtf32(cp);
                    }
                    if (decoded != null)
                    {
                        sb.Append(decoded);
                        i = semi + 1;
                        continue;
                    }
                }
            }
            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }


    public override async ValueTask ValueOfAsync(XsltValueOf instruction)
    {
        // xsl:value-of always creates a text node, even a zero-length one. Recorded so a typed
        // body can tell that apart from having produced nothing at all.
        _alwaysTextInstructionCount++;

        // Evaluate separator AVT once (it may contain dynamic expressions).
        // Resolved before the streaming handoff so a watched sequence feeding
        // value-of atomizes and joins with the correct separator (B3).
        string? resolvedSeparator = instruction.Separator != null
            ? await EvaluateAvtAsync(instruction.Separator).ConfigureAwait(false)
            : null;

        // SM-ctx streaming handoff: a consuming simple-map LEFT ! RIGHT select whose
        // RIGHT was registered as an inline-driven subscription streams in place.
        // value-of atomizes element results and joins with the value-of separator
        // (default single space, or an explicit @separator).
        if (await TryHandoffSimpleMapContextStreamingAsync(instruction.Select, resolvedSeparator ?? " ").ConfigureAwait(false))
            return;

        // ForExpr streaming handoff: `for $x in CONSUMING-PATH return EXPR` registered as
        // an inline-driven subscription streams in place (mirrors the SM-ctx handoff).
        if (await TryHandoffForExpressionStreamingAsync(instruction.Select, resolvedSeparator ?? " ").ConfigureAwait(false))
            return;

        string value;

        if (instruction.Select != null)
        {
            var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);

            // XSLT 1.0 backwards-compatible mode: xsl:value-of uses first-value semantics
            // (only the first item, no separator joining) when no explicit separator is set
            if (IsBackwardsCompatible && resolvedSeparator == null)
            {
                if (result is object?[] arr10)
                    value = arr10.Length > 0 && arr10[0] != null ? StringValueOf(arr10[0]!) : "";
                else if (result is System.Collections.IEnumerable enumerable10 && result is not string && result is not XdmNode)
                {
                    var firstItem = enumerable10.Cast<object?>().FirstOrDefault();
                    value = firstItem != null ? StringValueOf(firstItem) : "";
                }
                else
                    value = StringValueOf(result);
            }
            else if (result is object?[] arr)
            {
                var sep = resolvedSeparator ?? " ";
                // Use MergeSimpleContent to merge adjacent text nodes (no separator)
                // while separating other items (elements, atomics) with separator
                value = MergeSimpleContent(new List<object?>(arr), sep);
            }
            else if (result is List<object?> arrayItems)
            {
                // XSLT/XPath 4.0: value-of atomizes its select. fn:data of an array recursively
                // flattens ALL members into one atomic sequence, so a member that is itself a
                // sequence (or a nested array) contributes each of its atoms individually — each
                // separated by the value-of separator, NOT collapsed into one space-joined token
                // per member. (sx-square-array 032-035.)
                var sep = resolvedSeparator ?? " ";
                var flat = new List<object?>();
                FlattenArrayMembers(arrayItems, flat);
                value = MergeSimpleContent(flat, sep);
            }
            else if (result is IDictionary<object, object?> or XQueryFunction)
            {
                // FOTY0013: Maps and function items cannot be atomized
                value = StringValueOf(result); // will throw FOTY0013
            }
            else if (result is System.Collections.IEnumerable enumerable && result is not string && result is not XdmNode)
            {
                var sep = resolvedSeparator ?? " ";
                var items = new List<object?>();
                foreach (var item in enumerable)
                    items.Add(item);
                value = MergeSimpleContent(items, sep);
            }
            else
            {
                value = StringValueOf(result);
            }
        }
        else if (instruction.Content != null)
        {
            // Use sequence accumulator to collect items, then join with separator.
            // Enable _collectTextAsSequenceItems so inner xsl:value-of text goes to
            // the accumulator instead of _output, preserving instruction ordering (§5.7.2).
            var savedAccumulator = _sequenceAccumulator;
            var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
            var savedCollectText = _collectTextAsSequenceItems;
            var savedElemDepth = _serializingElementDepth;
            _sequenceAccumulator = new List<object?>();
            _collectTextAsSequenceItems = true;
            _serializingElementDepth = 0;
            _textContentDepth++;

            try
            { await instruction.Content.ExecuteAsync(this).ConfigureAwait(false); }
            finally
            {
                _textContentDepth--;
                _collectTextAsSequenceItems = savedCollectText;
                _serializingElementDepth = savedElemDepth;
            }

            // Flush any trailing text as a text node item
            var trailingText = savedScope.GetWritten();
            if (trailingText.Length > 0)
                AppendToSeqAccumulator(new Xdm.TextNodeItem(trailingText));

            var sep = resolvedSeparator ?? "";
            value = MergeSimpleContent(_sequenceAccumulator, sep);

            _sequenceAccumulator = savedAccumulator;
            savedScope.Dispose();
        }
        else
        {
            value = "";
        }

        // Inside function body at top level, route through WriteTextItem so the
        // text node becomes a separate sequence item (like xsl:text does).
        // This ensures count(fn()) is correct when fn() uses xsl:value-of.
        // Only in function body context — not in variable/param body where text
        // needs to be serialized as part of the result tree.
        if (InFunctionBodyProper
            && _textContentDepth == 0 && _serializingElementDepth == 0
            && !instruction.DisableOutputEscaping)
        {
            WriteTextItem(value);
        }
        else
        {
            WriteText(value, instruction.DisableOutputEscaping);
        }
    }


    /// <summary>
    /// Heuristic static cardinality check for an xsl:copy/@select expression in
    /// streaming context. Returns true ONLY for absolute paths (rooted at /) whose
    /// step shape provably yields more than one item — these can't be rescued by
    /// streaming subtree materialization the way relative paths can, so the runtime
    /// cardinality check would otherwise miss them (the absolute path evaluates
    /// against the synthetic empty document and silently returns zero items,
    /// masking XTTE3180). Relative paths (e.g. inner <c>xsl:copy select="*"</c>)
    /// run against a real per-element context provided by the streaming subtree
    /// machinery and remain caught by the runtime nonNullItems.Count check.
    /// </summary>
    private static bool SelectMayReturnMultipleItems(XQueryExpression expr)
    {
        if (expr is not PathExpression path) return false;
        if (!path.IsAbsolute) return false;
        if (path.Steps.Count == 0) return false;
        // First step: from a document node, a child-axis step yields at most the
        // single document element (e.g. /* or /root each pin to one). However a
        // descendant-axis first step (// or descendant::) can fan out immediately.
        var first = path.Steps[0];
        if (first.Axis is Axis.Descendant or Axis.DescendantOrSelf or Axis.Following or Axis.Preceding
            && StepMayReturnMultipleItems(first))
            return true;
        for (var i = 1; i < path.Steps.Count; i++)
        {
            if (StepMayReturnMultipleItems(path.Steps[i]))
                return true;
        }
        return false;
    }


    private static bool StepMayReturnMultipleItems(StepExpression step)
    {
        // A positional predicate [n] (numeric literal) pins to a single item.
        foreach (var pred in step.Predicates)
        {
            if (pred is IntegerLiteral or DecimalLiteral or DoubleLiteral)
                return false;
        }

        // Axes that can yield > 1 node: child, descendant, descendant-or-self,
        // following, following-sibling, preceding, preceding-sibling, attribute (wildcard).
        // Self/parent always yield ≤ 1. Attribute with named test yields ≤ 1.
        return step.Axis switch
        {
            Axis.Child or Axis.Descendant or Axis.DescendantOrSelf
                or Axis.Following or Axis.FollowingSibling
                or Axis.Preceding or Axis.PrecedingSibling => true,
            Axis.Attribute => step.NodeTest is NameTest nt
                ? (nt.IsLocalNameWildcard || nt.IsNamespaceWildcard)
                : true,
            _ => false,
        };
    }


    /// <summary>
    /// Parity with the reparse fallback (base-URI preservation): when a copy-of serializes, the
    /// serializer emits a base sentinel for a copied source element whose source base URI differs
    /// from the enclosing serialization context (TryEmitBaseSentinel), and the reparse recovers it
    /// onto <see cref="XdmNode.CopySourceBaseUri"/>. This mirrors that EXACT decision on the
    /// natively-cloned node — same inputs, read before serialization mutates them — so the flipped
    /// node is byte-identical to the reparse. When no sentinel would be emitted (base equals
    /// context, or not a temp-tree serialize), the clone is left untouched to match.
    /// </summary>
    private void StampCopySourceBaseSentinel(XdmElement srcElem, NodeId cloneId)
    {
        var srcBase = srcElem.CopySourceBaseUri ?? ComputeSourceBaseUri(srcElem);
        if (_tempTreeSerializeDepth > 0 && !string.IsNullOrEmpty(srcBase)
            && !string.Equals(srcBase, _serializeBaseContext, StringComparison.Ordinal)
            && _nodeStore!.GetNode(cloneId) is XdmElement cloneElem)
        {
            cloneElem.CopySourceBaseUri = srcBase;
        }
    }


    /// <summary>
    /// SP-C targeted: routes an ordered copy-of node sequence into the untyped-RTF-flip
    /// constructor as the AUTHORITATIVE node build. Elements are deep-cloned into Document 0 —
    /// constructed nodes whose in-scope namespace enumeration (GatherInScopeNamespaces)
    /// reads their own complete (copy-namespaces-filtered) in-scope set with NO ancestor walk, so a
    /// copied element does NOT acquire the enclosing LRE's default namespace (XSLT 3.0 §11.7.2; the
    /// serialize-reparse fallback gets this wrong — W3C copy-1220/1221). Text / comment / PI nodes
    /// append verbatim in order. The clone's copy-source base URI is stamped to mirror the reparse's
    /// recovered base sentinel exactly, as in the byte-parity routing.
    /// </summary>
    private void RouteDivergentCopyOfInto(TreeConstructor tc, IReadOnlyList<XdmNode> nodes, bool copyNs)
    {
        foreach (var n in nodes)
        {
            switch (n)
            {
                case XdmElement srcElem:
                {
                    var cloneId = CloneSubtreeDeep(srcElem, null, copyNs, new DocumentId(0));
                    StampCopySourceBaseSentinel(srcElem, cloneId);
                    tc.AppendNode(cloneId);
                    break;
                }
                case XdmText t: tc.AppendText(t.Value); break;
                case XdmComment cm: tc.AppendComment(cm.Value); break;
                case XdmProcessingInstruction pi: tc.AppendProcessingInstruction(pi.Target, pi.Value); break;
            }
        }
    }


    /// <summary>
    /// Gathers the COMPLETE in-scope namespace bindings of a source element — its own xmlns
    /// declarations plus every binding inherited from an ancestor (XDM §6.2), nearest-prefix
    /// wins, honouring <c>xmlns=""</c> default undeclarations and the element's own name prefix.
    /// Mirrors the parsed-element branch of the XQuery engine's <c>GatherInScopeNamespaces</c>
    /// (the single source of truth the namespace axis / <c>fn:in-scope-prefixes</c> use), walking
    /// ancestors through the main node store so a colliding supplementary id can never skew it.
    /// Used only by the Document-0 divergent copy-of clone, where the axis reads a constructed
    /// element's declarations WITHOUT an ancestor walk, so each clone must carry its full set.
    /// A constructed (DocumentId 0) source already holds its complete set, so no walk is done.
    /// </summary>
    private IEnumerable<Xdm.NamespaceBinding> GatherSourceInScopeBindings(XdmElement src)
    {
        // Constructed source (DocumentId 0): NamespaceDeclarations already hold the full in-scope
        // set. Surface everything except an xmlns="" undeclaration (not a binding).
        if (src.Document.Value == 0)
        {
            foreach (var nb in src.NamespaceDeclarations)
            {
                var prefix = nb.Prefix ?? "";
                if (string.IsNullOrEmpty(prefix) && nb.Namespace == NamespaceId.None)
                    continue;
                yield return nb;
            }
            yield break;
        }

        // Parsed source (non-zero DocumentId): the source records only the xmlns declarations
        // physically present on each element, so inherited bindings are collected by walking
        // ancestors (nearest wins).
        var seen = new HashSet<string>();
        var undeclared = new HashSet<string>();
        XdmNode? current = src;
        while (current is not null)
        {
            if (current is XdmElement ce)
            {
                foreach (var nb in ce.NamespaceDeclarations)
                {
                    var prefix = nb.Prefix ?? "";
                    // xmlns="" undeclaration: the default namespace is out of scope from here up.
                    if (string.IsNullOrEmpty(prefix) && nb.Namespace == NamespaceId.None)
                    {
                        undeclared.Add("");
                        seen.Add("");
                        continue;
                    }
                    if (undeclared.Contains(prefix) || !seen.Add(prefix))
                        continue;
                    yield return nb;
                }
                // The element's own name prefix is in scope even if declared by an ancestor.
                if (!string.IsNullOrEmpty(ce.Prefix) && ce.Namespace != NamespaceId.None
                    && !undeclared.Contains(ce.Prefix) && seen.Add(ce.Prefix))
                {
                    yield return new Xdm.NamespaceBinding(ce.Prefix, ce.Namespace);
                }
            }
            current = current.Parent is { } pid && pid != NodeId.None ? _nodeStore!.GetNode(pid) : null;
        }
    }


    /// <summary>
    /// Checks whether a copy-of result contains attribute nodes that need accumulator handling.
    /// </summary>
    private static bool ShouldAccumulate(object? result)
    {
        if (result is XdmAttribute or XdmDocument)
            return true;
        if (result is object?[] arr)
            return arr.Length > 0 && arr[0] is XdmAttribute or XdmDocument;
        if (result is IEnumerable<object> seq && result is not string)
        {
            foreach (var item in seq)
                return item is XdmAttribute or XdmDocument; // check first item only
        }
        return false;
    }


    /// <summary>
    /// Recursively flattens an XDM array (a <see cref="List{T}"/> of <see cref="object"/>) and any
    /// sub-sequences (<c>object?[]</c>) among its members into individual items, mirroring
    /// <c>fn:data</c>'s recursive atomization of arrays (the result of atomizing an array is the
    /// concatenation of atomizing each member). A nested array recurses; an empty member
    /// (<c>null</c>) contributes nothing; atomic/node items are appended as-is (they are atomized
    /// downstream by <see cref="MergeSimpleContent"/>). Used by <c>xsl:value-of</c> so an array
    /// whose members are sequences joins every atom with the value-of separator rather than
    /// string-valuing each member into one token. (sx-square-array 032-035.)
    /// </summary>
    private static void FlattenArrayMembers(IEnumerable<object?> items, List<object?> sink)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case null:
                    break;
                case List<object?> nestedArray:   // nested XDM array → recurse over its members
                    FlattenArrayMembers(nestedArray, sink);
                    break;
                case object?[] subSequence:       // a member holding a multi-item sequence → spread
                    FlattenArrayMembers(subSequence, sink);
                    break;
                default:
                    sink.Add(item);
                    break;
            }
        }
    }


    public override async ValueTask CreatePIAsync(XsltProcessingInstruction instruction)
    {
        var name = await EvaluateAvtAsync(instruction.Name).ConfigureAwait(false);

        // XTDE0890: The name must be a valid NCName and a valid PITarget
        try
        { System.Xml.XmlConvert.VerifyNCName(name); }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException) // "" throws ArgumentException
        {
            throw Error($"XTDE0890: The effective value of the 'name' attribute of xsl:processing-instruction ('{name}') is not a valid NCName");
        }
        if (string.Equals(name, "xml", StringComparison.OrdinalIgnoreCase))
            throw Error($"XTDE0890: The name '{name}' is not allowed as a processing instruction target");

        string value;
        if (instruction.Select != null)
        {
            var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
            value = StringValueOf(result);
        }
        else if (instruction.Content != null)
        {
            // §5.7.2 simple content: same pattern as xsl:comment
            var savedAccumulator = _sequenceAccumulator;
            var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
            var savedCollectText = _collectTextAsSequenceItems;
            var savedElemDepth = _serializingElementDepth;
            var savedLastWasNode = _simpleContentLastWasNode;
            var savedAtomic = _lastResultWasAtomic;
            _sequenceAccumulator = new List<object?>();
            _collectTextAsSequenceItems = true;
            _serializingElementDepth = 0;
            _simpleContentLastWasNode = false;
            _lastResultWasAtomic = false;
            _textContentDepth++;
            try
            {
                await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
            }
            finally
            {
                _textContentDepth--;
                _collectTextAsSequenceItems = savedCollectText;
                _serializingElementDepth = savedElemDepth;
                _simpleContentLastWasNode = savedLastWasNode;
                _lastResultWasAtomic = savedAtomic;
            }

            var trailingText = savedScope.GetWritten();
            if (trailingText.Length > 0)
                AppendToSeqAccumulator(new Xdm.TextNodeItem(trailingText));

            value = MergeSimpleContent(_sequenceAccumulator, " ");
            _sequenceAccumulator = savedAccumulator;
            savedScope.Dispose();
        }
        else
        {
            value = "";
        }

        // XSLT spec: strip leading whitespace from PI value
        value = value.TrimStart();

        // Top-level item-separator: a copy of the separator precedes this PI when an item was
        // already emitted in the enclosing result sequence (§5.7.2 sequence normalization).
        TryWriteTopLevelItemSeparator();

        _sink.ProcessingInstruction(name, value);

        // SP-B slice 4: route a PI child into the active constructor (element-content level only).
        if (_textContentDepth == 0 && _activeTreeConstructor is { } ptc)
            ptc.AppendProcessingInstruction(name, value);

        // Processing instructions count as "populated" for xsl:where-populated
        MarkContentProduced();
        // PI is a node, so it breaks adjacent atomic value chain
        _lastResultWasAtomic = false;
    }


    /// <summary>
    /// Runs the serialized content through the registered <see cref="PhoenixmlDb.XQuery.ISchemaProvider"/>
    /// when the XSLT instruction's <c>validation=</c> attribute requests it. Modes <c>strip</c>
    /// and <c>preserve</c> are no-ops (XSLT 3.0 §27.2): they only affect type annotations on the
    /// XDM tree, which the string-output path doesn't carry.
    /// </summary>
    /// <param name="mode">The validation mode parsed from the instruction.</param>
    /// <param name="content">Already-serialized XML content to validate.</param>
    /// <param name="kind">Validation conformance: document for `xsl:result-document` / `xsl:document`,
    /// fragment for `xsl:element` / `xsl:attribute` / `xsl:copy` / `xsl:copy-of`.</param>
    /// <param name="instructionName">Used in error messages, e.g. "xsl:result-document".</param>
    /// <param name="location">Source location for the diagnostic.</param>
    private void RunValidation(Ast.ValidationMode? mode, string content, ValidationKind kind,
        string instructionName, SourceLocation? location)
    {
        PhoenixmlDb.XQuery.ValidationMode? xqueryMode = mode switch
        {
            Ast.ValidationMode.Strict => PhoenixmlDb.XQuery.ValidationMode.Strict,
            Ast.ValidationMode.Lax => PhoenixmlDb.XQuery.ValidationMode.Lax,
            _ => null,
        };
        if (xqueryMode is null) return;
        if (_schemaProvider is null)
        {
            throw new XsltException(
                $"XTTE1545: {instructionName} validation requires a registered ISchemaProvider on the XsltTransformer",
                location);
        }

        try
        {
            switch (kind)
            {
                case ValidationKind.Document:
                    _schemaProvider.ValidateXml(content, xqueryMode.Value);
                    break;
                case ValidationKind.Fragment:
                    _schemaProvider.ValidateXmlFragment(content, xqueryMode.Value,
                        inScopeNamespaces: SnapshotInScopeNamespaces());
                    break;
            }
        }
        catch (PhoenixmlDb.XQuery.SchemaValidationException ex)
        {
            throw new XsltException(
                $"{ex.ErrorCode}: {instructionName} validation failed: {ex.Message}",
                location);
        }
    }


    /// <summary>
    /// Resolves a single <c>cdata-section-elements</c> token to a QName. Mirrors
    /// StylesheetParser.ParseCdataSectionQName: EQName (<c>Q{uri}local</c>) and prefixed names use
    /// their namespace; an unprefixed name adopts the in-scope default namespace when one is declared.
    /// </summary>
    private static QName ResolveCdataSectionQName(string token, IReadOnlyDictionary<string, string>? nsBindings)
    {
        if (token.StartsWith("Q{", StringComparison.Ordinal))
        {
            var close = token.IndexOf('}', 2);
            if (close > 1)
            {
                var uri = token[2..close];
                var local = token[(close + 1)..];
                return uri.Length == 0
                    ? new QName(NamespaceId.None, local)
                    : new QName(StylesheetParser.ResolveNamespaceUri(uri), local) { ExpandedNamespace = uri };
            }
        }

        var colon = token.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            var prefix = token[..colon];
            var local = token[(colon + 1)..];
            if (nsBindings != null && nsBindings.TryGetValue(prefix, out var ns) && ns.Length > 0)
                return new QName(StylesheetParser.ResolveNamespaceUri(ns), local, prefix);
            // Undeclared prefix: a static value would have been rejected at parse time; a dynamic AVT
            // yielding an unbound prefix falls back to no namespace rather than throwing mid-serialization.
            return new QName(NamespaceId.None, local, prefix);
        }

        // Unprefixed: adopt the default namespace if one is in scope.
        if (nsBindings != null && nsBindings.TryGetValue("", out var defNs) && defNs.Length > 0)
            return new QName(StylesheetParser.ResolveNamespaceUri(defNs), token);
        return new QName(NamespaceId.None, token);
    }


    /// <summary>
    /// Returns the union of two cdata-section-elements sets (either may be <c>null</c>), or <c>null</c>
    /// if the union would be empty. Used to merge an xsl:result-document's own cdata-section-elements
    /// with those of its matched xsl:output declaration (result-document-0240).
    /// </summary>
    private static HashSet<QName>? UnionCdataSectionElements(HashSet<QName>? a, HashSet<QName>? b)
    {
        if ((a == null || a.Count == 0) && (b == null || b.Count == 0))
            return null;
        var union = a != null ? new HashSet<QName>(a) : [];
        if (b != null)
            union.UnionWith(b);
        return union.Count > 0 ? union : null;
    }


    private Ast.XsltOutput CreateSyntheticOutput(
        OutputMethod? method, bool? omitXmlDecl = null, bool? indent = null,
        bool? standalone = null, bool standaloneSpecified = false, string? version = null,
        string? mediaType = null, bool? includeContentType = null,
        bool? byteOrderMark = null, bool? escapeUriAttributes = null,
        string? htmlVersion = null)
    {
        var baseOutput = _stylesheet.Outputs.FirstOrDefault();
        return new Ast.XsltOutput
        {
            // A null method leaves EffectiveMethod at its default (xml), so the XML
            // declaration is still emitted for an href-less result-document with no method.
            Method = method,
            OmitXmlDeclaration = omitXmlDecl ?? baseOutput?.OmitXmlDeclaration,
            Encoding = baseOutput?.Encoding,
            // An explicit standalone on xsl:result-document (including "omit" → null) overrides
            // the stylesheet-level xsl:output; otherwise inherit the base value.
            Standalone = standaloneSpecified ? standalone : baseOutput?.Standalone,
            Indent = indent ?? baseOutput?.Indent,
            Version = version ?? baseOutput?.Version,
            // html-version >= 5.0 on an html/xhtml result-document triggers the HTML5 DOCTYPE
            // "<!DOCTYPE html>" (result-document-0242 / 0244); otherwise inherit the base value.
            HtmlVersion = htmlVersion ?? baseOutput?.HtmlVersion,
            // media-type / include-content-type drive the HTML/XHTML Content-Type meta
            // (result-document-0223 / 0224); the result-document's own values win over the base.
            MediaType = mediaType ?? baseOutput?.MediaType,
            IncludeContentType = includeContentType ?? baseOutput?.IncludeContentType,
            // byte-order-mark / escape-uri-attributes on xsl:result-document win over the base
            // xsl:output; otherwise inherit (result-document-0256/0258/0260/1203, -0264/0266/0268).
            ByteOrderMark = byteOrderMark ?? baseOutput?.ByteOrderMark,
            EscapeUriAttributes = escapeUriAttributes ?? baseOutput?.EscapeUriAttributes,
        };
    }


    /// <summary>
    /// Pre-computes accumulator values by walking the document tree depth-first.
    /// All accumulators are computed simultaneously in a single tree walk so that
    /// cross-references between accumulators (e.g., accumulator-after('other'))
    /// work correctly per XSLT 3.0 §6.5.3.
    /// </summary>
    internal async ValueTask PreComputeAccumulatorsAsync(
        XdmDocument document,
        IReadOnlyList<XsltAccumulator> accumulators,
        XdmInMemoryStore nodeStore)
    {
        _accumulatorValues ??= new();
        _accumulatorComputedDocuments ??= new();
        _accumulatorComputedDocuments.Add(document.Id);

        // Initialize all accumulators and their per-node value stores
        var currentValues = new object?[accumulators.Count];
        var nodeValueMaps = new Dictionary<NodeId, (object? before, object? after)>[accumulators.Count];
        for (var i = 0; i < accumulators.Count; i++)
        {
            // Get or create the per-accumulator node value map (merges across documents)
            if (!_accumulatorValues.TryGetValue(accumulators[i].Name, out var existingMap))
            {
                existingMap = new Dictionary<NodeId, (object? before, object? after)>();
                _accumulatorValues[accumulators[i].Name] = existingMap;
            }
            nodeValueMaps[i] = existingMap;
            try
            {
                var initVal = await EvaluateAsync(accumulators[i].InitialValue).ConfigureAwait(false);
                // Coerce initial value to declared type (e.g. xs:integer 0 → xs:double 0.0)
                if (accumulators[i].As != null)
                    initVal = CoerceAccumulatorValue(initVal, accumulators[i].As!, accumulators[i].Name);
                currentValues[i] = initVal;
            }
            catch (Exception ex) when (AccumulatorDeferredError.IsDeferrable(ex))
            {
                // Defer the error — it will be re-thrown when the value is accessed
                currentValues[i] = new AccumulatorDeferredError(ex);
            }
        }

        // Walk the tree once, processing all accumulators at each node
        await WalkAccumulatorsAsync(document, accumulators, currentValues, nodeValueMaps, nodeStore).ConfigureAwait(false);
    }


    /// <summary>
    /// True when an accumulator's declared item type does NOT call for atomization - a node
    /// kind, <c>item()</c>, or a function/map/array type.
    /// </summary>
    /// <remarks>
    /// XSLT 3.0 §18.2 applies the function conversion rules to an accumulator value, and those
    /// atomize only when the required type is atomic. An accumulator declared
    /// <c>as="element()*"</c> keeps its nodes. Atomizing unconditionally turned every node-valued
    /// accumulator into a sequence of xs:untypedAtomic, which is invisible until something asks
    /// the value for an axis step - XSpec's stacked-vardecls accumulator holds the x:variable
    /// elements themselves and reads @name off them.
    /// </remarks>
    private static bool PreservesItems(ItemType t) => t is ItemType.Item
        or ItemType.Node or ItemType.Element or ItemType.Attribute or ItemType.Text
        or ItemType.Comment or ItemType.ProcessingInstruction or ItemType.Document
        // ItemType.Namespace belongs here too, but it is a local-source addition to
        // PhoenixmlDb.XQuery that this project does not see yet - it consumes XQuery as a
        // published package. Add it when the pin is bumped.
        or ItemType.SchemaElement or ItemType.SchemaAttribute
        or ItemType.Map or ItemType.Array or ItemType.Function or ItemType.Record;


    /// <summary>
    /// Resolves an accumulator name string (possibly prefixed like "f:figNr" or EQName like "Q{uri}local")
    /// to a QName matching an accumulator in the stylesheet.
    /// </summary>
    /// <summary>
    /// The library package that declared the component currently executing (topmost
    /// xsl:function or template on the call stack), or null when executing a component of
    /// the principal package. Used for package-local resolution of accumulators.
    /// </summary>
    internal Ast.XsltStylesheet? CurrentComponentPackage()
    {
        if (_currentXsltFunctionStack.Count > 0)
            return _currentXsltFunctionStack.Peek().PackageStylesheet;
        if (_currentTemplateStack.Count > 0)
            return _currentTemplateStack.Peek().PackageStylesheet;
        return _currentGlobalPackage;
    }


    internal QName ResolveAccumulatorName(string name)
    {
        var resolved = ResolveAccumulatorNameCore(name);
        // Package-local remap: a component of a used package resolves an accumulator name to
        // its own package's accumulator when a like-named one exists in the using package
        // (override-misc-005).
        if (_stylesheet.PackageAccumulatorRemap.Count > 0)
        {
            var pkg = CurrentComponentPackage();
            if (pkg != null && _stylesheet.PackageAccumulatorRemap.TryGetValue((pkg, resolved), out var remapped))
                return remapped;
        }
        return resolved;
    }


    private QName ResolveAccumulatorNameCore(string name)
    {
        // Handle EQName (Q{uri}local) format
        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var closeBrace = name.IndexOf('}', 2);
            if (closeBrace > 2)
            {
                var nsUri = name[2..closeBrace];
                var localName = name[(closeBrace + 1)..];
                var nsId = StylesheetParser.ResolveNamespaceUri(nsUri);
                var eqName = new QName(nsId, localName);
                if (_stylesheet.Accumulators.ContainsKey(eqName))
                    return eqName;
            }
        }

        // Handle prefixed name (prefix:local) — resolve prefix via stylesheet namespaces
        var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
        if (colonIdx > 0)
        {
            var prefix = name[..colonIdx];
            var localName = name[(colonIdx + 1)..];

            if (_stylesheet.Namespaces.TryGetValue(prefix, out var nsUri))
            {
                var nsId = StylesheetParser.ResolveNamespaceUri(nsUri);
                var nsQname = new QName(nsId, localName);
                if (_stylesheet.Accumulators.ContainsKey(nsQname))
                    return nsQname;
            }
        }

        // Unprefixed name — try direct match
        var accName = new QName(NamespaceId.None, name);
        if (_stylesheet.Accumulators.ContainsKey(accName))
            return accName;

        return accName; // Return as-is; caller will handle not-found
    }


    /// <summary>
    /// Looks up a pre-computed accumulator value for the given node.
    /// </summary>
    /// <summary>
    /// The pre-descent accumulator value recorded by the streaming pass for the element a buffered
    /// subtree was materialised from, or null when this node is not such a copy (or the streaming
    /// pass recorded nothing for it).
    /// </summary>
    internal bool TryGetStreamedAccumulatorBefore(QName accumulatorName, object node, out object? value)
    {
        value = null;
        if (_bufferedSubtreeOrigin == null || _accumulatorValues == null || node is not XdmNode xdmNode)
            return false;
        if (!_bufferedSubtreeOrigin.TryGetValue(xdmNode.Id, out var streamedId))
            return false;
        if (!_accumulatorValues.TryGetValue(accumulatorName, out var nodeValues)
            || !nodeValues.TryGetValue(streamedId, out var recorded))
            return false;
        value = recorded.before;
        return true;
    }


    internal (object? before, object? after)? GetAccumulatorValue(QName accumulatorName, object node, bool isAfter = false)
    {
        if (_accumulatorValues == null)
            return null;

        if (!_accumulatorValues.TryGetValue(accumulatorName, out var nodeValues))
            return null;

        NodeId nodeId;
        if (node is XdmDocument doc)
            nodeId = doc.Id;
        else if (node is XdmNode xdmNode)
            nodeId = xdmNode.Id;
        else
            return null;

        // XTDE3400: Detect cyclic dependency — accumulator-after called for an accumulator
        // whose end-phase rule is currently being evaluated at this node
        if (isAfter && _evaluatingAccEndPhase?.Contains((accumulatorName, nodeId)) == true)
            throw Error($"XTDE3400: Cyclic dependency detected in accumulator '{accumulatorName}' at the current node");

        return nodeValues.TryGetValue(nodeId, out var value) ? value : null;
    }


    /// <summary>
    /// Finds the XdmDocument containing a given node by walking up the parent chain.
    /// </summary>
    internal XdmDocument? FindDocumentForNode(object node)
    {
        if (node is XdmDocument doc)
            return doc;
        if (node is not XdmNode xdmNode || _nodeStore == null)
            return null;

        // Walk up parent chain to find the document root
        var current = xdmNode;
        while (current.Parent.HasValue && current.Parent.Value != NodeId.None)
        {
            var parent = _nodeStore.GetNode(current.Parent.Value);
            if (parent is XdmDocument parentDoc)
                return parentDoc;
            if (parent is XdmNode parentNode)
                current = parentNode;
            else
                break;
        }

        // The current node itself might be the document (if it has no parent)
        return current as XdmDocument;
    }


    /// <summary>
    /// Gets the DocumentId of the tree containing the input node(s).
    /// For arrays, checks the first node element.
    /// </summary>
    private DocumentId? FindDocumentIdForInput(object? input)
    {
        if (input is XdmDocument doc)
            return doc.Document;
        if (input is XdmNode node)
        {
            var foundDoc = FindDocumentForNode(node);
            return foundDoc?.Document;
        }
        if (input is object?[] arr)
        {
            foreach (var item in arr)
            {
                if (item is XdmNode n)
                {
                    var foundDoc = FindDocumentForNode(n);
                    if (foundDoc != null)
                        return foundDoc.Document;
                }
            }
        }
        return null;
    }


    /// <summary>
    /// Evaluates the given grounded operands as for-each iterations against the
    /// subscription body. Each operand expression is evaluated in the current
    /// execution context; the resulting items are iterated in document order and
    /// the subscription's body is executed once per item with the item pushed as
    /// the context item and the xsl:current() item. Used to drain
    /// <see cref="ForEachSubscription.PrefixItems"/> before, and
    /// <see cref="ForEachSubscription.SuffixItems"/> after, the streaming pass
    /// for mixed-sequence xsl:for-each selects.
    /// </summary>
    private async ValueTask ExecuteForEachSubscriptionItemsAsync(
        ForEachSubscription sub,
        IReadOnlyList<XQueryExpression> items)
    {
        if (items.Count == 0) return;
        foreach (var itemExpr in items)
        {
            var result = await EvaluateAsync(itemExpr).ConfigureAwait(false);
            IEnumerable<object> seq = result switch
            {
                IEnumerable<object> s => s,
                object obj => [obj],
                _ => []
            };
            var list = seq.ToList();
            var position = 0;
            foreach (var item in list)
            {
                CheckResourceLimits();
                position++;
                var effectiveItem = item is ResultTreeFragment rtf && _nodeStore != null
                    ? (object?)ParseResultTreeFragment(rtf) ?? item
                    : item;
                PushContextItem(effectiveItem, position, list.Count);
                PushCurrentItem(effectiveItem);
                PushScope();
                var savedTemplate = _currentTemplate;
                _currentTemplate = null;
                try
                {
                    // PrefixItems/SuffixItems drain only ever applies to a for-each
                    // subscription (Body always set); an SM-ctx subscription carries
                    // no prefix/suffix operands, so Body is non-null here.
                    await RunBodyWithPostureAsync(sub.Body!, sub.OperandUsage).ConfigureAwait(false);
                }
                finally
                {
                    _currentTemplate = savedTemplate;
                    PopScope();
                    PopCurrentItem();
                    PopContextItem();
                }
            }
        }
    }


    /// <summary>
    /// Finds an element with the given xml:id value in an XDM document tree.
    /// </summary>
    private XdmElement? FindElementByXmlId(XdmDocument doc, string id)
    {
        var store = _nodeStore;
        if (store == null) return null;

        foreach (var childId in doc.Children)
        {
            if (store.GetNode(childId) is XdmElement element)
            {
                var found = FindElementByXmlIdRecursive(element, id, store);
                if (found != null) return found;
            }
        }
        return null;
    }


    private static XdmElement? FindElementByXmlIdRecursive(XdmElement element, string id, XdmInMemoryStore store)
    {
        // Check xml:id attribute
        foreach (var attrId in element.Attributes)
        {
            if (store.GetNode(attrId) is XdmAttribute attr
                && attr.LocalName == "id" && attr.Prefix == "xml"
                && XsltIdFunction.NormalizeIdValue(attr.Value) == id)
                return element;
        }
        // Recurse into children
        foreach (var childId in element.Children)
        {
            if (store.GetNode(childId) is XdmElement childElement)
            {
                var found = FindElementByXmlIdRecursive(childElement, id, store);
                if (found != null) return found;
            }
        }
        return null;
    }


    public override async ValueTask MessageAsync(XsltMessage instruction)
    {
        string message;

        try
        {
            if (instruction.Select != null)
            {
                var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
                message = StringValueOf(result);
            }
            else if (instruction.Content != null)
            {
                var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
                // Save/restore sequence accumulator so xsl:sequence inside xsl:message
                // doesn't pollute the parent variable's accumulator
                var savedAccumulator = _sequenceAccumulator;
                _sequenceAccumulator = null;
                await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                _sequenceAccumulator = savedAccumulator;
                message = savedScope.GetWritten();
                savedScope.Dispose();
            }
            else
            {
                message = "";
            }
        }
#pragma warning disable CA1031 // XSLT 3.0: dynamic errors in xsl:message content are recoverable
        catch (Exception)
        {
            return;
        }
#pragma warning restore CA1031

        // Evaluate terminate: can be static bool or dynamic AVT
        var shouldTerminate = instruction.Terminate;
        if (!shouldTerminate && instruction.TerminateAvt != null)
        {
            var terminateStr = (await EvaluateAvtAsync(instruction.TerminateAvt).ConfigureAwait(false)).Trim();
            if (terminateStr is "yes" or "true" or "1")
                shouldTerminate = true;
            else if (terminateStr is not ("no" or "false" or "0"))
                throw Error($"XTDE0030: The effective value of the terminate attribute ('{terminateStr}') is not a valid xs:boolean value (yes|no|true|false|1|0)");
        }

        var msgLine = instruction.Location?.Line ?? 0;
        var msgCol = instruction.Location?.Column ?? 0;
        if (_options.MessageListenerWithLocation != null)
            _options.MessageListenerWithLocation.Invoke(message, shouldTerminate, msgLine, msgCol);
        else
            _options.MessageListener?.Invoke(message, shouldTerminate);

        if (shouldTerminate)
        {
            // XTMM9000 is the code the spec assigns to a terminated transformation — unless the
            // instruction names its own with error-code, which was parsed and then never read.
            var code = instruction.ErrorCodeAvt is null
                ? "XTMM9000"
                : await ResolveMessageErrorCodeAsync(instruction).ConfigureAwait(false);
            throw Error($"{code}: Transformation terminated: {message}");
        }
    }


    /// <summary>
    /// Evaluates xsl:message/@error-code to the code the terminated transformation reports: a
    /// standard code (the xqt-errors namespace) as its bare local name, like every other code
    /// this engine raises, and any other as an EQName, Q{uri}local — Q{} for no namespace.
    /// The attribute is an AVT whose value is an EQName or a lexical QName, prefixed or not; an
    /// unprefixed one is in no namespace. A value that is not a valid QName, or whose prefix is
    /// not in scope, falls back to XTMM9000 (W3C message-0406).
    /// </summary>
    private async ValueTask<string> ResolveMessageErrorCodeAsync(XsltMessage instruction)
    {
        const string ErrNs = "http://www.w3.org/2005/xqt-errors";
        const string Fallback = "XTMM9000";
        var value = (await EvaluateAvtAsync(instruction.ErrorCodeAvt!).ConfigureAwait(false)).Trim();
        string uri, local;
        if (value.StartsWith("Q{", StringComparison.Ordinal) && value.IndexOf('}', StringComparison.Ordinal) is var close and > 1)
        {
            uri = value[2..close];
            local = value[(close + 1)..];
        }
        else if (value.IndexOf(':', StringComparison.Ordinal) is var colon and > 0)
        {
            local = value[(colon + 1)..];
            if (!IsNCName(value[..colon])
                || instruction.ErrorCodeNamespaces is null
                || !instruction.ErrorCodeNamespaces.TryGetValue(value[..colon], out uri!))
                return Fallback;
        }
        else
        {
            uri = "";
            local = value;
        }
        if (!IsNCName(local))
            return Fallback;
        return uri == ErrNs ? local : $"Q{{{uri}}}{local}";

        static bool IsNCName(string s)
        {
            if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_'))
                return false;
            foreach (var c in s)
                if (!(char.IsLetterOrDigit(c) || c is '_' or '-' or '.'))
                    return false;
            return true;
        }
    }


    public override async ValueTask AssertAsync(XsltAssert instruction)
    {
        var testResult = await EvaluateBooleanAsync(instruction.Test).ConfigureAwait(false);

        if (!testResult)
        {
            var errorCode = instruction.ErrorCode ?? "XTMM9001";
            string message;
            if (instruction.Select != null)
            {
                var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
                message = StringValueOf(result);
                if (string.IsNullOrEmpty(message))
                    message = "Assertion failed";
            }
            else
            {
                message = "Assertion failed";
            }

            throw Error($"{errorCode}: {message}");
        }
    }


    private static double ConvertToNumberDouble(object? value)
    {
        value = PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(value);
        return value switch
        {
            null => double.NaN,
            long l => l,
            int i => i,
            double d => d,
            decimal m => (double)m,
            System.Numerics.BigInteger bi => (double)bi,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            string => double.NaN,
            object[] arr when arr.Length > 0 => ConvertToNumberDouble(arr[0]),
            _ => double.NaN
        };
    }


    /// <summary>
    /// Converts a value to a number for xsl:number formatting.
    /// Returns double for normal values, BigInteger for values that overflow long.
    /// </summary>
    private static object ConvertToNumberObject(object? value)
    {
        value = PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(value);
        return value switch
        {
            null => double.NaN,
            System.Numerics.BigInteger bi => (object)bi,
            long l => (double)l,
            int i => (double)i,
            double d => d,
            decimal m => (double)m,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => (double)n,
            string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d2) => d2,
            object[] arr when arr.Length > 0 => ConvertToNumberObject(arr[0]),
            _ => double.NaN
        };
    }


    private List<object> ConvertToNumberSequence(object? value)
    {
        // Empty sequence handling depends on XSLT version:
        // - XSLT 2.0+: empty sequence produces no output (empty list)
        // - XSLT 1.0 (backwards compatible): empty sequence converts to NaN
        if (value == null)
            return IsBackwardsCompatible ? [double.NaN] : [];

        if (value is object?[] arr)
        {
            // Empty array is also an empty sequence
            if (arr.Length == 0)
                return IsBackwardsCompatible ? [double.NaN] : [];
            // XSLT 1.0 backwards-compatible mode: use first item only
            if (IsBackwardsCompatible)
                return [ConvertToNumberObject(arr[0])];
            var result = new List<object>();
            foreach (var item in arr)
                result.Add(ConvertToNumberObject(item));
            return result;
        }
        if (value is IEnumerable<object> seq && value is not string)
        {
            // XSLT 1.0 backwards-compatible mode: use first item only
            if (IsBackwardsCompatible)
            {
                var first = seq.FirstOrDefault();
                return first != null ? [ConvertToNumberObject(first)] : [double.NaN];
            }
            var result = new List<object>();
            foreach (var item in seq)
                result.Add(ConvertToNumberObject(item));
            // If enumerable was empty, handle per version
            if (result.Count == 0 && IsBackwardsCompatible)
                return [double.NaN];
            return result;
        }
        return [ConvertToNumberObject(value)];
    }


    private static long ConvertToNumber(object? value)
    {
        var d = ConvertToNumberDouble(value);
        return double.IsNaN(d) ? 0 : (long)Math.Round(d);
    }


    /// <summary>
    /// Finds the root node of the tree containing the given node.
    /// This walks up through ancestors until reaching a node with no parent.
    /// </summary>
    private XdmNode FindTreeRoot(XdmNode node)
    {
        var current = node;
        while (current.Parent is { } parentId && parentId != NodeId.None)
        {
            var parent = _nodeStore!.GetNode(parentId);
            if (parent == null)
                break;
            current = parent;
        }
        return current;
    }


    private PathPattern CreateDefaultCountPattern(XdmNode node)
    {
        return node switch
        {
            XdmElement elem => CreateElementCountPattern(elem),
            XdmText => new PathPattern
            {
                Steps = [new PatternStep
                {
                    Axis = Axis.Child,
                    NodeTest = new KindTest { Kind = XdmNodeKind.Text }
                }]
            },
            XdmDocument => new PathPattern
            {
                Steps = [new PatternStep
                {
                    Axis = Axis.Self,
                    NodeTest = new KindTest { Kind = XdmNodeKind.Document }
                }]
            },
            _ => new PathPattern
            {
                Steps = [new PatternStep
                {
                    Axis = Axis.Child,
                    NodeTest = new KindTest { Kind = XdmNodeKind.None }
                }]
            }
        };
    }


    private void EnsureMatchCtxDelegates()
    {
        if (_matchCtxPredicateEvaluator is null)
        {
            _matchCtxNodeResolver = _nodeStore != null ? id => _nodeStore.GetNode(id) : null;
            _matchCtxPredicateEvaluator = EvaluatePatternPredicate;
            _matchCtxSequencePredicateEvaluator = EvaluateSequencePredicates;
            _matchCtxPositionComputer = ComputeNodePosition;
            _matchCtxKeyPatternEvaluator = EvaluateKeyPattern;
            _matchCtxIdPatternEvaluator = EvaluateIdPattern;
            _matchCtxVariablePatternEvaluator = EvaluateVariablePattern;
            _matchCtxDocPatternEvaluator = EvaluateDocPattern;
            _matchCtxTreeNodesInDocumentOrder = EnumerateTreeNodesInDocumentOrder;
        }
    }


    /// <summary>
    /// Enumerates every node (in document order) of the tree that contains
    /// <paramref name="node"/>, including attributes. Used to evaluate outer positional
    /// predicates on parenthesized patterns such as <c>(doc/descendant::foo)[2]</c>.
    /// </summary>
    private List<object> EnumerateTreeNodesInDocumentOrder(object node)
    {
        var result = new List<object>();
        if (node is not XdmNode start || _nodeStore == null)
        {
            if (node != null) result.Add(node);
            return result;
        }

        // Walk up to the root of the tree.
        XdmNode root = start;
        while (root.Parent is { } pid && pid != NodeId.None)
        {
            var parent = _nodeStore.GetNode(pid);
            if (parent == null) break;
            root = parent;
        }

        void Walk(XdmNode n)
        {
            result.Add(n);
            if (n is XdmElement elem)
            {
                foreach (var attr in _nodeStore.GetAttributes(elem))
                    result.Add(attr);
            }
            foreach (var child in _nodeStore.GetChildren(n))
                Walk(child);
        }
        Walk(root);
        return result;
    }


    internal MatchContextLease AcquireMatchContext(int position = 0, int size = 0)
    {
        EnsureMatchCtxDelegates();
        var ctx = _matchCtxPool.TryPop(out var pooled) ? pooled : new XsltContext();
        ctx.CurrentNode = ContextItem;
        ctx.Position = position;
        ctx.Last = size;
        ctx.MatchedNode = null;
        ctx.DescendantPositionAncestor = null;
        ctx.NodeResolver = _matchCtxNodeResolver;
        ctx.PredicateEvaluator = _matchCtxPredicateEvaluator;
        ctx.SequencePredicateEvaluator = _matchCtxSequencePredicateEvaluator;
        ctx.PositionComputer = _matchCtxPositionComputer;
        ctx.KeyPatternEvaluator = _matchCtxKeyPatternEvaluator;
        ctx.IdPatternEvaluator = _matchCtxIdPatternEvaluator;
        ctx.VariablePatternEvaluator = _matchCtxVariablePatternEvaluator;
        ctx.DocPatternEvaluator = _matchCtxDocPatternEvaluator;
        ctx.TreeNodesInDocumentOrder = _matchCtxTreeNodesInDocumentOrder;
        return new MatchContextLease(this, ctx);
    }


    internal void ReleaseMatchContext(XsltContext ctx)
    {
        if (_matchCtxPool.Count >= MaxPooledMatchContexts) return;
        // Clear node references so the pool slot doesn't pin user data while idle.
        ctx.CurrentNode = null;
        ctx.MatchedNode = null;
        ctx.DescendantPositionAncestor = null;
        _matchCtxPool.Push(ctx);
    }


    /// <summary>
    /// Allocating fallback for callsites that can't naturally adopt the lease pattern
    /// (callers that store the context in a local for cross-method use). Prefer
    /// <see cref="AcquireMatchContext"/> on hot paths.
    /// </summary>
    internal XsltContext CreateMatchContext(int position = 0, int size = 0)
    {
        EnsureMatchCtxDelegates();
        return new XsltContext
        {
            CurrentNode = ContextItem,
            Position = position,
            Last = size,
            NodeResolver = _matchCtxNodeResolver,
            PredicateEvaluator = _matchCtxPredicateEvaluator,
            SequencePredicateEvaluator = _matchCtxSequencePredicateEvaluator,
            PositionComputer = _matchCtxPositionComputer,
            KeyPatternEvaluator = _matchCtxKeyPatternEvaluator,
            IdPatternEvaluator = _matchCtxIdPatternEvaluator,
            VariablePatternEvaluator = _matchCtxVariablePatternEvaluator,
            DocPatternEvaluator = _matchCtxDocPatternEvaluator,
            TreeNodesInDocumentOrder = _matchCtxTreeNodesInDocumentOrder,
        };
    }


    /// <summary>
    /// Computes the position of a node among its siblings matching the given node test.
    /// Returns (position, size) where position is 1-based.
    /// </summary>
    internal (int position, int size) ComputeNodePosition(object node, NodeTest nodeTest, object? descendantAncestor)
    {
        if (node is not XdmNode xdmNode || _nodeStore == null)
            return (1, 1);

        // For descendant axis patterns, compute position among ALL descendants of the ancestor
        if (descendantAncestor is XdmNode ancestor)
        {
            return ComputeDescendantPosition(xdmNode, nodeTest, ancestor);
        }

        if (xdmNode.Parent is not { } parentId || parentId == NodeId.None)
            return (1, 1);

        var parent = _nodeStore.GetNode(parentId);
        if (parent == null)
            return (1, 1);

        var children = _nodeStore.GetChildren(parent).ToList();
        int position = 0;
        int size = 0;

        foreach (var sibling in children)
        {
            if (MatchesNodeTest(sibling, nodeTest))
            {
                size++;
                if (sibling.Id == xdmNode.Id)
                    position = size;
            }
        }

        return position == 0 ? (1, 1) : (position, size);
    }


    /// <summary>
    /// Computes the position of a node among all descendants of an ancestor matching a node test.
    /// Used for descendant axis patterns like doc/descendant::*[position() mod 2 = 0].
    /// </summary>
    private (int position, int size) ComputeDescendantPosition(XdmNode node, NodeTest nodeTest, XdmNode ancestor)
    {
        int position = 0;
        int size = 0;

        void WalkDescendants(XdmNode parent)
        {
            foreach (var child in _nodeStore!.GetChildren(parent))
            {
                if (MatchesNodeTest(child, nodeTest))
                {
                    size++;
                    if (child.Id == node.Id)
                        position = size;
                }
                // Recurse into children for deeper descendants
                WalkDescendants(child);
            }
        }

        WalkDescendants(ancestor);
        return position == 0 ? (1, 1) : (position, size);
    }


    /// <summary>
    /// Builds the candidate sequence (document order) that <paramref name="node"/> belongs to
    /// for pattern predicate evaluation: all descendants of <paramref name="descendantAncestor"/>
    /// matching the node test when set, otherwise the node's siblings matching the node test.
    /// Mirrors the enumeration used by <see cref="ComputeNodePosition"/> /
    /// <see cref="ComputeDescendantPosition"/> so positions agree with the single-predicate path.
    /// </summary>
    private List<XdmNode> BuildCandidateSequence(XdmNode node, NodeTest nodeTest, object? descendantAncestor)
    {
        var result = new List<XdmNode>();

        if (descendantAncestor is XdmNode ancestor)
        {
            void WalkDescendants(XdmNode parent)
            {
                foreach (var child in _nodeStore!.GetChildren(parent))
                {
                    if (MatchesNodeTest(child, nodeTest))
                        result.Add(child);
                    WalkDescendants(child);
                }
            }
            WalkDescendants(ancestor);
            return result;
        }

        if (node.Parent is not { } parentId || parentId == NodeId.None)
        {
            // Parentless node — the candidate sequence is just the node itself.
            result.Add(node);
            return result;
        }

        var parent = _nodeStore!.GetNode(parentId);
        if (parent == null)
        {
            result.Add(node);
            return result;
        }

        foreach (var sibling in _nodeStore.GetChildren(parent))
        {
            if (MatchesNodeTest(sibling, nodeTest))
                result.Add(sibling);
        }
        return result;
    }


    private static bool ContainsNodeId(List<XdmNode> nodes, NodeId id)
    {
        foreach (var n in nodes)
        {
            if (n.Id == id)
                return true;
        }
        return false;
    }


    /// <summary>
    /// Checks if a string starts with a Unicode format token that may be a surrogate pair.
    /// Returns the token length (1 or 2) if it's a format token, 0 otherwise.
    /// </summary>
    private static int GetUnicodeFormatTokenLength(string s, int index)
    {
        if (index >= s.Length)
            return 0;

        var c = s[index];

        // Check for BMP format tokens
        if (IsFormatToken(c))
            return 1;

        // Check for surrogate pairs (SMP characters)
        if (char.IsHighSurrogate(c) && index + 1 < s.Length && char.IsLowSurrogate(s[index + 1]))
        {
            var codePoint = char.ConvertToUtf32(c, s[index + 1]);
            // DIGIT ZERO FULL STOP: 🄀 (U+1F100)
            if (codePoint == 0x1F100)
                return 2;
            // DIGIT COMMA sequence: 🄁-🄊 (U+1F101-U+1F10A)
            if (codePoint >= 0x1F101 && codePoint <= 0x1F10A)
                return 2;
            // Circled sans-serif zero: 🄋 (U+1F10B)
            if (codePoint == 0x1F10B)
                return 2;
            // Negative circled sans-serif zero: 🄌 (U+1F10C)
            if (codePoint == 0x1F10C)
                return 2;
            // Aegean Numbers: 𐄇 (U+10107) to 𐄐 (U+10110)
            if (codePoint >= 0x10107 && codePoint <= 0x10110)
                return 2;
            // Coptic Epact Numbers: 𐋡 (U+102E1) to 𐋪 (U+102EA)
            if (codePoint >= 0x102E1 && codePoint <= 0x102EA)
                return 2;
            // Rumi Numeral Symbols: 𐹠 (U+10E60) to 𐹩 (U+10E69)
            if (codePoint >= 0x10E60 && codePoint <= 0x10E69)
                return 2;
            // Brahmi Numbers: 𑁒 (U+11052) to 𑁛 (U+1105B)
            if (codePoint >= 0x11052 && codePoint <= 0x1105B)
                return 2;
            // Sinhala Archaic Numbers: 𑇡 (U+111E1) to 𑇪 (U+111EA)
            if (codePoint >= 0x111E1 && codePoint <= 0x111EA)
                return 2;
            // Counting Rod Numerals: 𝍠 (U+1D360) to 𝍨 (U+1D368)
            if (codePoint >= 0x1D360 && codePoint <= 0x1D368)
                return 2;
            // Mende Kikakui Digits: 𞣇 (U+1E8C7) to 𞣏 (U+1E8CF)
            if (codePoint >= 0x1E8C7 && codePoint <= 0x1E8CF)
                return 2;
        }

        return 0;
    }


    /// <summary>
    /// Converts a double to BigInteger using the shortest decimal representation (via ToString("R"))
    /// rather than the raw binary representation (via new BigInteger(double)).
    /// This preserves the "conceptual" value: e.g., 1e100 becomes exactly 10^100 instead of
    /// 10000000000000000159028911097599180468... (the nearest IEEE 754 double).
    /// </summary>
    private static BigInteger DoubleToBigInteger(double d)
    {
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        var eIdx = s.IndexOfAny(['E', 'e']);
        if (eIdx < 0)
        {
            // No scientific notation — truncate decimal part
            var dotIdx = s.IndexOf('.', StringComparison.Ordinal);
            return BigInteger.Parse(dotIdx < 0 ? s : s[..dotIdx], CultureInfo.InvariantCulture);
        }

        var mantissa = s[..eIdx];
        var exp = int.Parse(s[(eIdx + 1)..], CultureInfo.InvariantCulture);
        var dotIdx2 = mantissa.IndexOf('.', StringComparison.Ordinal);
        if (dotIdx2 >= 0)
        {
            var fracDigits = mantissa.Length - dotIdx2 - 1;
            mantissa = mantissa.Remove(dotIdx2, 1);
            exp -= fracDigits;
        }

        var result = BigInteger.Parse(mantissa, CultureInfo.InvariantCulture);
        if (exp > 0)
            result *= BigInteger.Pow(10, exp);
        else if (exp < 0)
            result /= BigInteger.Pow(10, -exp);
        return result;
    }


    private static string GetOrdinalSuffix(long number, string? lang = null)
    {
        var langCode = lang?.Split('-')[0];

        // German uses "." as ordinal suffix (e.g., "1." for "first")
        if (string.Equals(langCode, "de", StringComparison.OrdinalIgnoreCase))
            return ".";

        // English ordinals
        var abs = Math.Abs(number);
        var lastTwo = abs % 100;
        var lastOne = abs % 10;

        if (lastTwo >= 11 && lastTwo <= 13)
            return "th";

        return lastOne switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th"
        };
    }


    private static string ToAlpha(long number, bool lowercase, string? lang = null)
    {
        if (number <= 0)
            return number.ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        var n = number;
        while (n > 0)
        {
            n--;
            var c = (char)((n % 26) + (lowercase ? 'a' : 'A'));
            sb.Insert(0, c);
            n /= 26;
        }
        return sb.ToString();
    }


    private static string ToRoman(long number, bool lowercase)
    {
        if (number <= 0 || number >= 4000)
            return number.ToString(CultureInfo.InvariantCulture);

        ReadOnlySpan<(int value, string numeral)> romanNumerals =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        ];

        var sb = new StringBuilder();
        var n = (int)number;
        foreach (var (value, numeral) in romanNumerals)
        {
            while (n >= value)
            {
                sb.Append(numeral);
                n -= value;
            }
        }

        var result = sb.ToString();
        if (lowercase)
        {
            result = string.Create(result.Length, result, (chars, src) =>
            {
                for (var j = 0; j < src.Length; j++)
                    chars[j] = char.ToLower(src[j], CultureInfo.InvariantCulture);
            });
        }
        return result;
    }


    /// <summary>
    /// Upper-cases number words. German ß has no simple upper-case mapping under
    /// ToUpperInvariant (it stays ß), but the orthographically correct upper-case form is
    /// "SS" (e.g. "dreißig" → "DREISSIG"), which the W3C number corpus expects. Other
    /// languages contain no ß, so the replacement is a no-op for them.
    /// </summary>
    private static string UpperCaseWords(string words)
        => words.ToUpperInvariant().Replace("ß", "SS", StringComparison.Ordinal);


    private static string ToWords(long number, bool lowercase = false, bool titleCase = false, string? lang = null)
    {
        // Normalize language code (e.g., "de-DE" -> "de")
        var langCode = lang?.Split('-')[0];
        var isGerman = string.Equals(langCode, "de", StringComparison.OrdinalIgnoreCase);

        string zeroWord = isGerman ? "null" : "zero";
        string minusWord = isGerman ? "minus " : "minus ";

        if (number == 0)
            return lowercase ? zeroWord : titleCase ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(zeroWord) : UpperCaseWords(zeroWord);

        var words = isGerman ? NumberToWordsGerman(Math.Abs(number)) : NumberToWordsEnglish(Math.Abs(number));
        if (number < 0)
            words = minusWord + words;

        if (lowercase)
            return words;
        if (titleCase)
            return TitleCaseNumberWords(words);
        return UpperCaseWords(words);
    }


    private static string ToOrdinalWords(long number, bool lowercase = false, bool titleCase = false, string? lang = null, string? ordinalScheme = null)
    {
        var langCode = lang?.Split('-')[0];
        var isGerman = string.Equals(langCode, "de", StringComparison.OrdinalIgnoreCase);

        if (number == 0)
        {
            var zeroOrd = isGerman ? "nullte" : "zeroth";
            if (lowercase) return zeroOrd;
            if (titleCase) return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(zeroOrd);
            return UpperCaseWords(zeroOrd);
        }

        string words;
        if (isGerman)
        {
            words = NumberToOrdinalWordsGerman(Math.Abs(number), GermanOrdinalEnding(ordinalScheme));
        }
        else
        {
            words = NumberToOrdinalWordsEnglish(Math.Abs(number));
        }

        if (number < 0)
            words = "minus " + words;

        if (lowercase) return words;
        if (titleCase) return TitleCaseNumberWords(words);
        return UpperCaseWords(words);
    }


    /// <summary>
    /// Resolves the German ordinal inflection ending. XSLT's <c>ordinal</c> attribute may carry
    /// an explicit ending token such as <c>-e</c>, <c>-er</c>, <c>-es</c>, <c>-en</c> (used by the
    /// legacy per-form corpus), or a CLDR scheme name like <c>%spellout-ordinal</c> (no explicit
    /// inflection), in which case the default nominative ending <c>-e</c> is used.
    /// </summary>
    private static string GermanOrdinalEnding(string? ordinalScheme)
    {
        if (!string.IsNullOrEmpty(ordinalScheme)
            && ordinalScheme[0] == '-'
            && ordinalScheme.Length > 1)
        {
            return ordinalScheme[1..];
        }
        return "e";
    }


    /// <summary>Title-case for number words, keeping "and"/"und" lowercase.</summary>
    private static string TitleCaseNumberWords(string words)
    {
        var parts = words.Split(' ');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0 && parts[i] is not ("and" or "und"))
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        }
        return string.Join(' ', parts);
    }


    private static string AddGroupingSeparators(string number, string separator, int size)
    {
        if (size <= 0 || number.Length <= size)
            return number;

        var sb = new StringBuilder();
        var count = 0;
        for (var i = number.Length - 1; i >= 0; i--)
        {
            if (count > 0 && count % size == 0)
                sb.Insert(0, separator);
            sb.Insert(0, number[i]);
            count++;
        }
        return sb.ToString();
    }


    public override void NextIteration(XsltNextIteration instruction)
    {
        throw new NextIterationException { WithParams = instruction.WithParams };
    }


    private static void AddToList(List<object> list, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case IEnumerable<object> seq:
                list.AddRange(seq);
                break;
            default:
                list.Add(value);
                break;
        }
    }


    private static double ToDouble(object? v)
    {
        // Merge keys are typically XDM atomic values, but a path-step select
        // (e.g. select="@n") delivers a node — atomize before parsing or every
        // node compares equal under data-type="number" (NaN==NaN==no-op),
        // collapsing the K-way merge into a single group.
        switch (v)
        {
            case long l: return l;
            case int i: return i;
            case double d: return d;
            case float f: return f;
            case decimal m: return (double)m;
            case string s when TryParseDouble(s, out var sr): return sr;
            case Xdm.Nodes.XdmNode n when TryParseDouble(n.StringValue, out var nr): return nr;
            case XsUntypedAtomic ua when TryParseDouble(ua.Value, out var uar): return uar;
            default: return double.NaN;
        }
    }


    public override async ValueTask CreateMapAsync(XsltMap instruction)
    {
        var map = new OrderedXdmMap(EqualityComparer<object>.Default);
        _mapBuildStack.Push(map);

        // XTTE3375: Content of xsl:map must produce only map entries.
        // Monitor for stray items (non-map-entry content) by intercepting the sequence accumulator.
        var savedAccumulator = _sequenceAccumulator;
        var strayItems = new List<object?>();
        _sequenceAccumulator = strayItems;
        try
        {
            if (instruction.Content != null)
                await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
        }
        finally
        {
            _sequenceAccumulator = savedAccumulator;
            _mapBuildStack.Pop();
        }

        // Content may produce maps (via xsl:sequence, xsl:apply-templates, etc.) — merge them.
        // XTTE3375: Only non-map items are errors. Whitespace-only text nodes (the source
        // formatting between `<xsl:map-entry>` siblings, e.g. `\n  `) are insignificant per
        // XSLT 3.0 §6.2 and must not trigger XTTE3375 — they'd be stripped during stylesheet
        // parsing, but our engine collects literal-text-element output to the accumulator
        // which sometimes leaks them. Treat empty-or-whitespace strings as no-ops here.
        foreach (var item in strayItems)
        {
            if (item is IDictionary<object, object?> mapItem)
            {
                foreach (var kvp in mapItem)
                {
                    if (map.ContainsKey(kvp.Key))
                        throw Error($"XTDE3365: Duplicate key '{kvp.Key}' in xsl:map");
                    map[kvp.Key] = kvp.Value;
                }
            }
            else if (item is string s && string.IsNullOrWhiteSpace(s))
            {
                // Insignificant whitespace text from source formatting — ignore.
            }
            else if (item is Xdm.TextNodeItem tni && string.IsNullOrWhiteSpace(tni.Value))
            {
                // Same, but already wrapped as a text-node item.
            }
            else if (item != null)
                throw Error($"XTTE3375: The content of xsl:map must consist entirely of xsl:map-entry instructions; found non-map content (got {item.GetType().Name}: {item})");
        }

        // Add the completed map to the output
        if (_sequenceAccumulator != null)
            AppendToSeqAccumulator(map);
        else if (_outputNsScopes.Count > 0)
            throw Error("XTDE0450: An item in a sequence used as the content of an element or document node is a map");
        else
        {
            // Top-level map: serialize as JSON text per XSLT 3.0 §20 adaptive/JSON output
            WriteText(XsltTransformEngine.SerializeItemAsJson(map, true), false);
        }
    }


    public override async ValueTask CreateMapEntryAsync(XsltMapEntry instruction)
    {
        // Evaluate the key (xsl:map-entry key is an XPath expression, not an AVT)
        var keyVal = await EvaluateAsync(instruction.Key).ConfigureAwait(false);
        object key = keyVal ?? throw Error("XTDE3365: Map entry key must not be empty");

        // Evaluate the value
        object? value = null;
        if (instruction.Select != null)
        {
            value = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
        }
        else if (instruction.Content != null)
        {
            var savedAccum = _sequenceAccumulator;
            _sequenceAccumulator = new List<object?>();
            // Save/clear _output so text content is captured for the map entry value
            var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
            _temporaryOutputDepth++;
            try
            {
                await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                var items = _sequenceAccumulator;
                if (items.Count == 0 && savedScope.WrittenLength > 0)
                {
                    // Text-only content — use the text as the value
                    value = savedScope.GetWritten();
                }
                else
                {
                    value = items.Count switch
                    {
                        0 => null,
                        1 => items[0],
                        _ => items.ToArray()
                    };
                }
            }
            finally
            {
                _temporaryOutputDepth--;
                savedScope.Dispose();
                _sequenceAccumulator = savedAccum;
            }
        }

        if (_mapBuildStack.Count > 0)
        {
            // Inside xsl:map — add entry to the map being built
            var map = _mapBuildStack.Peek();
            // XTDE3365: duplicate key
            if (map.ContainsKey(key))
                throw Error($"XTDE3365: Duplicate key '{key}' in xsl:map");
            map[key] = value;
        }
        else
        {
            // Standalone xsl:map-entry — produce a singleton map
            var singletonMap = new OrderedXdmMap(EqualityComparer<object>.Default) { [key] = value };
            if (_sequenceAccumulator != null)
                AppendToSeqAccumulator(singletonMap);
            else
                OutputValue(singletonMap);
        }
    }


    public override async ValueTask CreateArrayAsync(XsltArray instruction)
    {
        var array = new List<object?>();
        _arrayBuildStack.Push(array);
        // Per XSLT 4.0 §22, every top-level item produced by the body becomes a member
        // of the new array. Redirect the sequence accumulator to `array` so items
        // emitted by xsl:map / xsl:sequence / atomic value-of inside the body land
        // directly as array members. Without this, those items leaked to whatever
        // outer accumulator was active and the array itself ended up empty (or with
        // only xsl:array-member contributions). Reported by Martin Honnen via the
        // streamed-JSON repro: the wrapping xsl:map-entry's value gathered the inner
        // maps plus an empty trailing [].
        var savedAccumulator = _sequenceAccumulator;
        _sequenceAccumulator = array;
        try
        {
            if (instruction.Select != null)
            {
                // XSLT 4.0 §22: the value may come from select instead of a sequence
                // constructor. With composite="no" (the default) each ITEM becomes its own
                // member; with composite="yes" the whole value is one member.
                var selected = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
                if (instruction.Composite)
                {
                    // The whole value is ONE member, whatever its length.
                    array.Add(selected);
                }
                else if (selected is object?[] seq)
                {
                    // object?[] is the engine's SEQUENCE representation, so each of its items
                    // is a separate member. (A List<object?> would be an ARRAY — a single item
                    // — and must not be spread; that distinction has caused three bugs here.)
                    foreach (var item in seq)
                        array.Add(item);
                }
                else if (selected is not null)
                {
                    array.Add(selected);
                }
            }
            else if (instruction.Content != null)
            {
                await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
            }
        }
        finally
        {
            _sequenceAccumulator = savedAccumulator;
            _arrayBuildStack.Pop();
        }

        // Add the completed array to the output AS AN ARRAY. List<object?> is the array
        // representation and object?[] is the sequence representation, so array.ToArray()
        // handed on a SEQUENCE of the members instead of one array item. It looked right for
        // the common case — a 5-member array still printed [1,2,3,4,5] — and was wrong at the
        // edges: a one-member array collapsed to its member (select="42" gave 42, not [42]),
        // and composite="yes" was indistinguishable from composite="no".
        if (_sequenceAccumulator != null)
            AppendToSeqAccumulator(array);
        // When no sequence accumulator, array is being serialized directly —
        // only valid with adaptive/json output methods. Silently ignore for now.
    }


    public override async ValueTask CreateArrayMemberAsync(XsltArrayMember instruction)
    {
        if (_arrayBuildStack.Count == 0)
            throw Error("XTDE0450: xsl:array-member must appear within xsl:array");

        var array = _arrayBuildStack.Peek();

        if (instruction.Select != null)
        {
            var value = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
            array.Add(value);
        }
        else if (instruction.Content != null)
        {
            var savedAccum = _sequenceAccumulator;
            _sequenceAccumulator = new List<object?>();
            try
            {
                await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                var items = _sequenceAccumulator;
                var member = items.Count switch
                {
                    0 => (object?)null,
                    1 => items[0],
                    _ => items.ToArray()
                };
                array.Add(member);
            }
            finally
            {
                _sequenceAccumulator = savedAccum;
            }
        }
    }


    public override async ValueTask CreateRecordAsync(Ast.XsltRecord instruction)
    {
        // xsl:record produces an XDM map with string keys from xsl:entry children.
        // Each entry has a name (string key) and a value (from select or sequence constructor body).
        var map = new OrderedXdmMap(EqualityComparer<object>.Default);

        foreach (var (name, valueBody) in instruction.Entries)
        {
            // Evaluate the entry's sequence constructor to get the value
            object? value = null;
            if (valueBody != null && valueBody.Instructions.Count > 0)
            {
                var savedAccum = _sequenceAccumulator;
                _sequenceAccumulator = new List<object?>();
                var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
                _temporaryOutputDepth++;
                try
                {
                    await valueBody.ExecuteAsync(this).ConfigureAwait(false);
                    var items = _sequenceAccumulator;
                    if (items.Count == 0 && savedScope.WrittenLength > 0)
                    {
                        value = savedScope.GetWritten();
                    }
                    else
                    {
                        value = items.Count switch
                        {
                            0 => null,
                            1 => items[0],
                            _ => items.ToArray()
                        };
                    }
                }
                finally
                {
                    _temporaryOutputDepth--;
                    savedScope.Dispose();
                    _sequenceAccumulator = savedAccum;
                }
            }

            if (map.ContainsKey(name))
                throw Error($"XTDE3365: Duplicate key '{name}' in xsl:record");
            map[name] = value;
        }

        // Add the completed map to the output (same as xsl:map)
        if (_sequenceAccumulator != null)
            AppendToSeqAccumulator(map);
        else if (_outputNsScopes.Count > 0)
            throw Error("XTDE0450: An item in a sequence used as the content of an element or document node is a map");
    }


    public override async ValueTask WherePopulatedAsync(XsltWherePopulated instruction)
    {
        // Buffer output and track whether "real" content is produced
        var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
        var savedLogicalStart = _outputLogicalStart;
        _outputLogicalStart = _output.Length;

        // Save collected attributes so where-populated content captures attributes separately.
        // This allows filtering zero-length "insignificant" attributes per XSLT 3.0 §11.4.
        var savedAttrs = _attributeCollecting ? _collectedAttributes!.ToString() : null;
        if (_attributeCollecting)
            _collectedAttributes!.Clear();

        // When the surrounding scope is an `as=` template/function body, xsl:attribute goes
        // to `_sequenceAccumulator` rather than `_collectedAttributes`. Snapshot its length
        // here so we can identify (and filter) accumulator items produced inside this body.
        var accumStartCount = _sequenceAccumulator?.Count ?? 0;
        var capturePositionsStartCount = _currentAsBodyCapture?.Positions.Count ?? 0;

        _wherePopulatedDepth++;
        BeginPopulatedTracking();
        try
        {
            await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
        }
        finally
        {
            _wherePopulatedDepth--;
        }
        var trackingMarked = EndPopulatedTracking();

        // Filter accumulator items added during the body: drop XdmAttributes whose value is a
        // zero-length string (insignificant per §11.4). Other items (elements, text, etc.) are
        // preserved unconditionally — `where-populated` only filters at the attribute level.
        if (_sequenceAccumulator != null && _sequenceAccumulator.Count > accumStartCount)
        {
            var addedRange = _sequenceAccumulator.GetRange(accumStartCount, _sequenceAccumulator.Count - accumStartCount);
            _sequenceAccumulator.RemoveRange(accumStartCount, _sequenceAccumulator.Count - accumStartCount);
            // Drop the matching range of recorded positions when the active accumulator
            // is the as= body's capture — they're about to be re-added (or filtered out)
            // along with their items by the loop below.
            if (_currentAsBodyCapture is { } cap
                && ReferenceEquals(_sequenceAccumulator, cap.Accumulator)
                && cap.Positions.Count > capturePositionsStartCount)
                cap.Positions.RemoveRange(capturePositionsStartCount, cap.Positions.Count - capturePositionsStartCount);
            foreach (var item in addedRange)
            {
                if (item is XdmAttribute attr && string.IsNullOrEmpty(attr.Value))
                    continue;
                AppendToSeqAccumulator(item);
            }
        }

        var result = savedScope.GetWritten();
        savedScope.Dispose();
        _outputLogicalStart = savedLogicalStart;

        // Collect and filter attributes produced inside where-populated
        string? wpAttrs = null;
        if (_attributeCollecting)
        {
            var rawAttrs = _collectedAttributes!.ToString();
            _collectedAttributes!.Clear();
            _collectedAttributes.Append(savedAttrs);

            // Filter out zero-length attribute values (insignificant per spec)
            wpAttrs = FilterInsignificantAttributes(rawAttrs);
        }

        if (result.Length == 0 && string.IsNullOrEmpty(wpAttrs))
            return;

        // Per XSLT 3.0 §11.4: filter out insignificant top-level element children individually.
        // An element is insignificant if it has no children (or only zero-length text nodes).
        // Attributes alone do not make an element significant.
        var filtered = result.Length > 0 ? FilterWherePopulatedContent(result) : "";

        // Apply surviving attributes first, then element content
        if (!string.IsNullOrEmpty(wpAttrs))
        {
            _collectedAttributes!.Append(wpAttrs);
            MarkContentProduced();
        }
        if (filtered.Length > 0)
        {
            _output.Append(filtered);
            MarkContentProduced();
        }
        else if (trackingMarked && result.Length > 0)
        {
            // The filter removed everything, but content-producing instructions (like xsl:document)
            // explicitly marked content as produced. This handles cases where serialized output
            // loses node type info (e.g., a document node containing <e/> serializes as just <e/>,
            // which the filter sees as an insignificant empty element, but the document node itself
            // is significant because it has children).
            _output.Append(result);
            MarkContentProduced();
        }
    }


    /// <summary>
    /// Filters attributes with zero-length values (insignificant per XSLT 3.0 §11.4 where-populated).
    /// Among surviving attributes with the same name, last-wins applies.
    /// An attribute overridden by a zero-length attribute is preserved (the zero-length one is removed).
    /// </summary>
    private static string? FilterInsignificantAttributes(string rawAttrs)
    {
        if (string.IsNullOrEmpty(rawAttrs))
            return null;

        // Parse all attributes preserving order
        var all = new List<(string name, string value)>();
        var pos = 0;
        while (pos < rawAttrs.Length)
        {
            while (pos < rawAttrs.Length && char.IsWhiteSpace(rawAttrs[pos]))
                pos++;
            if (pos >= rawAttrs.Length) break;
            var nameStart = pos;
            while (pos < rawAttrs.Length && rawAttrs[pos] != '=')
                pos++;
            if (pos >= rawAttrs.Length) break;
            var name = rawAttrs[nameStart..pos].Trim();
            pos++; // skip '='
            while (pos < rawAttrs.Length && char.IsWhiteSpace(rawAttrs[pos]))
                pos++;
            if (pos >= rawAttrs.Length) break;
            var quote = rawAttrs[pos];
            if (quote != '"' && quote != '\'') break;
            pos++;
            var valueStart = pos;
            while (pos < rawAttrs.Length && rawAttrs[pos] != quote)
                pos++;
            var value = rawAttrs[valueStart..pos];
            if (pos < rawAttrs.Length) pos++;
            if (!string.IsNullOrEmpty(name))
                all.Add((name, value));
        }

        // Filter: keep only attributes with non-empty values, last-wins among non-empty
        // Zero-length attributes are insignificant and simply ignored (they do NOT override
        // a previous non-empty value — the insignificant attribute is discarded per spec)
        var surviving = new Dictionary<string, string>();
        foreach (var (name, value) in all)
        {
            if (value.Length > 0)
                surviving[name] = value;
            // Zero-length values are silently ignored
        }

        if (surviving.Count == 0)
            return null;

        var sb = new StringBuilder();
        foreach (var (name, value) in surviving)
        {
            sb.Append(' ');
            sb.Append(name);
            sb.Append("=\"");
            sb.Append(value);
            sb.Append('"');
        }
        return sb.ToString();
    }


    /// <summary>
    /// Filters insignificant top-level elements from where-populated output.
    /// Uses string scanning to preserve original serialization (avoids re-parsing
    /// which can reorder namespace declarations).
    /// </summary>
    private static string FilterWherePopulatedContent(string xml)
    {
        var nodes = ScanWherePopulatedNodes(xml);
        if (nodes == null)
            return xml; // Not parseable as XML fragments; keep as-is

        bool hasInsignificant = false;
        foreach (var (_, _, significant) in nodes)
        {
            if (!significant)
            { hasInsignificant = true; break; }
        }

        if (!hasInsignificant)
            return xml;

        var sb = new StringBuilder();
        foreach (var (start, end, significant) in nodes)
        {
            if (significant)
                sb.Append(xml, start, end - start);
        }
        return sb.ToString();
    }


    /// <summary>
    /// Skips past attribute declarations in an element tag, handling quoted values.
    /// Returns position at '>' or '/'.
    /// </summary>
    private static int SkipElementAttributes(string xml, int pos)
    {
        while (pos < xml.Length && xml[pos] != '>' && !(xml[pos] == '/' && pos + 1 < xml.Length && xml[pos + 1] == '>'))
        {
            if (xml[pos] == '"')
            {
                pos++;
                while (pos < xml.Length && xml[pos] != '"')
                    pos++;
                if (pos < xml.Length)
                    pos++;
            }
            else if (xml[pos] == '\'')
            {
                pos++;
                while (pos < xml.Length && xml[pos] != '\'')
                    pos++;
                if (pos < xml.Length)
                    pos++;
            }
            else
            {
                pos++;
            }
        }
        return pos;
    }





    public override async ValueTask OnEmptyAsync(XsltOnEmpty instruction)
    {
        // xsl:on-empty is handled specially by the parent element constructor.
        // If we get here directly, it means the parent didn't handle it,
        // so execute the content.
        if (instruction.Select != null)
        {
            var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
            if (result != null)
            {
                // xsl:on-empty select="..." produces atomic values (like xsl:sequence)
                if (result is string str)
                {
                    if (_lastResultWasAtomic && _attributeContentDepth == 0)
                        WriteText(" ", false);
                    WriteText(str, false);
                    _lastResultWasAtomic = true;
                }
                else
                {
                    SerializeResult(result);
                }
            }
        }
        else if (instruction.Content != null)
        {
            await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
        }
    }


    public override async ValueTask OnNonEmptyAsync(XsltOnNonEmpty instruction)
    {
        // xsl:on-non-empty: execute content (it will be included because the parent
        // verified that there IS non-empty output via where-populated)
        if (instruction.Select != null)
        {
            var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
            if (result != null)
            {
                // xsl:on-non-empty select="..." produces atomic values
                if (result is string str)
                {
                    if (_lastResultWasAtomic && _attributeContentDepth == 0)
                        WriteText(" ", false);
                    WriteText(str, false);
                    _lastResultWasAtomic = true;
                }
                else
                {
                    SerializeResult(result);
                }
            }
        }
        else if (instruction.Content != null)
        {
            await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
        }
    }


    private QName ResolveQNameRuntime(string prefix, string localName, Dictionary<string, string> nsBindings)
    {
        if (nsBindings.TryGetValue(prefix, out var nsUri) && !string.IsNullOrEmpty(nsUri))
        {
            var nsId = StylesheetParser.ResolveNamespaceUri(nsUri);
            // Ensure the function library knows about this namespace
            _functionLibrary.RegisterNamespaceUri(nsUri, nsId);
            return new QName(nsId, localName, prefix);
        }
        // Use stylesheet namespaces as fallback
        if (_stylesheet.Namespaces.TryGetValue(prefix, out var fallbackUri))
        {
            var nsId = StylesheetParser.ResolveNamespaceUri(fallbackUri);
            if (nsId != NamespaceId.None)
                return new QName(nsId, localName, prefix);
        }
        return new QName(NamespaceId.None, localName, prefix);
    }


    /// <summary>
    /// Returns a StringComparer appropriate for the given collation URI.
    /// </summary>
    internal static StringComparer GetCollationComparer(string? collationUri)
    {
        if (string.IsNullOrEmpty(collationUri))
            return StringComparer.Ordinal;
        if (string.Equals(collationUri, "http://www.w3.org/2005/xpath-functions/collation/codepoint", StringComparison.Ordinal))
            return StringComparer.Ordinal;
        if (string.Equals(collationUri, "http://www.w3.org/2005/xpath-functions/collation/html-ascii-case-insensitive", StringComparison.Ordinal))
            return StringComparer.OrdinalIgnoreCase;
        if (collationUri!.StartsWith("http://www.w3.org/2013/collation/UCA", StringComparison.Ordinal))
        {
            if (collationUri.Contains("strength=primary", StringComparison.OrdinalIgnoreCase)
                || collationUri.Contains("strength=secondary", StringComparison.OrdinalIgnoreCase))
                return StringComparer.OrdinalIgnoreCase;
            return StringComparer.InvariantCulture;
        }
        return StringComparer.Ordinal;
    }

#pragma warning restore CA1309

    /// <summary>
    /// Attempts to extract a double from a sort key value.
    /// Returns non-null for numeric types or when forceNumeric is true.
    /// </summary>
    private static double? ToSortDouble(object? value, bool forceNumeric)
    {
        return value switch
        {
            double d => d,
            float f => (double)f,
            int i => (double)i,
            long l => (double)l,
            decimal m => (double)m,
            System.Numerics.BigInteger bi => (double)bi,
            short s => (double)s,
            byte b => (double)b,
            _ when forceNumeric => double.TryParse(StringValueOf(value), NumberStyles.Any,
                CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN,
            _ => null
        };
    }


    private IEnumerable<object> GetChildren(object? node)
    {
        if (node is XdmNode xdmNode && _nodeStore != null)
        {
            return _nodeStore.GetChildren(xdmNode).Cast<object>();
        }
        return [];
    }


    private static bool EffectiveBooleanValue(object? value)
    {
        return value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            Xdm.XsUntypedAtomic ua => ua.Value.Length > 0,
            Xdm.XsAnyUri uri => uri.Value.Length > 0,
            int i => i != 0,
            long l => l != 0,
            double d => d != 0 && !double.IsNaN(d),
            decimal m => m != 0,
            System.Numerics.BigInteger bi => !bi.IsZero,
            Xdm.Nodes.XdmNode => true,
            Xdm.TextNodeItem => true,
            ResultTreeFragment => true,
            System.Xml.XmlNode => true,
            System.Xml.Linq.XNode => true,
            IDictionary<object, object?> => throw new XsltException("FORG0006: Effective boolean value not defined for a map"),
            IEnumerable<object> seq => EbvOfSequence(seq),
            _ => throw new XsltException($"FORG0006: Effective boolean value not defined for type {value!.GetType().Name}")
        };
    }


    private static bool EbvOfSequence(IEnumerable<object> seq)
    {
        using var enumerator = seq.GetEnumerator();
        if (!enumerator.MoveNext())
            return false; // empty sequence
        var first = enumerator.Current;
        // First item is a node → true
        if (first is Xdm.Nodes.XdmNode or ResultTreeFragment or System.Xml.XmlNode or System.Xml.Linq.XNode)
            return true;
        // First item is atomic — only valid if single item
        if (!enumerator.MoveNext())
            return EffectiveBooleanValue(first); // single atomic → recurse
        // 2+ items starting with non-node → FORG0006
        throw new XsltException("FORG0006: Effective boolean value not defined for a sequence of two or more items starting with a non-node value");
    }


    /// <summary>
    /// Takes the serialized output an <c>as=</c>-typed body contributed to its own return
    /// value, removing it from <c>_output</c> so the validated items can be re-emitted.
    /// </summary>
    /// <param name="savedLen">Length of <c>_output</c> when the body started.</param>
    /// <param name="rdClaimedPrimaryBefore">
    /// Whether an <c>xsl:result-document</c> had already claimed the principal output
    /// destination before the body ran.
    /// </param>
    /// <remarks>
    /// <c>xsl:result-document</c> returns the EMPTY SEQUENCE (XSLT 3.0 §26.1). One carrying
    /// an <c>href</c> is redirected into its own buffer, so it never lands here. One
    /// targeting the PRINCIPAL output has nowhere else to go — <c>_output</c> IS the
    /// principal result — so its content sits inside this body's capture window even
    /// though it is not the body's return value.
    ///
    /// When the body claimed the principal output, return nothing and leave <c>_output</c>
    /// intact: the content is the principal result document and must still be serialized,
    /// but the body's return value is the empty sequence. XTDE1490 permits at most one such
    /// instruction per transformation and rejects a principal output that already has
    /// content, so the claimed region is exactly what the body wrote.
    /// </remarks>
    private string TakeAsBodyOutput(int savedLen, bool rdClaimedPrimaryBefore)
    {
        if (!rdClaimedPrimaryBefore && _primaryOutputClaimedByResultDocument)
            return "";
        var bodyOutput = _output.ToString(savedLen, _output.Length - savedLen);
        _output.Length = savedLen;
        return bodyOutput;
    }


    /// <summary>
    /// Flattens nested arrays and unwraps ResultTreeFragments into actual XDM nodes.
    /// Used before type checking variables declared as node types (element()*, document-node()*, etc.)
    /// </summary>
    private object?[] FlattenAndUnwrapToNodes(object?[] items, ItemType targetType = ItemType.Node, string? docElementName = null)
    {
        var result = new List<object?>();
        FlattenItems(items, result);
        return result.ToArray();

        void FlattenItems(object?[] arr, List<object?> output)
        {
            foreach (var item in arr)
            {
                if (item == null)
                    continue;
                if (item is object?[] nested)
                {
                    FlattenItems(nested, output);
                }
                else if (item is ResultTreeFragment rtf && _nodeStore != null)
                {
                    // When target type is document-node(), wrap RTF as a proper XdmDocument
                    // so instance-of checks work correctly
                    if (targetType == ItemType.Document)
                    {
                        var docNode = ParseResultTreeFragment(rtf);
                        if (docNode != null)
                        {
                            output.Add(docNode);
                        }
                        else
                        {
                            output.Add(item);
                        }
                    }
                    else
                    {
                        // Parse RTF XML content into actual XDM nodes (children only) —
                        // stream via XmlReader rather than allocating an XmlDocument and
                        // re-converting it. Same hot-path optimization as the as=-body
                        // assembly path.
                        try
                        {
                            var rtfSettings = new System.Xml.XmlReaderSettings
                            {
                                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                                IgnoreWhitespace = false,
                                IgnoreComments = false,
                                IgnoreProcessingInstructions = false,
                            };
                            using var rtfStringReader = new System.IO.StringReader($"<_rtf_wrap_>{rtf.XmlContent}</_rtf_wrap_>");
                            using var rtfReader = System.Xml.XmlReader.Create(rtfStringReader, rtfSettings);
                            var rtfChildren = new List<object?>();
                            ReadAsBodyChunkChildren(rtfReader, rtfChildren);
                            foreach (var child in rtfChildren)
                            {
                                if (child is XdmNode xn)
                                    output.Add(xn);
                            }
                        }
                        catch (System.Xml.XmlException)
                        {
                            output.Add(item); // Keep as-is if unparseable
                        }
                    }
                }
                else
                {
                    output.Add(item);
                }
            }
        }
    }


    /// <summary>
    /// Checks if an item can be coerced to the target type via function conversion rules.
    /// Strings are treated as potentially untypedAtomic (castable to matching types).
    /// </summary>
    private static bool CanCoerceToItemType(object item, ItemType targetType)
    {
        if (XQuery.Execution.TypeCastHelper.MatchesItemType(item, targetType))
            return true;

        // Numeric promotion: integer → double/float, decimal → double/float, float → double
        if (targetType == ItemType.Double && item is int or long or float or decimal)
            return true;
        if (targetType == ItemType.Float && item is int or long)
            return true;
        if (targetType == ItemType.Decimal && item is int or long)
            return true;

        // UntypedAtomic can be cast to any atomic type
        if (item is Xdm.XsUntypedAtomic)
            return true;

        // xs:anyURI satisfies xs:string per XPath function conversion rules (F&O §1.6.3,
        // step 4): "If the supplied value is an instance of xs:anyURI ... and the
        // expected type is xs:string, the value is cast to xs:string." This was missing,
        // so `<xsl:variable as="xs:string" select="resolve-uri(...)">` raised XTTE0570.
        // Found while triaging Martin Honnen's Docbook TNG report — `templates.xsl`
        // declares such a variable and resolve-uri() returns xs:anyURI.
        if (item is Xdm.XsAnyUri && targetType == ItemType.String)
            return true;

        // XDM nodes can be atomized: extract string value and try coercion
        string? s = item switch
        {
            string str => str,
            XdmNode node => node.StringValue,
            Xdm.XsAnyUri uri => uri.Value,
            // A TextNodeItem is a text node — the accumulator's lightweight stand-in for one —
            // so it atomizes to its string value like any other node. Without this it reached
            // the `_ => null` arm and the whole coercion answered false, so a typed variable
            // whose body produced text raised XTTE0570 against a value of exactly the right
            // type: `<xsl:variable as="xs:string"><xsl:choose>…<xsl:when>dateTime</xsl:when>`
            // failed with "the body produced 1 items (a text item 'dateTime')".
            Xdm.TextNodeItem textItem => textItem.Value,
            _ => null
        };

        if (s != null)
        {
            return targetType switch
            {
                ItemType.String or ItemType.AnyAtomicType => true,
                ItemType.Integer => long.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out _),
                ItemType.Double => double.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out _),
                ItemType.Decimal => decimal.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out _),
                ItemType.Float => float.TryParse(s, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out _),
                ItemType.Boolean => s is "true" or "false" or "0" or "1",
                ItemType.Date => DateOnly.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out _),
                ItemType.DateTime => DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out _),
                ItemType.Time => TimeOnly.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out _),
                _ => true // for unknown types, be lenient
            };
        }

        return false;
    }


    /// <summary>
    /// Extracts the string value of any result (XDM node or primitive).
    /// </summary>
    internal static string StringValueOf(object? value)
    {
        return value switch
        {
            null => "",
            LazyValue lazy => StringValueOf(lazy.GetValueAsync().AsTask().GetAwaiter().GetResult()),
            XdmNode node => node.StringValue,
            ResultTreeFragment rtf => StripXmlMarkup(rtf.XmlContent),
            Xdm.TextNodeItem tni => tni.Value,
            Xdm.XsAnyUri uri => uri.Value,
            // Casting xs:QName to xs:string yields the LEXICAL form — "prefix:local", or the bare
            // local name when there is no prefix (XPath 3.1 §19.2). QName.ToString() renders the
            // EQName form "Q{uri}local" once an expanded namespace is attached: a good debugging
            // rendering, and the wrong value. PhoenixmlDb.XQuery's XQueryStringValue carries the
            // identical rule for fn:string; both are needed, because xsl:value-of routes through
            // THIS method and fn:string through that one. Fixing only one makes the two paths
            // disagree about the same value.
            QName qname => string.IsNullOrEmpty(qname.Prefix)
                ? qname.LocalName
                : qname.Prefix + ":" + qname.LocalName,
            string s => s,
            bool b => b ? "true" : "false",
            double d => FormatDouble(d),
            float f => FormatFloat(f),
            decimal m => FormatDecimal(m),
            XsDateTime xdt => xdt.ToString(),
            XsDate xd => xd.ToString(),
            XsTime xt => xt.ToString(),
            DateTimeOffset dto => FormatDateTimeOffset(dto),
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan ts => System.Xml.XmlConvert.ToString(ts),
            IDictionary<object, object?> => throw new XsltException("FOTY0013: Atomization is not defined for maps"),
            List<object?> arrayList => string.Join(" ", arrayList.Select(StringValueOf)),
            XQueryFunction => throw new XsltException("FOTY0013: Atomization is not defined for function items"),
            object[] arr => string.Join(" ", arr.Select(StringValueOf)),
            IEnumerable<object?> seq => string.Join(" ", seq.Select(StringValueOf)),
            _ => value.ToString() ?? ""
        };
    }


    /// <summary>
    /// Wraps callable items in coercion wrappers per XSLT 3.0 §5.4.11.
    /// The wrapper validates return types at invocation time.
    /// </summary>
    private static object? WrapInCoercionWrapper(object? value, XdmSequenceType targetType)
    {
        if (value == null) return null;

        var paramTypes = targetType.FunctionParameterTypes!;
        var returnType = targetType.FunctionReturnType!;

        if (value is object?[] arr)
        {
            var wrapped = new object?[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i] is PhoenixmlDb.XQuery.Ast.XQueryFunction fn)
                    wrapped[i] = new FunctionCoercionWrapper(fn, paramTypes, returnType);
                else
                    wrapped[i] = arr[i];
            }
            return wrapped;
        }

        if (value is PhoenixmlDb.XQuery.Ast.XQueryFunction singleFn)
            return new FunctionCoercionWrapper(singleFn, paramTypes, returnType);

        return value;
    }


    /// <summary>
    /// Enforces xsl:context-item constraints on a template invocation.
    /// Returns true if the context item should be made absent (use="optional" with type mismatch).
    /// </summary>
    private bool EnforceContextItemConstraint(XsltTemplate template)
    {
        var ci = ContextItem;
        var isAbsent = ci == null || ci == PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus;

        switch (template.ContextItemUse)
        {
            case ContextItemUse.Required when isAbsent:
                throw Error("XTTE3090: The template requires a context item, but none has been supplied");
            case ContextItemUse.Absent:
                // Context item will be made absent within the template body (handled at call site)
                return false;
        }

        // Type checking: if as attribute is specified, verify the context item matches
        if (template.ContextItemAs != null && !isAbsent)
        {
            var typeMatches = XQuery.Execution.TypeCastHelper.MatchesSequenceItemType(ci, template.ContextItemAs);

            // Additional check for document-node(element(name)): verify document element name
            if (typeMatches && template.ContextItemAs.DocumentElementName != null
                && ci is Xdm.Nodes.XdmDocument doc && _nodeStore != null)
            {
                var docElem = doc.Children
                    .Select(id => _nodeStore.GetNode(id))
                    .OfType<Xdm.Nodes.XdmElement>()
                    .FirstOrDefault();
                if (docElem == null || docElem.LocalName != template.ContextItemAs.DocumentElementName)
                    typeMatches = false;
            }

            if (!typeMatches)
            {
                if (template.ContextItemUse == ContextItemUse.Optional)
                {
                    // XSLT 3.0 §9.6.2: If use="optional" and the context item doesn't match the type,
                    // the template executes as if the context item were absent.
                    return true;
                }
                throw Error("XTTE0590: The context item does not match the required type declared by xsl:context-item");
            }
        }
        return false;
    }


    private static bool CompositeKeysEqual(List<object?> key1, List<object?> key2, StringComparer? comparer = null)
    {
        if (key1.Count != key2.Count)
            return false;

        for (int i = 0; i < key1.Count; i++)
        {
            if (!ValuesEqual(key1[i], key2[i], comparer))
                return false;
        }
        return true;
    }


    /// <summary>
    /// Compares two atomic values for equality using XQuery semantics.
    /// </summary>
    private static bool ValuesEqual(object? a, object? b, StringComparer? comparer = null)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return a is null && b is null;

        // Numeric comparison: use XPath type promotion rules (§B.2)
        // float vs double → both to double; decimal vs float → both to float;
        // decimal vs double → both to double; integer vs X → both to X
        if (IsNumeric(a) && IsNumeric(b))
        {
            bool aIsFloat = a is float;
            bool bIsFloat = b is float;
            bool aIsDecimal = a is decimal;
            bool bIsDecimal = b is decimal;
            // decimal vs float: promote decimal to float (NOT to double)
            if ((aIsDecimal && bIsFloat) || (aIsFloat && bIsDecimal))
            {
                var fa = Convert.ToSingle(a, CultureInfo.InvariantCulture);
                var fb = Convert.ToSingle(b, CultureInfo.InvariantCulture);
                if (float.IsNaN(fa) || float.IsNaN(fb))
                    return false;
                return fa == fb;
            }
            // All other numeric combinations: promote to double
            var da = a is System.Numerics.BigInteger abi ? (double)abi : Convert.ToDouble(a, CultureInfo.InvariantCulture);
            var db = b is System.Numerics.BigInteger bbi ? (double)bbi : Convert.ToDouble(b, CultureInfo.InvariantCulture);
            if (double.IsNaN(da) || double.IsNaN(db))
                return false;
            return da == db;
        }

        var cmp = comparer ?? StringComparer.Ordinal;

        // For XDM nodes, compare string values
        if (a is XdmNode na && b is XdmNode nb)
            return cmp.Equals(na.StringValue, nb.StringValue);
        if (a is XdmNode nodeA)
            return cmp.Equals(nodeA.StringValue, StringValueOf(b));
        if (b is XdmNode nodeB)
            return cmp.Equals(StringValueOf(a), nodeB.StringValue);

        // String-like comparisons: treat XsUntypedAtomic and XsAnyUri as strings
        var sa = a is string s1 ? s1 : a is Xdm.XsUntypedAtomic ua1 ? ua1.Value : a is Xdm.XsAnyUri u1 ? u1.Value : null;
        var sb = b is string s2 ? s2 : b is Xdm.XsUntypedAtomic ua2 ? ua2.Value : b is Xdm.XsAnyUri u2 ? u2.Value : null;
        if (sa != null && sb != null)
            return cmp.Equals(sa, sb);
        if (sa != null || sb != null)
            return cmp.Equals(sa ?? StringValueOf(a), sb ?? StringValueOf(b));

        return object.Equals(a, b);
    }


    internal static string EscapeText(string text)
        => CharacterEscaper.EscapeXmlText(text);


    internal static string EscapeAttributeValue(string value)
        => EscapeControlCharsAsNcr(CharacterEscaper.EscapeXmlAttribute(value));


    /// <summary>
    /// Emits DEL, the C1 control characters (U+0080..U+009F) and the line separator
    /// U+2028 as numeric character references. These characters cannot appear literally
    /// in serialized XML/XHTML attribute values, so they are written as <c>&amp;#xNN;</c>,
    /// matching the XQuery serializer's <c>WriteTextEscaped</c> attribute path
    /// (W3C Serialization 4.0 §7.2). The shared <see cref="CharacterEscaper"/> only
    /// introduces ASCII markup, so the sole remaining control characters in its output
    /// are the original literals — no double-escaping occurs.
    /// </summary>
    private static string EscapeControlCharsAsNcr(string escaped)
    {
        StringBuilder? sb = null;
        for (var i = 0; i < escaped.Length; i++)
        {
            var c = escaped[i];
            if (c == '\u2028' || (c >= '\u007F' && c <= '\u009F'))
            {
                sb ??= new StringBuilder(escaped.Length + 8).Append(escaped, 0, i);
                sb.Append("&#x").Append(((int)c).ToString("X", CultureInfo.InvariantCulture)).Append(';');
            }
            else
            {
                sb?.Append(c);
            }
        }
        return sb?.ToString() ?? escaped;
    }

    private string GenerateNsPrefix(string nsUri)
    {
        // Use well-known prefixes for common namespaces
        if (nsUri == "http://www.w3.org/1999/xhtml")
            return "xhtml";
        if (nsUri == "http://www.w3.org/2000/svg")
            return "svg";
        if (nsUri == "http://www.w3.org/1999/XSL/Transform")
            return "xsl";
        return $"ns{_nsPrefixCounter++}";
    }


    internal static string EscapeCommentValue(string value)
    {
        value = value.Replace("--", "- -", StringComparison.Ordinal);
        if (value.EndsWith('-'))
            value += " ";
        return value;
    }


    internal static string EscapePIValue(string value)
    {
        return value.Replace("?>", "? >", StringComparison.Ordinal);
    }


    /// <summary>
    /// Extracts the literal value from an attribute's <c>=value</c> segment (e.g. <c>="foo"</c>),
    /// stripping the leading <c>=</c>, surrounding whitespace, and quote characters.
    /// </summary>
    private static string ExtractQuotedValue(string eqValue)
    {
        int start = -1;
        char quote = '"';
        for (int j = 0; j < eqValue.Length; j++)
        {
            if (eqValue[j] == '"' || eqValue[j] == '\'')
            {
                start = j;
                quote = eqValue[j];
                break;
            }
        }
        if (start < 0)
            return "";
        int end = -1;
        for (int j = start + 1; j < eqValue.Length; j++)
        {
            if (eqValue[j] == quote)
            {
                end = j;
                break;
            }
        }
        if (end < 0)
            return "";
        return eqValue[(start + 1)..end];
    }


    /// <summary>
    /// Inserts a meta element with Content-Type as the first child of head elements.
    /// Per XSLT serialization spec: when include-content-type is "yes" (default for html/xhtml).
    /// </summary>
    internal static string InsertContentTypeMeta(string output, XsltOutput outputDecl)
    {
        var encoding = outputDecl.Encoding ?? "UTF-8";
        var mediaType = outputDecl.MediaType ?? "text/html";
        var content = $"{mediaType}; charset={encoding}";

        string metaElement;
        if (outputDecl.EffectiveMethod == OutputMethod.Xhtml)
            metaElement = $"<meta http-equiv=\"Content-Type\" content=\"{content}\" />";
        else
            metaElement = $"<meta http-equiv=\"Content-Type\" content=\"{content}\">";

        // Find <head> or <head ...> tags and insert meta as first child.
        // Per XSLT 3.0 §27.6.4 (HTML/XHTML output): the serializer adds a Content-Type
        // meta only if one is not already present in the head. Without this check,
        // a stylesheet that emits its own meta (e.g. Docbook TNG's XHTML-style
        // `<meta http-equiv="Content-Type" content="…" />`) ends up with two of them.
        var result = output;
        var searchFrom = 0;
        while (true)
        {
            var idx = result.IndexOf("<head", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) break;

            var afterName = idx + 5; // length of "<head"
            if (afterName >= result.Length) break;

            var ch = result[afterName];
            if (ch == '>' || ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r')
            {
                // Find closing > of the opening tag
                var closeTag = result.IndexOf('>', afterName);
                if (closeTag >= 0 && result[closeTag - 1] != '/') // not self-closing
                {
                    // Locate the matching </head> and search the head's content for an
                    // existing Content-Type meta. If found, skip insertion for this head.
                    var endHead = result.IndexOf("</head", closeTag + 1, StringComparison.OrdinalIgnoreCase);
                    var headEndAbs = endHead > 0 ? endHead : result.Length;
                    var existing = FindContentTypeMetaSpan(result, closeTag + 1, headEndAbs);
                    if (existing.Start < 0)
                    {
                        // No Content-Type meta present: insert one as the first child of head.
                        result = result.Insert(closeTag + 1, metaElement);
                        searchFrom = closeTag + 1 + metaElement.Length;
                        continue;
                    }
                    // Per the serialization spec (§HTML/XHTML output), an existing
                    // http-equiv="Content-Type" meta is REPLACED by the serializer's computed
                    // value (correct media type + charset) rather than left as-is or duplicated
                    // (output-0143/0144/0157/0158; keeps DocBook TNG to a single correct meta).
                    result = result.Remove(existing.Start, existing.Length).Insert(existing.Start, metaElement);
                    searchFrom = existing.Start + metaElement.Length;
                    continue;
                }
            }
            searchFrom = afterName;
        }
        return result;
    }


    /// <summary>
    /// Locates an existing http-equiv="Content-Type" meta element within result[from..end)
    /// and returns its absolute (start, length) span, or (-1, 0) if none is present.
    /// Attribute names/values are matched case-insensitively per HTML rules.
    /// </summary>
    private static (int Start, int Length) FindContentTypeMetaSpan(string s, int from, int end)
    {
        var i = from;
        while (i < end)
        {
            var lt = s.IndexOf('<', i);
            if (lt < 0 || lt >= end) break;
            i = lt;
            if (i + 5 <= end
                && (s[i + 1] == 'm' || s[i + 1] == 'M')
                && (s[i + 2] == 'e' || s[i + 2] == 'E')
                && (s[i + 3] == 't' || s[i + 3] == 'T')
                && (s[i + 4] == 'a' || s[i + 4] == 'A')
                && (i + 5 == end || s[i + 5] == ' ' || s[i + 5] == '\t' || s[i + 5] == '\n'
                    || s[i + 5] == '\r' || s[i + 5] == '/' || s[i + 5] == '>'))
            {
                var gt = s.IndexOf('>', i);
                if (gt < 0 || gt >= end) break;
                var tag = s.AsSpan(i, gt + 1 - i);
                if (tag.IndexOf("http-equiv", StringComparison.OrdinalIgnoreCase) >= 0
                    && tag.IndexOf("Content-Type", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return (i, gt + 1 - i);
                }
                i = gt + 1;
                continue;
            }
            i++;
        }
        return (-1, 0);
    }

}
