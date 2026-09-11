using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Parses XSLT stylesheets from XML.
/// </summary>
public sealed partial class StylesheetParser
{

    /// <summary>
    /// Checks if validation="strict" or type attributes should be rejected (XTSE1660).
    /// Returns false (suppress error) when xsl:import-schema is present.
    /// </summary>
    private bool ShouldRejectSchemaAware => !_hasImportSchema;


    /// <summary>
    /// When true, DTD processing is allowed in stylesheet loading. Default is false (secure).
    /// </summary>
    public bool AllowDtdProcessing { get; init; }


    /// <summary>
    /// Optional resource policy for controlling xsl:import/xsl:include resolution.
    /// </summary>
    internal PhoenixmlDb.XQuery.Security.ResourcePolicy? ResourcePolicy { get; init; }


    /// <summary>
    /// Optional pre-fetched HTTP imports / includes. Consulted before the parser falls
    /// back to <see cref="HttpResourceLoader.GetStringSync"/>, so callers running on a
    /// runtime that cannot block (Blazor WebAssembly) can pre-fetch async and pass the
    /// content through. See <see cref="PreloadedResources"/> for usage.
    /// </summary>
    internal PreloadedResources? PreloadedResources { get; init; }


    /// <summary>
    /// Policy for selecting among multiple available package versions that satisfy an
    /// <c>xsl:use-package/@package-version</c> range. Defaults to
    /// <see cref="PackageVersionResolution.Highest"/> (spec-recommended).
    /// </summary>
    public PackageVersionResolution VersionResolution { get; init; } = PackageVersionResolution.Highest;


    /// <summary>
    /// All dynamically-registered URI → NamespaceId mappings.
    /// Used by the function library to resolve EQName function calls.
    /// </summary>
    public static IReadOnlyDictionary<string, NamespaceId> DynamicNamespaces => _dynamicNamespaces;


    /// <summary>
    /// Records the visible components a single xsl:use-package contributes to the using package
    /// and raises XTSE3050 when a distinct earlier use-package already contributed the same
    /// symbolic name. A component is contributed when it is exposed at the used package's
    /// boundary and the using package's xsl:accept did not hide it (its effective visibility is
    /// public, private, or final — abstract components are unusable and never counted, hidden
    /// ones are not components). A symbol resolved by xsl:override in this use-package is the
    /// spec exception and is skipped.
    /// </summary>
    private static void RecordAcceptedComponent(
        XsltStylesheet stylesheet,
        HashSet<(string, QName, int)> overriddenSymbols,
        XElement element,
        string kind,
        IEnumerable<(QName Name, bool IsBoundaryExposed, Ast.Visibility Vis, int Arity)> components)
    {
        foreach (var (name, exposed, vis, arity) in components)
        {
            if (!exposed) continue;
            if (vis is not (Ast.Visibility.Public or Ast.Visibility.Private or Ast.Visibility.Final))
                continue;
            var symbol = (kind, name, arity);
            if (overriddenSymbols.Contains(symbol)) continue;
            if (!stylesheet.AcceptedComponentSymbols.Add(symbol))
                throw new XsltException(
                    $"XTSE3050: The package contains two components of kind '{KindDisplayName(kind)}' " +
                    $"with the same name '{name.LocalName}', each accepted from a used package, " +
                    "and neither resolves the other via xsl:override",
                    GetSourceLocation(element));
        }
    }


    private static string KindDisplayName(string kind) => kind switch
    {
        "T" => "template",
        "V" => "variable",
        "F" => "function",
        "A" => "attribute-set",
        "M" => "mode",
        _ => kind,
    };


    /// <summary>
    /// Selects the file path of the best-matching package version for a requested
    /// <c>@package-version</c> range. Collects every entry whose version satisfies the
    /// range (using catalog metadata, falling back to each file's declared
    /// <c>package-version</c>) then picks one per <paramref name="resolution"/>: the
    /// highest matching version (default / <see cref="PackageVersionResolution.Unspecified"/>)
    /// or the lowest. Returns <c>null</c> when nothing matches.
    /// </summary>
    public static string? SelectMatchingPackage(
        IReadOnlyList<(string? Version, string FilePath)> entries,
        string? requestedVersion,
        PackageVersionResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(entries);

        // Gather all matching entries with a comparable effective version.
        var matches = new List<(string EffVersion, string FilePath)>();
        foreach (var (version, filePath) in entries)
        {
            if (VersionMatches(version, requestedVersion))
                matches.Add((version ?? "1", filePath));
        }

        // Fallback: the catalog version may be absent or differ from the package file's
        // actual version. Consult the package-version declared in each file.
        if (matches.Count == 0)
        {
            foreach (var (_, filePath) in entries)
            {
                var fileVersion = TryReadFilePackageVersion(filePath);
                if (fileVersion != null && VersionMatches(fileVersion, requestedVersion))
                    matches.Add((fileVersion, filePath));
            }
        }

        if (matches.Count == 0) return null;

        // Select according to the resolution strategy. Lowest picks the minimum matching
        // version; Highest and Unspecified pick the maximum (deterministic).
        var best = matches[0];
        for (var i = 1; i < matches.Count; i++)
        {
            var cmp = CompareVersions(matches[i].EffVersion, best.EffVersion);
            var take = resolution == PackageVersionResolution.Lowest ? cmp < 0 : cmp > 0;
            if (take) best = matches[i];
        }
        return best.FilePath;
    }


    /// <summary>
    /// Public version matching for fn:transform package-version resolution.
    /// </summary>
    public static bool VersionMatchesPublic(string? available, string? requested)
        => VersionMatches(available, requested);


    private static bool VersionMatches(string? available, string? requested)
    {
        if (requested == null || requested == "*") return true;
        if (available == null)
        {
            // Versionless package defaults to version "1"
            available = "1";
        }

        // Version list: "1.0.0, 2.0" — match if any element matches
        if (requested.Contains(',', StringComparison.Ordinal))
        {
            foreach (var part in requested.Split(','))
            {
                if (VersionMatches(available, part.Trim()))
                    return true;
            }
            return false;
        }

        // Range: "1.5 to 2.5", "to 1.5", "1.5 to" — inclusive range match
        if (requested.StartsWith("to ", StringComparison.Ordinal))
        {
            var high = requested[3..].Trim();
            return CompareVersions(available, high) <= 0;
        }
        var toIndex = requested.IndexOf(" to ", StringComparison.Ordinal);
        if (toIndex >= 0)
        {
            var low = requested[..toIndex].Trim();
            var high = requested[(toIndex + 4)..].Trim();
            if (high.Length == 0)
                return CompareVersions(available, low) >= 0; // "1.5 to" = 1.5 or above
            return CompareVersions(available, low) >= 0 && CompareVersions(available, high) <= 0;
        }

        // Minimum: "1.5+" — version 1.5 or above
        if (requested.EndsWith('+'))
        {
            var min = requested[..^1];
            return CompareVersions(available, min) >= 0;
        }

        // Wildcard suffix matching: "1.*" matches "1.0", "1.5", etc.
        if (requested.EndsWith(".*", StringComparison.Ordinal))
        {
            var prefix = requested[..^2];
            if (available.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (available.Length == prefix.Length || available[prefix.Length] == '.')
                    return true;
            }
            var normAvail = NormalizeVersion(available);
            if (normAvail.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (normAvail.Length == prefix.Length || normAvail[prefix.Length] == '.')
                    return true;
            }
            return false;
        }

        // Exact match (with normalization)
        return string.Equals(available, requested, StringComparison.Ordinal)
            || NormalizeVersion(available) == NormalizeVersion(requested);
    }


    private static (string Numeric, string? PreRelease) SplitPreRelease(string version)
    {
        var idx = version.IndexOf('-', StringComparison.Ordinal);
        return idx >= 0 ? (version[..idx], version[(idx + 1)..]) : (version, null);
    }


