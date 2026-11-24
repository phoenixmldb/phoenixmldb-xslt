using System.Text;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// An <c>xsl:copy</c> of a streamed element that is open while a streamed grouping in it runs,
/// and may send its start tag and the content so far to the writer between groups (BUGS #140).
/// </summary>
/// <remarks>
/// The start tag of an element is written when its content is complete, because attributes and
/// namespace nodes may be added until the first child. While streaming, a rule such as
/// <c>&lt;xsl:copy&gt;&lt;xsl:for-each-group select="item!copy-of()" …/&gt;&lt;/xsl:copy&gt;</c>
/// for the root element therefore held the whole result until the input ended, so memory grew
/// with the input. Once the content has a child, no attribute can follow (XTDE0410), and the
/// start tag is final.
/// </remarks>
internal sealed class CommittableElementFrame
{
    /// <summary>The content of the <c>xsl:copy</c>; the grouping is one of its instructions.</summary>
    public required XsltSequenceConstructor Content { get; init; }

    /// <summary>The variable-scope depth inside the content: the grouping runs at this depth.</summary>
    public required int ScopeDepth { get; init; }

    /// <summary>The output namespace-scope depth inside the content; the top two are the element's.</summary>
    public required int NsScopeDepth { get; init; }

    /// <summary>The attribute-collection depth inside the content; the top one is the element's.</summary>
    public required int AttributeDepth { get; init; }

    public required string ElemName { get; init; }

    /// <summary>The element's namespace bindings, as the start tag at its end would read them.</summary>
    public required Dictionary<string, string> NsBindings { get; init; }

    /// <summary>The buffer of the element's content, from its start.</summary>
    public required XsltTransformEngine.ScopedOutputBuffer Scope { get; init; }

    /// <summary>True once the start tag and the content so far were written and sent on.</summary>
    public bool Committed { get; set; }
}

internal sealed partial class DefaultXsltExecutionContext
{
    /// <summary>
    /// The frame for a streamed <c>xsl:copy</c> of an element that may be committed between
    /// groups, or null. Only for the one shape whose output buffers are known: the
    /// <c>xsl:copy</c> is an instruction of the body of a rule the streaming processor itself
    /// dispatched with nothing open around it, it writes text (no tree is being built, no
    /// validation, no sequence being collected), and the element is open for the processor to
    /// close at the source element's end. Called before the element's namespace scopes are pushed.
    /// </summary>
    private CommittableElementFrame? OpenCommittableFrame(XsltCopy instruction, string elemName,
        Dictionary<string, string> nsBindings, XsltTransformEngine.ScopedOutputBuffer scope)
    {
        if (_drainableBody is not { } body || _scopes.Count != _drainableScopeDepth
            || instruction.Content is not { } content
            || !ContainsInstruction(body, instruction)
            || _streamingOutputSink == null || _streamingOutputHeld > 0
            || !_isStreamingExecution || _activeStreamingReader == null || _streamingDispatchElementMaterialized
            || _activeTreeConstructor != null || _sequenceAccumulator != null || _currentAsBodyCapture != null
            || _temporaryOutputDepth != 0 || _textContentDepth != 0 || _attributeContentDepth != 0
            || _wherePopulatedDepth != 0 || IsTrackingPopulated
            || ShouldRunValidation(instruction.Validation) || instruction.ValidationType != null
            || HasEmptinessInstruction(content))
            return null;
        return new CommittableElementFrame
        {
            Content = content,
            // The element pushes one variable scope, two namespace scopes and one attribute
            // collection before its content runs.
            ScopeDepth = _scopes.Count + 1,
            NsScopeDepth = _outputNsScopes.Count + 2,
            AttributeDepth = _collectedAttributesStack.Count,
            ElemName = elemName,
            NsBindings = nsBindings,
            Scope = scope,
        };
    }

    private static bool ContainsInstruction(XsltSequenceConstructor body, XsltInstruction instruction)
    {
        foreach (var candidate in body.Instructions)
        {
            if (ReferenceEquals(candidate, instruction))
                return true;
        }
        return false;
    }

    // xsl:on-empty and xsl:on-non-empty decide on the whole content, which is then not final
    // until it ends.
    private static bool HasEmptinessInstruction(XsltSequenceConstructor content)
    {
        foreach (var candidate in content.Instructions)
        {
            if (candidate is XsltOnEmpty or XsltOnNonEmpty)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Between two groups of a streamed grouping: when the grouping is an instruction of the
    /// content of an open committable element and nothing else is open, the element's start tag
    /// and its content so far go to the writer (BUGS #140). Before the content has a child the
    /// element waits: an attribute may still come.
    /// </summary>
    private async ValueTask DrainBetweenGroupsAsync(XsltForEachGroup instruction)
    {
        if (_committableFrame is not { } frame
            || _streamingOutputSink == null || _streamingOutputHeld > 0
            || _scopes.Count != frame.ScopeDepth
            || _outputNsScopes.Count != frame.NsScopeDepth
            || _collectedAttributesStack.Count != frame.AttributeDepth
            || !ContainsInstruction(frame.Content, instruction)
            || _activeTreeConstructor != null || _sequenceAccumulator != null || _currentAsBodyCapture != null
            || _temporaryOutputDepth != 0 || _textContentDepth != 0 || _attributeContentDepth != 0
            || _wherePopulatedDepth != 0 || IsTrackingPopulated)
            return;

        if (!frame.Committed)
        {
            if (frame.Scope.WrittenLength == 0)
                return;
            var content = frame.Scope.GetWritten();
            frame.Scope.Dispose();
            WriteCommittedStartTag(frame);
            _sink.StartElementClose(false);
            _sink.RawText(content);
            frame.Committed = true;
        }

        await DrainStreamingOutputAsync(_activeStreamingCancellationToken).ConfigureAwait(false);
        // The open element has children that were sent on: an attribute or namespace node
        // that came now is XTDE0410 (every check compares the buffer's length with this).
        _outputLogicalStart = -1;
    }

    // The start tag as the end of xsl:copy writes it (CopyCoreAsync), judged against the
    // namespace scopes outside the element: its own two are the top of the stack.
    private void WriteCommittedStartTag(CommittableElementFrame frame)
    {
        _sink.StartElementOpen(frame.ElemName);
        foreach (var (prefix, uri) in frame.NsBindings)
        {
            if (IsNamespaceInScopeOutside(prefix, uri, skip: 2))
                continue;
            _sink.Namespace(prefix, uri);
        }
        var attrs = new Dictionary<string, string>();
        CheckAttrGuard(_collectedAttributesStack.Peek(), "streamed xsl:copy, committed");
        ReadCollectedAttributes(_collectedAttributesStack.Peek(), attrs, frame.ElemName, elementNamespace: null);
        DeduplicateByExpandedName(attrs, frame.ElemName);
        foreach (var (attrName, attrValue) in attrs)
        {
            _output.Append(' ');
            _output.Append(attrName);
            _output.Append("=\"");
            _output.Append(attrValue);
            _output.Append('"');
        }
    }

    private bool IsNamespaceInScopeOutside(string prefix, string uri, int skip)
    {
        foreach (var scope in _outputNsScopes)
        {
            if (skip > 0)
            {
                skip--;
                continue;
            }
            if (scope.TryGetValue(prefix, out var scopedUri))
                return scopedUri == uri;
        }
        return false;
    }
}
