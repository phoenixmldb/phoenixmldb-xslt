using System.Xml;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// An <see cref="XmlReader"/> over the live streamed document that can answer "does the element
/// whose start tag I am on have children?" without any consumer seeing that it looked ahead.
/// </summary>
/// <remarks>
/// <para>
/// fn:has-children() is motionless (XSLT 3.0 §19.8.9) but cannot be answered at the start tag:
/// <c>&lt;a&gt;&lt;/a&gt;</c> is not an empty element and has no children, so
/// <see cref="XmlReader.IsEmptyElement"/> is the wrong question (BUGS #92). The answer is the next
/// event. But the streaming engine has about thirty places that read from this reader expecting to
/// be on the start tag — the processor loop, apply-templates, copy-of, the subtree materialiser —
/// and teaching each one about a lookahead is the change BUGS #92 warned against.
/// </para>
/// <para>
/// So the lookahead lives here instead. <see cref="PeekHasChildren"/> snapshots the start tag,
/// reads ahead, and then REPLAYS the start tag before handing the live reader back. Until then
/// every member answers from the snapshot, so the peek is invisible. Without a peek, every member
/// is a straight pass-through.
/// </para>
/// <para>
/// It also applies xsl:strip-space, for the same reason: streamed input used to ignore it entirely,
/// because the tree path strips while building a document and the streaming path builds none. A
/// whitespace-only text node whose parent's whitespace is stripped is simply never surfaced, so
/// every consumer of the stream sees the stripped document without any of them changing.
/// </para>
/// </remarks>
internal sealed class PeekableXmlReader : XmlReader, IXmlLineInfo, IXmlNamespaceResolver
{
    private readonly XmlReader _inner;

    // Replay state. Non-null only between a peek and the Read() that exhausts the replay queue;
    // _replay[_replayIndex] is the node currently presented, and the inner reader sits on the node
    // AFTER the last one in the queue.
    private List<Snapshot>? _replay;
    private int _replayIndex;
    private int _attrIndex = -1;        // -1: on the element; otherwise the attribute presented
    private bool _onAttrValue;          // ReadAttributeValue() moved onto the attribute's text

    // How many times the reader has advanced. has-children() records it when a start tag is
    // dispatched and only peeks if it is unchanged, so a stale question cannot be answered about
    // whatever node the reader has since moved on to.
    private long _position;
    private long _peekedPosition = -1;
    private bool _peekedAnswer;

    // xsl:strip-space by element name, and one entry per open element: does it strip whitespace?
    // Null when the stylesheet strips nothing, in which case nothing is tracked.
    private readonly Func<string, string, bool>? _stripsWhitespace;
    private readonly List<bool> _openElementStrips = new();

    public PeekableXmlReader(XmlReader inner, Func<string, string, bool>? stripsWhitespace = null)
    {
        _inner = inner;
        _stripsWhitespace = stripsWhitespace;
    }

    /// <summary>Advances whenever the presented node changes; see <see cref="PeekHasChildren"/>.</summary>
    public long Position => _position;

    private sealed record Snapshot(
        XmlNodeType NodeType, string LocalName, string NamespaceURI, string Prefix, string Name,
        string Value, int Depth, bool IsEmptyElement, string BaseURI, string XmlLang, XmlSpace XmlSpace,
        int LineNumber, int LinePosition,
        IReadOnlyList<(string LocalName, string NamespaceURI, string Prefix, string Name, string Value)> Attributes,
        IDictionary<string, string>? NamespacesInScope);

    private Snapshot? Current => _replay is null ? null : _replay[_replayIndex];

    /// <summary>
    /// Whether the element whose (non-empty) start tag is presented has at least one child. Reads
    /// one event ahead — whitespace xsl:strip-space removes is already filtered, so it is not a
    /// child — and replays the start tag.
    /// </summary>
    /// <returns><c>null</c> when the reader is not on a start tag at <paramref name="expectedPosition"/>.</returns>
    public bool? PeekHasChildren(long expectedPosition)
    {
        if (_peekedPosition == expectedPosition)
            return _peekedAnswer;
        if (_position != expectedPosition || _replay is not null
            || _inner.NodeType != XmlNodeType.Element)
            return null;
        if (_inner.IsEmptyElement)
            return false;

        // Snapshot BEFORE advancing: once the inner reader has moved, the start tag is gone.
        var start = Capture(_inner);
        AdvanceInner();
        var hasChildren = !(_inner.NodeType == XmlNodeType.EndElement && _inner.Depth == start.Depth);

        _replay = new List<Snapshot> { start };
        _replayIndex = 0;
        _attrIndex = -1;
        _onAttrValue = false;
        _peekedPosition = expectedPosition;
        _peekedAnswer = hasChildren;
        return hasChildren;
    }

