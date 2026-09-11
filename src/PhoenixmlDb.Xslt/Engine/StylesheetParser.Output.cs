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

    private XsltResultDocument ParseResultDocument(XElement element, SourceLocation? location)
    {
        var hrefAttr = element.Attribute("href");
        var formatAttr = element.Attribute("format");
        var validationAttr = element.Attribute("validation");
        var typeAttr = element.Attribute("type");
        var methodAttr = element.Attribute("method");
        var omitXmlDeclAttr = element.Attribute("omit-xml-declaration");
        var encodingAttr = element.Attribute("encoding");
        var indentAttr = element.Attribute("indent");
        var buildTreeAttr = element.Attribute("build-tree");
        var itemSeparatorAttr = element.Attribute("item-separator");
        var htmlVersionAttr = element.Attribute("html-version");
        var mediaTypeAttr = element.Attribute("media-type");
        var includeContentTypeAttr = element.Attribute("include-content-type");
        var byteOrderMarkAttr = element.Attribute("byte-order-mark");
        var escapeUriAttributesAttr = element.Attribute("escape-uri-attributes");
        var useCharMapsAttr = element.Attribute("use-character-maps");
        var allowDupNamesAttr = element.Attribute("allow-duplicate-names");
        var cdataSectionElementsAttr = element.Attribute("cdata-section-elements");

        // XTSE0020: html-version must be a valid decimal number (static check only — AVTs validated at runtime)
        if (htmlVersionAttr != null && !htmlVersionAttr.Value.Contains('{', StringComparison.Ordinal))
        {
            if (!decimal.TryParse(htmlVersionAttr.Value.Trim(), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out _))
                throw new XsltException($"XTSE0020: Invalid html-version value '{htmlVersionAttr.Value}': must be a decimal number", location);
        }

        // XTSE0020: reject invalid literal values for the yes-or-no serialization attributes.
        // Values may be AVTs (e.g. standalone="{$x}"); those are validated at runtime, so
        // skip any value containing a "{".
        foreach (var yn in new[] { "omit-xml-declaration", "indent", "byte-order-mark",
                                   "escape-uri-attributes", "include-content-type",
                                   "undeclare-prefixes", "allow-duplicate-names" })
        {
            var a = element.Attribute(yn);
            if (a != null && !a.Value.Contains('{', StringComparison.Ordinal) && ParseYesNo(a) == null)
                throw new XsltException($"XTSE0020: Invalid value '{a.Value}' for xsl:result-document {yn} attribute (must be yes, no, true, false, 1, or 0)", location);
        }
        var rdStandalone = element.Attribute("standalone");
        if (rdStandalone != null && !rdStandalone.Value.Contains('{', StringComparison.Ordinal))
        {
            var v = rdStandalone.Value.Trim();
            if (v is not ("yes" or "no" or "true" or "false" or "1" or "0" or "omit"))
                throw new XsltException($"XTSE0020: Invalid standalone value '{rdStandalone.Value}' on xsl:result-document: must be yes, no, true, false, 1, 0, or omit", location);
        }
        var rdDoctypePublic = element.Attribute("doctype-public");
        if (rdDoctypePublic != null && !rdDoctypePublic.Value.Contains('{', StringComparison.Ordinal)
            && rdDoctypePublic.Value.Length != 0
            && !IsValidPublicId(rdDoctypePublic.Value))
            throw new XsltException($"XTSE0020: Invalid doctype-public value '{rdDoctypePublic.Value}' on xsl:result-document: not a valid public identifier", location);
        var rdDoctypeSystem = element.Attribute("doctype-system");

        // XTSE1660: Non-schema-aware processor must reject type attribute
        if (typeAttr != null && ShouldRejectSchemaAware)
            throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the type attribute on xsl:result-document", location);
        if (validationAttr != null)
        {
            var v = validationAttr.Value.Trim();
            if (v is "strict" or "type" && ShouldRejectSchemaAware)
                throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept validation=\"{v}\" on xsl:result-document", location);
        }

        var formatAvt = formatAttr != null ? ParseAvt(formatAttr.Value, element, formatAttr) : null;
        // Resolve static format names at parse time (namespace-aware)
        QName? resolvedFormat = null;
        if (formatAttr != null && formatAvt != null
            && formatAvt.Parts.Count == 1 && formatAvt.Parts[0] is AvtLiteral)
        {
            // Not a QName, or a prefix with no binding: XTDE1460 (which may be raised
            // statically), not XTSE0280 (error-1460c).
            try
            {
                resolvedFormat = ParseQName(formatAttr.Value.Trim(), element);
            }
            catch (XsltException ex) when (ex.Message.StartsWith("XTSE0280", StringComparison.Ordinal)
                || ex.Message.StartsWith("XTSE0020", StringComparison.Ordinal))
            {
                throw new XsltException($"XTDE1460: The format attribute of xsl:result-document ('{formatAttr.Value}') is not a valid EQName", location);
            }
        }

        var cdataSectionElementsAvt = cdataSectionElementsAttr != null
            ? ParseAvt(cdataSectionElementsAttr.Value, element, cdataSectionElementsAttr) : null;

        // Collect namespace bindings for runtime resolution of prefixed format names and of the
        // QNames in cdata-section-elements (which are resolved against the result-document element's
        // in-scope namespaces, including the default namespace, per §cdata-section-elements).
        IReadOnlyDictionary<string, string>? nsBindings = null;
        if ((formatAvt != null && resolvedFormat == null) || cdataSectionElementsAvt != null)
        {
            var bindings = new Dictionary<string, string>();
            foreach (var ns in element.AncestorsAndSelf().SelectMany(e => e.Attributes().Where(a => a.IsNamespaceDeclaration)))
            {
                var prefix = ns.Name.LocalName == "xmlns" ? "" : ns.Name.LocalName;
                bindings.TryAdd(prefix, ns.Value);
            }
            nsBindings = bindings;
        }

        var useCharMaps = new List<QName>();
        if (useCharMapsAttr != null)
        {
            foreach (var n in useCharMapsAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                useCharMaps.Add(ParseQName(n, element));
        }

        // XSLT 3.0 §27.1: parameter-document is an attribute value template on xsl:result-document
        // (its URI may reference variables — result-document-1406 uses "{$o}-params.xml"), so it is
        // resolved and loaded at runtime, not here. The base URI of the declaring module is captured
        // so the runtime loader can resolve a relative href against the right module.
        var paramDocAttr = element.Attribute("parameter-document");
        var paramDocAvt = paramDocAttr != null ? ParseAvt(paramDocAttr.Value, element, paramDocAttr) : null;

        return new XsltResultDocument
        {
            Location = location,
            Href = hrefAttr != null ? ParseAvt(hrefAttr.Value, element, hrefAttr) : null,
            Format = formatAvt,
            ResolvedFormatName = resolvedFormat,
            Validation = ParseValidationMode(validationAttr),
            // Type: rejected by XTSE1660 for non-schema-aware processors
            Method = methodAttr != null ? ParseAvt(methodAttr.Value, element, methodAttr) : null,
            OmitXmlDeclaration = omitXmlDeclAttr != null ? ParseAvt(omitXmlDeclAttr.Value, element, omitXmlDeclAttr) : null,
            Encoding = encodingAttr != null ? ParseAvt(encodingAttr.Value, element, encodingAttr) : null,
            Standalone = rdStandalone != null ? ParseAvt(rdStandalone.Value, element, rdStandalone) : null,
            OutputVersion = element.Attribute("output-version") is { } ov ? ParseAvt(ov.Value, element, ov) : null,
            Indent = indentAttr != null ? ParseAvt(indentAttr.Value, element, indentAttr) : null,
            HtmlVersion = htmlVersionAttr != null ? ParseAvt(htmlVersionAttr.Value, element, htmlVersionAttr) : null,
            DoctypePublic = rdDoctypePublic != null ? ParseAvt(rdDoctypePublic.Value, element, rdDoctypePublic) : null,
            DoctypeSystem = rdDoctypeSystem != null ? ParseAvt(rdDoctypeSystem.Value, element, rdDoctypeSystem) : null,
            MediaType = mediaTypeAttr != null ? ParseAvt(mediaTypeAttr.Value, element, mediaTypeAttr) : null,
            IncludeContentType = includeContentTypeAttr != null ? ParseAvt(includeContentTypeAttr.Value, element, includeContentTypeAttr) : null,
            ByteOrderMark = byteOrderMarkAttr != null ? ParseAvt(byteOrderMarkAttr.Value, element, byteOrderMarkAttr) : null,
            EscapeUriAttributes = escapeUriAttributesAttr != null ? ParseAvt(escapeUriAttributesAttr.Value, element, escapeUriAttributesAttr) : null,
            CdataSectionElements = cdataSectionElementsAvt,
            BuildTree = ParseYesNo(buildTreeAttr),
            ItemSeparator = itemSeparatorAttr != null ? ParseAvt(itemSeparatorAttr.Value, element, itemSeparatorAttr) : null,
            AllowDuplicateNames = allowDupNamesAttr != null ? ParseAvt(allowDupNamesAttr.Value, element, allowDupNamesAttr) : null,
            UseCharacterMaps = useCharMaps,
            ParameterDocument = paramDocAvt,
            NamespaceBindings = nsBindings,
            Content = ParseSequenceConstructor(element)
        };
    }


    private XsltSourceDocument ParseSourceDocument(XElement element, SourceLocation? location, bool forceStreamable = false)
    {
        var hrefAttr = element.Attribute("href");
        var streamableAttr = element.Attribute("streamable");
        var validationAttr = element.Attribute("validation");
        var useAccumulatorsAttr = element.Attribute("use-accumulators");

        if (hrefAttr == null)
            throw new XsltException("xsl:source-document requires href attribute", location);

        var useAccumulators = new List<QName>();
        if (useAccumulatorsAttr != null)
        {
            foreach (var name in useAccumulatorsAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (name == "#all")
                    continue; // Will be handled at runtime by using all declared accumulators
                useAccumulators.Add(ParseQName(name, element));
            }
        }

        var result = new XsltSourceDocument
        {
            Location = location,
            Href = ParseAvt(hrefAttr.Value, element, hrefAttr),
            Streamable = forceStreamable || streamableAttr?.Value == "yes",
            Validation = ParseValidationMode(validationAttr) ?? Ast.ValidationMode.Strip,
            Content = element.Nodes().Any() ? ParseSequenceConstructor(element) : null,
            BaseUri = ResolveEffectiveBaseUri(element),
            UseAccumulators = useAccumulators
        };

        // XTSE3430: Check streamability of the body when streamable="yes".
        // Defer the error to runtime so shared stylesheets with multiple templates
        // can compile even if some templates have non-streamable source-document bodies.
        if (result.Streamable)
        {
            try
            {
                StreamabilityChecker.CheckSourceDocumentBody(result.Content, location, _currentStylesheet?.AttributeSets, _currentStylesheet?.Functions);
            }
            catch (XsltException ex)
            {
                result = new XsltSourceDocument
                {
                    Location = result.Location,
                    Href = result.Href,
                    Streamable = result.Streamable,
                    Validation = result.Validation,
                    Content = result.Content,
                    BaseUri = result.BaseUri,
                    UseAccumulators = result.UseAccumulators,
                    StreamabilityError = ex.Message
                };
            }
        }

        return result;
    }

}
