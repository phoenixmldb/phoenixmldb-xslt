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
    /// Compares two version strings. Supports numeric and pre-release (SemVer-style) comparison.
    /// Returns negative if a &lt; b, zero if equal, positive if a &gt; b.
    /// </summary>
    private static int CompareVersions(string a, string b)
    {
        // Split into numeric part and pre-release part (e.g., "2.0.0-alpha" → "2.0.0" + "alpha")
        var (aNum, aPre) = SplitPreRelease(a);
        var (bNum, bPre) = SplitPreRelease(b);

        // Compare numeric parts
        var aParts = aNum.Split('.');
        var bParts = bNum.Split('.');
        var maxLen = Math.Max(aParts.Length, bParts.Length);
        for (var i = 0; i < maxLen; i++)
        {
            var ai = i < aParts.Length && int.TryParse(aParts[i], out var av) ? av : 0;
            var bi = i < bParts.Length && int.TryParse(bParts[i], out var bv) ? bv : 0;
            if (ai != bi) return ai.CompareTo(bi);
        }

        // Same numeric version — compare pre-release (per SemVer: no pre-release > pre-release)
        if (aPre == null && bPre == null) return 0;
        if (aPre == null) return 1;  // 2.0.0 > 2.0.0-alpha
        if (bPre == null) return -1; // 2.0.0-alpha < 2.0.0
        return string.Compare(aPre, bPre, StringComparison.Ordinal);
    }


    private static void MergePackageComponents(XsltStylesheet target, XsltStylesheet package)
    {
        // Stamp all components with their originating package for package-local resolution
        // (decimal formats, keys, character maps, outputs, variables)
        foreach (var (_, func) in package.Functions)
            func.PackageStylesheet ??= package;
        foreach (var (_, tmpl) in package.NamedTemplates)
            tmpl.PackageStylesheet ??= package;
        foreach (var tmpl in package.Templates)
            tmpl.PackageStylesheet ??= package;

        // Named templates: merge all except abstract (private templates may be called
        // by public templates via xsl:call-template from the same package).
        foreach (var (name, template) in package.NamedTemplates)
        {
            if (template.Visibility is not (Visibility.Abstract or Visibility.Hidden))
                target.NamedTemplates.TryAdd(name, template);
            else if (template.Visibility is Visibility.Abstract)
                target.AbstractTemplateNames.Add(name);
        }
        foreach (var name in package.AbstractTemplateNames)
            target.AbstractTemplateNames.Add(name);
        foreach (var key in package.AbstractFunctionKeys)
            target.AbstractFunctionKeys.Add(key);
        {
        }

        // Template rules: add public/final and overrides
        foreach (var template in package.Templates)
        {
            if (template.Visibility is not (Visibility.Abstract or Visibility.Hidden))
                target.Templates.Add(template);
        }

        // Functions: merge ALL into the function library (private functions may be called
        // by public functions internally). Visibility enforcement happens at the call site.
        foreach (var (key, func) in package.Functions)
        {
            if (func.Visibility is not (Visibility.Abstract or Visibility.Hidden))
                target.Functions.TryAdd(key, func);
            else if (func.Visibility is Visibility.Abstract)
                target.AbstractFunctionKeys.Add(key);
        }

        // Variables: merge all except abstract (private variables may be referenced
        // by public functions from the same package). An abstract variable that is not
        // overridden has no evaluable value — record its name so a used-package global
        // that references it is deferred rather than eagerly evaluated (accept-042/043).
        foreach (var variable in package.Variables)
        {
            variable.PackageStylesheet ??= package;
            if (variable.Visibility is Visibility.Abstract || variable.IsAbstract)
                target.AbstractVariableNames.Add(variable.Name);
            else if (variable.Visibility is not Visibility.Hidden)
            {
                if (!target.Variables.Any(v => v.Name.Equals(variable.Name)))
                {
                    target.Variables.Add(variable);
                    // A global the used package does not expose across its boundary (private —
                    // explicitly or by the package default) is still merged so its own package's
                    // components can reference it, but must not resolve from any other package.
                    // Record (name → owning package); GetVariable enforces at resolution time
                    // (use-package-006 / use-package-007). Only the global occupying the principal
                    // QName slot is recorded — a same-named public sibling from another package
                    // (diamond) claims the slot first and is provided, so the private one arrives
                    // as a package-local shadow (below) and is resolved per calling package,
                    // never entering this map.
                    if (!variable.ProvidedByPackage)
                        target.PackagePrivateGlobals[variable.Name] = package;
                }
                else
                {
                    // A same-named global already claimed the principal QName slot (this happens
                    // in a diamond where two packages each expose a same-named global with a
                    // different value — use-package-175/176). Globals are package-local, so keep
                    // this one as a package-local shadow, resolved per calling package at runtime
                    // rather than being silently dropped into the winner's single value. Key it by
                    // the CONSUMING package boundary (the used package whose components reference
                    // it), which is how those components' templates/functions are stamped.
                    variable.PackageStylesheet = package;
                    target.PackageLocalShadowVariables.Add(variable);
                }
            }
        }

        // Parameters: merge all
        foreach (var param in package.Parameters)
        {
            if (!target.Parameters.Any(p => p.Name.Equals(param.Name)))
                target.Parameters.Add(param);
        }

        // Attribute sets: stamp every set of the used package with its owning package so a
        // nested use-attribute-sets reference resolves within THAT package's scope (which
        // includes its private/abstract sets), not the merged registry (override-as-005).
        // Only PUBLIC/FINAL sets are exposed into the merged registry — private and hidden
        // sets stay package-local (resolvable only from within their own package). An abstract
        // set that is not overridden remains unusable — record its name so a reference to it
        // at runtime raises XTDE3052 rather than silently applying nothing (accept-911).
        foreach (var (_, attrSet) in package.AttributeSets)
            attrSet.PackageStylesheet ??= package;
        foreach (var (name, attrSet) in package.AttributeSets)
        {
            if (attrSet.Visibility is Visibility.Abstract || attrSet.IsAbstract)
                target.AbstractAttributeSetNames.Add(name);
            // Provided by the package (exposed public/final/abstract at its boundary) and not
            // hidden by the using package's xsl:accept — a set the using package accepts as
            // "private" is still usable (accept-002); a set declared private in the USED
            // package is not provided and stays package-local (override-as-005).
            else if (attrSet.ProvidedByPackage && attrSet.Visibility is not Visibility.Hidden)
                target.AttributeSets.TryAdd(name, attrSet);
        }

        // Keys: merge into the principal registry (so eager namespace resolution and the
        // shared key index continue to cover them), BUT stamp each definition with its
        // owning package. Keys are LOCAL to their declaring package (XSLT 3.0 §3.6.2):
        // key() resolves definitions only from the calling package, so a used-package key
        // must never leak into the using package and same-named keys in different packages
        // must index independently (use-package-102 / use-package-105). The per-package
        // filtering happens at resolution time (XsltKeyFunction.ResolveKeyDefinition).
        foreach (var (_, key) in package.Keys)
        {
            key.PackageStylesheet ??= package;
            if (key.OtherDefinitions != null)
                foreach (var other in key.OtherDefinitions)
                    other.PackageStylesheet ??= package;
        }
        foreach (var (name, key) in package.Keys)
        {
            if (target.Keys.TryGetValue(name, out var existing))
            {
                existing.OtherDefinitions ??= new List<XsltKey>();
                existing.OtherDefinitions.Add(key);
            }
            else
            {
                target.Keys[name] = key;
            }
        }

        // Modes: merge
        foreach (var (key, mode) in package.Modes)
        {
            if (mode.Visibility is Visibility.Public or Visibility.Final)
                target.Modes.TryAdd(key, mode);
        }

        // Strip/preserve space: merge
        target.StripSpace.AddRange(package.StripSpace);
        target.PreserveSpace.AddRange(package.PreserveSpace);

        // Accumulators: merge all. Accumulators are package-local, so record which names
        // came from the used package — a same-named accumulator declared in the using
        // package is not a duplicate (XTSE3350) (override-misc-005).
        foreach (var (name, acc) in package.Accumulators)
        {
            acc.PackageStylesheet ??= package;
            target.Accumulators.TryAdd(name, acc);
            target.PackageMergedAccumulatorNames.Add(name);
        }

        // Character maps, decimal formats, namespace aliases, and outputs are
        // package-local per XSLT 3.0 spec — they do NOT cross package boundaries.
        // (use-package-101: "Decimal formats are local to a package"). They stay in the
        // used package's own stylesheet and are resolved package-locally at runtime via the
        // component's PackageStylesheet (decimal-format: FindDecimalFormatInStylesheet;
        // namespace-alias: CreateLiteralElementCoreAsync). Stamp each named output with its
        // owning package so xsl:result-document/@format naming it (from that package's own
        // template) resolves the output AND its use-character-maps in that package
        // (use-package-108 / use-package-108b).
        foreach (var output in package.Outputs)
            output.PackageStylesheet ??= package;

        // Namespace bindings: merge (needed for function name resolution across packages)
        foreach (var (prefix, uri) in package.Namespaces)
            target.Namespaces.TryAdd(prefix, uri);

        // Extension namespaces: merge
        foreach (var extNs in package.ExtensionElementPrefixes)
            target.ExtensionElementPrefixes.Add(extNs);

        // Global context item: per XSLT 3.0 §3.7.2, xsl:global-context-item in a library package
        // is private to that package — EXCEPT use="required" which causes XTTE0590 at runtime.
        if (package.GlobalContextItemUse == ContextItemUse.Required)
        {
            // Library package declares use="required" — this is an error per spec
            throw new XsltException("XTTE0590: A library package declares xsl:global-context-item with use=\"required\"");
        }
        // Otherwise, only merge if the consuming package has no declaration
        if (target.GlobalContextItemUse == null && package.GlobalContextItemUse != null)
        {
            target.GlobalContextItemUse = package.GlobalContextItemUse;
            target.GlobalContextItemAs = package.GlobalContextItemAs;
        }
    }


    /// <summary>
    /// Merge named templates, functions, keys, and variables from an imported stylesheet.
    /// Called in reverse import order so later imports (higher precedence) are TryAdd'd first.
    /// A stylesheet's own declarations take precedence over its imports, so TryAdd own first.
    /// </summary>
    private static void MergeImportedNamedDeclarations(XsltStylesheet target, XsltStylesheet imported)
    {
        // Add the imported stylesheet's OWN declarations first (higher precedence than its imports)
        foreach (var (name, template) in imported.NamedTemplates)
            target.NamedTemplates.TryAdd(name, template);

        foreach (var (name, func) in imported.Functions)
            target.Functions.TryAdd(name, func);

        foreach (var (name, key) in imported.Keys)
        {
            if (target.Keys.TryGetValue(name, out var existingKey))
            {
                // Merge same-named key definitions per XSLT spec (union of matches)
                existingKey.OtherDefinitions ??= new List<XsltKey>();
                existingKey.OtherDefinitions.Add(key);
                if (key.OtherDefinitions != null)
                    existingKey.OtherDefinitions.AddRange(key.OtherDefinitions);
            }
            else
            {
                target.Keys[name] = key;
            }
        }

        foreach (var variable in imported.Variables)
        {
            if (!target.Variables.Any(v => v.Name.Equals(variable.Name)))
                target.Variables.Add(variable);
        }

        // Merge character maps from imports (TryAdd preserves higher-precedence definitions)
        foreach (var (name, charMap) in imported.CharacterMaps)
            target.CharacterMaps.TryAdd(name, charMap);

        // Merge decimal formats from imports
        foreach (var (name, decFmt) in imported.DecimalFormats)
            target.DecimalFormats.TryAdd(name, decFmt);

        // Merge accumulators from imports (TryAdd preserves higher-precedence definitions)
        foreach (var (name, acc) in imported.Accumulators)
            target.Accumulators.TryAdd(name, acc);

        // XTSE3350: Check for duplicate accumulators in imported module that weren't
        // overridden by a higher-precedence definition in the target
        foreach (var dupName in imported.DuplicateAccumulatorNames)
        {
            if (!target.Accumulators.TryGetValue(dupName, out var existing) || existing == imported.Accumulators[dupName])
                throw new XsltException($"XTSE3350: Duplicate accumulator name '{dupName.LocalName}'");
        }

        // Merge modes from imports (property-level merge with import precedence)
        foreach (var (key, importedMode) in imported.Modes)
        {
            if (!target.Modes.TryGetValue(key, out var existingMode))
            {
                target.Modes[key] = importedMode;
            }
            else
            {
                // Merge: higher-precedence (existing) explicit properties win;
                // fill in unset properties from imported mode
                target.Modes[key] = new XsltMode
                {
                    Name = existingMode.Name,
                    Streamable = existingMode.Streamable || importedMode.Streamable,
                    OnNoMatch = existingMode.OnNoMatch ?? importedMode.OnNoMatch,
                    OnMultipleMatch = existingMode.OnMultipleMatch,
                    UseAllAccumulators = existingMode.UseAllAccumulators || importedMode.UseAllAccumulators,
                    UseAccumulatorNames = existingMode.UseAccumulatorNames.Count > 0
                        ? existingMode.UseAccumulatorNames
                        : importedMode.UseAccumulatorNames,
                    Visibility = existingMode.VisibilityAttr != null ? existingMode.Visibility : importedMode.Visibility,
                    VisibilityAttr = existingMode.VisibilityAttr ?? importedMode.VisibilityAttr,
                    TypedValueWarnings = existingMode.TypedValueWarnings ?? importedMode.TypedValueWarnings,
                    Typed = existingMode.Typed || importedMode.Typed,
                    UseAccumulatorsAttr = existingMode.UseAccumulatorsAttr ?? importedMode.UseAccumulatorsAttr,
                };
                // If the higher-precedence module has explicit use-accumulators,
                // it resolves any conflict from the imported module
                if (existingMode.UseAccumulatorsAttr != null)
                    target.ConflictingModeAccumulators.Remove(key);
                // If the higher-precedence module has explicit visibility,
                // it resolves any conflict from the imported module
                if (existingMode.VisibilityAttr != null)
                    target.ConflictingModeVisibility.Remove(key);
            }
        }

        // Propagate unresolved conflicts from imported stylesheet
        foreach (var conflict in imported.ConflictingModeAccumulators)
        {
            // Only propagate if the target doesn't have its own higher-precedence declaration
            if (!target.Modes.TryGetValue(conflict, out var conflictMode) || conflictMode.UseAccumulatorsAttr == null)
                target.ConflictingModeAccumulators.Add(conflict);
        }

        foreach (var conflict in imported.ConflictingModeVisibility)
        {
            if (!target.Modes.TryGetValue(conflict, out var conflictMode) || conflictMode.VisibilityAttr == null)
                target.ConflictingModeVisibility.Add(conflict);
        }

        // Merge namespace prefix bindings from imported modules
        // (needed for element-available, function-available prefix resolution at runtime)
        foreach (var (prefix, uri) in imported.Namespaces)
            target.Namespaces.TryAdd(prefix, uri);

        // Merge extension element namespaces from imported modules
        foreach (var extNs in imported.ExtensionElementPrefixes)
            target.ExtensionElementPrefixes.Add(extNs);

        // Then recursively merge nested imports (reverse order for precedence among siblings)
        for (var i = imported.Imports.Count - 1; i >= 0; i--)
            MergeImportedNamedDeclarations(target, imported.Imports[i]);
    }


    /// <summary>
    /// Merge attribute sets from an imported stylesheet.
    /// Called in forward import order so higher-precedence attributes come last and override.
    /// localAttrSetNames tracks which attribute sets were defined in the main module itself
    /// (highest precedence). Imported parts are inserted before local parts but after
    /// previously-imported parts to maintain correct import precedence ordering.
    /// </summary>
    private static void MergeImportedAttributeSets(XsltStylesheet target, XsltStylesheet imported, HashSet<QName> localAttrSetNames)
    {
        // Recursively merge nested imports first (forward order)
        foreach (var nestedImport in imported.Imports)
            MergeImportedAttributeSets(target, nestedImport, localAttrSetNames);

        foreach (var (name, attrSet) in imported.AttributeSets)
        {
            if (target.AttributeSets.TryGetValue(name, out var existingSet))
            {
                // Preserve per-definition parts for correct interleaving.
                existingSet.Parts ??= new List<XsltAttributeSetPart>
                {
                    new() { UseAttributeSets = new List<QName>(existingSet.UseAttributeSets), Attributes = new List<XsltAttribute>(existingSet.Attributes) }
                };

                // If the existing set includes a locally-defined part (from the main module),
                // insert imported parts BEFORE it (at Parts.Count - 1) so the local part
                // executes last and wins (highest precedence, last-written wins).
                // If the existing set is entirely from earlier imports, append at the end
                // so later imports (higher precedence) execute last and win.
                var isLocal = localAttrSetNames.Contains(name);
                var insertAt = isLocal ? existingSet.Parts.Count - 1 : existingSet.Parts.Count;

                if (attrSet.Parts != null)
                    existingSet.Parts.InsertRange(insertAt, attrSet.Parts);
                else
                    existingSet.Parts.Insert(insertAt, new XsltAttributeSetPart { UseAttributeSets = attrSet.UseAttributeSets, Attributes = attrSet.Attributes });

                existingSet.Attributes.AddRange(attrSet.Attributes);
                foreach (var u in attrSet.UseAttributeSets)
                {
                    if (!existingSet.UseAttributeSets.Contains(u))
                        existingSet.UseAttributeSets.Add(u);
                }
            }
            else
            {
                target.AttributeSets[name] = attrSet;
            }
        }
    }


    /// <summary>
    /// Merges decimal formats from imported stylesheets into the main stylesheet.
    /// Imported formats have lower precedence — they're only used for attributes
    /// not already defined in the importing stylesheet.
    /// </summary>
    private static void MergeImportedDecimalFormats(XsltStylesheet stylesheet)
    {
        foreach (var imported in stylesheet.Imports)
        {
            // Recursively merge imports of imports
            MergeImportedDecimalFormats(imported);

            foreach (var (name, importedDf) in imported.DecimalFormats)
            {
                if (stylesheet.DecimalFormats.TryGetValue(name, out var existingDf))
                {
                    // Merge: existing (higher precedence) overrides imported (lower precedence)
                    // For each property, keep existing if it differs from default, otherwise use imported
                    var defaults = new XsltDecimalFormat();
                    stylesheet.DecimalFormats[name] = new XsltDecimalFormat
                    {
                        Name = existingDf.Name,
                        DecimalSeparator = existingDf.DecimalSeparator != defaults.DecimalSeparator ? existingDf.DecimalSeparator : importedDf.DecimalSeparator,
                        GroupingSeparator = existingDf.GroupingSeparator != defaults.GroupingSeparator ? existingDf.GroupingSeparator : importedDf.GroupingSeparator,
                        Infinity = existingDf.Infinity != defaults.Infinity ? existingDf.Infinity : importedDf.Infinity,
                        MinusSign = existingDf.MinusSign != defaults.MinusSign ? existingDf.MinusSign : importedDf.MinusSign,
                        NaN = existingDf.NaN != defaults.NaN ? existingDf.NaN : importedDf.NaN,
                        Percent = existingDf.Percent != defaults.Percent ? existingDf.Percent : importedDf.Percent,
                        PerMille = existingDf.PerMille != defaults.PerMille ? existingDf.PerMille : importedDf.PerMille,
                        ZeroDigit = existingDf.ZeroDigit != defaults.ZeroDigit ? existingDf.ZeroDigit : importedDf.ZeroDigit,
                        Digit = existingDf.Digit != defaults.Digit ? existingDf.Digit : importedDf.Digit,
                        PatternSeparator = existingDf.PatternSeparator != defaults.PatternSeparator ? existingDf.PatternSeparator : importedDf.PatternSeparator,
                        ExponentSeparator = existingDf.ExponentSeparator != defaults.ExponentSeparator ? existingDf.ExponentSeparator : importedDf.ExponentSeparator,
                        // Keep any conflict from the higher-precedence format's same-level merging;
                        // imported conflicts are resolved by this higher-precedence override
                        HasConflict = existingDf.HasConflict,
                        ConflictDescription = existingDf.ConflictDescription
                    };
                }
                else
                {
                    // No existing format — add the imported one directly
                    stylesheet.DecimalFormats[name] = importedDf;
                }
            }
        }
    }


    /// <summary>
    /// Merges namespace aliases from imported stylesheets into the main stylesheet.
    /// Imported aliases have lower precedence — main module aliases take priority.
    /// </summary>
    private static void MergeImportedNamespaceAliases(XsltStylesheet stylesheet)
    {
        foreach (var imported in stylesheet.Imports)
        {
            MergeImportedNamespaceAliases(imported);

            foreach (var (nsUri, alias) in imported.NamespaceAliases)
            {
                // TryAdd: main module (higher precedence) takes priority
                stylesheet.NamespaceAliases.TryAdd(nsUri, alias);
            }
        }
    }


    private static void MergeOutputDeclarations(XsltStylesheet stylesheet)
    {
        if (stylesheet.Outputs.Count <= 1)
            return;

        // Group by name (null for unnamed). Key on the EXPANDED QName identity (namespace URI
        // + local name), never the lexical prefix: two xsl:output declarations whose @name uses
        // different prefixes bound to the same namespace URI denote the same output definition
        // and must merge (output-0135). QName.ToString() emits the prefix when no expanded
        // namespace is attached, so it cannot be the key.
        static string OutputKey(QName name)
            => "{" + (name.ResolvedNamespace ?? "#" + name.Namespace.Value.ToString(CultureInfo.InvariantCulture)) + "}" + name.LocalName;
        var groups = new Dictionary<string, List<XsltOutput>>();
        foreach (var output in stylesheet.Outputs)
        {
            var key = output.Name != null ? OutputKey(output.Name.Value) : "";
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<XsltOutput>();
                groups[key] = list;
            }
            list.Add(output);
        }

        stylesheet.Outputs.Clear();
        foreach (var (_, outputList) in groups)
        {
            if (outputList.Count == 1)
            {
                stylesheet.Outputs.Add(outputList[0]);
                continue;
            }

            // XTSE1560: Check for conflicting attribute values at same import precedence,
            // but only when a higher-precedence output doesn't resolve the conflict
            var byPrecedence = outputList.GroupBy(o => o.ImportPrecedence).ToDictionary(g => g.Key, g => g.ToList());
            foreach (var (_, samePrecList) in byPrecedence)
            {
                for (var i = 0; i < samePrecList.Count; i++)
                {
                    for (var j = i + 1; j < samePrecList.Count; j++)
                    {
                        var a = samePrecList[i];
                        var b = samePrecList[j];
                        // Only report conflict if no higher-precedence output sets the attribute
                        var higherPrec = outputList.Where(o => o.ImportPrecedence < a.ImportPrecedence).ToList();
                        if (a.Method != null && b.Method != null && a.Method != b.Method
                            && !higherPrec.Any(o => o.Method != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: method '{a.Method}' vs '{b.Method}'");
                        if (a.Indent != null && b.Indent != null && a.Indent != b.Indent
                            && !higherPrec.Any(o => o.Indent != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: indent values differ");
                        if (a.Encoding != null && b.Encoding != null && !string.Equals(a.Encoding, b.Encoding, StringComparison.OrdinalIgnoreCase)
                            && !higherPrec.Any(o => o.Encoding != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: encoding '{a.Encoding}' vs '{b.Encoding}'");
                        if (a.OmitXmlDeclaration != null && b.OmitXmlDeclaration != null && a.OmitXmlDeclaration != b.OmitXmlDeclaration
                            && !higherPrec.Any(o => o.OmitXmlDeclaration != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: omit-xml-declaration values differ");
                        if (a.Standalone != null && b.Standalone != null && a.Standalone != b.Standalone
                            && !higherPrec.Any(o => o.Standalone != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: standalone values differ");
                        if (a.Version != null && b.Version != null && a.Version != b.Version
                            && !higherPrec.Any(o => o.Version != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: version '{a.Version}' vs '{b.Version}'");
                        if (a.MediaType != null && b.MediaType != null && a.MediaType != b.MediaType
                            && !higherPrec.Any(o => o.MediaType != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: media-type '{a.MediaType}' vs '{b.MediaType}'");
                        if (a.DoctypePublic != null && b.DoctypePublic != null && a.DoctypePublic != b.DoctypePublic
                            && !higherPrec.Any(o => o.DoctypePublic != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: doctype-public values differ");
                        if (a.DoctypeSystem != null && b.DoctypeSystem != null && a.DoctypeSystem != b.DoctypeSystem
                            && !higherPrec.Any(o => o.DoctypeSystem != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: doctype-system values differ");
                        if (a.NormalizationForm != null && b.NormalizationForm != null && a.NormalizationForm != b.NormalizationForm
                            && !higherPrec.Any(o => o.NormalizationForm != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: normalization-form values differ");
                        if (a.IncludeContentType != null && b.IncludeContentType != null && a.IncludeContentType != b.IncludeContentType
                            && !higherPrec.Any(o => o.IncludeContentType != null))
                            throw new XsltException($"XTSE1560: Conflicting xsl:output declarations: include-content-type values differ");
                    }
                }
            }

            // Merge: highest precedence (lowest ImportPrecedence number) wins for each attribute.
            // Sort by ImportPrecedence ascending so highest-precedence outputs are first.
            var sorted = outputList.OrderBy(o => o.ImportPrecedence).ToList();
            var merged = new XsltOutput
            {
                Name = outputList[0].Name,
                Method = sorted.Select(o => o.Method).FirstOrDefault(m => m != null),
                Version = sorted.Select(o => o.Version).FirstOrDefault(v => v != null),
                Encoding = sorted.Select(o => o.Encoding).FirstOrDefault(e => e != null),
                OmitXmlDeclaration = sorted.Select(o => o.OmitXmlDeclaration).FirstOrDefault(v => v != null),
                Standalone = sorted.Select(o => o.Standalone).FirstOrDefault(v => v != null),
                DoctypePublic = sorted.Select(o => o.DoctypePublic).FirstOrDefault(v => v != null),
                DoctypeSystem = sorted.Select(o => o.DoctypeSystem).FirstOrDefault(v => v != null),
                Indent = sorted.Select(o => o.Indent).FirstOrDefault(v => v != null),
                MediaType = sorted.Select(o => o.MediaType).FirstOrDefault(v => v != null),
                ItemSeparator = sorted.Select(o => o.ItemSeparator).FirstOrDefault(v => v != null),
                NormalizationForm = sorted.Select(o => o.NormalizationForm).FirstOrDefault(v => v != null),
                UndeclarePrefixes = sorted.Select(o => o.UndeclarePrefixes).FirstOrDefault(v => v != null),
                IncludeContentType = sorted.Select(o => o.IncludeContentType).FirstOrDefault(v => v != null),
                EscapeUriAttributes = sorted.Select(o => o.EscapeUriAttributes).FirstOrDefault(v => v != null),
                HtmlVersion = sorted.Select(o => o.HtmlVersion).FirstOrDefault(v => v != null),
                BuildTree = sorted.Select(o => o.BuildTree).FirstOrDefault(v => v != null),
                AllowDuplicateNames = sorted.Select(o => o.AllowDuplicateNames).FirstOrDefault(v => v != null),
                ByteOrderMark = sorted.Select(o => o.ByteOrderMark).FirstOrDefault(v => v != null),
                JsonNodeOutputMethod = sorted.Select(o => o.JsonNodeOutputMethod).FirstOrDefault(v => v != null),
            };

            // Union cdata-section-elements from all declarations
            HashSet<QName>? cdataElements = null;
            foreach (var o in outputList)
            {
                if (o.CdataSectionElements != null)
                {
                    cdataElements ??= new HashSet<QName>();
                    cdataElements.UnionWith(o.CdataSectionElements);
                }
            }
            merged.CdataSectionElements = cdataElements;

            // Union use-character-maps
            foreach (var o in outputList)
            {
                foreach (var m in o.UseCharacterMaps)
                {
                    if (!merged.UseCharacterMaps.Contains(m))
                        merged.UseCharacterMaps.Add(m);
                }
            }

            // Union suppress-indentation from all declarations
            HashSet<QName>? suppressElements = null;
            foreach (var o in outputList)
            {
                if (o.SuppressIndentation != null)
                {
                    suppressElements ??= new HashSet<QName>();
                    suppressElements.UnionWith(o.SuppressIndentation);
                }
            }
            merged.SuppressIndentation = suppressElements;

            stylesheet.Outputs.Add(merged);
        }
    }


    private static void MergeStylesheet(XsltStylesheet target, XsltStylesheet source)
    {
        // XTSE0265: Conflicting input-type-annotations across modules
        CheckInputTypeAnnotationsConflict(target, source);

        // Include semantics: merge all declarations at same precedence
        target.Templates.AddRange(source.Templates);
        // Only add variables/params that don't already exist (by name)
        foreach (var variable in source.Variables)
        {
            if (!target.Variables.Any(v => v.Name.Equals(variable.Name)))
                target.Variables.Add(variable);
        }
        foreach (var param in source.Parameters)
        {
            if (!target.Parameters.Any(p => p.Name.Equals(param.Name)))
                target.Parameters.Add(param);
        }

        foreach (var (name, template) in source.NamedTemplates)
        {
            if (!target.NamedTemplates.TryAdd(name, template))
                throw new XsltException($"XTSE0660: Duplicate named template '{name.LocalName}' at the same import precedence");
        }

        foreach (var (name, func) in source.Functions)
        {
            target.Functions.TryAdd(name, func);
        }

        foreach (var (name, key) in source.Keys)
        {
            if (target.Keys.TryGetValue(name, out var existingKey))
            {
                // Merge same-named key definitions per XSLT spec (union of matches)
                existingKey.OtherDefinitions ??= new List<XsltKey>();
                existingKey.OtherDefinitions.Add(key);
                // Also merge any other definitions from the source key
                if (key.OtherDefinitions != null)
                    existingKey.OtherDefinitions.AddRange(key.OtherDefinitions);
            }
            else
            {
                target.Keys[name] = key;
            }
        }

        foreach (var (name, attrSet) in source.AttributeSets)
        {
            // Merge same-named attribute sets per XSLT spec
            if (target.AttributeSets.TryGetValue(name, out var existingSet))
            {
                existingSet.Attributes.AddRange(attrSet.Attributes);
                foreach (var u in attrSet.UseAttributeSets)
                {
                    if (!existingSet.UseAttributeSets.Contains(u))
                        existingSet.UseAttributeSets.Add(u);
                }
            }
            else
            {
                target.AttributeSets[name] = attrSet;
            }
        }

        foreach (var (name, charMap) in source.CharacterMaps)
        {
            target.CharacterMaps.TryAdd(name, charMap);
        }

        foreach (var (name, decFmt) in source.DecimalFormats)
        {
            target.DecimalFormats.TryAdd(name, decFmt);
        }

        target.Outputs.AddRange(source.Outputs);
        target.StripSpace.AddRange(source.StripSpace);
        target.PreserveSpace.AddRange(source.PreserveSpace);

        // Merge namespace aliases from included modules (same import precedence)
        foreach (var (nsUri, alias) in source.NamespaceAliases)
        {
            if (target.NamespaceAliases.TryGetValue(nsUri, out var existing))
            {
                if (existing.ResultUri != alias.ResultUri)
                    throw new XsltException($"XTSE0810: Conflicting xsl:namespace-alias declarations for namespace '{nsUri}' at the same import precedence");
            }
            else
            {
                target.NamespaceAliases[nsUri] = alias;
            }
        }

        // Merge global-context-item declarations (XTSE3087 for inconsistency)
        if (source.GlobalContextItemUse != null)
        {
            if (target.GlobalContextItemUse != null)
            {
                // Both modules declare xsl:global-context-item — check consistency
                if (target.GlobalContextItemUse != source.GlobalContextItemUse)
                    throw new XsltException("XTSE3087: Inconsistent xsl:global-context-item declarations across stylesheet modules");
                // Check consistency of the 'as' type across modules
                var targetAs = target.GlobalContextItemAs?.ToString();
                var sourceAs = source.GlobalContextItemAs?.ToString();
                if (targetAs != sourceAs)
                    throw new XsltException($"XTSE3087: Inconsistent xsl:global-context-item 'as' type across stylesheet modules ('{targetAs ?? "item()"}' vs '{sourceAs ?? "item()"}')");

            }
            else
            {
                target.GlobalContextItemUse = source.GlobalContextItemUse;
                target.GlobalContextItemAs = source.GlobalContextItemAs;
            }
        }

        // Merge accumulators from included modules (same import precedence)
        foreach (var (name, acc) in source.Accumulators)
        {
            if (target.Accumulators.ContainsKey(name))
                target.DuplicateAccumulatorNames.Add(name);
            else
                target.Accumulators[name] = acc;
        }

        // Merge mode declarations from included modules (same import precedence)
        foreach (var (name, mode) in source.Modes)
        {
            if (target.Modes.TryGetValue(name, out var existingMode))
            {
                // Conflicting use-accumulators at same import precedence — defer the error
                // because a higher-precedence import may override and resolve the conflict
                if (UseAccumulatorsConflict(existingMode, mode))
                    target.ConflictingModeAccumulators.Add(name);
                if (VisibilityConflict(existingMode, mode))
                    target.ConflictingModeVisibility.Add(name);
            }
            else
            {
                target.Modes[name] = mode;
            }
        }
        // Propagate conflicts from source
        foreach (var conflict in source.ConflictingModeAccumulators)
            target.ConflictingModeAccumulators.Add(conflict);
        foreach (var conflict in source.ConflictingModeVisibility)
            target.ConflictingModeVisibility.Add(conflict);

        // Merge namespace prefix bindings from included modules
        // (needed for element-available, function-available prefix resolution at runtime)
        foreach (var (prefix, uri) in source.Namespaces)
        {
            target.Namespaces.TryAdd(prefix, uri);
        }

        // Merge extension element namespaces from included modules
        // (needed for element-available() to return true for extension elements at runtime)
        foreach (var extNs in source.ExtensionElementPrefixes)
            target.ExtensionElementPrefixes.Add(extNs);

        // Also merge any nested imports
        target.Imports.AddRange(source.Imports);

        // Propagate the components each module accepted from used packages, detecting a
        // cross-include XTSE3050: a component made visible via xsl:use-package in one included
        // module clashes with a same-named component made visible via a distinct xsl:use-package
        // in another module of the same package (the package-022err diamond, where an included
        // module use-packages P and another route reaches P again leaving a symbol visible on
        // both). Overridden symbols are already excluded before they are recorded.
        foreach (var symbol in source.AcceptedComponentSymbols)
        {
            if (!target.AcceptedComponentSymbols.Add(symbol))
                throw new XsltException(
                    $"XTSE3050: The package contains two components of kind '{KindDisplayName(symbol.Kind)}' " +
                    $"with the same name '{symbol.Name.LocalName}', each accepted from a used package " +
                    "(reached via different xsl:include routes), and neither overrides the other");
        }
    }


    /// <summary>
    /// Loads the serialization-parameters document referenced by <c>xsl:output/@parameter-document</c>
    /// and merges its simple parameters onto <paramref name="outputElement"/> (only where an attribute
    /// is not already present, since direct attributes win per XSLT 3.0 §26.1). Returns the character
    /// mappings declared by its <c>output:use-character-maps</c>, or <c>null</c> if none.
    /// </summary>
    private Dictionary<int, string>? MergeSerializationParameterDocument(XElement outputElement, string href)
    {
        var root = LoadSerializationParameterDocument(outputElement, href);
        Dictionary<int, string>? charMap = null;
        foreach (var child in root.Elements())
        {
            if (child.Name.Namespace != SerializationParamsNs)
                continue;
            var local = child.Name.LocalName;
            if (local == "use-character-maps")
            {
                foreach (var cm in child.Elements(SerializationParamsNs + "character-map"))
                {
                    var ch = cm.Attribute("character")?.Value;
                    var mapStr = cm.Attribute("map-string")?.Value;
                    if (ch == null || mapStr == null)
                        continue;
                    charMap ??= new Dictionary<int, string>();
                    if (ch.Length == 1)
                        charMap[ch[0]] = mapStr;
                    else if (ch.Length == 2 && char.IsHighSurrogate(ch[0]) && char.IsLowSurrogate(ch[1]))
                        charMap[char.ConvertToUtf32(ch[0], ch[1])] = mapStr;
                }
                continue;
            }
            // Simple value parameter (method, omit-xml-declaration, indent, …): the element carries
            // its value in a @value attribute. Only apply it when xsl:output does not already set the
            // same-named attribute directly.
            var value = child.Attribute("value")?.Value;
            if (value != null && outputElement.Attribute(local) == null)
                outputElement.SetAttributeValue(local, value);
        }
        return charMap;
    }


    /// <summary>
    /// Merges two decimal format declarations with the same name.
    /// Per XSLT spec, multiple declarations at the same import precedence are allowed if they don't
    /// conflict on the same attribute. Conflicts are recorded (not thrown) so higher-precedence
    /// import declarations can override them.
    /// </summary>
    private static XsltDecimalFormat MergeDecimalFormats(XsltDecimalFormat existing, XsltDecimalFormat newDf, XElement newElement)
    {
        var existingExplicit = existing.ExplicitAttributes;

        // Detect conflicting attribute values (XTSE1290) — deferred until after import resolution
        // Only flags when BOTH declarations explicitly set the same attribute to different values
        string? conflict = FindDecimalFormatConflict("decimal-separator", existing.DecimalSeparator, newDf.DecimalSeparator, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("grouping-separator", existing.GroupingSeparator, newDf.GroupingSeparator, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("infinity", existing.Infinity, newDf.Infinity, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("minus-sign", existing.MinusSign, newDf.MinusSign, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("NaN", existing.NaN, newDf.NaN, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("percent", existing.Percent, newDf.Percent, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("per-mille", existing.PerMille, newDf.PerMille, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("zero-digit", existing.ZeroDigit, newDf.ZeroDigit, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("digit", existing.Digit, newDf.Digit, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("pattern-separator", existing.PatternSeparator, newDf.PatternSeparator, newElement, existingExplicit)
            ?? FindDecimalFormatConflict("exponent-separator", existing.ExponentSeparator, newDf.ExponentSeparator, newElement, existingExplicit);

        // Merge explicit attribute sets
        var mergedExplicit = new HashSet<string>(existingExplicit);
        mergedExplicit.UnionWith(newDf.ExplicitAttributes);

        return new XsltDecimalFormat
        {
            Name = existing.Name,
            DecimalSeparator = newElement.Attribute("decimal-separator") != null ? newDf.DecimalSeparator : existing.DecimalSeparator,
            GroupingSeparator = newElement.Attribute("grouping-separator") != null ? newDf.GroupingSeparator : existing.GroupingSeparator,
            Infinity = newElement.Attribute("infinity") != null ? newDf.Infinity : existing.Infinity,
            MinusSign = newElement.Attribute("minus-sign") != null ? newDf.MinusSign : existing.MinusSign,
            NaN = newElement.Attribute("NaN") != null ? newDf.NaN : existing.NaN,
            Percent = newElement.Attribute("percent") != null ? newDf.Percent : existing.Percent,
            PerMille = newElement.Attribute("per-mille") != null ? newDf.PerMille : existing.PerMille,
            ZeroDigit = newElement.Attribute("zero-digit") != null ? newDf.ZeroDigit : existing.ZeroDigit,
            Digit = newElement.Attribute("digit") != null ? newDf.Digit : existing.Digit,
            PatternSeparator = newElement.Attribute("pattern-separator") != null ? newDf.PatternSeparator : existing.PatternSeparator,
            ExponentSeparator = newElement.Attribute("exponent-separator") != null ? newDf.ExponentSeparator : existing.ExponentSeparator,
            ExplicitAttributes = mergedExplicit,
            HasConflict = existing.HasConflict || conflict != null,
            ConflictDescription = conflict ?? existing.ConflictDescription
        };
    }


    private XsltPerformSort ParsePerformSort(XElement element, SourceLocation? location)
    {
        var selectAttr = element.Attribute("select");

        // XTSE1040: when select is present, only xsl:sort and xsl:fallback children are allowed
        if (selectAttr != null)
        {
            foreach (var child in element.Elements())
            {
                if (child.Name != XsltNs + "sort" && child.Name != XsltNs + "fallback")
                    throw new XsltException($"XTSE1040: xsl:perform-sort with a select attribute must not contain {child.Name.LocalName} — only xsl:sort and xsl:fallback are allowed",
                        GetSourceLocation(child));
            }
        }

        var sorts = new List<XsltSort>();
        var contentInstructions = new List<XsltInstruction>();
        var expandText = IsExpandTextActive(element);
        var pastSorts = false;

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child when child.Name == XsltNs + "sort":
                    if (!ShouldIncludeElement(child)) break;
                    if (pastSorts)
                        throw new XsltException("XTSE0010: xsl:sort elements must come before other content in xsl:perform-sort", location);
                    sorts.Add(ParseSort(child));
                    break;
                case XElement child:
                    pastSorts = true;
                    contentInstructions.Add(ParseInstruction(child));
                    break;
                case XText text:
                    if (!IsXmlWhitespaceOnly(text.Value))
                    {
                        pastSorts = true;
                        contentInstructions.Add(CreateTextInstruction(text.Value, expandText, element));
                    }
                    break;
            }
        }

        return new XsltPerformSort
        {
            Location = location,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Sorts = sorts,
            Content = contentInstructions.Count > 0
                ? new XsltSequenceConstructor { Instructions = contentInstructions }
                : null
        };
    }


    private XsltMerge ParseMerge(XElement element, SourceLocation? location)
    {
        var sources = new List<XsltMergeSource>();
        XsltSequenceConstructor? action = null;
        var actionCount = 0;

        foreach (var child in element.Elements())
        {
            if (child.Name == XsltNs + "merge-source")
            {
                sources.Add(ParseMergeSource(child));
            }
            else if (child.Name == XsltNs + "merge-action")
            {
                actionCount++;
                if (actionCount > 1)
                    throw new XsltException("XTSE0010: xsl:merge must not contain more than one xsl:merge-action", location);
                action = ParseSequenceConstructor(child);
            }
            else if (child.Name == XsltNs + "fallback")
            {
                // xsl:fallback must appear after xsl:merge-action
                if (actionCount == 0)
                    throw new XsltException("XTSE0010: xsl:fallback must appear after xsl:merge-action in xsl:merge", location);
                // Ignore fallback content
            }
            else
            {
                // XTSE0010: Only merge-source, merge-action, and fallback allowed as children
                throw new XsltException($"XTSE0010: xsl:merge must not contain {child.Name.LocalName}", location);
            }
        }

        if (action == null)
            throw new XsltException("XTSE0010: xsl:merge must contain xsl:merge-action", location);

        if (sources.Count == 0)
            throw new XsltException("XTSE0010: xsl:merge must contain at least one xsl:merge-source", location);

        // XTSE3190: Sibling merge-source elements must not have the same name
        // Only applies to explicitly named sources; unnamed sources are allowed to coexist
        var mergeSourceNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (source.Name is { } sourceName && !mergeSourceNames.Add(sourceName))
                throw new XsltException($"XTSE3190: Sibling xsl:merge-source elements must not have the same name '{sourceName}'",
                    source.Location);
        }

        // XTSE2200: All merge-sources must have the same number of merge-keys
        if (sources.Count > 1)
        {
            var expectedKeyCount = sources[0].MergeKeys.Count;
            for (var i = 1; i < sources.Count; i++)
            {
                if (sources[i].MergeKeys.Count != expectedKeyCount)
                    throw new XsltException(
                        $"XTSE2200: All xsl:merge-source elements must have the same number of xsl:merge-key children (expected {expectedKeyCount}, found {sources[i].MergeKeys.Count})",
                        sources[i].Location);
            }

            // XTSE0020: Validate that order attributes are consistent across sources for each key position
            for (var ki = 0; ki < expectedKeyCount; ki++)
            {
                var firstOrder = sources[0].MergeKeys[ki].Order;
                var firstDataType = sources[0].MergeKeys[ki].DataType;
                var firstLang = sources[0].MergeKeys[ki].Lang;
                for (var si = 1; si < sources.Count; si++)
                {
                    // Static check: if order/data-type/lang are literal (non-AVT) values, they must match
                    var otherDataType = sources[si].MergeKeys[ki].DataType;
                    if (firstDataType != null && otherDataType != null)
                    {
                        // Check for literal mismatch
                        var firstDtVal = GetLiteralAvtValue(firstDataType);
                        var otherDtVal = GetLiteralAvtValue(otherDataType);
                        if (firstDtVal != null && otherDtVal != null && firstDtVal != otherDtVal)
                            throw new XsltException(
                                $"XTDE2210: Incompatible data-type attributes on merge-key: '{firstDtVal}' vs '{otherDtVal}'",
                                location);
                    }
                    var otherLang = sources[si].MergeKeys[ki].Lang;
                    if (firstLang != null && otherLang != null)
                    {
                        var firstLangVal = GetLiteralAvtValue(firstLang);
                        var otherLangVal = GetLiteralAvtValue(otherLang);
                        if (firstLangVal != null && otherLangVal != null && firstLangVal != otherLangVal)
                            throw new XsltException(
                                $"XTDE2210: Incompatible lang attributes on merge-key: '{firstLangVal}' vs '{otherLangVal}'",
                                location);
                    }
                }
            }
        }

        return new XsltMerge
        {
            Location = location,
            Sources = sources,
            Action = action
        };
    }


    private XsltMergeSource ParseMergeSource(XElement element)
    {
        var location = GetSourceLocation(element);
        var nameAttr = element.Attribute("name");
        var selectAttr = element.Attribute("select");
        var forEachItemAttr = element.Attribute("for-each-item");
        var forEachSourceAttr = element.Attribute("for-each-source");
        var sortBeforeMergeAttr = element.Attribute("sort-before-merge");
        var streamableAttr = element.Attribute("streamable");
        var useAccumulatorsAttr = element.Attribute("use-accumulators");

        if (selectAttr == null)
            throw new XsltException("XTSE0010: xsl:merge-source must have a select attribute", location);

        // XTSE0020: Validate streamable is yes/no if present
        NormalizeYesNo(streamableAttr?.Value, "streamable", "xsl:merge-source", element);

        // XTSE3195: Cannot specify both for-each-item and for-each-source
        if (forEachItemAttr != null && forEachSourceAttr != null)
            throw new XsltException("XTSE3195: xsl:merge-source must not have both for-each-item and for-each-source", location);

        // XTSE1505: validation and type are mutually exclusive
        var validationAttr = element.Attribute("validation");
        var typeAttr = element.Attribute("type");
        if (validationAttr != null && typeAttr != null)
            throw new XsltException("XTSE1505: The validation and type attributes are mutually exclusive on xsl:merge-source", location);

        // XTSE1660/XTSE1650: type attribute requires schema-aware processing
        if (typeAttr != null)
        {
            if (ShouldRejectSchemaAware)
                throw new XsltException("XTSE1660: A non-schema-aware XSLT processor must not accept the type attribute on xsl:merge-source", location);
            // Even with import-schema, we don't actually resolve schema types
            throw new XsltException($"XTTE1540: Schema type '{typeAttr.Value}' cannot be resolved (schema validation not supported)", location);
        }

        // XTSE1660: validation="strict" requires schema-aware processing
        if (validationAttr != null && validationAttr.Value.Trim() is "strict" or "type" && ShouldRejectSchemaAware)
            throw new XsltException($"XTSE1660: A non-schema-aware XSLT processor must not accept validation=\"{validationAttr.Value.Trim()}\" on xsl:merge-source", location);

        // XTTE1510: validation="strict" with import-schema but no actual validation runtime
        if (validationAttr != null && validationAttr.Value.Trim() == "strict" && _hasImportSchema)
            throw new XsltException("XTTE1510: Schema validation is not supported (validation=\"strict\" on xsl:merge-source)", location);

        // XTSE0020: Validate name is a valid NCName if specified
        if (nameAttr != null)
        {
            try { System.Xml.XmlConvert.VerifyNCName(nameAttr.Value); }
            catch { throw new XsltException($"XTSE0020: Invalid NCName '{nameAttr.Value}' for name attribute on xsl:merge-source", location); }
        }

        var mergeKeys = new List<XsltMergeKey>();

        foreach (var child in element.Elements())
        {
            if (child.Name == XsltNs + "merge-key")
            {
                mergeKeys.Add(ParseMergeKey(child));
            }
            else
            {
                // XTSE0010: Only merge-key allowed as child of merge-source
                throw new XsltException($"XTSE0010: xsl:merge-source must not contain {child.Name.LocalName}", location);
            }
        }

        // Parse use-accumulators attribute
        var useAccumulators = new List<QName>();
        if (useAccumulatorsAttr != null)
        {
            var accNames = useAccumulatorsAttr.Value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var accNameStr in accNames)
            {
                useAccumulators.Add(ParseQName(accNameStr, element));
            }
        }

        var mergeSource = new XsltMergeSource
        {
            Name = nameAttr?.Value,
            Location = location,
            Select = ParseExpr(selectAttr.Value, selectAttr),
            ForEachItem = forEachItemAttr != null ? ParseExpr(forEachItemAttr.Value, forEachItemAttr) : null,
            ForEachSource = forEachSourceAttr != null ? ParseExpr(forEachSourceAttr.Value, forEachSourceAttr) : null,
            SortBeforeMerge = sortBeforeMergeAttr != null
                ? ParseYesNo(sortBeforeMergeAttr) ?? throw new XsltException($"XTSE0020: Invalid value '{sortBeforeMergeAttr.Value}' for sort-before-merge attribute (must be yes/no/true/false/0/1)", location)
                : false,
            Streamable = streamableAttr != null
                ? ParseYesNo(streamableAttr) ?? false
                : false,
            MergeKeys = mergeKeys,
            UseAccumulators = useAccumulators
        };
        StreamabilityChecker.CheckStreamableMergeSource(mergeSource);
        return mergeSource;
    }


    private XsltMergeKey ParseMergeKey(XElement element)
    {
        var location = GetSourceLocation(element);
        var selectAttr = element.Attribute("select");
        var orderAttr = element.Attribute("order");
        var collationAttr = element.Attribute("collation");
        var dataTypeAttr = element.Attribute("data-type");
        var langAttr = element.Attribute("lang");
        var caseOrderAttr = element.Attribute("case-order");

        // XTSE0090: Reject disallowed attributes on xsl:merge-key
        var stableAttr = element.Attribute("stable");
        if (stableAttr != null)
            throw new XsltException("XTSE0090: Attribute 'stable' is not allowed on xsl:merge-key", location);

        // XTSE3200: xsl:merge-key with select must not have content
        ValidateSelectContentExclusive(selectAttr, element, "XTSE3200", "xsl:merge-key", location);

        // xsl:merge-key can use either select attribute or child content
        XQueryExpression? select = null;
        XsltSequenceConstructor? content = null;
        if (selectAttr != null)
        {
            select = ParseExpr(selectAttr.Value, selectAttr);
        }
        else if (element.HasElements || element.Nodes().Any(n => n is XText t && !string.IsNullOrWhiteSpace(t.Value)))
        {
            // Body content — parse as a sequence constructor
            content = ParseSequenceConstructor(element);
        }
        else
        {
            // Default: select="."
            select = ParseExpr(".");
        }

        return new XsltMergeKey
        {
            Select = select,
            Content = content,
            Order = orderAttr != null ? ParseAvt(orderAttr.Value, element, orderAttr) : null,
            Collation = collationAttr != null ? ParseAvt(collationAttr.Value, element, collationAttr) : null,
            DataType = dataTypeAttr != null ? ParseAvt(dataTypeAttr.Value, element, dataTypeAttr) : null,
            Lang = langAttr != null ? ParseAvt(langAttr.Value, element, langAttr) : null,
            CaseOrder = caseOrderAttr != null ? ParseAvt(caseOrderAttr.Value, element, caseOrderAttr) : null
        };
    }


    private XsltSort ParseSort(XElement element)
    {
        var prevContext = _nsContext;
        _nsContext = element;
        var selectAttr = element.Attribute("select");
        var langAttr = element.Attribute("lang");
        var orderAttr = element.Attribute("order");
        var collationAttr = element.Attribute("collation");
        var stableAttr = element.Attribute("stable");
        var caseOrderAttr = element.Attribute("case-order");
        var dataTypeAttr = element.Attribute("data-type");

        // XTSE0020: lang must be a valid language tag when statically known
        if (langAttr != null && !langAttr.Value.Contains('{', StringComparison.Ordinal))
        {
            var langVal = langAttr.Value.Trim();
            if (langVal.Length > 0 && (!char.IsLetter(langVal[0]) || langVal.Any(c => !char.IsLetterOrDigit(c) && c != '-')))
                throw new XsltException($"XTSE0020: Invalid language tag '{langVal}' in xsl:sort lang attribute",
                    GetSourceLocation(element));
        }

        // XTSE1017: stable attribute only allowed on the first xsl:sort in a sibling sequence
        if (stableAttr != null && element.Parent != null)
        {
            var firstSort = element.Parent.Elements(XsltNs + "sort").FirstOrDefault();
            if (firstSort != null && firstSort != element)
                throw new XsltException("XTSE1017: The stable attribute may only appear on the first xsl:sort in a sequence of sibling sort elements",
                    GetSourceLocation(element));
        }

        // XTSE0020: stable must be a valid boolean value (or an AVT)
        if (stableAttr != null && !stableAttr.Value.Contains('{', StringComparison.Ordinal))
        {
            var stableVal = stableAttr.Value.Trim();
            if (stableVal != "yes" && stableVal != "no" && stableVal != "true" && stableVal != "false"
                && stableVal != "1" && stableVal != "0")
                throw new XsltException($"XTSE0020: Invalid value '{stableAttr.Value}' for stable attribute: must be 'yes', 'no', 'true', 'false', '1', or '0'",
                    GetSourceLocation(element));
        }

        // XTSE1015: select and non-empty content are mutually exclusive on xsl:sort
        ValidateSelectContentExclusive(selectAttr, element, "XTSE1015", "xsl:sort", GetSourceLocation(element));

        _nsContext = prevContext;
        return new XsltSort
        {
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Content = selectAttr == null && element.HasElements ? ParseSequenceConstructor(element) : null,
            Lang = langAttr != null ? ParseAvt(langAttr.Value, element, langAttr) : null,
            Order = orderAttr != null ? ParseAvt(orderAttr.Value, element, orderAttr) : null,
            Collation = collationAttr != null ? ParseAvt(collationAttr.Value, element, collationAttr) : null,
            Stable = stableAttr != null ? ParseAvt(stableAttr.Value, element, stableAttr) : null,
            CaseOrder = caseOrderAttr != null ? ParseAvt(caseOrderAttr.Value, element, caseOrderAttr) : null,
            DataType = dataTypeAttr != null ? ParseAvt(dataTypeAttr.Value, element, dataTypeAttr) : null
        };
    }


    /// <summary>
    /// XTSE0125: Validates that a default-collation list contains at least one recognized collation URI.
    /// </summary>
    private static void ValidateCollationList(string value, SourceLocation? location, string errorCode = "XTSE0125")
    {
        var uris = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var uri in uris)
        {
            if (string.Equals(uri, "http://www.w3.org/2005/xpath-functions/collation/codepoint", StringComparison.Ordinal)
                || string.Equals(uri, "http://www.w3.org/2005/xpath-functions/collation/html-ascii-case-insensitive", StringComparison.Ordinal)
                || string.Equals(uri, "http://www.w3.org/2013/collation/UCA", StringComparison.Ordinal)
                || uri.StartsWith("http://www.w3.org/2013/collation/UCA?", StringComparison.Ordinal))
                return; // At least one recognized collation found
        }
        throw new XsltException($"{errorCode}: No recognized collation URI: '{value}'", location);
    }


    private static int CompareValues(object? left, object? right)
    {
        // If both are numeric, compare numerically
        if (left is double ld && right is double rd)
            return ld.CompareTo(rd);

        // If one is numeric and other is string, convert string to number
        if (left is double || right is double)
        {
            var l = ToDouble(left);
            var r = ToDouble(right);
            return l.CompareTo(r);
        }

        // If both are boolean, compare as boolean
        if (left is bool lb && right is bool rb)
            return lb.CompareTo(rb);

        // Compare as strings
        var ls = left?.ToString() ?? "";
        var rs = right?.ToString() ?? "";
        return string.Compare(ls, rs, StringComparison.Ordinal);
    }

}
