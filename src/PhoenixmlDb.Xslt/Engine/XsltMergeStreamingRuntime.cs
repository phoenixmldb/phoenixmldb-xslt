using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

internal sealed partial class DefaultXsltExecutionContext
{
    /// <summary>
    /// Streaming K-way merge runtime. Each merge source is driven by an XmlReader walking
    /// the URIs produced by its <c>for-each-source</c> expression — input documents are
    /// never fully materialised. The classifier
    /// <see cref="XsltMergeStreaming.CanStreamMerge(XsltMerge)"/> guards entry; when it
    /// returns false the engine takes the non-streaming path in
    /// <see cref="MergeAsync(XsltMerge)"/>.
    /// </summary>
    internal async ValueTask MergeAsyncStreaming(XsltMerge instruction)
    {
        if (_nodeStore == null)
            throw Error("Streaming xsl:merge requires an in-memory node store");

        // Resolve key orders / data types once from the first source. The non-streaming
        // path already validates per-source agreement (XTDE2210); we mirror it here.
        var template = instruction.Sources[0].MergeKeys;
        var keyOrders = new List<string>(template.Count);
        var keyDataTypes = new List<string>(template.Count);
        foreach (var mk in template)
        {
            var order = mk.Order != null ? await EvaluateAvtAsync(mk.Order).ConfigureAwait(false) : "ascending";
            if (order != "ascending" && order != "descending")
                throw Error($"XTDE2210: Invalid value '{order}' for order attribute on xsl:merge-key (must be 'ascending' or 'descending')");
            keyOrders.Add(order);
            var dataType = mk.DataType != null ? await EvaluateAvtAsync(mk.DataType).ConfigureAwait(false) : "text";
            if (dataType != "text" && dataType != "number")
                throw Error($"XTDE2210: Invalid value '{dataType}' for data-type attribute on xsl:merge-key (must be 'text' or 'number')");
            keyDataTypes.Add(dataType);
        }
        for (var si = 1; si < instruction.Sources.Count; si++)
        {
            var siKeys = instruction.Sources[si].MergeKeys;
            for (var ki = 0; ki < siKeys.Count && ki < keyOrders.Count; ki++)
            {
                var order = siKeys[ki].Order != null ? await EvaluateAvtAsync(siKeys[ki].Order!).ConfigureAwait(false) : "ascending";
                if (order != keyOrders[ki])
                    throw Error($"XTDE2210: Merge key order differs across sources: '{keyOrders[ki]}' vs '{order}'");
                var dt = siKeys[ki].DataType != null ? await EvaluateAvtAsync(siKeys[ki].DataType!).ConfigureAwait(false) : "text";
                if (dt != keyDataTypes[ki])
                    throw Error($"XTDE2210: Merge key data-type differs across sources: '{keyDataTypes[ki]}' vs '{dt}'");
            }
        }

        // Resolve URI list per source and prepare lazy iterators.
        var iterators = new MergeSourceIterator[instruction.Sources.Count];
        try
        {
            for (var si = 0; si < instruction.Sources.Count; si++)
            {
                var src = instruction.Sources[si];
                var uriResult = await EvaluateAsync(src.ForEachSource!).ConfigureAwait(false);
                var uris = new List<string>();
                AddUriStrings(uris, uriResult);
                iterators[si] = new MergeSourceIterator(this, src, uris);
                // Prime each iterator so the first key is available for the merge loop.
                await iterators[si].AdvanceAsync().ConfigureAwait(false);
            }

            await RunMergeAsync(instruction, iterators, keyOrders, keyDataTypes).ConfigureAwait(false);
        }
        finally
        {
            foreach (var it in iterators)
                it?.Dispose();
        }
    }

    private async Task RunMergeAsync(
        XsltMerge instruction,
        MergeSourceIterator[] iterators,
        List<string> keyOrders,
        List<string> keyDataTypes)
    {
        // Build name → index map for current-merge-group('name') lookups.
        var sourceNames = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var si = 0; si < instruction.Sources.Count; si++)
            if (instruction.Sources[si].Name != null)
                sourceNames[instruction.Sources[si].Name!] = si;

