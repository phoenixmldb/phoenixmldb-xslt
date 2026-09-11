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

    /// <summary>
    /// Computes the base URI of a source XDM element by checking xml:base attributes
    /// and walking up ancestors. Used by xsl:copy to preserve source base URI.
    /// </summary>
    // True when the element carries its OWN xml:base attribute. Used by the untyped-RTF flip
    // base-sentinel guard: a shallow xsl:copy discards this attribute, so only an element that
    // HAD one needs the sentinel to carry its resolved base across the content reparse.
    private bool ElementHasOwnXmlBase(Xdm.Nodes.XdmElement elem)
    {
        foreach (var attrId in elem.Attributes)
        {
            if (_nodeStore!.GetNode(attrId) is Xdm.Nodes.XdmAttribute attr
                && attr.Namespace == NamespaceId.Xml && attr.LocalName == "base")
                return true;
        }
        return false;
    }


    public override async ValueTask CreateElementAsync(XsltElement instruction)
    {
        if (_recursionDepth >= MaxRecursionDepth)
            return;
        _recursionDepth++;
        try
        {
            await RunInstructionWithValidationAsync(
                instruction.Validation, "xsl:element", instruction.Location,
                () => CreateElementCoreAsync(instruction)).ConfigureAwait(false);
        }
        finally
        {
            _recursionDepth--;
        }
    }


    private async ValueTask CreateElementCoreAsync(XsltElement instruction)
    {
        // Inside attribute/comment/PI content, suppress element tags and pass through
        // only text content (atomization per XSLT spec 5.7.2)
        if (_textContentDepth > 0)
        {
            // When collecting simple content items, atomized elements are separate items
            // (not text nodes), so they get space separators in the final join (step 5).
            if (_collectTextAsSequenceItems && _sequenceAccumulator != null && _serializingElementDepth == 0)
            {
                // Flush any pending text in _output as a TextNodeItem.
                //
                // Slice and truncate from the CURRENT SCOPE's base, never from 0. Inside a
                // stylesheet function (or any scope that recorded a base) the buffer already
                // holds the caller's content below _outputLogicalStart; taking ToString() from
                // 0 and Clear()ing stole that content and left _output shorter than the offset
                // the caller saved, so the caller's own ToString(saved, …) then threw
                // "startIndex cannot be larger than length of string".
                var pendingBase = Math.Clamp(_outputLogicalStart, 0, _output.Length);
                var pending = _output.ToString(pendingBase, _output.Length - pendingBase);
                if (pending.Length > 0)
                {
                    AppendToSeqAccumulator(new Xdm.TextNodeItem(pending));
                    _output.Length = pendingBase;
                }
                // Execute element content with _collectTextAsSequenceItems disabled
                // so text goes to _output (not accumulator) for this element only
                _collectTextAsSequenceItems = false;
                try
                {
                    if (instruction.Content != null)
                        await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    _collectTextAsSequenceItems = true;
                }
                // Add the element's atomized content as a plain string (separate item),
                // again relative to this scope's base rather than the whole buffer.
                var elementBase = Math.Clamp(_outputLogicalStart, 0, _output.Length);
                var elementText = _output.ToString(elementBase, _output.Length - elementBase);
                _output.Length = elementBase;
                if (elementText.Length > 0)
                    AppendToSeqAccumulator(elementText);
                return;
            }
            // Simple content construction: space between items from different nodes
            // (only for comment/PI content; attribute content uses empty-string separator)
            if (_attributeContentDepth == 0 && _output.Length > 0)
                _sink.RawText(" ");
            if (instruction.Content != null)
                await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
            if (_attributeContentDepth == 0)
                _simpleContentLastWasNode = true;
            return;
        }

        var name = (await EvaluateAvtAsync(instruction.Name).ConfigureAwait(false)).Trim();

        // XTDE0820: Reject empty or invalid element names
        if (string.IsNullOrEmpty(name))
            throw Error("XTDE0820: The effective value of the 'name' attribute of xsl:element is a zero-length string");
        try
        {
            // Verify the computed name is a valid QName (prefix:local or just local)
            var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx >= 0)
            {
                System.Xml.XmlConvert.VerifyNCName(name[..colonIdx]);
                System.Xml.XmlConvert.VerifyNCName(name[(colonIdx + 1)..]);
            }
            else
            {
                System.Xml.XmlConvert.VerifyNCName(name);
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException) // "" throws ArgumentException
        {
            throw Error($"XTDE0820: The effective value of the 'name' attribute of xsl:element ('{name}') is not a valid QName");
        }

        string? nsUri = null;
        if (instruction.Namespace != null)
        {
            nsUri = await EvaluateAvtAsync(instruction.Namespace).ConfigureAwait(false);
            // When namespace="" is explicit, the element is in no namespace and any prefix is discarded
            if (nsUri != null && nsUri.Length == 0)
            {
                var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
                if (colonIdx > 0)
                    name = name[(colonIdx + 1)..];
            }
        }

        // Process use-attribute-sets
        var attrParts = new StringBuilder();
        await ApplyAttributeSetsAsync(instruction.UseAttributeSets, attrParts).ConfigureAwait(false);

        // Process content — attributes and non-attribute content are interleaved
        // Resolve namespace before processing content so children can inherit it
        if (nsUri == null)
        {
            var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx > 0)
            {
                var prefix = name[..colonIdx];
                if (instruction.InScopeNamespaces.TryGetValue(prefix, out var resolvedUri))
                {
                    nsUri = resolvedUri;
                }
                else if (prefix == "xml")
                {
                    nsUri = "http://www.w3.org/XML/1998/namespace";
                }
                else
                {
                    // XTDE0830: Prefixed name with no namespace attribute and undeclared prefix
                    throw Error($"XTDE0830: The prefix '{prefix}' in the element name '{name}' is not declared in any in-scope namespace declaration");
                }
            }
            else
            {
                // Unprefixed name: use default namespace if declared
                if (instruction.InScopeNamespaces.TryGetValue("", out var defaultNs)
                    && !string.IsNullOrEmpty(defaultNs))
                {
                    nsUri = defaultNs;
                }
            }
        }

        // XTDE0835: Element namespace must not be the xmlns namespace
        if (nsUri == "http://www.w3.org/2000/xmlns/")
            throw Error("XTDE0835: The namespace URI 'http://www.w3.org/2000/xmlns/' is not allowed for xsl:element");

        // Build namespace bindings for this element
        var elemNsBindings = new Dictionary<string, string>();
        if (nsUri != null)
        {
            var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
            var prefix = colonIdx > 0 ? name[..colonIdx] : "";
            elemNsBindings[prefix] = nsUri;
        }
        else
        {
            // Element has no namespace — if parent has a default namespace, record the undeclaration
            // in the scope so children know the default namespace has been cleared
            var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx < 0 && GetInScopeDefaultNamespace() != null)
            {
                elemNsBindings[""] = "";
            }
        }

        // inherit-namespaces="no" on parent: add default ns undeclaration so children
        // don't inherit the parent's xmlns="..." in the serialized output
        if (_forceDefaultNsUndeclaration && !elemNsBindings.ContainsKey("")
            && GetInScopeDefaultNamespace() != null)
        {
            elemNsBindings[""] = "";
        }

        // Use attribute-collection mode flag (push new buffer for nesting support)
        _collectedAttributesStack.Push(new StringBuilder());

        var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
        var savedLogicalStart = _outputLogicalStart;
        _outputLogicalStart = _output.Length;
        var savedLastAtomic = _lastResultWasAtomic;
        _lastResultWasAtomic = false;

        // Track element name for CDATA section serialization
        var localName = name;
        var colonIdx2 = name.IndexOf(':', StringComparison.Ordinal);
        if (colonIdx2 > 0)
            localName = name[(colonIdx2 + 1)..];
        // Carry the element's resolved namespace so cdata-section-elements matches by expanded
        // name (decl/output-0138). nsUri is the element's serialized namespace URI (or null).
        _outputElementStack.Push(new QName(
            string.IsNullOrEmpty(nsUri) ? NamespaceId.None : StylesheetParser.ResolveNamespaceUri(nsUri),
            localName));
        _outputElementHasNsStack.Push(nsUri != null || colonIdx2 > 0);
        _outputElementIsLreStack.Push(false); // xsl:element — not an LRE
        _xslNamespaceBindings.Push(new Dictionary<string, string>());

        PushOutputNsScope(elemNsBindings);
        PushOutputNsScope(new Dictionary<string, string>()); // Separate scope for attribute-emitted ns decls
        PushScope(); // Variables declared inside this element should be scoped to this element
        var savedSeqAccum1 = _sequenceAccumulator;
        _sequenceAccumulator = null; // Suspend accumulation inside element content
        _serializingElementDepth++;
        Dictionary<string, string>? xslNsBindingsForFixup = null;
        // inherit-namespaces="no": children must emit xmlns="" to undeclare the default namespace
        // that this element introduces in the serialized output
        var savedForceNsUndecl = _forceDefaultNsUndeclaration;
        _forceDefaultNsUndeclaration = instruction.InheritNamespaces == false
            && elemNsBindings.ContainsKey("") && !string.IsNullOrEmpty(elemNsBindings[""]);
        var savedInheritNsNoLre = _inheritNamespacesNo;
        _inheritNamespacesNo = instruction.InheritNamespaces == false;
        // Push xml:base onto static base URI stack so resolve-uri() resolves against it
        if (instruction.BaseUri != null)
            _staticBaseUriStack.Push(XsltTransformEngine.UriString(instruction.BaseUri)!);
        // SP-B slice 4: capture the active tree constructor (installed by the as="element()" body
        // seam under the differential) and open THIS element on it BEFORE executing content, so
        // child text/comment/PI/nested elements build natively into it in document order. The
        // constructor stays active through content (no suspend), and the element is sealed after
        // content in TcFinishElement. Namespaces/attributes/fixup are decided post-content and
        // applied to the still-open frame there.
        var tcForThisElement = _activeTreeConstructor;
        if (tcForThisElement != null)
            TcOpenElement(tcForThisElement, name, nsUri);
        try
        {
            await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
        }
        finally
        {
            if (instruction.BaseUri != null)
                _staticBaseUriStack.Pop();
            _forceDefaultNsUndeclaration = savedForceNsUndecl;
            _inheritNamespacesNo = savedInheritNsNoLre;
            _serializingElementDepth--;
            _sequenceAccumulator = savedSeqAccum1;
            PopScope();
            PopOutputNsScope(); // attribute ns decls scope
            PopOutputNsScope(); // element ns scope
            _outputElementStack.Pop();
            _outputElementHasNsStack.Pop();
            _outputElementIsLreStack.Pop();
            // Save xsl:namespace bindings for namespace fixup before popping
            if (_xslNamespaceBindings.Peek().Count > 0)
                xslNsBindingsForFixup = new Dictionary<string, string>(_xslNamespaceBindings.Peek());
            _xslNamespaceBindings.Pop();
        }

        var collectedAttrs = _collectedAttributesStack.Pop();
        var content = savedScope.GetWritten();
        savedScope.Dispose();
        _outputLogicalStart = savedLogicalStart;
        // After producing an element node, reset atomic state (nodes break atomic adjacency)
        _lastResultWasAtomic = false;

        // Namespace fixup: if xsl:namespace redefined the element's prefix to a different URI,
        // rename the element's prefix to avoid the conflict (XSLT 3.0 §11.7)
        string? fixupNewPrefix = null;
        if (xslNsBindingsForFixup != null && nsUri != null && nsUri.Length > 0)
        {
            var fixupColonIdx = name.IndexOf(':', StringComparison.Ordinal);
            if (fixupColonIdx > 0)
            {
                var elemPfx = name[..fixupColonIdx];
                if (xslNsBindingsForFixup.TryGetValue(elemPfx, out var xslUri) && xslUri != nsUri)
                {
                    fixupNewPrefix = GenerateUniquePrefix(elemPfx);
                    name = $"{fixupNewPrefix}:{name[(fixupColonIdx + 1)..]}";
                }
            }
        }

        // SP-B: when a tree constructor is active, mirror the exact namespace/attribute
        // decisions the string path makes below into typed lists, then build a byte-identical
        // XDM element via the constructor after the string element is fully emitted. `tcAbort`
        // is set if some decision can't be faithfully represented yet (e.g. an attribute prefix
        // not declared on this element); the constructor build is then skipped, which the
        // differential seam treats as "not captured" (no false divergence).
        List<(string Prefix, NamespaceId Ns)>? tcNsDecls = tcForThisElement != null ? new() : null;
        List<(NamespaceId Ns, string Local, string? Prefix, string Value)>? tcAttrs = tcForThisElement != null ? new() : null;
        var tcAbort = false;

        _sink.StartElementOpen(name);

        // Emit namespace declaration if not already in scope
        if (nsUri != null && nsUri.Length > 0)
        {
            var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx > 0)
            {
                var prefix = name[..colonIdx];
                if (!IsNamespaceInScope(prefix, nsUri))
                {
                    _sink.Namespace(prefix, nsUri);
                    tcNsDecls?.Add((prefix, _nodeStore!.InternNamespace(nsUri)));
                }
            }
            else
            {
                if (!IsNamespaceInScope("", nsUri))
                {
                    _sink.Namespace("", nsUri);
                    tcNsDecls?.Add(("", _nodeStore!.InternNamespace(nsUri)));
                }
            }
        }
        else
        {
            // Element is in no namespace (nsUri is null or empty string).
            // If the in-scope default namespace is non-empty, emit xmlns="" to undeclare it.
            var colonIdx3 = name.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx3 < 0)
            {
                var parentDefaultNs = GetInScopeDefaultNamespace();
                if (parentDefaultNs != null)
                {
                    _sink.Namespace("", "");
                    // xmlns="" is a real namespace undeclaration in the node model.
                    tcNsDecls?.Add(("", NamespaceId.None));
                }
            }
        }

        // Deduplicate attributes (last-wins)
        var attrs = new Dictionary<string, string>();
        ParseAttributeString(attrParts.ToString(), attrs);
        ParseAttributeString(collectedAttrs.ToString(), attrs);
        foreach (var (attrName, attrValue) in attrs)
        {
            _output.Append(' ');
            _output.Append(attrName);
            _output.Append("=\"");
            _output.Append(attrValue);
            _output.Append('"');
            if (tcAttrs != null)
                RecordTreeAttribute(attrName, attrValue, elemNsBindings, tcNsDecls!, tcAttrs, ref tcAbort);
        }

        if (content.Length > 0)
        {
            _sink.StartElementClose(false);
            _sink.RawText(content);
            _sink.EndElement(name);
        }
        else
        {
            _sink.StartElementClose(true);
        }

        // Element is "significant" for where-populated only if it has child content
        // (attributes alone don't count as "populated" per XSLT 3.0 spec)
        if (content.Length > 0)
            MarkContentProduced();

        // SP-B slice 4: seal the element opened before content; its children built natively in
        // document order. Namespaces/attributes/fixup applied to the still-open frame here.
        if (tcForThisElement is { } tc)
            TcFinishElement(tc, name, tcNsDecls!, tcAttrs!, tcAbort);
    }


    public override async ValueTask CreateAttributeAsync(XsltAttribute instruction)
    {
        // Set ambient location so XTDE0410/0420 raised from helper paths (CopySingleItemAsync,
        // SerializeNode) can attribute the error to *this* xsl:attribute. We don't restore on
        // exit — the next instruction will overwrite, and the value is best-effort diagnostics.
        _currentInstructionLocation = instruction.Location ?? _currentInstructionLocation;

        // XTDE0420: Cannot add attribute to a document node
        if (_documentNodeDepth > 0 && !_attributeCollecting)
        {
            DiagXtde0420("CreateAttributeAsync", instruction.Name?.ToString());
            throw new XsltException("XTDE0420: Cannot add an attribute node to a document node", instruction.Location);
        }

        // XTDE0410: Cannot add attribute after child content has been added to the element
        // Inside xsl:where-populated, defer this check until after insignificant items are filtered
        if (_attributeCollecting && _output.Length > _outputLogicalStart && _wherePopulatedDepth == 0)
        {
            if (!IsBackwardsCompatible)
                throw new XsltException("XTDE0410: Cannot add an attribute to an element after children have been added", instruction.Location);
            // In backwards-compatible (1.0) mode, silently ignore the attribute
            return;
        }

        var name = await EvaluateAvtAsync(instruction.Name).ConfigureAwait(false);

        // XTDE0850: Validate attribute name is a valid QName
        if (string.IsNullOrEmpty(name))
            throw Error("XTDE0850: The effective value of the 'name' attribute of xsl:attribute is a zero-length string");
        // XTDE0855: The name "xmlns" without a namespace attribute is not allowed
        if (name == "xmlns" && instruction.Namespace == null)
            throw Error("XTDE0855: The attribute name 'xmlns' is not allowed on xsl:attribute without a namespace attribute");
        try
        {
            var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx >= 0)
            {
                System.Xml.XmlConvert.VerifyNCName(name[..colonIdx]);
                System.Xml.XmlConvert.VerifyNCName(name[(colonIdx + 1)..]);
            }
            else
            {
                System.Xml.XmlConvert.VerifyNCName(name);
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException) // "" throws ArgumentException
        {
            throw Error($"XTDE0850: The effective value of the 'name' attribute of xsl:attribute ('{name}') is not a valid QName");
        }

        // XTDE0860: Prefixed name with no namespace attribute — prefix must be declared
        if (instruction.Namespace == null)
        {
            var colonIdx2 = name.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx2 > 0)
            {
                var prefix = name[..colonIdx2];
                // The xml prefix is always implicitly bound to http://www.w3.org/XML/1998/namespace
                string? prefixNsUri = null;
                if (prefix != "xml" && !instruction.InScopeNamespaces.TryGetValue(prefix, out prefixNsUri))
                    throw Error($"XTDE0860: The prefix '{prefix}' in the attribute name '{name}' is not declared in any in-scope namespace declaration");
                // Emit namespace declaration for the prefix if not already in scope
                // Skip when accumulating XDM nodes — the attribute node stores namespace info natively
                if (_sequenceAccumulator == null && prefix != "xml" && prefixNsUri != null && !IsNamespaceInScope(prefix, prefixNsUri))
                {
                    var target2 = _attributeCollecting ? _collectedAttributes! : _output;
                    target2.Append(" xmlns:");
                    target2.Append(prefix);
                    target2.Append("=\"");
                    target2.Append(EscapeAttributeValue(prefixNsUri));
                    target2.Append('"');
                    if (_outputNsScopes.Count > 0)
                        _outputNsScopes.Peek()[prefix] = prefixNsUri;
                }
            }
        }

        // Handle namespace attribute
        string? explicitNsUri = null;
        if (instruction.Namespace != null)
        {
            explicitNsUri = await EvaluateAvtAsync(instruction.Namespace).ConfigureAwait(false);
            // XTDE0865: Attribute namespace must not be the xmlns namespace
            if (explicitNsUri == "http://www.w3.org/2000/xmlns/")
                throw Error("XTDE0865: The namespace URI 'http://www.w3.org/2000/xmlns/' is not allowed for xsl:attribute");
            // When namespace="" is explicit, the attribute is in no namespace and any prefix is discarded
            if (explicitNsUri != null && explicitNsUri.Length == 0)
            {
                var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
                if (colonIdx > 0)
                    name = name[(colonIdx + 1)..];
            }
            if (!string.IsNullOrEmpty(explicitNsUri))
            {
                var colonIdx2 = name.IndexOf(':', StringComparison.Ordinal);
                if (colonIdx2 < 0)
                {
                    // No prefix — generate one
                    var prefix = GenerateNsPrefix(explicitNsUri);
                    name = $"{prefix}:{name}";
                }
                else
                {
                    // Has prefix — check if it's already bound to a different namespace
                    var existingPrefix = name[..colonIdx2];
                    if (_stylesheet.Namespaces.TryGetValue(existingPrefix, out var existingNs) && existingNs != explicitNsUri)
                    {
                        // Prefix conflict — generate a new prefix based on original
                        var newPrefix = $"{existingPrefix}_1";
                        name = $"{newPrefix}:{name[(colonIdx2 + 1)..]}";
                    }
                }
                // Emit namespace declaration for the prefix if not already in scope
                // Skip when accumulating XDM nodes — the attribute node stores namespace info natively
                if (_sequenceAccumulator == null)
                {
                    var attrPrefix = name[..name.IndexOf(':', StringComparison.Ordinal)];
                    if (!IsNamespaceInScope(attrPrefix, explicitNsUri))
                    {
                        var target2 = _attributeCollecting ? _collectedAttributes! : _output;
                        target2.Append(" xmlns:");
                        target2.Append(attrPrefix);
                        target2.Append("=\"");
                        target2.Append(EscapeAttributeValue(explicitNsUri));
                        target2.Append('"');
                        // Register in the current scope so child elements won't re-emit it
                        if (_outputNsScopes.Count > 0)
                            _outputNsScopes.Peek()[attrPrefix] = explicitNsUri;
                    }
                }
            }
        }

        // Evaluate separator AVT once (it may contain dynamic expressions)
        string? resolvedSeparator = instruction.Separator != null
            ? await EvaluateAvtAsync(instruction.Separator).ConfigureAwait(false)
            : null;

        string value;
        if (instruction.Select != null)
        {
            var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
            var sep = resolvedSeparator ?? " ";
            // §5.7.2 simple-content construction: merge ADJACENT text nodes with no
            // separator (so a run of text() nodes concatenates: 352/430/480 → "352430480"),
            // then join the remaining items (element/atomic string values) with the
            // separator. Element items atomize to their string value. This mirrors
            // xsl:value-of and is shared by the streaming and non-streaming paths.
            if (result is object?[] arr)
            {
                value = MergeSimpleContent(new List<object?>(arr), sep);
            }
            else if (result is System.Collections.IEnumerable seq && result is not string && result is not XdmNode)
            {
                var items = new List<object?>();
                foreach (var item in seq)
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
            // When an explicit separator is specified on xsl:attribute with content form,
            // use the sequence accumulator to collect individual items, then join with separator.
            // Per XSLT 3.0 spec 5.7.2, the default separator for content form is zero-length string.
            if (resolvedSeparator != null)
            {
                var savedAccumulator = _sequenceAccumulator;
                _sequenceAccumulator = new List<object?>();
                var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
                _textContentDepth++;
                _attributeContentDepth++;
                // Disable outer text collection so attribute content text goes to
                // _output (attribute uses its own accumulator for separator handling)
                var savedCollectText1 = _collectTextAsSequenceItems;
                _collectTextAsSequenceItems = false;
                try
                {
                    await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    _collectTextAsSequenceItems = savedCollectText1;
                    _attributeContentDepth--;
                    _textContentDepth--;
                }
                // Collect items from both accumulator and output buffer
                var items = new List<string>();
                foreach (var item in _sequenceAccumulator)
                {
                    if (item != null)
                        items.Add(StringValueOf(item));
                }
                var textContent = savedScope.GetWritten();
                if (!string.IsNullOrEmpty(textContent))
                    items.Add(textContent);
                value = string.Join(resolvedSeparator, items);
                savedScope.Dispose();
                _sequenceAccumulator = savedAccumulator;
            }
            else
            {
                var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
                _textContentDepth++;
                _attributeContentDepth++;
                // Disable text collection so attribute content text goes to _output
                // (not the outer variable's sequence accumulator)
                var savedCollectText2 = _collectTextAsSequenceItems;
                _collectTextAsSequenceItems = false;
                // ...and detach the accumulator itself, for the same reason. Only the text path
                // was covered: xsl:sequence checks `_sequenceAccumulator != null` FIRST and
                // appends there regardless of attribute-content depth, so
                //
                //     <xsl:attribute name="class">
                //       <xsl:choose>…<xsl:sequence select="'failed'"/>…</xsl:choose>
                //     </xsl:attribute>
                //
                // inside a typed template put "failed" into the caller's sequence and built the
                // attribute EMPTY — two items where the template declared attribute(class), so
                // XTTE0505 "expected exactly one item, got 2". Reported by Martin Honnen against
                // XSpec's format-xspec-report.xsl, whose scenario-html-class-attribute is exactly
                // this shape.
                //
                // The separator branch above already does this; this branch is the common case
                // and did not. Same instruction, two branches, one of them right.
                var savedAccumulator2 = _sequenceAccumulator;
                _sequenceAccumulator = null;
                try
                {
                    await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    _sequenceAccumulator = savedAccumulator2;
                    _collectTextAsSequenceItems = savedCollectText2;
                    _attributeContentDepth--;
                    _textContentDepth--;
                }
                value = savedScope.GetWritten();
                savedScope.Dispose();
            }
        }
        else
        {
            value = "";
        }

        // xsl:attribute validation. The XSLT 3.0 spec §27.2 says attribute validation is
        // considered to occur in the context of an element. Without that context here, we
        // do the part we can do reliably: confirm the global schema-attribute declaration
        // exists when validation="strict" (raise XQDY0027 otherwise), and skip silently
        // for lax. Value-against-type checking is a TODO; today it relies on the parent
        // element's validation= when one is set.
        ValidateAttributeIfRequested(instruction, name, explicitNsUri);

        // If sequence accumulator is active, create an XdmAttribute node for the sequence
        if (_sequenceAccumulator != null)
        {
            var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
            var localName = colonIdx >= 0 ? name[(colonIdx + 1)..] : name;
            var prefix = colonIdx >= 0 ? name[..colonIdx] : null;
            var attrNsId = NamespaceId.None;
            if (!string.IsNullOrEmpty(explicitNsUri))
            {
                attrNsId = _nodeStore!.InternNamespace(explicitNsUri);
            }
            else if (prefix != null && _stylesheet.Namespaces.TryGetValue(prefix, out var prefixUri))
            {
                attrNsId = _nodeStore!.InternNamespace(prefixUri);
            }
            var attr = new XdmAttribute
            {
                Document = DocumentId.None,
                LocalName = localName,
                Prefix = prefix,
                Namespace = attrNsId,
                Value = value,
                Id = NodeId.None,
                Parent = NodeId.None
            };
            AppendToSeqAccumulator(attr);
            return;
        }

        var target = _attributeCollecting ? _collectedAttributes! : _output;
        if (target == _output)
        {
            _sink.Attribute(name, value);
        }
        else
        {
            target.Append(' ');
            target.Append(name);
            target.Append("=\"");
            target.Append(EscapeAttributeValue(value));
            target.Append('"');
        }
    }


    public override async ValueTask CreateCommentAsync(XsltComment instruction)
    {
        string value;

        if (instruction.Select != null)
        {
            var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
            value = StringValueOf(result);
        }
        else if (instruction.Content != null)
        {
            // §5.7.2 simple content: collect each instruction's output as a separate
            // item in the accumulator, then join with space separator.
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

            // Flush any trailing text to accumulator
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

        // Top-level item-separator: a copy of the separator precedes this comment when an item was
        // already emitted in the enclosing result sequence (§5.7.2 sequence normalization).
        TryWriteTopLevelItemSeparator();

        _sink.Comment(value);

        // SP-B slice 4: route a comment child into the active constructor (element-content level
        // only; a comment collected inside another comment/PI body has _textContentDepth > 0).
        if (_textContentDepth == 0 && _activeTreeConstructor is { } ctc)
            ctc.AppendComment(value);

        // Comments count as "populated" for xsl:where-populated
        MarkContentProduced();
        // Comment is a node, so it breaks adjacent atomic value chain
        _lastResultWasAtomic = false;
    }


    public override async ValueTask CreateNamespaceAsync(XsltNamespace instruction)
    {
        var prefix = await EvaluateAvtAsync(instruction.Name).ConfigureAwait(false);

        // XTDE0920: Name must be a valid NCName (and not "xmlns")
        if (string.Equals(prefix, "xmlns", StringComparison.Ordinal))
            throw Error("XTDE0920: The name 'xmlns' is not allowed as the prefix for xsl:namespace");
        if (!string.IsNullOrEmpty(prefix))
        {
            try
            { System.Xml.XmlConvert.VerifyNCName(prefix); }
            catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException) // "" throws ArgumentException
            {
                throw Error($"XTDE0920: The effective value of the 'name' attribute of xsl:namespace ('{prefix}') is not a valid NCName");
            }
        }

        string uri;
        if (instruction.Select != null)
        {
            var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
            uri = StringValueOf(result);
        }
        else if (instruction.Content != null)
        {
            var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
            // Save/restore sequence accumulator to prevent xsl:value-of inside namespace
            // content from leaking TextNodeItems into the function's accumulator
            var savedAccumNs = _sequenceAccumulator;
            _sequenceAccumulator = null;
            // SP-B slice 4: this content is captured into the scoped buffer as the namespace URI
            // (a string value), not emitted as element children — so suspend the active tree
            // constructor to keep its text (routed by WriteText) from being appended as a spurious
            // child text node on the enclosing element's frame. Unlike xsl:comment/PI/attribute,
            // xsl:namespace does not raise _textContentDepth, so the WriteText guard needs this.
            var savedTcNs = _activeTreeConstructor;
            _activeTreeConstructor = null;
            try
            {
                await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
            }
            finally
            {
                _activeTreeConstructor = savedTcNs;
            }
            _sequenceAccumulator = savedAccumNs;
            uri = savedScope.GetWritten();
            savedScope.Dispose();
        }
        else
        {
            uri = "";
        }

        // XTDE0930: The namespace URI must not be empty when a prefix is specified
        if (!string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(uri))
            throw Error($"XTDE0930: The string value of the xsl:namespace node is a zero-length string, but a prefix ('{prefix}') was specified");

        // XTDE0925: xml namespace rules
        if (string.Equals(prefix, "xml", StringComparison.Ordinal)
            && !string.Equals(uri, "http://www.w3.org/XML/1998/namespace", StringComparison.Ordinal))
            throw Error("XTDE0925: Prefix 'xml' must be bound to 'http://www.w3.org/XML/1998/namespace'");
        if (!string.Equals(prefix, "xml", StringComparison.Ordinal)
            && string.Equals(uri, "http://www.w3.org/XML/1998/namespace", StringComparison.Ordinal))
            throw Error("XTDE0925: Only the prefix 'xml' can be bound to 'http://www.w3.org/XML/1998/namespace'");

        // XTDE0905: The namespace URI must be valid and not the xmlns namespace
        if (string.Equals(uri, "http://www.w3.org/2000/xmlns/", StringComparison.Ordinal))
            throw Error("XTDE0905: The namespace URI 'http://www.w3.org/2000/xmlns/' is not allowed");
        // XTDE0905: Namespace URI must be a valid xs:anyURI. Use Uri.TryCreate(Absolute)
        // which is permissive (accepts IRI chars like | and "). For relative URIs, accept
        // anything that doesn't have multiple # delimiters (only one fragment allowed).
        if (!string.IsNullOrWhiteSpace(uri)
            && !Uri.TryCreate(uri, UriKind.Absolute, out _))
        {
            // Check for multiple # characters (invalid fragment syntax)
            var firstHash = uri.IndexOf('#', StringComparison.Ordinal);
            if (firstHash >= 0 && uri.AsSpan(firstHash + 1).Contains('#'))
                throw Error($"XTDE0905: The namespace URI '{uri}' is not a valid URI");
        }

        // XTDE0420: Cannot add namespace to a document node
        if (_documentNodeDepth > 0 && !_attributeCollecting)
        {
            DiagXtde0420("CreateNamespaceAsync", $"xmlns:{prefix}={uri}");
            throw Error("XTDE0420: Cannot add a namespace node to a document node");
        }

        // A namespace node produced into the sequence accumulator is FREE-STANDING — it is a
        // value being returned (e.g. from a function declared as="namespace-node()*"), not a
        // declaration being attached to an element under construction. XTDE0440 and XTDE0430
        // below both describe "the element being constructed", and the stacks they consult
        // still hold the ENCLOSING element's entries here, so applying them to a free node
        // reports a conflict between namespaces that never share an element. XSpec's
        // x:copy-of-namespaces is exactly this shape: called once per source element, it
        // returned two default-namespace nodes with different URIs and was rejected as a
        // duplicate declaration even though each belonged to a different element.
        if (_sequenceAccumulator != null)
        {
            var nsNode = new XdmNamespace
            {
                Id = NodeId.None,
                Document = DocumentId.None,
                Parent = NodeId.None,
                Prefix = prefix ?? "",
                Uri = uri
            };
            AppendToSeqAccumulator(nsNode);
            return;
        }

        // XTDE0440: Cannot define a default namespace when the element is in no namespace
        if (string.IsNullOrEmpty(prefix) && !string.IsNullOrEmpty(uri)
            && _outputElementHasNsStack.Count > 0 && !_outputElementHasNsStack.Peek())
            throw Error($"XTDE0440: Cannot define a default namespace ('{uri}') when the element being constructed is in no namespace");

        // XTDE0430: Duplicate namespace nodes with same prefix but different URIs
        var nsPrefix = prefix ?? "";
        if (_xslNamespaceBindings.Count > 0)
        {
            var currentBindings = _xslNamespaceBindings.Peek();
            if (currentBindings.TryGetValue(nsPrefix, out var existingUri) && existingUri != uri)
                throw Error($"XTDE0430: Two namespace nodes with the same prefix '{nsPrefix}' have different namespace URIs ('{existingUri}' and '{uri}')");
            currentBindings[nsPrefix] = uri;
        }

        // Conflicts between xsl:namespace and the element's own namespace bindings
        // are detected at element construction time, where we can check whether
        // exclude-result-prefixes was set (→ namespace fixup) or not (→ XTDE0430).

        // Namespace declarations should appear in the element's start tag,
        // so write to _collectedAttributes when in attribute collection mode
        var target = _attributeCollecting ? _collectedAttributes! : _output;

        // Adding a namespace node for a (prefix, uri) the element under construction already
        // has is a no-op, not a duplicate declaration: XTDE0430 covers only the case where the
        // URIs differ (checked above). Emitting it again yields two identical xmlns:p
        // attributes on one start tag, which is not well-formed XML — the output cannot be
        // reparsed at all.
        //
        // The construction sites (LRE / xsl:element / xsl:copy) each push exactly two scopes
        // for the element being built: its own declarations, then an empty scope for
        // attribute-emitted ones. Checking those two covers both "the element already declares
        // this" and "an earlier xsl:namespace on this element already emitted it".
        //
        // This also suppresses the case where the element's binding was itself dropped as
        // redundant with an identical ancestor declaration. That is still correct: the
        // namespace node is in scope either way, and omitting a redundant re-declaration is
        // valid serialization of the same infoset.
        //
        // Hit by DocBook xslTNG's tools/generate-parameters.xsl, where xsl:namespace-alias puts
        // the result root in the XSL namespace and <xsl:namespace name="xsl"> re-declares that
        // same binding — producing a stylesheet no parser would accept.
        var scopesChecked = 0;
        foreach (var scope in _outputNsScopes)
        {
            if (scopesChecked++ >= 2) break;
            if (scope.TryGetValue(nsPrefix, out var declaredUri) && declaredUri == uri)
                return;
        }

        target.Append(" xmlns");
        if (!string.IsNullOrEmpty(prefix))
        {
            target.Append(':');
            target.Append(prefix);
        }
        target.Append("=\"");
        target.Append(EscapeAttributeValue(uri));
        target.Append('"');
    }


    public override async ValueTask CreateDocumentAsync(XsltDocument instruction)
    {
        // When in sequence accumulator mode and we have a node store,
        // create a proper XDM document node so pattern matching and
        // document-node functions work correctly.
        if (_sequenceAccumulator != null && _nodeStore != null)
        {
            var seqScope = new XsltTransformEngine.ScopedOutputBuffer(_output);

            // Save and clear the sequence accumulator so that child instructions
            // (like xsl:sequence) serialize to the output buffer instead of
            // leaking items to the outer accumulator.
            var savedAccumulator = _sequenceAccumulator;
            _sequenceAccumulator = null;

            // xsl:document creates a new output scope — if inside text content mode
            // (comment/PI body), temporarily exit so child instructions serialize
            // as proper XML nodes instead of atomized text.
            var savedTextContentDepth = _textContentDepth;
            var savedCollectText = _collectTextAsSequenceItems;
            if (_textContentDepth > 0)
            {
                _textContentDepth = 0;
                _collectTextAsSequenceItems = false;
            }

            _documentNodeDepth++;
            // EMIT context: content serialized here is captured as text and reparsed below,
            // so raise the temp-tree depth and seed the serialization base context with this
            // scope's effective base URI (the same value stamped on the reparsed document).
            // A SOURCE element copied in whose base differs picks up a base sentinel that the
            // reparse recovers onto CopySourceBaseUri.
            _tempTreeSerializeDepth++;
            var savedDocBaseContext = _serializeBaseContext;
            _serializeBaseContext = EffectiveBaseUri;
            try
            {
                await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
            }
            finally
            {
                _documentNodeDepth--;
                _tempTreeSerializeDepth--;
                _serializeBaseContext = savedDocBaseContext;
                _sequenceAccumulator = savedAccumulator;
                _textContentDepth = savedTextContentDepth;
                _collectTextAsSequenceItems = savedCollectText;
            }

            var content = seqScope.GetWritten();
            seqScope.Dispose();


            if (content.Length > 0)
            {
                RunValidation(instruction.Validation, content, ValidationKind.Fragment,
                    "xsl:document", instruction.Location);
                try
                {
                    // Stream-parse via XmlReader rather than allocating a full XmlDocument.
                    // Same hot-path optimization as AddBodyOutputChunk and the xsl:variable
                    // as=element(...) path: reads one synthetic wrapper element and emits
                    // its children directly into XDM nodes without an intermediate DOM.
                    var settings = new System.Xml.XmlReaderSettings
                    {
                        DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                        IgnoreWhitespace = false,
                        IgnoreComments = false,
                        IgnoreProcessingInstructions = false,
                    };
                    using var stringReader = new System.IO.StringReader($"<_seq_root_>{content}</_seq_root_>");
                    using var reader = System.Xml.XmlReader.Create(stringReader, settings);
                    var seqBaseUri = EffectiveBaseUri;
                    var docId = _nodeStore.NextId();
                    var children = new List<NodeId>();
                    NodeId docElementId = NodeId.None;
                    var parsedChildren = new List<object?>();
                    ReadAsBodyChunkChildren(reader, parsedChildren);
                    var stringValueBuilder = new StringBuilder();
                    foreach (var item in parsedChildren)
                    {
                        if (item is XdmNode cn)
                        {
                            cn.Parent = docId;
                            children.Add(cn.Id);
                            if (cn is XdmElement && docElementId == NodeId.None)
                                docElementId = cn.Id;
                            // Concatenate descendant text values for the document's string value
                            stringValueBuilder.Append(cn.StringValue);
                        }
                    }
                    string? docElemLocalName = docElementId != NodeId.None
                        ? (_nodeStore.GetNode(docElementId) as XdmElement)?.LocalName : null;
                    var docNode = new XdmDocument
                    {
                        Id = docId,
                        Document = new DocumentId(1),
                        Parent = NodeId.None,
                        DocumentElement = docElementId,
                        Children = children,
                        DocumentElementLocalName = docElemLocalName,
                        _stringValue = stringValueBuilder.ToString(),
                    };
                    docNode.BaseUri = seqBaseUri;
                    _nodeStore.Register(docNode);
                    AppendToSeqAccumulator(docNode);
                }
                catch (System.Xml.XmlException)
                {
                    // If XML parsing fails, fall through to text output
                    _sink.RawText(content);
                }
            }
            else
            {
                // Empty xsl:document creates an empty document node
                var docId = _nodeStore.NextId();
                var docNode = new XdmDocument
                {
                    Id = docId,
                    Document = new DocumentId(1),
                    Parent = NodeId.None,
                    DocumentElement = NodeId.None,
                    Children = new List<NodeId>(),
                    BaseUri = EffectiveBaseUri,
                };
                _nodeStore.Register(docNode);
                AppendToSeqAccumulator(docNode);
            }

            MarkContentProduced();
            return;
        }

        // When inside text content (comment/PI/attribute), create a proper XDM document
        // node so that its string value excludes comment/PI children (per XDM spec).
        if (_textContentDepth > 0 && _nodeStore != null)
        {
            var savedOut2 = _output.ToString();
            _output.Clear();

            // Temporarily leave text content mode so nodes serialize as XML
            var savedTextDepth = _textContentDepth;
            var savedAttrDepth = _attributeContentDepth;
            _textContentDepth = 0;
            _attributeContentDepth = 0;

            _documentNodeDepth++;
            try
            {
                await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
            }
            finally
            {
                _documentNodeDepth--;
                _textContentDepth = savedTextDepth;
                _attributeContentDepth = savedAttrDepth;
            }

            var xmlContent = _output.ToString();
            _output.Clear();
            _output.Append(savedOut2);

            if (xmlContent.Length > 0)
            {
                RunValidation(instruction.Validation, xmlContent, ValidationKind.Fragment,
                    "xsl:document", instruction.Location);
                try
                {
                    // Stream descendant text nodes — equivalent to
                    // XmlDocument.DocumentElement.InnerText without DOM allocation.
                    var docSettings = new System.Xml.XmlReaderSettings
                    {
                        Async = true,
                        DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                        IgnoreWhitespace = false,
                        IgnoreComments = true,
                        IgnoreProcessingInstructions = true,
                    };
                    using var docStringReader = new System.IO.StringReader($"<_doc_wrap_>{xmlContent}</_doc_wrap_>");
                    using var docReader = System.Xml.XmlReader.Create(docStringReader, docSettings);
                    while (await docReader.ReadAsync().ConfigureAwait(false))
                    {
                        if (docReader.NodeType is System.Xml.XmlNodeType.Text
                            or System.Xml.XmlNodeType.CDATA
                            or System.Xml.XmlNodeType.SignificantWhitespace
                            or System.Xml.XmlNodeType.Whitespace)
                        {
                            _sink.RawText(docReader.Value);
                        }
                    }
                }
                catch (System.Xml.XmlException)
                {
                    // Fallback: use raw text content
                    _sink.RawText(xmlContent);
                }
                MarkContentProduced();
            }
            return;
        }

        // Default path: buffer content to text output
        var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
        var savedLogicalStart = _outputLogicalStart;
        _outputLogicalStart = _output.Length;

        // XSLT 3.0 §5.7.1: a document node emitted into element/complex content transmits its
        // children; the node itself is not an atomic value, so it neither takes a §5.7.2 separator
        // from a preceding sibling document nor leaves a trailing atomic for the next one. Under
        // streaming, the transmit-vs-absorb distinction was stamped on the for-each subscription at
        // scan time (the fused variable+data() wrapper is gone by now) and threaded here via
        // CurrentUsage. Transmission → reset the atomic run around the body so adjacent constructed
        // documents concatenate (si-document-001/007/010); Absorption → leave the flag as the body
        // set it so a downstream data() atomization keeps its separators (si-document-003).
        var transmit = CurrentUsage == Usage.Transmission;
        if (transmit)
            _lastResultWasAtomic = false;

        _documentNodeDepth++;
        try
        {
            await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
        }
        finally
        {
            _documentNodeDepth--;
        }

        var textContent = savedScope.GetWritten();
        savedScope.Dispose();
        _outputLogicalStart = savedLogicalStart;
        if (textContent.Length > 0)
        {
            RunValidation(instruction.Validation, textContent, ValidationKind.Fragment,
                "xsl:document", instruction.Location);
            _sink.RawText(textContent);
            // A non-empty document node is significant for where-populated tracking
            MarkContentProduced();
        }

        // §5.7.1: a transmitted document node breaks the atomic run — the next sibling item does
        // not separate from this document's trailing atomic content.
        if (transmit)
            _lastResultWasAtomic = false;
    }


    private PathPattern CreateElementCountPattern(XdmElement elem)
    {
        var nsUri = _nodeStore?.GetNamespaceUri(elem.Namespace);
        var nameTest = new NameTest { LocalName = elem.LocalName, NamespaceUri = nsUri };
        if (!string.IsNullOrEmpty(nsUri) && _nodeStore != null)
            nameTest.ResolveNamespace(_nodeStore.InternNamespace);
        return new PathPattern
        {
            Steps = [new PatternStep { Axis = Axis.Child, NodeTest = nameTest }]
        };
    }


    public override async ValueTask CreateLiteralElementAsync(XsltLiteralResultElement instruction)
    {
        if (_recursionDepth >= MaxRecursionDepth)
            return;
        _recursionDepth++;
        try
        {
            await CreateLiteralElementCoreAsync(instruction).ConfigureAwait(false);
        }
        finally
        {
            _recursionDepth--;
        }
    }


    private async ValueTask CreateLiteralElementCoreAsync(XsltLiteralResultElement instruction)
    {
        // Inside attribute/comment/PI content, suppress element tags and pass through
        // only text content (atomization per XSLT spec 5.7.2)
        if (_textContentDepth > 0)
        {
            // When collecting simple content items, atomized elements are separate items
            if (_collectTextAsSequenceItems && _sequenceAccumulator != null && _serializingElementDepth == 0)
            {
                var pending = _output.ToString();
                if (pending.Length > 0)
                {
                    AppendToSeqAccumulator(new Xdm.TextNodeItem(pending));
                    _output.Clear();
                }
                _collectTextAsSequenceItems = false;
                try
                {
                    await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    _collectTextAsSequenceItems = true;
                }
                var elementText = _output.ToString();
                _output.Clear();
                if (elementText.Length > 0)
                    AppendToSeqAccumulator(elementText);
                return;
            }
            // Simple content construction: space between items from different nodes
            // (only for comment/PI content; attribute content uses empty-string separator)
            if (_attributeContentDepth == 0 && _output.Length > 0)
                _sink.RawText(" ");
            await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
            if (_attributeContentDepth == 0)
                _simpleContentLastWasNode = true;
            return;
        }

        // Build a prefix→URI map from the instruction's namespace declarations for alias resolution
        var prefixToUri = new Dictionary<string, string>();
        foreach (var (p, u) in instruction.NamespaceDeclarations)
            prefixToUri[p] = u;

        // xsl:namespace-alias is LOCAL to its declaring package (XSLT 3.0 §3.6.7 / §11.1.2):
        // it rewrites only literal result elements produced by code in the SAME package.
        // A literal element constructed while executing a used package's component must be
        // aliased by THAT package's declarations, not the principal's (use-package-103 /
        // use-package-108b). Non-package stylesheets are unaffected: CurrentComponentPackage
        // is null for principal-owned code, so the principal's merged map is used as before.
        var nsAliases = CurrentComponentPackage()?.NamespaceAliases ?? _stylesheet.NamespaceAliases;

        // Apply namespace aliases to element name
        var elemPrefix = instruction.Name.Prefix;
        var elemLocalName = instruction.Name.LocalName;
        if (nsAliases.Count > 0)
        {
            // Determine the element's current namespace URI
            string elemNsUri;
            if (elemPrefix != null)
                elemNsUri = prefixToUri.GetValueOrDefault(elemPrefix, "");
            else
                elemNsUri = prefixToUri.GetValueOrDefault("", ""); // Default namespace or empty

            if (nsAliases.TryGetValue(elemNsUri, out var elemAlias))
                elemPrefix = string.IsNullOrEmpty(elemAlias.ResultPrefix) ? null : elemAlias.ResultPrefix;
        }

        var elemName = elemPrefix != null
            ? $"{elemPrefix}:{elemLocalName}"
            : elemLocalName;

        // Collect all attributes in a dictionary (last-wins deduplication)
        var attributes = new Dictionary<string, string>();

        // Build set of prefixes in use by the element and its attributes (using aliased prefixes)
        var usedPrefixes = new HashSet<string>();
        if (elemPrefix != null)
            usedPrefixes.Add(elemPrefix);
        else
            usedPrefixes.Add(""); // default namespace is in use

        // Add attribute prefixes BEFORE processing namespace declarations so that
        // exclude-result-prefixes does not remove namespaces needed by attributes
        foreach (var (attrName, _) in instruction.Attributes)
        {
            var aPrefix = attrName.Prefix;
            if (aPrefix != null && prefixToUri.TryGetValue(aPrefix, out var aNsUri)
                && nsAliases.TryGetValue(aNsUri, out var aAlias))
            {
                usedPrefixes.Add(string.IsNullOrEmpty(aAlias.ResultPrefix) ? "" : aAlias.ResultPrefix);
            }
            else if (aPrefix != null)
                usedPrefixes.Add(aPrefix);
        }

        // Pre-compute namespace bindings for this element (so children can see them in scope)
        // Apply namespace aliases: transform URIs and prefixes
        var nsBindings = new Dictionary<string, string>();
        foreach (var (prefix, uri) in instruction.NamespaceDeclarations)
        {
            var effectiveUri = uri;
            var effectivePrefix = prefix;

            // Apply namespace alias if this URI is aliased
            if (nsAliases.TryGetValue(uri, out var alias))
            {
                effectiveUri = alias.ResultUri;
                effectivePrefix = alias.ResultPrefix;
            }

            // XSLT spec: A namespace node whose string value is a zero-length string is not created.
            // xmlns="" on an LRE does not produce a namespace node; it simply removes the default namespace.
            if (string.IsNullOrEmpty(effectiveUri))
                continue;
            // Skip XSLT namespace unless it's the result of a namespace alias
            if (effectiveUri == "http://www.w3.org/1999/XSL/Transform"
                && !nsAliases.TryGetValue(uri, out _))
                continue;
            if (effectiveUri == "http://www.w3.org/XML/1998/namespace")
                continue;
            // Skip the original stylesheet URI that was aliased (it's replaced by the result)
            if (uri != effectiveUri && uri == "http://www.w3.org/1999/XSL/Transform")
                continue;

            // A namespace that is the RESULT namespace of an xsl:namespace-alias must appear
            // in the result even if its prefix is in exclude-result-prefixes (XSLT 3.0 §11.1.3;
            // use-package-108 / use-package-108b, W3C bug fix 2019-03-05). Exempt any binding
            // whose (effective) URI is an alias result namespace from exclusion.
            var isAliasResultNs = nsAliases.Count > 0
                && nsAliases.Values.Any(a => a.ResultUri == effectiveUri);
            // Check exclude-result-prefixes using the ORIGINAL prefix (from stylesheet)
            var isInUse = usedPrefixes.Contains(effectivePrefix) || isAliasResultNs;
            if (!isInUse && (
                instruction.ExcludeResultPrefixes.Contains("#all") ||
                (!string.IsNullOrEmpty(prefix) && (_stylesheet.ExcludeResultPrefixes.Contains(prefix) || instruction.ExcludeResultPrefixes.Contains(prefix))) ||
                (string.IsNullOrEmpty(prefix) && (_stylesheet.ExcludeResultPrefixes.Contains("#default") || instruction.ExcludeResultPrefixes.Contains("#default"))) ||
                (!string.IsNullOrEmpty(prefix) && _stylesheet.ExtensionElementPrefixes.Contains(prefix))))
                continue;

            nsBindings[effectivePrefix] = effectiveUri;
        }

        // Ensure the element's aliased namespace is declared if not already present
        if (nsAliases.Count > 0 && elemPrefix != null)
        {
            // The element uses an aliased prefix — make sure that prefix → URI binding exists
            string elemNsUri;
            if (instruction.Name.Prefix != null)
                elemNsUri = prefixToUri.GetValueOrDefault(instruction.Name.Prefix, "");
            else
                elemNsUri = prefixToUri.GetValueOrDefault("", "");

            if (nsAliases.TryGetValue(elemNsUri, out var eAlias)
                && !nsBindings.ContainsKey(eAlias.ResultPrefix))
            {
                nsBindings[eAlias.ResultPrefix] = eAlias.ResultUri;
            }
        }

        // Push effective version and collation if set on this element
        // (must be before AVT evaluation so BC mode / collation affects attribute values)
        if (instruction.Version != null)
            _effectiveVersionStack.Push(instruction.Version);
        if (instruction.DefaultCollation != null)
            _defaultCollationStack.Push(instruction.DefaultCollation);

        // 1. Apply use-attribute-sets FIRST (lowest priority - can be overridden)
        if (instruction.UseAttributeSets.Count > 0)
        {
            var attrSetParts = new StringBuilder();
            await ApplyAttributeSetsAsync(instruction.UseAttributeSets, attrSetParts).ConfigureAwait(false);
            ParseAttributeString(attrSetParts.ToString(), attributes);
        }

        // 2. Inline attributes (override attribute-set values)
        foreach (var (name, avt) in instruction.Attributes)
        {
            var value = await EvaluateAvtAsync(avt).ConfigureAwait(false);
            // Apply namespace alias to attribute prefix
            var attrPrefix = name.Prefix;
            if (attrPrefix != null && prefixToUri.TryGetValue(attrPrefix, out var attrNsUri)
                && nsAliases.TryGetValue(attrNsUri, out var attrAlias))
            {
                attrPrefix = string.IsNullOrEmpty(attrAlias.ResultPrefix) ? null : attrAlias.ResultPrefix;
            }
            var attrName = attrPrefix != null
                ? $"{attrPrefix}:{name.LocalName}"
                : name.LocalName;
            attributes[attrName] = EscapeAttributeValue(value);
        }

        // 3. xsl:attribute instructions in body (highest priority)
        // Push namespace scope before processing body so children can see our ns bindings
        _collectedAttributesStack.Push(new StringBuilder());

        var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
        var savedLogicalStart = _outputLogicalStart;
        _outputLogicalStart = _output.Length;
        var savedLastAtomic = _lastResultWasAtomic;
        _lastResultWasAtomic = false;

        // Track element name (with its EXPANDED namespace) for CDATA section serialization.
        // instruction.Name only carries the source prefix — its NamespaceId is unresolved — so
        // resolve the element's serialized namespace URI (applying any namespace-alias) and intern
        // it, giving cdata-section-elements a correct expanded-name comparison. (decl/output-0138.)
        var cdataNsUri = instruction.SourceNamespaceName;
        if (nsAliases.Count > 0 && !string.IsNullOrEmpty(cdataNsUri)
            && nsAliases.TryGetValue(cdataNsUri, out var cdataAlias))
            cdataNsUri = cdataAlias.ResultUri;
        var cdataNsId = string.IsNullOrEmpty(cdataNsUri)
            ? NamespaceId.None
            : StylesheetParser.ResolveNamespaceUri(cdataNsUri);
        _outputElementStack.Push(new QName(cdataNsId, instruction.Name.LocalName, instruction.Name.Prefix));
        // Track whether element is in a namespace (for XTDE0440)
        var elemHasNs = elemPrefix != null
            || (nsBindings.TryGetValue("", out var defNs) && !string.IsNullOrEmpty(defNs))
            || GetInScopeDefaultNamespace() != null;
        _outputElementHasNsStack.Push(elemHasNs);
        _outputElementIsLreStack.Push(true); // literal result element
        _xslNamespaceBindings.Push(new Dictionary<string, string>());

        // inherit-namespaces="no" on parent: add default ns undeclaration so children
        // don't inherit the parent's xmlns="..." in the serialized output
        if (_forceDefaultNsUndeclaration && !nsBindings.ContainsKey("")
            && GetInScopeDefaultNamespace() != null)
        {
            nsBindings[""] = "";
        }

        PushOutputNsScope(nsBindings);
        PushOutputNsScope(new Dictionary<string, string>()); // Separate scope for attribute-emitted ns decls
        PushScope(); // Variables declared inside this element should be scoped to this element
        var savedSeqAccum3 = _sequenceAccumulator;
        _sequenceAccumulator = null; // Suspend accumulation inside element content
        _serializingElementDepth++;
        Dictionary<string, string>? xslNsBindingsForFixup = null;
        // Save/set inherit-namespaces flag for children
        var savedForceNsUndecl3 = _forceDefaultNsUndeclaration;
        _forceDefaultNsUndeclaration = instruction.InheritNamespaces == false
            && nsBindings.ContainsKey("") && !string.IsNullOrEmpty(nsBindings.GetValueOrDefault(""));
        var savedInheritNsNoElem = _inheritNamespacesNo;
        _inheritNamespacesNo = instruction.InheritNamespaces == false;
        // Push xml:base onto static base URI stack so document()/resolve-uri() resolve against it
        if (instruction.StaticBaseUri != null)
            _staticBaseUriStack.Push(instruction.StaticBaseUri);
        // SP-B slice 4: open THIS literal result element on the active constructor BEFORE content
        // (constructor stays active — no suspend), so child text/comment/PI/nested elements build
        // natively in document order. Sealed after content in TcFinishElement. Uses the pre-fixup
        // elemName / post-alias namespace URI (cdataNsUri); a post-content prefix fixup is mirrored
        // onto the open frame there. Mirrors CreateElementCoreAsync.
        var tcForThisElement = _activeTreeConstructor;
        if (tcForThisElement != null)
            TcOpenElement(tcForThisElement, elemName, cdataNsUri);
        try
        {
            await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
        }
        finally
        {
            if (instruction.StaticBaseUri != null)
                _staticBaseUriStack.Pop();
            _forceDefaultNsUndeclaration = savedForceNsUndecl3;
            _inheritNamespacesNo = savedInheritNsNoElem;
            _serializingElementDepth--;
            _sequenceAccumulator = savedSeqAccum3;
            PopScope();
            PopOutputNsScope(); // attribute ns decls scope
            PopOutputNsScope(); // element ns scope
            if (instruction.DefaultCollation != null)
                _defaultCollationStack.Pop();
            if (instruction.Version != null)
                _effectiveVersionStack.Pop();
            _outputElementStack.Pop();
            _outputElementHasNsStack.Pop();
            _outputElementIsLreStack.Pop();
            // Save xsl:namespace bindings for namespace fixup before popping
            xslNsBindingsForFixup = _xslNamespaceBindings.Peek().Count > 0
                ? new Dictionary<string, string>(_xslNamespaceBindings.Peek())
                : null;
            _xslNamespaceBindings.Pop();
        }

        var collectedAttrs = _collectedAttributesStack.Pop();
        var content = savedScope.GetWritten();
        savedScope.Dispose();
        _outputLogicalStart = savedLogicalStart;
        // After producing an element node, reset atomic state (nodes break atomic adjacency)
        _lastResultWasAtomic = false;

        // Parse collected attributes and add to dictionary (overriding earlier values)
        ParseAttributeString(collectedAttrs.ToString(), attributes);

        // Namespace fixup: if xsl:namespace redefined the element's prefix to a different URI,
        // either do namespace fixup (if prefix was excluded) or throw XTDE0430 (XSLT 3.0 §11.7)
        if (xslNsBindingsForFixup != null && elemPrefix != null)
        {
            if (xslNsBindingsForFixup.TryGetValue(elemPrefix, out var xslUri))
            {
                // Get the element's original namespace URI
                var elemNsUri = nsBindings.GetValueOrDefault(elemPrefix, "");
                if (elemNsUri.Length == 0)
                {
                    // The prefix may have been excluded; look up the original declaration
                    foreach (var (p, u) in instruction.NamespaceDeclarations)
                    {
                        if (p == elemPrefix) { elemNsUri = u; break; }
                    }
                    // Apply namespace alias if applicable
                    if (nsAliases.TryGetValue(elemNsUri, out var alias2))
                        elemNsUri = alias2.ResultUri;
                }
                if (xslUri != elemNsUri)
                {
                    // Check if the prefix was in exclude-result-prefixes
                    var prefixWasExcluded = instruction.ExcludeResultPrefixes.Contains(elemPrefix)
                        || instruction.ExcludeResultPrefixes.Contains("#all")
                        || _stylesheet.ExcludeResultPrefixes.Contains(elemPrefix);
                    if (!prefixWasExcluded)
                        throw Error($"XTDE0430: Namespace node with prefix '{elemPrefix}' and URI '{xslUri}' conflicts with the element's own namespace binding '{elemPrefix}' → '{elemNsUri}'");

                    var newPfx = GenerateUniquePrefix(elemPrefix);
                    elemName = $"{newPfx}:{elemLocalName}";
                    // Add new prefix → element namespace to nsBindings; remove old conflicting entry
                    nsBindings.Remove(elemPrefix);
                    nsBindings[newPfx] = elemNsUri;
                    // Remove the old xmlns:PREFIX from attributes dict — it will be emitted from nsBindings
                    // The xsl:namespace's binding stays in attributes as xmlns:ORIGINAL_PREFIX
                    attributes.Remove($"xmlns:{newPfx}"); // Ensure no conflict with new prefix
                }
            }
        }

        // SP-B: when a tree constructor is active, mirror the exact namespace/attribute
        // decisions the string path makes below into typed lists, then build a byte-identical
        // XDM element via the constructor after the string element is fully emitted. Same
        // machinery as CreateElementCoreAsync (slice 1); only the emission source differs.
        List<(string Prefix, NamespaceId Ns)>? tcNsDecls = tcForThisElement != null ? new() : null;
        List<(NamespaceId Ns, string Local, string? Prefix, string Value)>? tcAttrs = tcForThisElement != null ? new() : null;
        var tcAbort = false;

        // Build element output
        _sink.StartElementOpen(elemName);

        // Namespace declarations — emit only those not already in scope from ancestor elements
        foreach (var (prefix, uri) in nsBindings)
        {
            // Skip if already declared by an ancestor with same prefix→URI
            if (IsNamespaceInScope(prefix, uri))
                continue;

            // Skip xmlns="" (default namespace undeclaration) if there's no non-empty
            // default namespace in scope — the undeclaration would be redundant
            if (string.IsNullOrEmpty(prefix) && string.IsNullOrEmpty(uri)
                && GetInScopeDefaultNamespace() == null)
                continue;

            _sink.Namespace(prefix, uri);
            tcNsDecls?.Add((prefix, string.IsNullOrEmpty(uri) ? NamespaceId.None : _nodeStore!.InternNamespace(uri)));
        }

        // Default namespace undeclaration: if this element is in the null namespace
        // (no prefix, no default ns declared), but the output tree has a non-empty
        // default namespace in scope, emit xmlns="" to undeclare it.
        if (elemPrefix == null && !nsBindings.ContainsKey(""))
        {
            var parentDefaultNs = GetInScopeDefaultNamespace();
            if (parentDefaultNs != null)
            {
                _sink.Namespace("", "");
                tcNsDecls?.Add(("", NamespaceId.None));
            }
        }
        // inherit-namespaces="no": also undeclare inherited default namespace
        else if (instruction.InheritNamespaces == false && !nsBindings.ContainsKey(""))
        {
            var inheritedDefaultNs = GetInScopeDefaultNamespace();
            if (inheritedDefaultNs != null)
            {
                _sink.Namespace("", "");
                tcNsDecls?.Add(("", NamespaceId.None));
            }
        }

        // Emit deduplicated attributes
        foreach (var (attrName, attrValue) in attributes)
        {
            _output.Append(' ');
            _output.Append(attrName);
            _output.Append("=\"");
            _output.Append(attrValue);
            _output.Append('"');
            if (tcAttrs != null)
                RecordTreeAttribute(attrName, attrValue, nsBindings, tcNsDecls!, tcAttrs, ref tcAbort);
        }

        if (content.Length > 0)
        {
            _sink.StartElementClose(false);
            _sink.RawText(content);
            _sink.EndElement(elemName);
        }
        else
        {
            _sink.StartElementClose(true);
        }

        // LRE element is "significant" (populated) only if it has child content
        // (attributes alone don't count as "populated" per XSLT 3.0 spec)
        if (content.Length > 0)
            MarkContentProduced();

        // SP-B slice 4: seal the element opened before content; children built natively in order.
        // elemName carries any post-content prefix fixup applied to the open frame in TcFinishElement.
        if (tcForThisElement is { } tc)
            TcFinishElement(tc, elemName, tcNsDecls!, tcAttrs!, tcAbort);
    }

}
