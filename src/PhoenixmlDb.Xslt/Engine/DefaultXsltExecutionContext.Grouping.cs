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

internal sealed partial class DefaultXsltExecutionContext
{

    private static bool CompareAtomic(BinaryOperator op, object? left, object? right)
    {
        // Promote untyped strings to numeric if the other side is numeric.
        // Mirrors XPath 3.0 general comparison rules.
        var leftP = left;
        var rightP = right;
        if (leftP is string ls && rightP is not string && IsWatcherNumeric(rightP))
        {
            if (!TryParseWatcherNumber(ls, out leftP))
                throw new PhoenixmlDb.XQuery.Functions.XQueryException("XPTY0004",
                    $"Cannot compare xs:string '{ls}' with numeric type — value is not a valid number");
        }
        else if (rightP is string rs && leftP is not string && IsWatcherNumeric(leftP))
        {
            if (!TryParseWatcherNumber(rs, out rightP))
                throw new PhoenixmlDb.XQuery.Functions.XQueryException("XPTY0004",
                    $"Cannot compare xs:string '{rs}' with numeric type — value is not a valid number");
        }

        // Numeric vs numeric
        if (IsWatcherNumeric(leftP) && IsWatcherNumeric(rightP))
        {
            var ld = ToWatcherDouble(leftP!);
            var rd = ToWatcherDouble(rightP!);
            return op switch
            {
                BinaryOperator.GeneralEqual => ld == rd,
                BinaryOperator.GeneralNotEqual => ld != rd,
                BinaryOperator.GeneralLessThan => ld < rd,
                BinaryOperator.GeneralLessOrEqual => ld <= rd,
                BinaryOperator.GeneralGreaterThan => ld > rd,
                BinaryOperator.GeneralGreaterOrEqual => ld >= rd,
                _ => false
            };
        }

        // String vs string
        if (leftP is string lstr && rightP is string rstr)
        {
            var cmp = string.CompareOrdinal(lstr, rstr);
            return op switch
            {
                BinaryOperator.GeneralEqual => cmp == 0,
                BinaryOperator.GeneralNotEqual => cmp != 0,
                BinaryOperator.GeneralLessThan => cmp < 0,
                BinaryOperator.GeneralLessOrEqual => cmp <= 0,
                BinaryOperator.GeneralGreaterThan => cmp > 0,
                BinaryOperator.GeneralGreaterOrEqual => cmp >= 0,
                _ => false
            };
        }

        // Fallback: equality via Equals
        return op switch
        {
            BinaryOperator.GeneralEqual => Equals(leftP, rightP),
            BinaryOperator.GeneralNotEqual => !Equals(leftP, rightP),
            _ => throw new PhoenixmlDb.XQuery.Functions.XQueryException("XPTY0004",
                $"Cannot compare {leftP?.GetType().Name} with {rightP?.GetType().Name}")
        };
    }


    private static bool GroupAdjacentKeysEqual(object? a, object? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;

        var aSeq = a as object?[];
        var bSeq = b as object?[];
        if (aSeq != null || bSeq != null)
        {
            aSeq ??= new[] { a };
            bSeq ??= new[] { b };
            if (aSeq.Length != bSeq.Length) return false;
            for (int i = 0; i < aSeq.Length; i++)
                if (!GroupAdjacentKeysEqual(aSeq[i], bSeq[i])) return false;
            return true;
        }

        return Equals(a, b) || string.Equals(StringValueOf(a), StringValueOf(b), StringComparison.Ordinal);
    }


    private async Task<List<(object Key, List<object> Items)>> SortGroupsAsync(
        List<(object Key, List<object> Items)> groups,
        List<XsltSort> sorts)
    {
        // Resolve sort properties (AVTs) once
        var sortOrders = new List<string>();
        var sortDataTypes = new List<string>();
        foreach (var sort in sorts)
        {
            sortOrders.Add(sort.Order != null ? await EvaluateAvtAsync(sort.Order).ConfigureAwait(false) : "ascending");
            sortDataTypes.Add(sort.DataType != null ? await EvaluateAvtAsync(sort.DataType).ConfigureAwait(false) : "text");
        }

        // Build sort keys for each group (using the first item as context)
        var keyed = new List<(object Key, List<object> Items, List<string> SortKeys)>();
        foreach (var (key, items) in groups)
        {
            var sortKeys = new List<string>();
            PushContextItem(items[0], 1, 1);
            PushScope();
            // Set current-group and current-grouping-key for sort key evaluation (e.g., sort by current-grouping-key())
            SetVariable(new QName(NamespaceId.None, "current-group"), items);
            SetVariable(new QName(NamespaceId.None, "current-grouping-key"), key);
            try
            {
                foreach (var sort in sorts)
                {
                    if (sort.Select != null)
                    {
                        var val = await EvaluateAsync(sort.Select).ConfigureAwait(false);
                        sortKeys.Add(StringValueOf(val));
                    }
                    else
                    {
                        sortKeys.Add(StringValueOf(items[0]));
                    }
                }
            }
            finally
            {
                PopScope();
                PopContextItem();
            }
            keyed.Add((key, items, sortKeys));
        }

        // Sort using all sort keys
        keyed.Sort((a, b) =>
        {
            for (var i = 0; i < sorts.Count && i < a.SortKeys.Count && i < b.SortKeys.Count; i++)
            {
                var cmp = sortDataTypes[i] == "number"
                    ? CompareNumeric(a.SortKeys[i], b.SortKeys[i])
                    : string.Compare(a.SortKeys[i], b.SortKeys[i], StringComparison.Ordinal);
                if (sortOrders[i] == "descending")
                    cmp = -cmp;
                if (cmp != 0)
                    return cmp;
            }
            return 0;
        });

        return keyed.Select(k => (k.Key, k.Items)).ToList();
    }