        int mergeGroupPosition = 0;
        while (true)
        {
            // Find the smallest current key across all live iterators.
            int bestIdx = -1;
            List<object?>? bestKey = null;
            for (var si = 0; si < iterators.Length; si++)
            {
                if (!iterators[si].HasCurrent) continue;
                if (bestKey == null || CompareMergeKeys(iterators[si].CurrentKey!, bestKey, keyOrders, keyDataTypes) < 0)
                {
                    bestIdx = si;
                    bestKey = iterators[si].CurrentKey;
                }
            }
            if (bestIdx == -1) break;

            // Drain every iterator whose current key equals the best key.
            var taggedItems = new List<(object Item, int SourceIndex)>();
            for (var si = 0; si < iterators.Length; si++)
            {
                while (iterators[si].HasCurrent
                    && CompareMergeKeys(iterators[si].CurrentKey!, bestKey!, keyOrders, keyDataTypes) == 0)
                {
                    taggedItems.Add((iterators[si].CurrentItem!, si));
                    await iterators[si].AdvanceAsync().ConfigureAwait(false);
                }
            }

            mergeGroupPosition++;
            var items = new List<object>(taggedItems.Count);
            foreach (var t in taggedItems) items.Add(t.Item);

            PushScope();
            try
            {
                SetVariable(new QName(NamespaceId.None, "current-merge-group"), items);
                var mergeKeyValue = bestKey!.Count == 1
                    ? (object)StringValueOf(bestKey[0])
                    : bestKey.Select(StringValueOf).ToArray();
                SetVariable(new QName(NamespaceId.None, "current-merge-key"), mergeKeyValue);
                foreach (var (name, idx) in sourceNames)
                {
                    var perSource = taggedItems.Where(t => t.SourceIndex == idx).Select(t => t.Item).ToList();
                    SetVariable(new QName(NamespaceId.None, $"current-merge-group:{name}"), perSource);
                }
                PushContextItem(items[0], mergeGroupPosition, -1);
                try
                {
                    await instruction.Action.ExecuteAsync(this).ConfigureAwait(false);
                }
                finally
                {
                    PopContextItem();
                }
            }
            finally
            {
                PopScope();
            }
        }
    }

    private static void AddUriStrings(List<string> dest, object? value)
    {
        switch (value)
        {
            case null:
                return;
            case string s:
                if (!string.IsNullOrEmpty(s)) dest.Add(s);
                return;
            case object?[] arr:
                foreach (var x in arr) AddUriStrings(dest, x);
                return;
            case System.Collections.IEnumerable seq:
                foreach (var x in seq) AddUriStrings(dest, x);
                return;
            default:
                var str = value.ToString();
                if (!string.IsNullOrEmpty(str)) dest.Add(str);
                return;
        }
    }

    /// <summary>
    /// Lazy per-source cursor that pulls one matching item at a time from an XmlReader,
    /// computing the merge keys in temporary-output state (XTDE1480). Walks each URI in
    /// turn so a single source with multiple <c>for-each-source</c> URIs still produces
    /// a single ordered sub-sequence.
    /// </summary>
    private sealed class MergeSourceIterator : IDisposable
    {
        private readonly DefaultXsltExecutionContext _ctx;
        private readonly XsltMergeSource _source;
        private readonly NodeTest _childTest;
        private readonly IReadOnlyList<string> _uris;
        private int _uriIndex;
        private XmlReader? _reader;
        private bool _inRoot;
        private DocumentId _currentDocId;

        public object? CurrentItem { get; private set; }
        public List<object?>? CurrentKey { get; private set; }
        public bool HasCurrent => CurrentItem != null;

        public MergeSourceIterator(DefaultXsltExecutionContext ctx, XsltMergeSource source, IReadOnlyList<string> uris)
        {
            _ctx = ctx;
            _source = source;
            _uris = uris;
            _childTest = XsltMergeStreaming.TryGetSimpleChildStep(source.Select)!;
            // Resolve NameTest namespace eagerly if the parser left ResolvedNamespace unset.
            if (_childTest is NameTest nt && nt.ResolvedNamespace == null
                && !string.IsNullOrEmpty(nt.NamespaceUri) && nt.NamespaceUri != "*")
            {
                nt.ResolveNamespace(_ctx._nodeStore!.InternNamespace);
            }
            else if (_childTest is KindTest { Name: { } kn } && kn.ResolvedNamespace == null
                && !string.IsNullOrEmpty(kn.NamespaceUri) && kn.NamespaceUri != "*")
            {
                kn.ResolveNamespace(_ctx._nodeStore!.InternNamespace);
            }
        }

        public async ValueTask AdvanceAsync()
        {
            CurrentItem = null;
            CurrentKey = null;
            while (true)
            {
                if (_reader == null)
                {
                    if (_uriIndex >= _uris.Count) return;
                    _reader = OpenReader(_uris[_uriIndex++]);
                    _inRoot = false;
                    _currentDocId = new DocumentId((ulong)_ctx._nodeStore!.NextId().Value);
                }
                var match = ReadNextMatch();
                if (match != null)
                {
                    CurrentItem = match;
                    CurrentKey = await ComputeKeysAsync(match).ConfigureAwait(false);
                    return;
                }
                // Reader exhausted — move to next URI.
                _reader.Dispose();
                _reader = null;
            }
        }

        private XdmElement? ReadNextMatch()
        {
            // ReadXdmElementFromReader advances past the end tag on return, so this loop
            // must NOT call Read() unconditionally — it would skip the very next sibling.
            // Instead we only Read() when the reader's current node was not consumed by
            // a subtree build (e.g. the initial-state advance or skipping the root start).
            if (_reader!.ReadState == System.Xml.ReadState.Initial)
            {
                if (!_reader.Read()) return null;
            }
            while (!_reader.EOF)
            {
                if (!_inRoot)
                {
                    if (_reader.NodeType == XmlNodeType.Element)
                    {
                        _inRoot = true;
                        if (!_reader.Read()) return null; // step inside root
                        continue;
                    }
                    if (!_reader.Read()) return null;
                    continue;
                }

                if (_reader.NodeType == XmlNodeType.EndElement && _reader.Depth == 0)
                    return null; // root closed

                if (_reader.NodeType == XmlNodeType.Element && _reader.Depth == 1)
                {
                    var nsId = _ctx._nodeStore!.InternNamespace(_reader.NamespaceURI ?? "");
                    if (_childTest.Matches(XdmNodeKind.Element, nsId, _reader.LocalName))
                    {
                        // ReadXdmElementFromReader builds the subtree and leaves the
                        // reader positioned past the matching end tag — do not Read()
                        // before the next iteration.
                        return _ctx.ReadXdmElementFromReader(_reader, parentId: default, _currentDocId);
                    }
                    // Non-matching element: skip its entire subtree in one step (Skip()
                    // already advances past the end tag).
                    _reader.Skip();
                    continue;
                }

                if (!_reader.Read()) return null;
            }
            return null;
        }

        private async Task<List<object?>> ComputeKeysAsync(XdmElement item)
        {
            var keys = new List<object?>(_source.MergeKeys.Count);
            _ctx.PushContextItem(item, 1, 1);
            _ctx._temporaryOutputDepth++;
            try
            {
                foreach (var mk in _source.MergeKeys)
                {
                    object? keyVal;
                    if (mk.Select != null)
                    {
                        keyVal = await _ctx.EvaluateAsync(mk.Select).ConfigureAwait(false);
                        if (keyVal is object?[] arr)
                        {
                            if (arr.Length > 1)
                                throw _ctx.Error("XTTE1020: The value of a merge key must be a single atomic value");
                            keyVal = arr.Length == 1 ? arr[0] : null;
                        }
                        else if (keyVal is IEnumerable<object> seq && keyVal is not string && keyVal is not Xdm.Nodes.XdmNode)
                        {
                            var list = seq.ToList();
                            if (list.Count > 1)
                                throw _ctx.Error("XTTE1020: The value of a merge key must be a single atomic value");
                            keyVal = list.Count == 1 ? list[0] : null;
                        }
                    }
                    else if (mk.Content != null)
                    {
                        _ctx.BeginSequenceCollection();
                        await mk.Content.ExecuteAsync(_ctx).ConfigureAwait(false);
                        var items = _ctx.EndSequenceCollection();
                        keyVal = items.Count == 1 ? items[0] : items.Count == 0 ? null : items;
                    }
                    else
                    {
                        keyVal = null;
                    }
                    keys.Add(keyVal);
                }
            }
            finally
            {
                _ctx._temporaryOutputDepth--;
                _ctx.PopContextItem();
            }
            return keys;
        }

        private XmlReader OpenReader(string uri)
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Parse, Async = false };
            // Resolve relative URIs against the stylesheet's static base URI.
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var absUri))
            {
                if (_ctx.StaticBaseUri != null
                    && Uri.TryCreate(_ctx.StaticBaseUri, UriKind.Absolute, out var baseUri)
                    && Uri.TryCreate(baseUri, uri, out var resolved))
                {
                    absUri = resolved;
                }
                else
                {
                    // Bare relative path with no base — open as filesystem path.
                    return XmlReader.Create(uri, settings);
                }
            }
            if (absUri.IsFile)
                return XmlReader.Create(absUri.LocalPath, settings);
            if (absUri.Scheme is "http" or "https")
            {
                var stream = HttpDocumentLoader.OpenRead(absUri);
                return XmlReader.Create(stream, settings, absUri.AbsoluteUri);
            }
            // Unknown scheme — let XmlReader try (will likely throw, surfacing the URI).
            return XmlReader.Create(absUri.ToString(), settings);
        }

        public void Dispose()
        {
            _reader?.Dispose();
            _reader = null;
        }
    }
}