    // ---- the filtered advance -----------------------------------------------------------------

    /// <summary>
    /// Moves the inner reader to the next node that is in the tree: whitespace-only text inside an
    /// element whose whitespace is stripped is skipped. Tracks open elements as it goes.
    /// </summary>
    private bool AdvanceInner()
    {
        while (_inner.Read())
        {
            if (!ArriveAndKeep()) continue;
            return true;
        }
        return false;
    }

    private async Task<bool> AdvanceInnerAsync()
    {
        while (await _inner.ReadAsync().ConfigureAwait(false))
        {
            if (!ArriveAndKeep()) continue;
            return true;
        }
        return false;
    }

    /// <summary>Records the node the inner reader just landed on; false when it is stripped.</summary>
    private bool ArriveAndKeep()
    {
        if (_stripsWhitespace is null)
            return true;
        switch (_inner.NodeType)
        {
            case XmlNodeType.Element when !_inner.IsEmptyElement:
                _openElementStrips.Add(_stripsWhitespace(_inner.LocalName, _inner.NamespaceURI));
                return true;
            case XmlNodeType.EndElement:
                if (_openElementStrips.Count > 0) _openElementStrips.RemoveAt(_openElementStrips.Count - 1);
                return true;
            case XmlNodeType.Whitespace:
                // Only XmlNodeType.Whitespace: under xml:space="preserve" the reader reports
                // SignificantWhitespace, which xsl:strip-space must not touch.
                return !(_openElementStrips.Count > 0 && _openElementStrips[^1]);
            default:
                return true;
        }
    }

    private static Snapshot Capture(XmlReader r)
    {
        var attrs = new List<(string, string, string, string, string)>();
        if (r.NodeType == XmlNodeType.Element && r.MoveToFirstAttribute())
        {
            do attrs.Add((r.LocalName, r.NamespaceURI, r.Prefix, r.Name, r.Value));
            while (r.MoveToNextAttribute());
            r.MoveToElement();
        }
        var lineInfo = r as IXmlLineInfo;
        return new Snapshot(
            r.NodeType, r.LocalName, r.NamespaceURI, r.Prefix, r.Name, r.Value, r.Depth,
            r.IsEmptyElement, r.BaseURI, r.XmlLang, r.XmlSpace,
            lineInfo?.LineNumber ?? 0, lineInfo?.LinePosition ?? 0,
            attrs,
            r.NodeType == XmlNodeType.Element && r is IXmlNamespaceResolver ns
                ? ns.GetNamespacesInScope(XmlNamespaceScope.All)
                : null);
    }

    // ---- advancing ----------------------------------------------------------------------------

    public override bool Read()
    {
        _position++;
        if (_replay is null)
            return AdvanceInner();
        AdvanceReplay();
        return true;   // either the next replayed node, or the live node the inner reader is on
    }

    public override Task<bool> ReadAsync()
    {
        _position++;
        if (_replay is null)
            return AdvanceInnerAsync();
        AdvanceReplay();
        return Task.FromResult(true);
    }

    private void AdvanceReplay()
    {
        _attrIndex = -1;
        _onAttrValue = false;
        if (++_replayIndex >= _replay!.Count)
            _replay = null;   // replay exhausted: the inner reader is already on the next node
    }

