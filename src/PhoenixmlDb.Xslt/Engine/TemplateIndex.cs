using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xdm.Serialization;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
// XPath 4.0 ordered map: insertion-order iteration as a structural guarantee.
// xslt keeps its existing default key-equality (pass EqualityComparer<object>.Default
// at each construction site) — this change is about iteration order only.
using OrderedXdmMap = PhoenixmlDb.XQuery.Execution.OrderedXdmMap;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Index for efficient template lookup.
/// </summary>
internal sealed class TemplateIndex
{
    /// <summary>Sentinel QName for mode="#all" — matches any mode.</summary>
    internal static readonly QName AllModeSentinel = new(NamespaceId.None, "#all");
    /// <summary>Sentinel QName for mode="#default" when combined with named modes.</summary>
    internal static readonly QName DefaultModeSentinel = new(NamespaceId.None, "#default");

    private readonly Dictionary<QName, List<XsltTemplate>> _templatesByMode = new();
    private readonly List<XsltTemplate> _defaultModeTemplates = new();
    private readonly List<XsltTemplate> _allModeTemplates = new();
    private readonly List<TemplateIndex> _importedIndexes = new();
    // Cache for merged+sorted mode template lists to avoid re-allocating and re-sorting per call
    private readonly Dictionary<QName, List<XsltTemplate>> _mergedModeCache = new();

    /// <summary>Maps templates to their owning TemplateIndex for apply-imports module scoping.</summary>
    private readonly Dictionary<XsltTemplate, TemplateIndex> _templateOwners = new();

    public TemplateIndex(XsltStylesheet stylesheet)
    {
        IndexTemplates(stylesheet.Templates);

        // Build indexes for imported stylesheets (lower precedence)
        foreach (var imported in stylesheet.Imports)
        {
            _importedIndexes.Add(new TemplateIndex(imported));
        }
    }

    /// <summary>
    /// Finds a matching template searching only the imported stylesheet indexes
    /// (lower precedence). Used by xsl:apply-imports which must skip
    /// same-precedence templates and only search imports.
    /// </summary>
    public XsltTemplate? FindImportedTemplate(object node, QName? mode, XsltContext context)
    {
        for (var i = _importedIndexes.Count - 1; i >= 0; i--)
        {
            var match = _importedIndexes[i].FindMatchingTemplate(node, mode, context);
            if (match != null)
                return match;
        }
        return null;
    }

    /// <summary>
    /// Finds the TemplateIndex that directly owns a given template.
    /// Searches this index and all nested imported indexes.
    /// </summary>
    public TemplateIndex? FindOwnerIndex(XsltTemplate template)
    {
        if (_templateOwners.ContainsKey(template))
            return this;
        foreach (var imported in _importedIndexes)
        {
            var found = imported.FindOwnerIndex(template);
            if (found != null)
                return found;
        }
        return null;
    }

    private void IndexTemplates(List<XsltTemplate> templates)
    {
        foreach (var template in templates)
        {
            if (template.Match == null)
                continue;

            // Split union patterns into separate template entries
            var expandedTemplates = ExpandUnionPattern(template);

            foreach (var t in expandedTemplates)
            {
                _templateOwners[t] = this;
                if (t.Modes.Count == 0)
                {
                    _defaultModeTemplates.Add(t);
                }
                else if (t.Modes.Contains(AllModeSentinel))
                {
                    // mode="#all" — add to all-modes list AND default mode
                    _allModeTemplates.Add(t);
                    _defaultModeTemplates.Add(t);
                }
                else
                {
                    foreach (var mode in t.Modes)
                    {
                        if (mode == DefaultModeSentinel)
                        {
                            _defaultModeTemplates.Add(t);
                            continue;
                        }
                        if (!_templatesByMode.TryGetValue(mode, out var list))
                        {
                            list = new List<XsltTemplate>();
                            _templatesByMode[mode] = list;
                        }
                        list.Add(t);
                    }
                }
            }
        }

        // Stable sort by priority (higher first); last-declared wins for equal priority.
        // Since templates are added in declaration order, we use a stable sort
        // which preserves insertion order for equal keys.
        StableSortByPriority(_defaultModeTemplates);
        StableSortByPriority(_allModeTemplates);

        foreach (var list in _templatesByMode.Values)
        {
            StableSortByPriority(list);
        }
    }

