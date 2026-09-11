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

    /// <inheritdoc/>
    public override void PushInstructionLocation(SourceLocation location)
    {
        _locationStack.Push(_currentInstructionLocation);
        _currentInstructionLocation = location;
    }


    /// <inheritdoc/>
    public override void PopInstructionLocation()
    {
        _currentInstructionLocation = _locationStack.Count > 0 ? _locationStack.Pop() : null;
    }


    public override void PushVersion(string version) => _effectiveVersionStack.Push(version);

    public override void PopVersion() => _effectiveVersionStack.Pop();

    public override void PushCollation(string collation) => _defaultCollationStack.Push(collation);

    public override void PopCollation() => _defaultCollationStack.Pop();

    public override void PushStaticBaseUri(string baseUri) => _staticBaseUriStack.Push(baseUri);

    public override void PopStaticBaseUri() => _staticBaseUriStack.Pop();


    /// <summary>
    /// Begins sequence collection mode. xsl:sequence items will be collected into a list
    /// instead of being serialized to output.
    /// </summary>
    public void BeginSequenceCollection()
    {
        _sequenceAccumulator = new List<object?>();
    }


    /// <summary>
    /// Ends sequence collection mode and returns the collected items.
    /// </summary>
    public List<object?> EndSequenceCollection()
    {
        var result = _sequenceAccumulator ?? new List<object?>();
        _sequenceAccumulator = null;
        return result;
    }


    private void PushOutputNsScope(Dictionary<string, string> nsBindings)
    {
        _outputNsScopes.Push(nsBindings);
    }


    private void PopOutputNsScope()
    {
        if (_outputNsScopes.Count > 0)
            _outputNsScopes.Pop();
    }


    /// <summary>
    /// Begins tracking for xsl:where-populated. Call before executing content.
    /// </summary>
    private void BeginPopulatedTracking()
    {
        _populatedTracking.Push(false);
    }


    /// <summary>
    /// Ends tracking for xsl:where-populated. Returns true if content was produced.
    /// </summary>
    private bool EndPopulatedTracking()
    {
        return _populatedTracking.Count > 0 && _populatedTracking.Pop();
    }


    /// <summary>Begin content tracking for xsl:on-empty/xsl:on-non-empty support.</summary>
    public override void BeginContentTracking()
    {
        var outputLen = _output.Length;
        var attrsLen = _collectedAttributesStack.Count > 0 ? _collectedAttributesStack.Peek().Length : 0;
        var accumLen = _sequenceAccumulator?.Count ?? 0;
        _contentTrackingStack.Push((outputLen, attrsLen, accumLen, _lastResultWasAtomic));
        // Reset atomic state so content within this tracking scope starts fresh.
        // This prevents atomic state from a previous scope (e.g., on-empty output
        // in one for-each iteration) from leaking into the next scope and causing
        // spurious space separators before empty-string atomic values.
        _lastResultWasAtomic = false;
        _separatorCharsWritten = 0;
    }


    /// <summary>End content tracking and return whether content was produced.</summary>
    public override bool EndContentTracking()
    {
        if (_contentTrackingStack.Count == 0)
            return false;
        var (outputLen, attrsLen, accumLen, savedLastAtomic) = _contentTrackingStack.Pop();
        var outputGrew = _output.Length > outputLen;
        // If output grew, check if it was only separator spaces between zero-length strings.
        // Zero-length strings are insignificant per XSLT 3.0 §11.4, and their separators
        // should not make content appear non-empty.
        var significantOutput = outputGrew && (_output.Length - outputLen) > _separatorCharsWritten;
        var wasPopulated = significantOutput
            || (_collectedAttributesStack.Count > 0 && _collectedAttributesStack.Peek().Length > attrsLen)
            || (_sequenceAccumulator != null && _sequenceAccumulator.Count > accumLen);
        // When no content was produced, restore atomic spacing state to what it was
        // before the tracking scope. This undoes any side effects from non-on-empty
        // instructions (e.g., serializing an empty string sets the flag but produces
        // no output) while preserving state from before the scope for correct
        // cross-iteration spacing in for-each.
        if (!wasPopulated)
            _lastResultWasAtomic = savedLastAtomic;
        return wasPopulated;
    }


    /// <summary>Save current output state for on-non-empty probe phase.</summary>
    public override object SaveOutput()
    {
        var outputStr = _output.ToString();
        var attrsStr = _collectedAttributesStack.Count > 0 ? _collectedAttributesStack.Peek().ToString() : null;
        return (outputStr, attrsStr, _lastResultWasAtomic);
    }


    /// <summary>Restore output to a previously saved state.</summary>
    public override void RestoreOutput(object savedState)
    {
        var (outputStr, attrsStr, lastAtomic) = ((string, string?, bool))savedState;
        _output.Clear();
        _output.Append(outputStr);
        _lastResultWasAtomic = lastAtomic;
        if (attrsStr != null && _collectedAttributesStack.Count > 0)
        {
            _collectedAttributesStack.Peek().Clear();
            _collectedAttributesStack.Peek().Append(attrsStr);
        }
    }


    public override void SuppressEmptyStringSeparators()
    {
        _wherePopulatedDepth++;
        _preserveAtomicState++;
    }


    public override void RestoreEmptyStringSeparators()
    {
        _wherePopulatedDepth--;
        _preserveAtomicState--;
    }


    public void PushContextItem(object? item, int position, int last)
    {
        _contextItems.Push(item);
        _contextPositions.Push((position, last));
    }


    public void PopContextItem()
    {
        _contextItems.Pop();
        _contextPositions.Pop();
    }


    /// <summary>
    /// Pushes a new "current" item for XSLT current() function.
    /// Call this when entering a new XSLT instruction context (template, for-each, etc).
    /// </summary>
    public void PushCurrentItem(object? item)
    {
        _currentItems.Push(item);
    }


    /// <summary>
    /// Pops the "current" item stack.
    /// </summary>
    public void PopCurrentItem()
    {
        if (_currentItems.Count > 0)
            _currentItems.Pop();
    }


    public void PushScope()
    {
        if (_scopePool.TryPop(out var pooled))
        {
            pooled.Parent = _scopes.Peek();
            _scopes.Push(pooled);
        }
        else
        {
            _scopes.Push(new Scope(_scopes.Peek()));
        }
    }


    public void PopScope()
    {
        var s = _scopes.Pop();
        if (_scopePool.Count >= MaxPooledScopes) return;
        s.Reset();
        _scopePool.Push(s);
    }


    /// <summary>
    /// Checks if a declared variable name matches a reference name.
    /// Handles the case where XPath parser creates QNames with unresolved namespaces.
    /// </summary>
    private static bool VariableNameMatches(QName declared, QName reference)
    {
        if (declared.LocalName != reference.LocalName)
            return false;

        // EQName match: both have expanded namespace URIs
        if (reference.ExpandedNamespace != null && declared.ExpandedNamespace != null)
            return reference.ExpandedNamespace == declared.ExpandedNamespace;

        // EQName reference matching resolved declaration: compare URI to declared namespace
        if (reference.ExpandedNamespace != null && declared.Namespace != NamespaceId.None)
            return true; // Best effort: same local name with non-null expanded ns

        // Same prefix is always a match
        if (declared.Prefix == reference.Prefix)
            return true;

        // Both have resolved namespaces: compare by namespace
        if (reference.Namespace != NamespaceId.None && declared.Namespace == reference.Namespace)
            return true;

        // Reference has unresolved namespace but has a prefix: match any namespaced variable
        // with the same local name. This handles $new:me matching $txt:me when both
        // prefixes refer to the same namespace (which XPath parser can't resolve).
        if (reference.Prefix != null && reference.Namespace == NamespaceId.None &&
            declared.Namespace != NamespaceId.None)
            return true;

        return false;
    }


    /// <summary>
    /// XSLT 3.0: when a called template makes the context item absent
    /// (<c>xsl:context-item use="absent"</c>), the current group and current grouping key are
    /// absent too, so <c>current-group()</c>/<c>current-grouping-key()</c> must raise
    /// XTDE1061/XTDE1071 rather than leak the caller's group (si-fork-113/114). Shadows both
    /// grouping pseudo-variables with null in the callee scope; the caller's group is restored
    /// when that scope pops. Only called on the context-absent invocation path — a normal
    /// call-template retains the focus and correctly keeps the current group visible.
    /// </summary>
    private void SuppressGroupingFocus()
    {
        SetVariable(new QName(NamespaceId.None, "current-group"), null);
        SetVariable(new QName(NamespaceId.None, "current-grouping-key"), null);
    }


    public override async ValueTask BindVariableAsync(XsltVariableInstruction instruction)
    {

        // Try to evaluate the variable. If it fails with "not defined" error (potential
        // circular reference), store as lazy so it's only evaluated if actually used.
        // This implements the XSLT spec requirement that unused variables with circular
        // refs don't cause errors.
        object? value;

        if (instruction.Select != null)
        {
            try
            {
                value = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
            }
            catch (XsltException ex) when (
                ex.Message.Contains("not defined", StringComparison.Ordinal)
                || ex.ErrorCode == "XTDE0640")
            {
                // Potential circular reference - defer evaluation.
                // XTDE0640 is raised when a global variable/param being evaluated is re-accessed.
                // If the variable is never used, this deferred error is harmless (per spec).
                var selectExpr = instruction.Select;
                value = new LazyValue(async () => await EvaluateAsync(selectExpr).ConfigureAwait(false));
            }
        }
        else if (instruction.Content != null)
        {
            // Push effective base URI if the instruction has xml:base
            if (instruction.BaseUri != null)
                _baseUriStack.Push(XsltTransformEngine.UriString(instruction.BaseUri)!);

            // Use sequence accumulator to collect individual items when:
            // - ZeroOrMore/OneOrMore: always (existing behavior for multi-item sequences)
            // - ExactlyOne/ZeroOrOne with node types: needed for cardinality checking
            //   (e.g., as="text() ?" with 3 xsl:text items must raise XTTE0570)
            // For atomic types (xs:string, xs:integer, etc.) with ExactlyOne/ZeroOrOne,
            // the non-sequence path handles them correctly.
            var isNodeItemType = instruction.As != null &&
                instruction.As.ItemType is ItemType.Node or ItemType.Element or ItemType.Text
                or ItemType.Comment or ItemType.Document or ItemType.ProcessingInstruction
                or ItemType.Attribute or ItemType.Item or ItemType.Map or ItemType.Array
                or ItemType.Function;
            var isSequenceType = instruction.As != null &&
                (instruction.As.Occurrence == Occurrence.ZeroOrMore
                || instruction.As.Occurrence == Occurrence.OneOrMore
                || isNodeItemType);

            if (isSequenceType)
            {
                var savedAccumulator = _sequenceAccumulator;
                _sequenceAccumulator = new List<object?>();

                var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
                // Shared typed-body context (see EnterTypedBody). This seam is the reference
                // the others were compared against while consolidating; it uses the same helper
                // so the definition lives in one place rather than being copied here.
                var typedBody = EnterTypedBody(instruction.As);
                // Save and clear namespace scopes so RTF content has all needed declarations
                var savedNsScopes = new List<Dictionary<string, string>>(_outputNsScopes);
                _outputNsScopes.Clear();
                // Save and reset text/attribute content depth so LREs in variable content
                // are processed fully (not suppressed as in attribute/comment/PI bodies)

                // A variable's value is an independent temp tree / sequence, so its content must
                // NOT inherit the enclosing element's attribute-collection state. Otherwise an
                // xsl:copy / xsl:attribute inside the variable content — whose accumulator capture
                // is gated on !_attributeCollecting (CopySingleItemAsync attribute branch) — leaks
                // the constructed attribute onto the enclosing open element's start tag instead of
                // into $var. si-copy-003/004: a streamed `<xsl:for-each select="P/@v"><xsl:copy/>`
                // inside `<xsl:variable as="attribute(*)*">` under an open `<out>` LRE.

                // Install an AsBodyCapture so xsl:sequence items appended during body
                // execution record the _output offset at which they occur. Without this,
                // literal-result-elements (which stream to _output) and xsl:sequence
                // select= atomics (which go to the accumulator) form two segregated buckets;
                // recording positions lets the combine phase below weave them back into
                // document order (insn/sequence-0137a). OutputBaseLen is the buffer length
                // at body start, matching savedScope so positions index into textContent.
                var savedAsBodyCapture = _currentAsBodyCapture;
                var asBodyCapture = new AsBodyCapture
                {
                    Accumulator = _sequenceAccumulator,
                    OutputBaseLen = savedScope.SavedLength,
                    AttrDepthAtStart = _collectedAttributesStack.Count,
                };
                _currentAsBodyCapture = asBodyCapture;
                // When the target type allows multiple items (text()*, node()*, item()*),
                // collect each text write as a separate accumulator item so they remain
                // individual text nodes (not merged via string concatenation).
                var needsTextCollection = instruction.As != null &&
                    instruction.As.ItemType is ItemType.Text or ItemType.Node or ItemType.Item
                    && instruction.As.Occurrence is Occurrence.ZeroOrMore or Occurrence.OneOrMore;

                _temporaryOutputDepth++;
                // EMIT context (temp-tree base-URI preservation): when the declared type is a
                // node type whose body is serialized to TEXT and reparsed below (e.g.
                // as="document-node()" / as="element()*" built via apply-templates or
                // shallow-copy), raise the temp-tree depth and seed the serialize base context
                // so a SOURCE element copied in whose base URI differs picks up a base sentinel
                // that the reparse (ReadAsBodyChunkChildren) recovers onto CopySourceBaseUri.
                // Mirrors CreateDocumentAsync. Scoped to the body serialization only and
                // restored in finally, so the sentinel can never reach final output.
                var raiseTempTreeDepth = _nodeStore != null && isNodeItemType;
                var savedSeqBaseContext = _serializeBaseContext;
                if (raiseTempTreeDepth)
                {
                    _tempTreeSerializeDepth++;
                    _serializeBaseContext = XsltTransformEngine.UriString(instruction.BaseUri) ?? EffectiveBaseUri;
                }
                // SP-B slice 5a: install a fresh TreeConstructor for the duration of this
                // as="..."-typed body so from-scratch element/LRE/xsl:copy emitters build XDM
                // nodes directly. In PRODUCTION we now DELIVER those node roots when the body is
                // fully migrated (see the completeness gate below); when the differential toggle
                // is on we additionally assert byte-parity against a raw reparse. The legacy
                // serialize-reparse still runs unconditionally to (a) produce the fallback value
                // for incomplete bodies and (b) supply the reparse node count for the gate.
                TreeConstructor? bodyTc = null;
                var savedActiveTc = _activeTreeConstructor;
                var savedTcIncomplete = _tcFragmentIncomplete;
                // Snapshot of the body's own incomplete flag, captured in the finally before the
                // field is restored, so the migrated-candidate/differential gates below read the
                // value the body left behind rather than the outer saved value.
                var bodyTcIncomplete = false;
                if (_nodeStore != null)
                {
                    bodyTc = new TreeConstructor(_nodeStore, 1UL);
                    _activeTreeConstructor = bodyTc;
                    _tcFragmentIncomplete = false;
                }
                // Delta, not absolute: the body may be nested inside another typed body that
                // already ran an xsl:value-of, and only THIS body's instructions count.
                var alwaysTextBefore = _alwaysTextInstructionCount;
                var bodyAlwaysTextCount = 0;
                try
                { await instruction.Content.ExecuteAsync(this).ConfigureAwait(false); }
                finally
                {
                    bodyAlwaysTextCount = _alwaysTextInstructionCount - alwaysTextBefore;
                    _activeTreeConstructor = savedActiveTc;
                    bodyTcIncomplete = _tcFragmentIncomplete;
                    _tcFragmentIncomplete = savedTcIncomplete;
                    if (raiseTempTreeDepth)
                    {
                        _tempTreeSerializeDepth--;
                        _serializeBaseContext = savedSeqBaseContext;
                    }
                    _temporaryOutputDepth--;
                    ExitTypedBody(typedBody);
                    _currentAsBodyCapture = savedAsBodyCapture;
                    // Restore the enclosing element's attribute-collection stack (bottom-first so
                    // the original top-of-stack ends up on top again).

                }
                var textContent = savedScope.GetWritten();

                savedScope.Dispose();

                // Restore namespace scopes
                _outputNsScopes.Clear();
                foreach (var scope in savedNsScopes.AsEnumerable().Reverse())
                    _outputNsScopes.Push(scope);

                // SP-B slice 5a: finish the native node build ONCE and decide whether it is the
                // authoritative delivered result. A body is a "migrated candidate" when nothing
                // forced the string buffer (`_tcFragmentIncomplete` — xsl:copy-of of a source
                // node, built-in copy, an unresolvable attribute prefix) AND no item bypassed the
                // constructor into the sequence accumulator (xsl:sequence select=, xsl:attribute,
                // xsl:document). The final count-guard (reparse node count == node-root count) and
                // an all-nodes check happen below once sequenceItems is built. `_sequenceAccumulator`
                // is still the raw body accumulator here (the interleave consumes it later).
                IReadOnlyList<NodeId>? bodyNodeRoots = null;
                var migratedCandidate = false;
                if (bodyTc != null)
                {
                    bodyNodeRoots = bodyTc.FinishFragment();
                    migratedCandidate = !bodyTcIncomplete
                        && _sequenceAccumulator.Count == 0
                        && bodyNodeRoots.Count > 0;
                    // Differential: node build vs legacy reparse (structural parity check). Skip
                    // when the body is incomplete — the live tree is known-partial there, so a
                    // comparison would be a false divergence, not a real parity bug (counted Skipped).
                    if (TempTreeDifferential.Enabled)
                    {
                        if (!bodyTcIncomplete)
                            RunTreeConstructorDifferential(bodyNodeRoots, textContent);
                        else
                            TempTreeDifferential.Skipped++;
                    }
                }

                // Combine sequence accumulator items with any serialized output.
                // Literal result elements go to the output buffer; xsl:sequence items go
                // to the accumulator. We must merge them into a single sequence — and,
                // crucially, in construction (document) order. An `xsl:sequence` with
                // content interleaves LREs and nested `xsl:sequence select=` results
                // (insn/sequence-0137a); segregating "all atomics, then all nodes" reorders
                // the result. The AsBodyCapture installed above recorded, for each
                // accumulator item, the _output offset at which it was produced, so the
                // interleave below places each item at its true position in textContent.
                var sequenceItems = new List<object?>();
                var accBaseUri = XsltTransformEngine.UriString(instruction.BaseUri) ?? EffectiveBaseUri;
                var seqBaseUri = accBaseUri;
                // text()* and node()* DEMAND nodes, so a string item contributed by
                // xsl:sequence has to be wrapped into a text node to satisfy the declared type.
                // item()* does NOT: item() is the union of nodes AND atomic values, so wrapping
                // there silently retyped every atomic the body produced.
                //
                //   <xsl:template name="two" as="item()*"><xsl:sequence select="('a','b')"/></...>
                //   <xsl:variable name="v" as="item()*"><xsl:call-template name="two"/></...>
                //   $v[1] instance of xs:string   ->  false, it was a text node
                //
                // deep-equal($v, ('a','b')) is then correctly false, which is how this surfaced:
                // XSpec compares a @test expression's value against @select, and every expect
                // whose test yields atomic values failed on the type rather than the value.
                // as="xs:string*" masked it — that type coerces the text nodes back to strings.
                //
                // Literal character content in the body is unaffected: it reaches the result
                // through textContent, not as a string item in the accumulator, so
                // <xsl:variable as="item()*">hello</xsl:variable> still yields a text node.
                var wrapAsTextNode = _nodeStore != null && instruction.As != null
                    && instruction.As.ItemType is ItemType.Text or ItemType.Node;

                // Appends one accumulated item (from xsl:sequence select="..." / xsl:attribute /
                // xsl:document) to sequenceItems, applying base-URI fixup and text-node wrapping.
                void AddAccItem(object? item)
                {
                    if (item != null)
                    {
                        // Set base URI on orphaned nodes from the construction context.
                        // Skip nodes with CopySourceBaseUri — they preserve the source
                        // element's base URI from xsl:copy (XSLT 3.0 §11.9.1).
                        if (item is XdmNode accNode && accNode.BaseUri == null
                            && accNode.CopySourceBaseUri == null
                            && (!accNode.Parent.HasValue || accNode.Parent.Value == NodeId.None)
                            && accBaseUri != null)
                        {
                            accNode.BaseUri = accBaseUri;
                        }
                        // When as="text()*" or as="node()*", wrap string/TextNodeItem as XDM text nodes
                        // so they remain individual nodes (not merged via string concatenation)
                        if (wrapAsTextNode && item is string strItem)
                        {
                            var textId = _nodeStore!.NextId();
                            sequenceItems.Add(new Xdm.Nodes.XdmText
                            {
                                Id = textId,
                                Document = DocumentId.None,
                                Parent = NodeId.None,
                                Value = strItem
                            });
                        }
                        else if (wrapAsTextNode && item is Xdm.TextNodeItem tni)
                        {
                            var textId = _nodeStore!.NextId();
                            sequenceItems.Add(new Xdm.Nodes.XdmText
                            {
                                Id = textId,
                                Document = DocumentId.None,
                                Parent = NodeId.None,
                                Value = tni.Value
                            });
                        }
                        else
                        {
                            sequenceItems.Add(item);
                        }
                    }
                    // null items (from empty xsl:text) contribute nothing for non-node types.
                }

                // Parses a serialized-LRE chunk into individual XDM nodes, appending them to
                // sequenceItems. Each LRE carries its own namespace declarations (output
                // namespace scopes were cleared before body execution), so a chunk is
                // self-contained and safe to parse in isolation.
                void AddElementChunk(string chunk)
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
                        using var stringReader = new System.IO.StringReader($"<_seq_root_>{chunk}</_seq_root_>");
                        using var reader = System.Xml.XmlReader.Create(stringReader, settings);
                        var parsedChildren = new List<object?>();
                        ReadAsBodyChunkChildren(reader, parsedChildren);
                        foreach (var child in parsedChildren)
                        {
                            if (child is XdmNode cn)
                            {
                                cn.Parent = null;
                                // Don't clobber a base URI recovered from the temp-tree base
                                // sentinel (a source element copied into the body): that
                                // preserved source base must win over the construction base,
                                // mirroring the accumulator handling above.
                                if (seqBaseUri != null && cn.CopySourceBaseUri == null)
                                    cn.BaseUri = seqBaseUri;
                            }
                            sequenceItems.Add(child);
                        }
                    }
                    catch (System.Xml.XmlException)
                    {
                        // If parsing fails, wrap as RTF.
                        sequenceItems.Add(new ResultTreeFragment(chunk, accBaseUri));
                    }
                }

                // Appends a chunk of serialized body output: parse as nodes when it contains
                // markup, else treat as plain text (a text node for node-ish types, an atomic
                // string otherwise, XML entities decoded).
                void AddTextChunk(string chunk)
                {
                    if (string.IsNullOrEmpty(chunk))
                        return;
                    if (_nodeStore != null && chunk.Contains('<', StringComparison.Ordinal))
                    {
                        AddElementChunk(chunk);
                    }
                    else if (_nodeStore != null && instruction.As != null
                        && instruction.As.ItemType is ItemType.Text or ItemType.Node or ItemType.Item)
                    {
                        var textId = _nodeStore.NextId();
                        sequenceItems.Add(new Xdm.Nodes.XdmText
                        {
                            Id = textId,
                            Document = DocumentId.None,
                            Parent = NodeId.None,
                            Value = StripXmlMarkup(chunk)
                        });
                    }
                    else
                    {
                        sequenceItems.Add(StripXmlMarkup(chunk));
                    }
                }

                if (instruction.As?.ItemType == ItemType.Document)
                {
                    // as="document-node()" produces a single node wrapping the whole body,
                    // so interleaving is meaningless. Preserve the historical ordering:
                    // accumulator items first, then the serialized body as one document node.
                    foreach (var item in _sequenceAccumulator)
                        AddAccItem(item);

                    if (!string.IsNullOrEmpty(textContent) && _nodeStore != null && textContent.Contains('<', StringComparison.Ordinal))
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
                            using var stringReader = new System.IO.StringReader($"<_seq_root_>{textContent}</_seq_root_>");
                            using var reader = System.Xml.XmlReader.Create(stringReader, settings);
                            var docId = _nodeStore.NextId();
                            var children = new List<NodeId>();
                            NodeId docElementId = NodeId.None;
                            var parsedChildren = new List<object?>();
                            ReadAsBodyChunkChildren(reader, parsedChildren);
                            foreach (var item in parsedChildren)
                            {
                                if (item is XdmNode cn)
                                {
                                    cn.Parent = docId;
                                    children.Add(cn.Id);
                                    if (cn is XdmElement && docElementId == NodeId.None)
                                        docElementId = cn.Id;
                                }
                            }
                            string? docElemLocalName2 = docElementId != NodeId.None
                                ? (_nodeStore.GetNode(docElementId) as XdmElement)?.LocalName : null;
                            var docNode = new XdmDocument
                            {
                                StringValueResolver = _nodeStore.StringValueResolver,
                                Id = docId,
                                Document = new DocumentId(1),
                                Parent = NodeId.None,
                                DocumentElement = docElementId,
                                Children = children,
                                DocumentElementLocalName = docElemLocalName2,
                                BaseUri = seqBaseUri
                            };
                            _nodeStore.Register(docNode);
                            sequenceItems.Add(docNode);
                        }
                        catch (System.Xml.XmlException)
                        {
                            sequenceItems.Add(new ResultTreeFragment(textContent, accBaseUri));
                        }
                    }
                    else if (!string.IsNullOrEmpty(textContent))
                    {
                        AddTextChunk(textContent);
                    }
                }
                else
                {
                    // Interleave accumulator items with parsed LRE chunks in construction
                    // order, using the offsets AsBodyCapture recorded (insn/sequence-0137a).
                    var positions = asBodyCapture.Positions;
                    var cursor = 0;
                    for (var i = 0; i < _sequenceAccumulator.Count; i++)
                    {
                        var pos = i < positions.Count ? positions[i] : textContent.Length;
                        if (pos > textContent.Length) pos = textContent.Length;
                        if (pos < cursor) pos = cursor;
                        if (pos > cursor)
                        {
                            AddTextChunk(textContent.Substring(cursor, pos - cursor));
                            cursor = pos;
                        }
                        AddAccItem(_sequenceAccumulator[i]);
                    }
                    if (cursor < textContent.Length)
                        AddTextChunk(textContent[cursor..]);

                    // XSLT 3.0: an empty xsl:value-of body still creates a zero-length text
                    // node — only when nothing else was produced.
                    if (sequenceItems.Count == 0 && textContent != null && textContent.Length == 0
                        && bodyAlwaysTextCount > 0
                        && instruction.As != null
                        && instruction.As.ItemType is ItemType.Item or ItemType.Text or ItemType.Node)
                    {
                        if (_nodeStore != null)
                        {
                            var textId = _nodeStore.NextId();
                            sequenceItems.Add(new Xdm.Nodes.XdmText
                            {
                                Id = textId,
                                Document = DocumentId.None,
                                Parent = NodeId.None,
                                Value = ""
                            });
                        }
                        else
                        {
                            sequenceItems.Add(textContent);
                        }
                    }
                }

                // SP-B slice 5a: deliver the natively-built node roots as the authoritative result
                // when the body was fully migrated. Byte-parity with the reparse today (Task 4
                // proved node == reparse structurally at 147/0); this flip is what lets slice 5b's
                // node-build bug fixes take effect. The final gate reuses the differential's
                // count-guard: the node-root count must equal the reparse node count, and every
                // delivered item must be a node (no atomics/RTF/accumulator items) — so we only
                // ever swap in shapes the differential verified equal, never an unverified one.
                if (migratedCandidate && bodyNodeRoots != null && _nodeStore != null)
                {
                    if (instruction.As?.ItemType == ItemType.Document)
                    {
                        // as="document-node()": the reparse wrapped the body in a single
                        // XdmDocument. Rewrap the node roots identically (byte-parity with the
                        // legacy doc build above) so slice 5b can improve the wrapped children.
                        if (sequenceItems.Count == 1 && sequenceItems[0] is XdmDocument reparseDoc
                            && reparseDoc.Children.Count == bodyNodeRoots.Count)
                        {
                            var docId = _nodeStore.NextId();
                            var docChildren = new List<NodeId>();
                            NodeId flipDocElementId = NodeId.None;
                            foreach (var id in bodyNodeRoots)
                            {
                                if (_nodeStore.GetNode(id) is not XdmNode cn)
                                    continue;
                                cn.Parent = docId;
                                docChildren.Add(cn.Id);
                                if (cn is XdmElement && flipDocElementId == NodeId.None)
                                    flipDocElementId = cn.Id;
                            }
                            string? flipDocElemLocalName = flipDocElementId != NodeId.None
                                ? (_nodeStore.GetNode(flipDocElementId) as XdmElement)?.LocalName : null;
                            var flipDocNode = new XdmDocument
                            {
                                StringValueResolver = _nodeStore.StringValueResolver,
                                Id = docId,
                                Document = new DocumentId(1),
                                Parent = NodeId.None,
                                DocumentElement = flipDocElementId,
                                Children = docChildren,
                                DocumentElementLocalName = flipDocElemLocalName,
                                BaseUri = seqBaseUri
                            };
                            _nodeStore.Register(flipDocNode);
                            sequenceItems.Clear();
                            sequenceItems.Add(flipDocNode);
                        }
                    }
                    else if (textContent != null && textContent.Contains('<', StringComparison.Ordinal)
                        && sequenceItems.Count == bodyNodeRoots.Count
                        && sequenceItems.All(static x => x is XdmNode))
                    {
                        // Non-document: with the accumulator empty there was no interleave, so
                        // sequenceItems is exactly the reparse node roots in document order. Swap
                        // in the constructor's node roots, applying the construction base-URI fixup
                        // AddElementChunk applies to markup-bearing reparse nodes (plain seqBaseUri,
                        // never clobbering a preserved xsl:copy CopySourceBaseUri).
                        //
                        // Gate on textContent containing '<' — the SAME predicate
                        // RunTreeConstructorDifferential uses (:13906) to decide whether it builds
                        // rawNodeIds and compares. A pure-text (no-'<') body is reparsed by
                        // AddTextChunk (:19739), which creates an XdmText with BaseUri left null; the
                        // differential skips it (rawNodeIds empty vs 1 text root → count-guard skip),
                        // so it is never proven byte-equal. Applying the markup fixup here would set
                        // BaseUri = seqBaseUri, regressing base-uri($v) from () to the stylesheet URI.
                        // Keeping this in lockstep with the differential means flip ⊆ compared.
                        sequenceItems.Clear();
                        foreach (var id in bodyNodeRoots)
                        {
                            if (_nodeStore.GetNode(id) is not XdmNode cn)
                                continue;
                            cn.Parent = null;
                            if (seqBaseUri != null && cn.CopySourceBaseUri == null)
                                cn.BaseUri = seqBaseUri;
                            sequenceItems.Add(cn);
                        }
                    }
                }

                // A variable declared with a NODE type must hold real nodes. The body may have
                // produced TextNodeItem markers, which are not nodes: `<xsl:variable as="text()">
                // <xsl:text>t</xsl:text></xsl:variable>` then failed any operation requiring one,
                // e.g. "An operand of the except operator is not a node". Binding a variable is
                // where a sequence stops being under construction and becomes a value, so it is
                // where the marker has to be made real.
                if (instruction.As!.ItemType is ItemType.Node or ItemType.Text or ItemType.Item)
                {
                    for (var i = 0; i < sequenceItems.Count; i++)
                    {
                        if (sequenceItems[i] is Xdm.TextNodeItem tniVar
                            && MaterializeTextNodeItem(tniVar) is { } realText)
                            sequenceItems[i] = realText;
                    }
                }

                // For ExactlyOne/ZeroOrOne: check cardinality, then unwrap to single item
                // so downstream code sees a single value (not an array).
                var occurrence = instruction.As!.Occurrence;
                if (occurrence == Occurrence.ExactlyOne || occurrence == Occurrence.ZeroOrOne)
                {
                    // Report the COUNT and what the items actually are. A cardinality failure
                    // says the body produced the wrong number of items, and the number — and
                    // what they were — is the whole diagnosis. Without it the message names
                    // only the declared type, which the author already knows.
                    if (occurrence == Occurrence.ExactlyOne && sequenceItems.Count != 1)
                        throw Error($"XTTE0570: Variable ${instruction.Name.LocalName} value does not match declared type {instruction.As.ItemType} {instruction.As.Occurrence}"
                            + $" — the body produced {DescribeSequenceForDiagnostics(sequenceItems)}");
                    if (occurrence == Occurrence.ZeroOrOne && sequenceItems.Count > 1)
                        throw Error($"XTTE0570: Variable ${instruction.Name.LocalName} value does not match declared type {instruction.As.ItemType} {instruction.As.Occurrence}"
                            + $" — the body produced {DescribeSequenceForDiagnostics(sequenceItems)}");
                    // Unwrap single item so variable value is not an array
                    value = sequenceItems.Count == 1 ? sequenceItems[0] : null;
                }
                else
                {
                    value = sequenceItems.Count > 0 ? sequenceItems.ToArray() : Array.Empty<object?>();
                }

                _sequenceAccumulator = savedAccumulator;
            }
            else
            {
                var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
                var savedLogicalStart = _outputLogicalStart;
                _outputLogicalStart = _output.Length;
                var savedAtomic2 = _lastResultWasAtomic;
                _lastResultWasAtomic = false;
                // Variable content creates a document fragment — isolate attribute collection
                // from parent elements so attributes don't leak (XTDE0420)
                var savedAttrStack = new List<StringBuilder>(_collectedAttributesStack);
                _collectedAttributesStack.Clear();
                // Save and clear namespace scopes so RTF content has all needed declarations
                var savedNsScopes2 = new List<Dictionary<string, string>>(_outputNsScopes);
                _outputNsScopes.Clear();
                // Save and reset text/attribute content depth so LREs in variable content
                // are processed fully (not suppressed as in attribute/comment/PI bodies)
                var savedTextDepth = _textContentDepth;
                var savedAttrContentDepth = _attributeContentDepth;
                _textContentDepth = 0;
                _attributeContentDepth = 0;
                // Save and replace _sequenceAccumulator. When this xsl:variable bind runs
                // inside a function body (which sets up its own accumulator to capture
                // xsl:sequence return values), failing to isolate here means xsl:sequence
                // inside our body leaks into the FUNCTION's accumulator instead of
                // contributing to our variable's value — body output ends up empty and the
                // variable gets the empty string. Found in Docbook TNG `$process` (xs:boolean
                // body inside fp:run-transforms function).
                var savedAccumulatorVar = _sequenceAccumulator;
                _sequenceAccumulator = new List<object?>();
                _documentNodeDepth++;
                _temporaryOutputDepth++;
                // SP-C slice 3: install a fresh TreeConstructor for the duration of this
                // untyped (no `as=`) RTF body so from-scratch element/LRE/text emitters build
                // XDM nodes directly. When the body is fully node-native we DELIVER that node
                // document via ResultTreeFragment.CachedDocumentNode below, so consumers'
                // ParseResultTreeFragment short-circuits to the node build with no
                // serialize-reparse. The string buffer stays populated as the fallback (for
                // incomplete bodies, xsl:copy-of source nodes, unresolvable prefixes) and as the
                // differential reference. Mirrors the as="..."-typed seam above.
                // Only install the constructor when a static scan proves the body free of
                // base-URI-perturbing / copy-rerouting constructs. Installing it reroutes xsl:copy /
                // xsl:copy-of of a SOURCE node away from the legacy accumulator+drain base-URI
                // preservation onto the constructor branch, which (without Task-6 base sentinel
                // work) drops the copy-source base URI from the serialized fallback; and an
                // xml:base inside the body needs the reparse's resolution the node build does not
                // reproduce. Excluding those bodies keeps them byte-identical to the pre-seam path
                // (no flip, unchanged content) while the common copy-free / xml:base-free body still
                // builds nodes and flips. See UntypedRtfFlipBlocked.
                TreeConstructor? bodyTc = null;
                var savedActiveTcU = _activeTreeConstructor;
                var savedTcIncompleteU = _tcFragmentIncomplete;
                var savedFlipActiveU = _untypedRtfFlipActive;
                var savedFlipDivergentU = _untypedRtfFlipDivergent;
                var bodyTcIncompleteU = false;
                var bodyFlipDivergentU = false;
                var savedFlipBaseContextU = _untypedFlipBaseContext;
                // Hardening: the inherit-namespaces="no" context flag is owned by the construction
                // sites (xsl:copy/LRE/xsl:element) that save-restore it. Reset it at the variable
                // seam so a stale value from an OUTER inherit-namespaces="no" construction can't
                // leak into a bare xsl:copy-of body inside this variable (which has no enclosing
                // construction site of its own to reset it). Restored in the finally.
                var savedInheritNsNoU = _inheritNamespacesNo;
                if (_nodeStore != null && !UntypedRtfFlipBlocked(instruction.Content))
                {
                    bodyTc = new TreeConstructor(_nodeStore, 1UL);
                    _activeTreeConstructor = bodyTc;
                    _tcFragmentIncomplete = false;
                    _untypedRtfFlipActive = true;
                    _untypedRtfFlipDivergent = false;
                    _untypedFlipBaseContext = XsltTransformEngine.UriString(instruction.BaseUri) ?? EffectiveBaseUri;
                    _inheritNamespacesNo = false;
                }
                List<object?>? capturedAccumulator;
                try
                { await instruction.Content.ExecuteAsync(this).ConfigureAwait(false); }
                finally
                {
                    if (bodyTc != null)
                    {
                        _activeTreeConstructor = savedActiveTcU;
                        bodyTcIncompleteU = _tcFragmentIncomplete;
                        _tcFragmentIncomplete = savedTcIncompleteU;
                        bodyFlipDivergentU = _untypedRtfFlipDivergent;
                        _untypedRtfFlipActive = savedFlipActiveU;
                        _untypedRtfFlipDivergent = savedFlipDivergentU;
                        _untypedFlipBaseContext = savedFlipBaseContextU;
                        _inheritNamespacesNo = savedInheritNsNoU;
                    }
                    capturedAccumulator = _sequenceAccumulator;
                    _sequenceAccumulator = savedAccumulatorVar;
                    _temporaryOutputDepth--;
                    _documentNodeDepth--;
                    _textContentDepth = savedTextDepth;
                    _attributeContentDepth = savedAttrContentDepth;
                    _collectedAttributesStack.Clear();
                    // Restore BOTTOM-first — see the note at the ApplyTemplates seam.
                    for (var i = savedAttrStack.Count - 1; i >= 0; i--)
                        _collectedAttributesStack.Push(savedAttrStack[i]);
                    // Restore namespace scopes
                    _outputNsScopes.Clear();
                    foreach (var scope in savedNsScopes2.AsEnumerable().Reverse())
                        _outputNsScopes.Push(scope);
                }
                // RTF construction (no 'as=') only reads the text buffer below — drain
                // any captured accumulator items into _output (still scoped to savedScope)
                // before reading content. Without this, an inner template/instruction whose
                // 'as=' path routes its result into our accumulator-as-barrier vanishes from
                // the variable's tree. Found in SchXslt2 transpile: an `as="element(...)"`
                // template's result was lost when called from inside an unconstrained
                // `<xsl:variable name="transpiled-schematron">…<xsl:apply-templates/>…`.
                if (instruction.As == null && capturedAccumulator is { Count: > 0 })
                {
                    var savedAccDrain = _sequenceAccumulator;
                    _sequenceAccumulator = null;
                    // EMIT context: this drain serializes accumulator items (which may be
                    // source-element copies carrying CopySourceBaseUri) to TEXT that becomes
                    // an RTF and is reparsed below. Raise the temp-tree depth so the base
                    // sentinel rides through and the reparse recovers CopySourceBaseUri.
                    var drainBaseUri = XsltTransformEngine.UriString(instruction.BaseUri) ?? EffectiveBaseUri;
                    _tempTreeSerializeDepth++;
                    var savedDrainBaseContext = _serializeBaseContext;
                    _serializeBaseContext = drainBaseUri;
                    try
                    {
                        // §5.7.2 complex-content construction: adjacent atomic values in the
                        // body sequence are separated by a single space. The xsl:sequence
                        // accumulator branch appended each atomic as-is (no separator) and the
                        // plain string branch of SerializeResult treats strings as non-atomic
                        // text, so draining via a bare SerializeResult loop concatenates
                        // adjacent atomics with no separator (two empty strings collapse to ""
                        // rather than a single space — seqtor-036a/037a/039a/040a). Route the
                        // drain through SerializeSequenceItems, which inserts the single-space
                        // separator between adjacent atomic items while leaving nodes untouched.
                        var drainItems = new System.Collections.ArrayList(capturedAccumulator.Count);
                        foreach (var item in capturedAccumulator)
                            if (item != null)
                                drainItems.Add(item);
                        SerializeSequenceItems(drainItems);
                    }
                    finally
                    {
                        _tempTreeSerializeDepth--;
                        _serializeBaseContext = savedDrainBaseContext;
                        _sequenceAccumulator = savedAccDrain;
                    }
                }
                var content = savedScope.GetWritten();

                savedScope.Dispose();
                _outputLogicalStart = savedLogicalStart;
                _lastResultWasAtomic = savedAtomic2;
                // XSLT 2.0: variables with content and no 'as' attribute always create a
                // temporary tree (document node). Variables with 'as' that land here have typed
                // content that should remain as-is (e.g., namespace-node(), atomic types).
                var varBaseUri = XsltTransformEngine.UriString(instruction.BaseUri) ?? EffectiveBaseUri;
                if (instruction.As == null)
                {
                    var rtf = new ResultTreeFragment(content, varBaseUri);
                    // A body is a migrated candidate when nothing forced the string buffer
                    // (`_tcFragmentIncomplete` — xsl:copy-of of a source node, built-in copy, an
                    // unresolvable attribute prefix), the accumulator drain above did not run (its
                    // items go to the string buffer, NOT the constructor), and the body actually
                    // produced markup (a pure-text body has no node structure to deliver). We then
                    // build a document node from the constructor's roots — byte-identical to what
                    // ParseResultTreeFragment(content) would reparse — and pre-populate the RTF's
                    // CachedDocumentNode so every consumer uses it with no reparse.
                    var migratedCandidate = bodyTc != null && !bodyTcIncompleteU
                        && content.Contains('<', StringComparison.Ordinal)
                        && (capturedAccumulator == null || capturedAccumulator.Count == 0);
                    // SP-C slice 3 scope guard: the legacy reparse (the mandated byte-parity
                    // reference for THIS task) diverges from the node build on two Task-6 shapes:
                    //   (1) it LOSES an xsl:copy source element's base URI (no base sentinel is
                    //       emitted on the plain untyped-RTF serialize path), where the constructor
                    //       faithfully preserves CopySourceBaseUri; and
                    //   (2) it OVER-DECLARES namespaces — ConvertToXdm records each element's FULL
                    //       in-scope set (GetNamespacesInScope(All)) as NamespaceDeclarations, so a
                    //       nested element re-lists every inherited namespace, where the constructor
                    //       records only local declarations.
                    // Both are Task-6 (namespace/base-uri model) corrections, out of scope here.
                    // Exclude those shapes so the flip stays byte-identical to the reparse; the
                    // common namespace-free / copy-free body still flips.
                    // SP-C targeted: when a copy-of routed into the flip constructor produced the
                    // AUTHORITATIVE node build that intentionally diverges from the reparse (§11.7.2
                    // namespace fixup the reparse gets wrong — W3C copy-1220/1221), deliver it
                    // WITHOUT the byte-parity-safe veto (which would otherwise reject the divergent
                    // shape) and skip the differential comparison below.
                    IReadOnlyList<NodeId>? flipRoots = null;
                    if (migratedCandidate && _nodeStore != null)
                    {
                        flipRoots = bodyTc!.FinishFragment();
                        if (!bodyFlipDivergentU && !IsUntypedRtfFlipByteParitySafe(flipRoots, bodyTc))
                            migratedCandidate = false;
                    }
                    if (migratedCandidate && flipRoots != null && _nodeStore != null)
                    {
                        var flipDoc = BuildUntypedRtfFlipDocument(flipRoots, varBaseUri);
                        rtf.CachedDocumentNode = flipDoc;
                        // Differential: assert the node build equals the legacy reparse. Build the
                        // reparse reference from a FRESH RTF so its cache isn't the node doc we just
                        // set. CompareDocuments checks children recursively (kinds, names, ns sets,
                        // attributes, text, base URIs) — the structural parity that matters.
                        if (TempTreeDifferential.Enabled && bodyFlipDivergentU)
                        {
                            // Known-divergent §11.7.2 shape: the reparse is a wrong oracle, so the
                            // structural comparison would be a false positive. Skip it.
                            TempTreeDifferential.Skipped++;
                        }
                        else if (TempTreeDifferential.Enabled)
                        {
                            var reference = ParseResultTreeFragment(new ResultTreeFragment(content, varBaseUri));
                            if (reference != null)
                            {
                                var diff = TempTreeDifferential.TreeEqual(flipDoc.Id, reference.Id, _nodeStore);
                                if (diff != null && !TempTreeDifferential.IsExpectedDivergence(diff))
                                    throw new System.InvalidOperationException($"TEMPTREE-DIFF (untyped-rtf): {diff}");
                                TempTreeDifferential.Compared++;
                            }
                            else
                            {
                                TempTreeDifferential.Skipped++;
                            }
                        }
                    }
                    else if (TempTreeDifferential.Enabled && bodyTc != null)
                    {
                        TempTreeDifferential.Skipped++;
                    }
                    value = rtf;
                }
                else if (capturedAccumulator is { Count: > 0 } && content.Length == 0)
                {
                    // The body produced typed items via xsl:sequence (or similar) — preserve
                    // them rather than serializing through text. For ExactlyOne/ZeroOrOne the
                    // single item is unwrapped; the downstream type validation handles
                    // cardinality and coercion.
                    var occ = instruction.As.Occurrence;
                    if (occ == Occurrence.ExactlyOne || occ == Occurrence.ZeroOrOne)
                        value = capturedAccumulator.Count == 1 ? capturedAccumulator[0] : capturedAccumulator.ToArray();
                    else
                        value = capturedAccumulator.ToArray();
                }
                // When `as=` allows the empty sequence and the body produced no content
                // (no text and no accumulator items), the value MUST be the empty sequence,
                // not the empty string. Without this guard, an `xsl:variable as="xs:string?"`
                // wrapping an `xsl:for-each` whose body never fires ended up bound to "" —
                // and `empty($style)` then returned false, masking the no-match path.
                // Found in Docbook info.xsl personname → name-style lookup, where the empty
                // string blocked the apply-templates m:gentext fallback that would have
                // produced "first-last" from the locale.
                else if (content.Length == 0
                    && (capturedAccumulator is null || capturedAccumulator.Count == 0)
                    && instruction.As.Occurrence is Occurrence.ZeroOrOne or Occurrence.ZeroOrMore)
                    value = null;
                else
                    value = content.Contains('<', StringComparison.Ordinal)
                        ? new ResultTreeFragment(content, varBaseUri)
                        : (object)DefaultXsltExecutionContext.StripXmlMarkup(content);
            }

            // Pop effective base URI
            if (instruction.BaseUri != null)
                _baseUriStack.Pop();
        }
        else
        {
            // XSLT 3.0 §9.3, the select/as/content table:
            //
            //   select   as        content   effect
            //   absent   absent    empty     value is a ZERO-LENGTH STRING
            //   absent   PRESENT   empty     value is an EMPTY SEQUENCE, provided `as` permits one
            //
            // The zero-length-string rule is conditional on there being no `as` attribute —
            // "neither a select attribute nor an as attribute" — and that condition was missing
            // here, so every typed empty binding was bound to "" instead of ().
            //
            //   <xsl:variable name="n" as="empty-sequence()"/>   count() reported 1, not 0
            //   <xsl:variable name="e" as="element(x)*"/>        held a string, not elements
            //
            // XSpec writes <x:param as="empty-sequence()"/> for "pass nothing here", so the
            // string leaked into a function declared as="element(...)*"; the first axis step on
            // it raised XPTY0020 "context item is not a node (got xs:string """. 21 of its
            // suites died that way, all naming the axis step rather than the binding.
            //
            // A type that does NOT permit an empty sequence still errors — that is the "provided"
            // clause — but it is XTTE0570 from the validation below, raised against (), not a
            // string quietly satisfying as="xs:string".
            value = instruction.As != null ? System.Array.Empty<object?>() : (object?)"";
        }

        // When as="text()" and the content produced a string, wrap as XdmText node
        if (instruction.As != null && instruction.As.ItemType == ItemType.Text && value is string textVal && _nodeStore != null)
        {
            var textId = _nodeStore.NextId();
            value = new Xdm.Nodes.XdmText
            {
                Id = textId,
                Document = DocumentId.None,
                Parent = NodeId.None,
                Value = textVal
            };
        }

        // XTTE0570: Validate value against declared type if 'as' attribute is present
        if (instruction.As != null && value is not LazyValue)
        {
            var targetType = instruction.As.ItemType;
            var occurrence = instruction.As.Occurrence;

            // Handle empty-sequence() type: only empty values are allowed
            if (occurrence == Occurrence.Zero)
            {
                var isEmpty = value == null || (value is object?[] emptyArr && emptyArr.All(i => i == null))
                    || (value is string s && s.Length == 0);
                if (!isEmpty)
                    throw Error($"XTTE0570: Variable ${instruction.Name.LocalName} value does not match declared type empty-sequence()");
                goto doneValidation; // skip remaining type checks
            }

            // XTTE0570 cardinality: a declared type requiring at least one item is not satisfied
            // by the empty sequence. This check is deliberately type-independent — "how many
            // items" is decided before "of what type", and the per-type branches below all start
            // by assuming there is something to inspect, so an empty value slipped past every one
            // of them. as="xs:string+" bound to a filter that matched nothing simply succeeded.
            //
            // A zero-length string is NOT the empty sequence and must not be caught here: it is a
            // perfectly good single xs:string item. Only a null, or an array holding nothing but
            // nulls, is empty. (The empty-sequence() branch above does count "" as empty; that is
            // its own pre-existing behaviour and is left alone.)
            if (occurrence is Occurrence.ExactlyOne or Occurrence.OneOrMore
                && IsEmptySequenceValue(value))
            {
                throw Error($"XTTE0570: Variable ${instruction.Name.LocalName} requires "
                    + $"{DescribeRequiredCardinality(occurrence)} of type {targetType}, "
                    + "but the supplied value is an empty sequence");
            }

            var isNodeType = targetType is ItemType.Node or ItemType.Element or ItemType.Text
                or ItemType.Comment or ItemType.Document or ItemType.ProcessingInstruction
                or ItemType.Attribute or ItemType.Item;

            // For node types, unwrap RTFs and flatten nested arrays into actual XDM nodes
            if (isNodeType && value is object?[] nodeItems)
            {
                var flatItems = FlattenAndUnwrapToNodes(nodeItems, targetType, instruction.As.DocumentElementName);
                value = flatItems;

                // Cardinality check
                if (occurrence == Occurrence.ExactlyOne && flatItems.Length != 1)
                    throw Error($"XTTE0570: Variable ${instruction.Name.LocalName} value does not match declared type {instruction.As.ItemType} {instruction.As.Occurrence}"
                        + $" — the body produced {DescribeSequenceForDiagnostics(flatItems)}");
                if (occurrence == Occurrence.ZeroOrOne && flatItems.Length > 1)
                    throw Error($"XTTE0570: Variable ${instruction.Name.LocalName} value does not match declared type {instruction.As.ItemType} {instruction.As.Occurrence}"
                        + $" — the body produced {DescribeSequenceForDiagnostics(flatItems)}");

                // Item type check (if not generic node()/item())
                if (targetType is not ItemType.Node and not ItemType.Item)
                {
                    foreach (var item in flatItems)
                    {
                        if (item == null)
                            continue;
                        var matches = (targetType, item) switch
                        {
                            (ItemType.Element, XdmElement el) =>
                                instruction.As.ElementName == null || el.LocalName == instruction.As.ElementName,
                            (ItemType.Text, XdmText) => true,
                            (ItemType.Text, string) => true,
                            (ItemType.Text, Xdm.TextNodeItem) => true,
                            (ItemType.Comment, XdmComment) => true,
                            (ItemType.ProcessingInstruction, XdmProcessingInstruction) => true,
                            (ItemType.Attribute, XdmAttribute) => true,
                            (ItemType.Document, XdmDocument doc) =>
                                instruction.As.DocumentElementName == null ||
                                doc.DocumentElementLocalName == instruction.As.DocumentElementName,
                            _ => false
                        };
                        if (!matches)
                            throw Error($"XTTE0570: Variable ${instruction.Name.LocalName} value does not match declared type {instruction.As.ItemType} {instruction.As.Occurrence}");
                    }
                }
            }
            // Single node value (not in array) — also needs type check
            else if (isNodeType && value is XdmNode or Xdm.TextNodeItem && targetType is not ItemType.Node and not ItemType.Item)
            {
                var matches = (targetType, value) switch
                {
                    (ItemType.Element, XdmElement el) =>
                        instruction.As.ElementName == null || el.LocalName == instruction.As.ElementName,
                    (ItemType.Text, XdmText) => true,
                    (ItemType.Text, Xdm.TextNodeItem) => true,
                    (ItemType.Comment, XdmComment) => true,
                    (ItemType.ProcessingInstruction, XdmProcessingInstruction) => true,
                    (ItemType.Attribute, XdmAttribute) => true,
                    (ItemType.Document, XdmDocument doc) =>
                        instruction.As.DocumentElementName == null ||
                        doc.DocumentElementLocalName == instruction.As.DocumentElementName,
                    _ => false
                };
                if (!matches)
                    throw Error($"XTTE0570: Variable ${instruction.Name.LocalName} value does not match declared type {instruction.As.ItemType} {instruction.As.Occurrence}");
            }

            if (!isNodeType)
            {
                // RTF values need atomization: extract string content and coerce
                if (value is ResultTreeFragment rtf)
                {
                    // Parse the RTF to extract text content for atomization
                    var rtfText = rtf.XmlContent;
                    if (rtfText.Contains('<', StringComparison.Ordinal))
                    {
                        try
                        {
                            // Stream the RTF content and accumulate descendant text — equivalent
                            // to XmlDocument.DocumentElement.InnerText but without allocating the
                            // DOM. Same hot-path optimization as the rest of the as= body capture.
                            var settings = new System.Xml.XmlReaderSettings
                            {
                                Async = true,
                                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                                IgnoreWhitespace = false,
                                IgnoreComments = true,
                                IgnoreProcessingInstructions = true,
                            };
                            using var stringReader = new System.IO.StringReader($"<_rtf_>{rtfText}</_rtf_>");
                            using var reader = System.Xml.XmlReader.Create(stringReader, settings);
                            var sb = new StringBuilder();
                            while (await reader.ReadAsync().ConfigureAwait(false))
                            {
                                if (reader.NodeType is System.Xml.XmlNodeType.Text
                                    or System.Xml.XmlNodeType.CDATA
                                    or System.Xml.XmlNodeType.SignificantWhitespace
                                    or System.Xml.XmlNodeType.Whitespace)
                                {
                                    sb.Append(reader.Value);
                                }
                            }
                            rtfText = sb.ToString();
                        }
                        catch (System.Xml.XmlException)
                        {
                            // Intentional fallback: if RTF content contains angle brackets but is not
                            // well-formed XML (e.g. text with embedded < from disable-output-escaping),
                            // fall through and use the raw rtfText string for type coercion below.
                        }
                    }
                    if (CanCoerceToItemType(rtfText, targetType))
                    {
                        value = CoerceToType(rtfText, instruction.As);
                    }
                    else
                    {
                        throw Error($"XTTE0570: Variable ${instruction.Name.LocalName} value does not match declared type {instruction.As.ItemType} {instruction.As.Occurrence}");
                    }
                }

                object?[] items = value switch
                {
                    object?[] arr => arr,
                    IDictionary<object, object?> => [value], // Maps are single items, don't enumerate
                    List<object?> => [value], // XDM arrays are single items, don't enumerate
                    System.Collections.IEnumerable enumerable when value is not string
                        => enumerable.Cast<object?>().ToArray(),
                    null => [],
                    _ => [value]
                };
                var hasTypeMismatch = items.Any(item =>
                    item != null && !CanCoerceToItemType(item, targetType));
                if (hasTypeMismatch)
                {
                    throw Error($"XTTE0570: Variable ${instruction.Name.LocalName} value does not match declared type {instruction.As.ItemType} {instruction.As.Occurrence}"
                        + $" — the body produced {DescribeSequenceForDiagnostics(items)}");
                }

                // Coerce values to target type: atomize XDM nodes, apply numeric promotion, etc.
                // Per XSLT spec, when 'as' is declared, values must be coerced to the target type.
                var needsCoercion = items.Any(item => item is XdmNode
                    || (item != null && !XQuery.Execution.TypeCastHelper.MatchesItemType(item, targetType)));
                if (needsCoercion)
                {
                    var coerced = new object?[items.Length];
                    for (int i = 0; i < items.Length; i++)
                    {
                        coerced[i] = items[i] is XdmNode node
                            ? CoerceToType(node.StringValue, instruction.As)
                            : CoerceToType(items[i], instruction.As);
                    }
                    // Unwrap single-element arrays to preserve scalar semantics
                    value = coerced.Length == 1 ? coerced[0] : coerced;
                }
            }
        }

    doneValidation:
        SetVariable(instruction.Name, value);
    }


    public override async ValueTask BindParamAsync(XsltParamInstruction instruction)
    {
        // If already bound (from with-param or tunnel inheritance), skip
        try
        {
            GetVariable(instruction.Name);
            return;
        }
        catch (XsltException)
        {
            // Not bound, continue
        }

        // For tunnel params, check inherited tunnel parameters
        if (instruction.Tunnel && TryGetTunnelParam(instruction.Name, out var tunnelValue))
        {
            SetVariable(instruction.Name, tunnelValue);
            return;
        }

        if (instruction.Required)
        {
            // Reached when a template is entered by apply-templates / next-match /
            // apply-imports without the parameter: XTDE0700. Its siblings in
            // DefaultXsltExecutionContext.Templates carried the code; this one did not.
            throw Error($"XTDE0700: Required parameter ${instruction.Name.LocalName} not supplied");
        }

        object? value;

        if (instruction.Select != null)
        {
            value = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
        }
        else if (instruction.Content != null)
        {
            var isSequenceType = instruction.As != null &&
                (instruction.As.Occurrence == Occurrence.ZeroOrMore || instruction.As.Occurrence == Occurrence.OneOrMore);

            if (isSequenceType)
            {
                var savedAccumulator = _sequenceAccumulator;
                _sequenceAccumulator = new List<object?>();

                var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
                // Shared typed-body context (see EnterTypedBody).
                var typedBody = EnterTypedBody(instruction.As);
                try
                {
                    await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    ExitTypedBody(typedBody);
                }
                var textContent = savedScope.GetWritten();
                savedScope.Dispose();

                var sequenceItems = new List<object?>();
                foreach (var item in _sequenceAccumulator)
                    if (item != null)
                        sequenceItems.Add(item);

                if (!string.IsNullOrEmpty(textContent) && _nodeStore != null && textContent.Contains('<', StringComparison.Ordinal))
                {
                    try
                    {
                        // Stream-parse the wrapper rather than allocating a full XmlDocument.
                        var settings = new System.Xml.XmlReaderSettings
                        {
                            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                            IgnoreWhitespace = false,
                            IgnoreComments = false,
                            IgnoreProcessingInstructions = false,
                        };
                        using var stringReader = new System.IO.StringReader($"<_seq_root_>{textContent}</_seq_root_>");
                        using var reader = System.Xml.XmlReader.Create(stringReader, settings);
                        var parsedChildren = new List<object?>();
                        ReadAsBodyChunkChildren(reader, parsedChildren);
                        foreach (var child in parsedChildren)
                        {
                            if (child is XdmNode cn)
                                cn.Parent = null;
                            sequenceItems.Add(child);
                        }
                    }
                    catch (System.Xml.XmlException)
                    {
                        sequenceItems.Add(new ResultTreeFragment(textContent));
                    }
                }
                else if (!string.IsNullOrEmpty(textContent))
                {
                    sequenceItems.Add(new Xdm.XsUntypedAtomic(StripXmlMarkup(textContent)));
                }

                value = sequenceItems.Count > 0 ? sequenceItems.ToArray() : Array.Empty<object?>();
                _sequenceAccumulator = savedAccumulator;
            }
            else
            {
                var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
                var savedLogicalStart = _outputLogicalStart;
                _outputLogicalStart = _output.Length;
                var savedAtomic2 = _lastResultWasAtomic;
                _lastResultWasAtomic = false;
                await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                var content = savedScope.GetWritten();
                savedScope.Dispose();
                _outputLogicalStart = savedLogicalStart;
                _lastResultWasAtomic = savedAtomic2;
                value = content.Contains('<', StringComparison.Ordinal) ? new ResultTreeFragment(content) : (object)new Xdm.XsUntypedAtomic(StripXmlMarkup(content));
            }
        }
        else
        {
            // Per XSLT spec: param with no select and no content defaults to empty string,
            // but if the type allows empty sequence (? or *), default to null (empty sequence)
            if (instruction.As != null && (instruction.As.Occurrence == Occurrence.ZeroOrOne
                || instruction.As.Occurrence == Occurrence.ZeroOrMore))
                value = null;
            else
                value = "";
        }

        SetVariable(instruction.Name, value);
    }

}