    private static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrEmpty(version)) return "1.0.0";
        var parts = version.Split('.');
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => version
        };
    }


    /// <summary>
    /// Checks if a sequence constructor contains xsl:apply-imports (recursively through all children).
    /// </summary>
    private static bool ContainsApplyImports(XsltSequenceConstructor body)
    {
        foreach (var instr in body.Instructions)
        {
            if (ContainsApplyImportsInInstruction(instr))
                return true;
        }
        return false;
    }


    private static bool ContainsApplyImportsInInstruction(Ast.XsltInstruction instr)
    {
        if (instr is Ast.XsltApplyImports) return true;
        // Use reflection-free checks for common instruction types with nested content
        if (instr is Ast.XsltIf ifInstr)
            return ContainsApplyImports(ifInstr.Then);
        if (instr is Ast.XsltChoose chooseInstr)
        {
            foreach (var when in chooseInstr.When)
                if (ContainsApplyImports(when.Body)) return true;
            if (chooseInstr.Otherwise != null && ContainsApplyImports(chooseInstr.Otherwise)) return true;
        }
        if (instr is Ast.XsltLiteralResultElement lre)
            return ContainsApplyImports(lre.Content);
        if (instr is Ast.XsltElement elem && elem.Content != null)
            return ContainsApplyImports(elem.Content);
        if (instr is Ast.XsltForEach forEach)
            return ContainsApplyImports(forEach.Body);
        return false;
    }


    /// <summary>
    /// Finds the most-specific xsl:accept rule matching a component of the given kind and name.
    /// </summary>
    private static bool TryResolveAcceptVisibility(
        List<AcceptRule> rules, string componentType, QName name, Visibility currentVisibility,
        out Visibility visibility, int componentArity = -1)
    {
        var bestSpecificity = -1;
        var bestOrder = -1;
        visibility = default;
        var found = false;
        foreach (var rule in rules)
        {
            if (rule.Component != "*" && rule.Component != componentType) continue;
            // Erratum E36: a function accept token carries its arity ("p:f#0"). Match on the
            // name part and require the arity to agree; strip the suffix before name-matching and
            // specificity scoring (a suffixed token was previously never matched at all).
            var token = rule.Token;
            if (componentType == "function")
            {
                var hashIdx = token.IndexOf('#', StringComparison.Ordinal);
                if (hashIdx >= 0)
                {
                    var ruleArity = int.TryParse(token[(hashIdx + 1)..], out var a) ? a : -1;
                    if (ruleArity >= 0 && componentArity >= 0 && ruleArity != componentArity) continue;
                    token = token[..hashIdx];
                }
            }
            if (!AcceptTokenMatches(name, token, rule.Element)) continue;
            var spec = AcceptTokenSpecificity(token);
            // A wildcard xsl:accept that would make an abstract component public or final
            // cannot be satisfied and is treated as not matching that component (XSLT 3.0
            // §3.5.2); the abstract component then keeps its default hidden visibility.
            if (spec < 3 && currentVisibility == Visibility.Abstract
                && rule.Visibility is Visibility.Public or Visibility.Final)
                continue;
            // Higher specificity wins; among equal specificity, later document order wins.
            if (spec > bestSpecificity || (spec == bestSpecificity && rule.Order >= bestOrder))
            {
                bestSpecificity = spec;
                bestOrder = rule.Order;
                visibility = rule.Visibility;
                found = true;
            }
        }
        return found;
    }


    private static string VisibilityToAttr(Visibility vis) => vis switch
    {
        Visibility.Public => "public",
        Visibility.Private => "private",
        Visibility.Final => "final",
        Visibility.Abstract => "abstract",
        Visibility.Hidden => "hidden",
        _ => "private",
    };


    /// <summary>
    /// Merges multiple xsl:output declarations with the same name per XSLT spec.
    /// Later declarations override earlier ones for each attribute; cdata-section-elements are unioned.
    /// </summary>
    /// <summary>
    /// XTSE0265: It is a static error if one stylesheet module specifies input-type-annotations="strip"
    /// and another specifies input-type-annotations="preserve".
    /// </summary>
    private static void CheckInputTypeAnnotationsConflict(XsltStylesheet target, XsltStylesheet source)
    {
        if (target.InputTypeAnnotations != Ast.TypeAnnotations.Unspecified
            && source.InputTypeAnnotations != Ast.TypeAnnotations.Unspecified
            && target.InputTypeAnnotations != source.InputTypeAnnotations)
        {
            var targetVal = target.InputTypeAnnotations == Ast.TypeAnnotations.Strip ? "strip" : "preserve";
            var sourceVal = source.InputTypeAnnotations == Ast.TypeAnnotations.Strip ? "strip" : "preserve";
            throw new XsltException(
                $"XTSE0265: Conflicting input-type-annotations: one module specifies '{targetVal}' and another specifies '{sourceVal}'");
        }
    }


    private static void ValidateNoAbstractComponents(XsltStylesheet stylesheet)
    {
        // XTSE3080: A top-level package must not contain components with visibility="abstract".
        // Abstract components are only valid in library packages intended for use-package overriding.
        if (!stylesheet.IsPackage) return;

        foreach (var template in stylesheet.Templates)
        {
            if (template.Visibility == Visibility.Abstract)
                throw new XsltException("XTSE3080: Top-level package contains a template with visibility=\"abstract\"");
        }
        foreach (var (_, func) in stylesheet.Functions)
        {
            if (func.Visibility == Visibility.Abstract)
                throw new XsltException("XTSE3080: Top-level package contains a function with visibility=\"abstract\"");
        }
        foreach (var variable in stylesheet.Variables)
        {
            if (variable.Visibility == Visibility.Abstract)
                throw new XsltException("XTSE3080: Top-level package contains a variable with visibility=\"abstract\"");
        }
        foreach (var (_, attrSet) in stylesheet.AttributeSets)
        {
            if (attrSet.Visibility == Visibility.Abstract)
                throw new XsltException("XTSE3080: Top-level package contains an attribute-set with visibility=\"abstract\"");
        }
    }


    private static void ValidateSingleFunctionCall(
        PhoenixmlDb.XQuery.Ast.FunctionCallExpression fc, XsltStylesheet stylesheet,
        PhoenixmlDb.XQuery.Functions.FunctionLibrary lib,
        HashSet<NamespaceId> wellKnownNamespaces)
    {
        var name = fc.Name;
        var arity = fc.Arguments.Count;

        // Functions in well-known namespaces (fn, xs, math, etc.) or with no namespace
        // are assumed valid — they'll be validated at runtime
        if (wellKnownNamespaces.Contains(name.Namespace))
            return;

        // Functions with EQName syntax where the URI matches a well-known namespace
        if (name.ExpandedNamespace != null)
        {
            // Skip validation for well-known namespace URIs
            return;
        }

        // User-namespace function: must be declared as xsl:function
        if (stylesheet.Functions.ContainsKey((name, arity)))
            return;

        // Check if ANY arity exists for this function name (for better error message)
        var displayName = name.Prefix != null ? $"{name.Prefix}:{name.LocalName}" : name.LocalName;
        throw new XsltException($"XPST0017: Cannot find a {arity}-argument function named {{{displayName}}}. " +
                                 "No user-defined function with this name and arity is available.");
    }


    private static void ValidateUseAttributeSetRefsInInstructions(XsltSequenceConstructor body, XsltStylesheet stylesheet)
    {
        foreach (var instruction in body.Instructions)
        {
            ValidateUseAttributeSetRefsInInstruction(instruction, stylesheet);
        }
    }


    private static void ValidateUseAttributeSetRefsInInstruction(XsltInstruction instruction, XsltStylesheet stylesheet)
    {
        // Check use-attribute-sets on instructions that support them
        List<QName>? useAttrSets = instruction switch
        {
            XsltElement e => e.UseAttributeSets,
            XsltCopy c => c.UseAttributeSets,
            XsltLiteralResultElement lre => lre.UseAttributeSets,
            _ => null
        };

        if (useAttrSets != null)
        {
            foreach (var usedName in useAttrSets)
            {
                if (!stylesheet.AttributeSets.ContainsKey(usedName))
                    throw new XsltException($"XTSE0710: Attribute set '{usedName}' is not defined");
            }
        }

        // Recurse into child sequence constructors
        switch (instruction)
        {
            case XsltSequenceConstructor sc:
                foreach (var child in sc.Instructions)
                    ValidateUseAttributeSetRefsInInstruction(child, stylesheet);
                break;
            case XsltElement e:
                ValidateUseAttributeSetRefsInInstructions(e.Content, stylesheet);
                break;
            case XsltCopy c when c.Content != null:
                ValidateUseAttributeSetRefsInInstructions(c.Content, stylesheet);
                break;
            case XsltLiteralResultElement lre:
                ValidateUseAttributeSetRefsInInstructions(lre.Content, stylesheet);
                break;
            case XsltIf i:
                ValidateUseAttributeSetRefsInInstructions(i.Then, stylesheet);
                break;
            case XsltChoose ch:
                foreach (var w in ch.When)
                    ValidateUseAttributeSetRefsInInstructions(w.Body, stylesheet);
                if (ch.Otherwise != null)
                    ValidateUseAttributeSetRefsInInstructions(ch.Otherwise, stylesheet);
                break;
            case XsltForEach fe:
                ValidateUseAttributeSetRefsInInstructions(fe.Body, stylesheet);
                break;
            case XsltTry t:
                if (t.Body != null)
                    ValidateUseAttributeSetRefsInInstructions(t.Body, stylesheet);
                foreach (var c in t.Catches)
                {
                    if (c.Body != null)
                        ValidateUseAttributeSetRefsInInstructions(c.Body, stylesheet);
                }
                break;
        }
    }


    private XsltStylesheet ParseSimplifiedStylesheet(XElement element)
    {
        // XTSE0150: A simplified stylesheet must have an xsl:version attribute
        var versionAttr = element.Attribute(XsltNs + "version");
        if (versionAttr == null)
            throw new XsltException("XTSE0150: A literal result element used as a simplified stylesheet module must have an xsl:version attribute",
                GetSourceLocation(element));

        // A simplified stylesheet is a literal result element that acts as the template body
        var stylesheet = new XsltStylesheet
        {
            Version = versionAttr.Value,
            BaseUri = _baseUri
        };

        // Create implicit template matching /
        var template = new XsltTemplate
        {
            Match = new PathPattern
            {
                Steps =
                [
                    new PatternStep
                    {
                        Axis = Axis.Self,
                        NodeTest = new KindTest { Kind = XdmNodeKind.Document }
                    }
                ]
            },
            Body = new XsltSequenceConstructor
            {
                Instructions = [ParseInstruction(element)]
            }
        };

        stylesheet.Templates.Add(template);
        return stylesheet;
    }


    private XsltTemplate ParseTemplate(XElement element, string? moduleVersion = null)
    {
        var prevContext = _nsContext;
        _nsContext = element;

        var nameAttr = element.Attribute("name");
        var matchAttr = element.Attribute("match");
        var priorityAttr = element.Attribute("priority");
        var modeAttr = element.Attribute("mode");
        var asAttr = element.Attribute("as");
        var versionAttr = element.Attribute("version");

        // XTSE0090: Validate no unknown attributes
        ValidateAllowedAttributes(element, GetSourceLocation(element), "match", "name", "priority", "mode", "as", "visibility", "version", "default-collation");

        // XTSE0500: Template must have at least match or name
        if (matchAttr == null && nameAttr == null)
            throw new XsltException("XTSE0500: An xsl:template element must have either a match attribute or a name attribute, or both",
                GetSourceLocation(element));
        // XTSE0500: The visibility attribute is permitted only on a named template
        // (a component). A template rule — one with a match pattern but no name — is
        // not an independently-referenced component, so visibility is not allowed on it.
        // See W3C decl/package package-001t.
        if (nameAttr == null && element.Attribute("visibility") != null)
            throw new XsltException("XTSE0500: The visibility attribute is not permitted on an xsl:template element that has no name attribute",
                GetSourceLocation(element));

        // XTSE0500: Template without match must not have mode or priority
        if (matchAttr == null)
        {
            if (modeAttr != null)
                throw new XsltException("XTSE0500: An xsl:template element with no match attribute must have no mode attribute",
                    GetSourceLocation(element));
            if (priorityAttr != null)
                throw new XsltException("XTSE0500: An xsl:template element with no match attribute must have no priority attribute",
                    GetSourceLocation(element));
        }

        // XTSE0020: Validate name/mode are valid QNames
        if (nameAttr != null)
            ValidateQNameValue(nameAttr.Value, "name", GetSourceLocation(element));

        // Apply default-mode on this template FIRST — per XSLT spec, the effective
        // default-mode of an element is its own default-mode attribute if present,
        // otherwise inherited from its parent. This affects both the template's match
        // mode (when mode attr is absent) and instructions within the body.
        var prevDefaultMode = _currentDefaultMode;
        var templateDefaultMode = element.Attribute("default-mode");
        if (templateDefaultMode != null)
            _currentDefaultMode = templateDefaultMode.Value == "#unnamed" ? null : ParseQName(templateDefaultMode.Value, element);

        var modes = new List<QName>();
        if (modeAttr != null)
        {
            var modeTokens = modeAttr.Value.Split(WhitespaceSeparators, StringSplitOptions.RemoveEmptyEntries);
            // XTSE0550: mode list must not be empty
            if (modeTokens.Length == 0)
                throw new XsltException("XTSE0550: The mode attribute of xsl:template must not be empty", GetSourceLocation(element));
            var seenModes = new HashSet<string>();
            bool hasAll = false;
            foreach (var mode in modeTokens)
            {
                // XTSE0550: duplicate mode tokens
                if (!seenModes.Add(mode))
                    throw new XsltException($"XTSE0550: Duplicate mode '{mode}' in mode list of xsl:template", GetSourceLocation(element));
                if (mode == "#all")
                {
                    hasAll = true;
                    modes.Add(TemplateIndex.AllModeSentinel);
                    continue;
                }
                if (mode == "#default" || mode == "#unnamed")
                {
                    // Resolve #default/#unnamed to the effective default mode
                    if (mode == "#default" && _currentDefaultMode.HasValue)
                        modes.Add(_currentDefaultMode.Value);
                    else
                        modes.Add(TemplateIndex.DefaultModeSentinel);
                    continue;
                }
                // XTSE0550: validate mode token is a valid QName (no #, !, etc.)
                if (mode.StartsWith('#'))
                    throw new XsltException($"XTSE0550: Invalid mode token '{mode}' in mode list of xsl:template", GetSourceLocation(element));
                ValidateQNameValue(mode, "mode", GetSourceLocation(element));
                modes.Add(ParseQName(mode, element));
            }
            // XTSE0550: #all must not appear with other modes
            if (hasAll && modeTokens.Length > 1)
                throw new XsltException("XTSE0550: The token '#all' must not appear together with any other value in the mode attribute", GetSourceLocation(element));
        }
        else if (matchAttr != null && _currentDefaultMode.HasValue)
        {
            // Template with no mode attribute: per spec, it matches in the default mode.
            // The effective default mode comes from this template's own default-mode
            // attribute, or inherited from the stylesheet's default-mode.
            modes.Add(_currentDefaultMode.Value);
        }

        // Track mode references for XTSE3085 (declared-modes) validation
        if (_usedModeReferences != null && matchAttr != null)
        {
            if (modes.Count == 0)
            {
                // No mode attribute on a matching template → uses unnamed mode implicitly
                _usedModeReferences.Add((TemplateIndex.DefaultModeSentinel, GetSourceLocation(element)));
            }
            else
            {
                foreach (var m in modes)
                {
                    // #all doesn't need validation (it's a wildcard)
                    if (!m.Equals(TemplateIndex.AllModeSentinel))
                        _usedModeReferences.Add((m, GetSourceLocation(element)));
                }
            }
        }

        var parameters = new List<XsltParam>();
        var bodyInstructions = new List<XsltInstruction>();
        var expandText = IsExpandTextActive(element);
        var seenBody = false;
        var seenParam = false;
        var contextItemUse = ContextItemUse.Optional;
        XdmSequenceType? contextItemAs = null;
        var seenContextItem = false;

        var nodeList = element.Nodes().ToList();
        var preserveSpace = IsXmlSpacePreserve(element);
        for (var ni = 0; ni < nodeList.Count; ni++)
        {
            switch (nodeList[ni])
            {
                case XElement child when child.Name == XsltNs + "param":
                    if (ShouldIncludeElement(child))
                    {
                        if (seenBody)
                            throw new XsltException("XTSE0010: xsl:param elements must come before any other content in xsl:template",
                                GetSourceLocation(child));
                        seenParam = true;
                        parameters.Add(ParseParam(child));
                    }
                    break;
                case XElement child when child.Name == XsltNs + "context-item":
                    if (ShouldIncludeElement(child))
                    {
                        // XTSE0010: xsl:context-item must come before xsl:param and body content
                        if (seenParam)
                            throw new XsltException("XTSE0010: xsl:context-item must appear before xsl:param in xsl:template",
                                GetSourceLocation(child));
                        if (seenBody)
                            throw new XsltException("XTSE0010: xsl:context-item must appear before any other content in xsl:template",
                                GetSourceLocation(child));

                        // XTSE0010: Only one xsl:context-item allowed per template
                        if (seenContextItem)
                            throw new XsltException("XTSE0010: Only one xsl:context-item declaration is allowed per template",
                                GetSourceLocation(child));
                        seenContextItem = true;

                        // XTSE0090: Validate allowed attributes (only 'as' and 'use')
                        ValidateAllowedAttributes(child, GetSourceLocation(child), "as", "use");

                        var useAttr = child.Attribute("use")?.Value?.Trim();
                        var ciAsAttr = child.Attribute("as")?.Value?.Trim();

                        contextItemUse = useAttr switch
                        {
                            "required" => ContextItemUse.Required,
                            "absent" => ContextItemUse.Absent,
                            null or "optional" => ContextItemUse.Optional,
                            _ => throw new XsltException($"XTSE0020: Invalid value '{useAttr}' for xsl:context-item/@use", GetSourceLocation(child))
                        };

                        if (ciAsAttr != null)
                        {
                            // XTSE3088: use="absent" with as attribute is a static error
                            if (contextItemUse == ContextItemUse.Absent)
                                throw new XsltException("XTSE3088: xsl:context-item specifies use='absent' together with an 'as' attribute",
                                    GetSourceLocation(child));
                            // Strip comments from as attribute (per bug 29814)
                            var cleanedAs = System.Text.RegularExpressions.Regex.Replace(ciAsAttr, @"\(:.*?:\)", "").Trim();
                            // XTSE0020: Occurrence indicators not allowed in xsl:context-item/@as
                            if (cleanedAs.EndsWith('?') || cleanedAs.EndsWith('*') || cleanedAs.EndsWith('+'))
                                throw new XsltException("XTSE0020: Occurrence indicator is not allowed in xsl:context-item/@as",
                                    GetSourceLocation(child));
                            contextItemAs = ParseSequenceType(cleanedAs, child);
                            // XPST0051: If the type is unknown (fell through to ItemType.Item)
                            // and the original string is a prefixed name, the type doesn't exist
                            if (contextItemAs.ItemType == ItemType.Item
                                && cleanedAs != "item()"
                                && !cleanedAs.StartsWith("item(", StringComparison.Ordinal)
                                && cleanedAs.Contains(':', StringComparison.Ordinal))
                                throw new XsltException($"XPST0051: Unknown type '{ciAsAttr}' in xsl:context-item/@as",
                                    GetSourceLocation(child));
                        }
                    }
                    break;
                case XElement child:
                    seenBody = true;
                    bodyInstructions.Add(ParseInstruction(child));
                    break;
                case XText:
                case XComment:
                case XProcessingInstruction:
                    // Use contiguous-run grouping (same as ParseSequenceConstructor):
                    // merge text across comments/PIs and retain the whole run if any
                    // text in it is non-whitespace. This preserves whitespace-only text
                    // nodes adjacent to non-whitespace text separated by XML comments.
                    var hasNonWhitespace = false;
                    var sb = new System.Text.StringBuilder();
                    while (ni < nodeList.Count && nodeList[ni] is XText or XComment or XProcessingInstruction)
                    {
                        if (nodeList[ni] is XText textNode)
                        {
                            sb.Append(textNode.Value);
                            if (!hasNonWhitespace && !IsXmlWhitespaceOnly(textNode.Value))
                                hasNonWhitespace = true;
                        }
                        ni++;
                    }
                    ni--; // Back up since the for loop will increment
                    var textValue = sb.ToString();

                    // In the preamble (before params/context-item), whitespace-only
                    // text nodes are always stripped even with xml:space="preserve".
                    // preserveSpace only applies once we're in the body section.
                    if (hasNonWhitespace || (seenBody && preserveSpace))
                    {
                        seenBody = true;
                        bodyInstructions.Add(CreateTextInstruction(textValue, expandText, element));
                    }
                    break;
            }
        }

        // XTSE0580: Duplicate parameter names within a template
        var paramNames = new HashSet<QName>();
        foreach (var p in parameters)
        {
            if (!paramNames.Add(p.Name))
                throw new XsltException($"XTSE0580: Duplicate parameter name '{p.Name.LocalName}' in template",
                    GetSourceLocation(element));
        }

        QName? templateName = nameAttr != null ? ParseQName(nameAttr.Value, element) : null;

        // XTSE0020: If the template has match but no name, only use="required" is allowed
        // (match templates always receive a context item via apply-templates)
        if (matchAttr != null && nameAttr == null && contextItemUse == ContextItemUse.Absent)
            throw new XsltException("XTSE0020: xsl:context-item use='absent' is not allowed on a template rule that has no name attribute",
                GetSourceLocation(element));

        // XTSE0080: Template name must not be in the XSLT namespace
        // Exception: xsl:initial-template is allowed (XSLT 3.0 spec section 3.11)
        if (templateName != null && templateName.Value.Namespace == NamespaceId.Xslt
            && templateName.Value.LocalName != "initial-template")
            throw new XsltException("XTSE0080: The name of a template must not be in the XSLT namespace",
                GetSourceLocation(element));

        _nsContext = prevContext;
        _currentDefaultMode = prevDefaultMode;
        return new XsltTemplate
        {
            Name = templateName,
            Match = matchAttr != null ? ParsePattern(matchAttr.Value, element) : null,
            Priority = priorityAttr != null ? ParsePriorityValue(priorityAttr.Value, GetSourceLocation(element)) : null,
            Modes = modes,
            As = asAttr != null ? ParseSequenceType(asAttr.Value, element) : null,
            Parameters = parameters,
            Body = new XsltSequenceConstructor { Instructions = bodyInstructions },
            Version = versionAttr?.Value ?? moduleVersion,
            BaseUri = ResolveEffectiveBaseUri(element),
            DefaultCollation = ResolveDefaultCollation(element.Attribute("default-collation")?.Value),
            ContextItemUse = contextItemUse,
            ContextItemAs = contextItemAs,
            Visibility = ParseVisibility(element.Attribute("visibility")?.Value),
            VisibilityAttr = element.Attribute("visibility")?.Value.Trim()
        };
    }


    /// <summary>
    /// XTSE0020: throw when a present xsl:output yes-or-no attribute carries a value outside
    /// the permitted set ("yes"/"no"/"true"/"false"/"1"/"0"). Absent attributes are ignored.
    /// </summary>
    private static void ValidateYesNoOutputAttribute(XElement element, string attrName)
    {
        var attr = element.Attribute(attrName);
        if (attr == null) return;
        if (ParseYesNo(attr) == null)
            throw new XsltException($"XTSE0020: Invalid value '{attr.Value}' for xsl:output {attrName} attribute (must be yes, no, true, false, 1, or 0)");
    }


    /// <summary>
    /// XTSE0020: the disable-output-escaping attribute (on xsl:text / xsl:value-of) is a
    /// yes-or-no; reject any present value outside the permitted set (e.g. "YES", " ").
    /// </summary>
    private static void ValidateDoeAttribute(XAttribute? attr, SourceLocation? location)
    {
        if (attr == null) return;
        if (ParseYesNo(attr) == null)
            throw new XsltException($"XTSE0020: Invalid disable-output-escaping value '{attr.Value}' (must be yes, no, true, false, 1, or 0)", location);
    }


    /// <summary>Validates an XML PubidLiteral (public identifier) character set.</summary>
    private static bool IsValidPublicId(string value)
    {
        foreach (var c in value)
        {
            bool ok = c is ' ' or '\r' or '\n'
                || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || "-'()+,./:=?;!*#@$_%".Contains(c, StringComparison.Ordinal);
            if (!ok) return false;
        }
        return true;
    }


    private static void ValidateSingleCharAttr(XElement element, string attrName)
    {
        var attr = element.Attribute(attrName);
        if (attr != null && new System.Globalization.StringInfo(attr.Value).LengthInTextElements != 1)
            throw new XsltException(
                $"XTSE0020: Attribute '{attrName}' must be a single character, got '{attr.Value}'",
                GetSourceLocation(element));
    }


    /// <summary>
    /// Extracts the first text element (Unicode codepoint, including surrogate pairs) from an attribute value.
    /// Returns null if the attribute is not present.
    /// </summary>
    private static string? GetFirstTextElement(XElement element, string attrName)
    {
        var attr = element.Attribute(attrName);
        if (attr == null) return null;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(attr.Value);
        return enumerator.MoveNext() ? enumerator.GetTextElement() : null;
    }


    /// <summary>
    /// Gets the first Rune from a text element string (for Unicode property checks).
    /// </summary>
    private static System.Text.Rune GetFirstRune(string textElement)
    {
        System.Text.Rune.DecodeFromUtf16(textElement, out var rune, out _);
        return rune;
    }


    /// <summary>
    /// Checks for a conflicting attribute value during merge. Returns a conflict description if found,
    /// or null if no conflict. Only reports a conflict when BOTH declarations explicitly set the
    /// attribute to different values. Does NOT throw — conflicts are deferred until after import resolution.
    /// </summary>
    private static string? FindDecimalFormatConflict(string attrName, object existingVal, object newVal,
        XElement newElement, HashSet<string> existingExplicit)
    {
        // Only conflict when both declarations explicitly set the attribute to different values
        var newExplicit = newElement.Attribute(attrName) != null;
        if (newExplicit && existingExplicit.Contains(attrName) && !Equals(existingVal, newVal))
            return $"XTSE1290: Conflicting values for '{attrName}' in decimal-format declaration";
        return null;
    }


    private static OnNoMatchBehavior ParseOnNoMatchBehavior(string value, XElement element) => value.Trim() switch
    {
        "deep-copy" => OnNoMatchBehavior.DeepCopy,
        "shallow-copy" => OnNoMatchBehavior.ShallowCopy,
        "deep-skip" => OnNoMatchBehavior.DeepSkip,
        "shallow-skip" => OnNoMatchBehavior.ShallowSkip,
        "text-only-copy" => OnNoMatchBehavior.TextOnlyCopy,
        "fail" => OnNoMatchBehavior.Fail,
        _ => throw new XsltException(
            $"XTSE0020: Invalid value '{value}' for attribute 'on-no-match' on xsl:mode (must be 'deep-copy', 'shallow-copy', 'deep-skip', 'shallow-skip', 'text-only-copy', or 'fail')",
            GetSourceLocation(element))
    };


    /// <summary>
    /// Normalizes a yes/no attribute value, also accepting boolean-like values (true/false/0/1).
    /// Throws XTSE0020 for invalid values like "No", "Yes" (case-sensitive).
    /// </summary>
    private static bool NormalizeYesNo(string? value, string attrName, string elementName, XElement element)
    {
        return value?.Trim() switch
        {
            null or "no" or "false" or "0" => false,
            "yes" or "true" or "1" => true,
            _ => throw new XsltException(
                $"XTSE0020: Invalid value '{value}' for attribute '{attrName}' on {elementName} (must be 'yes' or 'no')",
                GetSourceLocation(element))
        };
    }


    private static OnMultipleMatchBehavior ParseOnMultipleMatchBehavior(string value, XElement element) => value.Trim() switch
    {
        "use-last" => OnMultipleMatchBehavior.UseLast,
        "fail" => OnMultipleMatchBehavior.Fail,
        _ => throw new XsltException(
            $"XTSE0020: Invalid value '{value}' for attribute 'on-multiple-match' on xsl:mode (must be 'use-last' or 'fail')",
            GetSourceLocation(element))
    };


    private static Visibility ParseVisibility(string? value) => value switch
    {
        "public" => Visibility.Public,
        "private" => Visibility.Private,
        "final" => Visibility.Final,
        "abstract" => Visibility.Abstract,
        "hidden" => Visibility.Hidden,
        // Default per XSLT 3.0 §3.5: components declared at the top level of an
        // xsl:package element default to private, but components in any other
        // stylesheet module (the common case) default to public. Defaulting to
        // Private here broke `xsl:evaluate` calls into ordinary stylesheet
        // functions like Docbook TNG's fp:pi-from-list — XTDE3160 fired even
        // though the function was correctly accessible from the rest of the
        // stylesheet. Package-aware code paths set Private explicitly when
        // needed; this default covers the non-package common case.
        _ => Visibility.Public
    };


    private XsltInstruction ParseInstruction(XElement element)
    {
        // Evaluate use-when BEFORE any parsing (spec §3.8 conditional inclusion)
        if (!ShouldIncludeElement(element))
            return new XsltNoOp { Location = GetSourceLocation(element) };

        var location = GetSourceLocation(element);
        var prevContext = _nsContext;
        _nsContext = element;

        // Scope default-mode: check for default-mode attribute on XSLT elements
        // or xsl:default-mode on literal result elements.
        // Per XSLT spec 3.5.1, default-mode applies "within or on" the element.
        var prevDefaultMode = _currentDefaultMode;
        if (element.Name.Namespace == XsltNs)
        {
            var dmAttr = element.Attribute("default-mode");
            if (dmAttr != null)
                _currentDefaultMode = dmAttr.Value == "#unnamed" ? null : ParseQName(dmAttr.Value, element);
        }
        else
        {
            var dmAttr = element.Attribute(XsltNs + "default-mode");
            if (dmAttr != null)
                _currentDefaultMode = dmAttr.Value == "#unnamed" ? null : ParseQName(dmAttr.Value, element);
        }

        try
        {
            // Check if it's an XSLT instruction
            if (element.Name.Namespace == XsltNs)
            {
                var instruction = element.Name.LocalName switch
                {
                    "apply-templates" => ParseApplyTemplates(element, location),
                    "call-template" => ParseCallTemplate(element, location),
                    "apply-imports" => ParseApplyImports(element, location),
                    "next-match" => ParseNextMatch(element, location),
                    "for-each" => ParseForEach(element, location),
                    "for-each-group" => ParseForEachGroup(element, location),
                    "iterate" => ParseIterate(element, location),
                    "if" => ParseIf(element, location),
                    "choose" => ParseChoose(element, location),
                    "switch" => ParseSwitch(element, location),
                    "record" => ParseRecord(element, location),
                    "for-each-member" => ParseForEachMember(element, location),
                    "try" => ParseTry(element, location),
                    "element" => ParseElement(element, location),
                    "attribute" => ParseAttribute(element, location),
                    "text" => ParseText(element, location),
                    "value-of" => ParseValueOf(element, location),
                    "copy" => ParseCopy(element, location),
                    "copy-of" => ParseCopyOf(element, location),
                    "sequence" => ParseSequenceInstr(element, location),
                    "comment" => ParseComment(element, location),
                    "processing-instruction" => ParsePI(element, location),
                    "namespace" => ParseNamespaceInstr(element, location),
                    "document" => ParseDocument(element, location),
                    "result-document" => ParseResultDocument(element, location),
                    "message" => ParseMessage(element, location),
                    "assert" => ParseAssert(element, location),
                    "variable" => ParseVariableInstr(element, location),
                    "param" => throw new XsltException("XTSE0010: xsl:param is not allowed here; it can only appear at the start of xsl:template, xsl:function, or xsl:iterate", location),
                    "number" => ParseNumber(element, location),
                    "perform-sort" => ParsePerformSort(element, location),
                    "analyze-string" => ParseAnalyzeString(element, location),
                    "break" => ParseBreak(element, location),
                    "next-iteration" => ParseNextIteration(element, location),
                    "merge" => ParseMerge(element, location),
                    "fork" => ParseFork(element, location),
                    "map" => ParseMap(element, location),
                    "map-entry" => ParseMapEntry(element, location),
                    "array" => ParseArray(element, location),
                    "array-member" => ParseArrayMember(element, location),
                    "fallback" => ParseFallbackAsNoOp(element, location),
                    "where-populated" => ParseWherePopulated(element, location),
                    "on-empty" => ParseOnEmpty(element, location),
                    "on-non-empty" => ParseOnNonEmpty(element, location),
                    "evaluate" => ParseEvaluate(element, location),
                    "source-document" => ParseSourceDocument(element, location),
                    "stream" => ParseStream(element, location),
                    _ => ParseUnknownInstruction(element, location)
                };
                // Propagate standard attributes from XSLT elements (per XSLT 3.0 §3.5)
                instruction.Version = element.Attribute("version")?.Value;
                instruction.DefaultCollation = ResolveDefaultCollation(element.Attribute("default-collation")?.Value);
                // xml:base on XSLT instructions overrides static-base-uri() for expressions in scope
                if (element.Attribute(XNamespace.Xml + "base") != null)
                    instruction.StaticBaseUri = ResolveEffectiveBaseUri(element)?.ToString();
                return instruction;
            }

            // Check if this is an extension element (non-XSLT element in a namespace declared
            // via extension-element-prefixes). If so, use xsl:fallback children.
            if (IsExtensionElement(element))
            {
                // Recognize EXSLT exsl:document — treat as xsl:result-document
                if (element.Name.NamespaceName == "http://exslt.org/common"
                    && element.Name.LocalName == "document")
                {
                    return ParseExslDocument(element, location);
                }

                var fallbacks = element.Elements(XsltNs + "fallback").ToList();
                if (fallbacks.Count > 0)
                {
                    var instructions2 = new List<XsltInstruction>();
                    foreach (var fb in fallbacks)
                        instructions2.Add(ParseSequenceConstructor(fb));
                    return instructions2.Count == 1
                        ? instructions2[0]
                        : new XsltSequenceConstructor { Instructions = instructions2 };
                }
                // No fallback — generate a dynamic error that fires only if executed
                return new XsltDynamicError
                {
                    ErrorCode = "XTDE1450",
                    Message = $"Extension instruction '{element.Name}' is not supported and has no xsl:fallback",
                    Location = location
                };
            }

            // It's a literal result element
            return ParseLiteralResultElement(element, location);
        }
        finally
        {
            _nsContext = prevContext;
            _currentDefaultMode = prevDefaultMode;
        }
    }


    /// <summary>
    /// Checks if an element is in an extension namespace (declared via extension-element-prefixes
    /// on the stylesheet or on ancestor LRE/XSLT elements).
    /// </summary>
    private bool IsExtensionElement(XElement element)
    {
        var nsUri = element.Name.NamespaceName;
        if (string.IsNullOrEmpty(nsUri)) return false;

        // Check global stylesheet-level extension namespace URIs
        if (_extensionNamespaces.Contains(nsUri)) return true;

        // Walk element and ancestors looking for scoped extension-element-prefixes declarations
        for (var ancestor = element; ancestor != null; ancestor = ancestor.Parent)
        {
            // Check xsl:extension-element-prefixes on LRE ancestors
            var extAttr = ancestor.Attribute(XsltNs + "extension-element-prefixes")
                       ?? ancestor.Attribute("extension-element-prefixes");
            if (extAttr != null)
            {
                foreach (var prefix in extAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (prefix == "#default")
                    {
                        var defaultNs = ancestor.GetDefaultNamespace().NamespaceName;
                        if (defaultNs == nsUri) return true;
                    }
                    else
                    {
                        var prefixNs = ancestor.GetNamespaceOfPrefix(prefix);
                        if (prefixNs?.NamespaceName == nsUri) return true;
                    }
                }
            }
        }
        return false;
    }


    /// <summary>
    /// Parses xsl:fallback as a no-op, but still evaluates use-when attributes on descendant
    /// elements per XSLT spec — use-when errors must still be raised even in ignored content.
    /// </summary>
    private XsltNoOp ParseFallbackAsNoOp(XElement element, SourceLocation? location)
    {
        // Walk descendant elements and evaluate use-when attributes to surface errors
        foreach (var desc in element.Descendants())
        {
            ShouldIncludeElement(desc);
        }
        return new XsltNoOp { Location = location };
    }


    private XsltInstruction ParseUnknownInstruction(XElement element, SourceLocation? location)
    {
        // Forwards compatibility: if the effective version for this element is > 3.0,
        // extract and parse xsl:fallback children instead of throwing.
        var versionAttr = element.Attribute("version")?.Value;
        var effectiveVersion = versionAttr ?? GetEffectiveVersion(element);
        if (ParseVersionNumber(effectiveVersion) > 3.0m)
        {
            var fallbacks = element.Elements(XsltNs + "fallback").ToList();
            if (fallbacks.Count == 0)
                throw new XsltException($"XTSE0010: Unknown XSLT instruction '{element.Name.LocalName}' with no xsl:fallback", location);

            var instructions = new List<XsltInstruction>();
            foreach (var fb in fallbacks)
                instructions.Add(ParseSequenceConstructor(fb));
            return instructions.Count == 1
                ? instructions[0]
                : new XsltSequenceConstructor { Instructions = instructions };
        }

        // Not "unknown": most of what lands here is a real XSLT element in the wrong place —
        // a declaration (xsl:key, xsl:template, xsl:include) or a child-only element (xsl:sort,
        // xsl:with-param) used as an instruction. The message used to say "Unknown XSLT
        // instruction: include" with no code, naming the wrong thing and matching nothing.
        // xsl:include and xsl:import have their own codes for "must be top-level".
        var name = element.Name.LocalName;
        var code = name switch
        {
            "include" => "XTSE0170",
            "import" => "XTSE0190",
            _ => "XTSE0010",
        };
        throw new XsltException($"{code}: xsl:{name} is not allowed in a sequence constructor", location);
    }


    private static void ValidateNextIterationParams(XsltSequenceConstructor body, HashSet<QName> iterateParams, SourceLocation? location)
    {
        foreach (var instr in body.Instructions)
        {
            if (instr is XsltNextIteration ni)
            {
                foreach (var wp in ni.WithParams)
                {
                    if (!iterateParams.Contains(wp.Name))
                        throw new XsltException($"XTSE3130: xsl:next-iteration references parameter '{wp.Name}' which is not declared on the enclosing xsl:iterate", location);
                }
            }
            else if (instr is XsltIf ifInstr)
            {
                ValidateNextIterationParams(ifInstr.Then, iterateParams, location);
            }
            else if (instr is XsltChoose choose)
            {
                foreach (var when in choose.When)
                    ValidateNextIterationParams(when.Body, iterateParams, location);
                if (choose.Otherwise != null)
                    ValidateNextIterationParams(choose.Otherwise, iterateParams, location);
            }
        }
    }


    /// <summary>
    /// Parses xsl:record (XSLT 4.0) — constructs a record (map with string keys).
    /// </summary>
    private Ast.XsltRecord ParseRecord(XElement element, SourceLocation? location)
    {
        var entries = new List<(string Name, Ast.XsltSequenceConstructor Value)>();

        foreach (var child in element.Elements())
        {
            if (!ShouldIncludeElement(child)) continue;
            if (child.Name == XsltNs + "entry")
            {
                var nameAttr = child.Attribute("key")?.Value ?? child.Attribute("name")?.Value;
                if (nameAttr != null)
                    entries.Add((nameAttr, ParseSequenceConstructor(child)));
            }
        }

        return new Ast.XsltRecord
        {
            Location = location,
            Entries = entries
        };
    }


    /// <summary>
    /// Body of an xsl:when / xsl:otherwise inside xsl:switch, honouring the <c>select</c>
    /// shorthand: <c>&lt;xsl:when test="..." select="'bitmap'"/&gt;</c> is equivalent to a body
    /// of <c>&lt;xsl:sequence select="'bitmap'"/&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Previously only child nodes were read, so a branch written with @select matched and then
    /// produced nothing — the attribute was silently ignored rather than rejected, which reads
    /// as "the branch did not match" at every point downstream.
    /// </remarks>
    private Ast.XsltSequenceConstructor ParseBranchBody(XElement branch)
    {
        var selectAttr = branch.Attribute("select");
        if (selectAttr == null)
            return ParseSequenceConstructor(branch);

        if (branch.Nodes().Any(n => n is not XText t || !string.IsNullOrWhiteSpace(t.Value)))
            throw new XsltException(
                $"XTSE0010: {branch.Name.LocalName} has both a 'select' attribute and non-empty content; use one or the other",
                GetSourceLocation(branch));

        return new Ast.XsltSequenceConstructor
        {
            Instructions = [new Ast.XsltSequence { Select = ParseExpr(selectAttr.Value, selectAttr) }],
        };
    }


    /// <summary>
    /// Validates that a QName attribute value is syntactically valid (XTSE0020).
    /// Rejects AVT syntax ({...}) and invalid QName characters.
    /// </summary>
    private static void ValidateQNameValue(string value, string attrName, SourceLocation? location)
    {
        value = value.Trim();
        // EQName syntax Q{uri}local is always valid — skip all checks
        if (value.StartsWith("Q{", StringComparison.Ordinal))
            return;
        // AVT syntax {..} is not permitted in QName attributes
        if (value.Contains('{', StringComparison.Ordinal) || value.Contains('}', StringComparison.Ordinal))
            throw new XsltException($"XTSE0020: Attribute value templates are not permitted in the '{attrName}' attribute", location);
        if (value.Length > 0 && char.IsAsciiDigit(value[0]))
            throw new XsltException($"XTSE0020: Invalid QName '{value}' for '{attrName}' attribute: names must not start with a digit", location);
        if (value.Contains('/', StringComparison.Ordinal))
            throw new XsltException($"XTSE0020: Invalid QName '{value}' for '{attrName}' attribute", location);
        if (value.Contains("::", StringComparison.Ordinal))
            throw new XsltException($"XTSE0020: Invalid QName '{value}' for '{attrName}' attribute", location);
        // Check for common invalid NCName characters
        foreach (var ch in value)
        {
            if (ch == '!' || ch == '#' || ch == '@' || ch == '$' || ch == '%' ||
                ch == '(' || ch == ')' || ch == '[' || ch == ']' || ch == ',' ||
                ch == '=' || ch == '+' || ch == '<' || ch == '>' || ch == '?')
            {
                throw new XsltException($"XTSE0020: Invalid QName '{value}' for '{attrName}' attribute: character '{ch}' is not allowed", location);
            }
        }
    }


    /// <summary>
    /// Validates that an XSLT element required to be empty has no content other than comments/PIs (XTSE0260).
    /// </summary>
    private static void ValidateEmptyElement(XElement element)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XComment || node is XProcessingInstruction)
                continue;
            if (node is XText text && IsXmlWhitespaceOnly(text.Value) && element.Attribute(XNamespace.Xml + "space")?.Value != "preserve")
                continue;
            throw new XsltException($"XTSE0260: The xsl:{element.Name.LocalName} element must be empty",
                GetSourceLocation(element));
        }
    }


    /// <summary>
    /// Validates that an XSLT instruction element has no unknown attributes (XTSE0090).
    /// Namespace declaration attributes (xmlns:*) are always allowed.
    /// </summary>
    private static void ValidateAllowedAttributes(XElement element, SourceLocation? location, params string[] allowed)
    {
        var allowedSet = new HashSet<string>(allowed);
        // In forwards-compatible mode (version > 3.0), unknown attributes are silently ignored
        var elementVersion = element.Attribute("version")?.Value ?? GetEffectiveVersion(element);
        var forwardsCompatible = ParseVersionNumber(elementVersion) > 3.0m;
        foreach (var attr in element.Attributes())
        {
            if (attr.IsNamespaceDeclaration) continue;
            // Attributes in the XSLT namespace are not permitted on XSLT elements
            if (attr.Name.Namespace == XsltNs)
                throw new XsltException($"XTSE0090: Attribute 'xsl:{attr.Name.LocalName}' in the XSLT namespace is not permitted on an XSLT element", location);
            if (attr.Name.Namespace != XNamespace.None) continue; // Extension attributes in other namespaces OK
            // Standard attributes allowed on all XSLT elements (XSLT 3.0, section 3.7)
            if (attr.Name.LocalName is "use-when" or "default-collation" or "default-mode"
                or "default-validation" or "exclude-result-prefixes" or "expand-text"
                or "extension-element-prefixes" or "version" or "xpath-default-namespace") continue;
            // Shadow attributes (starting with '_') are compile-time AVTs, always allowed
            if (attr.Name.LocalName.StartsWith('_')) continue;
            if (!allowedSet.Contains(attr.Name.LocalName))
            {
                if (forwardsCompatible) continue; // FC mode: silently ignore unknown attributes
                throw new XsltException($"XTSE0090: Attribute '{attr.Name.LocalName}' is not permitted on xsl:{element.Name.LocalName}", location);
            }
        }
    }


    /// <summary>
    /// Checks if two mode declarations have conflicting use-accumulators.
    /// Comparison is set-based on resolved QNames (order and prefix don't matter).
    /// </summary>
    private static bool UseAccumulatorsConflict(XsltMode a, XsltMode b)
    {
        if (a.UseAccumulatorNames.Count == 0 || b.UseAccumulatorNames.Count == 0)
            return false; // one or both don't specify use-accumulators
        var setA = new HashSet<QName>(a.UseAccumulatorNames);
        var setB = new HashSet<QName>(b.UseAccumulatorNames);
        return !setA.SetEquals(setB);
    }


    private static bool VisibilityConflict(XsltMode a, XsltMode b)
    {
        if (a.VisibilityAttr == null || b.VisibilityAttr == null)
            return false; // one or both don't explicitly specify visibility
        return a.VisibilityAttr != b.VisibilityAttr;
    }


    /// <summary>
    /// Validates that select attribute and non-empty content are not both present.
    /// </summary>
    private static void ValidateSelectContentExclusive(XAttribute? selectAttr, XElement element, string errorCode, string instrName, SourceLocation? location)
    {
        if (selectAttr != null && element.Nodes().Any(n => n is XElement || (n is XText t && !string.IsNullOrWhiteSpace(t.Value))))
            throw new XsltException($"{errorCode}: {instrName} must not have both a select attribute and non-empty content", location);
    }


    private XsltValueOf ParseValueOf(XElement element, SourceLocation? location)
    {
        ValidateAllowedAttributes(element, location, "select", "separator", "disable-output-escaping");
        var selectAttr = element.Attribute("select");
        var separatorAttr = element.Attribute("separator");
        var doeAttr = element.Attribute("disable-output-escaping");
        ValidateDoeAttribute(doeAttr, location);

        // XTSE0870: select and non-empty content are mutually exclusive
        ValidateSelectContentExclusive(selectAttr, element, "XTSE0870", "xsl:value-of", location);

        var hasContent = selectAttr == null && element.Nodes().Any();

        return new XsltValueOf
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = hasContent ? ParseSequenceConstructor(element) : null,
            // null means "use default" — in ValueOfAsync, default is " " for select, "" for content,
            // and in 1.0 backwards-compatible mode, first-value semantics are used instead.
            Separator = separatorAttr != null ? ParseAvt(separatorAttr.Value, element, separatorAttr) : null,
            DisableOutputEscaping = doeAttr?.Value.Trim() is "yes" or "true" or "1"
        };
    }


    private XsltDocument ParseDocument(XElement element, SourceLocation? location)
    {
        var validationAttr = element.Attribute("validation");
        var typeAttr = element.Attribute("type");

        // XTSE1660: Non-schema-aware processor must reject type attribute
        if (typeAttr != null && ShouldRejectSchemaAware)
            throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the type attribute on xsl:document", location);
        if (validationAttr != null)
        {
            var v = validationAttr.Value.Trim();
            if (v is "strict" or "type" && ShouldRejectSchemaAware)
                throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept validation=\"{v}\" on xsl:document", location);
        }

        return new XsltDocument
        {
            Location = location,
            Validation = ParseValidationMode(validationAttr),
            // Type: rejected by XTSE1660 for non-schema-aware processors
            Content = ParseSequenceConstructor(element)
        };
    }


    /// <summary>
    /// Parse EXSLT exsl:document extension element as an xsl:result-document equivalent.
    /// exsl:document uses 'href' for the output URI and standard serialization attributes.
    /// </summary>
    private XsltResultDocument ParseExslDocument(XElement element, SourceLocation? location)
    {
        var hrefAttr = element.Attribute("href");
        var methodAttr = element.Attribute("method");
        var encodingAttr = element.Attribute("encoding");
        var indentAttr = element.Attribute("indent");

        return new XsltResultDocument
        {
            Location = location,
            Href = hrefAttr != null ? ParseAvt(hrefAttr.Value, element, hrefAttr) : null,
            Method = methodAttr != null ? ParseAvt(methodAttr.Value, element, methodAttr) : null,
            Encoding = encodingAttr != null ? ParseAvt(encodingAttr.Value, element, encodingAttr) : null,
            Indent = indentAttr != null ? ParseAvt(indentAttr.Value, element, indentAttr) : null,
            Content = ParseSequenceConstructor(element)
        };
    }



    /// <summary>
    /// Resolves the effective base URI for an element by walking up xml:base attributes
    /// per the XML Base specification (RFC 2396/3986).
    /// </summary>
    private Uri? ResolveEffectiveBaseUri(XElement element)
    {
        // Collect xml:base attributes from the element and its ancestors (innermost first)
        var xmlBaseAttrs = new List<string>();
        for (XElement? el = element; el != null; el = el.Parent)
        {
            var xmlBase = el.Attribute(XNamespace.Xml + "base");
            if (xmlBase != null)
                xmlBaseAttrs.Add(xmlBase.Value);
        }

        if (xmlBaseAttrs.Count == 0)
            return _baseUri;

        // Start from the stylesheet base URI, then apply xml:base values from outermost to innermost
        Uri? result = _baseUri;
        for (int i = xmlBaseAttrs.Count - 1; i >= 0; i--)
        {
            var xmlBase = xmlBaseAttrs[i];
            // Check for a genuine absolute URI reference: one that begins with an RFC 3986
            // scheme ("[A-Za-z][A-Za-z0-9+.-]*:"), e.g. http:, https:, urn:, or the
            // single-letter d: scheme. This is NOT a bare "/path" (which .NET would mis-parse
            // as file:///path on Linux, though XML Base treats it as a relative reference) and
            // NOT a "./..." relative reference. An absolute xml:base replaces the accumulated
            // base rather than resolving against it.
            if (HasUriScheme(xmlBase) && Uri.TryCreate(xmlBase, UriKind.Absolute, out var absUri))
                result = absUri;
            else if (result != null)
                result = new Uri(result, xmlBase);
            else if (Uri.TryCreate(xmlBase, UriKind.Relative, out _))
                continue; // Cannot resolve relative without a base — skip
        }

        return result;
    }


    /// <summary>
    /// True when <paramref name="s"/> begins with an RFC 3986 URI scheme
    /// ("[A-Za-z][A-Za-z0-9+.-]*:") — i.e. it is an absolute URI reference, not a relative
    /// path such as "/xml/" or "./foo".
    /// </summary>
    private static bool HasUriScheme(string s)
    {
        int colon = s.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !char.IsAsciiLetter(s[0]))
            return false;
        for (int i = 1; i < colon; i++)
        {
            char c = s[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '+' && c != '.' && c != '-')
                return false;
        }
        return true;
    }


    private XsltMessage ParseMessage(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select");
        var terminateAttr = element.Attribute("terminate");
        var errorCodeAttr = element.Attribute("error-code");

        // Validate terminate attribute: must be AVT or static "yes"/"no" (XSLT 2.0) or yes-or-no (XSLT 3.0)
        bool isTerminateAvt = false;
        if (terminateAttr != null)
        {
            var tv = terminateAttr.Value;
            bool isAvt = tv.Contains('{', StringComparison.Ordinal);
            if (!isAvt && tv is not ("yes" or "no" or "true" or "false" or "1" or "0"))
                throw new XsltException($"XTSE0020: Invalid value for 'terminate' attribute: '{tv}'. Must be 'yes' or 'no'.");
            isTerminateAvt = isAvt;
        }

        return new XsltMessage
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.Nodes().Any() ? ParseSequenceConstructor(element) : null,
            Terminate = terminateAttr?.Value is "yes" or "true" or "1",
            TerminateAvt = isTerminateAvt ? ParseAvt(terminateAttr!.Value, element, terminateAttr) : null,
            ErrorCode = errorCodeAttr?.Value
        };
    }


    private XsltAssert ParseAssert(XElement element, SourceLocation? location)
    {
        var test = ParseExpr(element.Attribute("test")!.Value, element.Attribute("test"));
        var selectAttr = element.Attribute("select");
        var errorCodeAttr = element.Attribute("error-code");

        return new XsltAssert
        {
            Location = location,
            Test = test,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.HasElements
                ? ParseSequenceConstructor(element)
                : null,
            ErrorCode = errorCodeAttr?.Value
        };
    }


    private XsltNextIteration ParseNextIteration(XElement element, SourceLocation? location)
    {
        // Validate lexical enclosure: must be inside xsl:iterate
        ValidateIterateChildLocation(element, "xsl:next-iteration", location);

        var withParams = new List<XsltWithParam>();
        foreach (var child in element.Elements(XsltNs + "with-param"))
        {
            if (!ShouldIncludeElement(child)) continue;
            withParams.Add(ParseWithParam(child));
        }

        // Validate: no duplicate with-param names (XTSE0670)
        var paramNames = new HashSet<QName>();
        foreach (var wp in withParams)
        {
            if (!paramNames.Add(wp.Name))
                throw new XsltException($"XTSE0670: Duplicate with-param name '{wp.Name}' in xsl:next-iteration", location);
        }

        return new XsltNextIteration
        {
            Location = location,
            WithParams = withParams
        };
    }


    private XsltMap ParseMap(XElement element, SourceLocation? location)
    {
        return new XsltMap
        {
            Location = location,
            Content = element.Nodes().Any() ? ParseSequenceConstructor(element) : null
        };
    }


    private XsltMapEntry ParseMapEntry(XElement element, SourceLocation? location)
    {
        var key = ParseExpr(element.Attribute("key")!.Value, element.Attribute("key"));
        var selectAttr = element.Attribute("select");

        // XTSE3280: xsl:map-entry with select must not have content other than xsl:fallback
        if (selectAttr != null)
        {
            foreach (var node in element.Nodes())
            {
                if (node is XElement child && child.Name != XsltNs + "fallback")
                    throw new XsltException("XTSE3280: xsl:map-entry with a select attribute must not have content other than xsl:fallback",
                        GetSourceLocation(child));
                if (node is XText text && !IsXmlWhitespaceOnly(text.Value))
                    throw new XsltException("XTSE3280: xsl:map-entry with a select attribute must not have text content", location);
            }
        }

        return new XsltMapEntry
        {
            Location = location,
            Key = key,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = ParseContentBody(element, selectAttr)
        };
    }


    private XsltArray ParseArray(XElement element, SourceLocation? location)
    {
        // select was not read at all, so <xsl:array select="1 to 5"/> — which has no child
        // nodes — parsed as an array with no content and produced []. Reported by Martin
        // Honnen, 2026-08-24. xsl:array-member directly below has always honoured select.
        var selectAttr = element.Attribute("select");
        var compositeAttr = element.Attribute("composite");

        if (selectAttr != null && element.Elements().Any())
        {
            throw new XsltException(
                "XTSE3185: xsl:array must not have both a select attribute and a sequence constructor",
                location);
        }

        return new XsltArray
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            // composite defaults to no: each ITEM of the value becomes its own member.
            Composite = ParseYesNoBoolean(compositeAttr, "composite", location),
            Content = selectAttr == null && element.Nodes().Any() ? ParseSequenceConstructor(element) : null
        };
    }


    private XsltArrayMember ParseArrayMember(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select");

        return new XsltArrayMember
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.HasElements
                ? ParseSequenceConstructor(element)
                : null
        };
    }


    /// <summary>
    /// Validates that an instruction (xsl:break/xsl:next-iteration) is in tail position
    /// within the xsl:iterate body. Tail position is recursive: the instruction must be
    /// last in its sequence constructor, AND if inside xsl:if/xsl:choose/xsl:try, those
    /// containers must themselves be in tail position.
    /// </summary>
    private static void ValidateLastInSequence(XElement element, string instrName, SourceLocation? location)
    {
        // Structural XSLT elements that are not sequence instructions
        var structuralElements = new HashSet<string>
        {
            "fallback", "catch", "param", "sort", "on-completion",
            "when", "otherwise", "matching-substring", "non-matching-substring"
        };

        // Containers that allow tail-position propagation (the instruction is in tail position
        // if it's last in the container AND the container is itself in tail position)
        var tailPositionContainers = new HashSet<string>
        {
            "if", "choose", "try", "when", "otherwise", "catch"
        };

        var current = element;
        while (current != null)
        {
            var foundSelf = false;
            foreach (var sibling in current.Parent!.Nodes())
            {
                if (sibling == current)
                {
                    foundSelf = true;
                    continue;
                }
                if (!foundSelf) continue;

                // After self, check for any non-structural, non-whitespace content
                if (sibling is XElement sibElem)
                {
                    if (sibElem.Name.Namespace == XsltNs && structuralElements.Contains(sibElem.Name.LocalName))
                        continue;
                    throw new XsltException($"XTSE3120: {instrName} must be in tail position within xsl:iterate body", location);
                }
                if (sibling is XText text && !string.IsNullOrWhiteSpace(text.Value))
                {
                    throw new XsltException($"XTSE3120: {instrName} must be in tail position within xsl:iterate body", location);
                }
            }

            // If the parent is xsl:iterate, we've reached the iterate body — tail position confirmed
            if (current.Parent!.Name == XsltNs + "iterate")
                return;

            // If the parent is a tail-position container (if/choose/try/when/otherwise/catch),
            // we need to check that the container itself is in tail position
            if (current.Parent!.Name.Namespace == XsltNs
                && tailPositionContainers.Contains(current.Parent!.Name.LocalName))
            {
                // For when/otherwise/catch, we check the grandparent (choose/try) for tail position
                current = current.Parent!.Name.LocalName is "when" or "otherwise" or "catch"
                    ? current.Parent!.Parent!  // check the xsl:choose or xsl:try itself
                    : current.Parent!;         // check the xsl:if or xsl:try itself
                continue;
            }

            // Parent is not a tail-position container — stop checking
            return;
        }
    }


    /// <summary>
    /// Determines if expand-text is active for a given element by walking up
    /// the ancestor chain. The nearest expand-text attribute wins, falling back
    /// to the stylesheet-level default.
    /// </summary>
    private bool IsExpandTextActive(XElement element)
    {
        // Walk ancestors from element up to stylesheet, nearest wins
        var current = element;
        while (current != null)
        {
            // Check xsl:expand-text (on LREs) or expand-text (on XSLT instructions).
            // Unprefixed expand-text is only an XSLT attribute on elements in the XSLT namespace;
            // on non-XSLT elements (LREs), it's just a regular attribute and should be ignored.
            var attr = current.Attribute(XsltNs + "expand-text")
                       ?? (current.Name.Namespace == XsltNs ? current.Attribute("expand-text") : null);
            if (attr != null)
            {
                var v = attr.Value.Trim();
                return v switch
                {
                    "yes" or "1" or "true" => true,
                    "no" or "0" or "false" => false,
                    _ => throw new XsltException(
                        $"XTSE0020: Invalid value '{attr.Value}' for expand-text attribute; must be yes|no|true|false|1|0",
                        GetSourceLocation(current))
                };
            }
            current = current.Parent;
        }
        return _defaultExpandText;
    }


    /// <summary>
    /// Returns true if the string is null, empty, or contains only XML whitespace
    /// characters (#x20, #x9, #xD, #xA). Unlike string.IsNullOrWhiteSpace(), this
    /// does NOT treat U+00A0 (non-breaking space) or other Unicode whitespace as whitespace.
    /// </summary>
    private static bool IsXmlWhitespaceOnly(string? value)
    {
        if (string.IsNullOrEmpty(value)) return true;
        foreach (var c in value)
        {
            if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                return false;
        }
        return true;
    }


    private static string GetEffectiveVersion(XElement element)
    {
        // Walk up the ancestor chain looking for a version attribute
        for (var el = element.Parent; el != null; el = el.Parent)
        {
            // On an XSLT element, check the "version" attribute directly
            if (el.Name.Namespace == XsltNs)
            {
                var v = el.Attribute("version")?.Value;
                if (v != null) return v;
            }
            else
            {
                // On a non-XSLT (LRE) element, check xsl:version
                var v = el.Attribute(XsltNs + "version")?.Value;
                if (v != null) return v;
            }
        }
        return "3.0";
    }


    /// <summary>
    /// Returns true if the effective version of the element is less than 2.0
    /// (i.e., XSLT 1.0 backwards-compatible mode).
    /// </summary>
    private static bool IsBackwardsCompatible(XElement element)
    {
        var version = element.Attribute("version")?.Value ?? GetEffectiveVersion(element);
        return ParseVersionNumber(version) < 2.0m;
    }


    private static decimal ParseVersionNumber(string? version)
    {
        if (version != null && decimal.TryParse(version.Trim(),
            System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint
            | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite,
            System.Globalization.CultureInfo.InvariantCulture, out var result))
            return result;
        return 3.0m; // Non-numeric version strings default to 3.0
    }


    private static bool IsXmlSpacePreserve(XElement element)
    {
        var current = element;
        while (current != null)
        {
            var attr = current.Attribute(XNamespace.Xml + "space");
            if (attr != null)
                return attr.Value == "preserve";
            current = current.Parent;
        }
        return false;
    }


    /// <summary>
    /// Computes the file-absolute (line, column) of the first character of the AVT's
    /// value text plus the module URI, from the source attribute. Returns (0, 0, null)
    /// when no attribute or no line info is available — ParseAvt then falls back to
    /// the legacy locationless ParseExpr path.
    /// </summary>
    private static (int Line, int Column, string? ModuleUri) ComputeAvtBasePosition(System.Xml.Linq.XAttribute? attribute)
    {
        if (attribute is not System.Xml.IXmlLineInfo li || !li.HasLineInfo()) return (0, 0, null);
        var attrName = attribute.Name.LocalName;
        if (!string.IsNullOrEmpty(attribute.Name.NamespaceName)
            && attribute.Parent?.GetPrefixOfNamespace(attribute.Name.Namespace) is { Length: > 0 } prefix)
            attrName = prefix + ":" + attrName;
        var valueStartCol = li.LinePosition + attrName.Length + 2;
        return (li.LineNumber, valueStartCol, attribute.Parent?.BaseUri);
    }


    /// <summary>
    /// D3 helper: compute the file-absolute base position for an AVT/TVT that comes
    /// from element text content. Uses the first descendant <see cref="XText"/>'s
    /// IXmlLineInfo. When the element has multiple text-node children (e.g.
    /// interrupted by xsl:value-of), only the first text node's position is used —
    /// inner-expression positions for later text nodes will be off but at least
    /// land in the same file/line range; full multi-text accuracy is a follow-up.
    /// </summary>
    private static (int Line, int Column, string? ModuleUri) ComputeTextBasePosition(XElement element)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText t && t is System.Xml.IXmlLineInfo tli && tli.HasLineInfo())
                return (tli.LineNumber, tli.LinePosition, element.BaseUri);
        }
        // Fallback to element's own start position
        if (element is System.Xml.IXmlLineInfo eli && eli.HasLineInfo())
            return (eli.LineNumber, eli.LinePosition, element.BaseUri);
        return (0, 0, element.BaseUri);
    }


    /// <summary>
    /// Counts newlines in <paramref name="text"/> from index 0 to (exclusive) <paramref name="offset"/>
    /// and returns <c>(linesAfterStart, columnOnLastLine)</c>. Used to map an offset
    /// inside an attribute value to a (line, col) within the value.
    /// </summary>
    private static (int Lines, int ColumnOnLastLine) OffsetToLineColumn(string text, int offset)
    {
        var lines = 0;
        var lineStart = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\n') { lines++; lineStart = i + 1; }
        }
        return (lines, offset - lineStart);
    }


    private static int FindMatchingBrace(string value, int openBrace)
    {
        var depth = 1;
        var inString = false;
        var stringChar = '\0';
        var commentDepth = 0;

        for (var i = openBrace + 1; i < value.Length; i++)
        {
            var c = value[i];

            if (inString)
            {
                if (c == stringChar)
                    inString = false;
                continue;
            }

            // Handle XPath comments (: ... :) which can nest
            if (commentDepth > 0)
            {
                if (c == '(' && i + 1 < value.Length && value[i + 1] == ':')
                {
                    commentDepth++;
                    i++; // skip ':'
                }
                else if (c == ':' && i + 1 < value.Length && value[i + 1] == ')')
                {
                    commentDepth--;
                    i++; // skip ')'
                }
                continue;
            }

            // Check for comment start
            if (c == '(' && i + 1 < value.Length && value[i + 1] == ':')
            {
                commentDepth++;
                i++; // skip ':'
                continue;
            }

            switch (c)
            {
                case '"':
                case '\'':
                    inString = true;
                    stringChar = c;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return i;
                    break;
            }
        }

        return -1;
    }


    /// <summary>
    /// Resolves a default-collation attribute value to the first recognized collation URI.
    /// Returns null if no recognized collation or value is null.
    /// </summary>
    private static string? ResolveDefaultCollation(string? value)
    {
        if (value == null) return null;
        var uris = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var uri in uris)
        {
            if (string.Equals(uri, "http://www.w3.org/2005/xpath-functions/collation/codepoint", StringComparison.Ordinal)
                || string.Equals(uri, "http://www.w3.org/2005/xpath-functions/collation/html-ascii-case-insensitive", StringComparison.Ordinal)
                || string.Equals(uri, "http://www.w3.org/2013/collation/UCA", StringComparison.Ordinal)
                || uri.StartsWith("http://www.w3.org/2013/collation/UCA?", StringComparison.Ordinal))
                return uri;
        }
        return null;
    }


    /// <summary>
    /// Strips XPath comments (: ... :) from an expression, handling nesting.
    /// Used to check if an AVT expression is effectively empty after comment removal.
    /// </summary>
    private static string StripXPathComments(string expr)
    {
        var sb = new System.Text.StringBuilder(expr.Length);
        var commentDepth = 0;
        for (var i = 0; i < expr.Length; i++)
        {
            var c = expr[i];
            if (commentDepth > 0)
            {
                if (c == '(' && i + 1 < expr.Length && expr[i + 1] == ':')
                {
                    commentDepth++;
                    i++;
                }
                else if (c == ':' && i + 1 < expr.Length && expr[i + 1] == ')')
                {
                    commentDepth--;
                    i++;
                }
            }
            else if (c == '(' && i + 1 < expr.Length && expr[i + 1] == ':')
            {
                commentDepth++;
                i++;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }


    /// <summary>
    /// Checks if a pattern has non-motionless predicates (predicates on element/document
    /// nodes that may require traversing children to evaluate). Predicates on leaf nodes
    /// (text, attribute, comment, PI) are considered motionless.
    /// </summary>
    private static bool HasNonMotionlessPredicates(XsltPattern pattern) => pattern switch
    {
        PathPattern pp => pp.Steps.Any(s => s.Predicates.Count > 0 && !IsLeafNodeTest(s.NodeTest)),
        UnionPattern up => up.Patterns.Any(HasNonMotionlessPredicates),
        DotPattern dp => dp.Predicates.Count > 0,
        _ => false
    };


    private static bool IsLeafNodeTest(PhoenixmlDb.XQuery.Ast.NodeTest test) => test switch
    {
        PhoenixmlDb.XQuery.Ast.KindTest kt => kt.Kind is XdmNodeKind.Text or XdmNodeKind.Attribute
            or XdmNodeKind.Comment or XdmNodeKind.ProcessingInstruction,
        _ => false // NameTest matches elements by default — not a leaf node
    };


    /// <summary>
    /// Marks a parsed pattern as having come from a parenthesized group, so path patterns
    /// disable the child-or-top rule. Recurses into union alternatives. See W3C match-215.
    /// </summary>
    private static XsltPattern MarkParenthesized(XsltPattern pattern) => pattern switch
    {
        PathPattern pp => new PathPattern { Steps = pp.Steps, DisableChildOrTop = true },
        UnionPattern up => new UnionPattern { Patterns = up.Patterns.Select(MarkParenthesized).ToList() },
        _ => pattern
    };


    private static string ReadSegment(string pattern, ref int pos)
    {
        var start = pos;
        var bracketDepth = 0;
        var braceDepth = 0;
        while (pos < pattern.Length)
        {
            var c = pattern[pos];
            if (c == '[') bracketDepth++;
            else if (c == ']') bracketDepth--;
            else if (c == '{') braceDepth++;
            else if (c == '}') braceDepth--;
            else if (c == '/' && bracketDepth == 0 && braceDepth == 0) break;
            pos++;
        }
        return pattern[start..pos];
    }


    /// <summary>
    /// Splits a pattern at '|' delimiters outside of brackets and string literals.
    /// </summary>
    private static List<string> SplitUnionPattern(string pattern)
    {
        var parts = new List<string>();
        var bracketDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var start = 0;

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (inSingleQuote)
            {
                if (c == '\'') inSingleQuote = false;
            }
            else if (inDoubleQuote)
            {
                if (c == '"') inDoubleQuote = false;
            }
            else
            {
                switch (c)
                {
                    case '\'': inSingleQuote = true; break;
                    case '"': inDoubleQuote = true; break;
                    case '[': bracketDepth++; break;
                    case ']': bracketDepth--; break;
                    case '(' : bracketDepth++; break;
                    case ')': bracketDepth--; break;
                    case '|' when bracketDepth == 0:
                        parts.Add(pattern[start..i]);
                        start = i + 1;
                        break;
                    // XSLT 3.0: 'union' keyword as synonym for '|' in patterns.
                    // But per the leading-lone-slash rule, a '/' immediately preceding 'union'
                    // makes it a name test ("/union" = child::union), not the union operator —
                    // "union" can begin a RelativePathExpr. So don't split when the previous
                    // non-whitespace character is '/'. See W3C match-038.
                    case 'u' when bracketDepth == 0
                        && i + 5 <= pattern.Length
                        && pattern.AsSpan(i, 5).SequenceEqual("union")
                        && (i == 0 || !char.IsLetterOrDigit(pattern[i - 1]) && pattern[i - 1] != '_' && pattern[i - 1] != '-')
                        && (i + 5 >= pattern.Length || !char.IsLetterOrDigit(pattern[i + 5]) && pattern[i + 5] != '_' && pattern[i + 5] != '-')
                        && PrecedingNonWhitespaceChar(pattern, i) != '/':
                        parts.Add(pattern[start..i]);
                        start = i + 5;
                        i += 4; // loop will increment to i+5
                        break;
                }
            }
        }

        parts.Add(pattern[start..]);
        return parts;
    }


    /// <summary>
    /// Returns the nearest non-whitespace character before <paramref name="index"/>, or '\0' if none.
    /// </summary>
    private static char PrecedingNonWhitespaceChar(string s, int index)
    {
        for (var k = index - 1; k >= 0; k--)
        {
            if (!char.IsWhiteSpace(s[k]))
                return s[k];
        }
        return '\0';
    }


    /// <summary>
    /// Expands parenthesized unions within a path pattern into top-level union alternatives.
    /// E.g. "x/(child::a|descendant::b)" → ["x/child::a", "x/descendant::b"]
    /// Returns null if no expansion is needed.
    /// </summary>
    private static List<string>? ExpandParenthesizedUnions(string pattern)
    {
        // Scan the pattern for a '(' that is at the path-step level (not inside brackets/quotes)
        var bracketDepth = 0;
        var parenDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (inSingleQuote) { if (c == '\'') inSingleQuote = false; continue; }
            if (inDoubleQuote) { if (c == '"') inDoubleQuote = false; continue; }
            switch (c)
            {
                case '\'': inSingleQuote = true; break;
                case '"': inDoubleQuote = true; break;
                case '[': bracketDepth++; break;
                case ']': bracketDepth--; break;
                case '(' when bracketDepth == 0 && parenDepth == 0:
                {
                    // Found a top-level '(' — find the matching ')'
                    var openPos = i;
                    var depth = 1;
                    var j = i + 1;
                    while (j < pattern.Length && depth > 0)
                    {
                        var cc = pattern[j];
                        if (cc == '(') depth++;
                        else if (cc == ')') depth--;
                        j++;
                    }
                    if (depth != 0) break; // unmatched paren, skip
                    var closePos = j - 1;
                    var inner = pattern[(openPos + 1)..closePos];

                    // Split inner on '|' at depth 0
                    var alternatives = SplitUnionPattern(inner);
                    if (alternatives.Count <= 1) { parenDepth++; break; } // no union, skip

                    // Build expanded patterns: prefix + alternative + suffix
                    var prefix = pattern[..openPos];
                    var suffix = pattern[(closePos + 1)..];
                    var result = new List<string>();
                    foreach (var alt in alternatives)
                    {
                        result.Add((prefix + alt.Trim() + suffix).Trim());
                    }
                    return result;
                }
                case '(': parenDepth++; break;
                case ')': parenDepth--; break;
            }
        }

        return null; // no expansion needed
    }


    /// <summary>
    /// Splits a pattern on 'except' or 'intersect' keywords at depth 0 (outside brackets, parens, quotes, braces).
    /// Returns null if no such keyword is found.
    /// Result: list of (Part, IsExcept) where IsExcept is true for 'except', false for 'intersect'.
    /// The first element's IsExcept value is meaningless (it's the leftmost operand).
    /// </summary>
    private static List<(string Part, bool IsExcept)>? SplitExceptIntersect(string pattern)
    {
        // Find all top-level 'except'/'intersect' keyword positions
        var splits = new List<(int Position, int Length, bool IsExcept)>();
        var bracketDepth = 0;
        var parenDepth = 0;
        var braceDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (inSingleQuote) { if (c == '\'') inSingleQuote = false; continue; }
            if (inDoubleQuote) { if (c == '"') inDoubleQuote = false; continue; }
            switch (c)
            {
                case '\'': inSingleQuote = true; continue;
                case '"': inDoubleQuote = true; continue;
                case '[': bracketDepth++; continue;
                case ']': bracketDepth--; continue;
                case '(': parenDepth++; continue;
                case ')': parenDepth--; continue;
                case '{': braceDepth++; continue;
                case '}': braceDepth--; continue;
            }
            if (bracketDepth != 0 || parenDepth != 0 || braceDepth != 0) continue;

            // Check word boundary before the keyword
            if (i > 0 && (char.IsLetterOrDigit(pattern[i - 1]) || pattern[i - 1] == '-' || pattern[i - 1] == '_'))
                continue;

            if (c == 'e' && i + 6 <= pattern.Length && pattern.AsSpan(i, 6).SequenceEqual("except")
                && (i + 6 >= pattern.Length || !char.IsLetterOrDigit(pattern[i + 6]) && pattern[i + 6] != '-' && pattern[i + 6] != '_'))
            {
                splits.Add((i, 6, true));
                i += 5; // skip rest of keyword
            }
            else if (c == 'i' && i + 9 <= pattern.Length && pattern.AsSpan(i, 9).SequenceEqual("intersect")
                && (i + 9 >= pattern.Length || !char.IsLetterOrDigit(pattern[i + 9]) && pattern[i + 9] != '-' && pattern[i + 9] != '_'))
            {
                splits.Add((i, 9, false));
                i += 8;
            }
        }

        if (splits.Count == 0) return null;

        var parts = new List<(string Part, bool IsExcept)>();
        var prevEnd = 0;
        foreach (var (pos, len, isExcept) in splits)
        {
            parts.Add((pattern[prevEnd..pos].Trim(), isExcept));
            prevEnd = pos + len;
        }
        // Add the final part after the last keyword
        parts.Add((pattern[prevEnd..].Trim(), splits[^1].IsExcept));

        return parts;
    }


    /// <summary>
    /// Handles parenthesized except/intersect within path patterns.
    /// E.g., "x/(descendant::a except child::a)" → ExceptPattern(ParsePattern("x/descendant::a"), ParsePattern("x/child::a"))
    /// Returns null if no parenthesized except/intersect is found.
    /// </summary>
    private XsltPattern? TryExpandParenthesizedExceptIntersect(string pattern, XElement context)
    {
        var bracketDepth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (inSingleQuote) { if (c == '\'') inSingleQuote = false; continue; }
            if (inDoubleQuote) { if (c == '"') inDoubleQuote = false; continue; }
            switch (c)
            {
                case '\'': inSingleQuote = true; break;
                case '"': inDoubleQuote = true; break;
                case '[': bracketDepth++; break;
                case ']': bracketDepth--; break;
                case '(' when bracketDepth == 0:
                {
                    var openPos = i;
                    var depth = 1;
                    var j = i + 1;
                    while (j < pattern.Length && depth > 0)
                    {
                        if (pattern[j] == '(') depth++;
                        else if (pattern[j] == ')') depth--;
                        j++;
                    }
                    if (depth != 0) break;
                    var closePos = j - 1;
                    var inner = pattern[(openPos + 1)..closePos];

                    // Check if inner contains except/intersect at depth 0
                    var excIntParts = SplitExceptIntersect(inner);
                    if (excIntParts == null || excIntParts.Count < 2) break;

                    // Build expanded patterns: prefix + each part + suffix
                    var prefix = pattern[..openPos];
                    var suffix = pattern[(closePos + 1)..];

                    // Build the left-associative except/intersect chain with prefix/suffix
                    var result = ParsePattern((prefix + excIntParts[0].Part.Trim() + suffix).Trim(), context);
                    for (var idx = 1; idx < excIntParts.Count; idx++)
                    {
                        var right = ParsePattern((prefix + excIntParts[idx].Part.Trim() + suffix).Trim(), context);
                        result = excIntParts[idx].IsExcept
                            ? new ExceptPattern { Left = result, Right = right }
                            : new IntersectPattern { Left = result, Right = right };
                    }
                    return result;
                }
            }
        }

        return null;
    }


    private static NodeTest ParseNodeTest(string name, XElement context, bool isAttribute = false)
    {
        // Kind tests
        if (name == "node()")
            return new KindTest { Kind = XdmNodeKind.None }; // None = any node
        if (name == "text()")
            return new KindTest { Kind = XdmNodeKind.Text };
        if (name == "comment()")
            return new KindTest { Kind = XdmNodeKind.Comment };
        if (name.StartsWith("processing-instruction(", StringComparison.Ordinal))
        {
            // Extract PI target name from processing-instruction('name') or processing-instruction("name")
            var inner = name["processing-instruction(".Length..^1].Trim();
            NameTest? piNameTest = null;
            if (inner.Length >= 2 && (inner[0] == '\'' || inner[0] == '"') && inner[^1] == inner[0])
            {
                var piName = inner[1..^1];
                if (!string.IsNullOrEmpty(piName))
                    piNameTest = new NameTest { LocalName = piName };
            }
            else if (!string.IsNullOrEmpty(inner))
            {
                // Unquoted PI name — validate it's a valid NCName (no colons)
                if (inner.Contains(':', StringComparison.Ordinal))
                    throw new XsltException($"XTSE0340: Processing instruction name '{inner}' must not contain a colon");
                piNameTest = new NameTest { LocalName = inner };
            }
            return new KindTest { Kind = XdmNodeKind.ProcessingInstruction, Name = piNameTest };
        }
        if (name == "document-node()")
            return new KindTest { Kind = XdmNodeKind.Document };
        // document-node(element(E)) or document-node(element(E, type))
        if (name.StartsWith("document-node(element(", StringComparison.Ordinal) && name.EndsWith("))", StringComparison.Ordinal))
        {
            var inner = name["document-node(element(".Length..^2].Trim();
            NameTest? elemName = null;
            if (inner.Length > 0 && inner != "*")
            {
                // Handle element(name) or element(name, type) — only use name part
                var commaIdx = inner.IndexOf(',', StringComparison.Ordinal);
                var elemLocalName = commaIdx >= 0 ? inner[..commaIdx].Trim() : inner;
                elemName = ParseNameTest(elemLocalName, context, isAttribute: false) as NameTest;
            }
            return new KindTest { Kind = XdmNodeKind.Document, DocumentElementTest = elemName };
        }
        if (name.StartsWith("document-node(schema-element(", StringComparison.Ordinal) && name.EndsWith("))", StringComparison.Ordinal))
        {
            // Treat schema-element the same as element for non-schema-aware processor
            var inner = name["document-node(schema-element(".Length..^2].Trim();
            NameTest? elemName = inner.Length > 0 ? ParseNameTest(inner, context, isAttribute: false) as NameTest : null;
            return new KindTest { Kind = XdmNodeKind.Document, DocumentElementTest = elemName };
        }
        if (name == "namespace-node()")
            return new KindTest { Kind = XdmNodeKind.Namespace };

        // XSLT 2.0+: element() and attribute() KindTest patterns
        if (name.StartsWith("element(", StringComparison.Ordinal) && name.EndsWith(')'))
            return ParseKindTestWithArgs(XdmNodeKind.Element, name["element(".Length..^1].Trim(), context);
        if (name.StartsWith("schema-element(", StringComparison.Ordinal) && name.EndsWith(')'))
            return ParseKindTestWithArgs(XdmNodeKind.Element, name["schema-element(".Length..^1].Trim(), context);
        if (name.StartsWith("attribute(", StringComparison.Ordinal) && name.EndsWith(')'))
            return ParseKindTestWithArgs(XdmNodeKind.Attribute, name["attribute(".Length..^1].Trim(), context);
        if (name.StartsWith("schema-attribute(", StringComparison.Ordinal) && name.EndsWith(')'))
            return ParseKindTestWithArgs(XdmNodeKind.Attribute, name["schema-attribute(".Length..^1].Trim(), context);

        return ParseNameTest(name, context, isAttribute);
    }


    /// <summary>
    /// Parses the inner arguments of element(...) or attribute(...) KindTest patterns.
    /// Handles: element(), element(*), element(name), element(*, type), element(name, type).
    /// </summary>
    private static KindTest ParseKindTestWithArgs(XdmNodeKind kind, string inner, XElement context)
    {
        if (inner.Length == 0 || inner == "*")
            return new KindTest { Kind = kind };

        // Split on comma (for element(name, type) or element(*, type))
        var commaIdx = inner.IndexOf(',', StringComparison.Ordinal);
        if (commaIdx >= 0)
        {
            var namePart = inner[..commaIdx].Trim();
            var typePart = inner[(commaIdx + 1)..].Trim();

            NameTest? nameTest = null;
            if (namePart != "*" && !string.IsNullOrEmpty(namePart))
                nameTest = ParseNameTest(namePart, context);

            XdmTypeName? typeName = null;
            if (!string.IsNullOrEmpty(typePart))
            {
                if (typePart.Contains(':', StringComparison.Ordinal))
                {
                    var typeParts = typePart.Split(':');
                    var typeNs = context.GetNamespaceOfPrefix(typeParts[0])?.NamespaceName;
                    typeName = new XdmTypeName { Prefix = typeParts[0], NamespaceUri = typeNs, LocalName = typeParts[1] };
                }
                else
                {
                    typeName = new XdmTypeName { LocalName = typePart };
                }
            }

            return new KindTest { Kind = kind, Name = nameTest, TypeName = typeName };
        }

        // Single argument: element(name)
        return new KindTest
        {
            Kind = kind,
            Name = ParseNameTest(inner, context)
        };
    }


    private static NameTest ParseNameTest(string name, XElement context, bool isAttribute = false)
    {
        if (name == "*")
        {
            return new NameTest { LocalName = "*" };
        }

        // EQName: Q{uri}local
        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var closeBrace = name.IndexOf('}', 2);
            if (closeBrace > 0)
            {
                var uri = name[2..closeBrace];
                var local = name[(closeBrace + 1)..];
                return new NameTest
                {
                    NamespaceUri = string.IsNullOrEmpty(uri) ? null : uri,
                    LocalName = local
                };
            }
        }

        if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            var prefix = parts[0];

            // *:NCName means wildcard namespace, specific local name
            if (prefix == "*")
            {
                return new NameTest
                {
                    Prefix = "*",
                    NamespaceUri = "*",
                    LocalName = parts[1]
                };
            }

            var ns = context.GetNamespaceOfPrefix(prefix)?.NamespaceName;
            if (ns == null)
                throw new XsltException($"XTSE0280: Prefix '{prefix}' is not declared");
            return new NameTest
            {
                Prefix = prefix,
                NamespaceUri = ns,
                LocalName = parts[1]
            };
        }

        // Validate the name is a valid NCName — reject names starting with a digit
        // (e.g., "1223" in match="name/1223" or count="2+2")
        if (name.Length > 0 && name != "*" && char.IsAsciiDigit(name[0]))
            throw new XsltException($"XTSE0340: '{name}' is not a valid name test in a pattern");

        // Apply xpath-default-namespace for unprefixed element name tests
        // Per spec, xpath-default-namespace does NOT apply to attribute names
        if (!isAttribute)
        {
            var xdn = GetXpathDefaultNamespace(context);
            if (xdn != null)
                return new NameTest { LocalName = name, NamespaceUri = xdn };
        }

        return new NameTest { LocalName = name };
    }


    /// <summary>
    /// Parses content body for variables/params: handles both child elements and text-only content.
    /// </summary>
    private XsltSequenceConstructor? ParseContentBody(XElement element, XAttribute? selectAttr)
    {
        if (selectAttr != null) return null;
        if (element.HasElements) return ParseSequenceConstructor(element);
        if (!element.IsEmpty)
        {
            var textValue = element.Value;
            if (!string.IsNullOrEmpty(textValue))
                return new XsltSequenceConstructor { Instructions = [new XsltLiteralText { Value = textValue }] };
        }
        return null;
    }


    private static QName ParseQName(string name, XElement context)
    {
        name = name.Trim();

        // Handle EQName syntax: Q{namespace-uri}local-name
        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var closeBrace = name.IndexOf('}', 2);
            if (closeBrace > 1)
            {
                var nsUri = name[2..closeBrace];
                var localName = name[(closeBrace + 1)..];
                var nsId = ResolveNamespaceUri(nsUri);
                return new QName(nsId, localName) { ExpandedNamespace = nsUri };
            }
        }

        if (name.Contains(':', StringComparison.Ordinal))
        {
            var parts = name.Split(':');
            var ns = context.GetNamespaceOfPrefix(parts[0])?.NamespaceName;
            // XTSE0280: Prefixed QName must have an in-scope namespace binding
            if (ns == null)
                throw new XsltException($"XTSE0280: Namespace prefix '{parts[0]}' in QName '{name}' is not declared",
                    GetSourceLocation(context));
            // Assign/resolve the namespace ID (thread-safe intern)
            var nsId = ResolveNamespaceUri(ns);
            return new QName(nsId, parts[1], parts[0]);
        }

        return new QName(NamespaceId.None, name);
    }


    /// <summary>
    /// Parses a QName appearing in <c>xsl:output/@cdata-section-elements</c>. Unlike a bare
    /// <see cref="ParseQName"/>, an <em>unprefixed</em> name is resolved against the default
    /// namespace in scope on the <c>xsl:output</c> element (XSLT 2.0+), so a name like
    /// <c>h1</c> under <c>xmlns="…xhtml"</c> expands to <c>{…xhtml}h1</c>. This lets the
    /// element expanded-name match at serialization time carry the correct namespace on both
    /// sides. Prefixed and <c>Q{uri}local</c> names are resolved as usual. (W3C decl/output
    /// output-0138.)
    /// </summary>
    private static QName ParseCdataSectionQName(string name, XElement context)
    {
        var q = ParseQName(name, context);
        var trimmed = name.Trim();
        var isUnprefixed = q.Prefix == null && q.ExpandedNamespace == null
            && q.Namespace == NamespaceId.None
            && !trimmed.StartsWith("Q{", StringComparison.Ordinal);
        if (isUnprefixed)
        {
            var defNs = context.GetDefaultNamespace().NamespaceName;
            if (!string.IsNullOrEmpty(defNs))
                return new QName(ResolveNamespaceUri(defNs), q.LocalName);
        }
        return q;
    }


    /// <summary>
    /// Shifts every <see cref="SourceLocation"/> on the parsed expression tree from
    /// XPath-relative coordinates to file-absolute. Same arithmetic as
    /// <see cref="ShiftExpressionLocationsToFileAbsolute"/> but with explicitly
    /// supplied base position (D2 needs this for AVT inner expressions).
    /// </summary>
    private static void ShiftExpressionLocationsAt(
        XQueryExpression expr, int absoluteLine, int absoluteColumn, string? moduleUri)
    {
        WalkExpressions(expr, node =>
        {
            if (node.Location is not { } loc) return;
            var fileLine = absoluteLine + loc.Line - 1;
            var fileCol = loc.Line == 1 ? absoluteColumn + loc.Column : loc.Column;
            node.Location = loc with { Line = fileLine, Column = fileCol, Module = string.IsNullOrEmpty(moduleUri) ? loc.Module : moduleUri };
        });
    }


    /// <summary>
    /// Shifts every <see cref="SourceLocation"/> on the parsed expression tree from
    /// XPath-string-relative coordinates to absolute coordinates within the source
    /// XSLT file. The XPath text begins inside an attribute value; the value's start
    /// position is computed from the attribute's <see cref="System.Xml.IXmlLineInfo"/>
    /// plus the attribute name length and the <c>="</c> delimiter (3 chars).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Line offset (D5): XPaths can span multiple lines (rare but legal). Line N within
    /// the XPath maps to file line <c>(attr-line + N - 1)</c>. Column on line 1 of
    /// the XPath gets the value's start column added; subsequent lines start at
    /// column 1 of the file (no offset), which matches XML attribute-value continuation.
    /// </para>
    /// <para>
    /// Column conventions: ANTLR (used by the XQuery parser) reports columns 0-based;
    /// <see cref="System.Xml.IXmlLineInfo"/> reports them 1-based. We treat the
    /// <see cref="SourceLocation.Column"/> stored on parsed expressions as ANTLR's
    /// 0-based form and add the 1-based attribute-value start column directly — the
    /// off-by-one cancels out for line-1 columns since ANTLR's "first character" is 0
    /// while the attribute value's "first character" is at 1-based column N.
    /// </para>
    /// </remarks>
    private static void ShiftExpressionLocationsToFileAbsolute(
        XQueryExpression expr, System.Xml.Linq.XAttribute attribute)
    {
        if (attribute is not System.Xml.IXmlLineInfo li || !li.HasLineInfo()) return;
        var attrLine = li.LineNumber;
        // Position of the first character of the attribute value:
        //   attr-name + '="' = name.Length + 2 chars after the attribute name's start column.
        // The attribute's reported LinePosition points at the attribute name; add the
        // name length plus the '="' delimiter.
        var attrName = attribute.Name.LocalName;
        if (!string.IsNullOrEmpty(attribute.Name.NamespaceName)
            && attribute.Parent?.GetPrefixOfNamespace(attribute.Name.Namespace) is { Length: > 0 } prefix)
            attrName = prefix + ":" + attrName;
        var valueStartColumn = li.LinePosition + attrName.Length + 2;

        var moduleUri = attribute.Parent?.BaseUri;
        WalkExpressions(expr, node =>
        {
            if (node.Location is not { } loc) return;
            // Line: ANTLR is 1-based for line as well. XPath line 1 → attrLine.
            var fileLine = attrLine + loc.Line - 1;
            // Column: only line 1 of the XPath gets the value-start offset. For line N>1,
            // the XPath continues at column 1 of the next file line (no offset).
            var fileCol = loc.Line == 1 ? valueStartColumn + loc.Column : loc.Column;
            node.Location = loc with { Line = fileLine, Column = fileCol, Module = string.IsNullOrEmpty(moduleUri) ? loc.Module : moduleUri };
        });
    }


    /// <summary>
    /// Records the originating XSLT module URI + element line/column on the parsed
    /// expression's <see cref="SourceLocation"/>. Without this, runtime XQuery errors
    /// raised from XPath embedded in XSLT show only "[line N, col M]" relative to the
    /// inline XPath string — useless across thousands of similar expressions in real
    /// stylesheets (Docbook TNG, Schxslt2, etc.). With it, the EvaluateAsync diagnostic
    /// can prefix errors with the actual file URI and the line of the XSLT instruction
    /// that contained the offending XPath.
    /// </summary>
    private static void AttachXsltSourceLocation(XQueryExpression expr, XElement context)
    {
        if (context is not System.Xml.IXmlLineInfo li || !li.HasLineInfo()) return;
        var moduleUri = context.BaseUri;
        if (string.IsNullOrEmpty(moduleUri)) return;
        // Preserve any existing Line/Column from the XPath parser (which gave us the
        // position WITHIN the inline XPath string), but stamp Module = XSLT file URI so
        // the runtime can attribute errors to the right source file. The XSLT element's
        // line is also useful, so when the parsed expression has no Location yet,
        // synthesize one from the element's position.
        expr.Location = expr.Location is { } existing
            ? existing with { Module = moduleUri }
            : new SourceLocation(li.LineNumber, li.LinePosition, 0, 0) { Module = moduleUri };
    }


    /// <summary>
    /// Checks if an expression tree contains a VariableReference matching the given name.
    /// Used to detect self-referencing global variables (XPST0008).
    /// </summary>
    private static bool ContainsVariableReference(XQueryExpression expr, QName name)
    {
        switch (expr)
        {
            case VariableReference vr:
                return vr.Name.LocalName == name.LocalName && vr.Name.Namespace == name.Namespace;
            case BinaryExpression be:
                return ContainsVariableReference(be.Left, name) || ContainsVariableReference(be.Right, name);
            case UnaryExpression ue:
                return ContainsVariableReference(ue.Operand, name);
            case PathExpression pe:
                if (pe.InitialExpression != null && ContainsVariableReference(pe.InitialExpression, name))
                    return true;
                return pe.Steps.Any(s => ContainsVariableReference(s, name));
            case StepExpression se:
                return se.Predicates.Any(p => ContainsVariableReference(p, name));
            case FilterExpression fe:
                return ContainsVariableReference(fe.Primary, name) || fe.Predicates.Any(p => ContainsVariableReference(p, name));
            case IfExpression ie:
                return ContainsVariableReference(ie.Condition, name) || ContainsVariableReference(ie.Then, name)
                       || (ie.Else != null && ContainsVariableReference(ie.Else, name));
            case FunctionCallExpression fc:
                return fc.Arguments.Any(a => ContainsVariableReference(a, name));
            case SequenceExpression seq:
                return seq.Items.Any(i => ContainsVariableReference(i, name));
            case InlineFunctionExpression ife:
                return ife.Body != null && ContainsVariableReference(ife.Body, name);
            case DynamicFunctionCallExpression dfc:
                return ContainsVariableReference(dfc.FunctionExpression, name)
                       || dfc.Arguments.Any(a => ContainsVariableReference(a, name));
            case SimpleMapExpression sme:
                return ContainsVariableReference(sme.Left, name) || ContainsVariableReference(sme.Right, name);
            case StringConcatExpression sce:
                return sce.Operands.Any(o => ContainsVariableReference(o, name));
            case RangeExpression re:
                return ContainsVariableReference(re.Start, name) || ContainsVariableReference(re.End, name);
            case ArrowExpression ae:
                return ContainsVariableReference(ae.Expression, name) || ContainsVariableReference(ae.FunctionCall, name);
            case FlworExpression flwor:
                foreach (var clause in flwor.Clauses)
                {
                    if (clause is ForClause forClause && forClause.Bindings.Any(b => ContainsVariableReference(b.Expression, name)))
                        return true;
                    if (clause is LetClause letClause && letClause.Bindings.Any(b => ContainsVariableReference(b.Expression, name)))
                        return true;
                    if (clause is WhereClause whereClause && ContainsVariableReference(whereClause.Condition, name))
                        return true;
                    if (clause is OrderByClause orderBy && orderBy.OrderSpecs.Any(s => ContainsVariableReference(s.Expression, name)))
                        return true;
                }
                return ContainsVariableReference(flwor.ReturnExpression, name);
            case InstanceOfExpression inst:
                return ContainsVariableReference(inst.Expression, name);
            case CastExpression cast:
                return ContainsVariableReference(cast.Expression, name);
            case CastableExpression castable:
                return ContainsVariableReference(castable.Expression, name);
            case TreatExpression treat:
                return ContainsVariableReference(treat.Expression, name);
            default:
                return false;
        }
    }


    /// <summary>
    /// Gets the effective xpath-default-namespace from the context element or its XSLT ancestors.
    /// </summary>
    private static string? GetXpathDefaultNamespace(XElement context)
    {
        for (var el = context; el != null; el = el.Parent)
        {
            // Check for xsl:xpath-default-namespace (XSLT 3.0 standard attribute)
            var xdn = el.Attribute(XsltNs + "xpath-default-namespace");
            if (xdn != null)
                return string.IsNullOrEmpty(xdn.Value) ? null : xdn.Value;

            // Check for xpath-default-namespace on XSLT elements
            if (el.Name.Namespace == XsltNs)
            {
                xdn = el.Attribute("xpath-default-namespace");
                if (xdn != null)
                    return string.IsNullOrEmpty(xdn.Value) ? null : xdn.Value;
            }
        }
        return null;
    }


    /// <summary>
    /// Validates that an unprefixed atomic type name is in scope via xpath-default-namespace.
    /// Per XPath 3.1 §2.5.5.2, unprefixed type names in instance-of/cast/castable resolve using
    /// the default element/type namespace. If that namespace is not XSD, the name is unknown → XPST0051.
    /// </summary>
    private static void ValidateUnprefixedTypeName(XdmSequenceType type, XElement context)
    {
        if (type.UnprefixedTypeName == null) return;
        var xpathDefaultNs = GetXpathDefaultNamespace(context);
        if (xpathDefaultNs != "http://www.w3.org/2001/XMLSchema")
            throw new XsltException($"XPST0051: Unknown atomic type '{type.UnprefixedTypeName}' " +
                $"(unprefixed type names require xpath-default-namespace=\"http://www.w3.org/2001/XMLSchema\")",
                GetSourceLocation(context));
    }


    /// <summary>
    /// Splits a possibly-prefixed XML name into (localName, namespaceUri). Resolves prefixes
    /// against the in-scope namespace declarations on <paramref name="context"/>. Recognizes
    /// EQName syntax <c>Q{uri}local</c>. Returns <c>("", null)</c> for the wildcard <c>*</c>.
    /// </summary>
    private static (string localName, string? namespaceUri) SplitPrefixedName(string name, XElement? context)
    {
        if (string.IsNullOrEmpty(name) || name == "*")
            return ("", null);
        if (name.StartsWith("Q{", StringComparison.Ordinal))
        {
            var close = name.IndexOf('}', 2);
            if (close > 0 && close < name.Length - 1)
                return (name[(close + 1)..], name[2..close]);
            return (name, null);
        }
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0) return (name, null);
        var prefix = name[..colon];
        var local = name[(colon + 1)..];
        var ns = context?.GetNamespaceOfPrefix(prefix)?.NamespaceName;
        return (local, ns);
    }


    private static ItemType ParseAtomicItemType(string type) => type switch
    {
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
        _ => ItemType.Item
    };


    private static bool? ParseYesNo(XAttribute? attr)
    {
        if (attr == null) return null;

        // XSLT 3.0: xsl:yes-or-no accepts "yes", "no", "true", "false", "1", "0"
        // with leading/trailing whitespace stripped
        return attr.Value.Trim() switch
        {
            "yes" or "true" or "1" => true,
            "no" or "false" or "0" => false,
            _ => null
        };
    }


    private static Ast.ValidationMode? ParseValidationMode(XAttribute? attr)
    {
        return attr?.Value switch
        {
            "strict" => Ast.ValidationMode.Strict,
            "lax" => Ast.ValidationMode.Lax,
            "preserve" => Ast.ValidationMode.Preserve,
            "strip" => Ast.ValidationMode.Strip,
            _ => null
        };
    }


    /// <summary>
    /// Parses a yes/no/true/false/1/0 attribute value with XTSE0020 validation.
    /// Returns true for "yes"/"true"/"1", false for "no"/"false"/"0"/null.
    /// </summary>
    private static bool ParseYesNoBoolean(XAttribute? attr, string attrName, SourceLocation? location)
    {
        if (attr == null) return false;
        var val = attr.Value.Trim();
        return val switch
        {
            "yes" or "true" or "1" => true,
            "no" or "false" or "0" => false,
            _ => throw new XsltException($"XTSE0020: Invalid value '{attr.Value}' for {attrName} attribute: must be 'yes', 'no', 'true', 'false', '1', or '0'", location)
        };
    }


    private static SourceLocation? GetSourceLocation(XElement element)
    {
        if (element is IXmlLineInfo lineInfo && lineInfo.HasLineInfo())
        {
            return new SourceLocation(lineInfo.LineNumber, lineInfo.LinePosition, 0, 0)
            {
                Module = string.IsNullOrEmpty(element.BaseUri) ? null : element.BaseUri,
            };
        }
        // Even without line info, BaseUri is still useful diagnostic context.
        return string.IsNullOrEmpty(element.BaseUri)
            ? null
            : new SourceLocation(0, 0, 0, 0) { Module = element.BaseUri };
    }


    private XsltWherePopulated ParseWherePopulated(XElement element, SourceLocation? location)
    {
        return new XsltWherePopulated
        {
            Location = location,
            Content = ParseSequenceConstructor(element)
        };
    }


    private XsltOnEmpty ParseOnEmpty(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select");
        return new XsltOnEmpty
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = element.Nodes().Any() ? ParseSequenceConstructor(element) : null
        };
    }


    private XsltOnNonEmpty ParseOnNonEmpty(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select");
        return new XsltOnNonEmpty
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = element.Nodes().Any() ? ParseSequenceConstructor(element) : null
        };
    }


    /// <summary>
    /// Annotates an attribute produced from a shadow attribute whose static expression this
    /// parser could not evaluate (it evaluates only a subset of XPath statically — not
    /// <c>doc('')</c>, not <c>system-property()</c> inside <c>replace()</c>). The unevaluable part
    /// is dropped, so the attribute holds a value the stylesheet never asked for; a validator
    /// that judged it would report the parser's gap as the author's error.
    /// </summary>
    internal sealed class UnevaluatedShadowValue
    {
        public static readonly UnevaluatedShadowValue Instance = new();
        private UnevaluatedShadowValue() { }
    }

    /// <summary>
    /// Resolves XSLT 3.0 shadow attributes (section 3.6.2).
    /// Shadow attributes use the form _foo="{$param}" where the value is evaluated using
    /// static parameters, and the result replaces the real attribute foo.
    /// </summary>
    private static void ResolveShadowAttributes(XElement root, Dictionary<string, string>? externalStaticParams = null, Uri? explicitBaseUri = null)
    {
        // Collect static params AND variables from top-level elements
        var staticParams = new Dictionary<string, string>();

        // Also collect from imported stylesheets (process xsl:import/xsl:include first)
        var baseUri = root.BaseUri;
        Uri? baseUriObj = explicitBaseUri;
        if (baseUriObj == null && !string.IsNullOrEmpty(baseUri))
            Uri.TryCreate(baseUri, UriKind.Absolute, out baseUriObj);
        if (baseUriObj != null)
        {
            foreach (var child in root.Elements())
            {
                if (child.Name == XsltNs + "import" || child.Name == XsltNs + "include")
                {
                    var href = child.Attribute("href")?.Value;
                    if (href == null) continue;
                    try
                    {
                        var resolvedUri = new Uri(baseUriObj, href);
                        if (resolvedUri.IsFile && File.Exists(resolvedUri.LocalPath))
                        {
                            var importedDoc = XDocument.Load(resolvedUri.LocalPath, LoadOptions.SetBaseUri | LoadOptions.SetLineInfo);
                            if (importedDoc.Root != null)
                                CollectStaticDeclarations(importedDoc.Root, staticParams);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or XmlException or UriFormatException or UnauthorizedAccessException or FileNotFoundException)
                    {
                        // If import fails here, skip — it will be handled properly during parsing
                    }
                }
            }
        }

        CollectStaticDeclarations(root, staticParams, checkConsistency: true);

        // External static params override defaults (higher precedence)
        if (externalStaticParams != null)
        {
            foreach (var (name, value) in externalStaticParams)
            {
                var val = value.Trim();
                if (val.StartsWith('\'') && val.EndsWith('\''))
                    staticParams[name] = val[1..^1].Replace("''", "'", StringComparison.Ordinal);
                else if (val.StartsWith('"') && val.EndsWith('"'))
                    staticParams[name] = val[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
                else if (val is "true()" or "false()")
                    staticParams[name] = val == "true()" ? "yes" : "no";
                else
                    staticParams[name] = val;
            }
        }

        // Walk all elements and resolve shadow attributes
        // (even with no static params, shadow attributes need validation for XPST0017)
        ResolveShadowAttributesRecursive(root, staticParams);
    }


    /// <summary>
    /// Collects static param and variable declarations from a stylesheet root element.
    /// </summary>
    private static void CollectStaticDeclarations(XElement root, Dictionary<string, string> staticParams, bool checkConsistency = false)
    {
        // Track names seen in THIS module for same-precedence consistency check
        HashSet<string>? seenInModule = checkConsistency ? new() : null;
        foreach (var child in root.Elements())
        {
            if (child.Name != XsltNs + "param" && child.Name != XsltNs + "variable")
                continue;

            var staticAttr = child.Attribute("static");
            var isStatic = staticAttr?.Value?.Trim() is "yes" or "true" or "1";

            // Also check for _static shadow attribute (e.g., _static="{if ...}")
            if (!isStatic)
            {
                var shadowStatic = child.Attribute("_static");
                if (shadowStatic != null)
                {
                    var resolved = ResolveShadowValue(shadowStatic.Value, staticParams);
                    isStatic = resolved.Trim() is "yes" or "true" or "1";
                }
            }

            if (!isStatic) continue;

            var nameAttr = child.Attribute("name")?.Value;
            if (nameAttr == null) continue;

            var selectAttr = child.Attribute("select")?.Value;
            string? resolvedValue = null;
            if (selectAttr != null)
            {
                var val = selectAttr.Trim();
                if (val.StartsWith('\'') && val.EndsWith('\''))
                    resolvedValue = val[1..^1].Replace("''", "'", StringComparison.Ordinal);
                else if (val.StartsWith('"') && val.EndsWith('"'))
                    resolvedValue = val[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal);
                else if (val is "true()" or "false()")
                    resolvedValue = val == "true()" ? "yes" : "no";
                else
                    resolvedValue = val;
            }

            if (staticParams.TryGetValue(nameAttr, out var existing))
            {
                // XTSE3450: Only check consistency for same import precedence (same module)
                if (seenInModule != null && seenInModule.Contains(nameAttr) && resolvedValue != null && existing != resolvedValue)
                    throw new XsltException($"XTSE3450: Static variable '{nameAttr}' has value '{resolvedValue}' which is inconsistent with the value '{existing}' at the same import precedence");
                // Higher precedence wins — override
                if (resolvedValue != null)
                    staticParams[nameAttr] = resolvedValue;
                seenInModule?.Add(nameAttr);
                continue;
            }

            seenInModule?.Add(nameAttr);
            if (resolvedValue != null)
                staticParams[nameAttr] = resolvedValue;
        }
    }


    private static void ResolveShadowAttributesRecursive(XElement element, Dictionary<string, string> staticParams)
    {
        // Shadow attributes only apply to XSLT elements, not LREs (§3.9.3)
        if (element.Name.Namespace == XsltNs)
        {
            // Find shadow attributes (unprefixed attributes starting with underscore)
            var shadowAttrs = element.Attributes()
                .Where(a => a.Name.Namespace == XNamespace.None && a.Name.LocalName.StartsWith('_') && a.Name.LocalName.Length > 1)
                .ToList();

            foreach (var shadow in shadowAttrs)
            {
                var realName = shadow.Name.LocalName[1..]; // Remove leading underscore
                var value = ResolveShadowValue(shadow.Value, staticParams, out var complete);

                // Set the real attribute (overriding any existing value)
                element.SetAttributeValue(realName, value);
                if (!complete)
                    element.Attribute(realName)!.AddAnnotation(UnevaluatedShadowValue.Instance);

                // Remove the shadow attribute
                shadow.Remove();
            }
        }

        // Recurse into children
        foreach (var child in element.Elements())
        {
            ResolveShadowAttributesRecursive(child, staticParams);
        }
    }


    private static string ResolveShadowValue(string template, Dictionary<string, string> staticParams)
        => ResolveShadowValue(template, staticParams, out _);

    // `complete` is false when some expression in the template could not be evaluated here
    // and was dropped: the result is then not the value the stylesheet asked for, and must
    // not be validated as if it were.
    private static string ResolveShadowValue(string template, Dictionary<string, string> staticParams, out bool complete)
    {
        complete = true;
        // Process static AVT: {$name} → variable value, {{/}} → literal {/}, {...} → expression result
        var result = new System.Text.StringBuilder();
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                // {{ → literal {
                if (i + 1 < template.Length && template[i + 1] == '{')
                {
                    result.Append('{');
                    i += 2;
                    continue;
                }

                // Find closing brace (skipping braces inside string literals)
                var end = FindClosingBrace(template, i + 1);
                if (end > 0)
                {
                    var expr = template[(i + 1)..end];
                    if (expr.Length == 0)
                    {
                        // {} → empty expression, produces empty string
                    }
                    else if (expr.StartsWith('$'))
                    {
                        // {$name} → variable reference
                        var paramName = expr[1..];
                        if (staticParams.TryGetValue(paramName, out var paramValue))
                            result.Append(paramValue);
                    }
                    else
                    {
                        // Try to evaluate the expression statically
                        var evaluated = EvaluateShadowExpression(expr, staticParams);
                        if (evaluated != null)
                            result.Append(evaluated);
                        else
                            complete = false;
                    }
                    i = end + 1;
                    continue;
                }
            }
            else if (template[i] == '}' && i + 1 < template.Length && template[i + 1] == '}')
            {
                // }} → literal }
                result.Append('}');
                i += 2;
                continue;
            }
            result.Append(template[i]);
            i++;
        }
        return result.ToString();
    }


    /// <summary>
    /// Finds the closing '}' brace, skipping braces inside string literals.
    /// </summary>
    private static int FindClosingBrace(string template, int start)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        for (var j = start; j < template.Length; j++)
        {
            var c = template[j];
            if (c == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
            else if (c == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;
            else if (c == '}' && !inSingleQuote && !inDoubleQuote) return j;
        }
        return -1;
    }


    /// <summary>
    /// Finds a matching closing parenthesis, respecting nesting and string literals.
    /// </summary>
    private static int FindMatchingParen(string expr, int openPos)
    {
        var depth = 0;
        var inSingle = false;
        var inDouble = false;
        for (var i = openPos; i < expr.Length; i++)
        {
            var c = expr[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (!inSingle && !inDouble)
            {
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth == 0) return i; }
            }
        }
        return -1;
    }


    /// <summary>
    /// Finds a keyword in an expression, ensuring it's a word boundary (not inside a string or parentheses).
    /// </summary>
    private static int FindKeyword(string expr, string keyword)
    {
        var inSingle = false;
        var inDouble = false;
        var parenDepth = 0;
        for (var i = 0; i <= expr.Length - keyword.Length; i++)
        {
            var c = expr[i];
            if (c == '\'' && !inDouble) { inSingle = !inSingle; continue; }
            if (c == '"' && !inSingle) { inDouble = !inDouble; continue; }
            if (inSingle || inDouble) continue;
            if (c == '(') { parenDepth++; continue; }
            if (c == ')') { parenDepth--; continue; }
            if (parenDepth > 0) continue;
            if (expr.AsSpan(i, keyword.Length).SequenceEqual(keyword.AsSpan()) &&
                (i == 0 || !char.IsLetterOrDigit(expr[i - 1])) &&
                (i + keyword.Length >= expr.Length || !char.IsLetterOrDigit(expr[i + keyword.Length])))
            {
                return i;
            }
        }
        return -1;
    }


    /// <summary>
    /// Splits an expression on a binary operator, respecting string literal boundaries.
    /// </summary>
    private static List<string>? SplitOnOperator(string expr, string op)
    {
        var parts = new List<string>();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var parenDepth = 0;
        var lastSplit = 0;

        for (var j = 0; j < expr.Length; j++)
        {
            var c = expr[j];
            if (c == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
            else if (c == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;
            else if (c == '(' && !inSingleQuote && !inDoubleQuote) parenDepth++;
            else if (c == ')' && !inSingleQuote && !inDoubleQuote) parenDepth--;
            else if (!inSingleQuote && !inDoubleQuote && parenDepth == 0 &&
                     j + op.Length <= expr.Length && expr.AsSpan(j, op.Length).SequenceEqual(op.AsSpan()))
            {
                parts.Add(expr[lastSplit..j]);
                lastSplit = j + op.Length;
                j += op.Length - 1;
            }
        }
        parts.Add(expr[lastSplit..]);
        return parts.Count > 1 ? parts : null;
    }


    private static int FindCommaOutsideQuotes(string s)
    {
        var inSingleQuote = false;
        var inDoubleQuote = false;
        for (var j = 0; j < s.Length; j++)
        {
            var c = s[j];
            if (c == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
            else if (c == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;
            else if (c == ',' && !inSingleQuote && !inDoubleQuote) return j;
        }
        return -1;
    }


    // ── use-when static evaluation ──────────────────────────────────────

    /// <summary>
    /// Checks whether an element should be included based on its use-when attribute.
    /// XSLT elements use <c>use-when="expr"</c>; literal result elements use <c>xsl:use-when="expr"</c>.
    /// Returns true if the element should be included (no use-when attribute, or it evaluates to true).
    /// </summary>
    private bool ShouldIncludeElement(XElement element)
    {
        string? useWhenExpr;
        bool hasPrefixedUseWhen = false;
        if (element.Name.Namespace == XsltNs)
        {
            // On XSLT elements, only unprefixed use-when is processed.
            // xsl:use-when (prefixed) is an error (XTSE0090), but the check is deferred:
            // if the unprefixed use-when excludes the element, no error is raised.
            hasPrefixedUseWhen = element.Attribute(XsltNs + "use-when") != null;
            useWhenExpr = element.Attribute("use-when")?.Value;
            if (hasPrefixedUseWhen && useWhenExpr == null)
                throw new XsltException("XTSE0090: Attribute 'xsl:use-when' in the XSLT namespace is not permitted on an XSLT element (use unprefixed 'use-when' instead)",
                    GetSourceLocation(element));
        }
        else
        {
            useWhenExpr = element.Attribute(XsltNs + "use-when")?.Value;
        }

        if (useWhenExpr == null)
            return true;

        try
        {
            System.Xml.Linq.XObject useWhenOrigin =
                element.Attribute("use-when")
                ?? (System.Xml.Linq.XObject?)element.Attribute(XsltNs + "use-when")
                ?? element;
            var expr = ParseXPathWithContext(useWhenExpr, useWhenOrigin);
            ResolveExpressionNamespaces(expr, element);
            var result = EvaluateStaticExpression(expr, element);
            var include = CoerceToBoolean(result);
            // XTSE0090: If the element is included and has xsl:use-when (prefixed), raise error
            if (include && hasPrefixedUseWhen)
                throw new XsltException("XTSE0090: Attribute 'xsl:use-when' in the XSLT namespace is not permitted on an XSLT element (use unprefixed 'use-when' instead)",
                    GetSourceLocation(element));
            return include;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            // If we can't evaluate the use-when expression, include the element
            // (it will fail later with a proper error if the expression is actually needed)
            return true;
        }
        catch (XsltException)
        {
            // In forwards-compatible mode, if a use-when expression on an XSLT element
            // fails (e.g. unknown function, undeclared prefix), exclude the element
            if (element.Name.Namespace == XsltNs)
            {
                var ver = element.Attribute("version")?.Value ?? GetEffectiveVersion(element);
                if (ParseVersionNumber(ver) > 3.0m)
                    return false;
            }
            throw;
        }
    }


    /// <summary>
    /// Static evaluation of "instance of" in use-when context.
    /// Only handles atomic type checks on static values (strings, numbers, booleans).
    /// </summary>
    private static bool StaticInstanceOf(object? value, XdmSequenceType targetType)
    {
        // Check occurrence: null = empty sequence
        if (value == null)
            return targetType.Occurrence is Occurrence.ZeroOrMore or Occurrence.ZeroOrOne;

        // Single item check
        return targetType.ItemType switch
        {
            ItemType.Item => true,
            ItemType.AnyAtomicType => value is string or double or bool or int or long or decimal,
            ItemType.String => value is string,
            ItemType.Boolean => value is bool,
            ItemType.Integer => value is int or long || (value is double d && d == Math.Floor(d) && !double.IsInfinity(d) && !double.IsNaN(d)),
            ItemType.Decimal => value is decimal or int or long || (value is double d2 && !double.IsInfinity(d2) && !double.IsNaN(d2)),
            ItemType.Double => value is double,
            ItemType.Float => value is float or double,
            ItemType.Node or ItemType.Element or ItemType.Attribute or ItemType.Document
                or ItemType.Text or ItemType.Comment or ItemType.ProcessingInstruction => false, // static values are never nodes
            _ => false
        };
    }


    private static double ToDouble(object? value) =>
        value switch
        {
            double d => d,
            bool b => b ? 1.0 : 0.0,
            string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN,
            null => 0.0,
            _ => double.NaN
        };

}
