using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Aggregation strategy for a stream watcher.
/// </summary>
internal enum WatcherAggregation
{
    Count,
    Sum,
    Max,
    Min,
    Avg,
    StringJoin,
    Snapshot,
    Sequence,
    /// <summary>
    /// Returns only the first matched item as a scalar (used for fn:head(path) patterns).
    /// </summary>
    Head
}

/// <summary>
/// Matches a path pattern against the streaming element stack.
/// </summary>
internal sealed class StreamPathMatcher
{
    private readonly string[] _steps;

    public StreamPathMatcher(string pathPattern)
    {
        // Parse "transactions/transaction" into ["transactions", "transaction"]
        // or "transactions/transaction/@value" into ["transactions", "transaction", "@value"]
        _steps = pathPattern.Split('/');
    }

    /// <summary>
    /// Checks if the current element stack matches this pattern.
    /// </summary>
    /// <param name="ancestorStack">Current element name stack (outermost first).</param>
    /// <param name="currentName">The current element's local name.</param>
    public bool Matches(IReadOnlyList<string> ancestorStack, string currentName)
    {
        // Build full path: ancestors + current
        // Match against steps from the end
        if (_steps.Length == 0) return false;

        var lastStep = _steps[^1];

        // Attribute step — matched separately via MatchesAttribute
        if (lastStep.StartsWith('@')) return false;

        if (lastStep != currentName && lastStep != "*") return false;

        if (_steps.Length == 1) return true;

        return MatchAncestors(_steps, _steps.Length - 2, ancestorStack, ancestorStack.Count - 1);
    }

    /// <summary>
    /// Walks the step list backward against the ancestor stack. A "**" step is a
    /// descendant-axis marker that consumes zero or more ancestor entries, so the
    /// match becomes nondeterministic at that position — we try the shortest
    /// alignment first and fall back to deeper skips.
    /// </summary>
    private static bool MatchAncestors(string[] steps, int stepIdx, IReadOnlyList<string> ancestorStack, int stackIdx)
    {
        while (stepIdx >= 0)
        {
            var step = steps[stepIdx];
            if (step == "**")
            {
                // Descendant marker: match zero or more ancestor names. Recurse
                // for each possible alignment of the next concrete step.
                if (stepIdx == 0) return true; // unbounded left edge — anything above is fine
                var nextStep = steps[stepIdx - 1];
                for (var i = stackIdx; i >= 0; i--)
                {
                    if (nextStep == "**" || nextStep == ancestorStack[i] || nextStep == "*")
                    {
                        if (MatchAncestors(steps, stepIdx - 2, ancestorStack, i - 1)) return true;
                    }
                }
                return false;
            }
            if (stackIdx < 0) return false;
            if (step != ancestorStack[stackIdx] && step != "*") return false;
            stepIdx--;
            stackIdx--;
        }
        return true;
    }

    /// <summary>
    /// Checks if the pattern ends with an attribute step and the element path matches.
    /// Returns the attribute local name if matched, null otherwise.
    /// </summary>
    public string? MatchesAttribute(IReadOnlyList<string> ancestorStack, string currentElementName)
    {
        if (_steps.Length < 2) return null;
        var lastStep = _steps[^1];
        if (!lastStep.StartsWith('@')) return null;

        // Check the element path (all steps except the last)
        var elementSteps = _steps[..^1];
        if (elementSteps[^1] != currentElementName && elementSteps[^1] != "*") return null;

        if (!MatchAncestors(elementSteps, elementSteps.Length - 2, ancestorStack, ancestorStack.Count - 1))
            return null;

        return lastStep[1..]; // Strip the @ prefix
    }
}

/// <summary>
/// A watcher that accumulates data from the XML stream for a consuming sub-expression.
/// </summary>
internal sealed class StreamWatcher
{
    /// <summary>
    /// Identity of the AST sub-expression this watcher replaces.
    /// </summary>
    public required XQueryExpression SourceExpression { get; init; }

    /// <summary>
    /// Path pattern to match in the stream.
    /// </summary>
    public required StreamPathMatcher PathMatcher { get; init; }

    /// <summary>
    /// How to accumulate matched values.
    /// </summary>
    public required WatcherAggregation Aggregation { get; init; }

    /// <summary>
    /// Optional: attribute name to extract from matched elements (e.g., "value" for @value).
    /// When null, the element's text content or the element itself is used.
    /// </summary>
    public string? ValueAttribute { get; init; }