    private static int CompareNumeric(string a, string b)
    {
        var aOk = double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var aVal);
        var bOk = double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var bVal);
        if (aOk && bOk)
            return aVal.CompareTo(bVal);
        if (aOk)
            return -1; // numbers before non-numbers
        if (bOk)
            return 1;
        return string.Compare(a, b, StringComparison.Ordinal);
    }


    /// <summary>
    /// §5.7.2 simple content merging: merge adjacent text nodes (TextNodeItem, XdmText),
    /// remove zero-length text nodes, then join remaining items with separator.
    /// Plain strings (from xsl:sequence of atomic values) are NOT merged with text nodes.
    /// </summary>
    private static string MergeSimpleContent(List<object?> items, string separator)
    {
        // Step 1: Merge adjacent text nodes (TextNodeItem, XdmText, XdmNode text)
        var merged = new List<object?>();
        System.Text.StringBuilder? textRun = null;
        foreach (var item in items)
        {
            if (item is Xdm.TextNodeItem tni)
            {
                textRun ??= new System.Text.StringBuilder();
                textRun.Append(tni.Value);
            }
            else if (item is Xdm.Nodes.XdmText xt)
            {
                textRun ??= new System.Text.StringBuilder();
                textRun.Append(xt.Value);
            }
            else
            {
                if (textRun != null)
                {
                    // Step 2: Remove zero-length merged text nodes
                    if (textRun.Length > 0)
                        merged.Add(textRun.ToString());
                    textRun = null;
                }
                if (item != null)
                    merged.Add(item);
            }
        }
        if (textRun != null && textRun.Length > 0)
            merged.Add(textRun.ToString());

        // Step 3: Convert to strings and join with separator
        return string.Join(separator, merged.Select(StringValueOf));
    }


    public override async ValueTask PerformSortAsync(XsltPerformSort instruction)
    {
        IEnumerable<object> items;

        if (instruction.Select != null)
        {
            var result = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
            // An XDM array/map is a single item to sort, not flattened.
            items = SelectResultItems(result);
        }
        else if (instruction.Content != null)
        {
            // Execute the content body and collect items.
            // Text from xsl:value-of goes to _sequenceAccumulator as individual items (for sorting).
            // Literal result elements serialize to the output buffer as XML strings
            // and are parsed back into individual XDM nodes for sorting.
            var savedAccumulator = _sequenceAccumulator;
            _sequenceAccumulator = new List<object?>();
            var savedCollectText = _collectTextAsSequenceItems;
            var savedElementDepth = _serializingElementDepth;
            _collectTextAsSequenceItems = true;
            _serializingElementDepth = 0; // Reset so perform-sort content starts at "top level"

            var savedScope = new XsltTransformEngine.ScopedOutputBuffer(_output);
            var savedLogicalStart = _outputLogicalStart;
            _outputLogicalStart = _output.Length;
            await instruction.Content.ExecuteAsync(this).ConfigureAwait(false);
            var xmlContent = savedScope.GetWritten();
            savedScope.Dispose();
            _outputLogicalStart = savedLogicalStart;
            _collectTextAsSequenceItems = savedCollectText;
            _serializingElementDepth = savedElementDepth;

            var collected = new List<object>();

            // Add any xsl:sequence items first
            if (_sequenceAccumulator.Count > 0)
            {
                foreach (var item in _sequenceAccumulator)
                    if (item != null)
                        collected.Add(item);
            }

            // Parse serialized literal result elements into XDM nodes
            if (!string.IsNullOrEmpty(xmlContent) && _nodeStore != null)
            {
                try
                {
                    // Stream-parse rather than allocating a full XmlDocument.
                    var settings = new System.Xml.XmlReaderSettings
                    {
                        DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                        IgnoreWhitespace = false,
                        IgnoreComments = false,
                        IgnoreProcessingInstructions = false,
                    };
                    using var stringReader = new System.IO.StringReader($"<_sort_root_>{xmlContent}</_sort_root_>");
                    using var reader = System.Xml.XmlReader.Create(stringReader, settings);
                    var parsedChildren = new List<object?>();
                    ReadAsBodyChunkChildren(reader, parsedChildren);
                    foreach (var child in parsedChildren)
                        if (child != null)
                            collected.Add(child);
                }
                catch (System.Xml.XmlException)
                {
                    // If parsing fails, just output as-is
                }
            }

            _sequenceAccumulator = savedAccumulator;
            items = collected;
        }
        else
        {
            items = [];
        }

        var sorted = await SortNodesAsync(items, instruction.Sorts).ConfigureAwait(false);

        // If sequence accumulator is active (e.g., inside a function body or xsl:value-of),
        // add items individually to preserve them as a sequence for separator handling
        if (_sequenceAccumulator != null)
        {
            foreach (var item in sorted)
                AppendToSeqAccumulator(item);
        }
        else
        {
            foreach (var item in sorted)
            {
                if (item is string s)
                {
                    // Sorted string items are atomic values — need §5.7.2 space separator
                    if (_lastResultWasAtomic && _attributeContentDepth == 0)
                        WriteText(_itemSeparatorOverride ?? " ", false);
                    WriteText(s, false);
                    _lastResultWasAtomic = true;
                }
                else
                {
                    SerializeResult(item);
                }
            }
        }
    }


    public override async ValueTask MergeAsync(XsltMerge instruction)
    {
        // Fast path: all sources are streamable + use for-each-source + select is a
        // single child-axis step. Run the K-way merge over per-URI XmlReader pulls
        // so we never materialize the full input documents.
        if (XsltMergeStreaming.CanStreamMerge(instruction))
        {
            await MergeAsyncStreaming(instruction).ConfigureAwait(false);
            return;
        }

        // Key settings first: sub-sequence order checks below need them. Each key compares with
        // its own collation, or the default collation in scope (W3C merge-074: the template's
        // default-collation governs a key with no collation attribute).
        var (keyOrders, keyDataTypes, keyCollations) = instruction.Sources.Count > 0
            ? await ResolveMergeKeySettingsAsync(instruction.Sources[0].MergeKeys).ConfigureAwait(false)
            : (new List<string>(), new List<string>(), new List<string?>());
        // XTDE2210: every source must agree on each key's order, data-type and collation.
        for (var si = 1; si < instruction.Sources.Count; si++)
        {
            var (orders2, dataTypes2, collations2) = await ResolveMergeKeySettingsAsync(instruction.Sources[si].MergeKeys).ConfigureAwait(false);
            for (var ki = 0; ki < orders2.Count && ki < keyOrders.Count; ki++)
            {
                if (orders2[ki] != keyOrders[ki])
                    throw Error($"XTDE2210: Merge key order differs across sources: '{keyOrders[ki]}' vs '{orders2[ki]}'");
                if (dataTypes2[ki] != keyDataTypes[ki])
                    throw Error($"XTDE2210: Merge key data-type differs across sources: '{keyDataTypes[ki]}' vs '{dataTypes2[ki]}'");
                if (!string.Equals(collations2[ki], keyCollations[ki], StringComparison.Ordinal))
                    throw Error($"XTDE2210: Merge key collation differs across sources: '{keyCollations[ki]}' vs '{collations2[ki]}'");
            }
        }

        // Step 1: Collect items from each merge source
        var sourceSequences = new List<List<object>>();

        foreach (var source in instruction.Sources)
        {
            var allItems = new List<object>();

            if (source.ForEachItem != null)
            {
                // Evaluate for-each-item to get a sequence of items
                var forEachResult = await EvaluateAsync(source.ForEachItem).ConfigureAwait(false);
                var forEachItems = SelectResultItems(forEachResult).ToList();

                foreach (var item in forEachItems)
                {
                    PushContextItem(item, 1, 1);
                    List<object> subSequence;
                    try
                    {
                        var selected = await EvaluateAsync(source.Select).ConfigureAwait(false);
                        subSequence = new List<object>();
                        AddToList(subSequence, selected);
                    }
                    finally
                    {
                        PopContextItem();
                    }
                    allItems.AddRange(subSequence);
                }
            }
            else if (source.ForEachSource != null)
            {
                // Evaluate for-each-source to get URIs, then load each as a document
                var forEachResult = await EvaluateAsync(source.ForEachSource).ConfigureAwait(false);
                var forEachItems = SelectResultItems(forEachResult).ToList();

                foreach (var item in forEachItems)
                {
                    // for-each-source is xs:string*, so each item is a URI to load via doc(). The
                    // function conversion rules admit xs:anyURI (promoted) and xs:untypedAtomic
                    // (cast) as well as xs:string: uri-collection() returns xs:anyURI, and an
                    // xs:anyURI item used to become the context item itself, so
                    // select="events/event" failed "axis step ... context item is not a node"
                    // (W3C merge-039/098). Any other atomic type is XPTY0004 (merge-043:
                    // for-each-source="1 to 5"). Nodes keep their existing handling.
                    object contextItem = item;
                    var uri = item switch
                    {
                        string str => str,
                        Xdm.XsAnyUri anyUri => anyUri.Value,
                        Xdm.XsUntypedAtomic ua => ua.Value,
                        null or XdmNode => null,
                        _ => throw Error($"XPTY0004: for-each-source must yield URIs (xs:string*); got {item.GetType().Name}"),
                    };
                    if (uri != null)
                    {
                        var doc = _policyResolver?.ResolveDocument(uri) ?? _documentResolver.ResolveDocument(uri);
                        if (doc == null)
                            continue;
                        contextItem = doc;

                        // Compute specified accumulators on the merge source document
                        if (source.UseAccumulators.Count > 0 && _nodeStore != null)
                        {
                            var accumulators = new List<XsltAccumulator>();
                            foreach (var accName in source.UseAccumulators)
                            {
                                if (_stylesheet.Accumulators.TryGetValue(accName, out var acc))
                                    accumulators.Add(acc);
                            }
                            if (accumulators.Count > 0)
                                await PreComputeAccumulatorsAsync(doc, accumulators, _nodeStore).ConfigureAwait(false);
                        }
                    }

                    PushContextItem(contextItem, 1, 1);
                    List<object> subSequence;
                    try
                    {
                        var selected = await EvaluateAsync(source.Select).ConfigureAwait(false);
                        subSequence = new List<object>();
                        AddToList(subSequence, selected);
                    }
                    finally
                    {
                        PopContextItem();
                    }
                    allItems.AddRange(subSequence);
                }
            }
            else
            {
                // No for-each-item: evaluate select in current context
                var selected = await EvaluateAsync(source.Select).ConfigureAwait(false);
                AddToList(allItems, selected);
            }

            // Step 2: Sort items by merge keys if needed
            // sort-before-merge="yes" explicitly requests sorting.
            // Also sort when for-each-item/for-each-source is used: per spec these create
            // separate sub-sources in the K-way merge, but we concatenate them into one sequence,
            // so sorting is needed to produce correct merge results.
            var needsSort = source.SortBeforeMerge ||
                            source.ForEachItem != null || source.ForEachSource != null;
            if (needsSort && source.MergeKeys.Count > 0)
            {
                allItems = await SortByMergeKeysAsync(allItems, source.MergeKeys).ConfigureAwait(false);
            }

            sourceSequences.Add(allItems);
        }

        // Step 3: K-way merge
        // Pre-compute merge key values for all items in all sources
        var mergeKeysTemplate = instruction.Sources.Count > 0 ? instruction.Sources[0].MergeKeys : new List<XsltMergeKey>();

        // Compute keys for each item in each source — keep raw typed values
        var sourceKeys = new List<List<List<object?>>>();
        for (var si = 0; si < sourceSequences.Count; si++)
        {
            var keysForSource = new List<List<object?>>();
            var currentSourceKeys = si < instruction.Sources.Count ? instruction.Sources[si].MergeKeys : mergeKeysTemplate;
            foreach (var item in sourceSequences[si])
                keysForSource.Add(await ComputeMergeKeysForItemAsync(item, currentSourceKeys).ConfigureAwait(false));
            // XTDE2220: Verify input is correctly sorted (unless sort-before-merge)
            // Only check when no for-each-item/for-each-source — those create independent sub-sequences
            var currentSource = si < instruction.Sources.Count ? instruction.Sources[si] : null;
            if (currentSource != null && !currentSource.SortBeforeMerge &&
                currentSource.ForEachItem == null && currentSource.ForEachSource == null &&
                keysForSource.Count > 1)
            {
                for (var ki = 1; ki < keysForSource.Count; ki++)
                {
                    var cmp = CompareMergeKeys(keysForSource[ki - 1], keysForSource[ki], keyOrders, keyDataTypes, keyCollations);
                    if (cmp > 0)
                        throw Error($"XTDE2220: Merge source '{currentSource.Name ?? ("source " + si)}' is not correctly sorted at item {ki + 1}");
                }
            }

            sourceKeys.Add(keysForSource);
        }

        // K-way merge using indices
        var indices = new int[sourceSequences.Count];
        var mergedGroups = new List<(List<object?> Key, List<(object Item, int SourceIndex)> Items)>();

        while (true)
        {
            // Find the source with the smallest current key
            int bestSource = -1;
            List<object?>? bestKey = null;

            for (var si = 0; si < sourceSequences.Count; si++)
            {
                if (indices[si] >= sourceSequences[si].Count)
                    continue;
                var currentKey = sourceKeys[si][indices[si]];

                if (bestKey == null || CompareMergeKeys(currentKey, bestKey, keyOrders, keyDataTypes, keyCollations) < 0)
                {
                    bestSource = si;
                    bestKey = currentKey;
                }
            }

            if (bestSource == -1)
                break; // All sources exhausted

            // Collect all items with equal keys across all sources, tracking source index
            var group = new List<(object Item, int SourceIndex)>();
            for (var si = 0; si < sourceSequences.Count; si++)
            {
                while (indices[si] < sourceSequences[si].Count &&
                       CompareMergeKeys(sourceKeys[si][indices[si]], bestKey!, keyOrders, keyDataTypes, keyCollations) == 0)
                {
                    group.Add((sourceSequences[si][indices[si]], si));
                    indices[si]++;
                }
            }

            mergedGroups.Add((bestKey!, group));
        }

        // Build source name-to-index map for named merge groups
        var sourceNames = new Dictionary<string, int>();
        for (var si = 0; si < instruction.Sources.Count; si++)
        {
            if (instruction.Sources[si].Name != null)
                sourceNames[instruction.Sources[si].Name!] = si;
        }

        // Step 4: Execute merge-action for each group
        var mergeGroupCount = mergedGroups.Count;
        var mergeGroupPosition = 0;
        foreach (var (key, taggedItems) in mergedGroups)
        {
            mergeGroupPosition++;
            var items = taggedItems.Select(t => t.Item).ToList();
            PushScope();
            SetVariable(new QName(NamespaceId.None, "current-merge-group"), items);
            // Set merge key — stringify for current-merge-key() function
            var mergeKeyValue = key.Count == 1
                ? (object)StringValueOf(key[0])
                : key.Select(k => StringValueOf(k)).ToArray();
            SetVariable(new QName(NamespaceId.None, "current-merge-key"), mergeKeyValue);

            // Store per-source groups for current-merge-group('name')
            foreach (var (name, idx) in sourceNames)
            {
                var sourceItems = taggedItems.Where(t => t.SourceIndex == idx).Select(t => t.Item).ToList();
                SetVariable(new QName(NamespaceId.None, $"current-merge-group:{name}"), sourceItems);
            }

            // Set context item to the first item in the group
            PushContextItem(items[0], mergeGroupPosition, mergeGroupCount);
            try
            {
                await instruction.Action.ExecuteAsync(this).ConfigureAwait(false);
            }
            finally
            {
                PopContextItem();
                PopScope();
            }
        }
    }


    private async Task<List<object>> SortByMergeKeysAsync(List<object> items, List<XsltMergeKey> mergeKeys)
    {
        var (orders, dataTypes, collations) = await ResolveMergeKeySettingsAsync(mergeKeys).ConfigureAwait(false);

        // Compute keys for each item — keep raw typed values
        var keyed = new List<(object Item, List<object?> Keys)>();
        foreach (var item in items)
        {
            PushContextItem(item, 1, 1);
            try
            {
                var itemKeys = new List<object?>();
                _temporaryOutputDepth++;
                try
                {
                    foreach (var mk in mergeKeys)
                    {
                        object? keyVal;
                        if (mk.Select != null)
                            keyVal = await EvaluateAsync(mk.Select).ConfigureAwait(false);
                        else if (mk.Content != null)
                        {
                            BeginSequenceCollection();
                            await mk.Content.ExecuteAsync(this).ConfigureAwait(false);
                            var collected = EndSequenceCollection();
                            keyVal = collected.Count == 1 ? collected[0] : collected.Count == 0 ? null : collected;
                        }
                        else
                            keyVal = null;
                        itemKeys.Add(keyVal);
                    }
                }
                finally { _temporaryOutputDepth--; }
                keyed.Add((item, itemKeys));
            }
            finally
            {
                PopContextItem();
            }
        }

        keyed.Sort((a, b) => CompareMergeKeys(a.Keys, b.Keys, orders, dataTypes, collations));

        return keyed.Select(k => k.Item).ToList();
    }


    private static int CompareMergeKeys(List<object?> a, List<object?> b, List<string> orders, List<string> dataTypes,
        IReadOnlyList<string?> collations)
    {
        for (var i = 0; i < a.Count && i < b.Count && i < orders.Count; i++)
        {
            int cmp;
            var dataType = i < dataTypes.Count ? dataTypes[i] : "text";
            cmp = CompareKeyValues(a[i], b[i], dataType, i < collations.Count ? collations[i] : null);
            if (orders[i] == "descending")
                cmp = -cmp;
            if (cmp != 0)
                return cmp;
        }
        return 0;
    }


    private static int CompareKeyValues(object? a, object? b, string dataType, string? collation)
    {
        if (a is null && b is null)
            return 0;
        if (a is null)
            return -1;
        if (b is null)
            return 1;

        // For data-type="number", always compare as numeric
        if (dataType == "number")
        {
            var aNum = ToDouble(a);
            var bNum = ToDouble(b);
            if (!double.IsNaN(aNum) && !double.IsNaN(bNum))
                return aNum.CompareTo(bNum);
            if (!double.IsNaN(aNum))
                return -1;
            if (!double.IsNaN(bNum))
                return 1;
            return 0; // both NaN — maintain document order
        }

        // Use typed comparison based on actual value types
        if (a is long la && b is long lb)
            return la.CompareTo(lb);
        if (a is int ia && b is int ib)
            return ia.CompareTo(ib);
        if (IsMergeKeyNumeric(a) && IsMergeKeyNumeric(b))
            return ToDouble(a).CompareTo(ToDouble(b));
        if (a is XsDateTime xdta && b is XsDateTime xdtb)
            return xdta.CompareTo(xdtb);
        if (a is XsDate xda && b is XsDate xdb)
            return xda.CompareTo(xdb);
        if (a is XsTime xta && b is XsTime xtb)
            return xta.CompareTo(xtb);
        if (a is DateTimeOffset dtoa && b is DateTimeOffset dtob)
            return dtoa.CompareTo(dtob);
        if (a is decimal da && b is decimal db)
            return da.CompareTo(db);

        // XTTE2230: Check for incomparable types before falling back to string
        if (IsMergeKeyNumeric(a) != IsMergeKeyNumeric(b) && a is not string && b is not string)
            throw new XsltException($"XTTE2230: Merge key values are not comparable: {a.GetType().Name} vs {b.GetType().Name}");
        if ((a is TimeSpan or DayTimeDuration or YearMonthDuration) && b is not TimeSpan and not DayTimeDuration and not YearMonthDuration)
            throw new XsltException($"XTTE2230: Merge key values are not comparable: {a.GetType().Name} vs {b.GetType().Name}");
        if ((b is TimeSpan or DayTimeDuration or YearMonthDuration) && a is not TimeSpan and not DayTimeDuration and not YearMonthDuration)
            throw new XsltException($"XTTE2230: Merge key values are not comparable: {a.GetType().Name} vs {b.GetType().Name}");
        // DateTime/Date/Time vs non-DateTime types
        if ((a is XsDateTime or XsDate or XsTime or DateTimeOffset) && b is not XsDateTime and not XsDate and not XsTime and not DateTimeOffset)
            throw new XsltException($"XTTE2230: Merge key values are not comparable: {a.GetType().Name} vs {b.GetType().Name}");
        if ((b is XsDateTime or XsDate or XsTime or DateTimeOffset) && a is not XsDateTime and not XsDate and not XsTime and not DateTimeOffset)
            throw new XsltException($"XTTE2230: Merge key values are not comparable: {a.GetType().Name} vs {b.GetType().Name}");

        // Fall back to string comparison — atomize XDM nodes so attribute/element
        // selects produce string values (not "PhoenixmlDb.Xdm.Nodes.XdmAttribute").
        var aStr = AtomizeMergeKeyToString(a);
        var bStr = AtomizeMergeKeyToString(b);
        // The key's collation. Comparing with codepoints whatever the collation said merged keys
        // a collation calls equal as different, and could not see input that was out of order
        // under the collation it named (W3C merge-072/074, XTDE2220). Null is codepoint.
        return PhoenixmlDb.XQuery.Functions.CollationHelper.CompareWithCollation(aStr, bStr, collation);
    }


    /// <summary>
    /// One item's merge-key values (XTDE1480: evaluated in temporary output state; XTTE1020: each
    /// key a single atomic value). Shared by the merge and its input-order check.
    /// </summary>
    private async Task<List<object?>> ComputeMergeKeysForItemAsync(object item, List<XsltMergeKey> currentSourceKeys)
    {
        PushContextItem(item, 1, 1);
        try
        {
            var itemKeys = new List<object?>();
            // XTDE1480: Merge key evaluation is in temporary output state
            _temporaryOutputDepth++;
            try
            {
                foreach (var mk in currentSourceKeys)
                {
                    object? keyVal;
                    if (mk.Select != null)
                    {
                        keyVal = await EvaluateAsync(mk.Select).ConfigureAwait(false);
                        // XTTE1020: Merge key must be a singleton
                        if (keyVal is object?[] keyArr)
                        {
                            if (keyArr.Length > 1)
                                throw Error("XTTE1020: The value of a merge key must be a single atomic value");
                            keyVal = keyArr.Length == 1 ? keyArr[0] : null;
                        }
                        else if (keyVal is IEnumerable<object> keySeq && keyVal is not string && keyVal is not Xdm.Nodes.XdmNode)
                        {
                            var keyList = keySeq.ToList();
                            if (keyList.Count > 1)
                                throw Error("XTTE1020: The value of a merge key must be a single atomic value");
                            keyVal = keyList.Count == 1 ? keyList[0] : null;
                        }
                    }
                    else if (mk.Content != null)
                    {
                        BeginSequenceCollection();
                        await mk.Content.ExecuteAsync(this).ConfigureAwait(false);
                        var items = EndSequenceCollection();
                        keyVal = items.Count == 1 ? items[0] : items.Count == 0 ? null : items;
                    }
                    else
                        keyVal = null;
                    itemKeys.Add(keyVal);
                }
            }
            finally { _temporaryOutputDepth--; }
            return itemKeys;
        }
        finally
        {
            PopContextItem();
        }
    }


    /// <summary>
    /// A merge-key list's order, data-type and collation, each evaluated from its AVT. A key with
    /// no collation attribute uses the default collation in scope.
    /// </summary>
    private async Task<(List<string> Orders, List<string> DataTypes, List<string?> Collations)> ResolveMergeKeySettingsAsync(
        List<XsltMergeKey> mergeKeys)
    {
        var orders = new List<string>(mergeKeys.Count);
        var dataTypes = new List<string>(mergeKeys.Count);
        var collations = new List<string?>(mergeKeys.Count);
        foreach (var mk in mergeKeys)
        {
            var order = mk.Order != null ? await EvaluateAvtAsync(mk.Order).ConfigureAwait(false) : "ascending";
            if (order != "ascending" && order != "descending")
                throw Error($"XTDE2210: Invalid value '{order}' for order attribute on xsl:merge-key (must be 'ascending' or 'descending')");
            orders.Add(order);
            var dataType = mk.DataType != null ? await EvaluateAvtAsync(mk.DataType).ConfigureAwait(false) : "text";
            if (dataType != "text" && dataType != "number")
                throw Error($"XTDE2210: Invalid value '{dataType}' for data-type attribute on xsl:merge-key (must be 'text' or 'number')");
            dataTypes.Add(dataType);
            collations.Add(await ResolveMergeKeyCollationAsync(mk).ConfigureAwait(false));
        }
        return (orders, dataTypes, collations);
    }


    /// <summary>
    /// The collation a merge key compares with: its collation attribute; else, when it has lang,
    /// the UCA collation for that language (case-order giving caseFirst), as xsl:sort does; else
    /// the default collation in scope.
    /// </summary>
    /// <remarks>
    /// lang has to outrank the default. W3C merge-070 and friends merge Swedish-sorted city lists
    /// with lang="sv" and no collation attribute; under the default (codepoint) collation that
    /// input is out of order — "Aby" after "AElmhult" — and would be XTDE2220.
    /// </remarks>
    private async Task<string?> ResolveMergeKeyCollationAsync(XsltMergeKey mk)
    {
        if (mk.Collation != null)
            return await EvaluateAvtAsync(mk.Collation).ConfigureAwait(false);
        if (mk.Lang != null && await EvaluateAvtAsync(mk.Lang).ConfigureAwait(false) is { Length: > 0 } lang)
        {
            var uca = "http://www.w3.org/2013/collation/UCA?lang=" + lang;
            if (mk.CaseOrder != null)
            {
                var caseOrder = await EvaluateAvtAsync(mk.CaseOrder).ConfigureAwait(false);
                if (caseOrder == "upper-first") uca += ";caseFirst=upper";
                else if (caseOrder == "lower-first") uca += ";caseFirst=lower";
            }
            return uca;
        }
        return DefaultCollation;
    }


    private async Task<IEnumerable<object>> SortNodesAsync(IEnumerable<object> nodes, List<XsltSort> sorts)
    {
        if (sorts.Count == 0)
            return nodes;

        var list = nodes.ToList();

        // Resolve sort order directions, data types, lang, case-order, and collation upfront
        var descending = new bool[sorts.Count];
        var isNumeric = new bool[sorts.Count];
        var caseOrder = new string?[sorts.Count];
        var langs = new string?[sorts.Count];
        var collations = new string?[sorts.Count];
        for (var i = 0; i < sorts.Count; i++)
        {
            if (sorts[i].Order != null)
            {
                var orderValue = await EvaluateAvtAsync(sorts[i].Order!).ConfigureAwait(false);
                descending[i] = string.Equals(orderValue, "descending", StringComparison.OrdinalIgnoreCase);
            }
            if (sorts[i].DataType != null)
            {
                var dtValue = await EvaluateAvtAsync(sorts[i].DataType!).ConfigureAwait(false);
                isNumeric[i] = string.Equals(dtValue, "number", StringComparison.OrdinalIgnoreCase);
            }
            if (sorts[i].CaseOrder != null)
            {
                caseOrder[i] = await EvaluateAvtAsync(sorts[i].CaseOrder!).ConfigureAwait(false);
            }
            if (sorts[i].Lang != null)
            {
                langs[i] = await EvaluateAvtAsync(sorts[i].Lang!).ConfigureAwait(false);
                // XTDE0030: lang must be a valid language tag
                if (!string.IsNullOrEmpty(langs[i]) && !IsValidLanguageTag(langs[i]!))
                    throw Error($"XTDE0030: Invalid language tag '{langs[i]}' in xsl:sort");
            }
            if (sorts[i].Collation != null)
            {
                collations[i] = await EvaluateAvtAsync(sorts[i].Collation!).ConfigureAwait(false);
                // Validate collation URI — only codepoint, html-ascii, and Unicode collation are supported
                if (!string.IsNullOrEmpty(collations[i])
                    && !string.Equals(collations[i], "http://www.w3.org/2005/xpath-functions/collation/codepoint", StringComparison.Ordinal)
                    && !string.Equals(collations[i], "http://www.w3.org/2005/xpath-functions/collation/html-ascii-case-insensitive", StringComparison.Ordinal)
                    && !string.Equals(collations[i], "http://www.w3.org/2013/collation/UCA", StringComparison.Ordinal)
                    && !collations[i]!.StartsWith("http://www.w3.org/2013/collation/UCA?", StringComparison.Ordinal))
                    throw Error($"XTDE1035: Unknown collation URI '{collations[i]}'");
            }
        }

        // Build sort keys - preserve type information for XSLT 2.0+ typed comparison
        var keyed = new List<(object node, object?[] keys, int index)>();
        var idx = 0;
        foreach (var node in list)
        {
            var keys = new object?[sorts.Count];
            PushContextItem(node, idx + 1, list.Count);
            // Per XSLT 3.0 §20.4.2: "Within a sort key, the current function
            // returns the same node that is the context node."
            PushCurrentItem(node);

            try
            {
                for (var i = 0; i < sorts.Count; i++)
                {
                    var sort = sorts[i];
                    object? raw;
                    if (sort.Select != null)
                    {
                        raw = await EvaluateAsync(sort.Select).ConfigureAwait(false);
                    }
                    else if (sort.Content != null)
                    {
                        raw = await EvaluateSequenceConstructorAsync(sort.Content).ConfigureAwait(false);
                    }
                    else
                    {
                        raw = node;
                    }
                    // XTTE1020: Sort key must be a single atomic value in XSLT 2.0+
                    // In backwards-compatible mode, use first item of sequence
                    if (raw is object[] multiKey && multiKey.Length > 1)
                    {
                        if (IsBackwardsCompatible)
                            raw = multiKey[0];
                        else
                            throw Error($"XTTE1020: Sort key value is a sequence of {multiKey.Length} items; a single value is required");
                    }
                    keys[i] = raw;
                }
            }
            finally
            {
                PopCurrentItem();
                PopContextItem();
            }

            keyed.Add((node, keys, idx++));
        }

        // Stable sort: use index as tiebreaker to preserve document order
        keyed.Sort((a, b) =>
        {
            for (var i = 0; i < sorts.Count; i++)
            {
                var cmp = CompareSortKeys(a.keys[i], b.keys[i], isNumeric[i], langs[i], caseOrder[i], collations[i]);
                if (cmp != 0)
                    return descending[i] ? -cmp : cmp;
            }
            return a.index.CompareTo(b.index);
        });

        return keyed.Select(k => k.node);
    }


    /// <summary>
    /// Compares two sort key values with proper type-aware semantics.
    /// In XSLT 2.0+, numeric values compare numerically regardless of data-type attribute.
    /// </summary>
