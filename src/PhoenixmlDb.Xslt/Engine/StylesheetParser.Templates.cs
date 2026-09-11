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

    private XsltApplyTemplates ParseApplyTemplates(XElement element, SourceLocation? location)
    {
        // XTSE0090: Validate no unknown attributes
        ValidateAllowedAttributes(element, location, "select", "mode");

        var selectAttr = element.Attribute("select");
        var modeAttr = element.Attribute("mode");

        // XTSE0020: Validate mode is a valid QName (no AVTs)
        var modeValue = modeAttr?.Value.Trim();
        if (modeValue != null && modeValue is not ("#default" or "#current" or "#unnamed"))
            ValidateQNameValue(modeValue, "mode", location);

        var sorts = new List<XsltSort>();
        var withParams = new List<XsltWithParam>();

        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child)) continue;
            if (child.Name == XsltNs + "sort")
                sorts.Add(ParseSort(child));
            else if (child.Name == XsltNs + "with-param")
            {
                var wp = ParseWithParam(child);
                if (withParams.Any(p => p.Name.Equals(wp.Name)))
                    throw new XsltException($"XTSE0670: Duplicate xsl:with-param '{wp.Name.LocalName}' in xsl:apply-templates",
                        GetSourceLocation(child));
                withParams.Add(wp);
            }
            else
                throw new XsltException($"XTSE0010: Only xsl:sort and xsl:with-param are allowed as children of xsl:apply-templates, found {child.Name.LocalName}",
                    GetSourceLocation(child));
        }

        QName? mode = _currentDefaultMode; // Start with the effective default mode
        bool useCurrentMode = false;
        if (modeValue != null)
        {
            if (modeValue == "#default")
                mode = _currentDefaultMode; // Explicitly use the effective default mode
            else if (modeValue == "#current")
                useCurrentMode = true;
            else if (modeValue == "#unnamed")
                mode = null; // Explicit unnamed mode
            else
                mode = ParseQName(modeValue, element);
        }

        // Track mode references for XTSE3085 (declared-modes) validation
        if (_usedModeReferences != null && !useCurrentMode)
        {
            var refMode = mode ?? TemplateIndex.DefaultModeSentinel;
            _usedModeReferences.Add((refMode, location ?? GetSourceLocation(element)));
        }

        return new XsltApplyTemplates
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Mode = mode,
            UseCurrentMode = useCurrentMode,
            Sorts = sorts,
            WithParams = withParams
        };
    }


    private XsltCallTemplate ParseCallTemplate(XElement element, SourceLocation? location)
    {
        // XTSE0090: Validate no unknown attributes
        ValidateAllowedAttributes(element, location, "name");

        // XTSE0010: name is REQUIRED on xsl:call-template — there is nothing to call without it.
        // Was dereferenced with `!`, giving a NullReferenceException (error-0010ad).
        var nameValue = element.Attribute("name")?.Value
            ?? throw new XsltException("XTSE0010: xsl:call-template requires a 'name' attribute", location);
        // name="x/y" is not a QName: XTSE0020, not "template not found" (error-0020f).
        ValidateQNameValue(nameValue, "name", location);
        var name = ParseQName(nameValue, element);

        var withParams = new List<XsltWithParam>();
        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child)) continue;
            if (child.Name == XsltNs + "with-param")
            {
                var wp = ParseWithParam(child);
                if (withParams.Any(p => p.Name.Equals(wp.Name)))
                    throw new XsltException($"XTSE0670: Duplicate xsl:with-param '{wp.Name.LocalName}' in xsl:call-template",
                        GetSourceLocation(child));
                withParams.Add(wp);
            }
            else
                throw new XsltException($"XTSE0010: Only xsl:with-param is allowed as a child of xsl:call-template, found {child.Name.LocalName}",
                    GetSourceLocation(child));
        }

        return new XsltCallTemplate
        {
            Location = location,
            Name = name,
            WithParams = withParams
        };
    }


    private XsltApplyImports ParseApplyImports(XElement element, SourceLocation? location)
    {
        // XTSE0090: Validate no unknown attributes (apply-imports has no element-specific attributes)
        ValidateAllowedAttributes(element, location);

        var withParams = new List<XsltWithParam>();
        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child)) continue;
            if (child.Name == XsltNs + "with-param")
            {
                var wp = ParseWithParam(child);
                if (withParams.Any(p => p.Name.Equals(wp.Name)))
                    throw new XsltException($"XTSE0670: Duplicate xsl:with-param '{wp.Name.LocalName}' in xsl:apply-imports",
                        GetSourceLocation(child));
                withParams.Add(wp);
            }
            else
                throw new XsltException($"XTSE0010: Only xsl:with-param is allowed as a child of xsl:apply-imports, found {child.Name.LocalName}",
                    GetSourceLocation(child));
        }

        return new XsltApplyImports
        {
            Location = location,
            WithParams = withParams
        };
    }


    private XsltNextMatch ParseNextMatch(XElement element, SourceLocation? location)
    {
        var withParams = new List<XsltWithParam>();
        XsltSequenceConstructor? fallback = null;

        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child)) continue;
            if (child.Name == XsltNs + "with-param")
                withParams.Add(ParseWithParam(child));
            else if (child.Name == XsltNs + "fallback")
                fallback = ParseSequenceConstructor(child);
        }

        return new XsltNextMatch
        {
            Location = location,
            WithParams = withParams,
            Fallback = fallback
        };
    }


    /// <summary>
    /// Parses a priority value, validating it is valid xs:decimal (no scientific notation).
    /// </summary>
    private static double ParsePriorityValue(string value, SourceLocation? location)
    {
        ValidateDecimalValue(value, "XTSE0530", "priority", location);
        return double.Parse(value.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }

}