    public override void Skip()
    {
        if (_replay is null)
        {
            _position++;
            var skippingOpenElement = _inner.NodeType == XmlNodeType.Element && !_inner.IsEmptyElement;
            _inner.Skip();
            // The skipped element was recorded as open when the reader landed on it, and Skip()
            // consumed its end tag without our seeing it.
            if (skippingOpenElement && _stripsWhitespace is not null && _openElementStrips.Count > 0)
                _openElementStrips.RemoveAt(_openElementStrips.Count - 1);
            if (!_inner.EOF && !ArriveAndKeep())
                AdvanceInner();
            return;
        }
        var snap = Current!;
        if (snap.NodeType != XmlNodeType.Element) { Read(); return; }
        // Skip the whole replayed element: drop the replay and move the inner reader past the end
        // tag that matches the snapshot's depth. Nested elements are skipped whole, so they were
        // never recorded as open; the replayed element itself was, and is closed here.
        _position++;
        _replay = null;
        _attrIndex = -1;
        _onAttrValue = false;
        while (!(_inner.NodeType == XmlNodeType.EndElement && _inner.Depth == snap.Depth))
        {
            if (_inner.NodeType == XmlNodeType.Element && !_inner.IsEmptyElement && _inner.Depth > snap.Depth)
                _inner.Skip();
            else if (!_inner.Read())
                return;
        }
        if (_stripsWhitespace is not null && _openElementStrips.Count > 0)
            _openElementStrips.RemoveAt(_openElementStrips.Count - 1);
        AdvanceInner();
    }

    public override async Task SkipAsync()
    {
        if (_replay is not null) { Skip(); return; }
        _position++;
        var skippingOpenElement = _inner.NodeType == XmlNodeType.Element && !_inner.IsEmptyElement;
        await _inner.SkipAsync().ConfigureAwait(false);
        if (skippingOpenElement && _stripsWhitespace is not null && _openElementStrips.Count > 0)
            _openElementStrips.RemoveAt(_openElementStrips.Count - 1);
        if (!_inner.EOF && !ArriveAndKeep())
            await AdvanceInnerAsync().ConfigureAwait(false);
    }

    // ---- the presented node -------------------------------------------------------------------

    private (string LocalName, string NamespaceURI, string Prefix, string Name, string Value) Attr
        => Current!.Attributes[_attrIndex];

    public override XmlNodeType NodeType => Current is not { } s ? _inner.NodeType
        : _onAttrValue ? XmlNodeType.Text
        : _attrIndex >= 0 ? XmlNodeType.Attribute
        : s.NodeType;

    public override string LocalName => Current is null ? _inner.LocalName
        : _onAttrValue ? string.Empty : _attrIndex >= 0 ? Attr.LocalName : Current.LocalName;

    public override string NamespaceURI => Current is null ? _inner.NamespaceURI
        : _onAttrValue ? string.Empty : _attrIndex >= 0 ? Attr.NamespaceURI : Current.NamespaceURI;

    public override string Prefix => Current is null ? _inner.Prefix
        : _onAttrValue ? string.Empty : _attrIndex >= 0 ? Attr.Prefix : Current.Prefix;

    public override string Name => Current is null ? _inner.Name
        : _onAttrValue ? string.Empty : _attrIndex >= 0 ? Attr.Name : Current.Name;

    public override string Value => Current is null ? _inner.Value
        : _attrIndex >= 0 ? Attr.Value : Current.Value;

    public override bool HasValue => Current is null ? _inner.HasValue
        : _attrIndex >= 0 || Current.NodeType is XmlNodeType.Whitespace or XmlNodeType.Text;

    public override int Depth => Current is null ? _inner.Depth
        : Current.Depth + (_onAttrValue ? 2 : _attrIndex >= 0 ? 1 : 0);

    public override bool IsEmptyElement => Current is null ? _inner.IsEmptyElement
        : _attrIndex < 0 && Current.IsEmptyElement;

    public override string BaseURI => Current?.BaseURI ?? _inner.BaseURI;
    public override string XmlLang => Current?.XmlLang ?? _inner.XmlLang;
    public override XmlSpace XmlSpace => Current?.XmlSpace ?? _inner.XmlSpace;
    public override int AttributeCount => Current?.Attributes.Count ?? _inner.AttributeCount;
    public override bool EOF => Current is null && _inner.EOF;
    public override ReadState ReadState => Current is null ? _inner.ReadState : ReadState.Interactive;
    public override XmlNameTable NameTable => _inner.NameTable;
    public override XmlReaderSettings? Settings => _inner.Settings;
    public override bool CanResolveEntity => Current is null && _inner.CanResolveEntity;

    public override Task<string> GetValueAsync()
        => Current is null ? _inner.GetValueAsync() : Task.FromResult(Value);

    // ---- attributes ---------------------------------------------------------------------------

