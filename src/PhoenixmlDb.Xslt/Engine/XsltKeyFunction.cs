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
/// XSLT key() function — looks up keys defined by xsl:key.
/// </summary>
internal sealed class XsltKeyFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    [ThreadStatic]
    private static HashSet<string>? _keysBeingBuilt;

    public XsltKeyFunction(DefaultXsltExecutionContext context) => _context = context;
    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "key");
    public override PhoenixmlDb.XQuery.Ast.XdmSequenceType ReturnType => new()
    {
        ItemType = PhoenixmlDb.XQuery.Ast.ItemType.Node,
        Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrMore
    };
    public override IReadOnlyList<PhoenixmlDb.XQuery.Ast.FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "name"), Type = PhoenixmlDb.XQuery.Ast.XdmSequenceType.String },
        new() { Name = new QName(NamespaceId.None, "value"), Type = new PhoenixmlDb.XQuery.Ast.XdmSequenceType { ItemType = PhoenixmlDb.XQuery.Ast.ItemType.AnyAtomicType, Occurrence = PhoenixmlDb.XQuery.Ast.Occurrence.ZeroOrMore } }
    ];

    public override async ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var keyName = arguments[0]?.ToString() ?? "";

        // XTDE1260: Validate key name is a valid QName
        try
        {
            var colonIdx = keyName.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx >= 0)
            {
                System.Xml.XmlConvert.VerifyNCName(keyName[..colonIdx]);
                System.Xml.XmlConvert.VerifyNCName(keyName[(colonIdx + 1)..]);
            }
            else
            {
                System.Xml.XmlConvert.VerifyNCName(keyName);
            }
        }
        catch (System.Xml.XmlException)
        {
            throw new XsltException($"XTDE1260: The first argument of the key() function ('{keyName}') is not a valid QName");
        }

        // Look up the xsl:key definition in the stylesheet
        var keyDef = ResolveKeyDefinition(keyName);
        if (keyDef == null)
            throw new XsltException($"XTDE1260: No xsl:key declaration found with name '{keyName}'");

        // Detect re-entrancy (circular key references)
        var keysSet = _keysBeingBuilt ??= new HashSet<string>();
        if (!keysSet.Add(keyName))
            throw new XsltException($"XTDE0640: Circular reference detected in key '{keyName}'");

        try
        {
            // Collect individual lookup values from the second argument (typed for eq comparison)
            // In BC mode (XSLT 1.0), convert lookup values to strings first per XSLT 1.0 semantics
            var bcMode = _context.IsBackwardsCompatibleMode;
            var lookupTypedValues = bcMode
                ? GetIndividualStringValues(arguments[1]).Cast<object>().ToList()
                : GetIndividualTypedValues(arguments[1]);

            // Resolve collation comparer for string matching
            StringComparer? collationComparer = keyDef.Collation != null
                ? DefaultXsltExecutionContext.GetCollationComparer(keyDef.Collation) : null;

            // Find the document containing the context node (key() 2-arg searches context document)
            // Prefer XQuery context (set by path step evaluator) over XSLT context,
            // so that document('')/key(...) searches the stylesheet document, not the source.
            var contextItem = _context.CurrentItem ?? _context.ContextItem;
            // Guard: only when context is non-null (EvaluateKeyPattern passes null context)
            if (context != null)
            {
                if (XQueryFocus.ItemOrNull(context) is XdmNode xqNode
                    && _context.FindDocumentForNode(xqNode) != null)
                    contextItem = xqNode;
            }

            // XTDE1270: key() requires document-rooted nodes
            XdmNode? searchRoot;
            var isSubtreeScoped = arguments.Count > 2;
            if (isSubtreeScoped)
            {
                // 3-argument: search subtree rooted at the third argument (XSLT 3.0 §16.3.2)
                var thirdArg = arguments[2];
                if (thirdArg is not XdmNode thirdNode || _context.FindDocumentForNode(thirdNode) == null)
                    throw new XsltException("XTDE1270: The root of the tree containing the node supplied as the third argument to key() is not a document node");
                searchRoot = thirdNode;
            }
            else
            {
                // 2-argument: search context document
                if (contextItem == null)
                    throw new XsltException("XTDE1270: The key() function with two arguments requires a context node, but there is no context node");
                if (contextItem is not XdmNode contextNode || _context.FindDocumentForNode(contextNode) == null)
                    throw new XsltException("XTDE1270: The root of the tree containing the context node is not a document node");
                searchRoot = _context.FindDocumentForNode(contextNode);
            }

            // Collect candidate nodes from the search root
            var candidates = new List<XdmNode>();
            if (searchRoot != null)
            {
                // For 3-arg key(), include the search root itself (it's part of its own subtree)
                if (isSubtreeScoped)
                    candidates.Add(searchRoot);
                CollectDescendants(searchRoot, candidates);
            }
            else if (_context._nodeStore != null)
                candidates.AddRange(_context._nodeStore.GetAllNodes());

            // Build index: match nodes and evaluate use expression
            // Iterate all definitions for this key name (supports multiple xsl:key with same name)
            var isComposite = keyDef.Composite;
            var results = new List<object>();
            var seen = new HashSet<NodeId>();
            foreach (var node in candidates)
            {
                foreach (var def in keyDef.AllDefinitions)
                {
                    bool defMatched;
                    using (var mc = _context.AcquireMatchContext())
                        defMatched = def.Match.Matches(node, mc.Value);
                    if (defMatched)
                    {
                        var useValues = await EvaluateUseExpression(def, node).ConfigureAwait(false);
                        if (isComposite)
                        {
                            // Composite key: all use values form a single tuple, compared componentwise
                            if (useValues.Count == lookupTypedValues.Count)
                            {
                                bool allMatch = true;
                                for (int i = 0; i < useValues.Count; i++)
                                {
                                    var uv = bcMode ? (object)DefaultXsltExecutionContext.StringValueOf(useValues[i]) : useValues[i];
                                    if (!KeyValueEquals(uv, lookupTypedValues[i], collationComparer))
                                    {
                                        allMatch = false;
                                        break;
                                    }
                                }
                                if (allMatch && seen.Add(node.Id))
                                    results.Add(node);
                            }
                        }
                        else
                        {
                            // Non-composite: each use value is a separate key
                            foreach (var rawUv in useValues)
                            {
                                var uv = bcMode ? (object)DefaultXsltExecutionContext.StringValueOf(rawUv) : rawUv;
                                foreach (var lv in lookupTypedValues)
                                {
                                    if (KeyValueEquals(uv, lv, collationComparer) && seen.Add(node.Id))
                                    {
                                        results.Add(node);
                                        break;
                                    }
                                }
                                if (seen.Contains(node.Id))
                                    break;
                            }
                        }
                    }
                    if (seen.Contains(node.Id))
                        break; // already added by another definition
                }
            }
            return results.Count > 0 ? results.ToArray() : Array.Empty<object>();
        }
        finally
        {
            keysSet.Remove(keyName);
        }
    }

    /// <summary>
    /// Extracts individual string values from a key() argument.
    /// When the argument is a sequence, each item produces a separate lookup value.
    /// </summary>
    internal static HashSet<string> GetIndividualStringValues(object? value, StringComparer? comparer = null)
    {
        var values = new HashSet<string>(comparer ?? StringComparer.Ordinal);
        switch (value)
        {
            case null:
                break;
            case object[] arr:
                foreach (var item in arr)
                    values.Add(DefaultXsltExecutionContext.StringValueOf(item));
                break;
            case IEnumerable<object?> seq when value is not string && value is not XdmNode:
                foreach (var item in seq)
                    values.Add(DefaultXsltExecutionContext.StringValueOf(item));
                break;
            default:
                values.Add(DefaultXsltExecutionContext.StringValueOf(value));
                break;
        }
        return values;
    }

    /// <summary>
    /// Extracts individual typed values from a key() argument, preserving XPath types
    /// (integers, doubles, DateTimeOffset, etc.) instead of converting to strings.
    /// </summary>
    internal static List<object> GetIndividualTypedValues(object? value)
    {
        var values = new List<object>();
        switch (value)
        {
            case null:
                break;
            case ResultTreeFragment rtf:
                // RTFs (temporary trees from variables/params without as="") are string-valued
                values.Add(rtf.ToString());
                break;
            case object[] arr:
                foreach (var item in arr)
                {
                    if (item is ResultTreeFragment itemRtf)
                        values.Add(itemRtf.ToString());
                    else if (item != null)
                        values.Add(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(item) ?? item);
                }
                break;
            case IEnumerable<object?> seq when value is not string && value is not XdmNode:
                foreach (var item in seq)
                {
                    if (item is ResultTreeFragment seqRtf)
                        values.Add(seqRtf.ToString());
                    else if (item != null)
                        values.Add(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(item) ?? item);
                }
                break;
            default:
                values.Add(PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(value) ?? value);
                break;
        }
        return values;
    }

    /// <summary>
    /// Compares two key values using XPath eq semantics for key matching.
    /// Returns true if the values are equal under typed comparison rules.
    /// NaN != NaN, different incompatible types → no match.
    /// </summary>
    internal static bool KeyValueEquals(object? useValue, object? lookupValue, StringComparer? collationComparer = null)
    {
        if (useValue is null || lookupValue is null)
            return useValue is null && lookupValue is null;

        // Normalize untyped-atomic and anyURI wrappers to their underlying string.
        // A use="@attr" value atomizes to xs:untypedAtomic (a CLR XsUntypedAtomic struct,
        // not a string), so without this it would never match a string lookup key.
        // Per XPath key-matching semantics an xs:untypedAtomic use value compares against
        // the lookup value as though cast to the lookup value's type — string-vs-string
        // here, and the cast-to-numeric branch below handles the numeric-lookup case.
        // xs:anyURI is promotable to xs:string, so it is normalized the same way.
        if (useValue is XsUntypedAtomic useUntyped)
            useValue = useUntyped.Value;
        else if (useValue is XsAnyUri useUri)
            useValue = useUri.Value;
        if (lookupValue is XsUntypedAtomic lookupUntyped)
            lookupValue = lookupUntyped.Value;
        else if (lookupValue is XsAnyUri lookupUri)
            lookupValue = lookupUri.Value;

        var useIsNumeric = IsNumericForKey(useValue);
        var lookupIsNumeric = IsNumericForKey(lookupValue);

        // Both numeric: compare with numeric promotion, NaN != NaN
        if (useIsNumeric && lookupIsNumeric)
        {
            var ud = Convert.ToDouble(useValue, System.Globalization.CultureInfo.InvariantCulture);
            var ld = Convert.ToDouble(lookupValue, System.Globalization.CultureInfo.InvariantCulture);
            if (double.IsNaN(ud) || double.IsNaN(ld))
                return false;
            return ud == ld;
        }

        // Both strings: string comparison (using collation if specified)
        if (useValue is string us && lookupValue is string ls)
            return collationComparer != null
                ? collationComparer.Equals(us, ls)
                : string.Equals(us, ls, StringComparison.Ordinal);

        // xs:untypedAtomic (string) vs typed: cast the string to the typed type
        if (useValue is string useStr && lookupIsNumeric)
        {
            if (double.TryParse(useStr, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var d))
            {
                var ld = Convert.ToDouble(lookupValue, System.Globalization.CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsNaN(ld))
                    return false;
                return d == ld;
            }
            return false;
        }
        if (lookupValue is string lookupStr && useIsNumeric)
        {
            // Typed use value vs string lookup → different types, no match
            // (Per XSLT spec, only untyped use values get promoted, not the other way)
            return false;
        }

        // Date/time comparison with new types
        if (useValue is XsDateTime uxdt && lookupValue is XsDateTime lxdt)
            return uxdt.Value.UtcDateTime == lxdt.Value.UtcDateTime;
        if (useValue is XsDate uxd && lookupValue is XsDate lxd)
            return uxd == lxd;
        if (useValue is XsTime uxt && lookupValue is XsTime lxt)
            return uxt == lxt;

        // DateTimeOffset comparison
        if (useValue is DateTimeOffset udto && lookupValue is DateTimeOffset ldto)
            return udto.UtcDateTime == ldto.UtcDateTime;

        // DateOnly comparison
        if (useValue is DateOnly udo && lookupValue is DateOnly ldo)
            return udo == ldo;

        // Fall back to object equality
        return object.Equals(useValue, lookupValue);
    }

    private static bool IsNumericForKey(object? obj) =>
        obj is byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or System.Numerics.BigInteger;

    /// <summary>
    /// Resolves a key name (lexical QName) to the corresponding XsltKey definition.
    /// Handles both unprefixed names and prefixed names like "baz:mykey".
    /// </summary>
    internal PhoenixmlDb.Xslt.Ast.XsltKey? ResolveKeyDefinition(string keyName)
    {
        // Try exact QName lookup (for unprefixed names)
        var qname = new QName(NamespaceId.None, keyName);
        if (_context._stylesheet.Keys.TryGetValue(qname, out var keyDef))
            return FilterToCallingPackage(keyDef);

        // Handle namespace-prefixed key names like "baz:mykey"
        var colonIdx = keyName.IndexOf(':', StringComparison.Ordinal);
        if (colonIdx > 0)
        {
            var prefix = keyName[..colonIdx];
            var localName = keyName[(colonIdx + 1)..];

            // Resolve prefix to namespace URI using stylesheet namespace bindings,
            // then use the parser's static namespace resolver (keys are stored with parser-time IDs)
            if (_context._stylesheet.Namespaces.TryGetValue(prefix, out var nsUri))
            {
                var nsId = StylesheetParser.ResolveNamespaceUri(nsUri);
                var nsQname = new QName(nsId, localName);
                if (_context._stylesheet.Keys.TryGetValue(nsQname, out keyDef))
                    return FilterToCallingPackage(keyDef);
            }

            // XsltStylesheet.Namespaces is a single prefix -> URI map written last-wins while
            // parsing, so a prefix bound to DIFFERENT URIs in different modules keeps only one
            // of them and the lookup above builds the wrong expanded name. Whether it works
            // then depends on module order alone: swapping two xsl:include lines flips a key
            // between resolving and XTDE1260.
            //
            // XSpec is the motivating case — a dozen of its modules each declare their own
            // xmlns:local with a distinct URI, and key('local:scenarios') failed in 27 of its
            // 162 suites for that reason.
            //
            // Fall back to the local name when it is UNAMBIGUOUS. Strictly the argument should
            // resolve against the in-scope namespaces of the element containing the key() call,
            // which needs the expression's static context at runtime; this is narrower but
            // sound where it applies, and it refuses to guess when it cannot tell.
            var byLocalName = _context._stylesheet.Keys
                .Where(kv => string.Equals(kv.Key.LocalName, localName, StringComparison.Ordinal))
                .ToList();
            if (byLocalName.Count == 1)
                return FilterToCallingPackage(byLocalName[0].Value);
        }

        return null;
    }

    /// <summary>
    /// Keys are LOCAL to their declaring package (XSLT 3.0 §3.6.2). Restrict the merged
    /// key definition to only those component definitions declared in the CALLING package
    /// (the package owning the executing template/function; <c>null</c> = principal).
    /// If the calling package declares no key of this name, returns <c>null</c> so the
    /// caller raises XTDE1260 (use-package-105); otherwise returns a view whose
    /// definitions are exactly the calling package's (use-package-102). Non-package
    /// stylesheets are unaffected: every key's <c>PackageStylesheet</c> is <c>null</c> and
    /// the current package is <c>null</c>, so all definitions are retained as before.
    /// </summary>
    private PhoenixmlDb.Xslt.Ast.XsltKey? FilterToCallingPackage(PhoenixmlDb.Xslt.Ast.XsltKey keyDef)
    {
        var callingPackage = XsltFormatNumberEngine.GetCurrentPackageStylesheet(_context);
        var kept = keyDef.AllDefinitions
            .Where(d => ReferenceEquals(d.PackageStylesheet, callingPackage))
            .ToList();
        if (kept.Count == 0)
            return null;
        // Fast path: nothing was filtered out — return the original definition unchanged.
        if (kept.Count == 1 && keyDef.OtherDefinitions is null && ReferenceEquals(kept[0], keyDef))
            return keyDef;
        var primary = kept[0];
        return new PhoenixmlDb.Xslt.Ast.XsltKey
        {
            Name = primary.Name,
            Match = primary.Match,
            Use = primary.Use,
            UseContent = primary.UseContent,
            Collation = primary.Collation,
            Composite = primary.Composite,
            PackageStylesheet = primary.PackageStylesheet,
            OtherDefinitions = kept.Count > 1 ? kept.Skip(1).ToList() : null
        };
    }

    /// <summary>
    /// Evaluates the use expression (or use content) for a matched node,
    /// returning individual typed values (preserving XPath types for proper eq comparison).
    /// </summary>
    private async ValueTask<List<object>> EvaluateUseExpression(PhoenixmlDb.Xslt.Ast.XsltKey keyDef, XdmNode node)
    {
        var values = new List<object>();
        _context.PushContextItem(node, 1, 1);
        try
        {
            object? result = null;
            if (keyDef.Use != null)
                result = await _context.EvaluateAsync(keyDef.Use).ConfigureAwait(false);
            else if (keyDef.UseContent != null)
                result = await _context.EvaluateSequenceConstructorAsync(keyDef.UseContent).ConfigureAwait(false);
            else
                return values;

            // Each item in the result produces a separate key value (atomized, preserving type)
            if (result is null)
            {
                // null result → no key value
            }
            else if (result is object[] arr)
            {
                foreach (var item in arr)
                {
                    if (item != null)
                    {
                        var a = PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(item);
                        values.Add(a ?? item);
                    }
                }
            }
            else if (result is IEnumerable<object?> seq && result is not string && result is not XdmNode)
            {
                foreach (var el in seq)
                {
                    if (el != null)
                    {
                        var a = PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(el);
                        values.Add(a ?? el);
                    }
                }
            }
            else
            {
                var atomized = PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(result);
                values.Add(atomized ?? result);
            }
        }
        finally
        {
            _context.PopContextItem();
        }
        return values;
    }

    private void CollectDescendants(object? node, List<XdmNode> descendants)
    {
        if (node == null || _context._nodeStore == null)
            return;

        if (node is XdmDocument doc)
        {
            foreach (var childId in doc.Children)
            {
                var child = _context._nodeStore.GetNode(childId);
                if (child != null)
                { descendants.Add(child); CollectDescendants(child, descendants); }
            }
        }
        else if (node is XdmElement elem)
        {
            // Include attributes
            foreach (var attrId in elem.Attributes)
            {
                var attr = _context._nodeStore.GetNode(attrId);
                if (attr != null)
                    descendants.Add(attr);
            }
            foreach (var childId in elem.Children)
            {
                var child = _context._nodeStore.GetNode(childId);
                if (child != null)
                { descendants.Add(child); CollectDescendants(child, descendants); }
            }
        }
    }
}