    /// <summary>
    /// Expands a template with a union match pattern into multiple templates,
    /// one per union alternative, each with its own default priority.
    /// Non-union templates are returned as-is.
    /// </summary>
    private static List<XsltTemplate> ExpandUnionPattern(XsltTemplate template)
    {
        if (template.Match is not UnionPattern union)
            return [template];

        // Shared identity so that on-multiple-match="fail" checks can skip
        // alternatives from the same union pattern (spec bug 30402).
        // For next-match: only skip when there is an explicit priority (all
        // alternatives share it and represent one rule). Without explicit
        // priority each alternative is a separate rule (XSLT 3.0 §6.6).
        // Identify the group by the UNION PATTERN instance — the one thing every copy of a rule
        // shares. `new object()` minted a fresh group per split, and the template instance is no
        // better: CloneTemplateWithVisibility makes a NEW XsltTemplate that shares `Match` but
        // whose UnionGroupId is still null, so the original and the clone each expanded into a
        // different group. Two alternatives of ONE xsl:template then stopped recognising each
        // other as siblings, and matching both raised XTDE0540 for a conflict that spec bug
        // 30402 says is not one.
        //
        // Keying on the pattern is stable across cloning and makes re-expansion idempotent,
        // while two genuinely distinct rules still hold distinct pattern objects.
        var groupId = template.UnionGroupId ?? (object)union;
        var result = new List<XsltTemplate>(union.Patterns.Count);
        foreach (var pattern in union.Patterns)
        {
            result.Add(new XsltTemplate
            {
                Name = template.Name,
                Match = pattern,
                Priority = template.Priority, // explicit priority overrides all alternatives
                Modes = template.Modes,
                As = template.As,
                Parameters = template.Parameters,
                Body = template.Body,
                Visibility = template.Visibility,
                UnionGroupId = groupId,
                ImportPrecedence = template.ImportPrecedence
            });
        }
        return result;
    }

    /// <summary>
    /// Stable sort by descending priority. For equal priorities, last-declared
    /// template wins (maintained by stable sort preserving insertion order,
    /// then the natural iteration picks the last matching one... but we need
    /// last-declared to be first in list). We reverse equal-priority elements
    /// so that last-declared appears first when iterating.
    /// </summary>
    private static void StableSortByPriority(List<XsltTemplate> templates)
    {
        // Decorate with original index for stable sort
        var indexed = templates.Select((t, i) => (template: t, index: i)).ToList();
        // Sort by import precedence descending (XSLT 3.0 §6.6.2 — precedence dominates
        // priority), then priority descending, then by index descending (last-declared wins).
        indexed.Sort((a, b) =>
        {
            var cmp = CompareForConflict(a.template, b.template);
            return cmp != 0 ? cmp : b.index.CompareTo(a.index);
        });
        for (var i = 0; i < templates.Count; i++)
        {
            templates[i] = indexed[i].template;
        }
    }

    public static double EffectivePriority(XsltTemplate template)
    {
        return template.Priority ?? template.Match?.DefaultPriority ?? 0.5;
    }

    /// <summary>Import precedence for conflict resolution; higher wins.</summary>
    public static int EffectivePrecedence(XsltTemplate template) => template.ImportPrecedence;

    /// <summary>
    /// Orders two matching template rules for conflict resolution (XSLT 3.0 §6.6.2):
    /// higher import precedence first, then higher priority. Returns a negative value if
    /// <paramref name="a"/> should sort before (i.e. win over) <paramref name="b"/>.
    /// Does not break ties by document order — callers handle that separately.
    /// </summary>
    private static int CompareForConflict(XsltTemplate a, XsltTemplate b)
    {
        var byPrec = EffectivePrecedence(b).CompareTo(EffectivePrecedence(a));
        if (byPrec != 0)
            return byPrec;
        return EffectivePriority(b).CompareTo(EffectivePriority(a));
    }

