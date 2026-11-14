using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
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

        // Key settings from the first source, and XTDE2210 across sources — the same rules, and
        // the same collation handling, as the in-memory path (ResolveMergeKeySettingsAsync).
        var (keyOrders, keyDataTypes, keyCollations) =
            await ResolveMergeKeySettingsAsync(instruction.Sources[0].MergeKeys).ConfigureAwait(false);
        for (var si = 1; si < instruction.Sources.Count; si++)
        {
            var (orders2, dataTypes2, collations2) =
                await ResolveMergeKeySettingsAsync(instruction.Sources[si].MergeKeys).ConfigureAwait(false);
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

        // One input sequence per document: each URI of a for-each-source is merged with the
        // others, as it is unstreamed. One iterator read a source's documents one after the
        // other, so their items were concatenated, not merged.
        var opened = new List<MergeSourceIterator>();
        try
        {
            for (var si = 0; si < instruction.Sources.Count; si++)
            {
                var src = instruction.Sources[si];
                var uriResult = await EvaluateAsync(src.ForEachSource!).ConfigureAwait(false);
                var uris = new List<string>();
                AddUriStrings(uris, uriResult);
                foreach (var uri in uris)
                {
                    var iterator = new MergeSourceIterator(this, src, [uri]) { SourceIndex = si };
                    opened.Add(iterator);
                    // Prime each iterator so the first key is available for the merge loop.
                    await iterator.AdvanceAsync().ConfigureAwait(false);
                }
            }
            var iterators = opened.ToArray();

            await RunMergeAsync(instruction, iterators, keyOrders, keyDataTypes, keyCollations).ConfigureAwait(false);
        }
        finally
        {
            foreach (var it in opened)
                it.Dispose();
        }
    }

    private async Task RunMergeAsync(
        XsltMerge instruction,
        MergeSourceIterator[] iterators,
        List<string> keyOrders,
        List<string> keyDataTypes,
        IReadOnlyList<string?> keyCollations)
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
                if (bestKey == null || CompareMergeKeys(iterators[si].CurrentKey!, bestKey, keyOrders, keyDataTypes, keyCollations) < 0)
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
                    && CompareMergeKeys(iterators[si].CurrentKey!, bestKey!, keyOrders, keyDataTypes, keyCollations) == 0)
                {
                    taggedItems.Add((iterators[si].CurrentItem!, iterators[si].SourceIndex));
                    await iterators[si].AdvanceAsync().ConfigureAwait(false);
                }
            }

            mergeGroupPosition++;
            var items = new List<object>(taggedItems.Count);
            foreach (var t in taggedItems) items.Add(t.Item);

            PushScope();
            try
            {
                _mergeGroupBound = true;
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
        private readonly IReadOnlyList<NodeTest> _childTests;
        // The start tags of the elements the reader is inside, outermost first: an item is
        // delivered as a snapshot with these as its ancestors (XSLT 3.0 §15.4).
        private readonly List<OpenAncestor> _ancestors = [];

        private sealed record OpenAncestor(string NamespaceUri, string? Prefix, string LocalName,
            List<(string NamespaceUri, string? Prefix, string LocalName, string Value)> Attributes,
            List<NamespaceBinding> NamespaceDeclarations);
        private readonly IReadOnlyList<string> _uris;
        private int _uriIndex;
        private XmlReader? _reader;
        private DocumentId _currentDocId;

        /// <summary>The index of the xsl:merge-source this iterator reads a document of.</summary>
        public int SourceIndex { get; init; }

        public object? CurrentItem { get; private set; }
        public List<object?>? CurrentKey { get; private set; }
        public bool HasCurrent => CurrentItem != null;

        public MergeSourceIterator(DefaultXsltExecutionContext ctx, XsltMergeSource source, IReadOnlyList<string> uris)
        {
            _ctx = ctx;
            _source = source;
            _uris = uris;
            _childTests = XsltMergeStreaming.TryGetChildSteps(source.Select)!;
            // Resolve NameTest namespaces eagerly if the parser left ResolvedNamespace unset.
            foreach (var test in _childTests)
            {
                if (test is NameTest nt && nt.ResolvedNamespace == null
                    && !string.IsNullOrEmpty(nt.NamespaceUri) && nt.NamespaceUri != "*")
                {
                    nt.ResolveNamespace(_ctx._nodeStore!.InternNamespace);
                }
                else if (test is KindTest { Name: { } kn } && kn.ResolvedNamespace == null
                    && !string.IsNullOrEmpty(kn.NamespaceUri) && kn.NamespaceUri != "*")
                {
                    kn.ResolveNamespace(_ctx._nodeStore!.InternNamespace);
                }
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
            // An element at depth d is tested against step d of the path; one that fails is
            // skipped whole, so every element the reader reaches has ancestors that matched the
            // steps before. One that passes the last step is an item of the merge source.
            while (!_reader.EOF)
            {
                if (_reader.NodeType == XmlNodeType.Element)
                {
                    var depth = _reader.Depth;
                    var nsId = _ctx._nodeStore!.InternNamespace(_reader.NamespaceURI ?? "");
                    if (depth >= _childTests.Count
                        || !_childTests[depth].Matches(XdmNodeKind.Element, nsId, _reader.LocalName))
                    {
                        // Skip() advances past the end tag of the whole subtree.
                        _reader.Skip();
                        continue;
                    }
                    if (depth == _childTests.Count - 1)
                        return ReadSnapshot(depth);
                    // An ancestor of the items to come: keep its start tag.
                    _ancestors.RemoveRange(depth, _ancestors.Count - depth);
                    _ancestors.Add(ReadStartTag());
                }
                if (!_reader.Read()) return null;
            }
            return null;
        }

        private OpenAncestor ReadStartTag()
        {
            var attributes = new List<(string, string?, string, string)>();
            var declarations = new List<NamespaceBinding>();
            if (_reader!.HasAttributes)
            {
                for (var i = 0; i < _reader.AttributeCount; i++)
                {
                    _reader.MoveToAttribute(i);
                    if (_reader.Prefix == "xmlns")
                        declarations.Add(new NamespaceBinding(_reader.LocalName, _ctx._nodeStore!.InternNamespace(_reader.Value)));
                    else if (string.IsNullOrEmpty(_reader.Prefix) && _reader.LocalName == "xmlns")
                        declarations.Add(new NamespaceBinding("", _ctx._nodeStore!.InternNamespace(_reader.Value)));
                    else
                        attributes.Add((_reader.NamespaceURI ?? "", string.IsNullOrEmpty(_reader.Prefix) ? null : _reader.Prefix, _reader.LocalName, _reader.Value));
                }
                _reader.MoveToElement();
            }
            return new OpenAncestor(_reader.NamespaceURI ?? "", string.IsNullOrEmpty(_reader.Prefix) ? null : _reader.Prefix,
                _reader.LocalName, attributes, declarations);
        }

        /// <summary>
        /// The element at the reader, built with its subtree, under a document node and copies
        /// of its ancestors' start tags, each with the next as its only child: what fn:snapshot
        /// gives. It was built with no parent, so root() of an item was the item itself.
        /// </summary>
        private XdmElement ReadSnapshot(int depth)
        {
            var store = _ctx._nodeStore!;
            // Ids in document order: the document node, the ancestors outermost first, the item.
            var documentNodeId = store.NextId();
            var ancestorIds = new List<NodeId>(depth);
            for (var i = 0; i < depth; i++)
                ancestorIds.Add(store.NextId());
            var item = _ctx.ReadXdmElementFromReader(_reader!, depth > 0 ? ancestorIds[^1] : documentNodeId, _currentDocId);
            store.Register(new XdmDocument
            {
                StringValueResolver = store.StringValueResolver,
                Id = documentNodeId,
                Document = _currentDocId,
                Children = [depth > 0 ? ancestorIds[0] : item.Id],
                DocumentElement = depth > 0 ? ancestorIds[0] : item.Id,
                Parent = null,
                DocumentUri = _uris[_uriIndex - 1],
                BaseUri = _uris[_uriIndex - 1],
            });
            for (var i = 0; i < depth; i++)
            {
                var open = _ancestors[i];
                var attributeIds = new List<NodeId>(open.Attributes.Count);
                foreach (var (ns, prefix, local, value) in open.Attributes)
                {
                    var attributeId = store.NextId();
                    store.Register(new XdmAttribute
                    {
                        Id = attributeId,
                        Document = _currentDocId,
                        Parent = ancestorIds[i],
                        Namespace = store.InternNamespace(ns),
                        LocalName = local,
                        Prefix = prefix,
                        Value = value,
                    });
                    attributeIds.Add(attributeId);
                }
                store.Register(new XdmElement
                {
                    Id = ancestorIds[i],
                    Document = _currentDocId,
                    Namespace = store.InternNamespace(open.NamespaceUri),
                    LocalName = open.LocalName,
                    Prefix = open.Prefix,
                    Attributes = attributeIds.Count == 0 ? XdmElement.EmptyAttributes : attributeIds,
                    Children = [i + 1 < depth ? ancestorIds[i + 1] : item.Id],
                    NamespaceDeclarations = open.NamespaceDeclarations.Count == 0 ? XdmElement.EmptyNamespaceDeclarations : open.NamespaceDeclarations,
                    Parent = i == 0 ? documentNodeId : ancestorIds[i - 1],
                });
            }
            return item;
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
            if (_ctx.Policy is { } policy)
            {
                // Under a resource policy: resolve, authorise, and read only what was authorised.
                var baseUri = _ctx.StaticBaseUri is { } sb && Uri.TryCreate(sb, UriKind.Absolute, out var b) ? b : null;
                if (baseUri == null && PhoenixmlDb.XQuery.Functions.LocationResolver.IsRelativeName(uri))
                {
                    // A relative URI with no base: only the host can supply it, by that name. It
                    // was resolved against the process's working directory, offered to the host
                    // under that path, and read from there.
                    _ctx.ApplyEntityPolicy(settings);
                    settings.CloseInput = true;
                    try
                    {
                        if (PhoenixmlDb.XQuery.Security.ResourceGate.HostContent(policy, uri, _ctx.Caller,
                                PhoenixmlDb.XQuery.Security.ResourceAccessKind.ReadDocument) is { } named)
                            return XmlReader.Create(new System.IO.MemoryStream(named.ReadBytes().Bytes), settings, named.BaseUri.AbsoluteUri);
                    }
                    catch (PhoenixmlDb.XQuery.Security.ResourceAccessDeniedException e)
                    {
                        throw new XsltException($"FODC0002: {e.Message}");
                    }
                    throw new XsltException($"FODC0002: {PhoenixmlDb.XQuery.Functions.LocationResolver.NoBaseUri(uri)}");
                }
                var target = PhoenixmlDb.XQuery.Security.ResourcePolicy.Resolve(uri, baseUri)
                    ?? throw new XsltException($"FODC0002: Invalid merge source URI '{uri}'");
                _ctx.ApplyEntityPolicy(settings);
                settings.CloseInput = true;
                // The host's own content first: with it, nothing is opened here by name, and a
                // resolver that is the only source of resources is never passed by.
                try
                {
                    if (PhoenixmlDb.XQuery.Security.ResourceGate.HostContent(policy, target.AbsoluteUri, _ctx.Caller,
                            PhoenixmlDb.XQuery.Security.ResourceAccessKind.ReadDocument) is { } supplied)
                        return XmlReader.Create(new System.IO.MemoryStream(supplied.ReadBytes().Bytes), settings, target.AbsoluteUri);
                }
                catch (PhoenixmlDb.XQuery.Security.ResourceAccessDeniedException e)
                {
                    throw new XsltException($"FODC0002: {e.Message}");
                }
                var authorized = _ctx.AuthorizeResource(target, PhoenixmlDb.XQuery.Security.ResourceAccessKind.ReadDocument, "FODC0002");
                if (authorized.IsFile)
                    return XmlReader.Create(System.IO.File.OpenRead(authorized.LocalPath), settings, authorized.AbsoluteUri);
                if (authorized.Scheme is "http" or "https")
                    return XmlReader.Create(HttpDocumentLoader.OpenRead(authorized, policy), settings, authorized.AbsoluteUri);
                throw new XsltException($"FODC0002: Cannot read merge source '{uri}': unsupported URI scheme '{authorized.Scheme}'");
            }
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
                    // A relative URI with no base names nothing. It was opened as a path in the
                    // process's working directory.
                    throw new XsltException($"FODC0002: {PhoenixmlDb.XQuery.Functions.LocationResolver.NoBaseUri(uri)}");
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
