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
    /// Streams the children of the synthetic <c>_tmpl_wrap_</c> wrapper out of the
    /// supplied <see cref="System.Xml.XmlReader"/> and into <paramref name="result"/>
    /// as live XDM nodes registered in <c>_nodeStore</c>. Mirrors what
    /// <c>ConvertToXdm</c> does, but driven by reader events rather than a built DOM —
    /// avoids the per-call XmlDocument allocation that dominates wall time on workloads
    /// with many small <c>as=</c>-typed bodies.
    /// </summary>
    internal void ReadAsBodyChunkChildren(System.Xml.XmlReader reader, List<object?> result)
    {
        // Walk to the wrapper start tag, then descend one level so we read its children.
        if (!reader.Read())
            return;
        while (reader.NodeType != System.Xml.XmlNodeType.Element)
        {
            if (!reader.Read()) return;
        }
        // Reader is now at the start of `<_tmpl_wrap_>`. Step inside.
        if (reader.IsEmptyElement)
            return;
        reader.Read();

        // Read top-level children until the matching end tag.
        while (reader.NodeType != System.Xml.XmlNodeType.EndElement)
        {
            var node = ReadXdmNodeFromReader(reader, NodeId.None, new DocumentId(1));
            if (node != null)
                result.Add(node);
            else if (!reader.Read())
                break;
        }
    }


    /// <summary>
    /// Reads a single XDM-relevant node (element subtree, text, comment, PI) out of
    /// <paramref name="reader"/> and returns it. Returns <c>null</c> for irrelevant
    /// node types (whitespace at the wrapper level, XML declarations, etc.) and
    /// advances the reader past them so the caller can continue.
    /// </summary>
    private XdmNode? ReadXdmNodeFromReader(System.Xml.XmlReader reader, NodeId parentId, DocumentId docId)
    {
        switch (reader.NodeType)
        {
            case System.Xml.XmlNodeType.Element:
                return ReadXdmElementFromReader(reader, parentId, docId);

            case System.Xml.XmlNodeType.Text:
            case System.Xml.XmlNodeType.CDATA:
            case System.Xml.XmlNodeType.Whitespace:
            case System.Xml.XmlNodeType.SignificantWhitespace:
            {
                var textId = _nodeStore!.NextId();
                var value = reader.Value;
                var text = new XdmText
                {
                    Id = textId,
                    Document = docId,
                    Parent = parentId,
                    Value = value,
                };
                _nodeStore.Register(text);
                reader.Read();
                return text;
            }

            case System.Xml.XmlNodeType.Comment:
            {
                var commentId = _nodeStore!.NextId();
                var value = reader.Value;
                var comment = new XdmComment
                {
                    Id = commentId,
                    Document = docId,
                    Parent = parentId,
                    Value = value,
                };
                _nodeStore.Register(comment);
                reader.Read();
                return comment;
            }

            case System.Xml.XmlNodeType.ProcessingInstruction:
            {
                var piId = _nodeStore!.NextId();
                var pi = new XdmProcessingInstruction
                {
                    Id = piId,
                    Document = docId,
                    Parent = parentId,
                    Target = reader.Name,
                    Value = reader.Value,
                };
                _nodeStore.Register(pi);
                reader.Read();
                return pi;
            }

            default:
                // XmlDeclaration, DocumentType, EntityReference, etc. — skip
                reader.Read();
                return null;
        }
    }


    /// <summary>
    /// Reads an element subtree (start tag, attributes, namespaces, recursive children,
    /// end tag) out of <paramref name="reader"/> and returns it as a registered
    /// <see cref="XdmElement"/>. The reader is positioned past the matching end tag on
    /// return.
    /// </summary>
    private XdmElement ReadXdmElementFromReader(System.Xml.XmlReader reader, NodeId parentId, DocumentId docId)
    {
        var elemId = _nodeStore!.NextId();
        var localName = reader.LocalName;
        var prefix = reader.Prefix;
        var nsUri = reader.NamespaceURI;
        var elemNsId = _nodeStore.InternNamespace(nsUri ?? "");
        var isEmpty = reader.IsEmptyElement;

        // Walk attributes, splitting xmlns declarations from real attributes.
        var nsDecls = new List<NamespaceBinding>();
        var attrIds = new List<NodeId>();
        // RECOVER (temp-tree base-URI preservation): the sentinel attribute round-trips a
        // source element's base URI through the text serialize→reparse boundary. Capture it
        // here, stamp it onto CopySourceBaseUri below, and DROP both the attribute and its
        // namespace declaration so it never becomes a real XDM node or reaches output.
        string? recoveredBaseUri = null;
        if (reader.HasAttributes)
        {
            for (var i = 0; i < reader.AttributeCount; i++)
            {
                reader.MoveToAttribute(i);
                if (reader.Prefix == "xmlns")
                {
                    if (string.Equals(reader.Value, XsltTransformEngine.BaseSentinelNs, StringComparison.Ordinal))
                        continue; // drop the sentinel namespace declaration
                    nsDecls.Add(new NamespaceBinding(reader.LocalName, _nodeStore.InternNamespace(reader.Value)));
                }
                else if (string.IsNullOrEmpty(reader.Prefix) && reader.LocalName == "xmlns")
                {
                    nsDecls.Add(new NamespaceBinding("", _nodeStore.InternNamespace(reader.Value)));
                }
                else if (string.Equals(reader.NamespaceURI, XsltTransformEngine.BaseSentinelNs, StringComparison.Ordinal)
                    && reader.LocalName == XsltTransformEngine.BaseSentinelLocalName)
                {
                    recoveredBaseUri = reader.Value; // drop the sentinel attribute
                }
                else
                {
                    var attrId = _nodeStore.NextId();
                    var xdmAttr = new XdmAttribute
                    {
                        Id = attrId,
                        Document = docId,
                        Parent = elemId,
                        Namespace = _nodeStore.InternNamespace(reader.NamespaceURI ?? ""),
                        LocalName = reader.LocalName,
                        Prefix = string.IsNullOrEmpty(reader.Prefix) ? null : reader.Prefix,
                        Value = reader.Value,
                    };
                    _nodeStore.Register(xdmAttr);
                    attrIds.Add(attrId);
                }
            }
            reader.MoveToElement();
        }

        var childIds = new List<NodeId>();
        if (!isEmpty)
        {
            reader.Read(); // step inside the element
            while (reader.NodeType != System.Xml.XmlNodeType.EndElement)
            {
                var child = ReadXdmNodeFromReader(reader, elemId, docId);
                if (child != null)
                    childIds.Add(child.Id);
                else if (reader.NodeType != System.Xml.XmlNodeType.EndElement && !reader.Read())
                    break;
            }
        }
        reader.Read(); // consume the end tag (or the self-closing element itself)

        var elem = new XdmElement
        {
            StringValueResolver = _nodeStore.StringValueResolver,
            Id = elemId,
            Document = docId,
            Parent = parentId,
            Namespace = elemNsId,
            LocalName = localName,
            Prefix = string.IsNullOrEmpty(prefix) ? null : prefix,
            Attributes = attrIds,
            Children = childIds,
            NamespaceDeclarations = nsDecls.Count > 0 ? nsDecls.ToArray() : XdmElement.EmptyNamespaceDeclarations,
            CopySourceBaseUri = recoveredBaseUri,
        };
        // Precompute _stringValue bottom-up from child text and (already-finalized)
        // child element string values. Mirrors StreamingSubtreeMaterializer.FinalizeFrame
        // and XmlDocumentParser. Without this, value-of select="." on a variable-
        // constructed element (XdmElement.StringValue is non-lazy) returns "".
        if (childIds.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var childId in childIds)
            {
                var child = _nodeStore.GetNode(childId);
                switch (child)
                {
                    case XdmText t: sb.Append(t.Value); break;
                    case XdmElement ce: sb.Append(ce.StringValue); break;
                    // Comments and PIs contribute nothing to element string value.
                }
            }
            elem._stringValue = sb.ToString();
        }
        else
        {
            elem._stringValue = string.Empty;
        }
        _nodeStore.Register(elem);
        return elem;
    }


    private static decimal ParseVersionNumber(string version)
    {
        if (decimal.TryParse(version.Trim(), System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite, System.Globalization.CultureInfo.InvariantCulture, out var result))
            return result;
        // Non-numeric version strings default to 2.0 (no backwards compat) per spec
        return 2.0m;
    }


    /// <summary>
    /// Turns the lightweight <see cref="Xdm.TextNodeItem"/> marker into a real text node.
    /// </summary>
    /// <remarks>
    /// The marker exists so the sequence accumulator can tell a text NODE from an atomic string
    /// (XSLT 3.0 §5.7.2), and it is cheap precisely because it has no identity, parent or store.
    /// Every consumer that reasonably assumes a node has those breaks on it, so it must be
    /// materialized wherever a value stops being "sequence under construction" and becomes a
    /// node the stylesheet can navigate, compare or subtract.
    /// </remarks>
    private XdmText? MaterializeTextNodeItem(Xdm.TextNodeItem item)
    {
        if (_nodeStore == null) return null;
        var text = new XdmText
        {
            Id = _nodeStore.NextId(),
            Document = DocumentId.None,
            Parent = NodeId.None,
            Value = item.Value,
        };
        _nodeStore.Register(text);
        return text;
    }


    /// <summary>
    /// Parses a ResultTreeFragment (temporary tree) into an XDM document node.
    /// This allows RTFs to be processed by apply-templates.
    /// </summary>
    private XdmDocument? ParseResultTreeFragment(ResultTreeFragment rtf)
    {
        if (_nodeStore == null)
            return null;

        // Return cached parse result to preserve node identity across accesses
        if (rtf.CachedDocumentNode is XdmDocument cached)
            return cached;

        try
        {
            var xmlDoc = new System.Xml.XmlDocument();
            xmlDoc.PreserveWhitespace = true;
            // Wrap in a root element if the RTF is a fragment (multiple root elements or text at root)
            var content = rtf.XmlContent;
            var wasWrapped = false;
            try
            {
                xmlDoc.LoadXml(content);
            }
            catch (System.Xml.XmlException)
            {
                // Fragment - wrap in temporary root
                xmlDoc.LoadXml($"<_rtf_root_>{content}</_rtf_root_>");
                wasWrapped = true;
            }

            var xdmDoc = XsltTransformEngine.ConvertToXdm(xmlDoc, _nodeStore, documentUri: null);
            // Temporary trees have no document URI per XSLT 3.0 §11.9.1,
            // but need BaseUri for base-uri() resolution.
            xdmDoc.DocumentUri = null;
            xdmDoc.BaseUri = rtf.BaseUri;

            // If we wrapped in _rtf_root_, unwrap by promoting its children to be
            // direct children of the document node. This allows XPath like $var/*
            // to return the actual content elements, not the artificial wrapper.
            if (wasWrapped && xdmDoc.DocumentElement.HasValue && xdmDoc.DocumentElement.Value != NodeId.None)
            {
                var wrapperElem = _nodeStore.GetNode(xdmDoc.DocumentElement.Value) as XdmElement;
                if (wrapperElem != null && wrapperElem.LocalName == "_rtf_root_")
                {
                    // Reparent _rtf_root_'s children to the document
                    var newChildren = new List<NodeId>();
                    foreach (var childId in _nodeStore.GetChildren(wrapperElem).Select(c => c.Id))
                    {
                        newChildren.Add(childId);
                    }

                    // Find the first element child for DocumentElement
                    var newDocElemId = NodeId.None;
                    string? newDocElemLocalName = null;
                    foreach (var childId in newChildren)
                    {
                        if (_nodeStore.GetNode(childId) is XdmElement docElem)
                        {
                            newDocElemId = childId;
                            newDocElemLocalName = docElem.LocalName;
                            break;
                        }
                    }

                    // Create an updated document with unwrapped children
                    // Compute string value as concatenation of all text descendant values
                    var svBuilder = new System.Text.StringBuilder();
                    foreach (var childId in newChildren)
                    {
                        CollectStringValue(childId, svBuilder);
                    }

                    var newDoc = new XdmDocument
                    {
                        StringValueResolver = _nodeStore.StringValueResolver,
                        Id = xdmDoc.Id,
                        Document = xdmDoc.Document,
                        Parent = xdmDoc.Parent,
                        DocumentElement = newDocElemId,
                        DocumentUri = null, // RTFs have no document URI
                        Children = newChildren,
                        DocumentElementLocalName = newDocElemLocalName
                    };
                    newDoc.BaseUri = rtf.BaseUri;
                    newDoc._stringValue = svBuilder.ToString();
                    _nodeStore.Register(newDoc); // Overwrites the old registration
                    rtf.CachedDocumentNode = newDoc;
                    return newDoc;
                }
            }

            rtf.CachedDocumentNode = xdmDoc;
            return xdmDoc;
        }
        catch (System.Xml.XmlException)
        {
            // Invalid XML content - cannot parse RTF
            return null;
        }
    }


    /// <summary>
    /// Reads a full element subtree from the streaming reader starting at the current
    /// StartElement event, building a complete <see cref="Xdm.Nodes.XdmElement"/> with
    /// attributes and child element / text content. Consumes events through the
    /// matching EndElement. Used by both <see cref="ApplyTemplatesStreamingAsync"/> and
    /// <see cref="ForEachGroupStreamingAsync"/>.
    /// </summary>
    private async ValueTask<Xdm.Nodes.XdmElement> ReadStreamingElementForDispatchAsync(
        System.Xml.XmlReader reader, CancellationToken ct)
    {
        var startDepth = reader.Depth;
        var startName = reader.LocalName;
        var startNs = reader.NamespaceURI;
        var startPrefix = reader.Prefix;
        var startId = new NodeId(_nextStreamGroupNodeId++);
        var attrIds = new List<NodeId>();
        var nsId = !string.IsNullOrEmpty(startNs)
            ? _nodeStore!.InternNamespace(startNs)
            : NamespaceId.None;

        if (reader.HasAttributes)
        {
            for (int i = 0; i < reader.AttributeCount; i++)
            {
                reader.MoveToAttribute(i);
                if (reader.Prefix == "xmlns" || (reader.Prefix.Length == 0 && reader.LocalName == "xmlns"))
                    continue;
                var attrNsId = !string.IsNullOrEmpty(reader.NamespaceURI)
                    ? _nodeStore!.InternNamespace(reader.NamespaceURI)
                    : NamespaceId.None;
                var attrId = new NodeId(_nextStreamGroupNodeId++);
                var attrNode = new Xdm.Nodes.XdmAttribute
                {
                    Id = attrId,
                    Document = DocumentId.None,
                    Parent = startId,
                    LocalName = reader.LocalName,
                    Namespace = attrNsId,
                    Prefix = string.IsNullOrEmpty(reader.Prefix) ? null : reader.Prefix,
                    Value = reader.Value
                };
                _nodeStore!.Register(attrNode);
                attrIds.Add(attrId);
            }
            reader.MoveToElement();
        }

        var children = new List<NodeId>();
        if (!reader.IsEmptyElement)
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType == System.Xml.XmlNodeType.EndElement && reader.Depth == startDepth)
                    break;
                switch (reader.NodeType)
                {
                    case System.Xml.XmlNodeType.Element:
                    {
                        var child = await ReadStreamingElementForDispatchAsync(reader, ct).ConfigureAwait(false);
                        child.Parent = startId;
                        children.Add(child.Id);
                        break;
                    }
                    case System.Xml.XmlNodeType.Text:
                    case System.Xml.XmlNodeType.CDATA:
                    case System.Xml.XmlNodeType.SignificantWhitespace:
                    case System.Xml.XmlNodeType.Whitespace:
                    {
                        var textId = new NodeId(_nextStreamGroupNodeId++);
                        var textNode = new Xdm.Nodes.XdmText
                        {
                            Id = textId,
                            Document = DocumentId.None,
                            Parent = startId,
                            Value = reader.Value
                        };
                        _nodeStore!.Register(textNode);
                        children.Add(textId);
                        break;
                    }
                }
            }
        }

        var elem = new Xdm.Nodes.XdmElement
        {
            StringValueResolver = _nodeStore!.StringValueResolver,
            Id = startId,
            Document = DocumentId.None,
            Parent = NodeId.None,
            LocalName = startName,
            Namespace = nsId,
            Prefix = string.IsNullOrEmpty(startPrefix) ? null : startPrefix,
            Children = children,
            Attributes = attrIds,
            NamespaceDeclarations = Array.Empty<NamespaceBinding>()
        };
        _nodeStore!.Register(elem);
        return elem;
    }


    /// <summary>
    /// Loads the serialization-parameters document referenced by an xsl:result-document
    /// <c>parameter-document</c> (with the href already evaluated), returning its output method (if
    /// declared) and the combined character mappings declared by its <c>output:use-character-maps</c>
    /// children. The URI is resolved against the stylesheet base URI. Other simple serialization
    /// parameters are not yet consumed from a result-document parameter document — only the two that
    /// XSLT 3.0 §27.1 test result-document-1406 exercises (method + character maps). (XTDE0010 on
    /// resolution/read/well-formedness failure, matching xsl:output/@parameter-document handling.)
    /// </summary>
    private (OutputMethod? Method, Dictionary<int, string>? CharacterMap) LoadResultDocumentParameterDocument(string href)
    {
        // Resolve against the effective (static, xml:base-aware) base URI, falling back to the module base.
        Uri? baseUri = _stylesheet.BaseUri;
        if (StaticBaseUri is { } sbu && Uri.TryCreate(sbu, UriKind.Absolute, out var staticBase))
            baseUri = staticBase;
        Uri resolvedUri;
        if (Uri.TryCreate(href, UriKind.Absolute, out var absUri))
            resolvedUri = absUri;
        else if (baseUri != null)
            resolvedUri = new Uri(baseUri, href);
        else
            throw Error($"XTDE0010: Cannot resolve result-document parameter document '{href}' without a base URI");

        if (_options?.ResourcePolicy is { } policy &&
            !policy.IsAllowed(resolvedUri, PhoenixmlDb.XQuery.Security.ResourceAccessKind.ReadDocument))
            throw Error($"XTDE0010: Resource policy denied access to result-document parameter document '{href}'");

        string xml;
        try
        {
            if (resolvedUri.Scheme == Uri.UriSchemeHttp || resolvedUri.Scheme == Uri.UriSchemeHttps)
            {
                if (_options?.PreloadedResources is { } preloaded && preloaded.TryGet(resolvedUri, out var preloadedXml))
                    xml = preloadedXml;
                else if (OperatingSystem.IsBrowser())
                    throw Error($"XTDE0010: Cannot fetch result-document parameter document '{href}' on Blazor WebAssembly synchronously.");
                else
                {
                    using var stream = HttpDocumentLoader.OpenRead(resolvedUri);
                    using var reader = new System.IO.StreamReader(stream);
                    xml = reader.ReadToEnd();
                }
            }
            else if (resolvedUri.IsFile)
            {
                xml = System.IO.File.ReadAllText(resolvedUri.LocalPath);
            }
            else
            {
                throw Error($"XTDE0010: Cannot read result-document parameter document '{href}': unsupported URI scheme '{resolvedUri.Scheme}'");
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw Error($"XTDE0010: Cannot read result-document parameter document '{href}': {ex.Message}");
        }

        System.Xml.Linq.XDocument doc;
        try
        {
            doc = System.Xml.Linq.XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException ex)
        {
            throw Error($"XTDE0010: Result-document parameter document '{href}' is not well-formed: {ex.Message}");
        }

        var root = doc.Root;
        if (root == null || root.Name.Namespace != RdSerializationParamsNs || root.Name.LocalName != "serialization-parameters")
            throw Error($"XTDE0010: Result-document parameter document '{href}' root must be output:serialization-parameters");

        OutputMethod? method = null;
        Dictionary<int, string>? charMap = null;
        foreach (var child in root.Elements())
        {
            if (child.Name.Namespace != RdSerializationParamsNs)
                continue;
            if (child.Name.LocalName == "use-character-maps")
            {
                foreach (var cm in child.Elements(RdSerializationParamsNs + "character-map"))
                {
                    var ch = cm.Attribute("character")?.Value;
                    var mapStr = cm.Attribute("map-string")?.Value;
                    if (ch == null || mapStr == null)
                        continue;
                    charMap ??= new Dictionary<int, string>();
                    if (ch.Length == 1)
                        charMap[ch[0]] = mapStr;
                    else if (ch.Length == 2 && char.IsHighSurrogate(ch[0]) && char.IsLowSurrogate(ch[1]))
                        charMap[char.ConvertToUtf32(ch[0], ch[1])] = mapStr;
                }
            }
            else if (child.Name.LocalName == "method")
            {
                method = child.Attribute("value")?.Value.Trim() switch
                {
                    "xml" => OutputMethod.Xml,
                    "html" => OutputMethod.Html,
                    "xhtml" => OutputMethod.Xhtml,
                    "text" => OutputMethod.Text,
                    "json" => OutputMethod.Json,
                    "adaptive" => OutputMethod.Adaptive,
                    _ => method
                };
            }
        }
        return (method, charMap);
    }


    private static (string? prefix, List<string> formats, List<string> separators, string? suffix) ParseFormatTokens(string format)
    {
        if (string.IsNullOrEmpty(format))
            return (null, ["1"], ["."], null);

        var formats = new List<string>();
        var separators = new List<string>();
        string? prefix = null;
        string? suffix = null;

        var i = 0;

        // Extract prefix (non-format-token chars at start)
        var prefixEnd = 0;
        while (prefixEnd < format.Length && GetUnicodeFormatTokenLength(format, prefixEnd) == 0)
            prefixEnd++;
        if (prefixEnd > 0)
            prefix = format[..prefixEnd];
        i = prefixEnd;

        while (i < format.Length)
        {
            // Read format token (may include surrogate pairs)
            var tokenStart = i;
            int tokenLen;
            while ((tokenLen = GetUnicodeFormatTokenLength(format, i)) > 0)
                i += tokenLen;
            if (i > tokenStart)
                formats.Add(format[tokenStart..i]);

            // Read separator (non-format-token chars)
            var sepStart = i;
            while (i < format.Length && GetUnicodeFormatTokenLength(format, i) == 0)
                i++;
            if (i > sepStart)
            {
                if (i < format.Length) // More tokens follow
                    separators.Add(format[sepStart..i]);
                else
                    suffix = format[sepStart..i]; // Trailing chars are suffix
            }
        }

        if (formats.Count == 0)
        {
            formats.Add("1");
            // XSLT spec: if format contains only non-alphanumeric characters,
            // they are used as both prefix and suffix
            if (prefix != null && suffix == null)
                suffix = prefix;
        }

        return (prefix, formats, separators, suffix);
    }


    /// <summary>
    /// Parses a raw attribute string (e.g., ' color="black" size="14pt"') into a dictionary.
    /// Last occurrence of same name wins.
    /// </summary>
    private static void ParseAttributeString(string attrString, Dictionary<string, string> target)
    {
        var pos = 0;
        while (pos < attrString.Length)
        {
            // Skip whitespace
            while (pos < attrString.Length && char.IsWhiteSpace(attrString[pos]))
                pos++;

            if (pos >= attrString.Length)
                break;

            // Read attribute name (until '=')
            var nameStart = pos;
            while (pos < attrString.Length && attrString[pos] != '=')
                pos++;

            if (pos >= attrString.Length)
                break;

            var name = attrString[nameStart..pos].Trim();
            pos++; // skip '='

            // Skip optional whitespace and opening quote
            while (pos < attrString.Length && char.IsWhiteSpace(attrString[pos]))
                pos++;

            if (pos >= attrString.Length)
                break;

            var quote = attrString[pos];
            if (quote != '"' && quote != '\'')
                break;
            pos++; // skip opening quote

            // Read value until closing quote
            var valueStart = pos;
            while (pos < attrString.Length && attrString[pos] != quote)
                pos++;

            var value = attrString[valueStart..pos];
            if (pos < attrString.Length)
                pos++; // skip closing quote

            if (!string.IsNullOrEmpty(name))
            {
                target[name] = value;
            }
        }
    }

}
