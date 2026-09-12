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
    /// Matches a streaming node against templates in the given mode and executes
    /// the matched template (or built-in rules). Called by StreamingXmlProcessor
    /// for each node encountered during streaming execution.
    /// </summary>
    /// <summary>
    /// Writes a closing tag for a streaming element whose start tag was deferred.
    /// Called by StreamingXmlProcessor on EndElement events.
    /// </summary>
    internal void WriteStreamingEndTag(string qname)
    {
        _sink.EndElement(qname);
    }


    private void SerializeElement(XdmElement elem)
    {
        var nsUri = _nodeStore?.GetNamespaceUri(elem.Namespace) ?? "";
        var prefix = elem.Prefix ?? "";
        var localName = elem.LocalName;
        var qname = !string.IsNullOrEmpty(prefix) ? $"{prefix}:{localName}" : localName;

        _sink.StartElementOpen(qname);

        // Namespace declarations
        foreach (var nsDecl in elem.NamespaceDeclarations)
        {
            var nsDeclUri = _nodeStore?.GetNamespaceUri(nsDecl.Namespace) ?? "";
            var nsDeclPrefix = nsDecl.Prefix ?? "";
            _sink.Namespace(nsDeclPrefix, nsDeclUri);
        }

        // Attributes and then child nodes
        if (_nodeStore != null)
        {
            // Element attributes live in elem.Attributes, NOT in GetChildren(elem)
            // (which yields only element/text/comment/PI children). Emit them here so a
            // deep-copied element retains its attributes (e.g. <record id="a">). The
            // legacy "child is XdmAttribute" guard below is kept defensively for any
            // store shape that threads attributes through the child list.
            foreach (var attr in _nodeStore.GetAttributes(elem))
            {
                var attrPrefix0 = attr.Prefix ?? "";
                var attrName0 = !string.IsNullOrEmpty(attrPrefix0)
                    ? $"{attrPrefix0}:{attr.LocalName}"
                    : attr.LocalName;
                _sink.Attribute(attrName0, attr.Value);
            }
            var childElements = new List<XdmNode>();
            foreach (var child in _nodeStore.GetChildren(elem))
            {
                if (child is XdmAttribute attr)
                {
                    var attrPrefix = attr.Prefix ?? "";
                    var attrName = !string.IsNullOrEmpty(attrPrefix)
                        ? $"{attrPrefix}:{attr.LocalName}"
                        : attr.LocalName;
                    _sink.Attribute(attrName, attr.Value);
                }
                else
                {
                    childElements.Add(child);
                }
            }
            _sink.StartElementClose(false);
            foreach (var child in childElements)
            {
                switch (child)
                {
                    case XdmElement childElem:
                        SerializeElement(childElem);
                        break;
                    case XdmText text:
                        WriteText(text.Value, false);
                        break;
                    case XdmComment comment:
                        _sink.Comment(comment.Value);
                        break;
                    case XdmProcessingInstruction pi:
                        // Note: deep-copy path intentionally does not apply EscapePIValue
                        // (matches pre-existing behavior at this call site) — use RawText,
                        // not _sink.ProcessingInstruction, to keep byte-identical output.
                        _sink.RawText("<?" + pi.Target + (string.IsNullOrEmpty(pi.Value) ? "" : " " + pi.Value) + "?>");
                        break;
                }
            }
        }
        else
        {
            _sink.StartElementClose(false);
        }

        _sink.EndElement(qname);
    }


    /// <summary>
    /// Writes text to the result tree, XML-escaping it — EXCEPT while it is being collected as
    /// an attribute VALUE, where it must be written verbatim.
    /// </summary>
    /// <remarks>
    /// <c>xsl:attribute</c> with a sequence-constructor body runs that body into the ordinary
    /// output buffer and then takes the written span as the attribute's string value. Text
    /// escaped on the way in is therefore escaped a SECOND time when the finished value is
    /// serialized, so <c>&amp;</c> came out as <c>&amp;amp;amp;</c> and a <c>&gt;</c> written by
    /// <c>xsl:text</c> came out as <c>&amp;amp;gt;</c>.
    ///
    /// This corrupts data silently: the attribute is well-formed, just wrong. XSpec's own
    /// compiler builds a select attribute this way — <c>&lt;xsl:attribute name="select"&gt;</c>
    /// around an XPath containing <c>=&gt;</c> — and the generated stylesheet then failed to
    /// parse with "token recognition error at: '&amp;'", because the XPath lexer was handed a
    /// literal <c>&amp;gt;</c>.
    ///
    /// The select and AVT forms of xsl:attribute were unaffected: they produce the value as a
    /// string directly and never route it through the escaping output path.
    /// </remarks>
    private void EmitText(string value)
    {
        if (_attributeContentDepth > 0)
            _sink.RawText(value);
        else
            _sink.Text(value);
    }


    public override void WriteText(string value, bool disableOutputEscaping)
    {
        // SP-B slice 4: the node model stores raw (unescaped) text; capture the value before any
        // text-output-mode sentinel mangling below so the constructor gets the true text.
        var rawTextValue = value;
        // XTDE1490: Reject writes to primary output after it was claimed by result-document href=""
        if (_primaryOutputClaimedByResultDocument && _resultDocumentRedirectDepth == 0
            && _temporaryOutputDepth == 0 && _textContentDepth == 0
            && !string.IsNullOrWhiteSpace(value))
            throw Error("XTDE1490: Cannot write to the primary output destination — it has already been written by an explicit xsl:result-document");

        // In text output mode (method="text" result-document), protect text content from
        // StripXmlMarkup by sentinel-escaping < > & chars. Only actual XML markup (generated
        // by LRE/xsl:element which writes directly to _output) will be stripped.
        if (_textOutputModeDepth > 0 && (value.Contains('<', StringComparison.Ordinal)
            || value.Contains('>', StringComparison.Ordinal) || value.Contains('&', StringComparison.Ordinal)))
        {
            value = value.Replace("&", "\uFDD2", StringComparison.Ordinal)
                         .Replace("<", "\uFDD0", StringComparison.Ordinal)
                         .Replace(">", "\uFDD1", StringComparison.Ordinal);
        }

        // When collecting simple content items (§5.7.2), redirect text to accumulator
        // as TextNodeItem to distinguish from atomic strings. Takes priority over
        // _textContentDepth since we need proper item interleaving in comment/PI bodies.
        if (_collectTextAsSequenceItems && _serializingElementDepth == 0
            && _sequenceAccumulator != null)
        {
            AccumulateTextItem(value);
        }
        else if ((disableOutputEscaping && _temporaryOutputDepth == 0) || _textContentDepth > 0)
        {
            // Don't XML-escape when inside attribute/comment/PI body collection,
            // as the collected text is handled by the parent instruction
            // Per XSLT spec, disable-output-escaping is ignored when writing to a temporary tree
            // Simple content: insert space when transitioning from node-contributed text to new text
            // (only for comment/PI content; attribute content uses empty-string separator)
            if (_simpleContentLastWasNode && _textContentDepth > 0 && _attributeContentDepth == 0 && _output.Length > 0)
                _sink.RawText(" ");
            if (_attributeContentDepth == 0)
                _simpleContentLastWasNode = false;
            _sink.RawText(value);
        }
        else if (IsInCdataSectionElement())
        {
            // Wrap in CDATA section — escape any embedded "]]>" sequences
            _sink.RawText("<![CDATA[" + value.Replace("]]>", "]]]]><![CDATA[>", StringComparison.Ordinal) + "]]>");
        }
        else
        {
            EmitText(value);
        }

        // SP-B slice 4: route element-content text into the active constructor as a child text
        // node (raw value; the constructor coalesces adjacent runs to match the reparse). Excluded:
        // comment/PI/attribute body text (_textContentDepth > 0), which the parent instruction
        // consumes, and the simple-content accumulator branch above, which never reaches _output.
        // Also excluded while an xsl:copy-of of source nodes serializes after its clones were
        // already routed into the constructor (_suppressTcIncomplete): the copied subtrees' own
        // descendant text belongs INSIDE those cloned nodes, so re-routing it here would flatten
        // it into the enclosing frame as a spurious extra child (SP-C slice 2).
        if (_activeTreeConstructor is { } wtc && _textContentDepth == 0 && !_suppressTcIncomplete
            && !(_collectTextAsSequenceItems && _serializingElementDepth == 0 && _sequenceAccumulator != null))
        {
            wtc.AppendText(rawTextValue);
        }

        // Non-whitespace text counts as "populated" for xsl:where-populated,
        // but only when at the element content level (not inside attribute/comment/PI body).
        // Use XML whitespace definition (only #x20, #x9, #xD, #xA), not .NET's broader one.
        if (IsTrackingPopulated && _textContentDepth == 0 && !IsXmlWhitespaceOnly(value))
        {
            MarkContentProduced();
        }

        // Text output (even zero-length) breaks adjacent atomic value chain.
        // Per XSLT 3.0 spec, text nodes in a sequence (even empty from xsl:text/xsl:value-of)
        // break adjacency between atomic values, preventing space insertion.
        // Skip when _preserveAtomicState > 0 (Phase 2 of on-non-empty when !wasPopulated):
        // non-conditional instructions producing empty text shouldn't break the chain
        // between the previous iteration's last atomic and on-empty's atomic output.
        if (_preserveAtomicState == 0)
            _lastResultWasAtomic = false;
    }


    /// <summary>
    /// Adds a text node to the sequence being built and, at the top level of a function body,
    /// also writes it to <c>_output</c>. Returns whether it wrote to <c>_output</c>.
    /// </summary>
    /// <remarks>
    /// A function's result is assembled from BOTH channels: <c>_output</c> carries elements and
    /// text in source order, and the assembly skips accumulated TextNodeItems as duplicates of
    /// that text. So a text node that reaches only the accumulator is dropped. WriteTextItem
    /// wrote both; WriteText's text-as-items branch — which xsl:copy-of of a text node reaches —
    /// wrote only the accumulator. So a function returning <c>(text, element)</c> lost the text:
    /// <c>f:id($d/r/node())</c> over <c>&lt;r&gt; inner &lt;b/&gt;&lt;/r&gt;</c> gave back only
    /// the element, which is why W3C function-1022 found an index where the spec finds none.
    /// One helper now, so the two cannot disagree again.
    /// </remarks>
    private bool AccumulateTextItem(string value)
    {
        AppendToSeqAccumulator(new Xdm.TextNodeItem(value));
        // In function bodies at the top level (not inside value-of, attribute,
        // comment/PI content), also write escaped text to _output so that
        // text and LRE elements preserve their source order when the function
        // result is assembled from both _sequenceAccumulator and _output.
        if (!InFunctionBodyProper || _textContentDepth != 0)
            return false;
        EmitText(value);
        // This item now OWNS the text just written. Without this the weave emits the
        // item and then the same text again as a trailing output chunk.
        if (_currentAsBodyCapture is { } cap
            && ReferenceEquals(_sequenceAccumulator, cap.Accumulator)
            && cap.ConsumedTo.Count > 0)
        {
            cap.ConsumedTo[^1] = _output.Length - cap.OutputBaseLen;
        }
        return true;
    }

    public override void WriteTextItem(string value)
    {
        // If sequence accumulation is active, add as a TextNodeItem marker
        // to distinguish text nodes from atomic strings (for §5.7.2 processing)
        var emittedToOutput = false;
        if (_sequenceAccumulator != null && !InTreeBody)
        {
            emittedToOutput = AccumulateTextItem(value);
        }
        else
        {
            EmitText(value);
            emittedToOutput = true;
        }

        // SP-B slice 4: when this text reached the output buffer as element content (not the
        // accumulator-only path, and not a comment/PI/attribute body), route it into the active
        // constructor as a child text node. Text-value-templates (expand-text {…}) and value-of
        // in some contexts emit through WriteTextItem, so the WriteText routing alone would miss
        // an LRE's own textual content (e.g. <field>{.}</field>).
        if (emittedToOutput && _textContentDepth == 0 && !_suppressTcIncomplete && _activeTreeConstructor is { } wtc)
            wtc.AppendText(value);
        // Text output (even zero-length) breaks adjacent atomic value chain.
        // Per XSLT 3.0 spec, text nodes in a sequence (even empty from xsl:text/xsl:value-of)
        // break adjacency between atomic values, preventing space insertion.
        if (_preserveAtomicState == 0)
            _lastResultWasAtomic = false;
    }


    private void SerializeNode(object node, bool copyNamespaces = true, bool faithfulNamespaces = false)
    {
        // SP-B slice 4: node-to-string serialization (xsl:copy-of, sequence-of-nodes, etc.)
        // writes markup straight to the sink without routing into the active constructor, so the
        // live tree would be missing these children. Mark the body incomplete so the differential
        // skips it rather than reporting a false divergence. (Element-content level only; inside a
        // comment/PI/attribute body this contributes to an atomized string, not a child node.)
        if (_textContentDepth == 0)
            MarkTcIncompleteIfActive();
        switch (node)
        {
            case XdmDocument doc:
                if (_nodeStore != null)
                {
                    foreach (var child in _nodeStore.GetChildren(doc))
                        SerializeNode(child, copyNamespaces, faithfulNamespaces);
                }
                break;

            case XdmElement elem:
            {
                var eName = !string.IsNullOrEmpty(elem.Prefix) ? $"{elem.Prefix}:{elem.LocalName}" : elem.LocalName;
                _sink.StartElementOpen(eName);

                // Serialize namespace declarations, skipping those already in scope
                var nsBindings = new Dictionary<string, string>();
                foreach (var nsDecl in elem.NamespaceDeclarations)
                {
                    var uri = _nodeStore?.GetNamespaceUri(nsDecl.Namespace) ?? "";
                    var prefix = nsDecl.Prefix ?? "";

                    // When copy-namespaces="no", only emit namespaces that are in use
                    // (element prefix or attribute prefix). A dropped namespace must NOT be
                    // recorded in nsBindings: nsBindings is pushed as the in-scope namespace
                    // context for descendants, so recording a namespace we never emitted would
                    // falsely tell a descendant it is already in scope — causing the descendant
                    // to skip its own (required) declaration and silently adopt the wrong
                    // namespace. (copy-4901 / copy-5101: a deep-copy descendant whose default
                    // namespace is inherited lost it entirely.)
                    if (!copyNamespaces)
                    {
                        var isUsedByElement = prefix == (elem.Prefix ?? "");
                        var isUsedByAttr = false;
                        if (_nodeStore != null)
                        {
                            foreach (var a in _nodeStore.GetAttributes(elem))
                            {
                                if (a.Prefix == prefix && !string.IsNullOrEmpty(prefix))
                                {
                                    isUsedByAttr = true;
                                    break;
                                }
                            }
                        }
                        if (!isUsedByElement && !isUsedByAttr)
                            continue;
                    }

                    nsBindings[prefix] = uri;

                    // Skip if already declared by an ancestor with same prefix→URI
                    if (IsNamespaceInScope(prefix, uri))
                        continue;

                    _sink.Namespace(prefix, uri);
                }

                // Default namespace undeclaration: if the element has no default namespace
                // binding in its namespace declarations, but the output context has a non-empty
                // default namespace, emit xmlns="" to undeclare it.
                if (!nsBindings.ContainsKey(""))
                {
                    var needsUndeclaration = false;
                    if (string.IsNullOrEmpty(elem.Prefix))
                    {
                        // Unprefixed element in null namespace — always undeclare
                        var elemNsUri = _nodeStore?.GetNamespaceUri(elem.Namespace) ?? "";
                        if (string.IsNullOrEmpty(elemNsUri))
                            needsUndeclaration = true;
                    }
                    else if (faithfulNamespaces)
                    {
                        // Prefixed element during faithful document copy — the source had
                        // xmlns="" (which GetNamespacesInScope(All) omits during XDM parsing).
                        // Check if the XDM parent element has a default namespace that this
                        // element lacks, indicating we need to preserve the undeclaration.
                        if (_nodeStore != null)
                        {
                            var parentNode = elem.Parent.HasValue && elem.Parent.Value != NodeId.None
                                ? _nodeStore.GetNode(elem.Parent.Value) : null;
                            if (parentNode is XdmElement parentElem)
                            {
                                foreach (var nsDecl in parentElem.NamespaceDeclarations)
                                {
                                    if (string.IsNullOrEmpty(nsDecl.Prefix))
                                    {
                                        var parentNsUri = _nodeStore.GetNamespaceUri(nsDecl.Namespace) ?? "";
                                        if (!string.IsNullOrEmpty(parentNsUri))
                                            needsUndeclaration = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (needsUndeclaration)
                    {
                        var parentDefaultNs = GetInScopeDefaultNamespace();
                        if (parentDefaultNs != null)
                        {
                            _sink.Namespace("", "");
                            nsBindings[""] = "";
                        }
                    }
                }

                // Namespace fixup: ensure the element's own prefix→URI is declared. When a
                // node is detached from its source tree (e.g. selected via xsl:sequence from
                // a variable) and serialized in a new context, the prefix binding from the
                // source's ancestor chain is not present in either the local declarations or
                // the output scope. Emit it now so re-parsing produces a well-formed document.
                {
                    var elemPrefixForFixup = elem.Prefix ?? "";
                    if (!nsBindings.ContainsKey(elemPrefixForFixup))
                    {
                        var elemNsUri = _nodeStore?.GetNamespaceUri(elem.Namespace) ?? "";
                        // Fixup covers both prefixed elements AND the default namespace (prefix "").
                        // The default case is essential for copy-namespaces="no": an unprefixed
                        // element whose namespace is INHERITED (not locally declared) — e.g. a deep
                        // copy descendant — would otherwise be emitted with no xmlns and wrongly
                        // adopt an ancestor's default namespace in the new output context.
                        if (!string.IsNullOrEmpty(elemNsUri) && !IsNamespaceInScope(elemPrefixForFixup, elemNsUri))
                        {
                            _sink.Namespace(elemPrefixForFixup, elemNsUri);
                            nsBindings[elemPrefixForFixup] = elemNsUri;
                        }
                    }
                }

                // Serialize attributes
                if (_nodeStore != null)
                {
                    foreach (var a in _nodeStore.GetAttributes(elem))
                    {
                        var aName = a.Prefix != null ? $"{a.Prefix}:{a.LocalName}" : a.LocalName;
                        _sink.Attribute(aName, a.Value);
                    }
                }

                // EMIT (temp-tree base-URI preservation): when serializing into a buffer that
                // will be reparsed and this element's source base URI differs from the enclosing
                // serialization context, append the sentinel xmlns + attribute to the start tag.
                // Only fires on subtree roots where the base changes; descendants inherit the
                // updated context and stay silent. Never fires for final-output serialization
                // (_tempTreeSerializeDepth == 0).
                var savedBaseContext = _serializeBaseContext;
                TryEmitBaseSentinel(elem, ref _serializeBaseContext);

                // Serialize children with namespace scope tracking
                var hasChildren = false;
                if (_nodeStore != null)
                {
                    var children = _nodeStore.GetChildren(elem).ToList();
                    if (children.Count > 0)
                    {
                        hasChildren = true;
                        _sink.StartElementClose(false);
                        PushOutputNsScope(nsBindings);
                        // Descendant text belongs INSIDE this element. Without the depth bump,
                        // a text child reaches OutputText at _serializingElementDepth == 0, and
                        // when a body is collecting text as separate sequence items (an untyped
                        // xsl:function, whose default return type item()* enables that) the text
                        // is diverted into the accumulator instead — so the element came back
                        // correctly named but empty. position-2201 / function-1022: an untyped
                        // function doing xsl:copy-of of an element that is itself part of a copy
                        // yielded <ir:base/> where <ir:base>4</ir:base> was required.
                        _serializingElementDepth++;
                        try
                        {
                            foreach (var child in children)
                                SerializeNode(child, copyNamespaces, faithfulNamespaces);
                        }
                        finally
                        {
                            _serializingElementDepth--;
                        }
                        PopOutputNsScope();
                        _sink.EndElement(eName);
                    }
                }
                if (!hasChildren)
                    _sink.StartElementClose(true);
                _serializeBaseContext = savedBaseContext;
                break;
            }

            case XdmAttribute attr:
            {
                // XTDE0420: Cannot add attribute to a document node
                if (_documentNodeDepth > 0 && !_attributeCollecting)
                {
                    DiagXtde0420("SerializeNode/XdmAttribute", $"{attr.NodeName}={attr.Value}");
                    throw new XsltException("XTDE0420: Cannot add an attribute node to a document node", _currentInstructionLocation);
                }
                // XTDE0410: Cannot add attribute after child content has been added
                // Inside xsl:where-populated, defer this check until after filtering
                if (_attributeCollecting && _output.Length > _outputLogicalStart && !IsBackwardsCompatible && _wherePopulatedDepth == 0)
                    throw new XsltException("XTDE0410: Cannot add an attribute to an element after children have been added", _currentInstructionLocation);

                var target = _attributeCollecting ? _collectedAttributes! : _output;
                var attrPrefix = attr.Prefix;
                var attrLocalName = attr.LocalName;

                // Namespace fixup: emit namespace declaration for prefixed attributes
                if (!string.IsNullOrEmpty(attrPrefix) && attrPrefix != "xml" && _sequenceAccumulator == null)
                {
                    var attrNsUri = _nodeStore?.GetNamespaceUri(attr.Namespace) ?? "";
                    if (!string.IsNullOrEmpty(attrNsUri))
                    {
                        if (!IsNamespaceInScope(attrPrefix, attrNsUri))
                        {
                            // Check for prefix clash: same prefix bound to different URI
                            if (IsPrefixInUse(attrPrefix))
                            {
                                attrPrefix = GenerateUniquePrefix(attrPrefix);
                            }
                            target.Append(" xmlns:");
                            target.Append(attrPrefix);
                            target.Append("=\"");
                            target.Append(EscapeAttributeValue(attrNsUri));
                            target.Append('"');
                            if (_outputNsScopes.Count > 0)
                                _outputNsScopes.Peek()[attrPrefix] = attrNsUri;
                        }
                    }
                }

                var aName = attrPrefix != null ? $"{attrPrefix}:{attrLocalName}" : attrLocalName;
                if (target == _output)
                {
                    _sink.Attribute(aName, attr.Value);
                }
                else
                {
                    target.Append(' ');
                    target.Append(aName);
                    target.Append("=\"");
                    target.Append(EscapeAttributeValue(attr.Value));
                    target.Append('"');
                }
                break;
            }

            case XdmNamespace nsNode:
            {
                // Per XSLT 3.0 §5.7.3.1, copying a namespace node adds the namespace
                // declaration to the containing element. Without this case, the node
                // fell through to default (WriteText(node.ToString())) which emitted
                // the URI as text content. Found while transpiling Docbook xform-locale.xsl,
                // whose `xsl:copy-of select="@*,namespace::*[…]"` poisoned every locale
                // template with a leading docbook-namespace text node.
                if (_documentNodeDepth > 0 && !_attributeCollecting)
                {
                    DiagXtde0420("SerializeNode/XdmNamespace(0440)", $"xmlns:{nsNode.Prefix}={nsNode.Uri}");
                    throw new XsltException("XTDE0440: Cannot add a namespace node to a document node", _currentInstructionLocation);
                }
                if (_attributeCollecting && _output.Length > _outputLogicalStart && !IsBackwardsCompatible && _wherePopulatedDepth == 0)
                    throw new XsltException("XTDE0410: Cannot add a namespace node to an element after children have been added", _currentInstructionLocation);

                var prefix = nsNode.Prefix ?? "";
                var uri = nsNode.Uri ?? "";

                // Skip the special "xml" prefix — it's always implicitly bound and must not be redeclared.
                if (prefix == "xml")
                    break;
                // Suppress redundant declarations already in the output namespace scope.
                if (IsNamespaceInScope(prefix, uri))
                    break;

                var target = _attributeCollecting ? _collectedAttributes! : _output;
                target.Append(" xmlns");
                if (!string.IsNullOrEmpty(prefix))
                {
                    target.Append(':');
                    target.Append(prefix);
                }
                target.Append("=\"");
                target.Append(EscapeAttributeValue(uri));
                target.Append('"');
                if (_outputNsScopes.Count > 0)
                    _outputNsScopes.Peek()[prefix] = uri;
                break;
            }

            case XdmText text:
                WriteText(text.Value, false);
                break;

            case XdmComment comment:
                _sink.Comment(comment.Value);
                break;

            case XdmProcessingInstruction pi:
                _sink.ProcessingInstruction(pi.Target, pi.Value);
                break;

            case ResultTreeFragment rtf:
                _sink.RawText(rtf.XmlContent);
                break;

            case System.Xml.Linq.XNode linqNode:
                _sink.RawText(linqNode.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
                break;

            case Xdm.TextNodeItem tni:
                WriteText(tni.Value, false);
                break;

            case string str:
                WriteText(str, false);
                break;

            case bool b:
                WriteText(b ? "true" : "false", false);
                break;

            case float f:
                WriteText(FormatFloat(f), false);
                break;

            case double d:
                WriteText(FormatDouble(d), false);
                break;

            case Xdm.DayTimeDuration dtd:
                WriteText(dtd.ToString(), false);
                break;

            default:
                WriteText(node.ToString() ?? "", false);
                break;
        }
    }


    /// <summary>
    /// SM-ctx (OP-bucket phase 1): evaluates a consuming simple-map RIGHT expression
    /// against the currently-bound (materialized) context item and emits its result to
    /// the active output exactly as <c>xsl:value-of</c>/<c>xsl:sequence</c> would — atomic
    /// results space-separated per XPath atomization (with cross-call continuity via
    /// <see cref="_lastResultWasAtomic"/> so successive matched items separate correctly),
    /// node results serialized. Called once per matched item by the streaming dispatch.
    /// </summary>
    /// <summary>
    /// ForExpr streaming (sx-ForExpr): emits one matched item of a streamable
    /// <c>for $x in CONSUMING-PATH return EXPR</c>. The matched element snapshot is the
    /// current context item; this evaluates the trailing grounding step
    /// (<see cref="ForEachSubscription.RangeBindExpression"/>) against it to produce the
    /// range variable's value, binds <see cref="ForEachSubscription.RangeVariable"/> in a
    /// fresh scope (iterating if the grounded value is a sequence, per XPath <c>for</c>
    /// semantics), evaluates the <c>return</c> expression
    /// (<see cref="ForEachSubscription.PerItemSelect"/>), and emits each result through the
    /// same atomic-spacing/serialization path as <see cref="EmitSimpleMapContextResultAsync"/>.
    /// </summary>
    internal async ValueTask EmitForExpressionResultAsync(ForEachSubscription sub)
    {
        var bindValue = await EvaluateAsync(sub.RangeBindExpression!).ConfigureAwait(false);

        // Normalize the grounded bind value to the sequence of items the range variable
        // iterates. A single atomic/node binds once; a sequence binds once per member.
        PushScope();
        try
        {
            if (bindValue is object?[] arr)
            {
                foreach (var member in arr)
                {
                    SetVariable(sub.RangeVariable!.Value, member);
                    await EmitForExpressionReturnAsync(sub.PerItemSelect!).ConfigureAwait(false);
                }
            }
            else if (bindValue is System.Collections.IEnumerable en && bindValue is not string
                && bindValue is not XdmNode && bindValue is not ResultTreeFragment)
            {
                foreach (var member in en)
                {
                    SetVariable(sub.RangeVariable!.Value, member);
                    await EmitForExpressionReturnAsync(sub.PerItemSelect!).ConfigureAwait(false);
                }
            }
            else
            {
                // Empty sequence (null) still iterates zero times under for semantics.
                if (bindValue != null)
                {
                    SetVariable(sub.RangeVariable!.Value, bindValue);
                    await EmitForExpressionReturnAsync(sub.PerItemSelect!).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            PopScope();
        }
    }


    /// <summary>
    /// Evaluates and emits a single <c>return</c> result of a streamable ForExpr, with the
    /// range variable already bound. Mirrors <see cref="EmitSimpleMapContextResultAsync"/>'s
    /// atomic-string spacing and node serialization.
    /// </summary>
    private async ValueTask EmitForExpressionReturnAsync(XQueryExpression returnExpr)
    {
        var result = await EvaluateAsync(returnExpr).ConfigureAwait(false);
        if (result == null)
            return;

        if (result is string str)
        {
            if (str.Length == 0 && _wherePopulatedDepth > 0)
                return;
            if (_lastResultWasAtomic && _attributeContentDepth == 0)
            {
                var sep = _itemSeparatorOverride ?? " ";
                WriteText(sep, false);
                if (_contentTrackingStack.Count > 0)
                    _separatorCharsWritten += sep.Length;
            }
            WriteText(str, false);
            _lastResultWasAtomic = true;
        }
        else
        {
            SerializeResult(result);
        }
    }


    internal async ValueTask EmitSimpleMapContextResultAsync(XQueryExpression perItemSelect)
    {
        var result = await EvaluateAsync(perItemSelect).ConfigureAwait(false);
        if (result == null)
            return;

        // Mirror SequenceAsync's atomic-string handling: a top-level string from an
        // XPath expression is an xs:string atomic value that takes a space separator
        // from a preceding atomic item (XSLT 3.0 §5.7.2). SerializeResult handles
        // sequences/arrays/nodes (and their internal atomic spacing) directly.
        if (result is string str)
        {
            if (str.Length == 0 && _wherePopulatedDepth > 0)
                return;
            if (_lastResultWasAtomic && _attributeContentDepth == 0)
            {
                var sep = _itemSeparatorOverride ?? " ";
                WriteText(sep, false);
                if (_contentTrackingStack.Count > 0)
                    _separatorCharsWritten += sep.Length;
            }
            WriteText(str, false);
            _lastResultWasAtomic = true;
        }
        else
        {
            SerializeResult(result);
        }
    }


    /// <summary>
    /// Serializes a result value: XDM nodes are serialized as XML, atomic values as text.
    /// Adjacent atomic values in a sequence are separated by a space.
    /// </summary>
    private void SerializeResult(object result)
    {
        // XTDE0450: Maps and function items cannot be serialized as element/document content
        if (result is IDictionary<object, object?>)
            throw Error("XTDE0450: An item in a sequence used as the content of an element or document node is a map");
        if (result is PhoenixmlDb.XQuery.Ast.XQueryFunction)
            throw Error("XTDE0450: An item in a sequence used as the content of an element or document node is a function item");
        // XSLT 3.0 §5.7.2: Arrays in content sequences are flattened — members extracted recursively
        if (result is List<object?> arrayList)
        {
            foreach (var member in arrayList)
            {
                if (member != null)
                    SerializeResult(member);
            }
            return;
        }

        if (result is XdmNode)
        {
            // In simple content mode (comment/PI/attribute body), atomize nodes
            // to their string value instead of serializing as XML markup.
            if (_textContentDepth > 0)
                WriteText(StringValueOf(result), false);
            else
            {
                // Top-level item-separator: a node participates in §5.7.2 separation just like an
                // atomic value, so a copy of the separator precedes it when in that mode.
                TryWriteTopLevelItemSeparator();
                SerializeNode(result);
            }
            _lastResultWasAtomic = false;
        }
        else if (result is ResultTreeFragment rtf)
        {
            // Parse RTF and serialize through SerializeNode for proper namespace fixup
            var rtfDoc = ParseResultTreeFragment(rtf);
            if (rtfDoc != null)
                SerializeNode(rtfDoc);
            else
                _sink.RawText(rtf.XmlContent);
            _lastResultWasAtomic = false;
        }
        else if (result is System.Xml.Linq.XNode linqNode)
        {
            // LINQ XML nodes (from fn:analyze-string, fn:parse-xml, etc.)
            if (_textContentDepth > 0)
                WriteText(linqNode.ToString(System.Xml.Linq.SaveOptions.DisableFormatting), false);
            else
                _sink.RawText(linqNode.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
            _lastResultWasAtomic = false;
        }
        else if (result is object?[] arr)
        {
            SerializeSequenceItems(arr);
        }
        else if (result is System.Collections.IEnumerable enumerable && result is not string)
        {
            var items = new System.Collections.ArrayList();
            foreach (var item in enumerable)
                if (item != null)
                    items.Add(item);
            SerializeSequenceItems(items);
        }
        else if (result is Xdm.TextNodeItem tni)
        {
            // TextNodeItem: text node from sequence accumulator — not atomic.
            WriteText(tni.Value, false);
            _lastResultWasAtomic = false;
        }
        else if (result is string str)
        {
            // Text content (from sort results, text nodes, etc.) — not atomic.
            // Atomic string spacing is handled at the source (SequenceAsync, OnEmptyAsync)
            // where we know the string comes from an XPath expression.
            WriteText(str, false);
            _lastResultWasAtomic = false;
        }
        else
        {
            // Atomic value: add separator. In top-level item-separator mode, the separator is
            // inserted before every item that follows an already-emitted one (including after a
            // node); otherwise fall back to the legacy "separate adjacent atomic values" rule.
            if (!TryWriteTopLevelItemSeparator()
                && _lastResultWasAtomic && _attributeContentDepth == 0)
                WriteText(_itemSeparatorOverride ?? " ", false);
            if (result is bool b)
                WriteText(b ? "true" : "false", false);
            else if (result is TimeSpan ts)
                WriteText(System.Xml.XmlConvert.ToString(ts), false);
            else if (result is Xdm.DayTimeDuration dtd)
                WriteText(dtd.ToString(), false);
            else if (result is float f)
                WriteText(FormatFloat(f), false);
            else if (result is double d)
                WriteText(FormatDouble(d), false);
            else if (result is decimal m)
                // Canonical xs:decimal lexical form (e.g. 25.0m → "25"); CLR
                // decimal.ToString() retains the source scale ("25.0").
                WriteText(FormatDecimal(m), false);
            else
                WriteText(result.ToString() ?? "", false);
            _lastResultWasAtomic = true;
        }
    }


    private void SerializeSequenceItems(System.Collections.IList items)
    {
        // Top-level item-separator mode: every item (node or atomic) is separated uniformly, so
        // delegate the separator to the per-item SerializeResult branches (which call
        // TryWriteTopLevelItemSeparator) rather than applying the atomic-only local rule here.
        if (InTopLevelItemSeparatorSequence)
        {
            foreach (var item in items)
            {
                if (item == null)
                    continue;
                _lastResultWasAtomic = false;
                SerializeResult(item);
            }
            return;
        }

        // Use local tracking for inter-item spacing within the sequence.
        // Inherit _lastResultWasAtomic for space before first item (cross-call continuity).
        var lastWasAtomic = _lastResultWasAtomic;
        // Separator between adjacent atomized items:
        // - In element content or comment/PI body: single space (or item-separator override)
        // - In attribute content: zero-length (no separator)
        var useSeparator = _attributeContentDepth == 0;
        var separator = _itemSeparatorOverride ?? " ";
        foreach (var item in items)
        {
            if (item == null)
                continue;
            var isNode = item is XdmNode || item is Xdm.TextNodeItem;
            // In text content mode, nodes are atomized to strings, so treat as atomic for separator logic
            var isAtomic = _textContentDepth > 0 || !isNode;
            if (useSeparator && isAtomic && lastWasAtomic)
            {
                WriteText(separator, false);
            }
            // Reset before inner call to prevent non-string atomics from double-adding spaces
            _lastResultWasAtomic = false;
            SerializeResult(item);
            lastWasAtomic = isAtomic;
        }
        // Set for cross-call continuity after the sequence
        _lastResultWasAtomic = lastWasAtomic;
    }


    /// <summary>
    /// Serializes items from xsl:copy-of, handling text separators for atomic values
    /// and copy-namespaces for node items.
    /// </summary>
    private void SerializeCopyOfItems(object?[] items, bool copyNamespaces)
    {
        var lastWasAtomic = _lastResultWasAtomic;
        var useSeparator = _attributeContentDepth == 0;
        foreach (var item in items)
        {
            if (item == null)
                continue;
            if (item is ResultTreeFragment r)
            {
                _sink.RawText(r.XmlContent);
                lastWasAtomic = false;
            }
            else if (item is XdmNode || item is XdmDocument)
            {
                SerializeNode(item, copyNamespaces);
                lastWasAtomic = false;
            }
            else if (item is Xdm.TextNodeItem tni)
            {
                WriteText(tni.Value, false);
                lastWasAtomic = false;
            }
            else
            {
                // Atomic value: add text separator between adjacent atomic values
                if (useSeparator && lastWasAtomic)
                    WriteText(" ", false);
                _lastResultWasAtomic = false;
                SerializeResult(item);
                lastWasAtomic = true;
            }
        }
        _lastResultWasAtomic = lastWasAtomic;
    }


    public override async ValueTask ResultDocumentAsync(XsltResultDocument instruction)
    {
        // XTDE1480: Cannot use result-document in temporary output state (e.g., inside a variable)
        if (_temporaryOutputDepth > 0)
            throw Error("XTDE1480: It is a dynamic error to evaluate xsl:result-document in temporary output state (e.g., within a variable or parameter)");

        // XTDE1460: Validate format attribute and find matching output declaration.
        // A NAMED xsl:output is LOCAL to its declaring package (XSLT 3.0 §3.6.7): when the
        // executing xsl:result-document belongs to a used package, @format must resolve
        // against THAT package's output declarations, not the principal's (use-package-108 /
        // use-package-108b). Non-package code: CurrentComponentPackage is null → principal.
        var formatOutputs = CurrentComponentPackage()?.Outputs ?? _stylesheet.Outputs;
        XsltOutput? matchedOutput = null;
        if (instruction.Format != null)
        {
            var formatName = await EvaluateAvtAsync(instruction.Format).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(formatName))
            {
                // Use compile-time resolved QName when available (handles prefix:local namespace resolution)
                if (instruction.ResolvedFormatName != null)
                {
                    var resolved = instruction.ResolvedFormatName.Value;
                    matchedOutput = formatOutputs.FirstOrDefault(o =>
                        o.Name != null && o.Name.Value.Namespace == resolved.Namespace && o.Name.Value.LocalName == resolved.LocalName);
                    if (matchedOutput == null)
                        throw Error($"XTDE1460: The format attribute of xsl:result-document ('{formatName}') does not match any xsl:output declaration");
                }
                else
                {
                    // Dynamic AVT: parse the format name as an EQName (Q{uri}local or NCName)
                    string? formatNs = null;
                    string formatLocal = formatName;
                    if (formatName.StartsWith("Q{", StringComparison.Ordinal))
                    {
                        var closeBrace = formatName.IndexOf('}', 2);
                        if (closeBrace > 0 && closeBrace < formatName.Length - 1)
                        {
                            formatNs = formatName[2..closeBrace];
                            formatLocal = formatName[(closeBrace + 1)..];
                        }
                        else
                        {
                            throw Error($"XTDE1460: The format attribute of xsl:result-document ('{formatName}') is not a valid EQName");
                        }
                    }
                    else if (formatName.Contains(':', StringComparison.Ordinal))
                    {
                        // Prefixed QName — resolve prefix using compile-time namespace bindings
                        var colonIdx = formatName.IndexOf(':', StringComparison.Ordinal);
                        var prefix = formatName[..colonIdx];
                        formatLocal = formatName[(colonIdx + 1)..];
                        if (instruction.NamespaceBindings != null
                            && instruction.NamespaceBindings.TryGetValue(prefix, out var ns))
                        {
                            formatNs = ns;
                        }
                        else
                        {
                            throw Error($"XTDE1460: The format attribute of xsl:result-document ('{formatName}') references undeclared prefix '{prefix}'");
                        }
                    }
                    else
                    {
                        try
                        {
                            System.Xml.XmlConvert.VerifyNCName(formatName);
                        }
                        catch (Exception ex) when (ex is System.Xml.XmlException or ArgumentException) // "" throws ArgumentException
                        {
                            throw Error($"XTDE1460: The format attribute of xsl:result-document ('{formatName}') is not a valid EQName");
                        }
                    }

                    // Resolve formatNs to a NamespaceId for comparison
                    var formatNsId = NamespaceId.None;
                    if (formatNs != null)
                        formatNsId = StylesheetParser.ResolveNamespaceUri(formatNs);

                    foreach (var output in formatOutputs)
                    {
                        if (output.Name == null)
                            continue;
                        if (formatNs != null)
                        {
                            // Match by ExpandedNamespace string or by NamespaceId
                            if (output.Name.Value.LocalName == formatLocal
                                && (output.Name.Value.ExpandedNamespace == formatNs
                                    || output.Name.Value.Namespace == formatNsId))
                            { matchedOutput = output; break; }
                        }
                        else
                        {
                            if (output.Name.Value.LocalName == formatLocal && output.Name.Value.Namespace == NamespaceId.None)
                            { matchedOutput = output; break; }
                        }
                    }
                    if (matchedOutput == null)
                        throw Error($"XTDE1460: The format attribute of xsl:result-document ('{formatName}') does not match any xsl:output declaration");
                }
            }
        }

        // An xsl:result-document with no @format uses the unnamed (default) xsl:output declaration
        // (XSLT 3.0 §27.1): its serialization parameters — including method — govern this result
        // document. When the unnamed output declares e.g. method="text", an href-less result-document
        // must serialize as text, not fall through to the xml default (insn/result-document/result-document-0202).
        if (matchedOutput == null && instruction.Format == null)
            matchedOutput = formatOutputs.FirstOrDefault(o => o.Name == null);

        // XTDE1490: Check for duplicate result-document URIs
        string effectiveHref = "";
        if (instruction.Href != null)
        {
            effectiveHref = await EvaluateAvtAsync(instruction.Href).ConfigureAwait(false);
        }

        // Resource policy: check write access before proceeding
        if (!string.IsNullOrEmpty(effectiveHref))
            CheckResultDocumentPolicy(effectiveHref);

        // XTDE1490: Check for duplicate result-document URIs
        // When targeting primary output (empty href) and NOT inside a secondary redirect,
        // check if primary output has already been written to (implicit content before explicit result-document)
        if (string.IsNullOrEmpty(effectiveHref) && _resultDocumentRedirectDepth == 0 &&
            (_outputElementStack.Count > 0 || _output.Length > 0))
        {
            // Primary output already has content — conflict with explicit href=""
            throw new XsltException("XTDE1490: Cannot write a second result document to the implicit primary output destination", instruction.Location);
        }
        if (!_resultDocumentUris.Add(effectiveHref))
        {
            // Report duplicate for: (a) empty href (primary output) or (b) static href literals
            if (string.IsNullOrEmpty(effectiveHref)
                || (instruction.Href != null && instruction.Href.Parts.All(p => p is AvtLiteral)))
                throw new XsltException($"XTDE1490: A result document has already been written to '{effectiveHref}'", instruction.Location);
        }

        // XTDE1500: Check if we're writing to a URI that was read during this transformation
        if (!string.IsNullOrEmpty(effectiveHref))
        {
            // Parse with TryCreate throughout. The old code combined against the base URI with
            // the throwing Uri constructor and turned ANY parse failure into XTDE1400 — including
            // failures on hrefs that are perfectly valid, which aborted the transform outright.
            var hrefForParsing = NormalizeNoAuthorityUri(effectiveHref);
            Uri? resolvedWriteUri;
            if (Uri.TryCreate(hrefForParsing, UriKind.Absolute, out var absUri))
                resolvedWriteUri = absUri;
            else if (_stylesheet.BaseUri != null
                     && Uri.TryCreate(_stylesheet.BaseUri, hrefForParsing, out var combinedUri))
                resolvedWriteUri = combinedUri;
            else if (Uri.TryCreate(hrefForParsing, UriKind.RelativeOrAbsolute, out var anyUri))
                resolvedWriteUri = anyUri;
            else
                throw Error($"XTDE1400: Invalid URI in xsl:result-document href: '{effectiveHref}'");

            // Only an absolute URI can be compared against the documents read so far;
            // AbsoluteUri throws on a relative one, and a relative href that never resolved
            // cannot collide with a read URI anyway.
            if (resolvedWriteUri.IsAbsoluteUri)
            {
                var writeUriStr = resolvedWriteUri.AbsoluteUri;
                foreach (var readUri in _documentResolver.ReadDocumentUris)
                {
                    if (string.Equals(readUri, writeUriStr, StringComparison.OrdinalIgnoreCase))
                        throw new XsltException($"XTDE1500: Cannot write to URI '{effectiveHref}' — it was read during this transformation", instruction.Location);
                }
            }
        }

        // Evaluate omit-xml-declaration from xsl:result-document (AVT)
        bool? resultOmitXmlDecl = null;
        if (instruction.OmitXmlDeclaration != null)
        {
            var omitStr = await EvaluateAvtAsync(instruction.OmitXmlDeclaration).ConfigureAwait(false);
            resultOmitXmlDecl = omitStr.Trim() is "yes" or "true" or "1";
        }

        // Evaluate indent from xsl:result-document (AVT)
        bool? resultIndent = null;
        if (instruction.Indent != null)
        {
            var indentStr = await EvaluateAvtAsync(instruction.Indent).ConfigureAwait(false);
            resultIndent = indentStr.Trim() is "yes" or "true" or "1";
        }

        // Evaluate standalone from xsl:result-document (AVT). Tri-state: yes/no set the
        // attribute, "omit" (or absent) leaves it off. When specified, it overrides the
        // matched xsl:output — including an explicit "omit" that clears an inherited value.
        bool? resultStandalone = null;
        var resultStandaloneSpecified = instruction.Standalone != null;
        if (resultStandaloneSpecified)
        {
            var saStr = (await EvaluateAvtAsync(instruction.Standalone!).ConfigureAwait(false)).Trim();
            resultStandalone = saStr switch
            {
                "yes" or "true" or "1" => true,
                "no" or "false" or "0" => false,
                _ => null, // "omit" → no standalone attribute
            };
        }

        // Evaluate output-version from xsl:result-document (AVT) → XML declaration version.
        string? resultVersion = instruction.OutputVersion != null
            ? (await EvaluateAvtAsync(instruction.OutputVersion).ConfigureAwait(false)).Trim()
            : null;

        // Evaluate html-version from xsl:result-document (AVT). For html/xhtml a value >= 5.0
        // selects HTML5 serialization, which emits "<!DOCTYPE html>" (result-document-0242 / 0244).
        string? resultHtmlVersion = instruction.HtmlVersion != null
            ? (await EvaluateAvtAsync(instruction.HtmlVersion).ConfigureAwait(false)).Trim()
            : null;

        // Evaluate doctype-public / doctype-system from xsl:result-document (AVTs). When present
        // (even as a zero-length string, per erratum E31), they override the matched xsl:output.
        string? resultDoctypePublic = instruction.DoctypePublic != null
            ? await EvaluateAvtAsync(instruction.DoctypePublic).ConfigureAwait(false)
            : null;
        string? resultDoctypeSystem = instruction.DoctypeSystem != null
            ? await EvaluateAvtAsync(instruction.DoctypeSystem).ConfigureAwait(false)
            : null;

        // Evaluate media-type / include-content-type from xsl:result-document (AVTs). These drive
        // the HTML/XHTML Content-Type meta and serialized media type; the result-document's own
        // values take precedence over the matched xsl:output (result-document-0223 / 0224).
        string? resultMediaType = instruction.MediaType != null
            ? await EvaluateAvtAsync(instruction.MediaType).ConfigureAwait(false)
            : null;
        bool? resultIncludeContentType = null;
        if (instruction.IncludeContentType != null)
        {
            var ictStr = (await EvaluateAvtAsync(instruction.IncludeContentType).ConfigureAwait(false)).Trim();
            resultIncludeContentType = ictStr is "yes" or "true" or "1";
        }

        // Evaluate byte-order-mark / escape-uri-attributes from xsl:result-document (AVTs). When
        // present they override the matched xsl:output: byte-order-mark drives the leading U+FEFF,
        // escape-uri-attributes gates percent-encoding of URI-valued HTML/XHTML attributes.
        bool? resultByteOrderMark = null;
        if (instruction.ByteOrderMark != null)
        {
            var bomStr = (await EvaluateAvtAsync(instruction.ByteOrderMark).ConfigureAwait(false)).Trim();
            resultByteOrderMark = bomStr is "yes" or "true" or "1";
        }
        bool? resultEscapeUriAttributes = null;
        if (instruction.EscapeUriAttributes != null)
        {
            var euaStr = (await EvaluateAvtAsync(instruction.EscapeUriAttributes).ConfigureAwait(false)).Trim();
            resultEscapeUriAttributes = euaStr is "yes" or "true" or "1";
        }

        // Evaluate cdata-section-elements from xsl:result-document. This is an AVT (result-document-0401);
        // the value is split on whitespace and each token resolved to a QName against the
        // result-document element's in-scope namespaces. The effective set is the UNION of this list
        // and the matched xsl:output declaration's cdata-section-elements (result-document-0240).
        var resultCdataSectionElements = await EvaluateCdataSectionElementsAsync(instruction).ConfigureAwait(false);

        // XSLT 3.0 §27.1: load the parameter-document (an AVT — its URI may reference variables, so
        // it is resolved here at runtime, not at compile time — result-document-1406). It contributes
        // an output method and inline character maps that supplement this result-document, but any
        // parameter set directly on xsl:result-document takes precedence over the parameter document.
        OutputMethod? paramDocMethod = null;
        QName? paramDocCharMapName = null;
        if (instruction.ParameterDocument != null)
        {
            var paramDocHref = await EvaluateAvtAsync(instruction.ParameterDocument).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(paramDocHref))
            {
                var (loadedMethod, loadedCharMap) = LoadResultDocumentParameterDocument(paramDocHref);
                paramDocMethod = loadedMethod;
                if (loadedCharMap is { Count: > 0 })
                    paramDocCharMapName = Owner!.RegisterRuntimeCharacterMap(loadedCharMap);
            }
        }

        // Determine effective output method for this result-document
        // Priority: method attribute on xsl:result-document > parameter-document > matched xsl:output
        OutputMethod? resultMethod = null;
        if (instruction.Method != null)
        {
            var methodStr = await EvaluateAvtAsync(instruction.Method).ConfigureAwait(false);
            resultMethod = methodStr.Trim() switch
            {
                "text" => OutputMethod.Text,
                "html" => OutputMethod.Html,
                "xhtml" => OutputMethod.Xhtml,
                "xml" => OutputMethod.Xml,
                "json" => OutputMethod.Json,
                "adaptive" => OutputMethod.Adaptive,
                _ => null
            };
        }
        resultMethod ??= paramDocMethod;
        resultMethod ??= matchedOutput?.Method;

        // Resolve item-separator: instruction attribute > matched xsl:output > default
        // Special value "#absent" means the parameter is explicitly unset (XSLT 3.0 §20)
        string? itemSeparator = null;
        if (instruction.ItemSeparator != null)
        {
            var sep = await EvaluateAvtAsync(instruction.ItemSeparator).ConfigureAwait(false);
            if (sep != "#absent")
                itemSeparator = sep;
        }
        else if (matchedOutput?.ItemSeparator != null && matchedOutput.ItemSeparator != "#absent")
            itemSeparator = matchedOutput.ItemSeparator;

        // Resolve allow-duplicate-names: instruction attribute > matched xsl:output > default (false)
        bool allowDuplicateNames = matchedOutput?.AllowDuplicateNames == true;
        if (instruction.AllowDuplicateNames != null)
        {
            var adnStr = await EvaluateAvtAsync(instruction.AllowDuplicateNames).ConfigureAwait(false);
            allowDuplicateNames = adnStr.Trim() is "yes" or "true" or "1";
        }

        // Apply item-separator override during content execution. Each result-document is its own
        // top-level result sequence, so reset the "item already emitted" tracking for its content.
        var savedItemSeparator = _itemSeparatorOverride;
        var savedTopLevelItemEmitted = _topLevelItemEmitted;
        if (itemSeparator != null)
            _itemSeparatorOverride = itemSeparator;
        _topLevelItemEmitted = false;

        // When href is non-empty, redirect output to a separate buffer for the secondary result document
        var redirectToSecondary = !string.IsNullOrEmpty(effectiveHref);
        // When href is empty but we're inside a secondary redirect, we need to redirect
        // back to primary output by saving/restoring the secondary buffer
        var redirectToPrimary = !redirectToSecondary && _resultDocumentRedirectDepth > 0;

        // Effective use-character-maps: the parameter-document's inline maps apply FIRST, then any
        // use-character-maps written directly on xsl:result-document (which override, per §27.1).
        List<QName>? effectiveCharMaps = null;
        if (paramDocCharMapName != null || instruction.UseCharacterMaps.Count > 0)
        {
            effectiveCharMaps = new List<QName>();
            if (paramDocCharMapName != null)
                effectiveCharMaps.Add(paramDocCharMapName.Value);
            effectiveCharMaps.AddRange(instruction.UseCharacterMaps);
        }

        // Track character maps from xsl:result-document targeting principal output.
        // These persist until SerializeResult runs (no save/restore needed since only
        // one result-document can target principal output per XTDE1490).
        if (effectiveCharMaps != null && !redirectToSecondary)
            _principalOutputCharacterMaps = effectiveCharMaps;
        string? savedOutputContent = null;
        List<Dictionary<string, string>>? savedNsScopes = null;
        List<QName>? savedElementStack = null;
        List<bool>? savedElementHasNs = null;
        List<bool>? savedElementIsLre = null;
        List<Dictionary<string, string>>? savedXslNsBindings = null;
        if (redirectToSecondary || redirectToPrimary)
        {
            // If primary output has content before we redirect, mark it as claimed
            // so nested result-document href="" will correctly detect the duplicate
            if (_resultDocumentRedirectDepth == 0 && (_output.Length > 0 || _outputElementStack.Count > 0))
                _resultDocumentUris.Add("");
            savedOutputContent = _output.ToString();
            _output.Clear();
            // Save and clear namespace/element stacks
            savedNsScopes = new(_outputNsScopes);
            _outputNsScopes.Clear();
            savedElementStack = new(_outputElementStack);
            _outputElementStack.Clear();
            savedElementHasNs = new(_outputElementHasNsStack);
            _outputElementHasNsStack.Clear();
            savedElementIsLre = new(_outputElementIsLreStack);
            _outputElementIsLreStack.Clear();
            savedXslNsBindings = new(_xslNamespaceBindings);
            _xslNamespaceBindings.Clear();
            if (redirectToSecondary)
                _resultDocumentRedirectDepth++;
        }

        var savedActiveOutput = _activeResultDocumentOutput;
        // current-output-uri() must report THIS result document's destination for the duration of
        // its body (XSLT 3.0 §20.3.7). With no href the destination is the principal output, so the
        // base output URI stands; with an href it is that href resolved against the base output URI.
        // Reported by Martin Honnen 2026-09-06: the function was a stub returning the empty
        // sequence unconditionally, so it was silently empty even when the destination was known.
        var savedCurrentOutputUri = _currentOutputUri;
        if (!string.IsNullOrEmpty(effectiveHref))
        {
            var resolveAgainst = _baseOutputUri ?? _options.BaseUri;
            _currentOutputUri = resolveAgainst != null
                && Uri.TryCreate(resolveAgainst, effectiveHref, out var abs) ? abs
                : Uri.TryCreate(effectiveHref, UriKind.Absolute, out var direct) ? direct
                : _currentOutputUri;
        }
        // CDATA-section wrapping happens at text-emission time via IsInCdataSectionElement(), which
        // reads _activeResultDocumentOutput.CdataSectionElements. The matched xsl:output alone omits
        // the result-document's own cdata-section-elements, so compute the effective union here and
        // expose it through the active output declaration for the duration of content emission.
        var effectiveCdata = UnionCdataSectionElements(resultCdataSectionElements, matchedOutput?.CdataSectionElements);
        _activeResultDocumentOutput = effectiveCdata == null
            ? matchedOutput
            : (matchedOutput ?? new Ast.XsltOutput()).CloneWithCdataSectionElements(effectiveCdata);
        // XSLT 3.0 §26.1: the content of xsl:result-document builds a RESULT DOCUMENT and
        // contributes nothing to the containing sequence constructor. Detach the enclosing
        // typed-body capture for the duration, so an xsl:sequence inside the result document
        // serializes into the result document instead of being accumulated as an item of the
        // enclosing as="…" template/function. (The Json/Adaptive branch installs its own
        // accumulator below; it saves and restores whatever it finds here.)
        var savedAccumForResultDoc = _sequenceAccumulator;
        var savedAsBodyCaptureForResultDoc = _currentAsBodyCapture;
        _sequenceAccumulator = null;
        _currentAsBodyCapture = null;
        try
        {
            if (resultMethod == OutputMethod.Text)
            {
                // Text output method (XSLT 3.0 §20.5.3): output the string-value
                // of every text node in the result tree, without escaping.
                // For PRIMARY output: use sentinel escaping to protect text content
                // from the final StripXmlMarkup pass in TransformAsync serialization.
                // For SECONDARY output: no sentinel needed since content goes directly
                // to _secondaryResultDocuments without further stripping.
                var textSaved = _output.ToString();
                _output.Clear();
                var useSentinel = !redirectToSecondary;
                if (useSentinel) _textOutputModeDepth++;
                try
                {
                    await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    if (useSentinel) _textOutputModeDepth--;
                }
                var rawResult = _output.ToString();
                _output.Clear();
                _output.Append(textSaved);
                _output.Append(StripXmlMarkup(rawResult));
            }
            else if (resultMethod is OutputMethod.Json or OutputMethod.Adaptive)
            {
                var isAdaptive = resultMethod == OutputMethod.Adaptive;
                var allowDupNames = allowDuplicateNames;
                var savedAccum = _sequenceAccumulator;
                _sequenceAccumulator = new List<object?>();
                var jsonSaved = _output.ToString();
                _output.Clear();

                if (isAdaptive)
                {
                    // Adaptive mode: process instructions one by one to preserve interleaving
                    // of XDM items (maps, arrays, atomics) and XML output (elements, attributes)
                    var effectiveSep = itemSeparator ?? "\n";
                    var resultItems = new List<string>();
                    foreach (var childInsn in instruction.Content.Instructions)
                    {
                        var outputBefore = _output.Length;
                        var accumBefore = _sequenceAccumulator!.Count;
                        await childInsn.ExecuteAsync(this).ConfigureAwait(false);

                        // Collect any new sequence items
                        for (int si = accumBefore; si < _sequenceAccumulator!.Count; si++)
                        {
                            resultItems.Add(XsltTransformEngine.SerializeItemAdaptive(_sequenceAccumulator[si]));
                        }

                        // Collect any new XML output
                        if (_output.Length > outputBefore)
                        {
                            var xmlFragment = _output.ToString(outputBefore, _output.Length - outputBefore);
                            _output.Remove(outputBefore, _output.Length - outputBefore);
                            resultItems.Add(xmlFragment);
                        }
                    }
                    _output.Clear();
                    _output.Append(jsonSaved);
                    _sequenceAccumulator = savedAccum;

                    for (int i = 0; i < resultItems.Count; i++)
                    {
                        if (i > 0) _output.Append(effectiveSep);
                        _output.Append(resultItems[i]);
                    }
                }
                else
                {
                    // JSON mode: collect all items, then serialize
                    await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                    var textContent = _output.ToString();
                    _output.Clear();
                    _output.Append(jsonSaved);
                    var jsonItems = _sequenceAccumulator;
                    _sequenceAccumulator = savedAccum;

                    if (jsonItems.Count > 0)
                    {
                        var rdJsonIndent = (resultIndent ?? matchedOutput?.Indent) == true;
                        if (jsonItems.Count == 1)
                            _output.Append(XsltTransformEngine.SerializeItemAsJson(jsonItems[0], false, allowDupNames, indent: rdJsonIndent));
                        else
                        {
                            _output.Append('[');
                            for (int i = 0; i < jsonItems.Count; i++)
                            {
                                if (i > 0) _output.Append(',');
                                if (rdJsonIndent) { _output.Append('\n'); _output.Append("  "); }
                                _output.Append(XsltTransformEngine.SerializeItemAsJson(jsonItems[i], false, allowDupNames, indent: rdJsonIndent, depth: 1));
                            }
                            if (rdJsonIndent) _output.Append('\n');
                            _output.Append(']');
                        }
                    }
                    else if (!string.IsNullOrEmpty(textContent))
                    {
                        _output.Append(textContent);
                    }
                }
            }
            else
            {
                await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
            }

            // Store secondary result document content
            if (redirectToSecondary)
            {
                var secondaryContent = _output.ToString();

                // Route secondary result-document serialization through the shared FinalizeOutput
                // pipeline (text/html post-process, content-type meta, indentation, character maps,
                // Unicode normalization, xml-decl, doctype, BOM, escape-uri, sentinel restore) so it
                // gains the same treatment as primary/streaming output instead of a bespoke subset.
                //
                // Build a resolved output declaration for this result-document. The result-document's
                // own attributes (method/indent/omit-xml-declaration) take precedence over its matched
                // xsl:output (format=), which in turn falls back to the stylesheet's default xsl:output
                // for doctype/BOM/suppress-indentation/etc. This reproduces the previous effective
                // method/indent/doctype/BOM resolution exactly while adding the rest of the pipeline.
                //
                // Character maps: a secondary result-document's own use-character-maps were previously
                // not applied; the effective list (parameter-document maps first, then direct
                // use-character-maps) is now supplied so a parameter-document targeting a secondary
                // result document also rewrites its serialized output (§27.1).
                var rdBaseOutput = matchedOutput ?? _stylesheet.Outputs.FirstOrDefault();
                var rdOutputDecl = new Ast.XsltOutput
                {
                    Method = resultMethod ?? matchedOutput?.Method,
                    Indent = resultIndent ?? matchedOutput?.Indent,
                    HtmlVersion = resultHtmlVersion ?? rdBaseOutput?.HtmlVersion,
                    OmitXmlDeclaration = resultOmitXmlDecl ?? rdBaseOutput?.OmitXmlDeclaration,
                    Encoding = rdBaseOutput?.Encoding,
                    Version = resultVersion ?? rdBaseOutput?.Version,
                    Standalone = resultStandaloneSpecified ? resultStandalone : rdBaseOutput?.Standalone,
                    DoctypePublic = resultDoctypePublic ?? rdBaseOutput?.DoctypePublic,
                    DoctypeSystem = resultDoctypeSystem ?? rdBaseOutput?.DoctypeSystem,
                    MediaType = resultMediaType ?? rdBaseOutput?.MediaType,
                    IncludeContentType = resultIncludeContentType ?? rdBaseOutput?.IncludeContentType,
                    EscapeUriAttributes = resultEscapeUriAttributes ?? rdBaseOutput?.EscapeUriAttributes,
                    NormalizationForm = rdBaseOutput?.NormalizationForm,
                    ByteOrderMark = resultByteOrderMark ?? rdBaseOutput?.ByteOrderMark,
                    // CDATA wrapping is applied at emission time (via _activeResultDocumentOutput), not
                    // in FinalizeOutput; carry the effective union here for declaration consistency.
                    CdataSectionElements = effectiveCdata ?? matchedOutput?.CdataSectionElements,
                    SuppressIndentation = matchedOutput?.SuppressIndentation ?? _stylesheet.Outputs.FirstOrDefault()?.SuppressIndentation,
                };
                secondaryContent = Owner!.FinalizeOutput(
                    secondaryContent, rdOutputDecl, effectiveCharMaps, XsltTransformEngine.FinalizeKind.ResultDocument);

                if (MaxResultDocuments > 0 && _secondaryResultDocuments.Count >= MaxResultDocuments)
                    throw Error($"XTDE1490: Maximum number of secondary result documents ({MaxResultDocuments}) exceeded. " +
                        "Set MaxResultDocuments on XsltTransformer to increase the limit.");
                ValidateResultDocumentContent(instruction, secondaryContent);
                _secondaryResultDocuments[effectiveHref] = secondaryContent;
            }
            else if (redirectToPrimary)
            {
                // Nested xsl:result-document href="" inside a secondary redirect:
                // capture the content as pending primary output
                _pendingPrimaryContent = _output.ToString();
                _primaryOutputClaimedByResultDocument = true;
                // When result-document targets primary output with no matched format, create a
                // synthetic output declaration so serialization honours its method/standalone/
                // output-version/omit-xml-declaration (not the xsl:output default). A null method
                // still defaults to xml, so the XML declaration is emitted even with no attributes.
                _primaryOutputMatchedDeclaration = ApplyResultDocumentDoctype(
                    matchedOutput
                    ?? CreateSyntheticOutput(resultMethod, resultOmitXmlDecl, resultIndent,
                        resultStandalone, resultStandaloneSpecified, resultVersion,
                        resultMediaType, resultIncludeContentType,
                        resultByteOrderMark, resultEscapeUriAttributes, resultHtmlVersion),
                    resultDoctypePublic, resultDoctypeSystem);
            }
            else
            {
                // XTDE1490: Mark that the primary output has been claimed by an explicit
                // result-document href="", preventing further implicit writes
                _primaryOutputClaimedByResultDocument = true;
                _primaryOutputMatchedDeclaration = ApplyResultDocumentDoctype(
                    matchedOutput
                    ?? CreateSyntheticOutput(resultMethod, resultOmitXmlDecl, resultIndent,
                        resultStandalone, resultStandaloneSpecified, resultVersion,
                        resultMediaType, resultIncludeContentType,
                        resultByteOrderMark, resultEscapeUriAttributes, resultHtmlVersion),
                    resultDoctypePublic, resultDoctypeSystem);
            }
        }
        finally
        {
            _sequenceAccumulator = savedAccumForResultDoc;
            _currentAsBodyCapture = savedAsBodyCaptureForResultDoc;
            _activeResultDocumentOutput = savedActiveOutput;
            _currentOutputUri = savedCurrentOutputUri;
            _itemSeparatorOverride = savedItemSeparator;
            _topLevelItemEmitted = savedTopLevelItemEmitted;
            if (redirectToSecondary || redirectToPrimary)
            {
                if (redirectToSecondary)
                    _resultDocumentRedirectDepth--;
                _output.Clear();
                // When returning to primary output level and there's pending primary content
                // from a nested xsl:result-document href="", use it instead of the saved content
                if (redirectToSecondary && _resultDocumentRedirectDepth == 0 && _pendingPrimaryContent != null)
                {
                    _output.Append(_pendingPrimaryContent);
                    _pendingPrimaryContent = null;
                }
                else
                {
                    _output.Append(savedOutputContent);
                }
                // Restore namespace/element stacks
                _outputNsScopes.Clear();
                foreach (var scope in savedNsScopes!.AsEnumerable().Reverse())
                    _outputNsScopes.Push(scope);
                _outputElementStack.Clear();
                foreach (var item in savedElementStack!.AsEnumerable().Reverse())
                    _outputElementStack.Push(item);
                _outputElementHasNsStack.Clear();
                foreach (var item in savedElementHasNs!.AsEnumerable().Reverse())
                    _outputElementHasNsStack.Push(item);
                _outputElementIsLreStack.Clear();
                foreach (var item in savedElementIsLre!.AsEnumerable().Reverse())
                    _outputElementIsLreStack.Push(item);
                _xslNamespaceBindings.Clear();
                foreach (var item in savedXslNsBindings!.AsEnumerable().Reverse())
                    _xslNamespaceBindings.Push(item);
            }
        }
    }


    public override async ValueTask SourceDocumentAsync(XsltSourceDocument instruction)
    {
        // Deferred XTSE3430 streamability error — throw at runtime instead of parse time
        if (instruction.StreamabilityError != null)
            throw new XsltException(instruction.StreamabilityError, instruction.Location);

        // Evaluate the href AVT to get the URI string
        var href = await EvaluateAvtAsync(instruction.Href).ConfigureAwait(false);

        // Extract fragment identifier before URI construction (.NET Uri may encode # in
        // relative references when base was created from a file path)
        string? fragment = null;
        var hrefForResolve = href;
        var fragmentIdx = href.IndexOf('#', StringComparison.Ordinal);
        if (fragmentIdx >= 0)
        {
            fragment = href[fragmentIdx..]; // includes #
            hrefForResolve = href[..fragmentIdx];
        }

        // Resolve URI relative to the stylesheet base URI
        Uri resolvedUri;
        try
        {
            // Use instruction's effective base URI (accounts for xml:base), falling back to stylesheet base
            var baseUri = instruction.BaseUri ?? _stylesheet.BaseUri;

            // A href that is not a URI reference at all — a Windows path such as
            // c:\my\doc\books.xml — is FODC0005 (invalid argument), not FODC0002 (a valid URI
            // that cannot be retrieved). The backslashes were silently rewritten into a file:
            // URI, which then reported "document not found" (W3C stream-006, non-stream-006).
            foreach (var ch in hrefForResolve)
            {
                if (ch is '\\' or '<' or '>' or '"' or '{' or '}' or '|' or '^' or '`' || char.IsControl(ch))
                    throw Error($"FODC0005: Invalid URI '{href}': the character '{ch}' is not allowed in a URI reference");
            }

            if (Uri.TryCreate(hrefForResolve, UriKind.Absolute, out var absUri))
            {
                resolvedUri = absUri;
            }
            else if (baseUri != null)
            {
                resolvedUri = new Uri(baseUri, hrefForResolve);
            }
            else
            {
                // No base URI — try as absolute or throw
                resolvedUri = new Uri(hrefForResolve, UriKind.RelativeOrAbsolute);
                if (!resolvedUri.IsAbsoluteUri)
                    throw Error($"FODC0005: Cannot resolve relative URI '{href}' — no base URI available");
            }
        }
        catch (UriFormatException ex)
        {
            throw Error($"FODC0005: Invalid URI '{href}': {ex.Message}");
        }

        // If streamable="yes", use XmlReader-based streaming instead of full tree loading.
        // Exception: content that cannot be driven off the live reader at the document
        // level (notably xsl:fork / xsl:for-each-group with group-by, which has no
        // streaming dispatch) must materialize the whole input first — fall through to
        // the full-tree load path below, which evaluates group-by correctly.
        // #143 Phase 1.5 — the source-document whole-input decision is unified with the
        // document-node-template site through XsltTransformEngine.DocLevelWholeInputBuffer:
        // buffer when EITHER the proven legacy detector demands it OR the posture/sweep
        // classifier's StreamingPlan derives BufferWholeInput (additive — a guaranteed-streamable
        // body never triggers it, so si-iterate-037 and kin still stream).
        var sourceDocNeedsWholeInput =
            XsltTransformEngine.DocLevelWholeInputBuffer(instruction.Content);
        if (instruction.Streamable && resolvedUri.IsFile && string.IsNullOrEmpty(fragment)
            && !sourceDocNeedsWholeInput)
        {
            try
            {
                using var fileStream = System.IO.File.OpenRead(resolvedUri.LocalPath);
                var readerSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Parse,
                    Async = true,
                    MaxCharactersFromEntities = 1_000_000
                };
                using var xmlReader = XmlReader.Create(fileStream, readerSettings, resolvedUri.AbsoluteUri);

                var streamNodeStore = _nodeStore ?? new XdmInMemoryStore();
                _templateIndex.ResolvePatternNamespaces(streamNodeStore.InternNamespace);

                // Resolve accumulators for streaming
                var streamAccumulators = new List<XsltAccumulator>();
                if (instruction.UseAccumulators.Count > 0)
                {
                    foreach (var accName in instruction.UseAccumulators)
                    {
                        if (_stylesheet.Accumulators.TryGetValue(accName, out var acc))
                            streamAccumulators.Add(acc);
                    }
                }

                // Pre-scan for consuming expressions that need stream watchers
                var scanner = new StreamingExpressionScanner();
                var scanResult = instruction.Content != null
                    ? scanner.ScanWithSubscriptions(instruction.Content)
                    : default;
                var watchers = scanResult.Watchers ?? (IReadOnlyList<StreamWatcher>)Array.Empty<StreamWatcher>();
                var subscriptions = scanResult.Subscriptions ?? (IReadOnlyList<ForEachSubscription>)Array.Empty<ForEachSubscription>();

                // Subscription-dispatch-only: subscriptions OR watchers exist AND the
                // body has no xsl:apply-templates (so the default template machinery
                // shouldn't fire on non-subscribed/non-watched elements/text during
                // the streaming pass — only the accumulation/dispatch driven by the
                // scanned instructions should run).
                bool subscriptionOnly = (subscriptions.Count > 0 || watchers.Count > 0)
                    && instruction.Content != null
                    && !ContentContainsApplyTemplates(instruction.Content);

                var processor = new StreamingXmlProcessor(
                    _stylesheet, _templateIndex, this, streamNodeStore, _currentMode,
                    streamAccumulators.Count > 0 ? streamAccumulators : null,
                    watchers.Count > 0 ? watchers : null,
                    subscriptions.Count > 0 ? subscriptions : null,
                    subscriptionOnly);

                // Push a synthetic document node as context so the content body can execute
                var syntheticDocId = new NodeId(999_999);
                var syntheticDoc = new XdmDocument
                {
                    StringValueResolver = streamNodeStore.StringValueResolver,
                    Id = syntheticDocId,
                    Document = new DocumentId(0),
                    Children = [],
                    BaseUri = resolvedUri.AbsoluteUri
                };
                streamNodeStore.Register(syntheticDoc);

                PushContextItem(syntheticDoc, 1, 1);
                var savedStreamingProcessor = _activeStreamingProcessor;
                var savedStreamingReader = _activeStreamingReader;
                var savedStreamingCt = _activeStreamingCancellationToken;
                _activeStreamingProcessor = processor;
                _activeStreamingReader = xmlReader;
                _activeStreamingCancellationToken = _options.CancellationToken;
                // Store watchers for result substitution during EvaluateAsync
                var savedWatchers = _activeStreamWatchers;
                _activeStreamWatchers = watchers.Count > 0 ? watchers : null;

                try
                {
                    // If watchers/subscriptions exist but no xsl:apply-templates will trigger
                    // the streaming pass, run ProcessAsync now so watcher results are
                    // available when the content body evaluates variable select expressions
                    // (and so subscription for-each bodies execute per-match during the
                    // forward pass).
                    // A wrapped (inline-driven) for-each must run inside LINEAR body
                    // execution so its surrounding construction (the lexically-enclosing
                    // LRE / xsl:element / xsl:copy) is emitted around its output. When
                    // every subscription is inline-driven and there are no consuming
                    // watchers, fall through to the linear-body branch below; the wrapped
                    // for-each hands off to the live reader at its lexical position via
                    // the ForEachAsync streaming intercept. The forward-pass dispatch is
                    // used only for BARE top-of-body for-each (and watcher) bodies.
                    bool allInlineDriven = subscriptions.Count > 0
                        && watchers.Count == 0
                        && subscriptions.All(s => s.InlineDriven);

                    bool processorRanForSubscriptions = false;
                    if ((watchers.Count > 0 || subscriptions.Count > 0) && instruction.Content != null
                        && !ContentContainsApplyTemplates(instruction.Content)
                        && !allInlineDriven)
                    {
                        _activeStreamingProcessor = null;
                        _activeStreamingReader = null;

                        // Mixed-sequence prefix: grounded operands appearing BEFORE the
                        // streamable path in the for-each select. Evaluate each and
                        // dispatch the body per item, in document order, BEFORE the
                        // streaming pass starts.
                        foreach (var sub in subscriptions)
                        {
                            if (sub.PrefixItems.Count > 0)
                                await ExecuteForEachSubscriptionItemsAsync(sub, sub.PrefixItems).ConfigureAwait(false);
                        }

                        await processor.ProcessAsync(xmlReader, _options.CancellationToken).ConfigureAwait(false);

                        // Mixed-sequence suffix: same as prefix but after streaming.
                        foreach (var sub in subscriptions)
                        {
                            if (sub.SuffixItems.Count > 0)
                                await ExecuteForEachSubscriptionItemsAsync(sub, sub.SuffixItems).ConfigureAwait(false);
                        }

                        processorRanForSubscriptions = subscriptions.Count > 0;
                    }

                    if (instruction.Content != null && !processorRanForSubscriptions)
                    {
                        // Execute the Content body (typically xsl:apply-templates or xsl:iterate).
                        // When ApplyTemplatesAsync is invoked within the Content body and the
                        // streaming processor is active, it triggers the streaming processor
                        // to read the document via XmlReader in a forward pass.
                        // Skipped when subscriptions ran: the for-each body was already
                        // dispatched per-match during ProcessAsync; re-executing it would
                        // double-process against the synthetic empty document.
                        await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _activeStreamingProcessor = savedStreamingProcessor;
                    _activeStreamingReader = savedStreamingReader;
                    _activeStreamingCancellationToken = savedStreamingCt;
                    _activeStreamWatchers = savedWatchers;
                    PopContextItem();
                    streamNodeStore.Remove(syntheticDocId);
                }
            }
            catch (System.IO.FileNotFoundException)
            {
                throw Error($"FODC0002: Document not found: '{resolvedUri}'");
            }
            catch (System.IO.IOException ex)
            {
                throw Error($"FODC0002: Error loading document '{resolvedUri}': {ex.Message}");
            }

            return; // Skip the full-tree path
        }

        // Helper: check if a content body contains xsl:apply-templates (which triggers streaming)
        static bool ContentContainsApplyTemplates(Ast.XsltSequenceConstructor body)
        {
            foreach (var insn in body.Instructions)
            {
                if (insn is Ast.XsltApplyTemplates) return true;
                if (insn is Ast.XsltSequenceConstructor nested && ContentContainsApplyTemplates(nested)) return true;
            }
            return false;
        }

        // Load the XML document (resolvedUri is already fragment-free)
        string xmlContent;
        try
        {
            if (resolvedUri.IsFile)
            {
                xmlContent = await System.IO.File.ReadAllTextAsync(resolvedUri.LocalPath).ConfigureAwait(false);
            }
            else
            {
                throw Error($"FODC0002: Cannot resolve document URI '{resolvedUri}' — only file:// URIs are supported");
            }
        }
        catch (System.IO.FileNotFoundException)
        {
            throw Error($"FODC0002: Document not found: '{resolvedUri}'");
        }
        catch (System.IO.IOException ex)
        {
            throw Error($"FODC0002: Error loading document '{resolvedUri}': {ex.Message}");
        }

        // Parse and convert to XDM
        var xmlDoc = new XmlDocument { PreserveWhitespace = true };
        xmlDoc.LoadXml(xmlContent);

        var nodeStore = _nodeStore ?? new XdmInMemoryStore();
        var xdmDoc = XsltTransformEngine.ConvertToXdm(xmlDoc, nodeStore, resolvedUri.AbsoluteUri);

        // Apply xsl:strip-space declarations
        if (_stylesheet.StripSpace.Count > 0)
        {
            // Ensure namespace resolution for strip-space/preserve-space NameTests
            foreach (var nt in _stylesheet.StripSpace)
                nt.ResolveNamespace(nodeStore.InternNamespace);
            foreach (var nt in _stylesheet.PreserveSpace)
                nt.ResolveNamespace(nodeStore.InternNamespace);

            XsltTransformEngine.StripWhitespaceNodes(xdmDoc, _stylesheet.StripSpace, _stylesheet.PreserveSpace, nodeStore);
        }

        // Pre-compute accumulators if use-accumulators is specified
        var previousAccumulatorValues = _accumulatorValues;
        if (instruction.UseAccumulators.Count > 0)
        {
            var accumulators = new List<XsltAccumulator>();
            foreach (var accName in instruction.UseAccumulators)
            {
                if (_stylesheet.Accumulators.TryGetValue(accName, out var acc))
                    accumulators.Add(acc);
            }
            if (accumulators.Count > 0)
                await PreComputeAccumulatorsAsync(xdmDoc, accumulators, nodeStore).ConfigureAwait(false);
        }

        // If a fragment identifier is present, find the element with matching xml:id
        object contextItem = xdmDoc;
        if (!string.IsNullOrEmpty(fragment))
        {
            var fragmentId = fragment.TrimStart('#');
            var foundElement = FindElementByXmlId(xdmDoc, fragmentId);
            if (foundElement != null)
                contextItem = foundElement;
            // If not found, use the whole document (spec doesn't mandate an error)
        }

        // Push the loaded document (or fragment element) as context item (position=1, last=1)
        PushContextItem(contextItem, 1, 1);
        try
        {
            if (instruction.Content != null)
            {
                await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
            }
        }
        finally
        {
            PopContextItem();
            _accumulatorValues = previousAccumulatorValues;
        }
    }


    /// <summary>
    /// Serializes an XDM node to its XML string representation for passing to a sub-transform.
    /// </summary>
    internal string SerializeXdmNodeToXml(XdmNode node)
    {
        if (_nodeStore == null)
            return node.StringValue;
        var sb = new StringBuilder();
        SerializeXdmNodeToXml(node, sb);
        return sb.ToString();
    }


    private void SerializeXdmNodeToXml(XdmNode node, StringBuilder sb)
    {
        switch (node)
        {
            case XdmDocument doc:
                foreach (var child in _nodeStore!.GetChildren(doc))
                    SerializeXdmNodeToXml(child, sb);
                break;
            case XdmElement elem:
                var prefix = elem.Prefix;
                var localName = elem.LocalName;
                var qname = !string.IsNullOrEmpty(prefix) ? $"{prefix}:{localName}" : localName;
                sb.Append('<').Append(qname);
                foreach (var nsDecl in elem.NamespaceDeclarations)
                {
                    var nsUri = _nodeStore!.GetNamespaceUri(nsDecl.Namespace) ?? "";
                    if (string.IsNullOrEmpty(nsDecl.Prefix))
                    {
                        // xmlns="" is the ONE undeclaration XML allows.
                        sb.Append(" xmlns=\"").Append(nsUri).Append('"');
                    }
                    else if (!string.IsNullOrEmpty(nsUri))
                    {
                        sb.Append(" xmlns:").Append(nsDecl.Prefix).Append("=\"").Append(nsUri).Append('"');
                    }
                    // A PREFIXED declaration with an empty URI is written out as xmlns:p="",
                    // which XML 1.0 does not permit — only the default namespace can be
                    // undeclared, there is no syntax for retracting a prefix. Emitting it made
                    // this serialization unparseable, and it is the payload fn:transform uses to
                    // carry nodes across the engine boundary:
                    //
                    //   <context-child xmlns:mirror="" xmlns:x=""/>
                    //
                    // so the receiving side could not rebuild the element and produced an
                    // unnamed node instead. A prefix that is not bound is simply not declared;
                    // omitting it is the correct rendering, not an approximation of one.
                }
                foreach (var attr in _nodeStore!.GetAttributes(elem))
                {
                    var attrName = !string.IsNullOrEmpty(attr.Prefix) ? $"{attr.Prefix}:{attr.LocalName}" : attr.LocalName;
                    sb.Append(' ').Append(attrName).Append("=\"").Append(EscapeAttributeValue(attr.Value)).Append('"');
                }
                var children = _nodeStore.GetChildren(elem).ToList();
                if (children.Count == 0)
                {
                    sb.Append("/>");
                }
                else
                {
                    sb.Append('>');
                    foreach (var child in children)
                        SerializeXdmNodeToXml(child, sb);
                    sb.Append("</").Append(qname).Append('>');
                }
                break;
            case XdmText text:
                sb.Append(System.Security.SecurityElement.Escape(text.Value));
                break;
            case XdmComment comment:
                sb.Append("<!--").Append(comment.Value).Append("-->");
                break;
            case XdmProcessingInstruction pi:
                sb.Append("<?").Append(pi.Target);
                if (!string.IsNullOrEmpty(pi.Value))
                    sb.Append(' ').Append(pi.Value);
                sb.Append("?>");
                break;
        }
    }


    /// <summary>
    /// Output a value to the result tree as text.
    /// </summary>
    private void OutputValue(object? value)
    {
        if (value == null)
            return;

        // XTDE0450: Maps and function items cannot be added to element/document content
        if (value is IDictionary<object, object?>)
            throw Error("XTDE0450: An item in a sequence used as the content of an element or document node is a map");
        if (value is PhoenixmlDb.XQuery.Ast.XQueryFunction)
            throw Error("XTDE0450: An item in a sequence used as the content of an element or document node is a function item");
        // XSLT 3.0 §5.7.2: Arrays are flattened — serialize each member
        if (value is List<object?> arrayList)
        {
            foreach (var member in arrayList)
                OutputValue(member);
            return;
        }

        var text = StringValueOf(value);
        if (!string.IsNullOrEmpty(text))
        {
            EmitText(text);
        }
    }


    /// <summary>
    /// Output the result of xsl:evaluate. Node results are serialized (deep-copied like
    /// xsl:copy-of), while atomic values are output as text. Sequences of atomic values
    /// are space-separated per XSLT sequence serialization rules.
    /// </summary>
    private void OutputEvaluateResult(object? value)
    {
        if (value == null)
            return;

        if (value is object?[] arr)
        {
            // Check if any item is a node — if so, serialize each item individually
            var hasNodes = false;
            foreach (var item in arr)
            {
                if (item is XdmNode or XdmAttribute or System.Xml.XmlNode or System.Xml.Linq.XNode)
                {
                    hasNodes = true;
                    break;
                }
            }
            if (hasNodes)
            {
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] is XdmNode or XdmAttribute or System.Xml.XmlNode or System.Xml.Linq.XNode)
                        SerializeNode(arr[i]!);
                    else if (arr[i] != null)
                    {
                        var text = StringValueOf(arr[i]);
                        if (!string.IsNullOrEmpty(text))
                            EmitText(text);
                    }
                }
                return;
            }
            // All atomic values — fall through to OutputValue for space-separated output
            OutputValue(value);
            return;
        }

        // Single node result: serialize (deep-copy) to output
        if (value is XdmNode or XdmAttribute or System.Xml.XmlNode or System.Xml.Linq.XNode)
        {
            SerializeNode(value);
            return;
        }

        // Atomic values: output as text
        OutputValue(value);
    }


    /// <summary>
    /// Serializes a function return value to the output for initial-function invocation.
    /// Nodes are serialized as XML; atomic values are written as their string representation.
    /// </summary>
    internal void SerializeFunctionResult(object? result, StringBuilder outputBuilder)
    {
        if (result == null)
            return;

        if (result is object[] arr)
        {
            for (var i = 0; i < arr.Length; i++)
            {
                if (i > 0)
                    _sink.RawText(" ");
                SerializeFunctionResult(arr[i], outputBuilder);
            }
            return;
        }

        if (result is XdmNode node)
        {
            // Use SerializeNode which writes to _output
            SerializeNode(node);
            return;
        }

        if (result is ResultTreeFragment rtf)
        {
            _sink.RawText(rtf.XmlContent);
            return;
        }

        // Atomic values: serialize as string
        _sink.RawText(StringValueOf(result));
    }

}