    /// <summary>
    /// True when two matching rules are genuinely ambiguous — same import precedence AND
    /// same priority — which is the condition for XTDE0540 under on-multiple-match='fail'.
    /// </summary>
    public static bool SameConflictRank(XsltTemplate a, XsltTemplate b) =>
        EffectivePrecedence(a) == EffectivePrecedence(b)
        && EffectivePriority(a) == EffectivePriority(b);

    /// <summary>
    /// Resolves namespace URIs in patterns to NamespaceIds using the provided resolver.
    /// This should be called once per transformation when the node store is available.
    /// </summary>
    public void ResolvePatternNamespaces(Func<string, NamespaceId> namespaceResolver)
    {
        ResolveNamespacesInTemplates(_defaultModeTemplates, namespaceResolver);
        ResolveNamespacesInTemplates(_allModeTemplates, namespaceResolver);
        foreach (var list in _templatesByMode.Values)
        {
            ResolveNamespacesInTemplates(list, namespaceResolver);
        }
        foreach (var importedIndex in _importedIndexes)
        {
            importedIndex.ResolvePatternNamespaces(namespaceResolver);
        }
    }

    private static void ResolveNamespacesInTemplates(List<XsltTemplate> templates, Func<string, NamespaceId> resolver)
    {
        foreach (var template in templates)
        {
            ResolveNamespacesInPattern(template.Match, resolver);
        }
    }

    internal static void ResolveNamespacesInPattern(XsltPattern? pattern, Func<string, NamespaceId> resolver)
    {
        switch (pattern)
        {
            case PathPattern pp:
                foreach (var step in pp.Steps)
                {
                    // A NameTest can also be NESTED inside a KindTest — element(x:report) keeps
                    // its name in KindTest.Name, and document-node(element(x:report)) keeps it in
                    // KindTest.DocumentElementTest. This loop only ever visited the bare
                    // NameTest case, so those nested ones kept NamespaceUri with
                    // ResolvedNamespace still null, and NameTest.Matches ends on
                    //
                    //     // This shouldn't happen if namespace resolution is working correctly
                    //     return false;
                    //
                    // which is why match="document-node(element(x:report))" matched NOTHING while
                    // match="x:report" matched fine. Reported by Martin Honnen against XSpec's
                    // report stylesheet. element(x:report) was broken the same way.
                    switch (step.NodeTest)
                    {
                        case NameTest nt:
                            nt.ResolveNamespace(resolver);
                            break;
                        case KindTest kt:
                            kt.Name?.ResolveNamespace(resolver);
                            kt.DocumentElementTest?.ResolveNamespace(resolver);
                            break;
                    }
                }
                break;
            case UnionPattern up:
                foreach (var p in up.Patterns)
                {
                    ResolveNamespacesInPattern(p, resolver);
                }
                break;
            case ExceptPattern ep:
                ResolveNamespacesInPattern(ep.Left, resolver);
                ResolveNamespacesInPattern(ep.Right, resolver);
                break;
            case IntersectPattern ip:
                ResolveNamespacesInPattern(ip.Left, resolver);
                ResolveNamespacesInPattern(ip.Right, resolver);
                break;

            // Every remaining subclass that WRAPS another pattern. Omitting them left the
            // wrapped pattern's prefixes unresolved, so the name test compared an unresolved
            // NamespaceId and matched nothing - silently, because a pattern that matches
            // nothing is indistinguishable from a pattern with no matching nodes.
            //
            // match="(x:a | x:b)[true()]" therefore selected NOTHING while the identical
            // unparenthesized match="x:a[true()] | x:b[true()]" worked, and un-namespaced
            // "(a | b)[true()]" also worked - which is why this survived: the obvious test
            // uses no prefix. XSpec's stacked-vardecls accumulator is
            // "(x:scenario/x:param | x:scenario/x:variable | x:context)[...]".
            //
            // DotPattern is deliberately absent - it wraps no pattern and has no name test.
            case ParenthesizedPositionalPattern ppp:
                ResolveNamespacesInPattern(ppp.Inner, resolver);
                break;
            case KeyPattern kp:
                ResolveNamespacesInPattern(kp.Continuation, resolver);
                break;
            case IdPattern idp:
                ResolveNamespacesInPattern(idp.Continuation, resolver);
                break;
            case VariableReferencePattern vrp:
                ResolveNamespacesInPattern(vrp.Continuation, resolver);
                break;
            case DocFunctionPattern dfp:
                ResolveNamespacesInPattern(dfp.Continuation, resolver);
                break;
        }
    }

