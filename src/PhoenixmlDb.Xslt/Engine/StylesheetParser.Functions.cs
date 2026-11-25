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

    private static bool MatchesAcceptPattern(QName name, string pattern, bool isWildcard, XElement element)
    {
        if (pattern == "*") return true;
        if (isWildcard && pattern.EndsWith(":*", StringComparison.Ordinal))
        {
            var prefix = pattern[..^2];
            var nsUri = element.GetNamespaceOfPrefix(prefix)?.NamespaceName;
            if (nsUri != null)
            {
                var nsId = ResolveNamespaceUri(nsUri);
                return name.Namespace == nsId;
            }
            return name.Prefix == prefix;
        }
        return name.LocalName == pattern;
    }


    /// <summary>
    /// Tests whether a component QName matches a token in <c>xsl:expose/@names</c>.
    /// </summary>
    /// <remarks>
    /// This is the twin of <see cref="AcceptTokenMatches"/>, which does the same job for
    /// <c>xsl:accept/@names</c> over the same token grammar (§3.6.3.1: "a NameTest or a
    /// NamedFunctionRef"). Only one of the pair was complete: this one understood <c>*</c> and
    /// <c>prefix:*</c> and nothing else, so <c>*:local</c> and every <c>Q{uri}…</c> form fell
    /// through to an exact-name comparison against the raw token text and matched nothing — with
    /// no symptom, because an xsl:expose token that matched nothing did nothing. Delegating makes
    /// the two forms of the same rule one implementation.
    /// </remarks>
    private static bool MatchesExposePattern(QName name, string pattern, bool isWildcard, System.Xml.Linq.XElement? element)
    {
        if (pattern == "*") return true;
        if (element != null) return AcceptTokenMatches(name, pattern, element);
        return name.LocalName == pattern;
    }


    private XsltAnalyzeString ParseAnalyzeString(XElement element, SourceLocation? location)
    {
        var select = ParseExpr(RequiredAttribute(element, "select").Value, element.Attribute("select"));
        var regex = ParseAvt(RequiredAttribute(element, "regex").Value, element, element.Attribute("regex"));
        var flagsAttr = element.Attribute("flags");

        XsltSequenceConstructor? matchingSubstring = null;
        XsltSequenceConstructor? nonMatchingSubstring = null;
        bool seenMatching = false;
        bool seenNonMatching = false;
        bool seenFallback = false;

        foreach (var child in element.Elements())
        {
            if (child.Name == XsltNs + "matching-substring")
            {
                // XTSE0010: matching-substring must come before non-matching-substring and fallback
                if (seenNonMatching || seenFallback)
                    throw new XsltException(
                        "XTSE0010: xsl:matching-substring must appear before xsl:non-matching-substring and xsl:fallback",
                        GetSourceLocation(child));
                matchingSubstring = ParseSequenceConstructor(child);
                seenMatching = true;
            }
            else if (child.Name == XsltNs + "non-matching-substring")
            {
                // XTSE0010: non-matching-substring must come before fallback
                if (seenFallback)
                    throw new XsltException(
                        "XTSE0010: xsl:non-matching-substring must appear before xsl:fallback",
                        GetSourceLocation(child));
                nonMatchingSubstring = ParseSequenceConstructor(child);
                seenNonMatching = true;
            }
            else if (child.Name == XsltNs + "fallback")
            {
                seenFallback = true;
            }
            else
            {
                // XTSE0010: unexpected child element
                if (seenMatching || seenNonMatching)
                    throw new XsltException(
                        $"XTSE0010: Unexpected child element in xsl:analyze-string: {child.Name.LocalName}",
                        GetSourceLocation(child));
            }
        }

        // XTSE1130: at least one of matching-substring/non-matching-substring required
        if (matchingSubstring == null && nonMatchingSubstring == null)
            throw new InvalidOperationException(
                "XTSE1130: xsl:analyze-string must contain xsl:matching-substring or xsl:non-matching-substring");

        return new XsltAnalyzeString
        {
            Location = location,
            Select = select,
            Regex = regex,
            Flags = flagsAttr != null ? ParseAvt(flagsAttr.Value, element, flagsAttr) : null,
            MatchingSubstring = matchingSubstring,
            NonMatchingSubstring = nonMatchingSubstring
        };
    }


    private XsltEvaluate ParseEvaluate(XElement element, SourceLocation? location)
    {
        var xpathAttr = element.Attribute("xpath")
            ?? throw new XsltException("XTSE0010: xsl:evaluate requires an xpath attribute", location);
        var contextItemAttr = element.Attribute("context-item");
        var baseUriAttr = element.Attribute("base-uri");
        var nsContextAttr = element.Attribute("namespace-context");
        var withParamsAttr = element.Attribute("with-params");
        var asAttr = element.Attribute("as");
        var schemaAwareAttr = element.Attribute("schema-aware");
        // A boolean attribute (yes|no|true|false|1|0): a literal value outside that set is a static
        // error; one computed by an AVT is checked when it is evaluated (W3C evaluate-038, -014).
        if (schemaAwareAttr != null && !schemaAwareAttr.Value.Contains('{', StringComparison.Ordinal)
            && schemaAwareAttr.Value.Trim() is not ("yes" or "no" or "true" or "false" or "1" or "0"))
            throw new XsltException(
                $"XTSE0020: Invalid value '{schemaAwareAttr.Value}' for the schema-aware attribute of xsl:evaluate (must be yes, no, true, false, 1 or 0)",
                location);
        var collationAttr = element.Attribute("default-collation");

        // Parse with-param children
        var withParams = new List<XsltWithParam>();
        foreach (var child in element.Elements())
        {
            if (child.Name == XsltNs + "with-param")
                withParams.Add(ParseWithParam(child));
            else if (child.Name == XsltNs + "fallback")
            { /* handled below */ }
        }

        var fallbackElem = element.Elements()
            .FirstOrDefault(e => e.Name == XsltNs + "fallback");

        // Collect in-scope namespace bindings from the xsl:evaluate element
        // These serve as default namespace context when namespace-context is not specified
        var defaultNsBindings = new Dictionary<string, string>();
        {
            // Walk up from the element to collect all in-scope namespace declarations
            var current = element;
            while (current != null)
            {
                foreach (var attr in current.Attributes())
                {
                    if (attr.IsNamespaceDeclaration)
                    {
                        var prefix = attr.Name.LocalName == "xmlns" ? "" : attr.Name.LocalName;
                        var uri = attr.Value;
                        if (!defaultNsBindings.ContainsKey(prefix) && prefix != "xml" && uri != XsltNs.NamespaceName)
                            defaultNsBindings[prefix] = uri;
                    }
                }
                current = current.Parent as XElement;
            }
        }

        return new XsltEvaluate
        {
            Location = location,
            Xpath = ParseExpr(xpathAttr.Value, xpathAttr),
            ContextItem = contextItemAttr != null ? ParseExpr(contextItemAttr.Value, contextItemAttr) : null,
            BaseUri = baseUriAttr != null ? ParseAvt(baseUriAttr.Value, element, baseUriAttr) : null,
            NamespaceContext = nsContextAttr != null ? ParseExpr(nsContextAttr.Value, nsContextAttr) : null,
            WithParamsExpr = withParamsAttr != null ? ParseExpr(withParamsAttr.Value, withParamsAttr) : null,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            SchemaAware = schemaAwareAttr != null ? ParseAvt(schemaAwareAttr.Value, element, schemaAwareAttr) : null,
            EvaluateDefaultCollation = collationAttr?.Value,
            WithParams = withParams,
            Fallback = fallbackElem != null ? ParseSequenceConstructor(fallbackElem) : null,
            DefaultNamespaceBindings = defaultNsBindings,
            XpathDefaultNamespace = GetXpathDefaultNamespace(element),
        };
    }


    /// <summary>
    /// Whether a static parameter's default value can be converted to its declared atomic type.
    /// The coercion is lenient (it gives back a value it cannot convert), so each item is then
    /// checked against the type. A node or non-atomic declared type is not judged here.
    /// </summary>
    private static bool StaticDefaultMatches(object value, XdmSequenceType type)
    {
        if (!DefaultXsltExecutionContext.IsCastableAtomicTypePublic(type.ItemType))
            return true;
        object? coerced;
        try
        {
            coerced = DefaultXsltExecutionContext.CoerceSelectValueToDeclaredType(value, type);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException
                                   or PhoenixmlDb.XQuery.Execution.XQueryRuntimeException or XsltException)
        {
            return false;
        }
        foreach (var item in coerced is object?[] items ? items : [coerced])
        {
            if (item is null or PhoenixmlDb.Xdm.Nodes.XdmNode or ResultTreeFragment)
                continue;
            if (!PhoenixmlDb.XQuery.Execution.TypeCastHelper.MatchesItemType(item, type.ItemType))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The value of the select of a static variable or parameter, or null when it cannot be
    /// evaluated yet (see <see cref="TryEvaluateStatic"/>). A function a static expression may not
    /// call, a stylesheet function for one, is XPST0017; the message says what to change.
    /// </summary>
    private object? EvaluateStaticSelectSafe(XQueryExpression expr, XElement context)
    {
        try
        {
            return TryEvaluateStatic(expr, context, out var value) ? value : null;
        }
        catch (XsltException e) when (e.ErrorCode == "XPST0017")
        {
            throw StaticSelectXpst0017(e, context);
        }
    }

    // XPST0017 in the select of a static variable or parameter, with what to change: it loaded
    // before 3.3.0, the value computed at run time (Lucas, 2026-11-24).
    private static XsltException StaticSelectXpst0017(XsltException e, XElement declaration)
        => new(e.Message.TrimEnd('.') + ". The select of a static variable or parameter is evaluated when the "
            + "stylesheet is compiled, where only the functions of XSLT 3.0 §9.7 can be called, not a stylesheet "
            + "function: remove static=\"yes\" from the declaration, or do not call the function in its select.",
            e.Location ?? GetSourceLocation(declaration));



}
