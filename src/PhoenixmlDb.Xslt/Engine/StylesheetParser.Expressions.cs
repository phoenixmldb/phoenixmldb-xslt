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
    /// XPST0017: Validates that function calls in match pattern predicates reference
    /// known functions (standard library or user-defined xsl:function).
    /// </summary>
    private static void ValidatePatternFunctionReferences(XsltStylesheet stylesheet)
    {
        var lib = PhoenixmlDb.XQuery.Functions.FunctionLibrary.Standard;
        // Well-known namespace IDs that contain standard functions
        var wellKnownNamespaces = new HashSet<NamespaceId>
        {
            NamespaceId.None,
            PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn,
            PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Xs,
            PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Math,
            PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Map,
            PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Array,
            PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Local,
            NamespaceId.Xslt,
        };

        foreach (var template in stylesheet.Templates)
        {
            if (template.Match != null)
            {
                ValidatePatternFunctionsInPattern(template.Match, stylesheet, lib, wellKnownNamespaces);
            }
        }
    }


    private static void ValidatePatternFunctionsInPattern(
        XsltPattern pattern, XsltStylesheet stylesheet,
        PhoenixmlDb.XQuery.Functions.FunctionLibrary lib,
        HashSet<NamespaceId> wellKnownNamespaces)
    {
        switch (pattern)
        {
            case PathPattern pp:
                foreach (var step in pp.Steps)
                {
                    foreach (var pred in step.Predicates)
                        ValidateFunctionCallsInExpression(pred, stylesheet, lib, wellKnownNamespaces);
                }
                break;
            case UnionPattern up:
                foreach (var p in up.Patterns)
                    ValidatePatternFunctionsInPattern(p, stylesheet, lib, wellKnownNamespaces);
                break;
            case ExceptPattern ep:
                ValidatePatternFunctionsInPattern(ep.Left, stylesheet, lib, wellKnownNamespaces);
                ValidatePatternFunctionsInPattern(ep.Right, stylesheet, lib, wellKnownNamespaces);
                break;
            case IntersectPattern ip:
                ValidatePatternFunctionsInPattern(ip.Left, stylesheet, lib, wellKnownNamespaces);
                ValidatePatternFunctionsInPattern(ip.Right, stylesheet, lib, wellKnownNamespaces);
                break;
            case DotPattern dp:
                foreach (var pred in dp.Predicates)
                    ValidateFunctionCallsInExpression(pred, stylesheet, lib, wellKnownNamespaces);
                break;
            case ParenthesizedPositionalPattern ppp:
                ValidatePatternFunctionsInPattern(ppp.Inner, stylesheet, lib, wellKnownNamespaces);
                foreach (var pred in ppp.Predicates)
                    ValidateFunctionCallsInExpression(pred, stylesheet, lib, wellKnownNamespaces);
                break;
        }
    }


    private XsltLiteralResultElement ParseLiteralResultElement(XElement element, SourceLocation? location)
    {
        // Determine the prefix the stylesheet author intended for this LRE.
        // The XmlReader-built `_elementPrefixMap` is authoritative: it records EVERY
        // element's source prefix (including empty string for default-namespace elements).
        // Map hit with empty value → element had no prefix in source, return null/empty
        // and let serialization use the default-namespace binding. Map hit with non-empty
        // → use that exact prefix. Only fall through to the LINQ walk on a true miss
        // (e.g. when the prefix map couldn't be built — DTD failure, etc.).
        //
        // Earlier versions tried to skip the map for elements LINQ-to-XML thought had no
        // prefix, but `XElement.GetPrefixOfNamespace` returns ANY ancestor's prefix that
        // maps to the element's namespace — so for an element in the default xhtml namespace
        // when both `xmlns="xhtml"` and `xmlns:h="xhtml"` are in scope, LINQ returned "h"
        // and the guard let stale prefix-map hits leak through. Recording the source's
        // empty prefix in the map closes that hole. (Martin Honnen Docbook TNG bug — first
        // surfaced as `<xsl:theme>`, persisted as `<xsl:link>` after the partial fix.)
        string? lrePrefix = null;
        if (element.Name.Namespace != XNamespace.None)
        {
            if (_elementPrefixMap != null && element is IXmlLineInfo eli && eli.HasLineInfo()
                && _elementPrefixMap.TryGetValue((eli.LineNumber, eli.LinePosition), out var originalPrefix))
            {
                lrePrefix = string.IsNullOrEmpty(originalPrefix) ? null : originalPrefix;
            }
            else
            {
                // Fallback: walk up ancestor namespace declarations
                var nsUri = element.Name.NamespaceName;
                var found = false;
                for (var el = element; el != null && !found; el = el.Parent)
                {
                    foreach (var a in el.Attributes().Where(a => a.IsNamespaceDeclaration && a.Value == nsUri))
                    {
                        if (a.Name.Namespace == XNamespace.None) // xmlns="..." (default ns)
                            lrePrefix = null;
                        else
                            lrePrefix = a.Name.LocalName; // xmlns:prefix="..."
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    lrePrefix = element.GetPrefixOfNamespace(element.Name.Namespace);
                    if (string.IsNullOrEmpty(lrePrefix))
                        lrePrefix = null;
                }
            }
        }

        var name = new QName(
            new NamespaceId(0), // Would need proper namespace resolution
            element.Name.LocalName,
            lrePrefix
        );

        var attributes = new Dictionary<QName, XsltAttributeValueTemplate>();
        var namespaceDeclarations = new Dictionary<string, string>();
        var useAttributeSets = new List<QName>();
        var excludeResultPrefixes = new HashSet<string>();
        bool? inheritNamespaces = null;
        string? version = null;
        string? defaultCollation = null;

        foreach (var attr in element.Attributes())
        {
            if (attr.IsNamespaceDeclaration)
            {
                var prefix = attr.Name.LocalName == "xmlns" ? "" : attr.Name.LocalName;
                namespaceDeclarations[prefix] = attr.Value;
            }
            else if (attr.Name.Namespace == XsltNs)
            {
                // XSLT extension attributes
                switch (attr.Name.LocalName)
                {
                    case "use-attribute-sets":
                        foreach (var n in attr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            useAttributeSets.Add(ParseQName(n, element));
                        }
                        break;
                    case "inherit-namespaces":
                        // XSLT 3.0: accepts "yes", "no", "true", "false", "1", "0" with whitespace
                        inheritNamespaces = attr.Value.Trim() switch
                        {
                            "yes" or "true" or "1" => true,
                            "no" or "false" or "0" => false,
                            _ => null
                        };
                        break;
                    case "exclude-result-prefixes":
                        foreach (var p in attr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            if (p == "#all")
                            {
                                excludeResultPrefixes.Add(p);
                            }
                            else if (p == "#default")
                            {
                                // XTSE0809: #default requires a default namespace binding
                                if (string.IsNullOrEmpty(element.GetDefaultNamespace().NamespaceName))
                                    throw new XsltException("XTSE0809: The value '#default' is used in exclude-result-prefixes but the element has no default namespace",
                                        GetSourceLocation(element));
                                excludeResultPrefixes.Add(p);
                            }
                            else
                            {
                                // XTSE0808: prefix must have an in-scope namespace binding
                                var ns = element.GetNamespaceOfPrefix(p);
                                if (ns == null)
                                    throw new XsltException($"XTSE0808: Namespace prefix '{p}' used in exclude-result-prefixes is not declared",
                                        GetSourceLocation(element));
                                excludeResultPrefixes.Add(p);
                            }
                        }
                        break;
                    case "extension-element-prefixes":
                        foreach (var p in attr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        {
                            // XTSE1430: validate prefix is bound
                            if (p != "#default")
                            {
                                var extNs = element.GetNamespaceOfPrefix(p);
                                if (extNs == null)
                                    throw new XsltException($"XTSE1430: Namespace prefix '{p}' used in extension-element-prefixes is not declared",
                                        GetSourceLocation(element));
                                // XTSE0800: A reserved namespace must not be used as an extension namespace
                                if (extNs.NamespaceName is "http://www.w3.org/1999/XSL/Transform"
                                    or "http://www.w3.org/2001/XMLSchema"
                                    or "http://www.w3.org/2001/XMLSchema-instance"
                                    or "http://www.w3.org/XML/1998/namespace")
                                    throw new XsltException($"XTSE0800: The namespace '{extNs.NamespaceName}' is a reserved namespace and must not be used as an extension element namespace",
                                        GetSourceLocation(element));
                            }
                            excludeResultPrefixes.Add(p);
                        }
                        break;
                    case "version":
                        // xsl:version sets the effective XSLT version for this subtree
                        version = attr.Value.Trim();
                        break;
                    // Other valid XSLT attributes on LREs per spec section 11.1
                    case "default-collation":
                        defaultCollation = ResolveDefaultCollation(attr.Value.Trim());
                        break;
                    case "default-mode":
                    case "expand-text":
                    case "use-when":
                    case "xpath-default-namespace":
                        break;
                    case "type":
                        // XTSE1660: Non-schema-aware processor must reject xsl:type
                        if (ShouldRejectSchemaAware)
                            throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the xsl:type attribute",
                                GetSourceLocation(element));
                        break;
                    case "validation":
                        // XTSE1660: Non-schema-aware processor can only accept strip/preserve/lax
                        if (attr.Value.Trim() is "strict" && ShouldRejectSchemaAware)
                            throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept xsl:validation=\"{attr.Value.Trim()}\"",
                                GetSourceLocation(element));
                        break;
                    case "default-validation":
                        // XTSE1660: Non-schema-aware processor can only accept strip/preserve/lax
                        if (attr.Value.Trim() is "strict" && ShouldRejectSchemaAware)
                            throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept default-validation=\"{attr.Value.Trim()}\"",
                                GetSourceLocation(element));
                        break;
                    default:
                        // Forwards compatibility: ignore unknown XSLT attributes when
                        // effective version > 3.0 (per XSLT spec section 3.8)
                        var lreEffectiveVersion = version ?? GetEffectiveVersion(element);
                        if (ParseVersionNumber(lreEffectiveVersion) > 3.0m)
                            break;
                        throw new XsltException(
                            $"XTSE0805: Attribute '{attr.Name.LocalName}' in the XSLT namespace is not permitted on a literal result element",
                            GetSourceLocation(element));
                }
            }
            else
            {
                // Resolve the attribute's namespace URI to a NamespaceId (thread-safe intern)
                var attrNsName = attr.Name.NamespaceName;
                var attrNsId = ResolveNamespaceUri(attrNsName);
                // Use prefix map from XmlReader when available (preserves original prefix
                // even when multiple prefixes share the same namespace URI)
                string? attrPrefix = null;
                if (_elementPrefixMap != null && attr is IXmlLineInfo attrLi && attrLi.HasLineInfo())
                    _elementPrefixMap.TryGetValue((attrLi.LineNumber, attrLi.LinePosition), out attrPrefix);
                attrPrefix ??= element.GetPrefixOfNamespace(attr.Name.Namespace);
                var attrName = new QName(
                    attrNsId,
                    attr.Name.LocalName,
                    attrPrefix
                );
                attributes[attrName] = ParseAvt(attr.Value, element, attr);
            }
        }

        // Inherit namespace declarations and exclude-result-prefixes from ancestor elements
        for (var ancestor = element.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            foreach (var nsAttr in ancestor.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                var prefix = nsAttr.Name.LocalName == "xmlns" ? "" : nsAttr.Name.LocalName;
                namespaceDeclarations.TryAdd(prefix, nsAttr.Value);
            }

            // Inherit exclude-result-prefixes from ancestor XSLT elements
            // §7.1.2: #all excludes only namespaces in scope on the declaring element,
            // so expand to actual prefixes rather than passing through literally.
            var erpAttr = ancestor.Attribute("exclude-result-prefixes")
                          ?? ancestor.Attribute(XsltNs + "exclude-result-prefixes");
            if (erpAttr != null)
            {
                foreach (var p in erpAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (p == "#all")
                    {
                        foreach (var ns in ancestor.Attributes().Where(a => a.IsNamespaceDeclaration))
                        {
                            var np = ns.Name.LocalName == "xmlns" ? "" : ns.Name.LocalName;
                            var nu = ns.Value;
                            if (nu == "http://www.w3.org/1999/XSL/Transform"
                                || nu == "http://www.w3.org/XML/1998/namespace")
                                continue;
                            excludeResultPrefixes.Add(string.IsNullOrEmpty(np) ? "#default" : np);
                        }
                    }
                    else
                    {
                        excludeResultPrefixes.Add(p);
                    }
                }
            }
        }

        // Resolve xml:base on LRE for static-base-uri() of descendant expressions
        var xmlBase = element.Attribute(XNamespace.Xml + "base");
        string? staticBaseUri = null;
        if (xmlBase != null)
            staticBaseUri = ResolveEffectiveBaseUri(element)?.ToString();

        return new XsltLiteralResultElement
        {
            Location = location,
            Name = name,
            SourceNamespaceName = element.Name.NamespaceName,
            Attributes = attributes,
            NamespaceDeclarations = namespaceDeclarations,
            UseAttributeSets = useAttributeSets,
            InheritNamespaces = inheritNamespaces,
            ExcludeResultPrefixes = excludeResultPrefixes,
            Version = version,
            DefaultCollation = defaultCollation,
            StaticBaseUri = staticBaseUri,
            Content = ParseSequenceConstructor(element)
        };
    }


    private XsltAttributeValueTemplate ParseAvt(string value, XElement context, System.Xml.Linq.XAttribute? sourceAttribute = null)
    {
        // D2 source-location-audit: when the AVT comes from a real attribute, compute
        // the file-absolute position of every inner {…} expression's first character
        // and pass it through ParseExprAt so runtime errors pin to the offending
        // token, not to the start of the attribute. Multi-line AVTs are handled by
        // counting newlines in the prefix.
        var (avtBaseLine, avtBaseCol, moduleUri) = ComputeAvtBasePosition(sourceAttribute);
        return ParseAvtCore(value, context, avtBaseLine, avtBaseCol, moduleUri);
    }


    /// <summary>
    /// D3: parse an AVT-style template that comes from element text content rather
    /// than an attribute value (e.g. expand-text=yes TVT). Uses the first descendant
    /// <see cref="XText"/>'s IXmlLineInfo to seed the base position so inner-expression
    /// errors carry the right file (line, col).
    /// </summary>
    private XsltAttributeValueTemplate ParseAvtFromText(string value, XElement context)
    {
        var (line, col, module) = ComputeTextBasePosition(context);
        return ParseAvtCore(value, context, line, col, module);
    }


    private XsltAttributeValueTemplate ParseAvtCore(string value, XElement context, int avtBaseLine, int avtBaseCol, string? moduleUri)
    {
        var parts = new List<AvtPart>();
        var current = 0;

        while (current < value.Length)
        {
            var openBrace = value.IndexOf('{', current);

            if (openBrace < 0)
            {
                // No more expressions, rest is literal
                if (current < value.Length)
                {
                    CheckUnescapedRightBrace(value[current..], context);
                    parts.Add(new AvtLiteral { Value = UnescapeAvt(value[current..]) });
                }
                break;
            }

            // Check for escaped brace
            if (openBrace + 1 < value.Length && value[openBrace + 1] == '{')
            {
                CheckUnescapedRightBrace(value[current..(openBrace + 1)], context);
                parts.Add(new AvtLiteral { Value = UnescapeAvt(value[current..(openBrace + 1)]) });
                current = openBrace + 2;
                continue;
            }

            // Add literal before the brace
            if (openBrace > current)
            {
                CheckUnescapedRightBrace(value[current..openBrace], context);
                parts.Add(new AvtLiteral { Value = UnescapeAvt(value[current..openBrace]) });
            }

            // Find closing brace
            var closeBrace = FindMatchingBrace(value, openBrace);
            if (closeBrace < 0)
            {
                throw new XsltException("Unmatched '{' in attribute value template", GetSourceLocation(context));
            }

            var expr = value[(openBrace + 1)..closeBrace];
            // Empty expressions {} or comment-only expressions {(: ... :)}
            // produce empty text per XSLT 3.0 spec
            var exprTrimmed = StripXPathComments(expr).Trim();
            if (exprTrimmed.Length == 0)
            {
                // Empty expression — produces empty string (vacuous text node)
                // Don't add anything; this effectively disappears
            }
            else
            {
                XQueryExpression innerExpr;
                if (avtBaseLine > 0)
                {
                    // Compute absolute (line, col) of the character just past the '{'.
                    var (lineOffset, colOnFinalLine) = OffsetToLineColumn(value, openBrace + 1);
                    var innerLine = avtBaseLine + lineOffset;
                    var innerCol = lineOffset == 0 ? avtBaseCol + colOnFinalLine : colOnFinalLine + 1;
                    innerExpr = ParseExprAt(expr, innerLine, innerCol, moduleUri);
                }
                else
                {
                    innerExpr = ParseExpr(expr);
                }
                parts.Add(new AvtExpression { Expression = innerExpr });
            }

            current = closeBrace + 1;
        }

        if (parts.Count == 0)
        {
            parts.Add(new AvtLiteral { Value = "" });
        }

        return new XsltAttributeValueTemplate { Parts = parts };
    }


    private XsltPattern ParsePattern(string pattern, XElement context)
    {
        // Strip XPath comments (:...:) before parsing. Comments can nest.
        pattern = StripXPathComments(pattern);

        // XTSE1060: current-group() is not allowed in patterns
        if (System.Text.RegularExpressions.Regex.IsMatch(pattern, @"\bcurrent-group\s*\("))
            throw new XsltException("XTSE1060: current-group() is not allowed in a pattern", GetSourceLocation(context));
        // XTSE1070: current-grouping-key() is not allowed in patterns
        if (System.Text.RegularExpressions.Regex.IsMatch(pattern, @"\bcurrent-grouping-key\s*\("))
            throw new XsltException("XTSE1070: current-grouping-key() is not allowed in a pattern", GetSourceLocation(context));
        // XTSE3470: current-merge-group() is not allowed in patterns
        if (System.Text.RegularExpressions.Regex.IsMatch(pattern, @"\bcurrent-merge-group\s*\("))
            throw new XsltException("XTSE3470: current-merge-group() is not allowed in a pattern", GetSourceLocation(context));
        // XTSE3500: current-merge-key() is not allowed in patterns
        if (System.Text.RegularExpressions.Regex.IsMatch(pattern, @"\bcurrent-merge-key\s*\("))
            throw new XsltException("XTSE3500: current-merge-key() is not allowed in a pattern", GetSourceLocation(context));

        // Split union patterns at '|' outside of brackets
        var unionParts = SplitUnionPattern(pattern);
        if (unionParts.Count > 1)
        {
            var parsedParts = unionParts.Select(p => ParsePattern(p.Trim(), context)).ToList();
            // XTSE0340: Union operator '|' is not allowed with predicate patterns (.[pred])
            // because '|' only applies to node patterns, not atomic value patterns
            if (parsedParts.Any(p => p is DotPattern))
                throw new XsltException("XTSE0340: Union operator '|' is not allowed with predicate patterns", GetSourceLocation(context));
            return new UnionPattern
            {
                Patterns = parsedParts
            };
        }

        // XSLT 3.0: Split on 'except'/'intersect' keywords at the top level
        // Per grammar: IntersectExceptPattern ::= PathPattern (('intersect' | 'except') PathPattern)*
        {
            var exceptIntersectParts = SplitExceptIntersect(pattern);
            if (exceptIntersectParts != null)
            {
                // Build left-associative chain: A except B except C → (A except B) except C
                var result = ParsePattern(exceptIntersectParts[0].Part.Trim(), context);
                for (var idx = 1; idx < exceptIntersectParts.Count; idx++)
                {
                    var right = ParsePattern(exceptIntersectParts[idx].Part.Trim(), context);
                    result = exceptIntersectParts[idx].IsExcept
                        ? new ExceptPattern { Left = result, Right = right }
                        : new IntersectPattern { Left = result, Right = right };
                }
                return result;
            }
        }

        // Handle "/" (document root) pattern, optionally with predicates in parenthesized form: "/", "(/)[pred]"
        // Note: "/[pred]" without parens is a syntax error per bug 18861 (XTSE0340)
        {
            var trimmedRoot = pattern.Trim();
            var wasParenthesized = false;
            // Unwrap parenthesized form: (/)[pred] → /[pred]
            if (trimmedRoot.StartsWith("(/)", StringComparison.Ordinal))
            {
                trimmedRoot = "/" + trimmedRoot[3..].TrimStart();
                wasParenthesized = true;
            }
            if (trimmedRoot == "/" || (wasParenthesized && trimmedRoot.Length > 1 && trimmedRoot[0] == '/'
                && trimmedRoot.AsSpan(1).TrimStart().StartsWith("[")))
            {
                var predicates = new List<XQueryExpression>();
                var predPart = trimmedRoot[1..].Trim();
                while (predPart.StartsWith('['))
                {
                    var bracketDepth = 0;
                    var k = 0;
                    for (; k < predPart.Length; k++)
                    {
                        if (predPart[k] == '[') bracketDepth++;
                        else if (predPart[k] == ']') { bracketDepth--; if (bracketDepth == 0) break; }
                        else if (predPart[k] == '\'' || predPart[k] == '"')
                        {
                            var q = predPart[k];
                            k++;
                            while (k < predPart.Length && predPart[k] != q) k++;
                        }
                    }
                    if (k < predPart.Length)
                    {
                        var predExpr = predPart[1..k];
                        try { predicates.Add(ParseExpression(predExpr, context)); }
#pragma warning disable CA1031 // Intentional broad catch — see comment below
                        catch (Exception)
                        {
                            // Predicate parsing may fail during pattern optimization (e.g. forward
                            // references, complex expressions). The predicate is still evaluated at
                            // runtime via the full XPath evaluator; skipping it here only means this
                            // optimization path won't pre-filter using it.
                        }
#pragma warning restore CA1031
                        predPart = predPart[(k + 1)..].Trim();
                    }
                    else break;
                }
                return new PathPattern
                {
                    Steps = [new PatternStep
                    {
                        Axis = Axis.Self,
                        NodeTest = new KindTest { Kind = XdmNodeKind.Document },
                        Predicates = predicates
                    }]
                };
            }

            // "/[pred]" without parentheses is a syntax error per bug 18861
            if (!wasParenthesized && trimmedRoot.Length > 1 && trimmedRoot[0] == '/'
                && trimmedRoot.AsSpan(1).TrimStart().StartsWith("["))
                throw new XsltException("XTSE0340: /[predicate] is not a valid pattern; use (/)[predicate] instead", GetSourceLocation(context));
        }

        // Handle "." and ".[pred1][pred2]..." patterns (XSLT 3.0: matches any item)
        // Allow whitespace between "." and "[" per XPath grammar
        {
            var trimmedPat = pattern.Trim();
            if (trimmedPat == "." || (trimmedPat.Length > 1 && trimmedPat[0] == '.'
                && trimmedPat.AsSpan(1).TrimStart().StartsWith("[")))
            {
                var predicates = new List<XQueryExpression>();
                // Extract all predicate expressions [pred1][pred2]...
                var predPart = trimmedPat[1..].Trim();
                while (predPart.StartsWith('['))
                {
                    // Find matching ']', respecting nesting
                    var bracketDepth = 0;
                    var k = 0;
                    for (; k < predPart.Length; k++)
                    {
                        if (predPart[k] == '[') bracketDepth++;
                        else if (predPart[k] == ']') { bracketDepth--; if (bracketDepth == 0) break; }
                        else if (predPart[k] == '\'' || predPart[k] == '"')
                        {
                            var q = predPart[k];
                            k++;
                            while (k < predPart.Length && predPart[k] != q) k++;
                        }
                    }
                    if (k < predPart.Length)
                    {
                        var predExpr = predPart[1..k];
                        try
                        {
                            predicates.Add(ParseExpression(predExpr, context));
                        }
#pragma warning disable CA1031 // Intentional broad catch — see comment below
                        catch (Exception)
                        {
                            // Predicate parsing may fail during pattern optimization (e.g. forward
                            // references, complex expressions). The predicate is still evaluated at
                            // runtime via the full XPath evaluator; skipping it here only means this
                            // optimization path won't pre-filter using it.
                        }
#pragma warning restore CA1031
                        predPart = predPart[(k + 1)..].Trim();
                    }
                    else break;
                }
                return new DotPattern { Predicates = predicates };
            }
        }

        // XTSE0340: Parenthesized predicate patterns like "(.[pred])" are not valid
        {
            var trimmedPat = pattern.Trim();
            if (trimmedPat.StartsWith('(') && trimmedPat.EndsWith(')'))
            {
                var inner = trimmedPat[1..^1].Trim();
                if (inner.Length > 0 && inner[0] == '.' && (inner.Length == 1 || inner.AsSpan(1).TrimStart().StartsWith("[")))
                    throw new XsltException("XTSE0340: Predicate pattern cannot be parenthesized", GetSourceLocation(context));
            }
        }

        // Parenthesized pattern with an OUTER predicate: "(P)[pred1][pred2]...".
        // The predicate filters the whole sequence matching P (document order), which is
        // different from folding it onto P's last step. See W3C match-076.
        {
            var parenPositional = TryParseParenthesizedPositionalPattern(pattern.Trim(), context);
            if (parenPositional != null)
                return parenPositional;
        }

        // Expand parenthesized unions within path steps:
        // "x/(child::a|descendant::b)" → "x/child::a | x/descendant::b"
        {
            var expanded = ExpandParenthesizedUnions(pattern.Trim());
            if (expanded != null)
            {
                return new UnionPattern
                {
                    Patterns = expanded.Select(p => ParsePattern(p.Trim(), context)).ToList()
                };
            }
        }

        // Expand parenthesized except/intersect within path steps:
        // "x/(descendant::a except child::a)" → ExceptPattern(x/descendant::a, x/child::a)
        // Context-scoped matching ensures both sides are evaluated from the same ancestor.
        {
            var parenExcept = TryExpandParenthesizedExceptIntersect(pattern.Trim(), context);
            if (parenExcept != null)
                return parenExcept;
        }

        // key() function call patterns: key('name', value), key('name', value)//child, etc.
        {
            var keyPattern = TryParseKeyPattern(pattern.Trim(), context);
            if (keyPattern != null)
                return keyPattern;
        }

        // id() function call patterns: id(value), id(value)//child, etc.
        {
            var idPattern = TryParseIdPattern(pattern.Trim(), context);
            if (idPattern != null)
                return idPattern;
        }

        // XSLT 3.0: Variable reference patterns: $var, $var/path, $var//path
        {
            var varPattern = TryParseVariableReferencePattern(pattern.Trim(), context);
            if (varPattern != null)
                return varPattern;
        }

        // XSLT 3.0: doc() function patterns: doc('uri'), doc('uri')/path, doc('uri')//path
        {
            var docPattern = TryParseDocFunctionPattern(pattern.Trim(), context);
            if (docPattern != null)
                return docPattern;
        }

        // Simple path pattern — handle '//' by splitting carefully
        var steps = new List<PatternStep>();
        var trimmed = pattern.Trim();
        var startsWithRoot = false;

        // Split into segments, tracking whether '//' was used between them
        var segments = new List<(string name, bool descendantSeparator)>();
        var i = 0;

        // Detect leading '/' or '//'
        if (i < trimmed.Length && trimmed[i] == '/')
        {
            i++;
            if (i < trimmed.Length && trimmed[i] == '/')
            {
                // Leading '//' — descendant of root (match anywhere)
                i++;
                var seg = ReadSegment(trimmed, ref i);
                if (!string.IsNullOrWhiteSpace(seg))
                    segments.Add((seg, true));
            }
            else
            {
                // Leading '/' — direct child of document root
                startsWithRoot = true;
                var seg = ReadSegment(trimmed, ref i);
                if (!string.IsNullOrWhiteSpace(seg))
                    segments.Add((seg, false));
            }
        }
        else if (i < trimmed.Length)
        {
            var seg = ReadSegment(trimmed, ref i);
            if (!string.IsNullOrWhiteSpace(seg))
                segments.Add((seg, false));
        }

        // Parse remaining segments
        while (i < trimmed.Length)
        {
            if (trimmed[i] == '/')
            {
                i++;
                if (i < trimmed.Length && trimmed[i] == '/')
                {
                    // '//' separator
                    i++;
                    var seg = ReadSegment(trimmed, ref i);
                    if (!string.IsNullOrWhiteSpace(seg))
                        segments.Add((seg, true));
                }
                else
                {
                    // '/' separator
                    var seg = ReadSegment(trimmed, ref i);
                    if (!string.IsNullOrWhiteSpace(seg))
                        segments.Add((seg, false));
                }
            }
            else
            {
                break; // shouldn't happen
            }
        }

        // If pattern starts with '/', prepend a document-root step
        if (startsWithRoot)
        {
            steps.Add(new PatternStep
            {
                Axis = Axis.Self,
                NodeTest = new KindTest { Kind = XdmNodeKind.Document }
            });
        }

        foreach (var (part, descendantSep) in segments)
        {
            var axis = Axis.Child;
            // Trim whitespace that can surround a step when comments/whitespace appear around
            // the '/' separator, e.g. "*/(: c :) a" → "*/ a" → step " a". See W3C match-215.
            var name = part.Trim();

            // Extract predicates (e.g., "item[@type='a']" -> name="item", predicates=["@type='a'"])
            var predicates = new List<XQueryExpression>();
            var predStart = name.IndexOf('[', StringComparison.Ordinal);
            if (predStart >= 0)
            {
                var predPart = name[predStart..];
                // Whitespace is permitted between a NodeTest and its predicate
                // (e.g. "letters [true()]"); trim it so the node test stays valid.
                name = name[..predStart].Trim();

                // Parse each predicate expression between matching brackets
                var pPos = 0;
                while (pPos < predPart.Length)
                {
                    if (predPart[pPos] == '[')
                    {
                        pPos++;
                        var depth = 1;
                        var exprStart = pPos;
                        while (pPos < predPart.Length && depth > 0)
                        {
                            if (predPart[pPos] == '[') depth++;
                            else if (predPart[pPos] == ']') depth--;
                            if (depth > 0) pPos++;
                        }
                        if (pPos > exprStart)
                        {
                            var exprText = predPart[exprStart..pPos].Trim();
                            if (!string.IsNullOrEmpty(exprText))
                            {
                                try
                                {
                                    predicates.Add(ParseExpression(exprText, context));
                                }
#pragma warning disable CA1031 // Predicate parsing failure should not crash pattern parsing
                                catch (Exception ex) when (ex is not XsltException && ex is not PhoenixmlDb.XQuery.Execution.XQueryRuntimeException)
                                {
                                    // If predicate parsing fails for non-static reasons, skip it
                                }
#pragma warning restore CA1031
                            }
                        }
                        if (pPos < predPart.Length) pPos++; // skip ']'
                    }
                    else
                    {
                        pPos++;
                    }
                }
            }

            var explicitAxis = false;
            if (name.StartsWith('@'))
            {
                axis = Axis.Attribute;
                explicitAxis = true;
                name = name[1..];
            }
            else if (name.StartsWith("attribute::", StringComparison.Ordinal))
            {
                axis = Axis.Attribute;
                explicitAxis = true;
                name = name["attribute::".Length..];
            }
            else if (name.StartsWith("child::", StringComparison.Ordinal))
            {
                axis = Axis.Child;
                explicitAxis = true;
                name = name["child::".Length..];
            }
            else if (name.StartsWith("self::", StringComparison.Ordinal))
            {
                axis = Axis.Self;
                explicitAxis = true;
                name = name["self::".Length..];
            }
            else if (name.StartsWith("descendant-or-self::", StringComparison.Ordinal))
            {
                axis = Axis.DescendantOrSelf;
                explicitAxis = true;
                name = name["descendant-or-self::".Length..];
            }
            else if (name.StartsWith("descendant::", StringComparison.Ordinal))
            {
                axis = Axis.Descendant;
                explicitAxis = true;
                name = name["descendant::".Length..];
            }
            else if (name.StartsWith("parent::", StringComparison.Ordinal))
            {
                axis = Axis.Parent;
                explicitAxis = true;
                name = name["parent::".Length..];
            }
            else if (name.StartsWith("namespace::", StringComparison.Ordinal))
            {
                axis = Axis.Namespace;
                explicitAxis = true;
                name = name["namespace::".Length..];
            }
            else if (name.StartsWith("..", StringComparison.Ordinal))
            {
                axis = Axis.Parent;
                explicitAxis = true;
                name = "*";
            }
            else if (name == ".")
            {
                axis = Axis.Self;
                explicitAxis = true;
                name = "*";
            }

            // XTSE0340/XPST0017: Reject function calls that aren't valid pattern functions
            // Valid pattern functions (key, id, doc, document, root, element-with-id) are handled earlier.
            // Any remaining name with '(' that isn't a recognized kind test or valid function is invalid.
            if (name.Contains('(', StringComparison.Ordinal) && name.EndsWith(')')
                && !name.StartsWith("node(", StringComparison.Ordinal)
                && !name.StartsWith("text(", StringComparison.Ordinal)
                && !name.StartsWith("comment(", StringComparison.Ordinal)
                && !name.StartsWith("processing-instruction(", StringComparison.Ordinal)
                && !name.StartsWith("document-node(", StringComparison.Ordinal)
                && !name.StartsWith("namespace-node(", StringComparison.Ordinal)
                && !name.StartsWith("element(", StringComparison.Ordinal)
                && !name.StartsWith("schema-element(", StringComparison.Ordinal)
                && !name.StartsWith("attribute(", StringComparison.Ordinal)
                && !name.StartsWith("schema-attribute(", StringComparison.Ordinal)
                && !name.StartsWith("root(", StringComparison.Ordinal))
            {
                throw new XsltException($"XTSE0340: '{name}' is not allowed in a pattern", GetSourceLocation(context));
            }

            // root() function used as a pattern step: matches any root node (a node with
            // no parent — document nodes and parentless elements). See W3C match-233.
            if (name == "root()")
            {
                steps.Add(new PatternStep
                {
                    Axis = Axis.Self,
                    NodeTest = new KindTest { Kind = XdmNodeKind.None },
                    DescendantSeparator = descendantSep,
                    Predicates = predicates,
                    IsRootFunction = true
                });
                continue;
            }

            var nodeTest = ParseNodeTest(name, context, isAttribute: axis == Axis.Attribute);

            // KindTest patterns like attribute() and namespace-node() imply their axis
            // only when no explicit axis was provided (e.g., bare "attribute()" → Axis.Attribute,
            // but "child::attribute()" keeps Axis.Child per XSLT spec §5.5.3)
            if (!explicitAxis)
            {
                if (nodeTest is KindTest { Kind: XdmNodeKind.Attribute })
                    axis = Axis.Attribute;
                else if (nodeTest is KindTest { Kind: XdmNodeKind.Namespace })
                    axis = Axis.Namespace;
                else if (nodeTest is KindTest { Kind: XdmNodeKind.Document })
                    // A lone document-node() pattern matches the document node itself
                    // (self axis), like "/". An explicit "child::document-node()" keeps
                    // the child axis and therefore never matches (a document node is
                    // never a child of anything) — see W3C match-048.
                    axis = Axis.Self;
            }

            steps.Add(new PatternStep
            {
                Axis = axis,
                NodeTest = nodeTest,
                DescendantSeparator = descendantSep,
                Predicates = predicates
            });
        }

        return new PathPattern { Steps = steps };
    }


    /// <summary>
    /// Parses an XPath/XQuery expression and resolves namespace prefixes in QNames
    /// using the XSLT element's namespace context.
    /// </summary>
    /// <summary>
    /// Parses an XPath expression, turning a bare parse failure into one that says which
    /// expression failed and where it lives in the stylesheet.
    /// </summary>
    /// <remarks>
    /// The raw parser message is written for someone reading an XPath in isolation: ANTLR
    /// reports "mismatched input '&lt;EOF&gt;' expecting {...}" followed by every token the
    /// grammar would have accepted. In a stylesheet that expands to hundreds of lines of
    /// generated XSLT, that names neither the attribute nor the module, and the expected-token
    /// list is long enough to bury the part that matters. Appending the expression text and its
    /// origin is what makes such a failure locatable without bisecting the stylesheet.
    /// The structured <see cref="PhoenixmlDb.XQuery.Parser.ParseError"/> list is preserved so
    /// callers reading line/column (the language server) are unaffected.
    /// </remarks>
    private XQueryExpression ParseXPathWithContext(string expression, System.Xml.Linq.XObject? origin)
    {
        try
        {
            return _expressionParser.Parse(expression);
        }
        catch (PhoenixmlDb.XQuery.Parser.XQueryParseException ex)
        {
            var suffix = DescribeParseOrigin(expression, origin);
            if (ex.Errors.Count == 0)
                throw new PhoenixmlDb.XQuery.Parser.XQueryParseException(ex.Message + suffix);
            var enriched = new List<PhoenixmlDb.XQuery.Parser.ParseError>(ex.Errors.Count)
            {
                new(ex.Errors[0].Message + suffix, ex.Errors[0].Line, ex.Errors[0].Column),
            };
            for (var i = 1; i < ex.Errors.Count; i++)
                enriched.Add(ex.Errors[i]);
            throw new PhoenixmlDb.XQuery.Parser.XQueryParseException(enriched);
        }
    }


    private XQueryExpression ParseExpression(string expression, XElement context)
    {
        var expr = ParseXPathWithContext(expression, context);
        ResolveExpressionNamespaces(expr, context);
        AttachXsltSourceLocation(expr, context);
        return expr;
    }


    /// <summary>
    /// Parses an XPath expression using the current namespace context (_nsContext).
    /// This is the primary method for parsing expressions within XSLT instructions.
    /// </summary>
    /// <remarks>
    /// When <paramref name="sourceAttribute"/> is supplied, the parser shifts every
    /// sub-expression's <see cref="SourceLocation"/> from being xpath-string-relative
    /// (line/col within the inline XPath text) to being XSLT-file-absolute. This is
    /// the Phase D1 source-location-audit fix: prior to this, an error inside
    /// <c>select="foo[bad-syntax"</c> would report the column WITHIN the XPath
    /// (e.g. 14), not the column in the actual stylesheet (e.g. 35) — useless for
    /// LSP diagnostics that need to squiggle the offending token.
    /// </remarks>
    private XQueryExpression ParseExpr(string expression, System.Xml.Linq.XAttribute? sourceAttribute = null)
    {
        var expr = ParseXPathWithContext(expression, (System.Xml.Linq.XObject?)sourceAttribute ?? _nsContext);
        if (_nsContext != null)
        {
            ResolveExpressionNamespaces(expr, _nsContext);
            // When sourceAttribute is supplied, the more-precise shift below sets both
            // Module and absolute file coordinates on every sub-expression; the legacy
            // AttachXsltSourceLocation only stamps Module on the top-level expression
            // and leaves child line/col xpath-relative.
            if (sourceAttribute == null)
                AttachXsltSourceLocation(expr, _nsContext);
        }
        if (sourceAttribute != null)
            ShiftExpressionLocationsToFileAbsolute(expr, sourceAttribute);
        return expr;
    }


    /// <summary>
    /// Like <see cref="ParseExpr(string, System.Xml.Linq.XAttribute?)"/> but with an
    /// explicit base position — used by AVT (D2) and inline-expression (D3) parsers
    /// where the inner XPath starts at an offset INSIDE the attribute value (not at
    /// the start). Caller supplies the absolute file position of the inner XPath's
    /// first character plus the module URI.
    /// </summary>
    private XQueryExpression ParseExprAt(string expression, int absoluteLine, int absoluteColumn, string? moduleUri)
    {
        var expr = ParseXPathWithContext(expression, _nsContext);
        if (_nsContext != null)
            ResolveExpressionNamespaces(expr, _nsContext);
        ShiftExpressionLocationsAt(expr, absoluteLine, absoluteColumn, moduleUri);
        return expr;
    }


    /// <summary>
    /// Walks an XQuery expression tree and resolves namespace prefixes on
    /// VariableReference and FunctionCallExpression QNames.
    /// </summary>
    private static void ResolveExpressionNamespaces(XQueryExpression expr, XElement context)
    {
        switch (expr)
        {
            case VariableReference vr:
                if (!string.IsNullOrEmpty(vr.Name.Prefix) && vr.Name.Namespace == NamespaceId.None)
                    vr.Name = ParseQName($"{vr.Name.Prefix}:{vr.Name.LocalName}", context);
                // EQName variable references: resolve ExpandedNamespace to NamespaceId so
                // Dictionary lookup matches the declared variable's QName (record struct equality)
                else if (vr.Name.ExpandedNamespace != null && vr.Name.Namespace == NamespaceId.None)
                    vr.Name = ParseQName($"Q{{{vr.Name.ExpandedNamespace}}}{vr.Name.LocalName}", context);
                break;
            case FunctionCallExpression fc:
                if (!string.IsNullOrEmpty(fc.Name.Prefix) && fc.Name.Namespace == NamespaceId.None)
                    fc.Name = ParseQName($"{fc.Name.Prefix}:{fc.Name.LocalName}", context);
                foreach (var arg in fc.Arguments)
                    ResolveExpressionNamespaces(arg, context);
                break;
            case BinaryExpression be:
                ResolveExpressionNamespaces(be.Left, context);
                ResolveExpressionNamespaces(be.Right, context);
                break;
            case UnaryExpression ue:
                ResolveExpressionNamespaces(ue.Operand, context);
                break;
            case PathExpression pe:
                if (pe.InitialExpression != null)
                    ResolveExpressionNamespaces(pe.InitialExpression, context);
                foreach (var step in pe.Steps)
                    ResolveExpressionNamespaces(step, context);
                break;
            case StepExpression se:
                if (se.NodeTest is NameTest nt)
                {
                    if (!string.IsNullOrEmpty(nt.Prefix) && nt.Prefix != "*" && nt.NamespaceUri == null)
                    {
                        // Resolve explicit prefix to URI
                        var ns = context.GetNamespaceOfPrefix(nt.Prefix)?.NamespaceName;
                        if (ns != null)
                            nt.NamespaceUri = ns;
                        else if (!IsBackwardsCompatible(context))
                            throw new XsltException($"XPST0081: Namespace prefix '{nt.Prefix}' has not been declared");
                        // In backwards-compatible mode (XSLT 1.0), leave prefix unresolved —
                        // error deferred to runtime per XSLT §3.12
                    }
                    else if (nt.Prefix == null && nt.NamespaceUri == null && !nt.IsLocalNameWildcard
                             && se.Axis != Axis.Attribute && se.Axis != Axis.Namespace)
                    {
                        // Apply xpath-default-namespace for unprefixed element name tests
                        var xdn = GetXpathDefaultNamespace(context);
                        if (xdn != null)
                            nt.NamespaceUri = xdn;
                    }
                }
                else if (se.NodeTest is KindTest { Name: NameTest ktName } && !string.IsNullOrEmpty(ktName.Prefix)
                         && ktName.Prefix != "*" && ktName.NamespaceUri == null)
                {
                    // element(x:foo) / attribute(x:foo) name test — the XQuery parser leaves
                    // the prefix unresolved for XSLT callers (it can't see the stylesheet's
                    // namespaces). Resolve it here against the XSLT element's in-scope
                    // namespaces, mirroring the NameTest case. Martin Honnen 2026-07-30:
                    // self::attribute(x:expand-text). Element/attribute kind tests default to
                    // NO namespace (not the xpath-default-namespace), so an unprefixed name is
                    // left as-is.
                    var ktNs = context.GetNamespaceOfPrefix(ktName.Prefix)?.NamespaceName;
                    if (ktNs != null)
                        ktName.NamespaceUri = ktNs;
                    else if (!IsBackwardsCompatible(context))
                        throw new XsltException($"XPST0081: Namespace prefix '{ktName.Prefix}' has not been declared");
                }
                foreach (var pred in se.Predicates)
                    ResolveExpressionNamespaces(pred, context);
                break;
            case FilterExpression fe:
                ResolveExpressionNamespaces(fe.Primary, context);
                foreach (var pred in fe.Predicates)
                    ResolveExpressionNamespaces(pred, context);
                break;
            case IfExpression ie:
                ResolveExpressionNamespaces(ie.Condition, context);
                ResolveExpressionNamespaces(ie.Then, context);
                if (ie.Else != null)
                    ResolveExpressionNamespaces(ie.Else, context);
                break;
            case FlworExpression flwor:
                foreach (var clause in flwor.Clauses)
                {
                    if (clause is ForClause forClause)
                        foreach (var binding in forClause.Bindings)
                            ResolveExpressionNamespaces(binding.Expression, context);
                    else if (clause is LetClause letClause)
                        foreach (var binding in letClause.Bindings)
                            ResolveExpressionNamespaces(binding.Expression, context);
                    else if (clause is WhereClause whereClause)
                        ResolveExpressionNamespaces(whereClause.Condition, context);
                    else if (clause is OrderByClause orderBy)
                        foreach (var spec in orderBy.OrderSpecs)
                            ResolveExpressionNamespaces(spec.Expression, context);
                }
                ResolveExpressionNamespaces(flwor.ReturnExpression, context);
                break;
            case SequenceExpression seq:
                foreach (var item in seq.Items)
                    ResolveExpressionNamespaces(item, context);
                break;
            case InstanceOfExpression inst:
                ResolveExpressionNamespaces(inst.Expression, context);
                ValidateUnprefixedTypeName(inst.TargetType, context);
                break;
            case CastExpression cast:
                ResolveExpressionNamespaces(cast.Expression, context);
                ValidateUnprefixedTypeName(cast.TargetType, context);
                break;
            case CastableExpression castable:
                ResolveExpressionNamespaces(castable.Expression, context);
                ValidateUnprefixedTypeName(castable.TargetType, context);
                break;
            case TreatExpression treat:
                ResolveExpressionNamespaces(treat.Expression, context);
                ValidateUnprefixedTypeName(treat.TargetType, context);
                break;
            case SimpleMapExpression sme:
                ResolveExpressionNamespaces(sme.Left, context);
                ResolveExpressionNamespaces(sme.Right, context);
                break;
            case StringConcatExpression sce:
                foreach (var operand in sce.Operands)
                    ResolveExpressionNamespaces(operand, context);
                break;
            case RangeExpression re:
                ResolveExpressionNamespaces(re.Start, context);
                ResolveExpressionNamespaces(re.End, context);
                break;
            case ArrowExpression ae:
                ResolveExpressionNamespaces(ae.Expression, context);
                ResolveExpressionNamespaces(ae.FunctionCall, context);
                break;
            case InlineFunctionExpression ife:
                if (ife.Body != null)
                    ResolveExpressionNamespaces(ife.Body, context);
                break;
            case DynamicFunctionCallExpression dfc:
                ResolveExpressionNamespaces(dfc.FunctionExpression, context);
                foreach (var arg in dfc.Arguments)
                    ResolveExpressionNamespaces(arg, context);
                break;
        }
    }


    private static XdmSequenceType ParseSequenceType(string type, XElement? context = null)
    {
        // Simplified - would use full type parser
        var occurrence = Occurrence.ExactlyOne;

        // Strip occurrence indicator (* + ?) from end, but only if it's at the top level
        // (not inside nested parentheses). e.g., "map(*)" — the * is inside parens.
        // "map(xs:string, element()?)" — the ? is inside parens (part of value type).
        // "element()?" — the ? IS a top-level occurrence indicator.
        {
            var lastChar = type.Length > 0 ? type[^1] : '\0';
            if (lastChar is '*' or '+' or '?')
            {
                // Check if the indicator is at top-level (paren depth 0)
                int depth = 0;
                for (int oi = 0; oi < type.Length - 1; oi++)
                {
                    if (type[oi] == '(') depth++;
                    else if (type[oi] == ')') depth--;
                }
                if (depth == 0)
                {
                    // Also check it's not part of the type name: map(*), function(*)
                    var prevChar = type.Length >= 2 ? type[^2] : '\0';
                    if (lastChar == '*' && prevChar == '(')
                    {
                        // map(*), function(*), array(*) — not an occurrence indicator
                    }
                    else if (lastChar == '?')
                    {
                        // element()? at depth 0 is occurrence; (?)" should not match
                        if (!type.EndsWith("(?)", StringComparison.Ordinal))
                        {
                            occurrence = Occurrence.ZeroOrOne;
                            type = type[..^1].Trim();
                        }
                    }
                    else if (lastChar == '*')
                    {
                        occurrence = Occurrence.ZeroOrMore;
                        type = type[..^1].Trim();
                    }
                    else if (lastChar == '+')
                    {
                        occurrence = Occurrence.OneOrMore;
                        type = type[..^1].Trim();
                    }
                }
            }
        }

        // Normalize internal whitespace: "element ()" → "element()", "document-node ()" → "document-node()"
        type = System.Text.RegularExpressions.Regex.Replace(type.Trim(), @"\s*\(\s*\)", "()");
        type = System.Text.RegularExpressions.Regex.Replace(type, @"\s*\(\s*\*\s*\)", "(*)");

        // Strip outer parentheses: "(function(...) as ...)" → "function(...) as ..."
        // Only strip when the parens are truly wrapping the whole type (balanced)
        while (type.StartsWith('(') && type.EndsWith(')'))
        {
            // Verify the opening paren matches the closing one (not an inner group)
            int depth = 0;
            bool isWrap = true;
            for (int pi = 0; pi < type.Length - 1; pi++)
            {
                if (type[pi] == '(') depth++;
                else if (type[pi] == ')') depth--;
                if (depth == 0) { isWrap = false; break; }
            }
            if (isWrap)
                type = type[1..^1].Trim();
            else
                break;
        }

        // Apply xpath-default-namespace: when set to the XSD namespace, unprefixed atomic type
        // names (like "double") should be resolved as "xs:double". Per XSLT 3.0 spec section 5.2,
        // xpath-default-namespace applies to type names in SequenceType syntax.
        if (context != null && !type.Contains(':', StringComparison.Ordinal) && !type.Contains('(', StringComparison.Ordinal))
        {
            var xdn = GetXpathDefaultNamespace(context);
            if (xdn == "http://www.w3.org/2001/XMLSchema")
                type = "xs:" + type;
        }

        // Normalize any namespace prefix bound to the XSD namespace to the canonical "xs:"
        // so the type-name switch (keyed on "xs:") recognizes it. A stylesheet may bind e.g.
        // xmlns:xsd="http://www.w3.org/2001/XMLSchema" and write as="xsd:string" (attr/as-0116);
        // without this the type resolved to item() and `instance of` checks were wrong.
        if (context != null && !type.Contains('(', StringComparison.Ordinal))
        {
            var colon = type.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                var prefix = type[..colon];
                if (prefix != "xs"
                    && context.GetNamespaceOfPrefix(prefix)?.NamespaceName == "http://www.w3.org/2001/XMLSchema")
                {
                    type = "xs:" + type[(colon + 1)..];
                }
            }
        }

        // Handle parameterized map types: map(xs:string, xs:boolean)
        if (type.StartsWith("map(", StringComparison.Ordinal) && type.EndsWith(')') && type != "map(*)")
        {
            var inner = type[4..^1].Trim(); // content between "map(" and ")"
            var commaIdx = inner.IndexOf(',', StringComparison.Ordinal);
            if (commaIdx > 0)
            {
                var keyTypeStr = inner[..commaIdx].Trim();
                var valueTypeStr = inner[(commaIdx + 1)..].Trim();
                var keyType = ParseAtomicItemType(keyTypeStr);
                var valueType = ParseAtomicItemType(valueTypeStr);
                return new XdmSequenceType
                {
                    ItemType = ItemType.Map,
                    Occurrence = occurrence,
                    MapKeyType = keyType,
                    MapValueType = valueType
                };
            }
        }

        // Handle empty-sequence() — must return before the itemType switch
        if (type == "empty-sequence()")
        {
            return new XdmSequenceType { ItemType = ItemType.Item, Occurrence = Occurrence.Zero };
        }

        // Handle element(name) or element(name, type) or element(*, type) — named element type test.
        // Routed through SplitPrefixedName so EQName syntax (Q{ns}local) parses correctly:
        // the previous local-only split-on-':' handler swallowed the namespace URI's colon
        // (turning Q{urn:expected}root into ElementName="expected}root" with no namespace).
        // processing-instruction(target). The switch below matches the UNNAMED spelling
        // exactly, so the named form fell to its `_ => ItemType.Item` default. That is not a
        // harmless approximation for a global: needsNodeOrphan tests for
        // ItemType.ProcessingInstruction, an Item does not match it, and the binding drops to
        // the legacy route that wraps the node in a DOCUMENT — so
        // `<xsl:variable as="processing-instruction(p)">` held a document, not a PI.
        // element(name) and attribute(name) already have their own branches below for exactly
        // this reason; this is the same gap for the third named kind test.
        if (type.StartsWith("processing-instruction(", StringComparison.Ordinal)
            && type.EndsWith(')') && type != "processing-instruction()")
        {
            return new XdmSequenceType
            {
                ItemType = ItemType.ProcessingInstruction,
                Occurrence = occurrence
            };
        }

        if (type.StartsWith("element(", StringComparison.Ordinal) && type.EndsWith(')')  &&
            type != "element()" && type != "element(*)")
        {
            var inner = type[8..^1].Trim();
            var commaIdx = inner.IndexOf(',', StringComparison.Ordinal);
            var namePart = commaIdx >= 0 ? inner[..commaIdx].Trim() : inner;
            if (namePart == "*" || string.IsNullOrEmpty(namePart))
                return new XdmSequenceType { ItemType = ItemType.Element, Occurrence = occurrence };
            var (localName, nsUri) = SplitPrefixedName(namePart, context);
            return new XdmSequenceType
            {
                ItemType = ItemType.Element,
                Occurrence = occurrence,
                ElementName = localName,
                ElementNamespace = nsUri,
            };
        }

        // Handle document-node(element(name)) or document-node(element(name, type)) — document with named element test
        if (type.StartsWith("document-node(element(", StringComparison.Ordinal) && type.EndsWith("))", StringComparison.Ordinal))
        {
            var inner = type[22..^2].Trim();
            // Handle document-node(element(name, type)) — extract just the name part
            var commaIdx2 = inner.IndexOf(',', StringComparison.Ordinal);
            if (commaIdx2 >= 0)
                inner = inner[..commaIdx2].Trim();
            if (inner == "*" || inner.Length == 0)
                return new XdmSequenceType { ItemType = ItemType.Document, Occurrence = occurrence };
            var (docLocal, _) = SplitPrefixedName(inner, context);
            // XdmSequenceType has no DocumentElementNamespace yet; document-element namespace
            // matching is a future enhancement (rarely used in real stylesheets).
            return new XdmSequenceType
            {
                ItemType = ItemType.Document,
                Occurrence = occurrence,
                DocumentElementName = docLocal,
            };
        }

        // Handle attribute(name) or attribute(name, type) or attribute(*, type) — named attribute type test
        if (type.StartsWith("attribute(", StringComparison.Ordinal) && type.EndsWith(')')  &&
            type != "attribute()" && type != "attribute(*)")
        {
            var inner = type[10..^1].Trim();
            var commaIdx3 = inner.IndexOf(',', StringComparison.Ordinal);
            var namePart = commaIdx3 >= 0 ? inner[..commaIdx3].Trim() : inner;
            if (namePart == "*" || string.IsNullOrEmpty(namePart))
                return new XdmSequenceType { ItemType = ItemType.Attribute, Occurrence = occurrence };
            var (attrLocal, attrNs) = SplitPrefixedName(namePart, context);
            return new XdmSequenceType
            {
                ItemType = ItemType.Attribute,
                Occurrence = occurrence,
                AttributeName = attrLocal,
                AttributeNamespace = attrNs,
            };
        }

        // Handle schema-element(name) — schema-aware element test. The provider
        // supplies substitution-group / type-annotation matching at runtime.
        if (type.StartsWith("schema-element(", StringComparison.Ordinal) && type.EndsWith(')'))
        {
            var inner = type["schema-element(".Length..^1].Trim();
            var (localName, nsUri) = SplitPrefixedName(inner, context);
            return new XdmSequenceType
            {
                ItemType = ItemType.SchemaElement,
                Occurrence = occurrence,
                SchemaElementName = localName,
                SchemaElementNamespace = nsUri,
            };
        }

        // Handle schema-attribute(name)
        if (type.StartsWith("schema-attribute(", StringComparison.Ordinal) && type.EndsWith(')'))
        {
            var inner = type["schema-attribute(".Length..^1].Trim();
            var (localName, nsUri) = SplitPrefixedName(inner, context);
            return new XdmSequenceType
            {
                ItemType = ItemType.SchemaAttribute,
                Occurrence = occurrence,
                SchemaAttributeName = localName,
                SchemaAttributeNamespace = nsUri,
            };
        }

        // Handle parameterized array(TYPE) and map(KEY, VALUE) forms
        if (type.StartsWith("array(", StringComparison.Ordinal) && type.EndsWith(')'))
            return new XdmSequenceType { ItemType = ItemType.Array, Occurrence = occurrence };
        if (type.StartsWith("map(", StringComparison.Ordinal) && type.EndsWith(')'))
            return new XdmSequenceType { ItemType = ItemType.Map, Occurrence = occurrence };

        // Handle function types: function(*), function(T1, T2, ...) as ReturnType
        if (type.StartsWith("function(", StringComparison.Ordinal) && type != "function(*)")
        {
            // Find matching closing paren for the parameter list (start after opening '(' at index 8)
            int depth = 0;
            int closeIdx = -1;
            for (int fi = 9; fi < type.Length; fi++)
            {
                if (type[fi] == '(') depth++;
                else if (type[fi] == ')')
                {
                    if (depth == 0) { closeIdx = fi; break; }
                    depth--;
                }
            }
            if (closeIdx > 0)
            {
                var paramsPart = type[9..closeIdx].Trim();
                var afterParen = type[(closeIdx + 1)..].Trim();
                if (paramsPart.Length > 0)
                {
                    // Has parameter types — return type is required (XPST0003)
                    if (!afterParen.StartsWith("as ", StringComparison.OrdinalIgnoreCase))
                        throw new XsltException("XPST0003: Function type with parameter types requires 'as ReturnType'");

                    // Parse parameter types
                    var paramTypes = ParseFunctionParameterTypes(paramsPart, context);
                    // Parse return type (skip "as ")
                    var returnTypeStr = afterParen[3..].Trim();
                    var returnType = ParseSequenceType(returnTypeStr, context);

                    return new XdmSequenceType
                    {
                        ItemType = ItemType.Function,
                        Occurrence = occurrence,
                        FunctionParameterTypes = paramTypes,
                        FunctionReturnType = returnType
                    };
                }
                // function() as ReturnType — zero-arity typed function
                if (afterParen.StartsWith("as ", StringComparison.OrdinalIgnoreCase))
                {
                    var returnTypeStr = afterParen[3..].Trim();
                    var returnType = ParseSequenceType(returnTypeStr, context);
                    return new XdmSequenceType
                    {
                        ItemType = ItemType.Function,
                        Occurrence = occurrence,
                        FunctionParameterTypes = Array.Empty<XdmSequenceType>(),
                        FunctionReturnType = returnType
                    };
                }
                return new XdmSequenceType { ItemType = ItemType.Function, Occurrence = occurrence };
            }
        }

        var itemType = type switch
        {
            "item()" => ItemType.Item,
            "node()" => ItemType.Node,
            "element()" or "element(*)" => ItemType.Element,
            "attribute()" or "attribute(*)" => ItemType.Attribute,
            "text()" => ItemType.Text,
            "document-node()" => ItemType.Document,
            "comment()" => ItemType.Comment,
            "processing-instruction()" => ItemType.ProcessingInstruction,
            "namespace-node()" => ItemType.Node,
            "xs:string" => ItemType.String,
            "xs:integer" => ItemType.Integer,
            "xs:decimal" => ItemType.Decimal,
            "xs:double" => ItemType.Double,
            "xs:float" => ItemType.Float,
            "xs:boolean" => ItemType.Boolean,
            "xs:date" => ItemType.Date,
            "xs:dateTime" => ItemType.DateTime,
            "xs:time" => ItemType.Time,
            "xs:anyAtomicType" => ItemType.AnyAtomicType,
            "xs:untypedAtomic" => ItemType.UntypedAtomic,
            "xs:anyURI" => ItemType.AnyUri,
            "xs:QName" => ItemType.QName,
            "xs:duration" => ItemType.Duration,
            "xs:yearMonthDuration" => ItemType.YearMonthDuration,
            "xs:dayTimeDuration" => ItemType.DayTimeDuration,
            "xs:gYearMonth" => ItemType.GYearMonth,
            "xs:gYear" => ItemType.GYear,
            "xs:gMonthDay" => ItemType.GMonthDay,
            "xs:gDay" => ItemType.GDay,
            "xs:gMonth" => ItemType.GMonth,
            "xs:hexBinary" => ItemType.HexBinary,
            "xs:base64Binary" => ItemType.Base64Binary,
            // Derived string types — all subtypes of xs:string
            "xs:normalizedString" or "xs:token" or "xs:language" or "xs:NMTOKEN"
                or "xs:Name" or "xs:NCName" or "xs:ID" or "xs:IDREF" or "xs:ENTITY" => ItemType.String,
            // Derived integer types — all subtypes of xs:integer
            "xs:long" or "xs:int" or "xs:short" or "xs:byte"
                or "xs:nonNegativeInteger" or "xs:positiveInteger"
                or "xs:nonPositiveInteger" or "xs:negativeInteger"
                or "xs:unsignedLong" or "xs:unsignedInt" or "xs:unsignedShort" or "xs:unsignedByte" => ItemType.Integer,
            "xs:NOTATION" => ItemType.AnyAtomicType,
            "map(*)" => ItemType.Map,
            "array(*)" => ItemType.Array,
            "function(*)" => ItemType.Function,
            _ => ItemType.Item
        };

        return new XdmSequenceType { ItemType = itemType, Occurrence = occurrence };
    }


    private static string? GetLiteralAvtValue(XsltAttributeValueTemplate avt)
    {
        if (avt.Parts.Count == 1 && avt.Parts[0] is AvtLiteral lit)
            return lit.Value;
        return null;
    }

}