    /// <summary>
    /// For StringJoin: the separator string.
    /// </summary>
    public string? Separator { get; init; }

    // Accumulation state
    private long _count;
    private double _sum;
    private double? _max;
    private double? _min;
    private readonly List<string> _strings = [];
    private readonly List<object> _items = [];
    private readonly List<XdmNode> _snapshots = [];

    // Subtree collection state (for Sequence/Snapshot)
    private bool _collectingSubtree;
    private int _subtreeDepth;
    private readonly List<StreamXmlEvent> _subtreeEvents = [];

    /// <summary>
    /// Records a materialized element node for Snapshot/Sequence watchers, alongside
    /// the string capture in <see cref="_items"/>. Used when downstream consumers
    /// (xsl:copy-of, subsequence over copy-of, etc.) need real <see cref="XdmNode"/>
    /// references rather than atomized string values.
    /// </summary>
    public void OnLeafElementCaptured(XdmNode element)
    {
        if (Aggregation is WatcherAggregation.Snapshot or WatcherAggregation.Sequence)
            _snapshots.Add(element);
    }

    /// <summary>
    /// Fills the materialized-element snapshot for a previously reserved slot.
    /// Mirrors <see cref="FillSequenceSlot"/> for the XdmNode parallel array.
    /// Expands <c>_snapshots</c> as needed so it can be indexed by slot.
    /// </summary>
    public void FillSnapshotSlot(int index, XdmNode element)
    {
        if (Aggregation is not (WatcherAggregation.Snapshot or WatcherAggregation.Sequence)) return;
        if (index < 0) return;
        while (_snapshots.Count <= index)
            _snapshots.Add(null!);
        _snapshots[index] = element;
    }

    /// <summary>
    /// Reserves a slot in the items list at the current end (document order
    /// position) and returns its index. Used when an element MATCHES at
    /// StartElement but its text-content value cannot be known until
    /// EndElement (e.g., a non-leaf match under descendant axis). Subsequent
    /// inner matches that resolve sooner will get later indices, preserving
    /// XPath document order.
    /// </summary>
    public int ReserveSequenceSlot()
    {
        if (Aggregation is not (WatcherAggregation.Sequence or WatcherAggregation.Snapshot
            or WatcherAggregation.Sum or WatcherAggregation.Max or WatcherAggregation.Min
            or WatcherAggregation.Avg or WatcherAggregation.StringJoin))
            return -1;
        _items.Add(string.Empty);
        return _items.Count - 1;
    }

    /// <summary>
    /// Fills a previously reserved sequence slot with the element's resolved
    /// text/attribute value. Mirrors the Sequence/Snapshot branch of
    /// <see cref="OnElementMatch"/> but writes by index instead of appending.
    /// </summary>
    public void FillSequenceSlot(int index, IReadOnlyDictionary<string, string>? attributes, string? textContent)
    {
        if (index < 0 || index >= _items.Count) return;
        string? value;
        if (ValueAttribute != null && attributes != null)
            value = attributes.GetValueOrDefault(ValueAttribute);
        else
            value = textContent;
        _items[index] = value ?? string.Empty;
    }

    /// <summary>
    /// Called when a matching element is encountered during the streaming pass.
    /// </summary>
    public void OnElementMatch(string elementName, IReadOnlyDictionary<string, string>? attributes, string? textContent)
    {
        switch (Aggregation)
        {
            case WatcherAggregation.Count:
                _count++;
                break;

            case WatcherAggregation.Sum:
            case WatcherAggregation.Max:
            case WatcherAggregation.Min:
            case WatcherAggregation.Avg:
                var numStr = ValueAttribute != null
                    ? attributes?.GetValueOrDefault(ValueAttribute)
                    : textContent;
                if (numStr != null && double.TryParse(numStr, System.Globalization.CultureInfo.InvariantCulture, out var num))
                {
                    _sum += num;
                    _count++;
                    if (!_max.HasValue || num > _max.Value) _max = num;
                    if (!_min.HasValue || num < _min.Value) _min = num;
                }
                break;

            case WatcherAggregation.StringJoin:
                var str = ValueAttribute != null
                    ? attributes?.GetValueOrDefault(ValueAttribute)
                    : textContent;
                if (str != null) _strings.Add(str);
                break;

            case WatcherAggregation.Sequence:
            case WatcherAggregation.Snapshot:
                // For simple leaf elements, capture value directly
                if (ValueAttribute != null && attributes != null)
                {
                    var attrVal = attributes.GetValueOrDefault(ValueAttribute);
                    if (attrVal != null) _items.Add(attrVal);
                }
                else if (textContent != null)
                {
                    _items.Add(textContent);
                }
                // The streaming processor calls OnLeafElementMatch separately for
                // Snapshot/Sequence watchers when a full XdmElement is needed
                // (e.g. xsl:copy-of select="subsequence(copy-of(/path), 3)"). The
                // captured-element path coexists with the string path above so
                // existing consumers (value-of/string-join) still see strings.
                break;

            case WatcherAggregation.Head:
                // Only capture the first match; subsequent matches are ignored.
                if (_items.Count == 0)
                {
                    var headVal = ValueAttribute != null
                        ? attributes?.GetValueOrDefault(ValueAttribute)
                        : textContent;
                    if (headVal != null) _items.Add(headVal);
                }
                break;
        }
    }

