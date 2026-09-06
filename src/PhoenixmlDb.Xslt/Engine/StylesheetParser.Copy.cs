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

    // Clone helpers — XsltTemplate/Function/Variable use init properties, so we create new instances
    private static Ast.XsltTemplate CloneTemplateWithVisibility(Ast.XsltTemplate t, Ast.Visibility v) => new()
    {
        Name = t.Name, Match = t.Match, Priority = t.Priority, Modes = t.Modes,
        As = t.As, Parameters = t.Parameters, Body = t.Body, Visibility = v,
        VisibilityAttr = VisibilityToAttr(v),
        UnionGroupId = t.UnionGroupId, Version = t.Version, BaseUri = t.BaseUri,
        DefaultCollation = t.DefaultCollation, ContextItemUse = t.ContextItemUse,
        ContextItemAs = t.ContextItemAs, OriginalTemplate = t.OriginalTemplate
    };


    private static Ast.XsltFunction CloneFunctionWithVisibility(Ast.XsltFunction f, Ast.Visibility v) => new()
    {
        Name = f.Name, As = f.As, Parameters = f.Parameters, Body = f.Body,
        Override = f.Override, Visibility = v, Cache = f.Cache,
        NewEachTime = f.NewEachTime, Streamability = f.Streamability,
        OriginalFunction = f.OriginalFunction
    };


    private static Ast.XsltVariable CloneVariableWithVisibility(Ast.XsltVariable v, Ast.Visibility vis) => new()
    {
        Name = v.Name, As = v.As, Select = v.Select, Content = v.Content,
        Static = v.Static, Visibility = vis, BaseUri = v.BaseUri, Version = v.Version,
        IsAbstract = v.IsAbstract || v.Visibility == Ast.Visibility.Abstract,
        ProvidedByPackage = v.ProvidedByPackage,
        OriginalVariable = v.OriginalVariable, PackageStylesheet = v.PackageStylesheet
    };


    private static Ast.XsltAttributeSet CloneAttributeSetWithVisibility(Ast.XsltAttributeSet a, Ast.Visibility v) => new()
    {
        Name = a.Name, UseAttributeSets = a.UseAttributeSets, Attributes = a.Attributes,
        Visibility = v, Streamable = a.Streamable, BaseUri = a.BaseUri, Parts = a.Parts,
        OriginalAttributeSet = a.OriginalAttributeSet,
        IsAbstract = a.IsAbstract || a.Visibility == Ast.Visibility.Abstract,
        PackageStylesheet = a.PackageStylesheet, ProvidedByPackage = a.ProvidedByPackage
    };


    private XsltCopy ParseCopy(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select");
        var copyNamespacesAttr = element.Attribute("copy-namespaces");
        var inheritNamespacesAttr = element.Attribute("inherit-namespaces");
        var useAttributeSetsAttr = element.Attribute("use-attribute-sets");
        var validationAttr = element.Attribute("validation");
        var typeAttr = element.Attribute("type");

        // XTSE1660: Non-schema-aware processor must reject type attribute
        if (typeAttr != null)
        {
            if (ShouldRejectSchemaAware)
                throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the type attribute on xsl:copy", location);
            // Even with import-schema, we can't resolve schema types at runtime
            throw new XsltException($"XTTE1535: Schema type '{typeAttr.Value}' cannot be resolved on xsl:copy (schema validation not supported)", location);
        }
        if (validationAttr != null)
        {
            var v = validationAttr.Value.Trim();
            if (v is "strict" or "type" && ShouldRejectSchemaAware)
                throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept validation=\"{v}\" on xsl:copy", location);
        }

        // XTSE0020: copy-namespaces must be a valid yes/no value
        if (copyNamespacesAttr != null && !copyNamespacesAttr.Value.Contains('{', StringComparison.Ordinal))
        {
            var val = copyNamespacesAttr.Value.Trim();
            if (val != "yes" && val != "no" && val != "true" && val != "false" && val != "1" && val != "0")
                throw new XsltException($"XTSE0020: Invalid value '{copyNamespacesAttr.Value}' for copy-namespaces attribute", location);
        }

        var useAttributeSets = new List<QName>();
        if (useAttributeSetsAttr != null)
        {
            foreach (var n in useAttributeSetsAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                useAttributeSets.Add(ParseQName(n, element));
            }
        }

        return new XsltCopy
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            CopyNamespaces = ParseYesNo(copyNamespacesAttr),
            InheritNamespaces = ParseYesNo(inheritNamespacesAttr),
            UseAttributeSets = useAttributeSets,
            Validation = ParseValidationMode(validationAttr),
            // Type: rejected by XTSE1660 for non-schema-aware processors
            Content = element.Nodes().Any() ? ParseSequenceConstructor(element) : null
        };
    }


    private XsltCopyOf ParseCopyOf(XElement element, SourceLocation? location)
    {
        // XTSE0090: Reject unknown attributes on xsl:copy-of
        foreach (var attr in element.Attributes())
        {
            if (attr.IsNamespaceDeclaration || attr.Name.Namespace != XNamespace.None) continue;
            var localName = attr.Name.LocalName;
            // Skip shadow attributes (underscore-prefixed) — they are XSLT 3.0 compile-time resolved
            if (localName.StartsWith('_')) continue;
            if (localName is not ("select" or "copy-namespaces" or "copy-accumulators" or "validation" or "type"))
                throw new XsltException($"XTSE0090: Attribute '{localName}' is not allowed on xsl:copy-of", location);
        }

        // XTSE0260: xsl:copy-of must not have child content
        if (element.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException("XTSE0260: xsl:copy-of must not have child content", location);

        var selectAttr = element.Attribute("select");
        if (selectAttr == null)
            throw new XsltException("XTSE0010: xsl:copy-of requires a select attribute", location);

        var select = ParseExpr(selectAttr.Value, selectAttr);
        var copyNamespacesAttr = element.Attribute("copy-namespaces");
        var copyAccumulatorsAttr = element.Attribute("copy-accumulators");
        var validationAttr = element.Attribute("validation");
        var typeAttr = element.Attribute("type");

        // XTSE1660: Non-schema-aware processor must reject type attribute
        if (typeAttr != null)
        {
            if (ShouldRejectSchemaAware)
                throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the type attribute on xsl:copy-of", location);
            throw new XsltException($"XTTE1535: Schema type '{typeAttr.Value}' cannot be resolved on xsl:copy-of (schema validation not supported)", location);
        }
        if (validationAttr != null)
        {
            var v = validationAttr.Value.Trim();
            if (v is "strict" or "type" && ShouldRejectSchemaAware)
                throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept validation=\"{v}\" on xsl:copy-of", location);
        }

        // XTSE0020: copy-namespaces must be a valid yes/no value
        if (copyNamespacesAttr != null && !copyNamespacesAttr.Value.Contains('{', StringComparison.Ordinal))
        {
            var val = copyNamespacesAttr.Value.Trim();
            if (val != "yes" && val != "no" && val != "true" && val != "false" && val != "1" && val != "0")
                throw new XsltException($"XTSE0020: Invalid value '{copyNamespacesAttr.Value}' for copy-namespaces attribute", location);
        }

        return new XsltCopyOf
        {
            Location = location,
            Select = select,
            CopyNamespaces = ParseYesNo(copyNamespacesAttr),
            CopyAccumulators = ParseYesNo(copyAccumulatorsAttr),
            Validation = ParseValidationMode(validationAttr)
        };
    }

}
