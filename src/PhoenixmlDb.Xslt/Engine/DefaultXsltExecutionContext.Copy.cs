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

    public override ValueTask CopyAsync(XsltCopy instruction)
        => RunInstructionWithValidationAsync(
            instruction.Validation, "xsl:copy", instruction.Location,
            () => CopyCoreAsync(instruction));


    private async ValueTask CopyCoreAsync(XsltCopy instruction)
    {
        _currentInstructionLocation = instruction.Location ?? _currentInstructionLocation;
        if (instruction.Select != null)
        {
            // XSLT 3.0 §5.6: xsl:copy/@select must select at most one item.
            // In streaming context, the select expression may evaluate against the
            // synthetic empty document (returning zero items even though the streamed
            // input would yield many), masking the cardinality violation. Detect this
            // statically and raise XTTE3180 regardless of runtime result. Note: this
            // check uses _activeStreamingProcessor (rather than _isStreamingExecution)
            // because the streaming-execution flag is only set inside the processor
            // pass; with bare xsl:copy bodies (no apply-templates/watchers) the
            // processor never runs but we still want to enforce the cardinality rule.
            if (_activeStreamingProcessor != null && SelectMayReturnMultipleItems(instruction.Select))
                throw Error("XTTE3180: xsl:copy with select attribute must select at most one item");

            // XSLT 3.0: xsl:copy with select — evaluate and copy each item
            var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
            var items = result is object?[] arr ? arr : new[] { result };
            // Count non-null items; XTTE3180 if more than one
            var nonNullItems = new List<object>();
            foreach (var item in items)
            {
                if (item != null)
                    nonNullItems.Add(item);
            }
            if (nonNullItems.Count > 1)
                throw Error("XTTE3180: xsl:copy with select attribute must select at most one item");
            foreach (var item in nonNullItems)
            {
                PushContextItem(item, 1, 1);
                try
                {
                    await CopySingleItemAsync(item, instruction).ConfigureAwait(false);
                }
                finally
                {
                    PopContextItem();
                }
            }
            return;
        }

        var node = ContextItem;
        if (node == null || ReferenceEquals(node, PhoenixmlDb.XQuery.Execution.QueryExecutionContext.AbsentFocus))
            throw Error("XTTE0945: xsl:copy has no context item");
        await CopySingleItemAsync(node, instruction).ConfigureAwait(false);
    }


    private async ValueTask CopySingleItemAsync(object? node, XsltCopy instruction)
    {
        switch (node)
        {
            case XdmDocument srcCopyDoc:
                // Copy of document node: create a new document node (XSLT 3.0 §11.9.1).
                // The copy preserves the SOURCE document's base URI (dm:base-uri): stamp it
                // on the new doc node below so base-uri() of the copy — and of any content
                // placed inside it, which inherits the doc node's base — reports the source
                // document URI, not the construction (stylesheet) base the sequence-materialize
                // path would otherwise apply to an unstamped orphan doc node. (fn/base-uri 053:
                // shallow-doc / shallow-doc-deeper.)
                // Content may be absent: <xsl:copy/> on a document node is a legal shallow copy
                // producing an EMPTY document node. Requiring Content here (and in the fallback
                // below) meant an empty copy produced nothing at all, so a typed template
                // returned zero items — the same XTTE0505 Martin Honnen hit on attributes,
                // reached by a different route.
                if (_sequenceAccumulator != null && _nodeStore != null)
                {
                    // Create a proper XDM document node in sequence accumulator mode
                    var savedOutput2 = _output.ToString();
                    _output.Clear();

                    var savedAccum = _sequenceAccumulator;
                    _sequenceAccumulator = null;

                    var savedAttrStack2 = new List<StringBuilder>(_collectedAttributesStack);
                    _collectedAttributesStack.Clear();
                    _documentNodeDepth++;
                    try
                    {
                        if (instruction.Content != null)
                            await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                    }
                    finally
                    {
                        _documentNodeDepth--;
                        _sequenceAccumulator = savedAccum;
                        _collectedAttributesStack.Clear();
                        // Restore BOTTOM-first — see the note at the ApplyTemplates seam.
                        for (var i = savedAttrStack2.Count - 1; i >= 0; i--)
                            _collectedAttributesStack.Push(savedAttrStack2[i]);
                    }

                    var content = _output.ToString();
                    _output.Clear();
                    _output.Append(savedOutput2);

                    if (content.Length > 0)
                    {
                        try
                        {
                            // Stream-parse rather than allocating an XmlDocument and
                            // re-converting. Same hot-path optimization as elsewhere.
                            var settings2 = new System.Xml.XmlReaderSettings
                            {
                                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                                IgnoreWhitespace = false,
                                IgnoreComments = false,
                                IgnoreProcessingInstructions = false,
                            };
                            using var stringReader2 = new System.IO.StringReader($"<_seq_root_>{content}</_seq_root_>");
                            using var reader2 = System.Xml.XmlReader.Create(stringReader2, settings2);
                            var docId2 = _nodeStore.NextId();
                            var children2 = new List<NodeId>();
                            NodeId docElemId2 = NodeId.None;
                            var parsedChildren2 = new List<object?>();
                            ReadAsBodyChunkChildren(reader2, parsedChildren2);
                            foreach (var item in parsedChildren2)
                            {
                                if (item is XdmNode cn)
                                {
                                    cn.Parent = docId2;
                                    children2.Add(cn.Id);
                                    if (cn is XdmElement && docElemId2 == NodeId.None)
                                        docElemId2 = cn.Id;
                                }
                            }
                            string? docElemLocalName2 = docElemId2 != NodeId.None
                                ? (_nodeStore.GetNode(docElemId2) as XdmElement)?.LocalName : null;
                            var docNode2 = new XdmDocument
                            {
                                StringValueResolver = _nodeStore.StringValueResolver,
                                Id = docId2,
                                Document = new DocumentId(1),
                                Parent = NodeId.None,
                                DocumentElement = docElemId2,
                                Children = children2,
                                DocumentElementLocalName = docElemLocalName2,
                                BaseUri = srcCopyDoc.BaseUri,
                                CopySourceBaseUri = srcCopyDoc.BaseUri == null ? DocCopyNullSourceBaseSentinel : null,
                            };
                            _nodeStore.Register(docNode2);
                            AppendToSeqAccumulator(docNode2);
                        }
                        catch (System.Xml.XmlException)
                        {
                            _sink.RawText(content);
                        }
                    }
                    else
                    {
                        var docId2 = _nodeStore.NextId();
                        var docNode2 = new XdmDocument
                        {
                            StringValueResolver = _nodeStore.StringValueResolver,
                            Id = docId2,
                            Document = new DocumentId(1),
                            Parent = NodeId.None,
                            DocumentElement = NodeId.None,
                            Children = new List<NodeId>(),
                            BaseUri = srcCopyDoc.BaseUri,
                            CopySourceBaseUri = srcCopyDoc.BaseUri == null ? DocCopyNullSourceBaseSentinel : null,
                        };
                        _nodeStore.Register(docNode2);
                        AppendToSeqAccumulator(docNode2);
                    }
                    MarkContentProduced();
                }
                else if (instruction.Content != null)
                {
                    // Fallback: just process content inline
                    var savedAttrStack = new List<StringBuilder>(_collectedAttributesStack);
                    _collectedAttributesStack.Clear();
                    _documentNodeDepth++;
                    try
                    {
                        await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                    }
                    finally
                    {
                        _documentNodeDepth--;
                        _collectedAttributesStack.Clear();
                        // Restore BOTTOM-first — see the note at the ApplyTemplates seam.
                        for (var i = savedAttrStack.Count - 1; i >= 0; i--)
                            _collectedAttributesStack.Push(savedAttrStack[i]);
                    }
                }
                break;

            case XdmElement elem:
            {
                // Build namespace bindings for this element
                var copyNsBindings = new Dictionary<string, string>();
                var emittedPrefixes = new HashSet<string>();
                if (instruction.CopyNamespaces ?? true)
                {
                    // XSLT 3.0 §11.9.1: the copy gets a namespace node for EVERY namespace node
                    // of the original — that is the element's complete IN-SCOPE set, not merely
                    // the xmlns declarations physically written on it.
                    //
                    // Emitting only the local declarations is usually indistinguishable, because
                    // the copy lands inside a result tree whose ancestors re-supply the rest.
                    // It is NOT indistinguishable under inherit-namespaces="no": that ancestor
                    // contributes nothing, so every inherited binding simply vanishes and the
                    // copy keeps only the prefix of its own name. A later resolve-QName against
                    // that copy then fails with FONS0004 for a prefix the source plainly had.
                    //
                    // XSpec hits this squarely — x:combine wraps the combined document in
                    // <xsl:element inherit-namespaces="no"> (deliberately, with a comment saying
                    // why) and the compiler then copies scenarios out of that tree and resolves
                    // @function/@template/@as against the copies. It accounted for FONS0004 in
                    // 60 of 162 suites, every one of them failing before it could run a test.
                    // Gating this on the _inheritNamespacesNo FLAG does not work: that flag
                    // describes the construction context in force right now, whereas the loss is
                    // a property of the SOURCE element's tree, and the copy typically happens in
                    // a later, unrelated construction. GatherSourceInScopeBindings is the same
                    // helper the untyped-RTF branch below already relies on for this reason.
                    var sourceBindings = GatherSourceInScopeBindings(elem);
                    foreach (var nsDecl in sourceBindings)
                    {
                        var uri = _nodeStore?.GetNamespaceUri(nsDecl.Namespace) ?? "";
                        var prefix = nsDecl.Prefix ?? "";
                        emittedPrefixes.Add(prefix);
                        copyNsBindings[prefix] = uri;
                    }
                }
                // Per XSLT 3.0 §11.10.1 (xsl:copy) + §5.7.3.4 (Namespace Fixup): even with
                // copy-namespaces="no" the element's *own* namespace must be preserved —
                // the spec only lets us drop the source's *additional* namespace bindings,
                // not the binding that defines the copy's name. Without this, an element
                // copied with copy-namespaces="no" loses its namespace and downstream
                // path-step matchers (`/h:html`) can no longer find it. Found in Docbook
                // mp:remove-ghosts (`<xsl:copy copy-namespaces="no">…`) which dropped
                // xhtml-namespaced elements into the null namespace, breaking the entire
                // chunk-output dispatch.
                {
                    var prefix = elem.Prefix ?? "";
                    var elemNsUri = _nodeStore?.GetNamespaceUri(elem.Namespace) ?? "";
                    if (!string.IsNullOrEmpty(elemNsUri) && !emittedPrefixes.Contains(prefix))
                        copyNsBindings[prefix] = elemNsUri;

                    // The symmetric case, and it needs an OVERRIDE rather than a fill-in.
                    //
                    // An unprefixed element in no namespace can only exist where the default
                    // namespace is undeclared, so its in-scope default IS none. But
                    // GatherSourceInScopeBindings walks up and returns the nearest ancestor
                    // xmlns="…" regardless, ignoring the xmlns="" that undeclared it — so
                    // copyNsBindings already holds ""→ancestor-uri here, and copying wrote that
                    // declaration onto the element, CHANGING its name:
                    //
                    //   <outer xmlns="urn:o"><undeclared xmlns=""/></outer>
                    //   xsl:copy of `undeclared`  ->  namespace-uri() became "urn:o"
                    //
                    // xsl:copy-of was unaffected (it copies the subtree wholesale), so only the
                    // identity-transform shape showed it — which is any stylesheet that runs a
                    // document containing xmlns="" through xsl:copy.
                    if (string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(elemNsUri))
                    {
                        // Only when a default namespace would otherwise apply to the copy —
                        // either inherited into copyNsBindings by the gather above, or already
                        // in scope in the output. Setting it unconditionally wrote a redundant
                        // xmlns="" onto every no-namespace element in namespace-free documents,
                        // turning <doc> into <doc xmlns=""> (caught by the streaming
                        // iterate/copy wrapper tests).
                        var inheritedDefault = copyNsBindings.TryGetValue("", out var d) ? d : null;
                        if (!string.IsNullOrEmpty(inheritedDefault) || GetInScopeDefaultNamespace() != null)
                            copyNsBindings[""] = "";
                    }
                }

                // Use attribute collection mode like CreateElementAsync (push for nesting)
                _collectedAttributesStack.Push(new StringBuilder());
                // Track whether the copied element has a namespace (for XTDE0440)
                _outputElementHasNsStack.Push(!string.IsNullOrEmpty(elem.Prefix) || elem.Namespace != NamespaceId.None);
                _outputElementIsLreStack.Push(false); // xsl:copy — not an LRE
                _xslNamespaceBindings.Push(new Dictionary<string, string>());

                var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
                var savedLogicalStart = _outputLogicalStart;
                _outputLogicalStart = _output.Length;

                // Process use-attribute-sets first (they create attributes)
                var attrSetParts = new StringBuilder();
                await ApplyAttributeSetsAsync(instruction.UseAttributeSets, attrSetParts).ConfigureAwait(false);
                _collectedAttributes!.Append(attrSetParts);

                // SP-C copy-0612 family: a COPIED element under an inherit-namespaces="no" ancestor
                // (xsl:copy identity chain: match="*" → <xsl:copy inherit-namespaces="no">) carries
                // its OWN namespace nodes (XSLT 3.0 §11.9.1) — the COMPLETE in-scope set of the
                // source element — regardless of the ancestor's inherit-namespaces="no". Under the
                // untyped-RTF flip we materialise those as the copied element's LOCAL declarations
                // (self-contained, the same over-declared shape the serialize-reparse produces) for
                // two reasons: (1) fn:namespace-uri-for-prefix reads only local declarations (no
                // ancestor walk), so a minimal-decl copy relying on the walk would report an empty
                // default; (2) it prevents the inherit-namespaces="no" default-undeclaration logic
                // below from wrongly stripping a default the copy legitimately carries. This is a
                // known-divergent shape (the reparse cannot undeclare prefixed namespaces on the
                // grafted content — XML 1.0), so it also marks the body divergent.
                List<(string Prefix, NamespaceId Ns)>? divergentCopyFullDecls = null;
                if (_untypedRtfFlipActive && _inheritNamespacesNo && _activeTreeConstructor != null && _nodeStore != null)
                {
                    var copyNamespaces = instruction.CopyNamespaces ?? true;
                    divergentCopyFullDecls = new List<(string, NamespaceId)>();
                    foreach (var nb in GatherSourceInScopeBindings(elem))
                    {
                        var p = nb.Prefix ?? "";
                        if (!copyNamespaces)
                        {
                            // copy-namespaces="no": keep only namespaces used by the element name
                            // or an attribute name (mirrors CloneSubtreeDeep's filter).
                            var usedByElem = p == (elem.Prefix ?? "");
                            var usedByAttr = false;
                            if (!string.IsNullOrEmpty(p))
                                foreach (var a in _nodeStore.GetAttributes(elem))
                                    if (a.Prefix == p) { usedByAttr = true; break; }
                            if (!usedByElem && !usedByAttr)
                                continue;
                        }
                        divergentCopyFullDecls.Add((p, nb.Namespace));
                    }
                    _untypedRtfFlipDivergent = true;
                }
                // inherit-namespaces="no" on parent: add default ns undeclaration (for CONSTRUCTED
                // content only — a self-contained copy above carries its own default, so skip it).
                if (_forceDefaultNsUndeclaration && !copyNsBindings.ContainsKey("")
                    && GetInScopeDefaultNamespace() != null
                    && divergentCopyFullDecls == null)
                {
                    copyNsBindings[""] = "";
                }

                // Process content with ns scope so children can deduplicate
                PushOutputNsScope(copyNsBindings);
                PushOutputNsScope(new Dictionary<string, string>()); // Separate scope for attribute-emitted ns decls
                PushScope(); // Variables declared inside this element should be scoped to this element
                var savedSeqAccum2 = _sequenceAccumulator;
                _sequenceAccumulator = null; // Suspend accumulation inside element content
                _serializingElementDepth++;
                // Reset atomic spacing — element content is a new serialization context
                var savedLastResultWasAtomic2 = _lastResultWasAtomic;
                _lastResultWasAtomic = false;
                // Save/set inherit-namespaces flag for children
                var savedForceNsUndecl2 = _forceDefaultNsUndeclaration;
                _forceDefaultNsUndeclaration = instruction.InheritNamespaces == false
                    && copyNsBindings.ContainsKey("") && !string.IsNullOrEmpty(copyNsBindings.GetValueOrDefault(""));
                var savedInheritNsNo2 = _inheritNamespacesNo;
                _inheritNamespacesNo = instruction.InheritNamespaces == false;
                // SP-B slice 4: open THIS copied element on the active constructor BEFORE content
                // (constructor stays active — no suspend), so child text/comment/PI/nested
                // elements build natively in document order. The source element's name/namespace
                // are known up front (copy preserves them); sealed after content in TcFinishElement.
                var copyElemName = !string.IsNullOrEmpty(elem.Prefix)
                    ? $"{elem.Prefix}:{elem.LocalName}"
                    : elem.LocalName;
                var copyElemNsUri = _nodeStore?.GetNamespaceUri(elem.Namespace) ?? "";
                var tcForThisElement = _activeTreeConstructor;
                if (tcForThisElement != null)
                    TcOpenElement(tcForThisElement, copyElemName, copyElemNsUri);
                try
                {
                    if (instruction.Content != null)
                    {
                        await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _forceDefaultNsUndeclaration = savedForceNsUndecl2;
                    _inheritNamespacesNo = savedInheritNsNo2;
                    _lastResultWasAtomic = savedLastResultWasAtomic2;
                    _serializingElementDepth--;
                    _sequenceAccumulator = savedSeqAccum2;
                    PopScope();
                    PopOutputNsScope(); // attribute ns decls scope
                    PopOutputNsScope(); // element ns scope
                    _outputElementHasNsStack.Pop();
                    _outputElementIsLreStack.Pop();
                    _xslNamespaceBindings.Pop();
                }

                var collectedAttrs = _collectedAttributesStack.Pop();
                var content = savedScope.GetWritten();
                savedScope.Dispose();
                _outputLogicalStart = savedLogicalStart;

                // Build output element name (with prefix if present).
                // Treat empty-string prefix the same as null — an empty prefix means
                // "no prefix", not "use the colon with no name". Without this guard,
                // serialization produced ":body" for the body element when xsl:copy
                // hit an element with an empty Prefix string. Same fix shape as the
                // shallow-copy path in ApplyBuiltInShallowCopyAsync.
                var elemName = !string.IsNullOrEmpty(elem.Prefix)
                    ? $"{elem.Prefix}:{elem.LocalName}"
                    : elem.LocalName;

                // SP-B: when a tree constructor is active (differential), mirror the exact
                // namespace/attribute decisions the string path makes below into typed lists,
                // then build a byte-identical XDM element via the constructor. `tcAbort` is set
                // if some decision can't be faithfully represented; the node build is then
                // skipped (the differential seam treats that as "not captured", no divergence).
                List<(string Prefix, NamespaceId Ns)>? tcNsDecls = tcForThisElement != null ? new() : null;
                List<(NamespaceId Ns, string Local, string? Prefix, string Value)>? tcAttrs = tcForThisElement != null ? new() : null;
                var tcAbort = false;

                var elemStartPos = _output.Length;
                _sink.StartElementOpen(elemName);

                // Namespace declarations, skipping already-in-scope
                foreach (var (prefix, uri) in copyNsBindings)
                {
                    if (IsNamespaceInScope(prefix, uri))
                        continue;
                    _sink.Namespace(prefix, uri);
                    // xmlns="" (empty URI) is a real undeclaration in the node model.
                    tcNsDecls?.Add((prefix, string.IsNullOrEmpty(uri) ? NamespaceId.None : _nodeStore!.InternNamespace(uri)));
                }

                // SP-C copy-0612 family: for a self-contained divergent copy, the NODE must carry
                // the copied element's COMPLETE in-scope namespace set as local declarations (see
                // the divergentCopyFullDecls note above) — not just the ones not already in the
                // constructor's scope. The serialized content string keeps the minimal set emitted
                // above (it is not delivered — the node build is authoritative and the differential
                // is skipped), but the tree constructor's declarations are replaced with the full
                // set so fn:namespace-uri-for-prefix (local-only) and fn:in-scope-prefixes agree.
                if (divergentCopyFullDecls != null && tcNsDecls != null)
                {
                    tcNsDecls.Clear();
                    tcNsDecls.AddRange(divergentCopyFullDecls);
                }

                // SP-B: under the differential the copy stays in textContent (the base-uri
                // reparse below is skipped for the node-build path). Mirror built-in
                // shallow-copy and stamp the source base URI as a sentinel so the combine-phase
                // reparse AND the differential's own reparse both recover it onto
                // CopySourceBaseUri — matching the node the constructor builds. Save/restore the
                // serialize context so each sibling copy emits independently. In production
                // (no active constructor) nothing is emitted; the legacy reparse recovers it.
                if (tcForThisElement != null)
                {
                    var savedCopyBaseCtx = _serializeBaseContext;
                    // Under the untyped-RTF flip the temp-tree serialize depth is not raised (a
                    // blanket raise regresses fn/base-uri), so force the sentinel here — only at the
                    // xsl:copy site, only under the flip. The shallow xsl:copy drops the source
                    // element's attributes, so its serialized form is <e> with no xml:base; the
                    // content-reparse fallback (used when the flip is byte-parity-vetoed) then has to
                    // recompute base-uri() structurally. Two sub-cases, both seeded with the flip
                    // variable's enclosing base so TryEmitBaseSentinel's srcBase-vs-context guard
                    // only emits when the source base actually differs:
                    //   • NO enclosing base ("" — no stylesheet/variable xml:base): force always. A
                    //     base-URI-bearing source element (own xml:base OR source-doc entity base) is
                    //     the identity-copy-into-variable case; the reparse would give (), so emit the
                    //     source base (§11.9.1).
                    //   • enclosing base PRESENT: force ONLY when the copied element carries its OWN
                    //     xml:base. That attribute — which the shallow copy discarded — is what makes
                    //     the copy's base differ from the enclosing parent base, and the reparse can no
                    //     longer see it, so the sentinel must carry the resolved base (the vetoed-flip
                    //     corner: a source element with its own xml:base copied into a variable that has
                    //     a stylesheet/variable base). A copy WITHOUT its own xml:base correctly INHERITS
                    //     the enclosing parent base on reparse (base-uri-025/029/030/033/035/038/040 —
                    //     substring1 nested under a constructed <e1>); forcing there would wrongly
                    //     override it with the source-ancestor base, so leave it to the reparse.
                    if (_untypedRtfFlipActive
                        && (string.IsNullOrEmpty(_untypedFlipBaseContext) || ElementHasOwnXmlBase(elem)))
                    {
                        _serializeBaseContext = _untypedFlipBaseContext;
                        TryEmitBaseSentinel(elem, ref _serializeBaseContext, force: true);
                    }
                    else
                    {
                        TryEmitBaseSentinel(elem, ref _serializeBaseContext);
                    }
                    _serializeBaseContext = savedCopyBaseCtx;
                }

                // Deduplicate attributes (last-wins) like CreateElementCoreAsync
                var attrs = new Dictionary<string, string>();
                ParseAttributeString(collectedAttrs.ToString(), attrs);
                foreach (var (attrName, attrValue) in attrs)
                {
                    _output.Append(' ');
                    _output.Append(attrName);
                    _output.Append("=\"");
                    _output.Append(attrValue);
                    _output.Append('"');
                    if (tcAttrs != null)
                        RecordTreeAttribute(attrName, attrValue, copyNsBindings, tcNsDecls!, tcAttrs, ref tcAbort);
                }
                if (_isStreamingExecution && _activeStreamingReader != null)
                {
                    // Streaming mode: leave the element open and push onto the
                    // deferred-close stack so StreamingXmlProcessor closes it when
                    // the source element's EndElement event fires. Mirrors the
                    // built-in shallow-copy path. Without this, xsl:copy would emit
                    // <body>buffered</body> immediately and the processor's
                    // subsequent child events (h1, p, …) would leak out as siblings
                    // of body. Reported by Martin Honnen against 1.3.10
                    // (group-starting-with / group-adjacent in streamable mode).
                    _sink.StartElementClose(false);
                    _sink.RawText(content);
                    _streamingOpenElements.Push(elemName);
                }
                else if (content.Length > 0)
                {
                    _sink.StartElementClose(false);
                    _sink.RawText(content);
                    _sink.EndElement(elemName);
                }
                else
                {
                    _sink.StartElementClose(true);
                }

                // SP-B slice 4: seal the copied element opened before content; children built
                // natively in document order, and the source element's base URI carried onto
                // CopySourceBaseUri without a serialize-then-reparse. In production (no active
                // constructor) the legacy reparse below recovers the base URI as before.
                if (tcForThisElement is { } tc)
                {
                    // Reproduce the base sentinel's value EXACTLY (TryEmitBaseSentinel: an
                    // already-copied source element carries CopySourceBaseUri that
                    // ComputeSourceBaseUri does not consult). Otherwise the flip path would
                    // deliver a different base-uri() than the legacy reparse for a copy of a copy.
                    var sourceBaseUri = elem.CopySourceBaseUri ?? ComputeSourceBaseUri(elem);
                    TcFinishElement(tc, elemName, tcNsDecls!, tcAttrs!, tcAbort, sourceBaseUri);
                }
                // XSLT 3.0 §11.9.1: xsl:copy preserves the source element's base URI.
                // When inside a variable/param (sequence accumulator active), extract the
                // serialized element, reparse it, and set CopySourceBaseUri so orphaned
                // copies return the correct base-uri() while attached copies inherit parent.
                else if (_sequenceAccumulator != null && _nodeStore != null)
                {
                    var sourceBaseUri = ComputeSourceBaseUri(elem);
                    if (sourceBaseUri != null)
                    {
                        var elemXml = _output.ToString(elemStartPos, _output.Length - elemStartPos);
                        try
                        {
                            _output.Length = elemStartPos;
                            // Stream-parse rather than allocating an XmlDocument and
                            // re-converting. Wrap in a synthetic root so we can reuse
                            // ReadAsBodyChunkChildren and pick up the single element.
                            var settingsCopy = new System.Xml.XmlReaderSettings
                            {
                                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                                IgnoreWhitespace = false,
                                IgnoreComments = false,
                                IgnoreProcessingInstructions = false,
                            };
                            // The serialized element only carries the namespace declarations it
                            // actually needed at its position in the output — IsNamespaceInScope
                            // above suppresses any the ancestors already declare. Reparsed bare,
                            // such a fragment has an UNDECLARED PREFIX and throws, which used to
                            // silently drop the element (the truncation above had already run).
                            // Declare the in-scope set on the synthetic wrapper so the fragment
                            // stands alone. This is exactly what BuildInScopeNamespaceDeclarations
                            // exists for. Reported by Martin Honnen against XSpec gather-specs.xsl:
                            // a nested x:scenario copied inside its parent's xsl:copy vanished, and
                            // the empty result then raised a spurious XTTE0505.
                            using var stringReaderCopy = new System.IO.StringReader(
                                $"<_copy_root_{BuildInScopeNamespaceDeclarations()}>{elemXml}</_copy_root_>");
                            using var readerCopy = System.Xml.XmlReader.Create(stringReaderCopy, settingsCopy);
                            var copyChildren = new List<object?>();
                            ReadAsBodyChunkChildren(readerCopy, copyChildren);
                            foreach (var child in copyChildren)
                            {
                                if (child is XdmElement copyElem)
                                {
                                    copyElem.Parent = null;
                                    copyElem.CopySourceBaseUri = sourceBaseUri;
                                    AppendToSeqAccumulator(copyElem);
                                    break;
                                }
                            }
                        }
                        catch (System.Xml.XmlException)
                        {
                            // Fallback: leave the element serialized in _output. The truncation
                            // above has already run, so put the markup back — otherwise an
                            // unparseable fragment is destroyed rather than degraded to the
                            // string path, which is how the XSpec loss above went unnoticed.
                            _output.Length = elemStartPos;
                            _output.Append(elemXml);
                        }
                    }
                }
                break;
            }

            case XdmText text:
                WriteText(text.Value, false);
                // §5.7.2: a text node breaks any adjacent-atomic-value run, so a
                // following atomic value must NOT be prefixed with a separator
                // (text→atomic merges with no space: cy-007 "…16.47101 102").
                _lastResultWasAtomic = false;
                break;

            case XdmAttribute attr:
            {
                // When sequence accumulator is active, a copied attribute is an item of the
                // sequence being built — unless the body itself opened the element that is
                // collecting attributes, in which case it belongs to that element.
                //
                // The `!_attributeCollecting` test alone got this wrong whenever the CALLER was
                // mid-element. Martin Honnen's XSpec repro:
                //
                //   <xsl:template as="node()" name="local:identity">
                //     <xsl:copy><xsl:apply-templates mode="#current" select="attribute()|node()"/></xsl:copy>
                //   </xsl:template>
                //
                // applies templates to @status while the enclosing xsl:copy of <phrase> is still
                // collecting ITS attributes, so the attribute was silently added to <phrase> and
                // the typed template returned nothing — XTTE0505 "expected exactly one item,
                // got 0", pointing at the template rather than at the diverted attribute.
                if (_sequenceAccumulator != null
                    && (!_attributeCollecting || IsAttributeScopeOutsideAsBody()))
                {
                    AppendToSeqAccumulator(CopyNodeForAccumulator(attr));
                    break;
                }
                // XTDE0420: Cannot add attribute to a document node
                if (_documentNodeDepth > 0 && !_attributeCollecting)
                {
                    DiagXtde0420("CopySingleItem/XdmAttribute", $"{attr.NodeName}={attr.Value}");
                    throw new XsltException("XTDE0420: Cannot add an attribute node to a document node", _currentInstructionLocation);
                }
                // XTDE0410: Cannot add attribute after child content has been added
                // Inside xsl:where-populated, defer this check until after filtering
                if (_attributeCollecting && _output.Length > _outputLogicalStart && !IsBackwardsCompatible && _wherePopulatedDepth == 0)
                    throw new XsltException("XTDE0410: Cannot add an attribute to an element after children have been added", _currentInstructionLocation);

                // When in attribute collection mode, add as attribute
                var target = _attributeCollecting ? _collectedAttributes! : _output;
                var copyPrefix = attr.Prefix;

                // Namespace fixup: emit namespace declaration for prefixed attributes
                if (!string.IsNullOrEmpty(copyPrefix) && copyPrefix != "xml" && _sequenceAccumulator == null)
                {
                    var copyNsUri = _nodeStore?.GetNamespaceUri(attr.Namespace) ?? "";
                    if (!string.IsNullOrEmpty(copyNsUri))
                    {
                        if (!IsNamespaceInScope(copyPrefix, copyNsUri))
                        {
                            if (IsPrefixInUse(copyPrefix))
                                copyPrefix = GenerateUniquePrefix(copyPrefix);
                            target.Append(" xmlns:");
                            target.Append(copyPrefix);
                            target.Append("=\"");
                            target.Append(EscapeAttributeValue(copyNsUri));
                            target.Append('"');
                            if (_outputNsScopes.Count > 0)
                                _outputNsScopes.Peek()[copyPrefix] = copyNsUri;
                        }
                    }
                }

                var attrName = !string.IsNullOrEmpty(copyPrefix) ? $"{copyPrefix}:{attr.LocalName}" : attr.LocalName;
                if (target == _output)
                {
                    _sink.Attribute(attrName, attr.Value);
                }
                else
                {
                    target.Append(' ');
                    target.Append(attrName);
                    target.Append("=\"");
                    target.Append(EscapeAttributeValue(attr.Value));
                    target.Append('"');
                }
                break;
            }

            case XdmComment comment:
                _sink.Comment(comment.Value);
                break;

            case XdmProcessingInstruction pi:
                _sink.ProcessingInstruction(pi.Target, pi.Value);
                break;

            default:
                // Atomic values: output as text. §5.7.2 sequence normalization
                // inserts a single space between ADJACENT atomic values in the
                // result sequence (e.g. a striding for-each of xsl:copy over atomic
                // values: cy-001 "-15.00 -5.00 -2.33 -248.05"). Mirrors the copy-of
                // atomic path; shared _lastResultWasAtomic state carries the run
                // across for-each iterations. Suppressed in attribute content.
                if (node != null)
                {
                    if (_lastResultWasAtomic && _attributeContentDepth == 0)
                        WriteText(" ", false);
                    WriteText(StringValueOf(node), false);
                    _lastResultWasAtomic = true;
                }
                break;
        }
    }


    public override ValueTask CopyOfAsync(XsltCopyOf instruction)
        => RunInstructionWithValidationAsync(
            instruction.Validation, "xsl:copy-of", instruction.Location,
            () => CopyOfCoreAsync(instruction));


    private async ValueTask CopyOfCoreAsync(XsltCopyOf instruction)
    {
        _currentInstructionLocation = instruction.Location ?? _currentInstructionLocation;

        // SM-ctx streaming handoff (OP phase 2): a consuming simple-map LEFT ! RIGHT
        // select whose RIGHT (a set/sequence operator combining the per-item streamed
        // nodes with grounded nodes) was registered as an inline-driven subscription
        // streams in place. Each matched item is materialized and RIGHT evaluated
        // in-memory; node results are emitted with copy-of semantics (deep XML
        // serialization) by EmitSimpleMapContextResultAsync. Mirrors ValueOfAsync /
        // SequenceAsync.
        if (await TryHandoffSimpleMapContextStreamingAsync(instruction.Select).ConfigureAwait(false))
            return;

        // Streaming whole-subtree copy (si-lre-011 / si-copy-011 / si-copy-of-011): inside
        // a streamable xsl:source-document whose body has no apply-templates, the live
        // reader is still positioned at the document start when a lexical xsl:copy-of runs.
        // Its select (child::node() / child::* / "." ) evaluates against the CLOSED
        // synthetic document node and yields empty. Instead, forward the live reader's
        // subtree events straight into _output at this lexical position. select="." copies
        // the document node, whose serialization is exactly its children — identical to
        // child::node(). Handles only the document-level fresh-reader case (context =
        // streamed document node, reader unpositioned); every other shape falls through to
        // the normal evaluate-and-serialize path below.
        if (_activeStreamingReader != null && _nodeStore != null
            && ContextItem is XdmDocument
            && (IsConsumingChildSelect(instruction.Select) || IsSelfContextSelect(instruction.Select))
            && await TryStreamingCopyOfDocumentChildrenAsync(instruction.CopyNamespaces ?? true).ConfigureAwait(false))
            return;

        var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
        var copyNs = instruction.CopyNamespaces ?? true;

        // When inside attribute/comment/PI content, atomize nodes to their string values
        if (_textContentDepth > 0)
        {
            // When collecting simple content items (§5.7.2), route through accumulator
            // so copy-of results interleave correctly with other sequence items.
            // Use plain string (not TextNodeItem) — copy-of produces atomic values that
            // get space separators between items, NOT text nodes that merge with adjacent text.
            if (_collectTextAsSequenceItems && _sequenceAccumulator != null && _serializingElementDepth == 0)
            {
                AppendToSeqAccumulator(StringValueOf(result));
                return;
            }
            // In attribute context: items concatenated without separator (XSLT 2.0 spec 5.7.2)
            // In comment/PI context: items separated by single space
            if (_attributeContentDepth > 0 && (result is object?[] || (result is IEnumerable<object?> && result is not string && result is not XdmNode && result is not ResultTreeFragment)))
            {
                var items = result is object?[] a ? a : ((IEnumerable<object?>)result).ToArray();
                foreach (var item in items)
                    _sink.RawText(StringValueOf(item));
            }
            else
            {
                _sink.RawText(StringValueOf(result));
            }
            return;
        }

        // XTDE3362: copy-accumulators="yes" when accumulator not applicable to source tree
        if (instruction.CopyAccumulators == true && _stylesheet.Accumulators.Count > 0 && _principalSourceDocId.HasValue)
        {
            var sourceDocId = FindDocumentIdForInput(result);
            if (sourceDocId.HasValue && sourceDocId.Value == _principalSourceDocId.Value)
            {
                foreach (var accName in _stylesheet.Accumulators.Keys)
                {
                    if (!IsAccumulatorApplicableForCopy(accName))
                        throw Error($"XTDE3362: Accumulator '{accName.LocalName}' is not applicable to the source tree (not listed in use-accumulators for the initial mode)");
                }
            }
        }

        // When sequence accumulator is active and copy-accumulators="yes", deep-copy nodes
        // with accumulator value propagation and add directly to accumulator (avoiding
        // serialization to XML which would lose accumulator values).
        if (_sequenceAccumulator != null && instruction.CopyAccumulators == true && result is XdmNode or object?[])
        {
            var copied = await DeepCopyWithAccumulatorsAsync(result).ConfigureAwait(false);
            if (copied is object?[] copiedArr)
            {
                foreach (var item in copiedArr)
                    if (item != null)
                        AppendToSeqAccumulator(item);
            }
            else if (copied != null)
            {
                AppendToSeqAccumulator(copied);
            }
            return;
        }

        // When sequence accumulator is active, redirect attribute and document nodes to the accumulator —
        // attributes can't be serialized to XML output buffer, they need to stay as XdmAttribute objects.
        // Document nodes must stay as XdmDocument objects for instance-of document-node() to work.
        // Other node types (elements, etc.) go through normal serialization to get proper orphan copies.
        if (_sequenceAccumulator != null && ShouldAccumulate(result))
        {
            if (result is object?[] accArr)
            {
                foreach (var item in accArr)
                    if (item != null)
                        AppendToSeqAccumulator(CopyNodeForAccumulator(item));
            }
            else if (result is IEnumerable<object> accSeq && result is not string)
            {
                foreach (var item in accSeq)
                    AppendToSeqAccumulator(CopyNodeForAccumulator(item));
            }
            else if (result != null)
            {
                AppendToSeqAccumulator(CopyNodeForAccumulator(result));
            }
            return;
        }

        // XSLT 3.0 §11.9.1: xsl:copy-of preserves the SOURCE element's base URI on the
        // copied element. When a sequence accumulator is active (e.g. copy-of inside a
        // variable/param/function body), materialize copied element nodes — serialize,
        // reparse, orphan, and stamp CopySourceBaseUri — rather than flattening to text,
        // so a later read-side step can have base-uri() on the copy return the source's
        // base URI. Mirrors the xsl:copy set-site in CopyAsync. Only engages when the
        // source has a non-empty computed base URI to preserve; otherwise the normal
        // text-serialization path below runs unchanged.
        if (_sequenceAccumulator != null && _nodeStore != null
            && TryGetCopyOfElements(result, out var copyOfElems))
        {
            if (await TryAccumulateCopyOfElementsWithBaseUriAsync(copyOfElems!, copyNs).ConfigureAwait(false))
                return;
        }

        // SP-C slice 1: when building a temp-tree node fragment, clone copy-of'd source
        // elements directly into the active constructor as top-level fragment roots so the body
        // stays node-native and can flip (it no longer trips _tcFragmentIncomplete). Mirrors
        // TryAccumulateCopyOfElementsWithBaseUriAsync but targets the active TreeConstructor
        // rather than the sequence accumulator. Serialization to the string buffer STILL runs
        // (below, under _suppressTcIncomplete): the reparse fallback, the flip count-guard, and
        // the PXDB_TEMPTREE_DIFF differential all need a byte-parity comparison target, so this
        // keeps flip ⊆ compared — the clone is delivered only when the differential verified it
        // byte-identical to that reparse.
        //
        // SP-C slice 2: nested copy-of routes too (the Task-1 tc.Depth==0 fragment-root
        // restriction is lifted), so a copy-of inside an open LRE / xsl:element / xsl:copy also
        // clones directly into the constructor and lets the enclosing body flip. Two parity gaps
        // that lifting exposed are closed here: (1) the copied subtrees' descendant text is
        // suppressed from the tc text-routing during the follow-on serialization (see
        // _suppressTcIncomplete in WriteText/WriteTextItem) so it stays INSIDE the cloned nodes
        // rather than flattening into the enclosing frame; (2) the clone's CopySourceBaseUri is
        // stamped to mirror the reparse's recovered base sentinel exactly (below). Only engages
        // when a constructor is active (production for non-flipping bodies is unchanged, since
        // _suppressTcIncomplete stays false).
        var routedCopyOfToTree = false;
        var savedSuppressTcIncomplete = _suppressTcIncomplete;
        if (_untypedRtfFlipActive && _inheritNamespacesNo
            && _activeTreeConstructor is { } inhTc && _nodeStore != null
            && TryGetCopyOfNodesForDivergent(result, out var inhNodes))
        {
            // SP-C copy-0612 family: this copy-of is grafting content INTO an element declared
            // inherit-namespaces="no" (xsl:copy / LRE / xsl:element). Per XSLT 3.0 §11.7.2 the
            // copied content must NOT acquire any namespace from the constructing element — not the
            // default namespace and not any prefixed ancestor binding. The byte-parity route below
            // would keep the clone in its source document (non-zero) so the tree-constructor's
            // ancestor walk re-introduces the constructing element's b/d/default bindings (the
            // inherit-namespaces="yes" answer). Route the whole sequence as the authoritative
            // Document-0 build instead: each element carries only its own copy-namespaces-filtered
            // in-scope set (no ancestor walk), and the reparse — which cannot undeclare prefixed
            // namespaces in XML 1.0 — is a known-wrong oracle, so skip the differential.
            RouteDivergentCopyOfInto(inhTc, inhNodes!, copyNs);
            routedCopyOfToTree = true;
            _suppressTcIncomplete = true;
            _untypedRtfFlipDivergent = true;
        }
        else if (_activeTreeConstructor is { } tc && _nodeStore != null
            && TryGetCopyOfElements(result, out var tcCopyElems))
        {
            // Stage the clones first, then commit them together, so a byte-parity guard can
            // abandon the whole routing (leaving the legacy serialize-reparse path to mark the
            // body incomplete) without half-appending.
            var staged = new List<NodeId>(tcCopyElems!.Count);
            var enclosingScope = tc.CurrentInScope;
            var parityUnsafe = false;
            foreach (var srcElem in tcCopyElems!)
            {
                var cloneId = CloneSubtreeDeep(srcElem, null, copyNs);
                StampCopySourceBaseSentinel(srcElem, cloneId);
                // Byte-parity guard (SP-C slice 2): the serialize-reparse path STRIPS namespace
                // declarations that are already in scope at the insertion point (see the RTF note
                // below and the serializer's IsNamespaceInScope skip). The native clone keeps them
                // (copy-namespaces retains in-scope namespaces — the deliberately-more-correct
                // node model), so a copied subtree carrying a redundant in-scope declaration would
                // flip to a DIFFERENT namespace set than production delivers. Reconciling that is
                // the Task-4 namespace work; until then, detect it and leave this body on the
                // legacy path (no flip) rather than deliver a divergent tree.
                if (CloneDeclaresRedundantNamespace(cloneId, enclosingScope))
                    parityUnsafe = true;
                staged.Add(cloneId);
            }
            if (!parityUnsafe)
            {
                foreach (var cloneId in staged)
                    tc.AppendNode(cloneId);
                routedCopyOfToTree = true;
                _suppressTcIncomplete = true;
            }
            else if (_untypedRtfFlipActive)
            {
                // SP-C targeted: the redundant-in-scope declarations the reparse strips (and the
                // enclosing default namespace it re-contaminates the copy with) make the reparse a
                // known-wrong oracle for this shape. The node build is authoritative — deliver it
                // and skip the differential. Re-clone into Document 0 so the namespace axis reads
                // the copy's own complete in-scope set (no ancestor walk into the enclosing LRE).
                RouteDivergentCopyOfInto(tc, tcCopyElems!.ConvertAll(e => (XdmNode)e), copyNs);
                routedCopyOfToTree = true;
                _suppressTcIncomplete = true;
                _untypedRtfFlipDivergent = true;
            }
        }
        else if (_untypedRtfFlipActive && _activeTreeConstructor is { } mixedTc && _nodeStore != null
            && TryGetCopyOfNodes(result, out var flipNodes))
        {
            // SP-C targeted: mixed element+text/comment/PI copy-of sequence (copy-1220's
            // `wrapper/child::node()` — <a/>, text "sandwich", <a/>). TryGetCopyOfElements is
            // element-only, so this shape never routed; route the full ordered sequence into the
            // flip constructor as the authoritative node build (§11.7.2 fixup as node data) and
            // skip the differential (reparse re-contaminates copied namespaces — copy-1220/1221).
            RouteDivergentCopyOfInto(mixedTc, flipNodes!, copyNs);
            routedCopyOfToTree = true;
            _suppressTcIncomplete = true;
            _untypedRtfFlipDivergent = true;
        }

        try
        {
            if (result is ResultTreeFragment rtf)
            {
                // Parse RTF to XDM and serialize through SerializeNode for proper namespace fixup.
                // This ensures redundant namespace declarations are stripped and missing undeclarations
                // are added based on the current output namespace context.
                var rtfDoc = ParseResultTreeFragment(rtf);
                if (rtfDoc != null)
                    SerializeNode(rtfDoc, copyNs);
                else
                    _sink.RawText(rtf.XmlContent);
            }
            else if (result is System.Xml.Linq.XNode linqNode)
            {
                // LINQ XML nodes (from fn:analyze-string, fn:json-to-xml, etc.)
                _sink.RawText(linqNode.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
            }
            else if (result is object?[] arr)
            {
                SerializeCopyOfItems(arr, copyNs);
            }
            else if (result is IEnumerable<object> seq)
            {
                SerializeCopyOfItems(seq.ToArray(), copyNs);
            }
            else if (result != null)
            {
                // For atomic values (string, number, etc.), add space separator between
                // adjacent atomic values per XSLT 3.0 §5.7.2
                if (result is not XdmNode and not XdmDocument and not ResultTreeFragment
                    and not System.Xml.Linq.XNode and not Xdm.TextNodeItem)
                {
                    if (_lastResultWasAtomic && _attributeContentDepth == 0)
                        WriteText(" ", false);
                    SerializeNode(result, copyNs);
                    _lastResultWasAtomic = true;
                }
                else
                {
                    SerializeNode(result, copyNs, faithfulNamespaces: result is XdmDocument);
                    _lastResultWasAtomic = false;
                }
            }
        }
        finally
        {
            if (routedCopyOfToTree)
                _suppressTcIncomplete = savedSuppressTcIncomplete;
        }
    }


    /// <summary>
    /// Byte-parity guard for the SP-C copy-of routing: walks a freshly-cloned subtree carrying an
    /// accumulating in-scope namespace map (seeded from <paramref name="inheritedScope"/>, the tree
    /// constructor's insertion frame) and reports whether any element declares a namespace binding
    /// (same prefix → same URI) that is ALREADY in scope. The serialize-reparse path this routing
    /// must stay byte-identical to strips exactly those redundant declarations; the native clone
    /// keeps them, so a subtree that trips this must NOT flip until the Task-4 namespace work
    /// reconciles the two representations. Undeclarations (URI = <see cref="NamespaceId.None"/>) are
    /// never "redundant" — they change scope — so they never trip the guard.
    /// </summary>
    private bool CloneDeclaresRedundantNamespace(NodeId cloneId, IReadOnlyDictionary<string, NamespaceId> inheritedScope)
    {
        if (_nodeStore!.GetNode(cloneId) is not XdmElement e)
            return false;
        var scope = new Dictionary<string, NamespaceId>(inheritedScope);
        foreach (var nb in e.NamespaceDeclarations)
        {
            var prefix = nb.Prefix ?? "";
            if (nb.Namespace != NamespaceId.None
                && scope.TryGetValue(prefix, out var existing) && existing == nb.Namespace)
                return true;
            if (nb.Namespace == NamespaceId.None)
                scope.Remove(prefix);
            else
                scope[prefix] = nb.Namespace;
        }
        foreach (var c in _nodeStore.GetChildren(e))
            if (c is XdmElement && CloneDeclaresRedundantNamespace(c.Id, scope))
                return true;
        return false;
    }


    /// <summary>
    /// Deep-clones a source node subtree directly into the node store — the node-model
    /// replacement for the copy-of serialize-then-reparse path. Reproduces the
    /// <c>copy-namespaces</c> filtering (a dropped namespace is one used by neither the
    /// element's own name nor an attribute name) without the XML-text round-trip, so a copied
    /// element retains all its in-scope namespace nodes (which the reparse path dropped when a
    /// namespace happened to be in scope in the transient serialization context). Returns the
    /// clone's NodeId. Temp-tree node-model migration (SP1: copy-of seam).
    /// </summary>
    private NodeId CloneSubtreeDeep(XdmNode src, NodeId? parent, bool copyNamespaces, DocumentId? forceDocument = null)
    {
        switch (src)
        {
            case XdmElement e:
            {
                var id = _nodeStore!.NextId();
                var cloneDoc = forceDocument ?? e.Document;
                var decls = System.Collections.Immutable.ImmutableArray.CreateBuilder<Xdm.NamespaceBinding>();
                // When materialising into Document 0 (the divergent copy-of routing), the
                // namespace axis reads a constructed element's own NamespaceDeclarations with NO
                // ancestor walk (GatherInScopeNamespaces' DocumentId-0 branch). A source parsed via
                // the reader path / streaming records only each element's LOCAL declarations, so a
                // descendant that inherits a binding from an ancestor WITHIN the copied subtree
                // would lose it under the no-walk read. Materialise each clone's COMPLETE in-scope
                // set — gathered from the source with an ancestor walk — so the Document-0 read is
                // correct for every source convention. (Non-forced callers keep the local-decls
                // behaviour; their non-zero-document read still walks ancestors.)
                var sourceBindings = forceDocument is { } fdoc && fdoc.Value == 0
                    ? GatherSourceInScopeBindings(e)
                    : (IEnumerable<Xdm.NamespaceBinding>)e.NamespaceDeclarations;
                foreach (var nb in sourceBindings)
                {
                    if (!copyNamespaces)
                    {
                        var prefix = nb.Prefix ?? "";
                        var usedByElem = prefix == (e.Prefix ?? "");
                        var usedByAttr = false;
                        if (!string.IsNullOrEmpty(prefix))
                        {
                            foreach (var a in _nodeStore.GetAttributes(e))
                            {
                                if (a.Prefix == prefix) { usedByAttr = true; break; }
                            }
                        }
                        if (!usedByElem && !usedByAttr)
                            continue;
                    }
                    decls.Add(nb);
                }
                var attrIds = new List<NodeId>();
                foreach (var a in _nodeStore.GetAttributes(e))
                {
                    var aid = _nodeStore.NextId();
                    _nodeStore.Register(new XdmAttribute
                    {
                        Id = aid, Document = cloneDoc, Namespace = a.Namespace,
                        LocalName = a.LocalName, Prefix = a.Prefix, Value = a.Value, Parent = id,
                    });
                    attrIds.Add(aid);
                }
                var childIds = new List<NodeId>();
                foreach (var c in _nodeStore.GetChildren(e))
                    childIds.Add(CloneSubtreeDeep(c, id, copyNamespaces, forceDocument));
                var clone = new XdmElement
                {
                    StringValueResolver = _nodeStore.StringValueResolver,
                    Id = id, Document = cloneDoc, Namespace = e.Namespace,
                    LocalName = e.LocalName, Prefix = e.Prefix,
                    Attributes = attrIds.Count == 0 ? XdmElement.EmptyAttributes : System.Collections.Immutable.ImmutableArray.CreateRange(attrIds),
                    Children = childIds.Count == 0 ? XdmElement.EmptyChildren : System.Collections.Immutable.ImmutableArray.CreateRange(childIds),
                    NamespaceDeclarations = decls.Count == 0 ? XdmElement.EmptyNamespaceDeclarations : decls.ToImmutable(),
                    Parent = parent,
                };
                // XdmElement.StringValue returns the cached _stringValue (it is NOT computed
                // lazily from children), so a clone with a null cache reports an empty string
                // value — string($clone)/xsl:value-of would be blank. The source element carries
                // the correct cached value (ConvertToXdm / the reparse set it), and the clone's
                // descendant text is identical, so copy it over to stay byte-identical to the
                // serialize-then-reparse path.
                clone._stringValue = e.StringValue;
                _nodeStore.Register(clone);
                return id;
            }
            case XdmText t:
            {
                var id = _nodeStore!.NextId();
                _nodeStore.Register(new XdmText { Id = id, Document = forceDocument ?? t.Document, Value = t.Value, Parent = parent });
                return id;
            }
            case XdmComment cm:
            {
                var id = _nodeStore!.NextId();
                _nodeStore.Register(new XdmComment { Id = id, Document = forceDocument ?? cm.Document, Value = cm.Value, Parent = parent });
                return id;
            }
            case XdmProcessingInstruction pi:
            {
                var id = _nodeStore!.NextId();
                _nodeStore.Register(new XdmProcessingInstruction { Id = id, Document = forceDocument ?? pi.Document, Target = pi.Target, Value = pi.Value, Parent = parent });
                return id;
            }
            default:
                return src.Id;
        }
    }


    /// <summary>
    /// Creates a parentless copy of a node for sequence accumulator use.
    /// Attribute and namespace nodes need copies with no parent reference.
    /// </summary>
    private object CopyNodeForAccumulator(object item)
    {
        if (item is XdmAttribute attr)
        {
            return new XdmAttribute
            {
                Document = DocumentId.None,
                LocalName = attr.LocalName,
                Prefix = attr.Prefix,
                Namespace = attr.Namespace,
                Value = attr.Value,
                Id = _nodeStore?.NextId() ?? NodeId.None,
                Parent = NodeId.None
            };
        }
        // Deep-copy document nodes so copies have fresh node identity
        // (needed for union deduplication to work correctly)
        if (item is XdmDocument doc && _nodeStore != null)
        {
            return DeepCopyDocument(doc);
        }
        return item;
    }


    /// <summary>
    /// Creates a deep copy of an XdmDocument with fresh node IDs.
    /// Needed when copy-of in functions should return distinct node copies.
    /// </summary>
    private XdmDocument DeepCopyDocument(XdmDocument original)
    {
        var newDocId = _nodeStore!.NextId();
        var newChildren = new List<NodeId>();
        NodeId newDocElem = NodeId.None;

        foreach (var childId in original.Children)
        {
            var child = _nodeStore.GetNode(childId);
            if (child != null)
            {
                var copiedChild = DeepCopyNode(child, newDocId);
                newChildren.Add(copiedChild.Id);
                if (copiedChild is XdmElement && newDocElem == NodeId.None)
                    newDocElem = copiedChild.Id;
            }
        }

        string? docElemLocalName = newDocElem != NodeId.None
            ? (_nodeStore.GetNode(newDocElem) as XdmElement)?.LocalName : null;

        var newDoc = new XdmDocument
        {
            StringValueResolver = _nodeStore.StringValueResolver,
            Id = newDocId,
            Document = new DocumentId((uint)newDocId.Value),
            Parent = NodeId.None,
            DocumentElement = newDocElem,
            Children = newChildren,
            DocumentElementLocalName = docElemLocalName,
            // A deep copy (xsl:copy-of) of a document node preserves the source document's
            // base URI (dm:base-uri). Without this the materialize path stamps the orphan
            // copy with the construction (stylesheet) base. (fn/base-uri 053: deep-doc.)
            BaseUri = original.BaseUri,
            // When the source doc had NO base URI, mark the copy so AddAccItem's re-stamp
            // (guarded on CopySourceBaseUri == null) leaves its null base intact → base-uri() = ().
            CopySourceBaseUri = original.BaseUri == null ? DocCopyNullSourceBaseSentinel : null,
        };
        _nodeStore.Register(newDoc);
        return newDoc;
    }


    /// <summary>
    /// Recursively deep-copies an XDM node with fresh IDs, setting the given parent.
    /// </summary>
    private XdmNode DeepCopyNode(object node, NodeId parentId)
    {
        switch (node)
        {
            case XdmElement elem:
            {
                var newId = _nodeStore!.NextId();
                var newChildren = new List<NodeId>();
                foreach (var childId in elem.Children)
                {
                    var child = _nodeStore.GetNode(childId);
                    if (child != null)
                    {
                        var copied = DeepCopyNode(child, newId);
                        newChildren.Add(copied.Id);
                    }
                }
                var newAttrs = new List<NodeId>();
                foreach (var attrId in elem.Attributes)
                {
                    if (_nodeStore.GetNode(attrId) is XdmAttribute origAttr)
                    {
                        var newAttr = new XdmAttribute
                        {
                            Id = _nodeStore.NextId(),
                            Document = origAttr.Document,
                            Parent = newId,
                            LocalName = origAttr.LocalName,
                            Prefix = origAttr.Prefix,
                            Namespace = origAttr.Namespace,
                            Value = origAttr.Value
                        };
                        _nodeStore.Register(newAttr);
                        newAttrs.Add(newAttr.Id);
                    }
                }
                var newElem = new XdmElement
                {
                    StringValueResolver = _nodeStore.StringValueResolver,
                    Id = newId,
                    Document = elem.Document,
                    Parent = parentId,
                    LocalName = elem.LocalName,
                    Prefix = elem.Prefix,
                    Namespace = elem.Namespace,
                    NamespaceDeclarations = new List<NamespaceBinding>(elem.NamespaceDeclarations),
                    Children = newChildren,
                    Attributes = newAttrs
                };
                newElem._stringValue = elem.StringValue;
                _nodeStore.Register(newElem);
                return newElem;
            }
            case XdmText text:
            {
                var newText = new XdmText
                {
                    Id = _nodeStore!.NextId(),
                    Document = text.Document,
                    Parent = parentId,
                    Value = text.Value
                };
                _nodeStore.Register(newText);
                return newText;
            }
            case XdmComment comment:
            {
                var newComment = new XdmComment
                {
                    Id = _nodeStore!.NextId(),
                    Document = comment.Document,
                    Parent = parentId,
                    Value = comment.Value
                };
                _nodeStore.Register(newComment);
                return newComment;
            }
            case XdmProcessingInstruction pi:
            {
                var newPi = new XdmProcessingInstruction
                {
                    Id = _nodeStore!.NextId(),
                    Document = pi.Document,
                    Parent = parentId,
                    Target = pi.Target,
                    Value = pi.Value
                };
                _nodeStore.Register(newPi);
                return newPi;
            }
            default:
                return (XdmNode)node;
        }
    }


    /// <summary>
    /// Copies accumulator values from source nodes to destination nodes using a NodeId mapping.
    /// Used by fn:copy-of() and xsl:copy-of copy-accumulators="yes" to propagate
    /// accumulator values to copied nodes per XSLT 3.0 §6.5.
    /// </summary>
    internal void CopyAccumulatorValues(Dictionary<NodeId, NodeId> nodeMapping)
    {
        if (_accumulatorValues == null || nodeMapping.Count == 0)
            return;

        foreach (var (accName, nodeValues) in _accumulatorValues)
        {
            // Collect entries to add (can't modify during iteration)
            var toAdd = new List<(NodeId destId, (object? before, object? after) values)>();
            foreach (var (sourceId, destId) in nodeMapping)
            {
                if (nodeValues.TryGetValue(sourceId, out var values))
                    toAdd.Add((destId, values));
            }
            foreach (var (destId, values) in toAdd)
                nodeValues[destId] = values;
        }
    }


    /// <summary>
    /// Deep-copies nodes and propagates accumulator values from source to copy.
    /// Ensures accumulators are computed on the source document first.
    /// </summary>
    internal async ValueTask<object?> DeepCopyWithAccumulatorsAsync(object? input)
    {
        if (input == null || _nodeStore == null)
            return input;

        // XTDE3362: Check that accumulators are applicable to the source tree.
        // Per §18.2.2, accumulators on the principal source document are only applicable
        // if declared via use-accumulators on the initial mode's xsl:mode.
        if (_stylesheet.Accumulators.Count > 0 && _principalSourceDocId.HasValue)
        {
            var sourceDocId = FindDocumentIdForInput(input);
            if (sourceDocId.HasValue && sourceDocId.Value == _principalSourceDocId.Value)
            {
                foreach (var accName in _stylesheet.Accumulators.Keys)
                {
                    if (!IsAccumulatorApplicableForCopy(accName))
                        throw Error($"XTDE3362: Accumulator '{accName.LocalName}' is not applicable to the source tree (not listed in use-accumulators for the initial mode)");
                }
            }
        }

        // Ensure accumulators are computed on the source document(s)
        if (_stylesheet.Accumulators.Count > 0)
        {
            foreach (var accName in _stylesheet.Accumulators.Keys)
            {
                await EnsureAccumulatorsComputedForInputAsync(accName, input).ConfigureAwait(false);
            }
        }

        // Deep copy with mapping
        var mapping = new Dictionary<NodeId, NodeId>();
        var result = SnapshotHelper.DeepCopyItems(input, _nodeStore, mapping);

        // Copy accumulator values to the new nodes
        CopyAccumulatorValues(mapping);

        return result;
    }

}