    public override string GetAttribute(int i)
        => Current is null ? _inner.GetAttribute(i) : Current.Attributes[i].Value;

    public override string? GetAttribute(string name)
    {
        if (Current is null) return _inner.GetAttribute(name);
        foreach (var a in Current.Attributes)
            if (a.Name == name) return a.Value;
        return null;
    }

    public override string? GetAttribute(string name, string? namespaceURI)
    {
        if (Current is null) return _inner.GetAttribute(name, namespaceURI);
        foreach (var a in Current.Attributes)
            if (a.LocalName == name && a.NamespaceURI == (namespaceURI ?? string.Empty)) return a.Value;
        return null;
    }

    public override bool MoveToAttribute(string name)
    {
        if (Current is null) return _inner.MoveToAttribute(name);
        for (var i = 0; i < Current.Attributes.Count; i++)
            if (Current.Attributes[i].Name == name) { _attrIndex = i; _onAttrValue = false; return true; }
        return false;
    }

    public override bool MoveToAttribute(string name, string? ns)
    {
        if (Current is null) return _inner.MoveToAttribute(name, ns);
        for (var i = 0; i < Current.Attributes.Count; i++)
            if (Current.Attributes[i].LocalName == name && Current.Attributes[i].NamespaceURI == (ns ?? string.Empty))
            { _attrIndex = i; _onAttrValue = false; return true; }
        return false;
    }

    public override void MoveToAttribute(int i)
    {
        if (Current is null) { _inner.MoveToAttribute(i); return; }
        if (i < 0 || i >= Current.Attributes.Count) throw new ArgumentOutOfRangeException(nameof(i));
        _attrIndex = i;
        _onAttrValue = false;
    }

    public override bool MoveToFirstAttribute()
    {
        if (Current is null) return _inner.MoveToFirstAttribute();
        if (Current.Attributes.Count == 0) return false;
        _attrIndex = 0;
        _onAttrValue = false;
        return true;
    }

    public override bool MoveToNextAttribute()
    {
        if (Current is null) return _inner.MoveToNextAttribute();
        if (_attrIndex + 1 >= Current.Attributes.Count) return false;
        _attrIndex++;
        _onAttrValue = false;
        return true;
    }

    public override bool MoveToElement()
    {
        if (Current is null) return _inner.MoveToElement();
        var moved = _attrIndex >= 0;
        _attrIndex = -1;
        _onAttrValue = false;
        return moved;
    }

    public override bool ReadAttributeValue()
    {
        if (Current is null) return _inner.ReadAttributeValue();
        if (_attrIndex < 0 || _onAttrValue) return false;
        _onAttrValue = true;
        return true;
    }

    public override string? LookupNamespace(string prefix)
    {
        if (Current?.NamespacesInScope is { } scope)
            return scope.TryGetValue(prefix, out var uri) ? uri : null;
        return _inner.LookupNamespace(prefix);
    }

    public override void ResolveEntity()
    {
        if (Current is not null) throw new InvalidOperationException("No entity reference to resolve on a replayed node.");
        _inner.ResolveEntity();
    }

    // ---- IXmlNamespaceResolver / IXmlLineInfo -------------------------------------------------

    IDictionary<string, string> IXmlNamespaceResolver.GetNamespacesInScope(XmlNamespaceScope scope)
        => Current?.NamespacesInScope is { } s ? new Dictionary<string, string>(s)
            : _inner is IXmlNamespaceResolver r ? r.GetNamespacesInScope(scope)
            : new Dictionary<string, string>();

    string? IXmlNamespaceResolver.LookupNamespace(string prefix) => LookupNamespace(prefix);

    string? IXmlNamespaceResolver.LookupPrefix(string namespaceName)
        => _inner is IXmlNamespaceResolver r ? r.LookupPrefix(namespaceName) : null;

    bool IXmlLineInfo.HasLineInfo() => Current is not null || (_inner as IXmlLineInfo)?.HasLineInfo() == true;
    int IXmlLineInfo.LineNumber => Current?.LineNumber ?? (_inner as IXmlLineInfo)?.LineNumber ?? 0;
    int IXmlLineInfo.LinePosition => Current?.LinePosition ?? (_inner as IXmlLineInfo)?.LinePosition ?? 0;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
