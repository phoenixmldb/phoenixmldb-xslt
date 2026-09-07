using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

public sealed partial class StylesheetParser
{

    /// <summary>
    /// Resolves a namespace URI to its interned NamespaceId, creating one if needed.
    /// Used by the public API to match template/mode names against compiled stylesheet names.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1054:URI-like parameters should not be strings")]
    public static NamespaceId ResolveNamespaceUri(string namespaceUri)
    {
        if (string.IsNullOrEmpty(namespaceUri))
            return NamespaceId.None;
        if (_wellKnownNamespaces.TryGetValue(namespaceUri, out var nsId))
            return nsId;
        // Thread-safe intern: GetOrAdd guarantees a single stored id per URI even when the
        // factory runs concurrently for the same key, and Interlocked.Increment makes id
        // allocation atomic. Under contention the factory may run more than once and skip a
        // few ids — harmless, since ids only need to be unique. (#116)
        return _dynamicNamespaces.GetOrAdd(namespaceUri,
            _ => new NamespaceId(Interlocked.Increment(ref _nextNamespaceId)));
    }


    private static Dictionary<(int Line, int Col), string>? BuildElementPrefixMap(string xml)
    {
        Dictionary<(int, int), string>? map = null;
        try
        {
            using var reader = XmlReader.Create(new System.IO.StringReader(xml), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore
            });
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element && reader is IXmlLineInfo lineInfo && lineInfo.HasLineInfo())
                {
                    // Record EVERY element's source prefix (empty for default-namespace
                    // elements). Recording even the empty case is important: it lets
                    // ParseLiteralResultElement distinguish "this element had no prefix
                    // in source" (lookup hit with empty value → don't propagate any
                    // ancestor's prefix) from "no entry exists for this position"
                    // (lookup miss → fall back to LINQ walk).
                    //
                    // Without the empty-case recording, a default-ns element would miss
                    // the map and fall through to the LINQ walk, which can return a
                    // non-empty prefix because LINQ's GetPrefixOfNamespace finds ANY
                    // ancestor xmlns:* declaration matching the element's namespace —
                    // even when the element itself had no prefix in source. That bug
                    // surfaced as Martin Honnen's `<theme>` and later `<link>` LREs
                    // being serialized as `<xsl:theme>` / `<xsl:link>` in Docbook TNG.
                    map ??= new Dictionary<(int, int), string>();
                    map[(lineInfo.LineNumber, lineInfo.LinePosition)] = reader.Prefix ?? "";
                    // Also record attribute prefixes (needed when multiple prefixes share same URI)
                    if (reader.HasAttributes)
                    {
                        for (int i = 0; i < reader.AttributeCount; i++)
                        {
                            reader.MoveToAttribute(i);
                            if (!string.IsNullOrEmpty(reader.Prefix) && !reader.IsDefault
                                && reader.Prefix != "xmlns"
                                && reader is IXmlLineInfo attrLineInfo && attrLineInfo.HasLineInfo())
                            {
                                map[(attrLineInfo.LineNumber, attrLineInfo.LinePosition)] = reader.Prefix;
                            }
                        }
                        reader.MoveToElement();
                    }
                }
            }
        }
        catch (XmlException)
        {
            // If XmlReader fails (e.g. DTD issues), fall back to LINQ to XML prefix resolution
        }
        return map;
    }


    private static void ValidateAttributeSetReferences(XsltStylesheet stylesheet)
    {
        foreach (var (name, attrSet) in stylesheet.AttributeSets)
        {
            foreach (var usedName in attrSet.UseAttributeSets)
            {
                // use-attribute-sets="xsl:original" is resolved dynamically against the
                // overriding attribute-set's overridden component; it is not a named set.
                if (usedName.Namespace == NamespaceId.Xslt && usedName.LocalName == "original")
                    continue;
                if (stylesheet.AttributeSets.ContainsKey(usedName))
                    continue;
                // Package-local resolution: a set merged from a used package resolves its
                // own use-attribute-sets references within that package's scope, which
                // includes the package's private and abstract sets (override-as-005,
                // accept-046/047). Only fall through to XTSE0710 if unresolved there too.
                if (attrSet.PackageStylesheet != null
                    && (attrSet.PackageStylesheet.AttributeSets.ContainsKey(usedName)
                        || attrSet.PackageStylesheet.AbstractAttributeSetNames.Contains(usedName)))
                    continue;
                throw new XsltException($"XTSE0710: Attribute set '{usedName}' referenced by '{name}' is not defined");
            }
        }

        // Also validate use-attribute-sets on instructions (xsl:copy, xsl:element, LREs)
        foreach (var template in stylesheet.Templates)
        {
            ValidateUseAttributeSetRefsInInstructions(template.Body, stylesheet);
        }
        // XTSE0720: Check for circular attribute set references
        foreach (var (name, _) in stylesheet.AttributeSets)
        {
            var visited = new HashSet<QName>();
            var queue = new Queue<QName>();
            queue.Enqueue(name);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!stylesheet.AttributeSets.TryGetValue(current, out var currentSet))
                    continue;
                foreach (var usedName in currentSet.UseAttributeSets)
                {
                    if (usedName == name)
                        throw new XsltException($"XTSE0720: Attribute set '{name}' directly or indirectly references itself");
                    if (visited.Add(usedName))
                        queue.Enqueue(usedName);
                }
            }
        }
    }


    private XsltAttributeSet ParseAttributeSet(XElement element)
    {
        // XTSE0090: Validate no unknown attributes
        ValidateAllowedAttributes(element, GetSourceLocation(element), "name", "use-attribute-sets", "visibility", "streamable");

        // XTSE0010: name is REQUIRED on xsl:attribute-set. Dereferenced with `!`, so omitting
        // it produced a NullReferenceException rather than a diagnosis (error-0010q).
        var nameValue = element.Attribute("name")?.Value
            ?? throw new XsltException("XTSE0010: xsl:attribute-set requires a 'name' attribute",
                GetSourceLocation(element));

        // XTSE0020: Validate name is a valid QName
        ValidateQNameValue(nameValue, "name", GetSourceLocation(element));

        var name = ParseQName(nameValue, element);
        var useAttr = element.Attribute("use-attribute-sets");

        var useAttributeSets = new List<QName>();
        if (useAttr != null)
        {
            foreach (var n in useAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                useAttributeSets.Add(ParseQName(n, element));
            }
        }

        // XTSE0010: xsl:attribute-set may only contain xsl:attribute children and no text
        foreach (var node in element.Nodes())
        {
            if (node is XText text && !IsXmlWhitespaceOnly(text.Value))
                throw new XsltException("XTSE0010: Text content is not allowed in xsl:attribute-set", GetSourceLocation(element));
            if (node is XElement child && child.Name != XsltNs + "attribute")
                throw new XsltException($"XTSE0010: Only xsl:attribute is allowed as a child of xsl:attribute-set, found {child.Name.LocalName}",
                    GetSourceLocation(child));
        }

        var attributes = new List<XsltAttribute>();
        foreach (var child in element.Elements(XsltNs + "attribute"))
        {
            attributes.Add((XsltAttribute)ParseInstruction(child));
        }

        var streamableAttr = element.Attribute("streamable")?.Value?.Trim();
        var streamable = streamableAttr != null && NormalizeYesNo(streamableAttr, "streamable", "xsl:attribute-set", element);

        // XTSE3430: If declared streamable="yes", validate that attribute expressions are actually streamable
        if (streamable)
        {
            StreamabilityChecker.CheckStreamableAttributeSet(attributes, GetSourceLocation(element));
        }

        return new XsltAttributeSet
        {
            Name = name,
            UseAttributeSets = useAttributeSets,
            Attributes = attributes,
            Visibility = ParseVisibility(element.Attribute("visibility")?.Value),
            IsAbstract = element.Attribute("visibility")?.Value == "abstract",
            Streamable = streamable,
            BaseUri = ResolveEffectiveBaseUri(element)
        };
    }


    private XsltElement ParseElement(XElement element, SourceLocation? location)
    {
        ValidateAllowedAttributes(element, location,
            "name", "namespace", "use-attribute-sets", "inherit-namespaces", "validation", "type");

        var nameAttr = element.Attribute("name");
        if (nameAttr == null)
            throw new XsltException("XTSE0010: xsl:element must have a name attribute", location);
        var name = ParseAvt(nameAttr.Value, element, nameAttr);
        var namespaceAttr = element.Attribute("namespace");
        var useAttributeSetsAttr = element.Attribute("use-attribute-sets");
        var inheritNamespacesAttr = element.Attribute("inherit-namespaces");
        var validationAttr = element.Attribute("validation");
        var typeAttr = element.Attribute("type");

        // XTSE1660: Non-schema-aware processor must reject type attribute
        if (typeAttr != null && ShouldRejectSchemaAware)
            throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the type attribute on xsl:element", location);
        // XTSE1660: Non-schema-aware processor must reject validation="strict" or "type"
        if (validationAttr != null)
        {
            var v = validationAttr.Value.Trim();
            if (v is "strict" or "type" && ShouldRejectSchemaAware)
                throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept validation=\"{v}\" on xsl:element", location);
        }

        // XTSE0020: Validate inherit-namespaces value
        if (inheritNamespacesAttr != null && ParseYesNo(inheritNamespacesAttr) == null)
            throw new XsltException($"XTSE0020: Invalid value '{inheritNamespacesAttr.Value}' for inherit-namespaces attribute", location);

        var useAttributeSets = new List<QName>();
        if (useAttributeSetsAttr != null)
        {
            foreach (var n in useAttributeSetsAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                useAttributeSets.Add(ParseQName(n, element));
            }
        }

        // Capture in-scope namespace bindings for prefix resolution at runtime
        var inScopeNamespaces = new Dictionary<string, string>();
        foreach (var nsAttr in element.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            var prefix = nsAttr.Name.LocalName == "xmlns" ? "" : nsAttr.Name.LocalName;
            inScopeNamespaces[prefix] = nsAttr.Value;
        }
        // Also include inherited namespaces from ancestor elements
        for (var ancestor = element.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            foreach (var nsAttr in ancestor.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                var prefix = nsAttr.Name.LocalName == "xmlns" ? "" : nsAttr.Name.LocalName;
                inScopeNamespaces.TryAdd(prefix, nsAttr.Value);
            }
        }

        return new XsltElement
        {
            Location = location,
            Name = name,
            Namespace = namespaceAttr != null ? ParseAvt(namespaceAttr.Value, element, namespaceAttr) : null,
            UseAttributeSets = useAttributeSets,
            InheritNamespaces = ParseYesNo(inheritNamespacesAttr),
            Validation = ParseValidationMode(validationAttr),
            // Type: rejected by XTSE1660 for non-schema-aware processors
            Content = ParseSequenceConstructor(element),
            InScopeNamespaces = inScopeNamespaces,
            BaseUri = ResolveEffectiveBaseUri(element)
        };
    }


    private XsltAttribute ParseAttribute(XElement element, SourceLocation? location)
    {
        var nameAttr = element.Attribute("name");
        if (nameAttr == null)
            throw new XsltException("XTSE0010: xsl:attribute must have a name attribute", location);
        var name = ParseAvt(nameAttr.Value, element, nameAttr);
        var namespaceAttr = element.Attribute("namespace");
        var selectAttr = element.Attribute("select");
        var separatorAttr = element.Attribute("separator");
        var validationAttr = element.Attribute("validation");
        var typeAttr = element.Attribute("type");

        // XTSE1660: Non-schema-aware processor must reject type attribute
        if (typeAttr != null && ShouldRejectSchemaAware)
            throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the type attribute on xsl:attribute", location);
        if (validationAttr != null)
        {
            var v = validationAttr.Value.Trim();
            if (v is "strict" or "type" && ShouldRejectSchemaAware)
                throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept validation=\"{v}\" on xsl:attribute", location);
        }

        // XTSE0840: select and non-empty content are mutually exclusive
        ValidateSelectContentExclusive(selectAttr, element, "XTSE0840", "xsl:attribute", location);

        // Collect in-scope namespaces for prefix resolution (XTDE0860)
        var inScopeNamespaces = new Dictionary<string, string>();
        foreach (var nsAttr in element.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            var prefix = nsAttr.Name.LocalName == "xmlns" ? "" : nsAttr.Name.LocalName;
            inScopeNamespaces[prefix] = nsAttr.Value;
        }
        for (var ancestor = element.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            foreach (var nsAttr in ancestor.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                var prefix = nsAttr.Name.LocalName == "xmlns" ? "" : nsAttr.Name.LocalName;
                inScopeNamespaces.TryAdd(prefix, nsAttr.Value);
            }
        }

        return new XsltAttribute
        {
            Location = location,
            Name = name,
            Namespace = namespaceAttr != null ? ParseAvt(namespaceAttr.Value, element, namespaceAttr) : null,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.Nodes().Any()
                ? ParseSequenceConstructor(element)
                : null,
            Separator = separatorAttr != null ? ParseAvt(separatorAttr.Value, element, separatorAttr) : null,
            Validation = ParseValidationMode(validationAttr),
            // Type: rejected by XTSE1660 for non-schema-aware processors
            InScopeNamespaces = inScopeNamespaces
        };
    }


    private XsltInstruction ParseText(XElement element, SourceLocation? location)
    {
        // XTSE0010: xsl:text must not contain child elements
        if (element.Elements().Any())
            throw new XsltException("XTSE0010: xsl:text must not contain child elements", location);

        var doeTextAttr = element.Attribute("disable-output-escaping");
        ValidateDoeAttribute(doeTextAttr, location);
        var doe = doeTextAttr?.Value.Trim() is "yes" or "true" or "1";
        var expandText = IsExpandTextActive(element);
        var value = element.Value;

        // When expand-text is active and the text contains TVT expressions, parse as TVT
        if (expandText && value.Contains('{', StringComparison.Ordinal))
        {
            var avt = ParseAvtFromText(value, element);
            // If the AVT resolved to just a single literal, keep as plain text
            if (avt.Parts.Count == 1 && avt.Parts[0] is AvtLiteral)
            {
                return new XsltText { Location = location, Value = value, DisableOutputEscaping = doe };
            }
            return new XsltTextValueTemplate { Template = avt, Location = location };
        }

        return new XsltText
        {
            Location = location,
            Value = value,
            DisableOutputEscaping = doe
        };
    }


    private XsltComment ParseComment(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select");

        // XTSE0940: select and non-empty content are mutually exclusive
        ValidateSelectContentExclusive(selectAttr, element, "XTSE0940", "xsl:comment", location);

        return new XsltComment
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.Nodes().Any()
                ? ParseSequenceConstructor(element)
                : null
        };
    }


    private XsltNamespace ParseNamespaceInstr(XElement element, SourceLocation? location)
    {
        var name = ParseAvt(element.Attribute("name")!.Value, element, element.Attribute("name"));
        var selectAttr = element.Attribute("select");

        // XTSE0910: select and non-empty content are mutually exclusive
        ValidateSelectContentExclusive(selectAttr, element, "XTSE0910", "xsl:namespace", location);

        return new XsltNamespace
        {
            Location = location,
            Name = name,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.Nodes().Any()
                ? ParseSequenceConstructor(element)
                : null
        };
    }


    /// <summary>
    /// Creates either a plain XsltLiteralText or an XsltTextValueTemplate depending
    /// on whether expand-text is active and the text contains curly braces.
    /// </summary>
    private XsltInstruction CreateTextInstruction(string value, bool expandText, XElement context)
    {
        if (expandText && value.Contains('{', StringComparison.Ordinal))
        {
            var avt = ParseAvtFromText(value, context);
            // If the AVT is just a single literal (no expressions), use the literal's
            // decoded value (with empty TVT expressions like {} stripped)
            if (avt.Parts.Count == 1 && avt.Parts[0] is AvtLiteral lit)
            {
                return new XsltLiteralText { Value = lit.Value };
            }
            if (avt.Parts.Count == 0)
            {
                // All expressions were empty — produce empty text (vacuous)
                return new XsltLiteralText { Value = "" };
            }
            return new XsltTextValueTemplate { Template = avt };
        }
        return new XsltLiteralText { Value = value };
    }

}