    /// <summary>
    /// Returns true if the given mode name is explicitly used by a template rule
    /// (not just via mode="#all") or declared via xsl:mode.
    /// </summary>
    public bool IsModeExplicitlyUsed(QName mode)
    {
        if (_templatesByMode.ContainsKey(mode))
            return true;
        foreach (var imported in _importedIndexes)
        {
            if (imported.IsModeExplicitlyUsed(mode))
                return true;
        }
        return false;
    }

    public XsltTemplate? FindMatchingTemplate(object node, QName? mode, XsltContext context)
    {
        return FindMatchingTemplate(node, mode, context, after: null);
    }

    public XsltTemplate? FindMatchingTemplate(object node, QName? mode, XsltContext context, XsltTemplate? after)
    {
        // Treat unnamed/default mode (empty string QName) same as null — use default mode templates
        var isDefaultMode = !mode.HasValue || mode.Value.LocalName.Length == 0;
        List<XsltTemplate> templates;
        if (!isDefaultMode && _templatesByMode.TryGetValue(mode!.Value, out var modeTemplates))
        {
            // Merge mode-specific templates with #all templates, sorted by priority
            if (_allModeTemplates.Count > 0)
            {
                // Use cached merged list to avoid re-allocating and re-sorting per call
                if (!_mergedModeCache.TryGetValue(mode.Value, out templates!))
                {
                    templates = new List<XsltTemplate>(modeTemplates.Count + _allModeTemplates.Count);
                    templates.AddRange(modeTemplates);
                    templates.AddRange(_allModeTemplates);
                    templates.Sort(CompareForConflict);
                    _mergedModeCache[mode.Value] = templates;
                }
            }
            else
            {
                templates = modeTemplates;
            }
        }
        else if (!isDefaultMode && _allModeTemplates.Count > 0)
        {
            // No mode-specific templates, but #all templates apply
            templates = _allModeTemplates;
        }
        else if (!isDefaultMode)
        {
            // Named mode with no matching templates — don't fall back to
            // default mode. Per XSLT spec, modes are separate matching
            // contexts; return null to trigger built-in template rules.
            templates = _allModeTemplates; // empty if no #all templates
        }
        else
        {
            templates = _defaultModeTemplates;
        }

        var pastAfter = after == null;
        // For next-match: skip remaining union alternatives only when the
        // template has an explicit priority (all alternatives share it and
        // represent one template rule). Without explicit priority, each
        // alternative is a separate rule per XSLT 3.0 §6.6.
        var skipUnionGroup = (after?.Priority.HasValue == true) ? after.UnionGroupId : null;
        foreach (var template in templates)
        {
            if (!pastAfter)
            {
                if (ReferenceEquals(template, after))
                    pastAfter = true;
                continue;
            }

            // Skip remaining alternatives of the same union pattern (same template rule body)
            if (skipUnionGroup != null && template.UnionGroupId == skipUnionGroup)
                continue;

            if (template.Match!.Matches(node, context))
            {
                return template;
            }
        }

        // Search imported stylesheets (lower precedence, in reverse order per XSLT spec)
        // If we haven't passed 'after' yet, carry it through imports
        for (var i = _importedIndexes.Count - 1; i >= 0; i--)
        {
            var match = _importedIndexes[i].FindMatchingTemplate(node, mode, context, pastAfter ? null : after);
            if (match != null)
                return match;
            // If this import contained 'after' but had no subsequent match,
            // mark it consumed so remaining lower-precedence imports search freely
            if (!pastAfter && after != null && _importedIndexes[i].FindOwnerIndex(after) != null)
                pastAfter = true;
        }

        return null;
    }
}