#pragma warning disable CA1309 // Locale-aware comparison is intentional for xsl:sort lang/case-order support
    private static int CompareSortKeys(object? a, object? b, bool forceNumeric,
        string? lang = null, string? caseOrder = null, string? collation = null)
    {
        // Extract numeric values from sort keys
        var aNum = ToSortDouble(a, forceNumeric);
        var bNum = ToSortDouble(b, forceNumeric);

        if (aNum.HasValue && bNum.HasValue)
        {
            // Both are numeric — compare as doubles
            // Per XSLT spec, NaN is less than any other numeric value
            var av = aNum.Value;
            var bv = bNum.Value;
            if (double.IsNaN(av) && double.IsNaN(bv))
                return 0;
            if (double.IsNaN(av))
                return -1;
            if (double.IsNaN(bv))
                return 1;
            return av.CompareTo(bv);
        }

        // XTDE1030: Detect type mismatch — one value is typed numeric, the other is not
        if (aNum.HasValue != bNum.HasValue && a is not null && b is not null)
            throw new XsltException("XTDE1030: Cannot compare sort keys of incompatible types (numeric vs non-numeric)");

        // XTDE1030: xs:duration values are not totally ordered and cannot be sorted
        if (a is XsDuration || b is XsDuration)
            throw new XsltException("XTDE1030: Cannot compare sort keys of type xs:duration (durations are not totally ordered)");

        // Detect type mismatch: comparing typed non-numeric values with strings is XTDE1030
        var aIsTyped = a is DateOnly or DateTimeOffset or TimeOnly or TimeSpan or XsDateTime or XsDate or XsTime or YearMonthDuration;
        var bIsTyped = b is DateOnly or DateTimeOffset or TimeOnly or TimeSpan or XsDateTime or XsDate or XsTime or YearMonthDuration;
        if (aIsTyped != bIsTyped && a is not null && b is not null)
            throw new XsltException("XTDE1030: Cannot compare sort keys of incompatible types");

        // Typed date/time/duration comparison — use CompareTo instead of string fallback
        if (aIsTyped && bIsTyped)
            return CompareValues(a!, b!);

        // Fall back to string comparison
        var aStr = a switch
        {
            null => "",
            string s => s,
            _ => StringValueOf(a)
        };
        var bStr = b switch
        {
            null => "",
            string s => s,
            _ => StringValueOf(b)
        };

        // Codepoint collation: use Ordinal (Unicode codepoint order)
        if (string.Equals(collation, "http://www.w3.org/2005/xpath-functions/collation/codepoint", StringComparison.Ordinal))
            return string.Compare(aStr, bStr, StringComparison.Ordinal);

        // HTML ASCII case-insensitive collation
        if (string.Equals(collation, "http://www.w3.org/2005/xpath-functions/collation/html-ascii-case-insensitive", StringComparison.Ordinal))
            return string.Compare(aStr, bStr, StringComparison.OrdinalIgnoreCase);

        // UCA collation (with optional parameters like strength=secondary for case-insensitive)
        if (collation != null && collation.StartsWith("http://www.w3.org/2013/collation/UCA", StringComparison.Ordinal))
            return string.Compare(aStr, bStr, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);

        // Use locale-aware comparison (default behavior per XSLT spec)
        CultureInfo culture;
        try
        {
            culture = !string.IsNullOrEmpty(lang) ? CultureInfo.GetCultureInfo(lang) : CultureInfo.InvariantCulture;
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.InvariantCulture;
        }

        var options = CompareOptions.None;
        if (string.Equals(caseOrder, "upper-first", StringComparison.OrdinalIgnoreCase))
        {
            // Upper-case letters sort before lower-case
            var cmp = string.Compare(aStr, bStr, culture, CompareOptions.IgnoreCase);
            if (cmp != 0)
                return cmp;
            // Tie-break: Ordinal puts uppercase first (A=0x41 < a=0x61)
            return string.Compare(aStr, bStr, StringComparison.Ordinal);
        }
        if (string.Equals(caseOrder, "lower-first", StringComparison.OrdinalIgnoreCase))
        {
            // Lower-case letters sort before upper-case
            var cmp = string.Compare(aStr, bStr, culture, CompareOptions.IgnoreCase);
            if (cmp != 0)
                return cmp;
            // Tie-break: negate Ordinal to put lowercase first
            return -string.Compare(aStr, bStr, StringComparison.Ordinal);
        }

        // With no collation and no lang, xsl:sort uses the default collation, whose default is the
        // Unicode codepoint collation (XPath F&O §5.3.4) — uppercase (A=0x41) sorts before
        // lowercase (a=0x61), not the locale-aware, effectively case-insensitive order. A lang or
        // an (unknown) explicit collation keeps the locale-aware fallback.
        if (string.IsNullOrEmpty(lang) && string.IsNullOrEmpty(collation))
            return string.Compare(aStr, bStr, StringComparison.Ordinal);

        return string.Compare(aStr, bStr, culture, options);
    }


    private static int CompareValues(object? a, object? b)
    {
        if (a == null && b == null)
            return 0;
        if (a == null)
            return -1;
        if (b == null)
            return 1;

        return (a, b) switch
        {
            (string sa, string sb) => string.Compare(sa, sb, StringComparison.Ordinal),
            // Date/time/duration types implement IComparable<T> but not IComparable
            (Xdm.XsDateTime ldt, Xdm.XsDateTime rdt) => ldt.CompareTo(rdt),
            (Xdm.XsDate ld, Xdm.XsDate rd) => ld.CompareTo(rd),
            (Xdm.XsTime lt, Xdm.XsTime rt) => lt.CompareTo(rt),
            (Xdm.YearMonthDuration lym, Xdm.YearMonthDuration rym) => lym.CompareTo(rym),
            (Xdm.XsDuration lxd, Xdm.XsDuration rxd) => lxd.CompareTo(rxd),
            _ when IsNumeric(a) && IsNumeric(b) => CompareNumeric(a, b),
            (IComparable ca, _) => ca.CompareTo(b),
            _ => 0
        };
    }


    private static int CompareNumeric(object a, object b)
    {
        var da = a is System.Numerics.BigInteger abi ? (double)abi : Convert.ToDouble(a, CultureInfo.InvariantCulture);
        var db = b is System.Numerics.BigInteger bbi ? (double)bbi : Convert.ToDouble(b, CultureInfo.InvariantCulture);
        return da.CompareTo(db);
    }


    /// <summary>
    /// Compares two composite keys for value equality using XQuery deep-equal semantics.
    /// Numeric values are compared by value (5 == 5.0 == 5m).
    /// </summary>
    /// <summary>
    /// Generates a canonical string key for group-by/group-adjacent comparison.
    /// Per XSLT 3.0 §15.3, grouping uses the eq operator for comparison.
    /// QNames compare by namespace URI + local name (ignoring prefix).
    /// </summary>
    private string GroupingKeyString(object? key)
    {
        if (key is PhoenixmlDb.Core.QName qn)
        {
            // Use expanded name for QName comparison — prefixes are irrelevant
            var ns = qn.ResolvedNamespace ?? _nodeStore?.GetNamespaceUri(qn.Namespace) ?? "";
            return string.IsNullOrEmpty(ns) ? qn.LocalName : $"Q{{{ns}}}{qn.LocalName}";
        }
        // dateTime grouping keys compare by their INSTANT with the implicit
        // timezone applied to timezone-less values (XPath F&O §10.4), so two
        // equal instants written in different timezones (13:00+02:00 vs
        // 11:00Z) fall into the SAME group. The "DT:" prefix keeps a dateTime
        // key distinct from any string/numeric key of the same lexical form —
        // cross-type keys are unequal, not an error (XSLT 3.0 §18.2).
        if (key is XsDateTime xdt)
        {
            var instant = xdt.HasTimezone
                ? xdt.Value
                : new DateTimeOffset(xdt.Value.DateTime, DateTimeOffset.Now.Offset);
            return "DT:" + xdt.EffectiveYear.ToString(CultureInfo.InvariantCulture)
                + ":" + instant.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        if (key is DateTimeOffset dto)
            return "DT:" + dto.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture);
        return StringValueOf(key);
    }

}
