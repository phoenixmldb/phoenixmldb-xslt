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
    /// Parses an XSLT stylesheet from an XElement.
    /// </summary>
    public XsltStylesheet ParseStylesheet(XElement element) => ParseStylesheet(element, isTopLevel: true);


    private XsltStylesheet ParseStylesheet(XElement element, bool isTopLevel)
    {
        ArgumentNullException.ThrowIfNull(element);

        // Accept xsl:stylesheet, xsl:transform, and xsl:package (XSLT 3.0)
        if (element.Name != XsltNs + "stylesheet" && element.Name != XsltNs + "transform" && element.Name != XsltNs + "package")
        {
            // Simplified stylesheet (single template)
            return ParseSimplifiedStylesheet(element);
        }

        // Scope mode element tracking to this stylesheet level (for XTSE0545 conflict detection)
        var prevModeElements = _modeElements;
        _modeElements = new Dictionary<QName, XElement>();

        var versionAttr = element.Attribute("version");
        if (versionAttr == null && !element.Attributes().Any(a => a.Name.LocalName == "_version"))
            throw new XsltException("XTSE0010: The 'version' attribute is required on xsl:stylesheet/xsl:transform", GetSourceLocation(element));
        var version = string.IsNullOrWhiteSpace(versionAttr?.Value) ? "3.0" : versionAttr.Value;

        // XTSE0110: The version attribute must be a valid xs:decimal
        if (versionAttr != null && !string.IsNullOrWhiteSpace(versionAttr.Value))
            ValidateDecimalValue(versionAttr.Value, "XTSE0110", "version", GetSourceLocation(element));

        // XTSE0125: Validate default-collation contains a recognized collation URI
        var defaultCollationAttr = element.Attribute("default-collation");
        if (defaultCollationAttr != null)
            ValidateCollationList(defaultCollationAttr.Value, GetSourceLocation(element));

        // Check for expand-text at stylesheet level
        var expandTextAttr = element.Attribute(XsltNs + "expand-text") ?? element.Attribute("expand-text");
        _defaultExpandText = expandTextAttr?.Value is "yes" or "1" or "true";

        var xpathDefaultNs = element.Attribute("xpath-default-namespace")?.Value;

        // Parse default-mode on the stylesheet root element
        var defaultModeAttr = element.Attribute("default-mode");
        QName? stylesheetDefaultMode = null;
        if (defaultModeAttr != null && defaultModeAttr.Value != "#unnamed")
        {
            _nsContext = element;
            stylesheetDefaultMode = ParseQName(defaultModeAttr.Value, element);
            _nsContext = null;
        }
        _currentDefaultMode = stylesheetDefaultMode;

        // XTSE0090: Validate no unknown attributes on stylesheet/transform/package
        // xsl:package also allows: name, package-version, declared-modes
        ValidateAllowedAttributes(element, GetSourceLocation(element),
            "id", "input-type-annotations", "name", "package-version", "declared-modes");

        // XTSE1660: Non-schema-aware processor must reject default-validation="strict"
        var defaultValidationAttr = element.Attribute("default-validation");
        if (defaultValidationAttr != null && defaultValidationAttr.Value.Trim() is "strict")
            throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept default-validation=\"{defaultValidationAttr.Value.Trim()}\"",
                GetSourceLocation(element));

        // Parse input-type-annotations
        var inputTypeAnnotationsAttr = element.Attribute("input-type-annotations");
        var inputTypeAnnotations = Ast.TypeAnnotations.Unspecified;
        if (inputTypeAnnotationsAttr != null)
        {
            inputTypeAnnotations = inputTypeAnnotationsAttr.Value.Trim() switch
            {
                "strip" => Ast.TypeAnnotations.Strip,
                "preserve" => Ast.TypeAnnotations.Preserve,
                "unspecified" => Ast.TypeAnnotations.Unspecified,
                _ => throw new XsltException($"XTSE0020: Invalid value '{inputTypeAnnotationsAttr.Value}' for input-type-annotations. Must be 'strip', 'preserve', or 'unspecified'",
                    GetSourceLocation(element))
            };
        }

        // Parse declared-modes and detect xsl:package
        var isPackage = element.Name == XsltNs + "package";

        // XTSE0090: package-version is only allowed on xsl:package
        // In forwards-compatibility mode (version > 3.0), unknown attributes are tolerated
        if (!isPackage && element.Attribute("package-version") != null
            && decimal.TryParse(version, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var versionNum)
            && versionNum <= 3.0m)
            throw new XsltException("XTSE0090: The 'package-version' attribute is not allowed on xsl:stylesheet/xsl:transform (only on xsl:package)",
                GetSourceLocation(element));

        if (isPackage && element.Attribute("package-version") is { } packageVersionAttr
            && packageVersionAttr.Annotation<UnevaluatedShadowValue>() is null
            && !PackageVersionSyntax.IsValidVersion(packageVersionAttr.Value.Trim()))
            throw new XsltException(
                $"XTSE0020: package-version '{packageVersionAttr.Value}' is not a valid package version " +
                "(integers separated by dots, optionally followed by '-' and an NCName)",
                GetSourceLocation(element));

        var declaredModesAttr = element.Attribute("declared-modes");
        // For xsl:package, declared-modes defaults to true; for xsl:stylesheet, defaults to false
        var declaredModes = isPackage;
        if (declaredModesAttr != null)
            declaredModes = declaredModesAttr.Value.Trim() is "yes" or "1" or "true";

        var stylesheet = new XsltStylesheet
        {
            Version = version,
            XpathDefaultNamespace = string.IsNullOrEmpty(xpathDefaultNs) ? null : xpathDefaultNs,
            DefaultMode = stylesheetDefaultMode,
            DefaultCollation = ResolveDefaultCollation(defaultCollationAttr?.Value),
            BaseUri = ResolveEffectiveBaseUri(element) ?? _baseUri,
            InputTypeAnnotations = inputTypeAnnotations,
            IsPackage = isPackage,
            DeclaredModes = declaredModes
        };
        _currentStylesheet = stylesheet;

        // Enable mode reference tracking for declared-modes validation
        var prevModeReferences = _usedModeReferences;
        if (declaredModes)
            _usedModeReferences = new List<(QName, SourceLocation?)>();

        // Pre-scan for xsl:import-schema to suppress XTSE1660 when present
        _hasImportSchema = element.Elements(XsltNs + "import-schema").Any();

        // Parse namespace declarations from root and all descendant elements
        // so xs:QName() type constructor can resolve prefixes at runtime
        foreach (var ns in element.Attributes().Where(a => a.IsNamespaceDeclaration))
        {
            var prefix = ns.Name.LocalName == "xmlns" ? "" : ns.Name.LocalName;
            stylesheet.Namespaces[prefix] = ns.Value;
        }
        foreach (var desc in element.Descendants())
        {
            foreach (var ns in desc.Attributes().Where(a => a.IsNamespaceDeclaration))
            {
                var prefix = ns.Name.LocalName == "xmlns" ? "" : ns.Name.LocalName;
                stylesheet.Namespaces.TryAdd(prefix, ns.Value);
            }
        }

        // Parse exclude-result-prefixes
        var excludeAttr = element.Attribute("exclude-result-prefixes");
        if (excludeAttr != null)
        {
            foreach (var prefix in excludeAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (prefix == "#all")
                {
                    // §7.1.2: #all excludes all namespaces in scope on THIS element.
                    // Expand to actual prefixes so LRE-only namespaces are not affected.
                    foreach (var ns in element.Attributes().Where(a => a.IsNamespaceDeclaration))
                    {
                        var p = ns.Name.LocalName == "xmlns" ? "" : ns.Name.LocalName;
                        var u = ns.Value;
                        // Skip XSLT namespace (always excluded anyway) and XML namespace
                        if (u == "http://www.w3.org/1999/XSL/Transform"
                            || u == "http://www.w3.org/XML/1998/namespace")
                            continue;
                        if (string.IsNullOrEmpty(p))
                            stylesheet.ExcludeResultPrefixes.Add("#default");
                        else
                            stylesheet.ExcludeResultPrefixes.Add(p);
                    }
                }
                else if (prefix == "#default")
                {
                    // XTSE0809: #default requires a default namespace binding
                    if (string.IsNullOrEmpty(element.GetDefaultNamespace().NamespaceName))
                        throw new XsltException("XTSE0809: The value '#default' is used in exclude-result-prefixes but the element has no default namespace",
                            GetSourceLocation(element));
                    stylesheet.ExcludeResultPrefixes.Add(prefix);
                }
                else
                {
                    // XTSE0808: prefix must have an in-scope namespace binding
                    var ns = element.GetNamespaceOfPrefix(prefix);
                    if (ns == null)
                        throw new XsltException($"XTSE0808: Namespace prefix '{prefix}' used in exclude-result-prefixes is not declared",
                            GetSourceLocation(element));
                    stylesheet.ExcludeResultPrefixes.Add(prefix);
                }
            }
        }

        // XTSE1430: Validate and store extension-element-prefixes
        var extPrefixesAttr = element.Attribute("extension-element-prefixes");
        if (extPrefixesAttr != null)
        {
            foreach (var prefix in extPrefixesAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (prefix == "#default")
                {
                    stylesheet.ExtensionElementPrefixes.Add(prefix);
                    continue;
                }
                var ns = element.GetNamespaceOfPrefix(prefix);
                if (ns == null)
                    throw new XsltException($"XTSE1430: Namespace prefix '{prefix}' used in extension-element-prefixes is not declared",
                        GetSourceLocation(element));
                // XTSE0800: A reserved namespace must not be used as an extension namespace
                var nsUri = ns.NamespaceName;
                if (nsUri is "http://www.w3.org/1999/XSL/Transform"
                    or "http://www.w3.org/2001/XMLSchema"
                    or "http://www.w3.org/2001/XMLSchema-instance"
                    or "http://www.w3.org/XML/1998/namespace")
                    throw new XsltException($"XTSE0800: The namespace '{nsUri}' is a reserved namespace and must not be used as an extension element namespace",
                        GetSourceLocation(element));
                // Store the namespace URI so we can match regardless of prefix used
                stylesheet.ExtensionElementPrefixes.Add(nsUri);
            }
        }

        // Populate parser-level extension namespace tracking from stylesheet-level declarations
        foreach (var extNs in stylesheet.ExtensionElementPrefixes)
            _extensionNamespaces.Add(extNs);

        // XTSE0120: An xsl:stylesheet element must not have any text node children
        foreach (var node in element.Nodes())
        {
            if (node is XText text && !string.IsNullOrWhiteSpace(text.Value))
                throw new XsltException("XTSE0120: An xsl:stylesheet element must not have any text node children",
                    GetSourceLocation(element));
        }

        // Parse children
        foreach (var child in element.Elements())
        {
            // Evaluate use-when BEFORE any processing (spec §3.8 conditional inclusion)
            if (!ShouldIncludeElement(child))
                continue;

            if (child.Name.Namespace != XsltNs)
            {
                // XTSE0130: Child element of xsl:stylesheet with null namespace URI is an error
                if (child.Name.Namespace == XNamespace.None)
                    throw new XsltException($"XTSE0130: Element '{child.Name.LocalName}' in xsl:stylesheet has no namespace URI",
                        GetSourceLocation(child));
                // Check for XSLT elements inside non-XSLT top-level elements
                if (child.Descendants().Any(d => d.Name.Namespace == XsltNs))
                {
                    throw new XsltException($"XTSE0130: XSLT element found inside non-XSLT element '{child.Name.LocalName}'",
                        GetSourceLocation(child));
                }
                continue;
            }

            // Forwards compatibility: if the effective version for this element is > 3.0,
            // it may contain unknown elements that should be silently ignored.
            // The effective version is the per-element version attribute, or the stylesheet version.
            var childVersion = child.Attribute("version")?.Value ?? version;
            var childVersionNum = ParseVersionNumber(childVersion);

            switch (child.Name.LocalName)
            {
                case "import":
                    {
                        ValidateAllowedAttributes(child, GetSourceLocation(child), "href");
                        ValidateEmptyElement(child);
                        // Save current static variables — importing module has higher precedence
                        var savedStaticVars = new Dictionary<QName, object?>(_staticVariables);
                        var savedInsideImported = _insideImportedModule;
                        _insideImportedModule = true;
                        var imported = LoadExternalStylesheet(child);
                        _insideImportedModule = savedInsideImported;
                        // Track which variables were newly added by the import (lower precedence)
                        // Also track whether each is a variable or param for XTSE3450 consistency check
                        var importedParamNames = imported != null
                            ? new HashSet<QName>(imported.Parameters.Where(p => p.Static).Select(p => p.Name))
                            : new HashSet<QName>();
                        foreach (var svName in _staticVariables.Keys)
                        {
                            if (!savedStaticVars.ContainsKey(svName))
                                _importedStaticVarNames[svName] = !importedParamNames.Contains(svName); // true=variable, false=param
                        }
                        // Restore importing module's static variable values (higher precedence wins)
                        foreach (var (svName, svVal) in savedStaticVars)
                            _staticVariables[svName] = svVal;
                        if (imported != null)
                        {
                            stylesheet.Imports.Add(imported);
                        }
                    }
                    break;

                case "include":
                    {
                        ValidateAllowedAttributes(child, GetSourceLocation(child), "href");
                        ValidateEmptyElement(child);
                        var included = LoadExternalStylesheet(child);
                        if (included != null)
                        {
                            // Include: merge all declarations at the same precedence
                            MergeStylesheet(stylesheet, included);
                        }
                    }
                    break;

                case "template":
                    var template = ParseTemplate(child, version);
                    stylesheet.Templates.Add(template);
                    // XTSE3050: a top-level template rule (outside xsl:override) that targets a
                    // mode declared in a used package adds a rule to a used component, which is
                    // only allowed inside xsl:override (override-m-018). Record explicit modes.
                    if (template.Match != null)
                    {
                        foreach (var m in template.Modes)
                        {
                            if (m.LocalName is "#all" or "#unnamed" or "#default" or "#current" or "")
                                continue;
                            stylesheet.LocalTemplateRuleModes.Add(m);
                        }
                    }
                    if (template.Name.HasValue)
                    {
                        if (stylesheet.NamedTemplates.TryGetValue(template.Name.Value, out var existingTmpl))
                        {
                            // If the existing template was merged from a package, overwrite silently
                            // (consuming stylesheet's own template takes precedence).
                            // If it was defined in THIS module, that's a genuine duplicate → XTSE0660.
                            if (!_packageMergedTemplateNames.Contains(template.Name.Value))
                                throw new XsltException($"XTSE0660: Duplicate named template '{template.Name.Value.LocalName}'",
                                    GetSourceLocation(child));
                        }
                        stylesheet.NamedTemplates[template.Name.Value] = template;
                    }
                    break;

                case "variable":
                    var variable = ParseVariable(child);
                    if (stylesheet.Variables.Any(v => v.Name.Equals(variable.Name)) ||
                        stylesheet.Parameters.Any(p => p.Name.Equals(variable.Name)))
                        throw new XsltException($"XTSE0630: Duplicate global variable '{variable.Name.LocalName}'",
                            GetSourceLocation(child));
                    stylesheet.Variables.Add(variable);
                    stylesheet.LocalComponentSymbols.Add(("V", variable.Name, 0));
                    // Track static variables for use-when and shadow attribute resolution
                    if (variable.Static && variable.Select != null)
                    {
                        var varKey = variable.Name;
                        var newVal = EvaluateStaticSelectSafe(variable.Select, child);
                        // XTSE3450: A later higher-precedence static variable conflicts with
                        // an earlier lower-precedence one from an import
                        if (_importedStaticVarNames.TryGetValue(varKey, out var importedIsVariable))
                        {
                            // Inconsistency: one is xsl:variable and the other is xsl:param
                            if (!importedIsVariable) // imported was a param, this is a variable
                                throw new XsltException($"XTSE3450: Static variable '{varKey.LocalName}' is inconsistent with an imported static parameter of the same name",
                                    GetSourceLocation(child));
                            if (_staticVariables.TryGetValue(varKey, out var importedVal) && !Equals(newVal, importedVal))
                                throw new XsltException($"XTSE3450: Static variable '{varKey.LocalName}' has value '{newVal}' which is inconsistent with the imported value '{importedVal}'",
                                    GetSourceLocation(child));
                        }
                        _staticVariables[varKey] = newVal;
                    }
                    break;

                case "param":
                    var param = ParseParam(child, isGlobal: true, allowTunnel: false);
                    if (stylesheet.Parameters.Any(p => p.Name.Equals(param.Name)) ||
                        stylesheet.Variables.Any(v => v.Name.Equals(param.Name)))
                        throw new XsltException($"XTSE0630: Duplicate global parameter '{param.Name.LocalName}'",
                            GetSourceLocation(child));
                    stylesheet.Parameters.Add(param);
                    stylesheet.LocalComponentSymbols.Add(("V", param.Name, 0));
                    // Track static params for use-when and shadow attribute resolution
                    if (param.Static && param.Select != null)
                    {
                        var paramKey = param.Name;
                        // Skip select evaluation if the value was provided externally by the calling processor
                        // (external params are passed as bare names, hence keyed by LocalName here).
                        if (_externalStaticParamNames.Contains(paramKey.LocalName))
                            break;
                        var newParamVal = EvaluateStaticSelectSafe(param.Select, child);
                        // XTSE3450: check same as for variables
                        if (_importedStaticVarNames.TryGetValue(paramKey, out var importedParamIsVariable))
                        {
                            // Inconsistency: one is xsl:variable and the other is xsl:param
                            if (importedParamIsVariable) // imported was a variable, this is a param
                                throw new XsltException($"XTSE3450: Static parameter '{paramKey.LocalName}' is inconsistent with an imported static variable of the same name",
                                    GetSourceLocation(child));
                            if (_staticVariables.TryGetValue(paramKey, out var importedParamVal) && !Equals(newParamVal, importedParamVal))
                                throw new XsltException($"XTSE3450: Static parameter '{paramKey.LocalName}' has value '{newParamVal}' which is inconsistent with the imported value '{importedParamVal}'",
                                    GetSourceLocation(child));
                        }
                        _staticVariables[paramKey] = newParamVal;
                    }
                    break;

                case "function":
                    var func = ParseFunction(child);
                    var funcKey = (func.Name, func.Parameters.Count);
                    if (stylesheet.Functions.ContainsKey(funcKey))
                        throw new XsltException($"XTSE0770: Duplicate function declaration '{func.Name.LocalName}' with arity {func.Parameters.Count}",
                            GetSourceLocation(child));
                    stylesheet.Functions[funcKey] = func;
                    stylesheet.LocalComponentSymbols.Add(("F", func.Name, func.Parameters.Count));
                    break;

                case "key":
                    var key = ParseKey(child, stylesheet.DefaultCollation);
                    // XTSE1222: Duplicate key declarations must have matching composite attribute
                    if (stylesheet.Keys.TryGetValue(key.Name, out var existingKey) && existingKey.Composite != key.Composite)
                        throw new XsltException($"XTSE1222: Conflicting xsl:key declarations for key '{key.Name.LocalName}': composite attribute values differ",
                            GetSourceLocation(child));
                    // XTSE1220: Duplicate key declarations must have the same collation
                    if (existingKey != null && !string.Equals(existingKey.Collation, key.Collation, StringComparison.Ordinal))
                        throw new XsltException($"XTSE1220: Conflicting xsl:key declarations for key '{key.Name.LocalName}': collation values differ ('{existingKey.Collation ?? "codepoint"}' vs '{key.Collation ?? "codepoint"}')",
                            GetSourceLocation(child));
                    if (existingKey != null)
                    {
                        // Multiple key definitions with same name: append to existing
                        existingKey.OtherDefinitions ??= new List<XsltKey>();
                        existingKey.OtherDefinitions.Add(key);
                    }
                    else
                    {
                        stylesheet.Keys[key.Name] = key;
                    }
                    break;

                case "output":
                    stylesheet.Outputs.Add(ParseOutput(child, stylesheet));
                    break;

                case "attribute-set":
                    var attrSet = ParseAttributeSet(child);
                    // Merge same-named attribute sets per XSLT spec
                    if (stylesheet.AttributeSets.TryGetValue(attrSet.Name, out var existing))
                    {
                        // Preserve per-definition parts for correct interleaving
                        existing.Parts ??= new List<XsltAttributeSetPart>
                        {
                            new() { UseAttributeSets = new List<QName>(existing.UseAttributeSets), Attributes = new List<XsltAttribute>(existing.Attributes), BaseUri = existing.BaseUri }
                        };
                        existing.Parts.Add(new XsltAttributeSetPart
                        {
                            UseAttributeSets = attrSet.UseAttributeSets,
                            Attributes = attrSet.Attributes,
                            BaseUri = attrSet.BaseUri
                        });
                        // Also maintain flat lists for backwards compat / simple cases
                        existing.Attributes.AddRange(attrSet.Attributes);
                        foreach (var u in attrSet.UseAttributeSets)
                        {
                            if (!existing.UseAttributeSets.Contains(u))
                                existing.UseAttributeSets.Add(u);
                        }
                    }
                    else
                    {
                        stylesheet.AttributeSets[attrSet.Name] = attrSet;
                    }
                    break;

                case "character-map":
                    var charMap = ParseCharacterMap(child);
                    // XTSE1580: Duplicate character map names at same import precedence
                    if (stylesheet.CharacterMaps.ContainsKey(charMap.Name))
                        throw new XsltException($"XTSE1580: Duplicate xsl:character-map declarations for name '{charMap.Name.LocalName}' at the same import precedence",
                            GetSourceLocation(child));
                    stylesheet.CharacterMaps[charMap.Name] = charMap;
                    break;

                case "decimal-format":
                    var decFormat = ParseDecimalFormat(child);
                    // Use empty QName for the default (unnamed) decimal format
                    var decFormatKey = decFormat.Name ?? new QName(NamespaceId.None, "");
                    if (stylesheet.DecimalFormats.TryGetValue(decFormatKey, out var existingDf))
                    {
                        // Merge: for each attribute, use the explicitly-set value from the new declaration,
                        // falling back to the existing declaration's value.
                        stylesheet.DecimalFormats[decFormatKey] = MergeDecimalFormats(existingDf, decFormat, child);
                    }
                    else
                    {
                        stylesheet.DecimalFormats[decFormatKey] = decFormat;
                    }
                    break;

                case "strip-space":
                    ValidateAllowedAttributes(child, GetSourceLocation(child), "elements");
                    ValidateEmptyElement(child);
                    var stripElements = child.Attribute("elements")?.Value ?? "";
                    foreach (var name in stripElements.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        stylesheet.StripSpace.Add(ParseNameTest(name, child));
                    }
                    break;

                case "preserve-space":
                    ValidateAllowedAttributes(child, GetSourceLocation(child), "elements");
                    ValidateEmptyElement(child);
                    var preserveElements = child.Attribute("elements")?.Value ?? "";
                    foreach (var name in preserveElements.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        stylesheet.PreserveSpace.Add(ParseNameTest(name, child));
                    }
                    break;

                case "namespace-alias":
                {
                    var stylesheetPrefix = child.Attribute("stylesheet-prefix")?.Value;
                    var resultPrefix = child.Attribute("result-prefix")?.Value;
                    // XTSE0010: Both stylesheet-prefix and result-prefix are required
                    if (stylesheetPrefix == null)
                        throw new XsltException("XTSE0010: xsl:namespace-alias requires a stylesheet-prefix attribute",
                            GetSourceLocation(child));
                    if (resultPrefix == null)
                        throw new XsltException("XTSE0010: xsl:namespace-alias requires a result-prefix attribute",
                            GetSourceLocation(child));
                    {
                        // XTSE0812: Validate prefixes are declared
                        if (stylesheetPrefix != "#default" && child.GetNamespaceOfPrefix(stylesheetPrefix) == null)
                            throw new XsltException($"XTSE0812: Namespace prefix '{stylesheetPrefix}' used in stylesheet-prefix of xsl:namespace-alias is not declared",
                                GetSourceLocation(child));
                        if (resultPrefix != "#default" && child.GetNamespaceOfPrefix(resultPrefix) == null)
                            throw new XsltException($"XTSE0812: Namespace prefix '{resultPrefix}' used in result-prefix of xsl:namespace-alias is not declared",
                                GetSourceLocation(child));

                        // Resolve the stylesheet prefix to its namespace URI
                        string stylesheetNsUri;
                        if (stylesheetPrefix == "#default")
                            stylesheetNsUri = child.GetDefaultNamespace().NamespaceName;
                        else
                            stylesheetNsUri = child.GetNamespaceOfPrefix(stylesheetPrefix)?.NamespaceName ?? "";

                        // Resolve the result prefix to its namespace URI
                        string resultNsUri;
                        string outputPrefix;
                        if (resultPrefix == "#default")
                        {
                            resultNsUri = child.GetDefaultNamespace().NamespaceName;
                            outputPrefix = "";
                        }
                        else
                        {
                            resultNsUri = child.GetNamespaceOfPrefix(resultPrefix)?.NamespaceName ?? "";
                            outputPrefix = resultPrefix;
                        }

                        // XTSE0810: Conflicting namespace-alias at same import precedence
                        if (stylesheet.NamespaceAliases.TryGetValue(stylesheetNsUri, out var existingAlias)
                            && existingAlias.ResultUri != resultNsUri)
                            throw new XsltException($"XTSE0810: Conflicting xsl:namespace-alias declarations for namespace '{stylesheetNsUri}' at the same import precedence",
                                GetSourceLocation(child));

                        stylesheet.NamespaceAliases[stylesheetNsUri] = (resultNsUri, outputPrefix);
                    }
                    break;
                }

                case "accumulator":
                    var acc = ParseAccumulator(child);
                    // Track duplicates within the same module for XTSE3350 detection.
                    // Don't throw yet — import precedence may resolve the conflict.
                    // An accumulator merged in from a used package is package-local, so it
                    // does not clash with a local accumulator of the same name (override-misc-005).
                    if (stylesheet.Accumulators.TryGetValue(acc.Name, out var priorAcc)
                        && stylesheet.PackageMergedAccumulatorNames.Contains(acc.Name)
                        && priorAcc.PackageStylesheet != null)
                    {
                        // A used package already contributed an accumulator with this name.
                        // Both are package-local: relocate the used package's copy under a
                        // synthetic internal key and record the remap so that package's own
                        // components (e.g. its templates calling accumulator-after) resolve to
                        // it, while the local declaration keeps the plain name (override-misc-005).
                        var synthetic = new QName(priorAcc.Name.Namespace,
                            priorAcc.Name.LocalName + "pkg" + stylesheet.PackageAccumulatorRemap.Count);
                        // Clone under the synthetic name so pre-computation keys the values by
                        // the synthetic key — PreComputeAccumulatorsAsync keys _accumulatorValues
                        // by the accumulator's own Name, so reusing the same object would collide
                        // under the plain 'ac' key and the used package's values would be lost.
                        stylesheet.Accumulators[synthetic] = new XsltAccumulator
                        {
                            Name = synthetic,
                            As = priorAcc.As,
                            InitialValue = priorAcc.InitialValue,
                            Rules = priorAcc.Rules,
                            Streamable = priorAcc.Streamable,
                            SourceName = priorAcc.SourceName,
                            PackageStylesheet = priorAcc.PackageStylesheet
                        };
                        stylesheet.PackageAccumulatorRemap[(priorAcc.PackageStylesheet, priorAcc.Name)] = synthetic;
                    }
                    else if (stylesheet.Accumulators.ContainsKey(acc.Name)
                        && !stylesheet.PackageMergedAccumulatorNames.Contains(acc.Name))
                        stylesheet.DuplicateAccumulatorNames.Add(acc.Name);
                    stylesheet.Accumulators[acc.Name] = acc;
                    break;

                case "mode":
                    var mode = ParseMode(child);
                    // Use empty QName for the unnamed (default) mode
                    var modeKey = mode.Name ?? new QName(NamespaceId.None, "");
                    // XTSE0545: Check for conflicting mode declarations at same import precedence
                    if (_modeElements.TryGetValue(modeKey, out var prevModeElement))
                    {
                        // Compare explicitly-set attributes between the two declarations (same module)
                        CheckModeAttrConflict(prevModeElement, child, "on-no-match", mode.Name?.LocalName ?? "(unnamed)");
                        CheckModeAttrConflict(prevModeElement, child, "on-multiple-match", mode.Name?.LocalName ?? "(unnamed)");
                        CheckModeAttrConflict(prevModeElement, child, "streamable", mode.Name?.LocalName ?? "(unnamed)");
                        // use-accumulators: defer conflict — may be resolved by higher-precedence import
                        if (UseAccumulatorsConflict(stylesheet.Modes[modeKey], mode))
                            stylesheet.ConflictingModeAccumulators.Add(modeKey);
                        // visibility: defer conflict — may be resolved by higher-precedence import
                        if (VisibilityConflict(stylesheet.Modes[modeKey], mode))
                            stylesheet.ConflictingModeVisibility.Add(modeKey);
                    }
                    else if (stylesheet.Modes.TryGetValue(modeKey, out var mergedMode))
                    {
                        // Mode exists from an included module — defer conflict check
                        if (UseAccumulatorsConflict(mergedMode, mode))
                            stylesheet.ConflictingModeAccumulators.Add(modeKey);
                        if (VisibilityConflict(mergedMode, mode))
                            stylesheet.ConflictingModeVisibility.Add(modeKey);
                    }
                    _modeElements[modeKey] = child;
                    stylesheet.Modes[modeKey] = mode;
                    if (mode.Name is { } namedMode)
                        stylesheet.LocalComponentSymbols.Add(("M", namedMode, 0));
                    break;

                // xsl:global-context-item declares constraints on the global context item.
                case "global-context-item":
                {
                    var gciUse = child.Attribute("use")?.Value?.Trim();
                    var gciAs = child.Attribute("as")?.Value?.Trim();
                    // XTSE3089: as + use="absent" is a static error
                    if (gciUse == "absent" && gciAs != null)
                        throw new XsltException("XTSE3089: xsl:global-context-item specifies use='absent' together with an 'as' attribute",
                            GetSourceLocation(child));
                    // XTSE3087: Multiple xsl:global-context-item declarations in the same module
                    if (stylesheet.GlobalContextItemUse != null)
                        throw new XsltException("XTSE3087: A stylesheet module contains more than one xsl:global-context-item declaration",
                            GetSourceLocation(child));
                    // Store the declaration
                    var useValue = gciUse switch
                    {
                        "required" => ContextItemUse.Required,
                        "absent" => ContextItemUse.Absent,
                        _ => ContextItemUse.Optional
                    };
                    stylesheet.GlobalContextItemUse = useValue;
                    if (gciAs != null)
                    {
                        var savedNsContext = _nsContext;
                        _nsContext = child;
                        stylesheet.GlobalContextItemAs = ParseSequenceType(gciAs, child);
                        _nsContext = savedNsContext;
                    }
                    break;
                }

                case "item-type":
                    // XSLT 4.0: xsl:item-type declares a named type alias
                    ParseItemType(child, stylesheet);
                    break;

                case "use-package":
                    if (_insideImportedModule)
                        throw new XsltException("XTSE3008: xsl:use-package is not permitted in an imported stylesheet module",
                            GetSourceLocation(child));
                    ParseUsePackage(child, stylesheet);
                    break;

                case "expose":
                    // Collect expose declarations — applied after all components are parsed
                    stylesheet.ExposeDeclarations.Add(new ExposeDeclaration
                    {
                        Component = child.Attribute("component")?.Value,
                        Names = child.Attribute("names")?.Value,
                        Visibility = ParseVisibility(child.Attribute("visibility")?.Value),
                        Element = child
                    });
                    break;

                case "import-schema":
                    {
                        // Capture the import for runtime resolution against the registered
                        // ISchemaProvider. namespace + schema-location attributes are both
                        // optional per the schema; missing namespace = no-namespace schema.
                        _hasImportSchema = true;
                        var nsAttr = child.Attribute("namespace")?.Value ?? "";
                        var locAttr = child.Attribute("schema-location")?.Value;
                        var locations = !string.IsNullOrWhiteSpace(locAttr)
                            ? locAttr.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries)
                            : Array.Empty<string>();
                        // The xmlns:* declarations on the element provide the prefix binding
                        // for any prefixed schema-element/attribute references later. Capture
                        // the prefix that maps to this namespace, if any.
                        string? prefix = null;
                        foreach (var attr in child.Attributes())
                        {
                            if (attr.IsNamespaceDeclaration && attr.Value == nsAttr)
                            {
                                prefix = attr.Name.LocalName == "xmlns" ? null : attr.Name.LocalName;
                                break;
                            }
                        }
                        stylesheet.SchemaImports.Add(new XsltSchemaImport
                        {
                            TargetNamespace = nsAttr,
                            Prefix = prefix,
                            SchemaLocations = locations,
                            Location = GetSourceLocation(child),
                        });
                    }
                    break;

                default:
                    // Forwards compatibility: if effective version > 3.0, unknown XSLT top-level
                    // declarations are silently ignored (per XSLT spec section 3.8).
                    if (childVersionNum > 3.0m)
                        break;
                    // XTSE0010: An XSLT element appears in a position where it is not permitted
                    throw new XsltException($"XTSE0010: Element xsl:{child.Name.LocalName} is not permitted as a top-level stylesheet element",
                        GetSourceLocation(child));
            }
        }

        // Apply xsl:expose declarations — changes visibility of the package's own components
        if (stylesheet.ExposeDeclarations.Count > 0)
            ApplyExposeDeclarations(stylesheet);

        // XTSE0265: Check for conflicting input-type-annotations across all modules
        foreach (var imported in stylesheet.Imports)
            CheckInputTypeAnnotationsConflict(stylesheet, imported);

        // Merge imported declarations at lower precedence — only at top level.
        // For sub-modules (included/imported), imports are kept separate and
        // transferred to the including module via MergeStylesheet, so the top-level
        // can resolve precedence correctly. Without this, templates from a sub-module's
        // imports would be treated as same-precedence and incorrectly trigger XTSE0660.
        if (isTopLevel)
        {
            // For named templates/functions/keys/variables: later imports have higher precedence,
            // so process in reverse order (TryAdd keeps first = highest precedence).
            // For attribute sets: merge in forward order so higher-precedence attributes come last
            // and override lower-precedence ones. Track which sets are locally defined so that
            // the main module's parts always come last (highest precedence).
            for (var i = stylesheet.Imports.Count - 1; i >= 0; i--)
                MergeImportedNamedDeclarations(stylesheet, stylesheet.Imports[i]);
            var localAttrSetNames = new HashSet<QName>(stylesheet.AttributeSets.Keys);
            foreach (var imported in stylesheet.Imports)
                MergeImportedAttributeSets(stylesheet, imported, localAttrSetNames);

            // Collect outputs from imported stylesheets (lower precedence)
            CollectImportedOutputs(stylesheet, stylesheet, 1);

            // Merge multiple xsl:output declarations with the same (or no) name
            MergeOutputDeclarations(stylesheet);

            // Merge decimal formats from imported stylesheets (lower precedence)
            MergeImportedDecimalFormats(stylesheet);

            // Merge namespace aliases from imported stylesheets (lower precedence)
            MergeImportedNamespaceAliases(stylesheet);
        }

        // Note: ValidateDecimalFormats is called only from the top-level Parse() method,
        // NOT here in ParseStylesheet, because imported stylesheets may have same-precedence
        // conflicts that are resolved by higher-precedence overrides in the importing stylesheet.

        // Conflicting strip-space/preserve-space is a static error (XTSE0270)
        CheckStripSpaceConflicts(stylesheet);

        // Note: ValidateAttributeSetReferences is called from the top-level Parse() method,
        // NOT here in ParseStylesheet, because imported stylesheets may reference attribute sets
        // defined in sibling imports that haven't been merged yet.

        // Validate character map references (XTSE1590, XTSE1600)
        ValidateCharacterMapReferences(stylesheet);

        // XTSE3085: When declared-modes="yes", all used modes must be declared via xsl:mode
        // Only check for packages that set declared-modes themselves — included xsl:stylesheet
        // modules inherit the parent's _usedModeReferences but should not validate against
        // their own (empty) mode declarations.
        if (declaredModes && _usedModeReferences != null)
        {
            var declaredModeNames = new HashSet<QName>(stylesheet.Modes.Keys);
            // The unnamed mode key is QName(None, "")
            var unnamedModeKey = new QName(NamespaceId.None, "");
            foreach (var (modeRef, refLocation) in _usedModeReferences)
            {
                // Map #default sentinel to the unnamed mode key
                var lookupKey = modeRef.Equals(TemplateIndex.DefaultModeSentinel) ? unnamedModeKey : modeRef;
                if (!declaredModeNames.Contains(lookupKey))
                {
                    var modeName = modeRef.Equals(TemplateIndex.DefaultModeSentinel) ? "#unnamed" : modeRef.LocalName;
                    throw new XsltException(
                        $"XTSE3085: Mode '{modeName}' is used but not declared, and declared-modes=\"yes\" is in effect",
                        refLocation);
                }
            }
        }
        _usedModeReferences = prevModeReferences;

        _modeElements = prevModeElements;

        // XTSE3050: A component declared in the using package (outside xsl:override) must not
        // have the same symbolic name as a public/final component of a used package. The
        // legal way to redefine such a component is within xsl:override.
        if (stylesheet.UsedComponentSymbols.Count > 0)
        {
            foreach (var sym in stylesheet.LocalComponentSymbols)
            {
                if (stylesheet.UsedComponentSymbols.Contains(sym))
                {
                    var kindName = sym.Kind switch
                    {
                        "F" => "function",
                        "M" => "mode",
                        _ => "variable"
                    };
                    throw new XsltException(
                        $"XTSE3050: The {kindName} '{sym.Name.LocalName}' has the same name as a public component of a used package and is declared outside xsl:override");
                }
            }
            // Adding a template rule to a mode from a used package is only allowed within
            // xsl:override (override-m-018).
            foreach (var ruleMode in stylesheet.LocalTemplateRuleModes)
            {
                if (stylesheet.UsedComponentSymbols.Contains(("M", ruleMode, 0)))
                    throw new XsltException(
                        $"XTSE3050: A template rule for mode '{ruleMode.LocalName}' (declared in a used package) must appear within xsl:override");
            }
        }

        // XTSE3350: Duplicate accumulators in the top-level module are always an error
        // (no higher-precedence import can override them).
        // Only check at the top level — imported modules' duplicates may be resolved by
        // a higher-precedence declaration in the importing module.
        if (isTopLevel && stylesheet.DuplicateAccumulatorNames.Count > 0)
        {
            var dupName = stylesheet.DuplicateAccumulatorNames.First();
            throw new XsltException($"XTSE3350: Duplicate accumulator name '{dupName.LocalName}'");
        }

        // XTSE0545: Unresolved mode use-accumulators conflicts
        if (isTopLevel && stylesheet.ConflictingModeAccumulators.Count > 0)
        {
            var conflictName = stylesheet.ConflictingModeAccumulators.First();
            throw new XsltException(
                $"XTSE0545: Conflicting xsl:mode declarations for mode '{conflictName.LocalName}': attribute 'use-accumulators' has conflicting values at the same import precedence");
        }

        // XTSE0545: Unresolved mode visibility conflicts
        if (isTopLevel && stylesheet.ConflictingModeVisibility.Count > 0)
        {
            var conflictName = stylesheet.ConflictingModeVisibility.First();
            throw new XsltException(
                $"XTSE0545: Conflicting xsl:mode declarations for mode '{conflictName.LocalName}': attribute 'visibility' has conflicting values at the same import precedence");
        }

        return stylesheet;
    }


    /// <summary>
    /// Parses an xsl:use-package declaration, resolves the package from the catalog,
    /// applies overrides, and merges visible components into the consuming stylesheet.
    /// </summary>
    private void ParseUsePackage(XElement element, XsltStylesheet stylesheet)
    {
        var packageName = element.Attribute("name")?.Value
            ?? throw new XsltException("XTSE0010: xsl:use-package requires a 'name' attribute",
                GetSourceLocation(element));
        var packageVersion = element.Attribute("package-version")?.Value;
        if (packageVersion != null && !PackageVersionSyntax.IsValidRange(packageVersion))
            throw new XsltException(
                $"XTSE0020: package-version '{packageVersion}' on xsl:use-package is not a valid version range",
                GetSourceLocation(element));

        // Resolve the package file from the catalog
        if (_packageCatalog == null || !_packageCatalog.TryGetValue(packageName, out var packageEntries))
            throw new XsltException($"XTDE3052: Package '{packageName}' not found",
                GetSourceLocation(element));

        // Version matching
        var packageFile = ResolvePackageVersion(packageEntries, packageVersion, packageName, GetSourceLocation(element));

        // Parse the package stylesheet with a fresh parser sharing our expression parser and catalog
        var packageParser = new StylesheetParser(_expressionParser, _packageCatalog) { AllowDtdProcessing = AllowDtdProcessing, ResourcePolicy = ResourcePolicy, PreloadedResources = PreloadedResources, VersionResolution = VersionResolution };
        var packageXml = System.IO.File.ReadAllText(packageFile);
        var packageBaseUri = new Uri(Path.GetFullPath(packageFile));
        var packageStylesheet = packageParser.Parse(packageXml, packageBaseUri, isLibraryPackage: true);

        // Template rules brought in via xsl:use-package have LOWER import precedence than
        // the using package's own declarations, including rules supplied inside xsl:override
        // (XSLT 3.0 §6.6.2: import precedence dominates priority). Shift every rule already
        // present in the used package down one level before overrides are applied; the
        // override rules added below stay at the using package's baseline (0), so an override
        // beats a used-package rule even when the used rule has higher priority. This shift
        // is relative, so it composes across nested use-package boundaries.
        foreach (var t in packageStylesheet.Templates)
            t.ImportPrecedence -= 1;

        // First pass: apply overrides and collect overridden component names
        var overriddenTemplateNames = new HashSet<QName>();
        var overriddenFunctionKeys = new HashSet<(QName, int)>();
        // Symbolic names resolved by xsl:override in THIS use-package. A component whose symbol
        // is overridden here is the XTSE3050 exception ("unless one overrides the other") and is
        // excluded from cross-package conflict accounting below.
        var overriddenSymbols = new HashSet<(string, QName, int)>();
        foreach (var child in element.Elements())
        {
            if (child.Name == XsltNs + "override")
            {
                // Collect names of overridden components for XTSE3051 checking
                foreach (var overChild in child.Elements())
                {
                    if (overChild.Name.Namespace != XsltNs) continue;
                    var overName = overChild.Attribute("name")?.Value;
                    if (overName == null) continue;
                    _nsContext = overChild;
                    var overQName = ParseQName(overName, overChild);
                    _nsContext = null;
                    switch (overChild.Name.LocalName)
                    {
                        case "template":
                            overriddenTemplateNames.Add(overQName);
                            overriddenSymbols.Add(("T", overQName, 0));
                            break;
                        case "function":
                            var arity = overChild.Elements(XsltNs + "param").Count();
                            overriddenFunctionKeys.Add((overQName, arity));
                            overriddenSymbols.Add(("F", overQName, arity));
                            break;
                        case "variable":
                        case "param":
                            overriddenSymbols.Add(("V", overQName, 0));
                            break;
                        case "attribute-set":
                            overriddenSymbols.Add(("A", overQName, 0));
                            break;
                        case "mode":
                            overriddenSymbols.Add(("M", overQName, 0));
                            break;
                    }
                }
                ApplyOverrides(child, packageStylesheet);
            }
        }

        // Second pass: validate accept declarations (after overrides, for XTSE3051 checking),
        // then apply visibility changes with name-specificity resolution across ALL accepts:
        // an explicit QName is more specific than a partial wildcard (prefix:* / *:local /
        // Q{uri}*), which is more specific than "*"; ties are broken by document order.
        // Capture which attribute-sets the used package PROVIDES at its boundary (exposed
        // public/final, or abstract) before xsl:accept lowers their effective visibility.
        // A set the using package accepts as "private" is still provided (usable), whereas a
        // set declared private in the used package is not provided and must not leak
        // (override-as-005 vs accept-002).
        foreach (var (_, attrSet) in packageStylesheet.AttributeSets)
            attrSet.ProvidedByPackage =
                attrSet.Visibility is Visibility.Public or Visibility.Final or Visibility.Abstract;

        // Same capture for global variables: whether the used package exposes the variable
        // across its boundary (explicit public/final/abstract — or exposed as such via
        // xsl:expose, which pre-sets ProvidedByPackage). Done BEFORE xsl:accept so a later
        // accept lowering to private keeps a provided variable usable, while a genuinely
        // private variable (private, or the package default) stays invisible to other packages
        // (use-package-006/007). The parser's public default (a workaround so plain
        // xsl:import/xsl:include modules stay mutually visible) leaves VisibilityAttr null, so
        // it is correctly treated as NOT provided. OR with any flag set by xsl:expose.
        foreach (var variable in packageStylesheet.Variables)
            variable.ProvidedByPackage |= XsltStylesheet.VisibleAcrossPackageBoundary(variable.VisibilityAttr);

        // Capture boundary exposure BEFORE xsl:accept runs — an accept clones the component and
        // drops VisibilityAttr, so the used package's own public/final exposure must be
        // snapshotted here. Used below (with the post-accept visibility) to decide which
        // components this use-package contributes for XTSE3050. Only components the used package
        // genuinely exposes as public/final become components of the using package; a private
        // component (including a private xsl:override) stays internal to the used package and
        // never conflicts (use-package-175/176 diamond). For templates/functions/modes an
        // explicit or expose-set VisibilityAttr of public/final is the signal (the parser's
        // public-by-default workaround leaves VisibilityAttr null, so it is correctly excluded).
        // For variables/attribute-sets, whose exposure clones drop VisibilityAttr, the signal is
        // ProvidedByPackage together with a pre-accept effective visibility of public/final —
        // ProvidedByPackage alone is set even for a private override, so it is not sufficient.
        // Only a component DECLARED (or overridden) in the immediately-used package itself is a
        // component this use-package contributes. A component merged into it from a deeper
        // use-package is stamped with that deeper package (PackageStylesheet) and only flows
        // through transitively as an effectively-private inherited component — it must not be
        // treated as re-exposed here, or two sibling packages that each inherit the same public
        // global from a shared lower package would falsely conflict (use-package-176 different
        // versions). A package's own declarations are unstamped (null) at this point; the merge
        // that stamps them runs after this capture.
        bool DeclaredHere(XsltStylesheet? owner) => owner == null || ReferenceEquals(owner, packageStylesheet);
        var boundaryExposedTemplates = new HashSet<QName>();
        foreach (var (tname, t) in packageStylesheet.NamedTemplates)
            if (t.VisibilityAttr is "public" or "final" && DeclaredHere(t.PackageStylesheet))
                boundaryExposedTemplates.Add(tname);
        var boundaryExposedModes = new HashSet<QName>();
        foreach (var (mname, m) in packageStylesheet.Modes)
            if (m.VisibilityAttr is "public" or "final") boundaryExposedModes.Add(mname);
        var boundaryExposedFunctions = new HashSet<(QName, int)>();
        foreach (var (fkey2, f) in packageStylesheet.Functions)
            if (f.VisibilityAttr is "public" or "final" && DeclaredHere(f.PackageStylesheet))
                boundaryExposedFunctions.Add((fkey2.Name, fkey2.Arity));
        var boundaryExposedVariables = new HashSet<QName>();
        foreach (var variable in packageStylesheet.Variables)
            if (variable.ProvidedByPackage && variable.Visibility is Visibility.Public or Visibility.Final
                && DeclaredHere(variable.PackageStylesheet))
                boundaryExposedVariables.Add(variable.Name);
        var boundaryExposedAttrSets = new HashSet<QName>();
        foreach (var (aname, a) in packageStylesheet.AttributeSets)
            if (a.ProvidedByPackage && a.Visibility is Visibility.Public or Visibility.Final
                && DeclaredHere(a.PackageStylesheet))
                boundaryExposedAttrSets.Add(aname);

        var acceptElements = new List<XElement>();
        foreach (var child in element.Elements())
        {
            if (child.Name == XsltNs + "accept")
            {
                ValidateAccept(child, packageStylesheet, overriddenTemplateNames, overriddenFunctionKeys);
                acceptElements.Add(child);
            }
        }
        if (acceptElements.Count > 0)
            ApplyAcceptVisibilities(acceptElements, packageStylesheet);

        // XTSE3050 (cross-package): each xsl:use-package is a distinct instance of the used
        // package, so a component it contributes as a visible (accepted, not hidden) component
        // of the using package clashes with a same-kind-same-name component contributed by any
        // OTHER use-package on this package. This covers two independent used packages exposing
        // the same unnamespaced symbol (accept-020) and the diamond where the same package is
        // reached via xsl:include + another use-package (package-022err). A symbol resolved by
        // xsl:override here is the spec exception and is excluded. "Contributed" means the
        // component is exposed at the used package's boundary (public/final — or exposed as such
        // via xsl:expose, captured for variables/attribute-sets by ProvidedByPackage) and was
        // not lowered to hidden by an xsl:accept in this use-package.
        RecordAcceptedComponent(stylesheet, overriddenSymbols, element,
            "T", packageStylesheet.NamedTemplates.Select(t =>
                (t.Key, IsBoundaryExposed: boundaryExposedTemplates.Contains(t.Key),
                 t.Value.Visibility, 0)));
        RecordAcceptedComponent(stylesheet, overriddenSymbols, element,
            "V", packageStylesheet.Variables.Select(v =>
                (v.Name, IsBoundaryExposed: boundaryExposedVariables.Contains(v.Name), v.Visibility, 0)));
        RecordAcceptedComponent(stylesheet, overriddenSymbols, element,
            "A", packageStylesheet.AttributeSets.Select(a =>
                (a.Key, IsBoundaryExposed: boundaryExposedAttrSets.Contains(a.Key), a.Value.Visibility, 0)));
        RecordAcceptedComponent(stylesheet, overriddenSymbols, element,
            "M", packageStylesheet.Modes.Select(m =>
                (m.Key, IsBoundaryExposed: boundaryExposedModes.Contains(m.Key),
                 m.Value.Visibility, 0)));
        RecordAcceptedComponent(stylesheet, overriddenSymbols, element,
            "F", packageStylesheet.Functions.Select(f =>
                (f.Key.Name, IsBoundaryExposed: boundaryExposedFunctions.Contains((f.Key.Name, f.Key.Arity)),
                 f.Value.Visibility, f.Key.Arity)));

        // Record the symbolic names of public/final components exposed by this used package.
        // A component of the same symbolic kind+name declared in the using package outside
        // xsl:override is a static error (XTSE3050): override-v-012 (variable), override-f-023
        // (function), override-m-017 (mode). Overriding declarations live inside xsl:override
        // and are therefore never added to LocalComponentSymbols, so they never collide here.
        // Only components with an EXPLICIT visibility of public or final are exposed:
        // package top-level components default to private (the parser's public default is a
        // workaround for non-package modules, so it must not be treated as "exposed" here).
        static bool ExplicitlyExposed(string? visAttr) => visAttr is "public" or "final";
        foreach (var (fkey, func) in packageStylesheet.Functions)
        {
            if (ExplicitlyExposed(func.VisibilityAttr))
                stylesheet.UsedComponentSymbols.Add(("F", fkey.Item1, fkey.Item2));
        }
        foreach (var variable in packageStylesheet.Variables)
        {
            if (ExplicitlyExposed(variable.VisibilityAttr))
                stylesheet.UsedComponentSymbols.Add(("V", variable.Name, 0));
        }
        foreach (var (modeKey, mode) in packageStylesheet.Modes)
        {
            if (ExplicitlyExposed(mode.VisibilityAttr))
                stylesheet.UsedComponentSymbols.Add(("M", modeKey, 0));
        }

        // Merge visible (public/final) components into the consuming stylesheet at lower precedence
        var beforeMerge = new HashSet<QName>(stylesheet.NamedTemplates.Keys);
        MergePackageComponents(stylesheet, packageStylesheet);
        // Track which template names were added by this package merge
        foreach (var name in stylesheet.NamedTemplates.Keys)
        {
            if (!beforeMerge.Contains(name))
                _packageMergedTemplateNames.Add(name);
        }
    }


    /// <summary>
    /// Resolves a package file path from the catalog using version matching, selecting
    /// among all matching versions according to <see cref="VersionResolution"/>.
    /// </summary>
    private string ResolvePackageVersion(
        List<(string? Version, string FilePath)> entries,
        string? requestedVersion,
        string packageName,
        SourceLocation? location)
    {
        if (entries.Count == 0)
            throw new XsltException($"XTDE3052: Package '{packageName}' has no available versions", location);

        var selected = SelectMatchingPackage(entries, requestedVersion, VersionResolution);
        if (selected != null)
            return selected;

        throw new XsltException($"XTDE3052: No matching version for package '{packageName}' " +
            $"(requested '{requestedVersion}')", location);
    }


    /// <summary>
    /// Applies xsl:override children to a package stylesheet — replaces matching
    /// components and stores originals for xsl:original resolution.
    /// </summary>
    private void ApplyOverrides(XElement overrideElement, XsltStylesheet packageStylesheet)
    {
        // Track overridden components for XTSE0770 duplicate detection
        var overriddenTemplates = new HashSet<QName>();
        var overriddenFunctions = new HashSet<(QName, int)>();
        var overriddenVariables = new HashSet<QName>();

        // xsl:override may have default-mode attribute — set it for template parsing
        var savedDefaultMode = _currentDefaultMode;
        var overrideDefaultMode = overrideElement.Attribute("default-mode")?.Value;
        if (overrideDefaultMode != null && overrideDefaultMode != "#unnamed")
        {
            _nsContext = overrideElement;
            _currentDefaultMode = ParseQName(overrideDefaultMode, overrideElement);
            _nsContext = null;
        }

        // XTSE0010: The only permitted children of xsl:override are the overridable
        // component declarations xsl:template, xsl:function, xsl:variable, xsl:param and
        // xsl:attribute-set. A non-whitespace text node child is not allowed (override-f-005).
        foreach (var node in overrideElement.Nodes())
        {
            if (node is XText text && !string.IsNullOrWhiteSpace(text.Value))
                throw new XsltException(
                    "XTSE0010: A text node is not permitted as a child of xsl:override",
                    GetSourceLocation(overrideElement));
        }

        foreach (var child in overrideElement.Elements())
        {
            // A literal result element (or any non-XSLT element) is not a permitted child
            // of xsl:override (override-f-006).
            if (child.Name.Namespace != XsltNs)
                throw new XsltException(
                    $"XTSE0010: Element '{child.Name.LocalName}' is not permitted as a child of xsl:override",
                    GetSourceLocation(child));
            _nsContext = child;

            switch (child.Name.LocalName)
            {
                case "mode":
                case "output":
                case "decimal-format":
                case "namespace-alias":
                case "character-map":
                case "import":
                case "include":
                case "key":
                case "accumulator":
                case "use-package":
                case "override":
                case "expose":
                    throw new XsltException(
                        $"XTSE0010: xsl:{child.Name.LocalName} is not permitted as a child of xsl:override",
                        GetSourceLocation(child));
                case "template":
                {
                    var overrideTemplate = ParseTemplate(child);
                    if (overrideTemplate.Name is { } templateName)
                    {
                        // XTSE0770: Duplicate override of same component
                        if (!overriddenTemplates.Add(templateName))
                            throw new XsltException(
                                $"XTSE0770: Duplicate override of template '{templateName.LocalName}'",
                                GetSourceLocation(child));
                        // Named template override — must match an existing component
                        if (packageStylesheet.NamedTemplates.TryGetValue(templateName, out var original))
                        {
                            // XTSE3060: Can only override public or abstract components
                            if (original.Visibility is Visibility.Private or Visibility.Final)
                                throw new XsltException(
                                    $"XTSE3060: Cannot override template '{templateName.LocalName}' which has visibility '{original.Visibility.ToString().ToUpperInvariant()}'",
                                    GetSourceLocation(child));
                            // XTSE3070: Override signature must be compatible with original
                            ValidateOverrideSignature(overrideTemplate, original, templateName, child);
                            overrideTemplate.OriginalTemplate = original;
                            packageStylesheet.NamedTemplates[templateName] = overrideTemplate;
                        }
                        else
                        {
                            throw new XsltException(
                                $"XTSE3058: Override template '{templateName.LocalName}' does not match any component in the used package",
                                GetSourceLocation(child));
                        }
                    }
                    if (overrideTemplate.Match != null)
                    {
                        // XTSE3440: Template rules in xsl:override must not use #all, #unnamed,
                        // or #default (when default mode is unnamed)
                        foreach (var mode in overrideTemplate.Modes)
                        {
                            if (mode.LocalName is "#all" or "#unnamed" or "#default"
                                || mode.Equals(new QName(NamespaceId.None, "")))
                            {
                                throw new XsltException(
                                    $"XTSE3440: Template rule in xsl:override must not use mode '{mode.LocalName}'",
                                    GetSourceLocation(child));
                            }
                        }
                        if (overrideTemplate.Modes.Count == 0)
                        {
                            // No explicit mode = default mode. Check if the xsl:override element
                            // has a default-mode attribute — if so, templates inherit that mode.
                            var overrideDefMode = overrideElement.Attribute("default-mode")?.Value;
                            if (overrideDefMode == null || overrideDefMode == "#unnamed")
                            {
                                throw new XsltException(
                                    "XTSE3440: Template rule in xsl:override must specify an explicit mode",
                                    GetSourceLocation(child));
                            }
                        }
                        // XTSE3060: Check that the mode used is overridable (not final/private)
                        foreach (var mode in overrideTemplate.Modes)
                        {
                            if (packageStylesheet.Modes.TryGetValue(mode, out var modeDecl))
                            {
                                if (modeDecl.Visibility is Visibility.Final)
                                    throw new XsltException(
                                        $"XTSE3060: Cannot override template rule in mode '{mode.LocalName}' which has visibility 'final'",
                                        GetSourceLocation(child));
                                if (modeDecl.Visibility is Visibility.Private)
                                    throw new XsltException(
                                        $"XTSE3060: Cannot override template rule in mode '{mode.LocalName}' which has visibility 'private'",
                                        GetSourceLocation(child));
                            }
                        }

                        // XTSE3460: apply-imports is not allowed in override template rules
                        if (ContainsApplyImports(overrideTemplate.Body))
                            throw new XsltException(
                                "XTSE3460: xsl:apply-imports is not allowed in a template rule within xsl:override",
                                GetSourceLocation(child));

                        // Template rule override — replace matching templates by mode+match
                        var replaced = false;
                        for (var i = 0; i < packageStylesheet.Templates.Count; i++)
                        {
                            var pkgTemplate = packageStylesheet.Templates[i];
                            if (pkgTemplate.Name != null && overrideTemplate.Name != null
                                && pkgTemplate.Name.Equals(overrideTemplate.Name))
                            {
                                overrideTemplate.OriginalTemplate = pkgTemplate;
                                packageStylesheet.Templates[i] = overrideTemplate;
                                replaced = true;
                                break;
                            }
                        }
                        if (!replaced)
                            packageStylesheet.Templates.Add(overrideTemplate);
                    }
                    break;
                }
                case "function":
                {
                    var overrideFunc = ParseFunction(child);
                    var key = (overrideFunc.Name, overrideFunc.Parameters.Count);
                    // XTSE0770: Duplicate override
                    if (!overriddenFunctions.Add(key))
                        throw new XsltException(
                            $"XTSE0770: Duplicate override of function '{overrideFunc.Name.LocalName}#{overrideFunc.Parameters.Count}'",
                            GetSourceLocation(child));
                    if (packageStylesheet.Functions.TryGetValue(key, out var original))
                    {
                        // XTSE3060: Can only override public or abstract components
                        if (original.Visibility is Visibility.Private or Visibility.Final)
                            throw new XsltException(
                                $"XTSE3060: Cannot override function '{overrideFunc.Name.LocalName}' which has visibility '{original.Visibility.ToString().ToUpperInvariant()}'",
                                GetSourceLocation(child));
                        // XTSE3070: Override signature must be compatible
                        ValidateOverrideFunctionSignature(overrideFunc, original, child);
                        overrideFunc.OriginalFunction = original;
                        packageStylesheet.Functions[key] = overrideFunc;
                    }
                    else
                    {
                        throw new XsltException(
                            $"XTSE3058: Override function '{overrideFunc.Name.LocalName}' does not match any component in the used package",
                            GetSourceLocation(child));
                    }
                    break;
                }
                case "variable":
                {
                    var overrideVar = ParseVariable(child);
                    // Replace matching variable, capturing the original for xsl:original resolution
                    for (var i = 0; i < packageStylesheet.Variables.Count; i++)
                    {
                        if (packageStylesheet.Variables[i].Name.Equals(overrideVar.Name))
                        {
                            overrideVar.OriginalVariable = packageStylesheet.Variables[i];
                            // An overriding variable targets a provided component of the used
                            // package, so it is itself visible across the boundary (accept-040/046).
                            overrideVar.ProvidedByPackage = true;
                            packageStylesheet.Variables[i] = overrideVar;
                            break;
                        }
                    }
                    break;
                }
                case "param":
                {
                    var overrideParam = ParseParam(child, isGlobal: true);
                    // Replace matching parameter
                    for (var i = 0; i < packageStylesheet.Parameters.Count; i++)
                    {
                        if (packageStylesheet.Parameters[i].Name.Equals(overrideParam.Name))
                        {
                            packageStylesheet.Parameters[i] = overrideParam;
                            break;
                        }
                    }
                    break;
                }
                case "attribute-set":
                {
                    var overrideAttrSet = ParseAttributeSet(child);
                    // Capture the original for use-attribute-sets="xsl:original" resolution
                    if (packageStylesheet.AttributeSets.TryGetValue(overrideAttrSet.Name, out var origAttrSet))
                    {
                        // XTSE3060: Can only override public or abstract components — a private
                        // or final attribute-set from the used package is not overridable
                        // (override-as-004).
                        if (origAttrSet.Visibility is Visibility.Private or Visibility.Final)
                            throw new XsltException(
                                $"XTSE3060: Cannot override attribute-set '{overrideAttrSet.Name.LocalName}' which has visibility '{origAttrSet.Visibility.ToString().ToUpperInvariant()}'",
                                GetSourceLocation(child));
                        overrideAttrSet.OriginalAttributeSet = origAttrSet;
                    }
                    packageStylesheet.AttributeSets[overrideAttrSet.Name] = overrideAttrSet;
                    break;
                }
                default:
                    // XTSE0010: any other XSLT element is not a permitted child of xsl:override.
                    throw new XsltException(
                        $"XTSE0010: xsl:{child.Name.LocalName} is not permitted as a child of xsl:override",
                        GetSourceLocation(child));
            }
            _nsContext = null;
        }

        // Restore default mode
        _currentDefaultMode = savedDefaultMode;
    }


    /// <summary>
    /// Validates that an overriding template's signature is compatible with the original (XTSE3070).
    /// </summary>
    private static void ValidateOverrideSignature(
        Ast.XsltTemplate overrideTemplate, Ast.XsltTemplate original,
        QName templateName, XElement element)
    {
        var location = GetSourceLocation(element);

        // Return type must match
        if (overrideTemplate.As != null && original.As != null
            && overrideTemplate.As.ItemType != original.As.ItemType)
            throw new XsltException(
                $"XTSE3070: Override template '{templateName.LocalName}' has incompatible return type",
                location);

        // Parameter types must be compatible
        foreach (var overParam in overrideTemplate.Parameters)
        {
            var origParam = original.Parameters.FirstOrDefault(p => p.Name.Equals(overParam.Name));
            if (origParam != null && overParam.As != null && origParam.As != null)
            {
                if (overParam.As.ItemType != origParam.As.ItemType)
                    throw new XsltException(
                        $"XTSE3070: Override template '{templateName.LocalName}' parameter '{overParam.Name.LocalName}' " +
                        $"has incompatible type (override: {overParam.As.ItemType}, original: {origParam.As.ItemType})",
                        location);
            }
            // Check tunnel mismatch
            if (origParam != null && overParam.Tunnel != origParam.Tunnel)
                throw new XsltException(
                    $"XTSE3070: Override template '{templateName.LocalName}' parameter '{overParam.Name.LocalName}' " +
                    $"tunnel attribute does not match original",
                    location);
        }

        // Context-item type must be compatible — adding a type constraint where original has none is incompatible
        if (overrideTemplate.ContextItemAs != null && original.ContextItemAs == null)
            throw new XsltException(
                $"XTSE3070: Override template '{templateName.LocalName}' adds a context-item type constraint not present in original",
                location);
        if (overrideTemplate.ContextItemAs != null && original.ContextItemAs != null
            && overrideTemplate.ContextItemAs.ItemType != original.ContextItemAs.ItemType)
            throw new XsltException(
                $"XTSE3070: Override template '{templateName.LocalName}' has incompatible context-item type",
                location);

        // Context-item use must be compatible — changing use is incompatible
        // (except both Optional which is the default)
        if (overrideTemplate.ContextItemUse != original.ContextItemUse)
            throw new XsltException(
                $"XTSE3070: Override template '{templateName.LocalName}' has incompatible context-item use",
                location);
    }


    /// <summary>
    /// Validates that an overriding function's signature is compatible with the original (XTSE3070).
    /// </summary>
    private static void ValidateOverrideFunctionSignature(
        Ast.XsltFunction overrideFunc, Ast.XsltFunction original, XElement element)
    {
        var location = GetSourceLocation(element);

        // Return type must match
        if (overrideFunc.As != null && original.As != null
            && overrideFunc.As.ItemType != original.As.ItemType)
            throw new XsltException(
                $"XTSE3070: Override function '{overrideFunc.Name.LocalName}' has incompatible return type",
                location);

        // Parameter types must be compatible
        for (var i = 0; i < overrideFunc.Parameters.Count && i < original.Parameters.Count; i++)
        {
            var overParam = overrideFunc.Parameters[i];
            var origParam = original.Parameters[i];
            if (overParam.As != null && origParam.As != null
                && overParam.As.ItemType != origParam.As.ItemType)
                throw new XsltException(
                    $"XTSE3070: Override function '{overrideFunc.Name.LocalName}' parameter '{overParam.Name.LocalName}' " +
                    $"has incompatible type",
                    location);
        }

        // new-each-time (determinism) must be compatible
        var overNewEach = overrideFunc.NewEachTime ?? "yes";
        var origNewEach = original.NewEachTime ?? "yes";
        if (overNewEach != origNewEach)
            throw new XsltException(
                $"XTSE3070: Override function '{overrideFunc.Name.LocalName}' has incompatible determinism " +
                $"(new-each-time: override='{overNewEach}', original='{origNewEach}')",
                location);
    }


    /// <summary>
    /// Validates a single xsl:accept declaration: named components exist (XTSE3030), the
    /// visibility change is compatible (XTSE3040), and no accepted name is also overridden
    /// (XTSE3051). Visibility application is done separately, with cross-declaration
    /// name-specificity resolution, in <see cref="ApplyAcceptVisibilities"/>.
    /// </summary>
    private static void ValidateAccept(XElement acceptElement, XsltStylesheet packageStylesheet,
        HashSet<QName>? overriddenTemplateNames = null, HashSet<(QName, int)>? overriddenFunctionKeys = null)
    {
        _ = overriddenFunctionKeys;
        var component = acceptElement.Attribute("component")?.Value;
        var names = acceptElement.Attribute("names")?.Value;
        var visibilityStr = acceptElement.Attribute("visibility")?.Value;
        if (component == null || names == null || visibilityStr == null) return;

        var visibility = ParseVisibility(visibilityStr);
        var location = GetSourceLocation(acceptElement);

        foreach (var token in names.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            // XTSE3032: xsl:accept component="*" may only be combined with a wildcard name.
            if (component == "*" && !IsAcceptWildcard(token))
                throw new XsltException(
                    $"XTSE3032: xsl:accept with component=\"*\" must use a wildcard name, not '{token}'",
                    location);

            // Wildcards (in any of the *, prefix:*, *:local, Q{uri}* forms) always match — skip validation
            if (IsAcceptWildcard(token)) continue;

            // XTSE3051: accept must not name a component also in xsl:override
            switch (component)
            {
                case "template":
                {
                    var qname = ResolveAcceptTokenQName(token, acceptElement);
                    if (overriddenTemplateNames != null && overriddenTemplateNames.Contains(qname))
                        throw new XsltException(
                            $"XTSE3051: Component '{token}' in xsl:accept is also declared in xsl:override",
                            location);
                    // XTSE3030: must match an existing component
                    if (!packageStylesheet.NamedTemplates.TryGetValue(qname, out var tmpl))
                        throw new XsltException(
                            $"XTSE3030: xsl:accept names template '{token}' which does not exist in the used package",
                            location);
                    // XTSE3040: visibility change must be compatible
                    // Private → anything other than private/hidden is incompatible
                    // Final → public is incompatible (final can't become overridable)
                    if ((tmpl.Visibility == Visibility.Private && visibility is not (Visibility.Private or Visibility.Hidden))
                        || (tmpl.Visibility == Visibility.Final && visibility == Visibility.Public))
                        throw new XsltException(
                            $"XTSE3040: Cannot change visibility of {tmpl.Visibility.ToString().ToUpperInvariant()} template '{token}' to {visibility.ToString().ToUpperInvariant()}",
                            location);
                    break;
                }
                case "function":
                {
                    // Parse function name and optional arity: "p:f1#0" → name="p:f1", arity=0
                    var funcToken = token;
                    var arity = -1;
                    var hashIdx = token.IndexOf('#', StringComparison.Ordinal);
                    if (hashIdx >= 0)
                    {
                        funcToken = token[..hashIdx];
                        _ = int.TryParse(token[(hashIdx + 1)..], out arity);
                    }
                    var funcName = ResolveAcceptTokenQName(funcToken, acceptElement);
                    foreach (var (fkey, func) in packageStylesheet.Functions)
                    {
                        if (fkey.Name.Equals(funcName) && (arity < 0 || fkey.Arity == arity))
                        {
                            if ((func.Visibility == Visibility.Private && visibility is not (Visibility.Private or Visibility.Hidden))
                                || (func.Visibility == Visibility.Final && visibility == Visibility.Public))
                                throw new XsltException(
                                    $"XTSE3040: Cannot change visibility of {func.Visibility.ToString().ToUpperInvariant()} function '{token}' to {visibility.ToString().ToUpperInvariant()}",
                                    location);
                        }
                    }
                    break;
                }
                case "variable":
                {
                    var qname = ResolveAcceptTokenQName(token, acceptElement);
                    if (!packageStylesheet.Variables.Any(v => v.Name.Equals(qname)))
                        throw new XsltException(
                            $"XTSE3030: xsl:accept names variable '{token}' which does not exist in the used package",
                            location);
                    break;
                }
            }
        }
    }


    /// <summary>
    /// Applies xsl:accept visibility changes across all xsl:accept declarations of a single
    /// xsl:use-package, resolving conflicts by name specificity (XSLT 3.0 §3.5.2): an explicit
    /// QName is more specific than a partial wildcard (prefix:* / *:local / Q{uri}*), which is
    /// more specific than "*". Among equally-specific matches, the last in document order wins.
    /// </summary>
    private static void ApplyAcceptVisibilities(List<XElement> acceptElements, XsltStylesheet package)
    {
        var rules = new List<AcceptRule>();
        var order = 0;
        foreach (var elem in acceptElements)
        {
            var component = elem.Attribute("component")?.Value;
            var names = elem.Attribute("names")?.Value;
            var visStr = elem.Attribute("visibility")?.Value;
            if (component != null && names != null && visStr != null)
            {
                var vis = ParseVisibility(visStr);
                foreach (var token in names.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries))
                    rules.Add(new AcceptRule(elem, component, token, vis, order));
            }
            order++;
        }

        foreach (var name in package.NamedTemplates.Keys.ToList())
            if (TryResolveAcceptVisibility(rules, "template", name, package.NamedTemplates[name].Visibility, out var vis))
                package.NamedTemplates[name] = CloneTemplateWithVisibility(package.NamedTemplates[name], vis);

        foreach (var key in package.Functions.Keys.ToList())
            if (TryResolveAcceptVisibility(rules, "function", key.Name, package.Functions[key].Visibility, out var vis, key.Arity))
                package.Functions[key] = CloneFunctionWithVisibility(package.Functions[key], vis);

        for (var i = 0; i < package.Variables.Count; i++)
            if (TryResolveAcceptVisibility(rules, "variable", package.Variables[i].Name, package.Variables[i].Visibility, out var vis))
                package.Variables[i] = CloneVariableWithVisibility(package.Variables[i], vis);

        foreach (var name in package.AttributeSets.Keys.ToList())
            if (TryResolveAcceptVisibility(rules, "attribute-set", name, package.AttributeSets[name].Visibility, out var vis))
                package.AttributeSets[name] = CloneAttributeSetWithVisibility(package.AttributeSets[name], vis);

        foreach (var name in package.Modes.Keys.ToList())
            if (TryResolveAcceptVisibility(rules, "mode", name, package.Modes[name].Visibility, out var vis))
            {
                var mode = package.Modes[name];
                package.Modes[name] = new Ast.XsltMode
                {
                    Name = mode.Name, Streamable = mode.Streamable,
                    OnNoMatch = mode.OnNoMatch, OnMultipleMatch = mode.OnMultipleMatch,
                    UseAllAccumulators = mode.UseAllAccumulators,
                    UseAccumulatorNames = mode.UseAccumulatorNames,
                    Visibility = vis,
                    VisibilityAttr = VisibilityToAttr(vis),
                    TypedValueWarnings = mode.TypedValueWarnings, Typed = mode.Typed,
                    UseAccumulatorsAttr = mode.UseAccumulatorsAttr,
                };
            }
    }


    private static bool IsAcceptWildcard(string token) => AcceptTokenSpecificity(token) < 3;


    private static int AcceptTokenSpecificity(string token)
    {
        if (token == "*") return 1;
        if (token.StartsWith("Q{", StringComparison.Ordinal) && token.EndsWith("}*", StringComparison.Ordinal)) return 2;
        if (token.EndsWith(":*", StringComparison.Ordinal)) return 2;
        if (token.StartsWith("*:", StringComparison.Ordinal)) return 2;
        return 3;
    }


    /// <summary>
    /// Resolves an explicit (non-wildcard) xsl:accept name token to a QName, honouring an
    /// optional prefix or Q{uri}local EQName form against the accept element's namespaces.
    /// </summary>
    private static QName ResolveAcceptTokenQName(string token, XElement element)
    {
        if (token.StartsWith("Q{", StringComparison.Ordinal))
        {
            var close = token.IndexOf('}', StringComparison.Ordinal);
            if (close > 0)
            {
                var uri = token[2..close];
                var local = token[(close + 1)..];
                var nsId = uri.Length == 0 ? NamespaceId.None : ResolveNamespaceUri(uri);
                return new QName(nsId, local);
            }
        }
        var colon = token.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            var prefix = token[..colon];
            var local = token[(colon + 1)..];
            var nsUri = element.GetNamespaceOfPrefix(prefix)?.NamespaceName;
            var nsId = nsUri != null ? ResolveNamespaceUri(nsUri) : NamespaceId.None;
            return new QName(nsId, local, prefix);
        }
        return new QName(NamespaceId.None, token);
    }


    /// <summary>
    /// Tests whether a component QName matches an xsl:accept name token, supporting the
    /// *, prefix:*, *:local, Q{uri}*, Q{uri}local, prefix:local and NCName forms.
    /// </summary>
    private static bool AcceptTokenMatches(QName name, string token, XElement element)
    {
        if (token == "*") return true;

        if (token.StartsWith("Q{", StringComparison.Ordinal))
        {
            var close = token.IndexOf('}', StringComparison.Ordinal);
            if (close < 0) return false;
            var uri = token[2..close];
            var local = token[(close + 1)..];
            var nsMatch = uri.Length == 0
                ? name.Namespace == NamespaceId.None
                : name.Namespace == ResolveNamespaceUri(uri);
            if (!nsMatch) return false;
            return local == "*" || name.LocalName == local;
        }

        if (token.EndsWith(":*", StringComparison.Ordinal))
        {
            var prefix = token[..^2];
            var nsUri = element.GetNamespaceOfPrefix(prefix)?.NamespaceName;
            if (nsUri != null)
                return name.Namespace == ResolveNamespaceUri(nsUri);
            return name.Prefix == prefix;
        }

        if (token.StartsWith("*:", StringComparison.Ordinal))
            return name.LocalName == token[2..];

        var colon = token.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            var prefix = token[..colon];
            var local = token[(colon + 1)..];
            var nsUri = element.GetNamespaceOfPrefix(prefix)?.NamespaceName;
            var nsId = nsUri != null ? ResolveNamespaceUri(nsUri) : NamespaceId.None;
            return name.Namespace == nsId && name.LocalName == local;
        }

        return name.Namespace == NamespaceId.None && name.LocalName == token;
    }


    /// <summary>
    /// Merges visible (public/final) components from a package into the consuming stylesheet.
    /// Uses the same TryAdd pattern as import merging — consuming stylesheet declarations take precedence.
    /// </summary>
    /// <summary>
    /// Applies xsl:expose declarations to change visibility of components within the package.
    /// Per XSLT 3.0 §3.6.3, expose changes the visibility of the package's own components
    /// based on component type and name pattern matching.
    /// </summary>
    private void ApplyExposeDeclarations(XsltStylesheet stylesheet)
    {
        foreach (var expose in stylesheet.ExposeDeclarations)
        {
            if (expose.Component == null || expose.Names == null) continue;
            var nameTokens = expose.Names.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
            var visibility = expose.Visibility;

            foreach (var token in nameTokens)
            {
                var isWildcard = token == "*" || token.EndsWith(":*", StringComparison.Ordinal);

                // Determine which component types to process
                var components = expose.Component == "*"
                    ? new[] { "template", "function", "variable", "attribute-set", "mode" }
                    : new[] { expose.Component };

                foreach (var comp in components)
                {
                    switch (comp)
                    {
                        case "template":
                            foreach (var (name, tmpl) in stylesheet.NamedTemplates)
                            {
                                if (MatchesExposePattern(name, token, isWildcard, expose.Element))
                                    stylesheet.NamedTemplates[name] = CloneTemplateWithVisibility(tmpl, visibility);
                            }
                            break;
                        case "function":
                            foreach (var (key, func) in stylesheet.Functions)
                            {
                                if (MatchesExposePattern(key.Name, token, isWildcard, expose.Element))
                                    stylesheet.Functions[key] = CloneFunctionWithVisibility(func, visibility);
                            }
                            break;
                        case "variable":
                            for (var i = 0; i < stylesheet.Variables.Count; i++)
                            {
                                if (MatchesExposePattern(stylesheet.Variables[i].Name, token, isWildcard, expose.Element))
                                {
                                    var exposedVar = CloneVariableWithVisibility(stylesheet.Variables[i], visibility);
                                    // Record that xsl:expose explicitly set this variable's boundary
                                    // visibility, so the use-package capture treats an exposed
                                    // public/final/abstract variable as provided even though the
                                    // clone leaves VisibilityAttr null (indistinguishable from the
                                    // parser's public default otherwise).
                                    exposedVar.ProvidedByPackage =
                                        visibility is Visibility.Public or Visibility.Final or Visibility.Abstract;
                                    stylesheet.Variables[i] = exposedVar;
                                }
                            }
                            break;
                        case "attribute-set":
                            foreach (var (name, attrSet) in stylesheet.AttributeSets)
                            {
                                if (MatchesExposePattern(name, token, isWildcard, expose.Element))
                                    stylesheet.AttributeSets[name] = CloneAttributeSetWithVisibility(attrSet, visibility);
                            }
                            break;
                        case "mode":
                            foreach (var (name, mode) in stylesheet.Modes)
                            {
                                if (MatchesExposePattern(name, token, isWildcard, expose.Element))
                                {
                                    stylesheet.Modes[name] = new Ast.XsltMode
                                    {
                                        Name = mode.Name,
                                        Streamable = mode.Streamable,
                                        OnNoMatch = mode.OnNoMatch,
                                        OnMultipleMatch = mode.OnMultipleMatch,
                                        UseAllAccumulators = mode.UseAllAccumulators,
                                        UseAccumulatorNames = mode.UseAccumulatorNames,
                                        Visibility = visibility,
                                        VisibilityAttr = expose.Element?.Attribute("visibility")?.Value,
                                        TypedValueWarnings = mode.TypedValueWarnings,
                                        Typed = mode.Typed,
                                        UseAccumulatorsAttr = mode.UseAccumulatorsAttr,
                                    };
                                    if (visibility == Visibility.Private)
                                        stylesheet.ExplicitlyExposedPrivateModes.Add(name);
                                }
                            }
                            // Implicit modes (declared-modes="false") never appear in
                            // stylesheet.Modes, but a wildcard/name expose still applies to
                            // them. When such a mode is explicitly exposed as private, record
                            // it so the initial-mode eligibility check (XTDE0045) can reject
                            // it — an implicitly-private mode remains eligible, but an
                            // explicitly-exposed-private one does not. Enumerate the modes
                            // named on template rules (which cover implicit modes not present
                            // in stylesheet.Modes). See W3C package-001j.
                            if (visibility == Visibility.Private)
                            {
                                foreach (var rule in stylesheet.Templates)
                                {
                                    foreach (var modeRef in rule.Modes)
                                    {
                                        if (modeRef.Equals(TemplateIndex.DefaultModeSentinel)
                                            || modeRef.Equals(TemplateIndex.AllModeSentinel))
                                            continue;
                                        if (!stylesheet.Modes.ContainsKey(modeRef)
                                            && MatchesExposePattern(modeRef, token, isWildcard, expose.Element))
                                            stylesheet.ExplicitlyExposedPrivateModes.Add(modeRef);
                                    }
                                }
                            }
                            break;
                    }
                }
            }
        }
    }


    private static void CheckStripSpaceConflicts(XsltStylesheet stylesheet)
    {
        if (stylesheet.StripSpace.Count == 0 || stylesheet.PreserveSpace.Count == 0)
            return;

        foreach (var strip in stylesheet.StripSpace)
        {
            foreach (var preserve in stylesheet.PreserveSpace)
            {
                // Check for conflict: same local name and compatible namespace
                if (strip.LocalName == preserve.LocalName &&
                    (strip.NamespaceUri ?? "") == (preserve.NamespaceUri ?? ""))
                {
                    throw new XsltException(
                        $"XTSE0270: Conflicting strip-space and preserve-space declarations for element '{strip}'");
                }
            }
        }
    }


    /// <summary>
    /// XTSE1590: Validates that use-character-maps on xsl:output reference defined character maps.
    /// Called at top level after all imports are merged.
    /// </summary>
    private static void ValidateOutputCharacterMapReferences(XsltStylesheet stylesheet)
    {
        foreach (var output in stylesheet.Outputs)
        {
            foreach (var usedName in output.UseCharacterMaps)
            {
                if (!stylesheet.CharacterMaps.ContainsKey(usedName))
                    throw new XsltException($"XTSE1590: xsl:output references undefined character map '{usedName.LocalName}'");
            }
        }
    }


    private static void ValidateFunctionCallsInExpression(
        PhoenixmlDb.XQuery.Ast.XQueryExpression expr, XsltStylesheet stylesheet,
        PhoenixmlDb.XQuery.Functions.FunctionLibrary lib,
        HashSet<NamespaceId> wellKnownNamespaces)
    {
        switch (expr)
        {
            case PhoenixmlDb.XQuery.Ast.FunctionCallExpression fc:
                ValidateSingleFunctionCall(fc, stylesheet, lib, wellKnownNamespaces);
                foreach (var arg in fc.Arguments)
                    ValidateFunctionCallsInExpression(arg, stylesheet, lib, wellKnownNamespaces);
                break;
            case PhoenixmlDb.XQuery.Ast.BinaryExpression be:
                ValidateFunctionCallsInExpression(be.Left, stylesheet, lib, wellKnownNamespaces);
                ValidateFunctionCallsInExpression(be.Right, stylesheet, lib, wellKnownNamespaces);
                break;
            case PhoenixmlDb.XQuery.Ast.UnaryExpression ue:
                ValidateFunctionCallsInExpression(ue.Operand, stylesheet, lib, wellKnownNamespaces);
                break;
            case PhoenixmlDb.XQuery.Ast.PathExpression pe:
                if (pe.InitialExpression != null)
                    ValidateFunctionCallsInExpression(pe.InitialExpression, stylesheet, lib, wellKnownNamespaces);
                foreach (var step in pe.Steps)
                    ValidateFunctionCallsInExpression(step, stylesheet, lib, wellKnownNamespaces);
                break;
            case PhoenixmlDb.XQuery.Ast.StepExpression se:
                foreach (var pred in se.Predicates)
                    ValidateFunctionCallsInExpression(pred, stylesheet, lib, wellKnownNamespaces);
                break;
            case PhoenixmlDb.XQuery.Ast.FilterExpression fe:
                ValidateFunctionCallsInExpression(fe.Primary, stylesheet, lib, wellKnownNamespaces);
                foreach (var pred in fe.Predicates)
                    ValidateFunctionCallsInExpression(pred, stylesheet, lib, wellKnownNamespaces);
                break;
            case PhoenixmlDb.XQuery.Ast.IfExpression ie:
                ValidateFunctionCallsInExpression(ie.Condition, stylesheet, lib, wellKnownNamespaces);
                ValidateFunctionCallsInExpression(ie.Then, stylesheet, lib, wellKnownNamespaces);
                if (ie.Else != null)
                    ValidateFunctionCallsInExpression(ie.Else, stylesheet, lib, wellKnownNamespaces);
                break;
            case PhoenixmlDb.XQuery.Ast.SequenceExpression seq:
                foreach (var item in seq.Items)
                    ValidateFunctionCallsInExpression(item, stylesheet, lib, wellKnownNamespaces);
                break;
        }
    }


    /// <summary>
    /// Validates character map references: XTSE1590 (undefined) and XTSE1600 (circular).
    /// </summary>
    private static void ValidateCharacterMapReferences(XsltStylesheet stylesheet)
    {
        foreach (var (name, charMap) in stylesheet.CharacterMaps)
        {
            foreach (var usedName in charMap.UseCharacterMaps)
            {
                if (!stylesheet.CharacterMaps.ContainsKey(usedName))
                    throw new XsltException($"XTSE1590: Character map '{name.LocalName}' references undefined character map '{usedName.LocalName}'");
            }
        }

        // XTSE1600: Check for circular character map references
        foreach (var (name, _) in stylesheet.CharacterMaps)
        {
            var visited = new HashSet<QName>();
            var queue = new Queue<QName>();
            queue.Enqueue(name);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!stylesheet.CharacterMaps.TryGetValue(current, out var currentMap))
                    continue;
                foreach (var usedName in currentMap.UseCharacterMaps)
                {
                    if (usedName == name)
                        throw new XsltException($"XTSE1600: Character map '{name.LocalName}' directly or indirectly references itself");
                    if (visited.Add(usedName))
                        queue.Enqueue(usedName);
                }
            }
        }
    }


    /// <summary>
    /// Validates all decimal formats after merging is complete.
    /// Checks for: unresolved same-precedence conflicts (XTSE1290) and
    /// duplicate character roles (XTSE1300).
    /// </summary>
    private static void ValidateDecimalFormats(XsltStylesheet stylesheet)
    {
        foreach (var (_, df) in stylesheet.DecimalFormats)
        {
            // Check for unresolved same-precedence conflicts (XTSE1290)
            if (df.HasConflict)
                throw new XsltException(df.ConflictDescription ?? "XTSE1290: Conflicting decimal-format declarations");

            // Check that no two properties share the same character (XTSE1300)
            var roles = new Dictionary<string, string>();
            void CheckRole(string c, string role)
            {
                if (roles.TryGetValue(c, out var existingRole))
                    throw new XsltException(
                        $"XTSE1300: Character '{c}' cannot be used as both {existingRole} and {role}");
                roles[c] = role;
            }
            CheckRole(df.DecimalSeparator, "decimal-separator");
            CheckRole(df.GroupingSeparator, "grouping-separator");
            CheckRole(df.Percent, "percent");
            CheckRole(df.PerMille, "per-mille");
            CheckRole(df.ZeroDigit, "zero-digit");
            CheckRole(df.Digit, "digit");
            CheckRole(df.PatternSeparator, "pattern-separator");
            CheckRole(df.ExponentSeparator, "exponent-separator");
        }
    }


    /// <summary>
    /// Recursively collect outputs from imported stylesheets, assigning import precedence levels.
    /// </summary>
    private static void CollectImportedOutputs(XsltStylesheet target, XsltStylesheet source, int precedenceLevel)
    {
        foreach (var imported in source.Imports)
        {
            foreach (var output in imported.Outputs)
            {
                output.ImportPrecedence = precedenceLevel;
                target.Outputs.Add(output);
            }
            // Recursively collect from nested imports at even lower precedence
            CollectImportedOutputs(target, imported, precedenceLevel + 1);
        }
    }


    private XsltFunction ParseFunction(XElement element)
    {
        var location = GetSourceLocation(element);
        var nameValue = element.Attribute("name")?.Value
            ?? throw new XsltException("XTSE0010: xsl:function must have a name attribute", location);

        // Handle EQName syntax: Q{uri}local
        QName name;
        if (nameValue.StartsWith("Q{", StringComparison.Ordinal))
        {
            var closeBrace = nameValue.IndexOf('}', 2);
            if (closeBrace < 0)
                throw new XsltException($"XTSE0020: Invalid EQName '{nameValue}' for name attribute", location);
            var nsUri = nameValue[2..closeBrace];
            var localName = nameValue[(closeBrace + 1)..];
            if (string.IsNullOrEmpty(localName))
                throw new XsltException($"XTSE0020: Invalid EQName '{nameValue}': missing local name", location);
            var nsId = ResolveNamespaceUri(nsUri);
            name = new QName(nsId, localName);
        }
        else
        {
            // Validate QName syntax before parsing
            if (nameValue.Any(c => !char.IsLetterOrDigit(c) && c != ':' && c != '_' && c != '-' && c != '.'))
                throw new XsltException($"XTSE0020: Invalid QName '{nameValue}' for name attribute", location);
            name = ParseQName(nameValue, element);
        }

        // XTSE0740: Function name must be in a namespace
        if (name.Namespace == NamespaceId.None)
            throw new XsltException("XTSE0740: The name of a stylesheet function must have a non-null namespace URI", location);

        // XTSE0080: Function name must not be in a reserved namespace
        if (name.Namespace == NamespaceId.Xslt
            || name.Namespace == new NamespaceId(2) // xs (XMLSchema)
            || name.Namespace == NamespaceId.Fn
            || name.Namespace == NamespaceId.Map
            || name.Namespace == NamespaceId.Array
            || name.Namespace == NamespaceId.Math)
            throw new XsltException($"XTSE0080: The name of stylesheet function '{name}' is in a reserved namespace", location);

        // XTSE0020: Validate attribute values
        var overrideAttr = element.Attribute("override");
        if (overrideAttr != null && ParseYesNo(overrideAttr) == null)
            throw new XsltException($"XTSE0020: Invalid value '{overrideAttr.Value}' for override attribute", location);

        var overrideExtAttr = element.Attribute("override-extension-function");
        if (overrideExtAttr != null && ParseYesNo(overrideExtAttr) == null)
            throw new XsltException($"XTSE0020: Invalid value '{overrideExtAttr.Value}' for override-extension-function attribute", location);

        // XTSE0020: if both override and override-extension-function are present, they must agree
        if (overrideAttr != null && overrideExtAttr != null)
        {
            var overrideVal = ParseYesNo(overrideAttr) ?? false;
            var overrideExtVal = ParseYesNo(overrideExtAttr) ?? false;
            if (overrideVal != overrideExtVal)
                throw new XsltException("XTSE0020: The 'override' and 'override-extension-function' attributes have conflicting values on xsl:function", location);
        }

        var newEachTimeAttr = element.Attribute("new-each-time");
        if (newEachTimeAttr != null)
        {
            var val = newEachTimeAttr.Value.Trim();
            if (val != "yes" && val != "no" && val != "maybe")
                throw new XsltException($"XTSE0020: Invalid value '{newEachTimeAttr.Value}' for new-each-time attribute: must be 'yes', 'no', or 'maybe'", location);
        }

        var cacheAttr = element.Attribute("cache");
        if (cacheAttr != null && ParseYesNo(cacheAttr) == null)
            throw new XsltException($"XTSE0020: Invalid value '{cacheAttr.Value}' for cache attribute", location);

        var asAttr = element.Attribute("as");

        var parameters = new List<XsltParam>();
        var instructions = new List<XsltInstruction>();

        // Parse function body using Nodes() (not Elements()) to capture literal text
        // between instructions — e.g., <xsl:text/>[<xsl:value-of/>]<xsl:text/>
        var expandText = IsExpandTextActive(element);
        var preserveSpace = IsXmlSpacePreserve(element);
        var bodyNodes = element.Nodes().ToList();
        var pastParams = false;
        for (var ni = 0; ni < bodyNodes.Count; ni++)
        {
            switch (bodyNodes[ni])
            {
                case XElement child:
                    if (!ShouldIncludeElement(child)) continue;
                    if (!pastParams && child.Name == XsltNs + "param")
                    {
                        // XTSE0020: required='no' is not allowed on function params (params are always required)
                        var requiredAttr = child.Attribute("required");
                        if (requiredAttr != null && requiredAttr.Value.Trim() == "no")
                            throw new XsltException("XTSE0020: The required attribute of xsl:param within xsl:function must not have the value 'no'",
                                GetSourceLocation(child));
                        // XTSE0760: Function params must not have default values (no select, no content)
                        if (child.Attribute("select") != null)
                            throw new XsltException("XTSE0760: An xsl:param element within xsl:function must not have a select attribute",
                                GetSourceLocation(child));
                        if (child.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
                            throw new XsltException("XTSE0760: An xsl:param element within xsl:function must not have non-empty content",
                                GetSourceLocation(child));
                        parameters.Add(ParseParam(child, allowTunnel: false));
                        continue;
                    }
                    pastParams = true;
                    instructions.Add(ParseInstruction(child));
                    break;
                case XText:
                    // Collect contiguous text run (same as ParseSequenceConstructor)
                    var hasNonWhitespace = false;
                    var sb = new System.Text.StringBuilder();
                    while (ni < bodyNodes.Count && bodyNodes[ni] is XText or XComment or XProcessingInstruction)
                    {
                        if (bodyNodes[ni] is XText textNode)
                        {
                            sb.Append(textNode.Value);
                            if (!hasNonWhitespace && !IsXmlWhitespaceOnly(textNode.Value))
                                hasNonWhitespace = true;
                        }
                        ni++;
                    }
                    ni--;
                    if (hasNonWhitespace || preserveSpace)
                    {
                        pastParams = true;
                        // Use XsltText (not XsltLiteralText) for function body literal text.
                        // XsltText routes through WriteTextItem, which uses the sequence
                        // accumulator in function bodies. This ensures literal text like
                        // "[" between xsl:text and xsl:value-of is captured alongside other
                        // text items and properly merged into the function result.
                        var textValue = sb.ToString();
                        if (expandText && textValue.Contains('{', StringComparison.Ordinal))
                            instructions.Add(CreateTextInstruction(textValue, expandText, element));
                        else
                            instructions.Add(new XsltText { Value = textValue });
                    }
                    break;
            }
        }

        // XTSE0580: No two sibling xsl:param elements may have the same expanded QName
        var paramNames = new HashSet<string>();
        foreach (var p in parameters)
        {
            var paramKey = $"{p.Name.Namespace}:{p.Name.LocalName}";
            if (!paramNames.Add(paramKey))
                throw new XsltException($"XTSE0580: Duplicate parameter name '{p.Name.LocalName}' in xsl:function", location);
        }

        var body = new XsltSequenceConstructor { Instructions = instructions };

        // XTSE3430: Check function streamability if declared
        var streamabilityAttr = element.Attribute("streamability");
        if (streamabilityAttr != null)
        {
            var streamability = streamabilityAttr.Value.Trim();
            if (streamability is "absorbing" or "filter" or "inspection" or "shallow-descent"
                or "deep-descent" or "ascent")
            {
                StreamabilityChecker.CheckStreamableFunctionBody(body, streamability, parameters, location, name);
            }
        }

        var streamabilityValue = streamabilityAttr?.Value.Trim();
        if (streamabilityValue is not ("absorbing" or "filter" or "inspection"
            or "shallow-descent" or "deep-descent" or "ascent"))
            streamabilityValue = null;

        return new XsltFunction
        {
            Name = name,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            Parameters = parameters,
            Body = body,
            Override = ParseYesNo(overrideAttr) ?? true,
            Visibility = ParseVisibility(element.Attribute("visibility")?.Value),
            VisibilityAttr = element.Attribute("visibility")?.Value,
            Cache = ParseYesNo(cacheAttr) ?? false,
            NewEachTime = newEachTimeAttr?.Value?.Trim(),
            Streamability = streamabilityValue,
            BaseUri = ResolveEffectiveBaseUri(element)
        };
    }


    private XsltKey ParseKey(XElement element, string? stylesheetDefaultCollation = null)
    {
        // XTSE0090: Validate no unknown attributes
        ValidateAllowedAttributes(element, GetSourceLocation(element), "name", "match", "use", "composite", "collation", "default-collation");

        // XTSE0010: name and match are both REQUIRED on xsl:key. These were dereferenced with
        // `!`, so omitting either produced a NullReferenceException instead of a diagnosis —
        // the stylesheet author got "Object reference not set to an instance of an object."
        var nameValue = element.Attribute("name")?.Value
            ?? throw new XsltException("XTSE0010: xsl:key requires a 'name' attribute",
                GetSourceLocation(element));
        var matchValue = element.Attribute("match")?.Value
            ?? throw new XsltException("XTSE0010: xsl:key requires a 'match' attribute",
                GetSourceLocation(element));

        // XTSE0020: Validate name is a valid QName
        ValidateQNameValue(nameValue, "name", GetSourceLocation(element));

        var name = ParseQName(nameValue, element);
        var match = ParsePattern(matchValue, element);
        var useAttr = element.Attribute("use");
        var collationAttr = element.Attribute("collation");
        var compositeAttr = element.Attribute("composite");
        var defaultCollationAttr = element.Attribute("default-collation");

        // XTSE1210: xsl:key collation must be a recognized collation URI
        if (collationAttr != null)
            ValidateCollationList(collationAttr.Value, GetSourceLocation(element), "XTSE1210");

        // XTSE1205: xsl:key must have either use attribute or non-empty content, not both
        var hasContent = element.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value)));
        if (useAttr != null && hasContent)
            throw new XsltException("XTSE1205: An xsl:key element must not have both a use attribute and non-empty content",
                GetSourceLocation(element));
        if (useAttr == null && !hasContent)
            throw new XsltException("XTSE1205: An xsl:key element must have either a use attribute or non-empty content",
                GetSourceLocation(element));

        // Resolve effective collation: explicit collation > default-collation on element > stylesheet default collation
        var effectiveCollation = collationAttr?.Value
            ?? ResolveDefaultCollation(defaultCollationAttr?.Value)
            ?? stylesheetDefaultCollation;

        return new XsltKey
        {
            Name = name,
            Match = match,
            Use = useAttr != null ? ParseExpr(useAttr.Value, useAttr) : null,
            UseContent = useAttr == null && element.HasElements
                ? ParseSequenceConstructor(element)
                : null,
            Collation = effectiveCollation,
            Composite = ParseYesNo(compositeAttr) ?? false
        };
    }


    private XsltOutput ParseOutput(XElement element, XsltStylesheet stylesheet)
    {
        // XSLT 3.0 §26.1: xsl:output may reference an external serialization-parameters document
        // via the parameter-document attribute. Merge its parameters onto the element (attributes
        // written directly on xsl:output take precedence) and capture any inline character maps it
        // declares so they can be applied at serialization time (output-0706/0720/0721/0722).
        Dictionary<int, string>? paramDocCharMap = null;
        var paramDocAttr = element.Attribute("parameter-document");
        if (paramDocAttr != null)
            paramDocCharMap = MergeSerializationParameterDocument(element, paramDocAttr.Value);

        // XTSE1570: Validate method attribute
        var methodAttr = element.Attribute("method");
        if (methodAttr != null)
        {
            var methodValue = methodAttr.Value.Trim();
            if (methodValue.Contains(':', StringComparison.Ordinal))
            {
                // Prefixed QName for extension method — validate it's a valid QName
                var parts = methodValue.Split(':', 2);
                if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1])
                    || methodValue.Contains("::", StringComparison.Ordinal))
                    throw new XsltException($"XTSE1570: Invalid output method '{methodValue}': must be a valid EQName");
                // Validate the prefix is bound
                if (element.GetNamespaceOfPrefix(parts[0]) == null)
                    throw new XsltException($"XTSE1570: Output method '{methodValue}' uses undeclared prefix '{parts[0]}'");
            }
            else if (methodValue is not ("xml" or "html" or "xhtml" or "text" or "json" or "adaptive"))
            {
                throw new XsltException($"XTSE1570: Invalid output method '{methodValue}': unprefixed method must be one of xml, html, xhtml, text, json, or adaptive");
            }
        }

        var output = new XsltOutput
        {
            Name = element.Attribute("name") != null
                ? ParseQName(element.Attribute("name")!.Value, element)
                : null,
            // The method value is a whitespace-collapsed token: leading/trailing whitespace is
            // stripped before matching (output-0221 uses method=" xhtml "). Validation above already
            // trims; the actual mapping must trim too or a padded value silently degrades to xml.
            Method = element.Attribute("method")?.Value.Trim() switch
            {
                "xml" => OutputMethod.Xml,
                "html" => OutputMethod.Html,
                "xhtml" => OutputMethod.Xhtml,
                "text" => OutputMethod.Text,
                "json" => OutputMethod.Json,
                "adaptive" => OutputMethod.Adaptive,
                "csv" => OutputMethod.Csv,
                null => null,
                _ => OutputMethod.Xml
            },
            Version = element.Attribute("version")?.Value,
            Encoding = element.Attribute("encoding")?.Value,
            OmitXmlDeclaration = ParseYesNo(element.Attribute("omit-xml-declaration")),
            Standalone = ParseYesNo(element.Attribute("standalone")),
            DoctypePublic = element.Attribute("doctype-public")?.Value,
            DoctypeSystem = element.Attribute("doctype-system")?.Value,
            Indent = ParseYesNo(element.Attribute("indent")),
            MediaType = element.Attribute("media-type")?.Value,
            ItemSeparator = element.Attribute("item-separator")?.Value,
            NormalizationForm = element.Attribute("normalization-form")?.Value,
            UndeclarePrefixes = ParseYesNo(element.Attribute("undeclare-prefixes")),
            IncludeContentType = ParseYesNo(element.Attribute("include-content-type")),
            EscapeUriAttributes = ParseYesNo(element.Attribute("escape-uri-attributes")),
            HtmlVersion = element.Attribute("html-version")?.Value,
            BuildTree = element.Attribute("build-tree")?.Value,
            AllowDuplicateNames = ParseYesNo(element.Attribute("allow-duplicate-names")),
            ByteOrderMark = ParseYesNo(element.Attribute("byte-order-mark")),
            JsonNodeOutputMethod = element.Attribute("json-node-output-method")?.Value,
        };

        // Validate html-version: must be a valid decimal number (XTSE0020)
        if (output.HtmlVersion != null &&
            !decimal.TryParse(output.HtmlVersion, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            throw new XsltException($"XTSE0020: Invalid html-version value '{output.HtmlVersion}': must be a decimal number");
        }

        // XTSE0020: reject invalid values for the yes-or-no serialization attributes.
        // The serialization spec ties these to SEPM0016 at serialization time; XSLT
        // catches them statically as XTSE0020. Only "yes"/"no"/"true"/"false"/"1"/"0"
        // (case-sensitive, per xsl:yes-or-no) are permitted, so uppercase values such as
        // "TRUE"/"YES" are errors rather than being silently ignored.
        ValidateYesNoOutputAttribute(element, "omit-xml-declaration");
        ValidateYesNoOutputAttribute(element, "indent");
        ValidateYesNoOutputAttribute(element, "include-content-type");
        ValidateYesNoOutputAttribute(element, "allow-duplicate-names");
        ValidateYesNoOutputAttribute(element, "byte-order-mark");
        ValidateYesNoOutputAttribute(element, "escape-uri-attributes");
        ValidateYesNoOutputAttribute(element, "undeclare-prefixes");

        // standalone additionally permits the value "omit".
        var standaloneAttr = element.Attribute("standalone");
        if (standaloneAttr != null)
        {
            var v = standaloneAttr.Value.Trim();
            if (v is not ("yes" or "no" or "true" or "false" or "1" or "0" or "omit"))
                throw new XsltException($"XTSE0020: Invalid standalone value '{standaloneAttr.Value}': must be yes, no, true, false, 1, 0, or omit");
        }

        // doctype-public must be a valid XML PubidLiteral (XTSE0020).
        var doctypePublicAttr = element.Attribute("doctype-public");
        if (doctypePublicAttr != null && !IsValidPublicId(doctypePublicAttr.Value))
            throw new XsltException($"XTSE0020: Invalid doctype-public value '{doctypePublicAttr.Value}': not a valid public identifier");

        var cdataAttr = element.Attribute("cdata-section-elements");
        if (cdataAttr != null)
        {
            output.CdataSectionElements = cdataAttr.Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(n => ParseCdataSectionQName(n, element))
                .ToHashSet();
        }

        var useCharMapsAttr = element.Attribute("use-character-maps");
        if (useCharMapsAttr != null)
        {
            foreach (var n in useCharMapsAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                output.UseCharacterMaps.Add(ParseQName(n, element));
            }
        }

        var suppressIndentAttr = element.Attribute("suppress-indentation");
        if (suppressIndentAttr != null)
        {
            output.SuppressIndentation = suppressIndentAttr.Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(n => ParseQName(n, element))
                .ToHashSet();
        }

        // Register the parameter document's inline character maps as an anonymous map and
        // reference it FIRST, so any use-character-maps written directly on xsl:output (which
        // ParseOutput already appended above) take precedence per §26.1.
        if (paramDocCharMap is { Count: > 0 })
        {
            var synthName = new QName(NamespaceId.None, "param-doc-character-map" + Guid.NewGuid().ToString("N"));
            stylesheet.CharacterMaps[synthName] = new XsltCharacterMap
            {
                Name = synthName,
                Mappings = paramDocCharMap
            };
            output.UseCharacterMaps.Insert(0, synthName);
        }

        return output;
    }


    private static XsltCharacterMap ParseCharacterMap(XElement element)
    {
        var name = ParseQName(element.Attribute("name")!.Value, element);
        var useAttr = element.Attribute("use-character-maps");

        var useCharacterMaps = new List<QName>();
        if (useAttr != null)
        {
            foreach (var n in useAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                useCharacterMaps.Add(ParseQName(n, element));
            }
        }

        var mappings = new Dictionary<int, string>();
        foreach (var child in element.Elements(XsltNs + "output-character"))
        {
            var charAttr = child.Attribute("character")?.Value
                ?? throw new XsltException("XTSE0010: xsl:output-character requires a 'character' attribute",
                    GetSourceLocation(child));
            var stringAttr = child.Attribute("string")?.Value
                ?? throw new XsltException("XTSE0010: xsl:output-character requires a 'string' attribute",
                    GetSourceLocation(child));
            // The 'character' attribute must be a single XML character (one Unicode code
            // point). That is one UTF-16 code unit for BMP characters, or a surrogate pair
            // for astral characters (> U+FFFF). Key the mapping on the code point so astral
            // characters are not silently dropped (character-map-007/010).
            if (charAttr.Length == 1)
            {
                mappings[charAttr[0]] = stringAttr;
            }
            else if (charAttr.Length == 2 && char.IsHighSurrogate(charAttr[0]) && char.IsLowSurrogate(charAttr[1]))
            {
                mappings[char.ConvertToUtf32(charAttr[0], charAttr[1])] = stringAttr;
            }
        }

        return new XsltCharacterMap
        {
            Name = name,
            UseCharacterMaps = useCharacterMaps,
            Mappings = mappings
        };
    }


    private static XsltDecimalFormat ParseDecimalFormat(XElement element)
    {
        // XTSE0020: Validate name is a valid QName (no AVTs, no invalid characters)
        var nameVal = element.Attribute("name")?.Value;
        if (nameVal != null)
            ValidateQNameValue(nameVal, "name", GetSourceLocation(element));

        // Validate single-character attributes (XTSE0020)
        ValidateSingleCharAttr(element, "decimal-separator");
        ValidateSingleCharAttr(element, "grouping-separator");
        ValidateSingleCharAttr(element, "minus-sign");
        ValidateSingleCharAttr(element, "percent");
        ValidateSingleCharAttr(element, "per-mille");
        ValidateSingleCharAttr(element, "zero-digit");
        ValidateSingleCharAttr(element, "digit");
        ValidateSingleCharAttr(element, "pattern-separator");
        ValidateSingleCharAttr(element, "exponent-separator");

        var decSep = GetFirstTextElement(element, "decimal-separator") ?? ".";
        var grpSep = GetFirstTextElement(element, "grouping-separator") ?? ",";
        var percent = GetFirstTextElement(element, "percent") ?? "%";
        var perMille = GetFirstTextElement(element, "per-mille") ?? "\u2030";
        var zeroDigit = GetFirstTextElement(element, "zero-digit") ?? "0";
        var digit = GetFirstTextElement(element, "digit") ?? "#";
        var patSep = GetFirstTextElement(element, "pattern-separator") ?? ";";
        var expSep = GetFirstTextElement(element, "exponent-separator") ?? "e";

        // Validate zero-digit is a Unicode digit character (XTSE1295)
        if (element.Attribute("zero-digit") != null)
        {
            var zdRune = GetFirstRune(zeroDigit);
            if (!System.Text.Rune.IsDigit(zdRune) || System.Text.Rune.GetNumericValue(zdRune) != 0)
                throw new XsltException(
                    $"XTSE1295: The zero-digit character '{zeroDigit}' must be a Unicode digit with value zero",
                    GetSourceLocation(element));
        }

        // Note: XTSE1300 (duplicate character roles) is checked AFTER all merging
        // in ValidateDecimalFormats(), since partial declarations may have false conflicts
        // with default values that will be resolved during merge.

        // Track which attributes are explicitly set (for merge conflict detection)
        var explicitAttrs = new HashSet<string>();
        string[] charAttrNames = ["decimal-separator", "grouping-separator", "infinity", "minus-sign",
            "NaN", "percent", "per-mille", "zero-digit", "digit", "pattern-separator", "exponent-separator"];
        foreach (var attrName in charAttrNames)
        {
            if (element.Attribute(attrName) != null)
                explicitAttrs.Add(attrName);
        }

        return new XsltDecimalFormat
        {
            Name = element.Attribute("name") != null
                ? ParseQName(element.Attribute("name")!.Value, element)
                : null,
            DecimalSeparator = decSep,
            GroupingSeparator = grpSep,
            Infinity = element.Attribute("infinity")?.Value ?? "Infinity",
            MinusSign = GetFirstTextElement(element, "minus-sign") ?? "-",
            NaN = element.Attribute("NaN")?.Value ?? "NaN",
            Percent = percent,
            PerMille = perMille,
            ZeroDigit = zeroDigit,
            Digit = digit,
            PatternSeparator = patSep,
            ExponentSeparator = expSep,
            ExplicitAttributes = explicitAttrs
        };
    }


    private XsltAccumulator ParseAccumulator(XElement element)
    {
        var name = ParseQName(element.Attribute("name")!.Value, element);
        var asAttr = element.Attribute("as");
        var initialValueAttr = element.Attribute("initial-value");
        var streamableAttr = element.Attribute("streamable");

        var rules = new List<XsltAccumulatorRule>();
        foreach (var child in element.Elements(XsltNs + "accumulator-rule"))
        {
            var matchStr = child.Attribute("match")!.Value;

            // XPST0008: $value is not in scope in accumulator match patterns (only in select)
            if (System.Text.RegularExpressions.Regex.IsMatch(matchStr, @"\$value\b"))
                throw new XsltException("XPST0008: Variable reference '$value' is not allowed in accumulator match pattern",
                    GetSourceLocation(child));

            var match = ParsePattern(matchStr, child);
            var phase = child.Attribute("phase")?.Value;
            var selectAttr = child.Attribute("select");

            rules.Add(new XsltAccumulatorRule
            {
                Match = match,
                Phase = phase == "end" ? AccumulatorPhase.End : AccumulatorPhase.Start,
                Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
                Content = selectAttr == null && child.HasElements
                    ? ParseSequenceConstructor(child)
                    : null
            });
        }

        if (rules.Count == 0)
            throw new XsltException("XTSE0010: xsl:accumulator must contain at least one xsl:accumulator-rule", GetSourceLocation(element));

        var isStreamable = NormalizeYesNo(streamableAttr?.Value, "streamable", "xsl:accumulator", element);

        // XTSE3430: Streamable accumulator patterns must be motionless
        // (no predicates, no upward/sibling axis navigation)
        if (isStreamable)
        {
            foreach (var rule in rules)
            {
                if (HasNonMotionlessPredicates(rule.Match))
                    throw new XsltException("XTSE3430: Accumulator rule pattern is not motionless: patterns in a streamable accumulator must not contain predicates",
                        GetSourceLocation(element));
            }
        }

        return new XsltAccumulator
        {
            Name = name,
            SourceName = element.Attribute("name")!.Value,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            InitialValue = ParseExpr(initialValueAttr!.Value),
            Rules = rules,
            Streamable = isStreamable
        };
    }


    private static XsltMode ParseMode(XElement element)
    {
        // XTSE0010: xsl:mode must be empty (no child elements)
        if (element.Elements().Any())
            throw new XsltException("XTSE0010: xsl:mode must be empty",
                GetSourceLocation(element));

        var nameAttr = element.Attribute("name");
        // XTSE0020: xsl:mode/@name must be a valid EQName. The reserved tokens
        // "#unnamed"/"#default" (and any other #-prefixed token) are not permitted —
        // the unnamed mode is selected by omitting @name entirely. See W3C
        // decl/package package-909.
        if (nameAttr != null)
            ValidateQNameValue(nameAttr.Value, "name", GetSourceLocation(element));
        var streamableAttr = element.Attribute("streamable");
        var onNoMatchAttr = element.Attribute("on-no-match");
        var onMultipleMatchAttr = element.Attribute("on-multiple-match");
        var visibilityAttr = element.Attribute("visibility");
        var useAccumulatorsAttr = element.Attribute("use-accumulators");

        // Parse use-accumulators: "#all" or space-separated list of QNames
        var useAllAccumulators = false;
        var useAccumulatorNames = new List<QName>();
        if (useAccumulatorsAttr != null)
        {
            var val = useAccumulatorsAttr.Value.Trim();
            if (val == "#all")
            {
                useAllAccumulators = true;
            }
            else
            {
                foreach (var name in val.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    useAccumulatorNames.Add(ParseQName(name, element));
                }
            }
        }

        // XTSE0020: The unnamed mode cannot be public or final
        if (nameAttr == null && visibilityAttr != null)
        {
            var vis = visibilityAttr.Value.Trim();
            if (vis is "public" or "final")
                throw new XsltException(
                    $"XTSE0020: The unnamed mode cannot be {vis}",
                    GetSourceLocation(element));
        }

        // Validate warning-on-no-match (XTSE0020 for invalid values like "Yes")
        var warningOnNoMatchAttr = element.Attribute("warning-on-no-match");
        if (warningOnNoMatchAttr != null)
            NormalizeYesNo(warningOnNoMatchAttr.Value.Trim(), "warning-on-no-match", "xsl:mode", element);

        // Validate warning-on-multiple-match (XTSE0020 for invalid values like "Yes")
        var warningOnMultipleMatchAttr = element.Attribute("warning-on-multiple-match");
        if (warningOnMultipleMatchAttr != null)
            NormalizeYesNo(warningOnMultipleMatchAttr.Value.Trim(), "warning-on-multiple-match", "xsl:mode", element);

        // Validate typed attribute (XTSE0020 for invalid values like "No")
        var typedAttr = element.Attribute("typed");
        if (typedAttr != null)
        {
            var typedVal = typedAttr.Value.Trim();
            if (typedVal is not ("yes" or "no" or "strict" or "lax" or "unspecified" or "true" or "false" or "0" or "1"))
                throw new XsltException(
                    $"XTSE0020: Invalid value '{typedVal}' for attribute 'typed' on xsl:mode (must be 'yes', 'no', 'strict', 'lax', or 'unspecified')",
                    GetSourceLocation(element));
        }

        var isTyped = typedAttr != null && typedAttr.Value.Trim() is "yes" or "strict" or "lax" or "true" or "1";

        return new XsltMode
        {
            Name = nameAttr != null ? ParseQName(nameAttr.Value, element) : null,
            Streamable = streamableAttr?.Value == "yes",
            OnNoMatch = onNoMatchAttr != null ? ParseOnNoMatchBehavior(onNoMatchAttr.Value, element) : null,
            OnMultipleMatch = onMultipleMatchAttr != null ? ParseOnMultipleMatchBehavior(onMultipleMatchAttr.Value, element) : OnMultipleMatchBehavior.UseLast,
            Visibility = ParseVisibility(visibilityAttr?.Value),
            VisibilityAttr = visibilityAttr?.Value.Trim(),
            UseAllAccumulators = useAllAccumulators,
            UseAccumulatorNames = useAccumulatorNames,
            UseAccumulatorsAttr = useAccumulatorsAttr?.Value.Trim(),
            Typed = isTyped
        };
    }


    /// <summary>
    /// Parses xsl:item-type (XSLT 4.0) — declares a named type alias.
    /// </summary>
    private void ParseItemType(XElement element, XsltStylesheet stylesheet)
    {
        var nameAttr = element.Attribute("name")?.Value
            ?? throw new XsltException("XTSE0010: xsl:item-type requires a 'name' attribute",
                GetSourceLocation(element));
        var asAttr = element.Attribute("as")?.Value
            ?? throw new XsltException("XTSE0010: xsl:item-type requires an 'as' attribute",
                GetSourceLocation(element));

        _nsContext = element;
        var name = ParseQName(nameAttr, element);
        var typeSpec = ParseSequenceType(asAttr, element);
        _nsContext = null;

        stylesheet.NamedTypes[name] = typeSpec;
    }


    /// <summary>
    /// Checks if two xsl:mode elements have conflicting values for a given attribute.
    /// </summary>
    private static void CheckModeAttrConflict(XElement prev, XElement current, string attrName, string modeName)
    {
        var prevAttr = prev.Attribute(attrName);
        var currAttr = current.Attribute(attrName);
        if (prevAttr != null && currAttr != null && prevAttr.Value != currAttr.Value)
            throw new XsltException($"XTSE0545: Conflicting xsl:mode declarations for mode '{modeName}': attribute '{attrName}' has values '{prevAttr.Value}' and '{currAttr.Value}'",
                GetSourceLocation(current));
    }


    /// <summary>
    /// Parses the arguments of a key() function call in a pattern.
    /// Returns (keyName, valueExpression).
    /// </summary>
    private (string? keyName, XQueryExpression? valueExpr) ParseKeyPatternArgs(string argsStr, XElement context)
    {
        // Split on comma, respecting string literals
        var commaIdx = -1;
        var inQuote = false;
        var quoteChar = '\0';
        var parenDepth = 0;
        for (var k = 0; k < argsStr.Length; k++)
        {
            var c = argsStr[k];
            if (inQuote)
            {
                if (c == quoteChar) inQuote = false;
            }
            else if (c == '\'' || c == '"')
            {
                inQuote = true;
                quoteChar = c;
            }
            else if (c == '(') parenDepth++;
            else if (c == ')') parenDepth--;
            else if (c == ',' && parenDepth == 0)
            {
                commaIdx = k;
                break;
            }
        }

        if (commaIdx < 0)
            return (null, null);

        var firstArg = argsStr[..commaIdx].Trim();
        var secondArg = argsStr[(commaIdx + 1)..].Trim();

        // First argument should be a string literal (key name)
        string? keyName = null;
        if (firstArg.Length >= 2 && (firstArg[0] == '\'' || firstArg[0] == '"') && firstArg[^1] == firstArg[0])
        {
            keyName = firstArg[1..^1];
        }
        else
        {
            return (null, null); // Not a string literal
        }

        // Second argument: parse as an XQuery expression
        try
        {
            var valueExpr = ParseExpr(secondArg);

            // XTSE0340: In a pattern, the second argument to key() must be a literal or variable reference
            if (valueExpr is not (XQuery.Ast.LiteralExpression or XQuery.Ast.VariableReference))
            {
                throw new XsltException(
                    "XTSE0340: The second argument to key() in a pattern must be a literal or variable reference",
                    GetSourceLocation(context));
            }

            return (keyName, valueExpr);
        }
#pragma warning disable CA1031
        catch (XsltException)
        {
            throw; // Re-throw XTSE0340
        }
        catch (Exception)
        {
            return (null, null);
        }
#pragma warning restore CA1031
    }


    /// <summary>
    /// Parses comma-separated function parameter types like "xs:string, xs:integer" or
    /// "xs:anyAtomicType?, item()*". Respects nested parentheses so types like
    /// "function(xs:string) as xs:boolean" are not split incorrectly.
    /// </summary>
    private static List<XdmSequenceType> ParseFunctionParameterTypes(string paramsPart, XElement? context)
    {
        var result = new List<XdmSequenceType>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < paramsPart.Length; i++)
        {
            if (paramsPart[i] == '(') depth++;
            else if (paramsPart[i] == ')') depth--;
            else if (paramsPart[i] == ',' && depth == 0)
            {
                result.Add(ParseSequenceType(paramsPart[start..i].Trim(), context));
                start = i + 1;
            }
        }
        if (start < paramsPart.Length)
            result.Add(ParseSequenceType(paramsPart[start..].Trim(), context));
        return result;
    }

}