    /// <summary>
    /// Begin collecting a subtree for Sequence/Snapshot watchers.
    /// Called when a match occurs at a non-leaf element.
    /// </summary>
    public void BeginSubtreeCollection()
    {
        _collectingSubtree = true;
        _subtreeDepth = 0;
        _subtreeEvents.Clear();
    }

    /// <summary>
    /// Feed a streaming event during subtree collection.
    /// Returns true if the subtree is complete.
    /// </summary>
    public bool OnSubtreeEvent(StreamXmlEvent evt)
    {
        if (!_collectingSubtree) return false;
        _subtreeEvents.Add(evt);

        if (evt.Type == XmlNodeType.Element) _subtreeDepth++;
        else if (evt.Type == XmlNodeType.EndElement)
        {
            _subtreeDepth--;
            if (_subtreeDepth < 0)
            {
                _collectingSubtree = false;
                // Build in-memory node from collected events
                // (implementation deferred to integration task)
                return true;
            }
        }

        return false;
    }

    public bool IsCollectingSubtree => _collectingSubtree;

    /// <summary>
    /// Gets the accumulated result after the streaming pass.
    /// </summary>
    public object? GetResult()
    {
        return Aggregation switch
        {
            WatcherAggregation.Count => _count,
            WatcherAggregation.Sum => _count > 0 ? _sum : null,
            WatcherAggregation.Max => _max.HasValue ? _max.Value : null,
            WatcherAggregation.Min => _min.HasValue ? _min.Value : null,
            WatcherAggregation.Avg => _count > 0 ? _sum / _count : null,
            WatcherAggregation.StringJoin => string.Join(Separator ?? "", _strings),
            // Prefer materialized element nodes when present AND every match
            // produced one (1:1 with _items). Mixed cases — nested-element or
            // attribute paths skip materialization — fall back to atomized
            // strings used by value-of / string-join consumers.
            WatcherAggregation.Sequence => _snapshots.Count > 0 && _snapshots.Count == _items.Count
                ? _snapshots.Cast<object>().ToArray()
                : (_items.Count > 0 ? _items.ToArray() : Array.Empty<object>()),
            // Snapshot falls back to Array.Empty<XdmNode>() (the prior contract)
            // rather than the atomized strings, so consumers that rely on the
            // node-typed empty result (e.g. sf-snapshot-0101b's deep-equal
            // against a real doc) don't see a sudden type change when
            // materialization skips a nested-element match.
            WatcherAggregation.Snapshot => _snapshots.Count > 0 && _snapshots.Count == _items.Count
                ? _snapshots.Cast<object>().ToArray()
                : Array.Empty<XdmNode>(),
            // Head returns the first item as a scalar (or null if none matched)
            WatcherAggregation.Head => _items.Count > 0 ? _items[0] : null,
            _ => null
        };
    }

    /// <summary>
    /// Reset for reuse.
    /// </summary>
    public void Reset()
    {
        _count = 0;
        _sum = 0;
        _max = null;
        _min = null;
        _strings.Clear();
        _items.Clear();
        _snapshots.Clear();
        _collectingSubtree = false;
        _subtreeEvents.Clear();
    }
}

/// <summary>
/// A captured XML event for incremental subtree building.
/// </summary>
internal readonly record struct StreamXmlEvent(
    XmlNodeType Type,
    string LocalName,
    string? NamespaceUri,
    string? Value,
    IReadOnlyDictionary<string, string>? Attributes);
