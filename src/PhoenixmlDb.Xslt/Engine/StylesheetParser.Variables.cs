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

    private void PopulateExternalStaticParams(Dictionary<string, string>? externalStaticParams)
    {
        _externalStaticParams = externalStaticParams;
        if (externalStaticParams == null) return;
        foreach (var (name, value) in externalStaticParams)
        {
            var qname = new QName(NamespaceId.None, name);
            var val = value.Trim();
            // CLI-friendly value parsing. Order matters: literal-quoted strings first so a
            // value like "'true'" stays a string. Then XPath-shaped literals (true()/false()/()),
            // then bare booleans (the typical command-line spelling), then numerics. Anything
            // else falls through as an xs:untypedAtomic-like raw string — the static-param
            // consumer (use-when, shadow attrs) coerces via boolean()/number() at use time.
            if ((val.StartsWith('\'') && val.EndsWith('\'')) || (val.StartsWith('"') && val.EndsWith('"')))
                _staticVariables[qname] = val[1..^1];
            else if (val is "true()" or "false()")
                _staticVariables[qname] = val == "true()" ? (object)true : false;
            else if (val == "()")
                _staticVariables[qname] = null;
            else if (val.Equals("true", StringComparison.Ordinal) || val.Equals("false", StringComparison.Ordinal))
                _staticVariables[qname] = val == "true";
            else if (long.TryParse(val, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l))
                _staticVariables[qname] = l;
            else if (double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                _staticVariables[qname] = d;
            else
                _staticVariables[qname] = val;
            _externalStaticParamNames.Add(name);
        }
    }


    private XsltVariable ParseVariable(XElement element)
    {
        var prevContext = _nsContext;
        _nsContext = element;
        var nameAttr = element.Attribute("name");
        if (nameAttr == null)
            throw new XsltException("XTSE0010: xsl:variable must have a name attribute", GetSourceLocation(element));
        // XTSE0020: Validate name is a valid QName
        ValidateQNameValue(nameAttr.Value, "name", GetSourceLocation(element));
        var name = ParseQName(nameAttr.Value, element);
        var asAttr = element.Attribute("as");
        var selectAttr = element.Attribute("select");
        var staticAttr = element.Attribute("static");
        var location = GetSourceLocation(element);

        // XTSE0090: Validate no unknown attributes
        ValidateAllowedAttributes(element, location, "name", "select", "as", "static", "visibility");

        // Validate static attribute value
        if (staticAttr != null)
        {
            var staticVal = staticAttr.Value.Trim();
            if (staticVal is not ("yes" or "no" or "true" or "false" or "1" or "0"))
                throw new XsltException($"XTSE0020: Invalid value '{staticAttr.Value}' for 'static' attribute", location);
        }
        var isStatic = staticAttr != null && staticAttr.Value.Trim() is "yes" or "true" or "1";

        // XTSE0010: static variable must not have content body (must use select)
        // Evaluate use-when on child elements first — excluded elements don't count as content
        if (isStatic && element.Nodes().Any(n => (n is XElement el && ShouldIncludeElement(el)) || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0010: A variable with static='yes' must not have content (use the 'select' attribute instead)", location);

        // XTSE0090: visibility is not allowed on static variables
        if (isStatic && element.Attribute("visibility") != null)
            throw new XsltException("XTSE0090: The 'visibility' attribute is not allowed on a variable with static='yes'", location);

        // XTSE0620: select attribute and non-empty content are mutually exclusive
        // Evaluate use-when on child elements first — excluded elements don't count as content
        if (selectAttr != null && element.Nodes().Any(n => (n is XElement el && ShouldIncludeElement(el)) || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0620: An xsl:variable element must not have both a select attribute and non-empty content", location);

        _nsContext = prevContext;
        var selectExpr = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null;

        // XPST0008: A global variable must not reference itself in its own select expression
        // (XSLT 3.0 §9.9.2: the scope of a global variable excludes its own definition)
        var isGlobal = element.Parent?.Name.Namespace == XsltNs
                       && element.Parent.Name.LocalName is "stylesheet" or "transform" or "package";
        if (isGlobal && selectExpr != null && ContainsVariableReference(selectExpr, name))
            throw new XsltException($"XPST0008: Variable ${name.LocalName} references itself in its own definition", location);

        return new XsltVariable
        {
            Name = name,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            Select = selectExpr,
            Content = ParseContentBody(element, selectAttr),
            Static = isStatic,
            Visibility = ParseVisibility(element.Attribute("visibility")?.Value),
            VisibilityAttr = element.Attribute("visibility")?.Value,
            IsAbstract = element.Attribute("visibility")?.Value == "abstract",
            BaseUri = ResolveEffectiveBaseUri(element),
            Version = element.Attribute("version")?.Value
        };
    }


    private XsltParam ParseParam(XElement element, bool isGlobal = false, bool allowTunnel = true)
    {
        var name = ParseQName(element.Attribute("name")!.Value, element);
        var asAttr = element.Attribute("as");
        var selectAttr = element.Attribute("select");
        var requiredAttr = element.Attribute("required");
        var tunnelAttr = element.Attribute("tunnel");
        var staticAttr = element.Attribute("static");
        var location = GetSourceLocation(element);

        // XTSE0090: Validate no unknown attributes
        ValidateAllowedAttributes(element, location, "name", "select", "as", "required", "tunnel", "static");

        // Validate static attribute value
        if (staticAttr != null)
        {
            var staticVal = staticAttr.Value.Trim();
            if (staticVal is not ("yes" or "no" or "true" or "false" or "1" or "0"))
                throw new XsltException($"XTSE0020: Invalid value '{staticAttr.Value}' for 'static' attribute", location);
        }
        var isStatic = staticAttr != null && staticAttr.Value.Trim() is "yes" or "true" or "1";

        // XTSE0020: static is only allowed on global params
        if (isStatic && !isGlobal)
            throw new XsltException("XTSE0020: The 'static' attribute is not allowed on a non-global xsl:param", location);

        // XTSE0010: static param must not have content body (must use select)
        // Evaluate use-when on child elements first — excluded elements don't count as content
        if (isStatic && element.Nodes().Any(n => (n is XElement el && ShouldIncludeElement(el)) || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0010: A parameter with static='yes' must not have content (use the 'select' attribute instead)", location);

        // XTSE0020: tunnel is not allowed on static params
        if (isStatic && tunnelAttr != null && tunnelAttr.Value.Trim() is "yes" or "true" or "1")
            throw new XsltException("XTSE0020: The 'tunnel' attribute is not allowed on a parameter with static='yes'", location);

        // XTSE0020: tunnel="yes" is only allowed on template params (not global or function params)
        if (!allowTunnel && tunnelAttr != null && tunnelAttr.Value.Trim() is "yes" or "true" or "1")
            throw new XsltException("XTSE0020: The 'tunnel' attribute with value 'yes' is not allowed on xsl:param in this context", location);

        // XTSE0020: Validate required attribute value
        if (requiredAttr != null)
        {
            var val = requiredAttr.Value.Trim();
            if (val != "yes" && val != "no" && val != "true" && val != "false" && val != "1" && val != "0")
                throw new XsltException($"XTSE0020: Invalid value '{requiredAttr.Value}' for required attribute: must be 'yes', 'no', 'true', 'false', '1', or '0'", location);
        }

        var isRequired = requiredAttr != null && (requiredAttr.Value.Trim() is "yes" or "true" or "1");

        // XTSE0010: required param must not have a default value (select or content)
        if (isRequired && selectAttr != null)
            throw new XsltException("XTSE0010: A parameter with required='yes' must not have a select attribute", location);
        if (isRequired && element.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0010: A parameter with required='yes' must not have content", location);

        // XTSE0620: select attribute and non-empty content are mutually exclusive
        if (selectAttr != null && element.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0620: An xsl:param element must not have both a select attribute and non-empty content", location);

        return new XsltParam
        {
            Name = name,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = ParseContentBody(element, selectAttr),
            Required = isRequired,
            Tunnel = ParseYesNoBoolean(tunnelAttr, "tunnel", location),
            Static = isStatic,
            BaseUri = ResolveEffectiveBaseUri(element),
            Version = element.Attribute("version")?.Value
        };
    }


    private XsltVariableInstruction ParseVariableInstr(XElement element, SourceLocation? location)
    {
        // XTSE0010: name is REQUIRED on xsl:variable. Was dereferenced with `!`, giving a
        // NullReferenceException rather than a diagnosis (error-0010an).
        var name = ParseQName(
            element.Attribute("name")?.Value
                ?? throw new XsltException("XTSE0010: xsl:variable requires a 'name' attribute",
                    location),
            element);
        var asAttr = element.Attribute("as");
        var selectAttr = element.Attribute("select");

        // XTSE0620: select attribute and non-empty content are mutually exclusive
        if (selectAttr != null && element.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0620: An xsl:variable element must not have both a select attribute and non-empty content", location);

        return new XsltVariableInstruction
        {
            Location = location,
            Name = name,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = ParseContentBody(element, selectAttr),
            BaseUri = ResolveEffectiveBaseUri(element)
        };
    }


    private XsltParamInstruction ParseParamInstr(XElement element, SourceLocation? location)
    {
        var name = ParseQName(element.Attribute("name")!.Value, element);
        var asAttr = element.Attribute("as");
        var selectAttr = element.Attribute("select");
        var requiredAttr = element.Attribute("required");
        var tunnelAttr = element.Attribute("tunnel");

        return new XsltParamInstruction
        {
            Location = location,
            Name = name,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = ParseContentBody(element, selectAttr),
            Required = requiredAttr?.Value == "yes",
            Tunnel = ParseYesNoBoolean(tunnelAttr, "tunnel", location)
        };
    }


    private XsltWithParam ParseWithParam(XElement element)
    {
        var name = ParseQName(element.Attribute("name")!.Value, element);
        var asAttr = element.Attribute("as");
        var selectAttr = element.Attribute("select");
        var tunnelAttr = element.Attribute("tunnel");

        // XTSE0090: required attribute is not permitted on xsl:with-param
        if (element.Attribute("required") != null)
            throw new XsltException("XTSE0090: The required attribute is not permitted on xsl:with-param",
                GetSourceLocation(element));

        // XTSE0620: select attribute and non-empty content are mutually exclusive
        if (selectAttr != null && element.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0620: An xsl:with-param element must not have both a select attribute and non-empty content",
                GetSourceLocation(element));

        // Use the with-param element as namespace context for select expressions,
        // since it may have local namespace declarations (e.g., xmlns:S="...")
        var prevContext = _nsContext;
        _nsContext = element;
        try
        {
            return new XsltWithParam
            {
                Name = name,
                As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
                Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
                Content = selectAttr == null && element.Nodes().Any()
                    ? ParseSequenceConstructor(element)
                    : null,
                Tunnel = ParseYesNoBoolean(tunnelAttr, "tunnel", GetSourceLocation(element))
            };
        }
        finally
        {
            _nsContext = prevContext;
        }
    }


    /// <summary>
    /// Checks if a variable name is declared in an outer scope (preceding siblings, ancestor scopes, or globals).
    /// Used to determine if a try-body variable shadows an outer variable visible from catch clauses.
    /// </summary>
    private static bool IsVariableDeclaredInOuterScope(XElement tryElement, string varName)
    {
        // Walk up from the try element checking preceding siblings at each level
        var current = tryElement;
        while (current.Parent != null)
        {
            foreach (var sibling in current.ElementsBeforeSelf())
            {
                if ((sibling.Name == XsltNs + "variable" || sibling.Name == XsltNs + "param")
                    && sibling.Attribute("name")?.Value == varName)
                    return true;
            }
            // Check template/function params
            if (current.Parent.Name == XsltNs + "template" || current.Parent.Name == XsltNs + "function")
            {
                foreach (var param in current.Parent.Elements(XsltNs + "param"))
                {
                    if (param.Attribute("name")?.Value == varName)
                        return true;
                }
            }
            // Check global scope
            if (current.Parent.Name == XsltNs + "stylesheet" || current.Parent.Name == XsltNs + "transform")
            {
                foreach (var child in current.Parent.Elements())
                {
                    if ((child.Name == XsltNs + "variable" || child.Name == XsltNs + "param")
                        && child.Attribute("name")?.Value == varName)
                        return true;
                }
                break;
            }
            current = current.Parent;
        }
        return false;
    }

}
