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


    private bool MatchesExposePattern(QName name, string pattern, bool isWildcard, System.Xml.Linq.XElement? element)
    {
        if (pattern == "*") return true;
        if (isWildcard && pattern.EndsWith(":*", StringComparison.Ordinal))
        {
            // Match by namespace prefix: "p:*" matches all names in the p: namespace
            var prefix = pattern[..^2];
            // Resolve the prefix to a namespace URI
            var nsUri = element?.GetNamespaceOfPrefix(prefix)?.NamespaceName;
            if (nsUri != null)
            {
                var nsId = ResolveNamespaceUri(nsUri);
                return name.Namespace == nsId;
            }
            return name.Prefix == prefix;
        }
        // Exact name match
        if (element != null)
        {
            _nsContext = element;
            try
            {
                var patternQName = ParseQName(pattern, element);
                return name.Equals(patternQName) || name.LocalName == patternQName.LocalName;
            }
            catch (XsltException)
            {
                return name.LocalName == pattern;
            }
            finally { _nsContext = null; }
        }
        return name.LocalName == pattern;
    }


    private XsltAnalyzeString ParseAnalyzeString(XElement element, SourceLocation? location)
    {
        var select = ParseExpr(element.Attribute("select")!.Value, element.Attribute("select"));
        var regex = ParseAvt(element.Attribute("regex")!.Value, element, element.Attribute("regex"));
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
            EvaluateDefaultCollation = collationAttr?.Value,
            WithParams = withParams,
            Fallback = fallbackElem != null ? ParseSequenceConstructor(fallbackElem) : null,
            DefaultNamespaceBindings = defaultNsBindings,
            XpathDefaultNamespace = GetXpathDefaultNamespace(element),
        };
    }


    /// <summary>
    /// Evaluates a simple static expression in a shadow attribute.
    /// Supports string literals, numeric literals, $variables, || concatenation, and QName().
    /// </summary>
    private static string? EvaluateShadowExpression(string expr, Dictionary<string, string> staticParams)
    {
        expr = expr.Trim();

        // String concatenation: split on || and evaluate each side
        // Must handle cases like 'a' || 'b' || $var
        if (expr.Contains("||", StringComparison.Ordinal))
        {
            var parts = SplitOnOperator(expr, "||");
            if (parts != null)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var part in parts)
                {
                    var partVal = EvaluateShadowExpression(part.Trim(), staticParams);
                    if (partVal == null) return null;
                    sb.Append(partVal);
                }
                return sb.ToString();
            }
        }

        // Variable reference
        if (expr.StartsWith('$'))
        {
            var paramName = expr[1..];
            return staticParams.TryGetValue(paramName, out var val) ? val : null;
        }

        // String literal
        if ((expr.StartsWith('\'') && expr.EndsWith('\'')) ||
            (expr.StartsWith('"') && expr.EndsWith('"')))
        {
            return expr[1..^1];
        }

        // Numeric literal
        if (double.TryParse(expr, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return expr;
        }

        // QName function: QName('ns', 'prefix:local') → prefix:local
        if (expr.StartsWith("QName(", StringComparison.Ordinal) && expr.EndsWith(')'))
        {
            var inner = expr[6..^1];
            var commaIdx = FindCommaOutsideQuotes(inner);
            if (commaIdx > 0)
            {
                var localPart = inner[(commaIdx + 1)..].Trim();
                return EvaluateShadowExpression(localPart, staticParams);
            }
        }

        // system-property('xsl:version') → '3.0', etc.
        if (expr.StartsWith("system-property(", StringComparison.Ordinal) && expr.EndsWith(')'))
        {
            var argExpr = expr[16..^1].Trim();
            var argVal = EvaluateShadowExpression(argExpr, staticParams);
            if (argVal != null)
            {
                // Strip 'xsl:' prefix — system-property expects XSLT namespace properties
                var propName = argVal;
                if (propName.Contains(':', StringComparison.Ordinal))
                    propName = propName[(propName.IndexOf(':', StringComparison.Ordinal) + 1)..];
                return propName switch
                {
                    "version" => "3.0",
                    "vendor" => "PhoenixmlDb",
                    "vendor-url" => "https://endpointsystems.com",
                    "product-name" => "PhoenixmlDb XSLT",
                    "product-version" => "1.0",
                    "is-schema-aware" => "no",
                    "supports-serialization" => "yes",
                    "supports-backwards-compatibility" => "yes",
                    "supports-namespace-axis" => "yes",
                    "supports-streaming" => "yes",
                    "supports-dynamic-evaluation" => "yes",
                    "supports-higher-order-functions" => "yes",
                    "xpath-version" => "4.0",
                    "xsd-version" => "1.1",
                    _ => ""
                };
            }
        }

        // if (...) then ... else ... conditional expression
        if (expr.StartsWith("if", StringComparison.Ordinal) && expr.Length > 2 &&
            (expr[2] == ' ' || expr[2] == '('))
        {
            // Find the condition in parentheses
            var condStart = expr.IndexOf('(', StringComparison.Ordinal);
            if (condStart >= 0)
            {
                var condEnd = FindMatchingParen(expr, condStart);
                if (condEnd > condStart)
                {
                    var condExpr = expr[(condStart + 1)..condEnd].Trim();
                    var rest = expr[(condEnd + 1)..].Trim();

                    // Find 'then' and 'else' keywords
                    var thenIdx = FindKeyword(rest, "then");
                    if (thenIdx >= 0)
                    {
                        var afterThen = rest[(thenIdx + 4)..].Trim();
                        var elseIdx = FindKeyword(afterThen, "else");
                        if (elseIdx >= 0)
                        {
                            var thenExpr = afterThen[..elseIdx].Trim();
                            var elseExpr = afterThen[(elseIdx + 4)..].Trim();

                            var condResult = EvaluateStaticCondition(condExpr, staticParams);
                            if (condResult.HasValue)
                            {
                                return condResult.Value
                                    ? EvaluateShadowExpression(thenExpr, staticParams)
                                    : EvaluateShadowExpression(elseExpr, staticParams);
                            }
                        }
                    }
                }
            }
        }

        // Check for XSLT runtime functions that are NOT available in static expressions (XPST0017)
        var parenIdx = expr.IndexOf('(', StringComparison.Ordinal);
        if (parenIdx > 0 && expr.EndsWith(')'))
        {
            var funcName = expr[..parenIdx].Trim();
            var localName = funcName.Contains(':', StringComparison.Ordinal)
                ? funcName[(funcName.IndexOf(':', StringComparison.Ordinal) + 1)..] : funcName;
            if (localName.Length > 0 && !localName.Contains(' ', StringComparison.Ordinal))
            {
                // XSLT runtime-only functions that cannot appear in static expressions
                var isRuntimeOnly = localName is "current" or "current-group" or "current-grouping-key"
                    or "current-merge-group" or "current-merge-key" or "current-output-uri"
                    or "regex-group" or "unparsed-entity-uri" or "unparsed-entity-public-id"
                    or "accumulator-before" or "accumulator-after" or "snapshot" or "copy-of";
                if (isRuntimeOnly)
                    throw new XsltException($"XPST0017: Function '{funcName}' is not available in a static expression (shadow attribute)");
            }
        }

        // For unrecognized expressions (including XPath function calls we can't evaluate inline),
        // return null — the shadow attribute value will use whatever was already accumulated.
        return null; // Cannot evaluate
    }


    /// <summary>
    /// Evaluates a simple static condition (e.g., "system-property('xsl:version') = '3.0'").
    /// </summary>
    private static bool? EvaluateStaticCondition(string condExpr, Dictionary<string, string> staticParams)
    {
        condExpr = condExpr.Trim();

        // Handle comparison: lhs = rhs or lhs != rhs
        var eqIdx = FindKeyword(condExpr, "=");
        if (eqIdx > 0)
        {
            // Check for != (the char before = is !)
            var isNotEqual = eqIdx > 0 && condExpr[eqIdx - 1] == '!';
            var lhsStr = isNotEqual ? condExpr[..(eqIdx - 1)].Trim() : condExpr[..eqIdx].Trim();
            var rhsStr = condExpr[(eqIdx + 1)..].Trim();

            var lhs = EvaluateShadowExpression(lhsStr, staticParams);
            var rhs = EvaluateShadowExpression(rhsStr, staticParams);

            if (lhs != null && rhs != null)
                return isNotEqual ? lhs != rhs : lhs == rhs;
        }

        // Handle boolean function result
        var val = EvaluateShadowExpression(condExpr, staticParams);
        if (val != null)
            return val is "yes" or "true" or "1";

        return null;
    }


    /// <summary>
    /// Safely evaluates a static param/variable's select expression.
    /// Returns the evaluated value or null if evaluation fails (e.g., complex expressions).
    /// </summary>
    private object? EvaluateStaticSelectSafe(XQueryExpression expr, XElement context)
    {
        try
        {
            return EvaluateStaticExpression(expr, context);
        }
        catch (XsltException ex) when (ex.ErrorCode == "XPST0008")
        {
            // Forward reference to undeclared static variable — must be a real error
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException or ArgumentException or XsltException)
        {
            return null;
        }
    }


    /// <summary>
    /// Statically evaluates an XPath expression in the use-when context.
    /// Only a limited set of expressions are supported (literals, comparisons,
    /// boolean operators, and system functions like element-available, system-property, etc.).
    /// </summary>
    private object? EvaluateStaticExpression(XQueryExpression expr, XElement context)
    {
        switch (expr)
        {
            case BooleanLiteral bl:
                return bl.Value;

            case IntegerLiteral il:
                return il.Value is long lv ? (double)lv : Convert.ToDouble(il.Value, System.Globalization.CultureInfo.InvariantCulture);

            case DecimalLiteral dl:
                return (double)dl.Value;

            case DoubleLiteral dbl:
                return dbl.Value;

            case StringLiteral sl:
                return sl.Value;

            case EmptySequence:
                return null;

            case UnaryExpression ue:
                var operand = EvaluateStaticExpression(ue.Operand, context);
                if (ue.Operator == UnaryOperator.Not)
                    return !CoerceToBoolean(operand);
                return ue.Operator == UnaryOperator.Minus ? -ToDouble(operand) : ToDouble(operand);

            case BinaryExpression be:
                return EvaluateStaticBinary(be, context);

            case FunctionCallExpression fc:
                return EvaluateStaticFunction(fc, context);

            case SequenceExpression seq:
                var items = new List<object?>();
                foreach (var item in seq.Items)
                {
                    var val = EvaluateStaticExpression(item, context);
                    if (val is List<object?> subList)
                        items.AddRange(subList);
                    else
                        items.Add(val);
                }
                return items;

            case RangeExpression re:
                var start = (int)ToDouble(EvaluateStaticExpression(re.Start, context));
                var end = (int)ToDouble(EvaluateStaticExpression(re.End, context));
                var range = new List<object?>();
                for (int i = start; i <= end; i++)
                    range.Add((double)i);
                return range;

            case QuantifiedExpression qe:
                return EvaluateStaticQuantified(qe, context, _staticVariables);

            case IfExpression ie:
                return CoerceToBoolean(EvaluateStaticExpression(ie.Condition, context))
                    ? EvaluateStaticExpression(ie.Then, context)
                    : ie.Else != null ? EvaluateStaticExpression(ie.Else, context) : null;

            case VariableReference vr:
                if (_staticVariables.TryGetValue(vr.Name, out var varVal))
                    return varVal;
                // Show the variable's display form (prefix:local or Q{uri}local) so users can
                // tell a prefixed reference apart from an unprefixed one — critical when
                // diagnosing namespace mismatches (e.g. $v:debug vs $debug).
                throw new XsltException(
                    $"XPST0008: Variable ${FormatVariableName(vr.Name)} is not declared in the static use-when context",
                    GetSourceLocation(context));

            case InstanceOfExpression instOf:
            {
                var value = EvaluateStaticExpression(instOf.Expression, context);
                return StaticInstanceOf(value, instOf.TargetType);
            }

            case ContextItemExpression:
                throw new XsltException("XPDY0002: Context item (.) is not available in static use-when context");

            // Path/step expressions access the context item, which is absent in use-when
            case PathExpression:
            case StepExpression:
                throw new XsltException("XPDY0002: Context item is not available in static use-when context (path expressions require a context item)");

            default:
                throw new InvalidOperationException($"Cannot statically evaluate expression: {expr}");
        }
    }


    private object? EvaluateStaticBinary(BinaryExpression be, XElement context)
    {
        // Short-circuit for and/or
        if (be.Operator == BinaryOperator.And)
        {
            var left = CoerceToBoolean(EvaluateStaticExpression(be.Left, context));
            return left && CoerceToBoolean(EvaluateStaticExpression(be.Right, context));
        }
        if (be.Operator == BinaryOperator.Or)
        {
            var left = CoerceToBoolean(EvaluateStaticExpression(be.Left, context));
            return left || CoerceToBoolean(EvaluateStaticExpression(be.Right, context));
        }

        var leftVal = EvaluateStaticExpression(be.Left, context);
        var rightVal = EvaluateStaticExpression(be.Right, context);

        return be.Operator switch
        {
            // Arithmetic
            BinaryOperator.Add => ToDouble(leftVal) + ToDouble(rightVal),
            BinaryOperator.Subtract => ToDouble(leftVal) - ToDouble(rightVal),
            BinaryOperator.Multiply => ToDouble(leftVal) * ToDouble(rightVal),
            BinaryOperator.Divide => ToDouble(leftVal) / ToDouble(rightVal),
            BinaryOperator.IntegerDivide => (double)(long)(ToDouble(leftVal) / ToDouble(rightVal)),
            BinaryOperator.Modulo => ToDouble(leftVal) % ToDouble(rightVal),

            // Value comparisons
            BinaryOperator.Equal => CompareValues(leftVal, rightVal) == 0,
            BinaryOperator.NotEqual => CompareValues(leftVal, rightVal) != 0,
            BinaryOperator.LessThan => CompareValues(leftVal, rightVal) < 0,
            BinaryOperator.LessOrEqual => CompareValues(leftVal, rightVal) <= 0,
            BinaryOperator.GreaterThan => CompareValues(leftVal, rightVal) > 0,
            BinaryOperator.GreaterOrEqual => CompareValues(leftVal, rightVal) >= 0,

            // General comparisons
            BinaryOperator.GeneralEqual => CompareValues(leftVal, rightVal) == 0,
            BinaryOperator.GeneralNotEqual => CompareValues(leftVal, rightVal) != 0,
            BinaryOperator.GeneralLessThan => CompareValues(leftVal, rightVal) < 0,
            BinaryOperator.GeneralLessOrEqual => CompareValues(leftVal, rightVal) <= 0,
            BinaryOperator.GeneralGreaterThan => CompareValues(leftVal, rightVal) > 0,
            BinaryOperator.GeneralGreaterOrEqual => CompareValues(leftVal, rightVal) >= 0,

            // String concat
            BinaryOperator.Concat => (leftVal?.ToString() ?? "") + (rightVal?.ToString() ?? ""),

            _ => throw new InvalidOperationException($"Cannot statically evaluate operator: {be.Operator}")
        };
    }


    private bool EvaluateStaticQuantified(QuantifiedExpression qe, XElement context, Dictionary<QName, object?> vars)
    {
        return EvaluateQuantifiedBinding(qe, context, vars, 0);
    }


    private bool EvaluateQuantifiedBinding(QuantifiedExpression qe, XElement context,
        Dictionary<QName, object?> vars, int bindingIndex)
    {
        if (bindingIndex >= qe.Bindings.Count)
            return CoerceToBoolean(EvaluateStaticExpression(qe.Satisfies, context));

        var binding = qe.Bindings[bindingIndex];
        var varName = binding.Variable;
        var sequenceVal = EvaluateStaticExpression(binding.Expression, context);

        // Convert to list of items
        var items = sequenceVal is List<object?> list ? list : [sequenceVal];

        var savedVal = vars.TryGetValue(varName, out var old) ? old : null;
        var hadVal = vars.ContainsKey(varName);

        try
        {
            foreach (var item in items)
            {
                vars[varName] = item;
                var result = EvaluateQuantifiedBinding(qe, context, vars, bindingIndex + 1);

                if (qe.Quantifier == Quantifier.Some && result)
                    return true;
                if (qe.Quantifier == Quantifier.Every && !result)
                    return false;
            }

            return qe.Quantifier == Quantifier.Every; // every: true if none failed; some: false if none matched
        }
        finally
        {
            if (hadVal) vars[varName] = savedVal;
            else vars.Remove(varName);
        }
    }


    private object? EvaluateStaticFunction(FunctionCallExpression fc, XElement context)
    {
        var localName = fc.Name.LocalName;

        switch (localName)
        {
            case "true":
                return true;
            case "false":
                return false;
            case "not":
                if (fc.Arguments.Count == 1)
                    return !CoerceToBoolean(EvaluateStaticExpression(fc.Arguments[0], context));
                break;
            case "empty":
                if (fc.Arguments.Count == 1)
                {
                    var val = EvaluateStaticExpression(fc.Arguments[0], context);
                    return val == null || (val is List<object?> emptyList && emptyList.Count == 0);
                }
                break;
            case "exists":
                if (fc.Arguments.Count == 1)
                {
                    var val = EvaluateStaticExpression(fc.Arguments[0], context);
                    return val != null && !(val is List<object?> existsList && existsList.Count == 0);
                }
                break;
            case "count":
                if (fc.Arguments.Count == 1)
                {
                    var val = EvaluateStaticExpression(fc.Arguments[0], context);
                    if (val == null) return 0.0;
                    if (val is List<object?> countList) return (double)countList.Count;
                    return 1.0;
                }
                break;
            case "concat":
                if (fc.Arguments.Count >= 2)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var arg in fc.Arguments)
                        sb.Append(EvaluateStaticExpression(arg, context)?.ToString() ?? "");
                    return sb.ToString();
                }
                break;
            case "string-length":
                if (fc.Arguments.Count == 1)
                    return (double)(EvaluateStaticExpression(fc.Arguments[0], context)?.ToString()?.Length ?? 0);
                break;
            case "substring":
                if (fc.Arguments.Count >= 2)
                {
                    var str = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    var startPos = (int)Math.Round(ToDouble(EvaluateStaticExpression(fc.Arguments[1], context))) - 1;
                    if (startPos < 0) startPos = 0;
                    if (startPos >= str.Length) return "";
                    if (fc.Arguments.Count == 3)
                    {
                        var len = (int)Math.Round(ToDouble(EvaluateStaticExpression(fc.Arguments[2], context)));
                        return str.Substring(startPos, Math.Min(len, str.Length - startPos));
                    }
                    return str[startPos..];
                }
                break;
            case "boolean":
                if (fc.Arguments.Count == 1)
                    return CoerceToBoolean(EvaluateStaticExpression(fc.Arguments[0], context));
                break;
            case "string":
                if (fc.Arguments.Count == 1)
                    return EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                break;
            case "number":
                if (fc.Arguments.Count == 1)
                    return ToDouble(EvaluateStaticExpression(fc.Arguments[0], context));
                break;

            case "system-property":
                if (fc.Arguments.Count == 1)
                {
                    var propName = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    return EvaluateSystemProperty(propName, context);
                }
                break;

            case "element-available":
                if (fc.Arguments.Count == 1)
                {
                    var elemName = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    return EvaluateElementAvailable(elemName, context);
                }
                break;

            case "function-available":
                if (fc.Arguments.Count >= 1)
                {
                    var funcName = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    var arity = fc.Arguments.Count > 1
                        ? (int)ToDouble(EvaluateStaticExpression(fc.Arguments[1], context))
                        : -1;
                    return EvaluateFunctionAvailable(funcName, context, arity);
                }
                break;

            case "type-available":
                if (fc.Arguments.Count == 1)
                {
                    var typeName = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    return EvaluateTypeAvailable(typeName, context);
                }
                break;

            case "available-environment-variables":
                if (fc.Arguments.Count == 0)
                {
                    var envVars = Environment.GetEnvironmentVariables();
                    var names = new List<object?>();
                    foreach (string key in envVars.Keys)
                        names.Add(key);
                    return names;
                }
                break;

            case "environment-variable":
                if (fc.Arguments.Count == 1)
                {
                    var envName = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    return Environment.GetEnvironmentVariable(envName) ?? null;
                }
                break;

            case "contains":
                if (fc.Arguments.Count == 2)
                {
                    var str = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    var sub = EvaluateStaticExpression(fc.Arguments[1], context)?.ToString() ?? "";
                    return str.Contains(sub, StringComparison.Ordinal);
                }
                break;

            case "starts-with":
                if (fc.Arguments.Count == 2)
                {
                    var str = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
                    var prefix = EvaluateStaticExpression(fc.Arguments[1], context)?.ToString() ?? "";
                    return str.StartsWith(prefix, StringComparison.Ordinal);
                }
                break;

            case "upper-case":
                if (fc.Arguments.Count == 1)
                    return EvaluateStaticExpression(fc.Arguments[0], context)?.ToString()?.ToUpperInvariant() ?? "";
                break;

            case "lower-case":
                if (fc.Arguments.Count == 1)
                {
                    var lcVal = EvaluateStaticExpression(fc.Arguments[0], context)?.ToString() ?? "";
#pragma warning disable CA1308 // lower-case() XPath function requires ToLowerInvariant
                    return lcVal.ToLowerInvariant();
#pragma warning restore CA1308
                }
                break;

            case "static-base-uri":
                if (fc.Arguments.Count == 0)
                {
                    var baseUri = ResolveEffectiveBaseUri(context);
                    return baseUri?.ToString();
                }
                break;
        }

        // Eagerly evaluate arguments to detect forward references (XPST0008) even for unsupported functions
        foreach (var arg in fc.Arguments)
            EvaluateStaticExpression(arg, context);

        // Functions with a non-standard namespace (user-defined functions) are not available in use-when
        if (fc.Name.Namespace != NamespaceId.None)
            throw new XsltException($"XPST0017: Function {fc.Name.Prefix}:{localName} is not available in the static use-when context");
        // Known XPath functions not available in use-when: doc, doc-available, collection, etc.
        if (localName is "doc" or "doc-available" or "collection" or "uri-collection" or "unparsed-text"
            or "unparsed-text-lines" or "unparsed-text-available")
            throw new XsltException($"FODC0002: Function {localName}() is not available in the static use-when context");
        throw new InvalidOperationException($"Cannot statically evaluate function: {localName}/{fc.Arguments.Count}");
    }


    private static string EvaluateSystemProperty(string name, XElement context)
    {
        // Resolve prefix
        var localName = name;
        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var braceClose = name.IndexOf('}', StringComparison.Ordinal);
            if (braceClose > 1)
            {
                var ns = name[2..braceClose];
                localName = name[(braceClose + 1)..];
                if (ns != "http://www.w3.org/1999/XSL/Transform")
                    return "";
            }
        }
        else if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            var prefix = parts[0];
            localName = parts[1];
            // Verify prefix maps to XSLT namespace
            var ns = context.GetNamespaceOfPrefix(prefix);
            if (ns?.NamespaceName != "http://www.w3.org/1999/XSL/Transform")
                return "";
        }

        return localName switch
        {
            "version" => "3.0",
            "vendor" => "PhoenixmlDb",
            "vendor-url" => "https://endpointsystems.com",
            "product-name" => "PhoenixmlDb XSLT",
            "product-version" => "1.0",
            "is-schema-aware" => "no",
            "supports-serialization" => "yes",
            "supports-backwards-compatibility" => "yes",
            "supports-namespace-axis" => "yes",
            "supports-streaming" => "yes",
            "supports-dynamic-evaluation" => "yes",
            "supports-higher-order-functions" => "yes",
            "xpath-version" => "4.0",
            "xsd-version" => "1.1",
            _ => ""
        };
    }


    private static bool EvaluateElementAvailable(string name, XElement context)
    {
        var localName = name;
        string? namespaceUri = null;

        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var braceClose = name.IndexOf('}', StringComparison.Ordinal);
            if (braceClose > 1)
            {
                namespaceUri = name[2..braceClose];
                localName = name[(braceClose + 1)..];
            }
        }
        else if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            var prefix = parts[0];
            localName = parts[1];
            namespaceUri = context.GetNamespaceOfPrefix(prefix)?.NamespaceName;
        }

        if (namespaceUri != null && namespaceUri != "http://www.w3.org/1999/XSL/Transform")
            return false;

        return localName is
            "apply-templates" or "call-template" or "choose" or "copy" or "copy-of" or
            "element" or "attribute" or "text" or "value-of" or "variable" or "param" or
            "if" or "for-each" or "for-each-group" or "sort" or "message" or "number" or
            "comment" or "processing-instruction" or "sequence" or "iterate" or
            "try" or "catch" or "next-match" or "apply-imports" or "result-document" or
            "analyze-string" or "matching-substring" or "non-matching-substring" or
            "where-populated" or "on-empty" or "on-non-empty" or "fallback" or
            "namespace" or "output" or "strip-space" or "preserve-space" or
            "stylesheet" or "transform" or "template" or "function" or
            "import" or "include" or "import-schema" or "decimal-format" or
            "character-map" or "output-character" or "key" or
            "document" or "source-document" or "with-param" or
            "when" or "otherwise" or "break" or "next-iteration" or
            "accumulator" or "accumulator-rule" or
            "context-item" or "global-context-item" or
            "map" or "map-entry" or "array" or "assert" or
            "merge" or "merge-source" or "merge-action" or "merge-key" or
            "fork" or "accept" or "expose" or "override" or "use-package" or
            "attribute-set" or "perform-sort";
    }


    private static bool EvaluateFunctionAvailable(string name, XElement context, int arity)
    {
        // XTDE1400: Validate name is a valid lexical EQName
        if (string.IsNullOrEmpty(name))
            throw new XsltException("XTDE1400: The argument to function-available() is a zero-length string");

        // Resolve the function name
        QName qname;
        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var braceClose = name.IndexOf('}', StringComparison.Ordinal);
            if (braceClose > 1)
            {
                var ns = name[2..braceClose];
                var localName = name[(braceClose + 1)..];
                qname = new QName(NamespaceId.None, localName) { ExpandedNamespace = ns };
            }
            else
            {
                qname = new QName(NamespaceId.None, name);
            }
        }
        else if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            // XTDE1400: Validate both prefix and local name are valid NCNames
            try
            {
                System.Xml.XmlConvert.VerifyNCName(parts[0]);
                System.Xml.XmlConvert.VerifyNCName(parts[1]);
            }
            catch (System.Xml.XmlException)
            {
                throw new XsltException($"XTDE1400: The argument to function-available() ('{name}') is not a valid QName");
            }
            // XTDE1400: Check prefix has a namespace binding in scope
            var prefix = parts[0];
            var nsObj = context.GetNamespaceOfPrefix(prefix);
            if (nsObj == null || string.IsNullOrEmpty(nsObj.NamespaceName))
                throw new XsltException($"XTDE1400: The prefix '{prefix}' in the argument to function-available() has no namespace binding");
            qname = new QName(NamespaceId.None, parts[1], parts[0]) { ExpandedNamespace = nsObj.NamespaceName };
        }
        else
        {
            // XTDE1400: Validate unprefixed name is a valid NCName
            try
            {
                System.Xml.XmlConvert.VerifyNCName(name);
            }
            catch (System.Xml.XmlException)
            {
                throw new XsltException($"XTDE1400: The argument to function-available() ('{name}') is not a valid QName");
            }
            qname = new QName(NamespaceId.None, name);
        }

        var lib = PhoenixmlDb.XQuery.Functions.FunctionLibrary.Standard;
        if (arity >= 0)
            return lib.Resolve(qname, arity) != null;

        // Try common arities 0-3
        for (int i = 0; i <= 3; i++)
        {
            if (lib.Resolve(qname, i) != null)
                return true;
        }
        return false;
    }


    private static bool EvaluateTypeAvailable(string name, XElement context)
    {
        // Strip prefix
        var localName = name;
        if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            localName = parts[1];
        }

        // Basic XSD types are always available
        return localName is
            "string" or "boolean" or "decimal" or "float" or "double" or
            "integer" or "long" or "int" or "short" or "byte" or
            "nonNegativeInteger" or "positiveInteger" or "nonPositiveInteger" or "negativeInteger" or
            "unsignedLong" or "unsignedInt" or "unsignedShort" or "unsignedByte" or
            "duration" or "dateTime" or "date" or "time" or
            "yearMonthDuration" or "dayTimeDuration" or
            "gYearMonth" or "gYear" or "gMonthDay" or "gDay" or "gMonth" or
            "hexBinary" or "base64Binary" or
            "anyURI" or "QName" or "NOTATION" or "normalizedString" or "token" or
            "language" or "NMTOKEN" or "Name" or "NCName" or "ID" or "IDREF" or "ENTITY" or
            "untypedAtomic" or "anyAtomicType" or "anySimpleType" or "anyType" or
            "numeric";
    }

}
