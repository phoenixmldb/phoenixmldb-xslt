using System.Globalization;
using System.Text;
using System.Threading.Channels;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Serializes a principal result that the transformation writes to the caller's writer while it
/// streams: <c>XsltTransformer.TransformAsync</c> with a stream or reader input and a writer, and
/// the command line's file and standard output. The text goes through the steps
/// <see cref="XsltTransformEngine.FinalizeOutput"/> applies to a whole result, a piece at a time:
/// indentation, the html and xhtml forms, the Content-Type meta, character maps, Unicode
/// normalization, references for characters the encoding cannot represent, the XML declaration,
/// the DOCTYPE, the byte-order mark, URI attributes and raw-text elements. The streamed result
/// skipped all of them, so <c>indent="yes"</c>, a doctype or a character map had no effect
/// (xslt#575).
/// </summary>
/// <remarks>
/// <para>
/// Memory stays bounded by what a step must see whole: the result up to the document element's
/// start tag (xhtml, a DOCTYPE), one tag, one script or style element, one head (the Content-Type
/// meta), the text between two tags. Never the result.
/// </para>
/// <para>
/// Where it differs from a whole result: a result of more than one top-level element is indented,
/// where a whole one is left unindented; a stylesheet with <c>disable-output-escaping="yes"</c>
/// and indentation keeps the result whole and serializes it at the end (it may not be
/// well-formed, and is then left unindented as a whole result is); SEPM0004 and SERE0015 are
/// raised where the result breaks the rule, after what came before is written; the output
/// declaration and character maps are those known when the transformation starts.
/// </para>
/// </remarks>
internal sealed class StreamedResultFinalizer : TextWriter
{
    private readonly XsltTransformEngine _owner;
    private readonly XsltOutput? _decl;
    private readonly IReadOnlyList<QName>? _resultDocCharacterMaps;
    private readonly IReadOnlySet<OutputMethod>? _allowedMethods;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
        Justification = "The caller's writer: the caller disposes it.")]
    private readonly TextWriter _sink;
    private readonly CancellationToken _cancellation;

    // disable-output-escaping and indentation: the result is kept and serialized at the end.
    private readonly StringBuilder? _whole;

    // The xhtml method looks at the document element before anything is written: held until its start tag ends.
    private StringBuilder? _gate;

    private ResultWriter.Stage? _head;
    private ResultShapeTracker? _shape;
    private readonly PendingBuffer _pending = new();

    // The xml and xhtml indentation reads the markup with an XmlReader, which pulls; it runs as
    // its own task over a bounded channel, and writes everything after it.
    private Channel<string>? _toIndent;
    private StringBuilder? _indentInput;
    private Task? _pump;

    // What the steps write goes to the caller's writer (or to the indentation) in pieces of at
    // least this many characters: a write per event cost three asynchronous calls and their state
    // each time the writer did not finish at once (a file), more than the steps themselves.
    private const int WriteThreshold = 8192;
    private const int SliceSize = 16384;

    /// <summary>When set, the markup as the engine wrote it, before the steps (the W3C harness's assertions on the result tree).</summary>
    internal StringBuilder? Markup { get; init; }

    /// <summary>The most characters any step held at once (tests: memory is bounded).</summary>
    internal int MaxHeld { get; private set; }

    public StreamedResultFinalizer(XsltTransformEngine owner, XsltOutput? decl, IReadOnlyList<QName>? resultDocCharacterMaps,
        IReadOnlySet<OutputMethod>? allowedMethods, bool keepWhole, TextWriter sink, CancellationToken cancellation)
    {
        _owner = owner;
        _decl = decl;
        _resultDocCharacterMaps = resultDocCharacterMaps;
        _allowedMethods = allowedMethods;
        _sink = sink;
        _cancellation = cancellation;
        if (keepWhole)
            _whole = new StringBuilder();
        else if (decl?.EffectiveMethod == OutputMethod.Xhtml)
            _gate = new StringBuilder();
        else
            Open("");
    }

    /// <summary>Whether a streamed result with this declaration is kept whole: it is indented, and the stylesheet may write markup that is not well-formed.</summary>
    internal static bool KeepsWhole(XsltOutput? decl, XsltStylesheet stylesheet)
        => stylesheet.UsesDisableOutputEscaping
           && decl is { EffectiveMethod: OutputMethod.Xml or OutputMethod.Xhtml }
           && (decl.Indent ?? decl.EffectiveMethod == OutputMethod.Xhtml);

    public override Encoding Encoding => _sink.Encoding;

    public override void Write(char value) => throw SyncWrite();
    public override void Write(char[] buffer, int index, int count) => throw SyncWrite();
    public override void Write(string? value) => throw SyncWrite();

    private static NotSupportedException SyncWrite()
        => new("The streamed result is written asynchronously (WriteAsync).");

    public override Task WriteAsync(string? value)
        => value is null ? Task.CompletedTask : WriteAsync(value.AsMemory(), _cancellation);

    public override Task WriteAsync(char[] buffer, int index, int count)
        => WriteAsync(buffer.AsMemory(index, count), _cancellation);

    public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
            return Task.CompletedTask;
        Markup?.Append(buffer.Span);
        if (_whole is not null)
        {
            _whole.Append(buffer.Span);
            return Task.CompletedTask;
        }
        if (buffer.Length > SliceSize)
            return WriteSlicesAsync(buffer, cancellationToken);
        return WriteSlice(buffer.Span) ? AfterWriteAsync(cancellationToken) : Task.CompletedTask;
    }

    // What the engine writes at once can be the whole result (it holds back what is written in
    // an element still open: xslt BUGS #140). It goes through the steps a slice at a time, and is
    // written on between slices, so that no step copies it whole.
    private async Task WriteSlicesAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken)
    {
        for (var at = 0; at < buffer.Length; at += SliceSize)
        {
            if (WriteSlice(buffer.Span.Slice(at, Math.Min(SliceSize, buffer.Length - at))))
                await AfterWriteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // Gives text to the steps; false while the xhtml method still holds the start of the result.
    private bool WriteSlice(ReadOnlySpan<char> text)
    {
        if (_gate is not null)
        {
            _gate.Append(text);
            Held(_gate.Length);
            var held = _gate.ToString();
            var root = XsltTransformEngine.FindDocumentElementStart(held, 0);
            if (root < 0 || held.IndexOf('>', root) < 0)
                return false;
            _gate = null;
            Open(held);
            return true;
        }
        _shape?.Write(text);
        _head!.Write(text);
        return true;
    }

    private Task AfterWriteAsync(CancellationToken cancellationToken)
    {
        if (_toIndent is not null)
            return _indentInput!.Length >= WriteThreshold ? SendToIndentAsync(cancellationToken) : Task.CompletedTask;
        return _pending.Length >= WriteThreshold ? FlushPendingAsync(cancellationToken) : Task.CompletedTask;
    }

    /// <summary>Writes the rest of the result: what the steps still hold, and anything held for the end.</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_whole is not null)
        {
            var output = _owner.FinalizeOutput(_whole.ToString(), _decl, _resultDocCharacterMaps,
                XsltTransformEngine.FinalizeKind.StreamingBuffered, _allowedMethods);
            await _sink.WriteAsync(output.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (_gate is not null)
        {
            var held = _gate.ToString();
            _gate = null;
            Open(held);
        }
        _shape?.Finish();
        _head!.Finish();
        if (_toIndent is not null)
        {
            await SendToIndentAsync(cancellationToken).ConfigureAwait(false);
            _toIndent.Writer.TryComplete();
            await _pump!.ConfigureAwait(false);
        }
        else
            await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
        await _sink.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Held(int chars)
    {
        if (chars > MaxHeld)
            MaxHeld = chars;
    }

    /// <summary>Decides the steps from the declaration and <paramref name="held"/>, the start of the result, and writes it to them.</summary>
    private void Open(string held)
    {
        var plan = _owner.PlanFinalize(held, _decl, _resultDocCharacterMaps, XsltTransformEngine.FinalizeKind.StreamingSink,
            _allowedMethods, explicitResultDocument: false);
        _shape = ResultShapeTracker.For(plan.Decl, this);

        var final = new FinalStage(plan, _pending);
        var prolog = new PrologStage(plan, final, this);
        var content = new ContentStage(_owner, plan, prolog);
        var method = plan.Decl?.EffectiveMethod;
        if (plan.Decl is null)
            _head = new Segmenter(content, htmlRawText: false, this);
        else if (method == OutputMethod.Text)
        {
            ResultWriter.Stage strip = plan.Kind == XsltTransformEngine.FinalizeKind.ResultDocument ? content : new StripStage(content);
            _head = new Segmenter(strip, htmlRawText: false, this);
        }
        else
        {
            var steps = XsltTransformEngine.MarkupSteps(plan);
            var segmenter = new Segmenter(content, htmlRawText: method == OutputMethod.Html, this);
            if (steps.Indent && method is OutputMethod.Xml or OutputMethod.Xhtml)
            {
                _indentInput = new StringBuilder();
                _toIndent = Channel.CreateBounded<string>(new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true });
                _head = ResultWriter.BuildStages(steps, new PendingStage(_indentInput), steps.RedefaultNamespaces);
                _pump = IndentAsync(steps.SuppressIndentation, method == OutputMethod.Xhtml, segmenter);
            }
            else
                _head = ResultWriter.BuildStages(steps, segmenter, steps.RedefaultNamespaces);
        }
        if (held.Length > 0)
        {
            _shape?.Write(held);
            _head.Write(held);
        }
    }

    private async Task SendToIndentAsync(CancellationToken cancellationToken)
    {
        if (_indentInput!.Length == 0)
            return;
        var text = _indentInput.ToString();
        _indentInput.Clear();
        try
        {
            await _toIndent!.Writer.WriteAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // The indentation ended with an error: that error is the one to report.
            await _pump!.ConfigureAwait(false);
            throw;
        }
    }

    private async Task FlushPendingAsync(CancellationToken cancellationToken)
    {
        if (_pending.Length == 0)
            return;
        Held(_pending.Length);
        await _sink.WriteAsync(_pending.Memory, cancellationToken).ConfigureAwait(false);
        _pending.Clear();
    }

    /// <summary>
    /// The xml and xhtml indentation of the streamed markup: <see cref="XmlIndenter"/> over an
    /// asynchronous XmlReader that reads from the channel, as <see cref="XsltTransformEngine.ApplyXmlIndentation"/>
    /// drives it over a whole result. The markup that reaches it never starts with an XML
    /// declaration: the engine writes none to a streamed result (this class does, at the end of
    /// the steps), and a stylesheet cannot write one but with disable-output-escaping, which keeps
    /// the result whole.
    /// </summary>
    private async Task IndentAsync(HashSet<QName>? suppressIndentation, bool xhtml, ResultWriter.Stage next)
    {
        await Task.Yield();
        try
        {
            using var input = new ChannelTextReader(_toIndent!.Reader);
            using var reader = XmlReader.Create(input, XmlIndenter.ReaderSettings(streamed: true));
            var buffer = new StringBuilder();
            using var sw = new StringWriter(buffer, CultureInfo.InvariantCulture);
            using (var xw = XmlWriter.Create(sw, XmlIndenter.WriterSettings(streamed: true)))
            {
                var indenter = new XmlIndenter(xw, buffer, suppressIndentation, xhtml);
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var value = XmlIndenter.HasValue(reader.NodeType) ? await reader.GetValueAsync().ConfigureAwait(false) : null;
                    indenter.Node(reader, value);
#pragma warning disable CA1849 // The XmlWriter writes to a StringBuilder in memory: nothing blocks.
                    xw.Flush();
#pragma warning restore CA1849
                    await PassOnAsync(buffer, next).ConfigureAwait(false);
                }
            }
            await PassOnAsync(buffer, next).ConfigureAwait(false);
            next.Finish();
            await FlushPendingAsync(_cancellation).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _toIndent!.Writer.TryComplete(e);
            if (e is XmlException xml)
                throw new XsltException($"The streamed result is not well-formed and cannot be indented: {xml.Message}", xml);
            throw;
        }
    }

    private Task PassOnAsync(StringBuilder buffer, ResultWriter.Stage next)
    {
        if (buffer.Length > 0)
        {
            Held(buffer.Length);
            foreach (var chunk in buffer.GetChunks())
                next.Write(chunk.Span);
            buffer.Clear();
        }
        return _pending.Length >= WriteThreshold ? FlushPendingAsync(_cancellation) : Task.CompletedTask;
    }

    // ── stages after the markup steps ──

    /// <summary>What the steps wrote, for the caller's writer: one buffer, used again for each write.</summary>
    private sealed class PendingBuffer : ResultWriter.Stage
    {
        private char[] _buf = new char[4096];

        public int Length { get; private set; }

        public ReadOnlyMemory<char> Memory => _buf.AsMemory(0, Length);

        public override void Write(ReadOnlySpan<char> text)
        {
            if (Length + text.Length > _buf.Length)
                Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + text.Length));
            text.CopyTo(_buf.AsSpan(Length));
            Length += text.Length;
        }

        public override void Finish() { }

        public void Clear()
        {
            Length = 0;
            // One very large write (a long text) does not keep its buffer.
            if (_buf.Length > 1 << 20)
                _buf = new char[4096];
        }
    }

    private sealed class PendingStage(StringBuilder pending) : ResultWriter.Stage
    {
        public override void Write(ReadOnlySpan<char> text) => pending.Append(text);
        public override void Finish() { }
    }

    /// <summary>The text method's strip of the markup, a segment at a time.</summary>
    private sealed class StripStage(ResultWriter.Stage next) : ResultWriter.Stage
    {
        public override void Write(ReadOnlySpan<char> text) => next.Write(DefaultXsltExecutionContext.StripXmlMarkup(text.ToString()));
        public override void Finish() => next.Finish();
    }

    /// <summary>Character maps, normalization and unrepresentable characters, a segment at a time.</summary>
    private sealed class ContentStage(XsltTransformEngine owner, XsltTransformEngine.FinalizePlan plan, ResultWriter.Stage next) : ResultWriter.Stage
    {
        // Most results have none of them: the text goes on as it is, not copied into a string.
        private readonly bool _changesNothing = XsltTransformEngine.ContentStepsChangeNothing(plan);

        public override void Write(ReadOnlySpan<char> text)
        {
            if (_changesNothing)
                next.Write(text);
            else
                next.Write(owner.ApplyContentSteps(text.ToString(), plan));
        }

        public override void Finish() => next.Finish();
    }

    /// <summary>URI attributes, sentinels and raw-text elements, a segment at a time.</summary>
    private sealed class FinalStage(XsltTransformEngine.FinalizePlan plan, ResultWriter.Stage next) : ResultWriter.Stage
    {
        // URI attributes and raw-text elements are the html and xhtml methods'; a sentinel is in the text or not.
        private readonly bool _htmlLike = plan.Decl?.EffectiveMethod is OutputMethod.Html or OutputMethod.Xhtml;

        public override void Write(ReadOnlySpan<char> text)
        {
            if (!_htmlLike && text.IndexOf('\uFDD0') < 0)
                next.Write(text);
            else
                next.Write(XsltTransformEngine.ApplyFinalSteps(text.ToString(), plan));
        }

        public override void Finish() => next.Finish();
    }

    /// <summary>
    /// The XML declaration, the DOCTYPE and the byte-order mark: the start of the result is held
    /// until what they depend on is known — the document element's start tag for a DOCTYPE, the
    /// first characters for the declaration — and goes on with them.
    /// </summary>
    private sealed class PrologStage(XsltTransformEngine.FinalizePlan plan, ResultWriter.Stage next, StreamedResultFinalizer owner) : ResultWriter.Stage
    {
        private StringBuilder? _held = new();
        private readonly bool _needsRoot = NeedsDocumentElement(plan);

        private static bool NeedsDocumentElement(XsltTransformEngine.FinalizePlan plan)
        {
            var decl = plan.Decl;
            if (decl is null)
                return false;
            if (!string.IsNullOrEmpty(decl.DoctypeSystem))
                return decl.EffectiveMethod != OutputMethod.Text;
            return !plan.ForeignXhtmlRoot && decl.EffectiveMethod is OutputMethod.Html or OutputMethod.Xhtml
                && XsltTransformEngine.Html5DoctypeApplies(decl);
        }

        public override void Write(ReadOnlySpan<char> text)
        {
            if (_held is null)
            {
                next.Write(text);
                return;
            }
            _held.Append(text);
            owner.Held(_held.Length);
            var s = _held.ToString();
            var decided = _needsRoot
                ? XsltTransformEngine.FindDocumentElementStart(s, 0) >= 0
                : s.Length >= 5 || !"<?xml".AsSpan().StartsWith(s, StringComparison.Ordinal);
            if (decided)
                Release(s);
        }

        private void Release(string s)
        {
            _held = null;
            next.Write(XsltTransformEngine.ApplyProlog(s, plan));
        }

        public override void Finish()
        {
            if (_held is not null)
                Release(_held.ToString());
            next.Finish();
        }
    }

    /// <summary>
    /// Cuts the markup into pieces that end just after an element's start or end tag, outside a
    /// comment, CDATA section, processing instruction and (html) script or style element: there,
    /// each later step is where it is at the start of a result, so a step applied to each piece
    /// gives what it gives applied to the whole.
    /// </summary>
    private sealed class Segmenter(ResultWriter.Stage next, bool htmlRawText, StreamedResultFinalizer owner) : ResultWriter.BufferedStage(next)
    {
        private enum State { Text, Tag, Comment, CData, Pi, Declaration, RawText }

        private State _state;
        private char _quote;
        private int _scanned;          // how far the held text is scanned, with _state at that point
        private int _tagStart;         // where the tag being scanned starts
        private string? _rawTextName;  // the raw-text element whose content is being scanned

        protected override int Process(ReadOnlySpan<char> s, bool final)
        {
            owner.Held(s.Length);
            var cut = 0;
            var i = _scanned;
            while (i < s.Length)
            {
                var c = s[i];
                switch (_state)
                {
                    case State.Text:
                        if (c != '<')
                        {
                            i++;
                            break;
                        }
                        var rest = s[i..];
                        if (rest.Length < 9 && !final && ("<!--".AsSpan().StartsWith(rest) || "<![CDATA[".AsSpan().StartsWith(rest) || rest.Length < 2))
                            goto done;
                        if (rest.StartsWith("<!--", StringComparison.Ordinal)) { _state = State.Comment; i += 4; }
                        else if (rest.StartsWith("<![CDATA[", StringComparison.Ordinal)) { _state = State.CData; i += 9; }
                        else if (rest.Length > 1 && rest[1] == '!') { _state = State.Declaration; i += 2; }
                        else if (rest.Length > 1 && rest[1] == '?') { _state = State.Pi; i += 2; }
                        else { _state = State.Tag; _tagStart = i; _quote = '\0'; i++; }
                        break;
                    case State.Tag:
                        if (_quote != '\0')
                        {
                            if (c == _quote)
                                _quote = '\0';
                            i++;
                            break;
                        }
                        if (c is '"' or '\'')
                        {
                            _quote = c;
                            i++;
                            break;
                        }
                        i++;
                        if (c != '>')
                            break;
                        _state = State.Text;
                        if (htmlRawText && s[_tagStart + 1] != '/' && s[i - 2] != '/' && RawTextName(s, _tagStart + 1) is { } name)
                        {
                            _rawTextName = name;
                            _state = State.RawText;
                            break;
                        }
                        cut = i;
                        break;
                    case State.Comment:
                        if (c == '>' && i >= 2 && s[i - 1] == '-' && s[i - 2] == '-')
                            _state = State.Text;
                        i++;
                        break;
                    case State.CData:
                        if (c == '>' && i >= 2 && s[i - 1] == ']' && s[i - 2] == ']')
                            _state = State.Text;
                        i++;
                        break;
                    case State.Pi:
                        if (c == '>' && i >= 1 && s[i - 1] == '?')
                            _state = State.Text;
                        i++;
                        break;
                    case State.Declaration:
                        if (c == '>')
                            _state = State.Text;
                        i++;
                        break;
                    case State.RawText:
                        // The content ends at the first "</name", case-insensitively; the end tag is then an ordinary tag.
                        if (c == '<')
                        {
                            var endTag = "</" + _rawTextName;
                            if (s.Length - i < endTag.Length && !final)
                                goto done;
                            if (s[i..].StartsWith(endTag, StringComparison.OrdinalIgnoreCase))
                            {
                                _state = State.Tag;
                                _tagStart = i;
                                _quote = '\0';
                                _rawTextName = null;
                            }
                        }
                        i++;
                        break;
                }
            }
        done:
            if (final)
            {
                Next.Write(s);
                _scanned = 0;
                return s.Length;
            }
            if (cut > 0)
                Next.Write(s[..cut]);
            _scanned = i - cut;
            _tagStart -= cut;
            return cut;
        }

        private static string? RawTextName(ReadOnlySpan<char> s, int pos)
        {
            foreach (var name in (ReadOnlySpan<string>)["script", "style"])
            {
                if (pos + name.Length <= s.Length && s.Slice(pos, name.Length).Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    var after = pos + name.Length;
                    if (after >= s.Length || s[after] is ' ' or '\t' or '\n' or '\r' or '>' or '/')
                        return name;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// SEPM0004 (standalone or doctype-system and not one document element) and SERE0015 (a '&gt;'
    /// in a processing instruction of the html method), checked on the markup as it is written,
    /// by the rules of <see cref="XsltTransformEngine.ValidateResultShape"/>.
    /// </summary>
    private sealed class ResultShapeTracker : ResultWriter.BufferedStage
    {
        private readonly bool _singleElement;
        private readonly bool _htmlPi;
        private readonly StreamedResultFinalizer _owner;
        private int _depth;
        private int _topElements;

        private ResultShapeTracker(bool singleElement, bool htmlPi, StreamedResultFinalizer owner) : base(new PendingStage(new StringBuilder()))
        {
            _singleElement = singleElement;
            _htmlPi = htmlPi;
            _owner = owner;
        }

        public static ResultShapeTracker? For(XsltOutput? decl, StreamedResultFinalizer owner)
        {
            if (decl is null)
                return null;
            var single = decl.EffectiveMethod is OutputMethod.Xml or OutputMethod.Xhtml && (decl.Standalone.HasValue || decl.DoctypeSystem != null);
            var htmlPi = decl.EffectiveMethod == OutputMethod.Html;
            return single || htmlPi ? new ResultShapeTracker(single, htmlPi, owner) : null;
        }

        private static XsltException NotOneElement()
            => new("SEPM0004: standalone or doctype-system requires a result that is a single well-formed document element");

        protected override int Process(ReadOnlySpan<char> s, bool final)
        {
            _owner.Held(s.Length);
            var i = 0;
            var n = s.Length;
            while (i < n)
            {
                var c = s[i];
                if (c != '<')
                {
                    if (_singleElement && _depth == 0 && !char.IsWhiteSpace(c))
                        throw NotOneElement();
                    i++;
                    continue;
                }
                var rest = s[i..];
                if (!final && rest.Length < 9 && ("<!--".AsSpan().StartsWith(rest) || "<![CDATA[".AsSpan().StartsWith(rest) || rest.Length < 2))
                    return i;
                int end;
                if (rest.StartsWith("<!--", StringComparison.Ordinal))
                    end = Find(s, i + 4, "-->");
                else if (rest.StartsWith("<![CDATA[", StringComparison.Ordinal))
                {
                    if (_singleElement && _depth == 0)
                        throw NotOneElement();
                    end = Find(s, i + 9, "]]>");
                }
                else if (rest.Length > 1 && rest[1] == '!')
                    end = Find(s, i, ">");
                else if (rest.Length > 1 && rest[1] == '?')
                {
                    end = Find(s, i + 2, "?>");
                    if (end >= 0 && _htmlPi && s[(i + 2)..(end - 2)].IndexOf('>') >= 0)
                        throw new XsltException(
                            "SERE0015: a '>' character appears within a processing instruction serialized with the HTML output method");
                }
                else if (rest.Length > 1 && rest[1] == '/')
                {
                    end = Find(s, i, ">");
                    if (end >= 0)
                        _depth--;
                }
                else
                {
                    end = TagEnd(s, i);
                    if (end >= 0)
                    {
                        if (_depth == 0 && ++_topElements > 1 && _singleElement)
                            throw NotOneElement();
                        if (s[end - 2] != '/')
                            _depth++;
                    }
                }
                if (end < 0)
                {
                    if (!final)
                        return i;
                    break;
                }
                i = end;
            }
            if (final && _singleElement && _topElements != 1)
                throw NotOneElement();
            return final ? n : i;
        }

        // The index just after the first `token` at or after `from`, or -1.
        private static int Find(ReadOnlySpan<char> s, int from, string token)
        {
            if (from > s.Length)
                return -1;
            var at = s[from..].IndexOf(token, StringComparison.Ordinal);
            return at < 0 ? -1 : from + at + token.Length;
        }

        // The index just after the '>' that ends the start tag at `start`, quotes skipped, or -1.
        private static int TagEnd(ReadOnlySpan<char> s, int start)
        {
            var te = start + 1;
            while (te < s.Length && s[te] != '>')
            {
                if (s[te] is '"' or '\'')
                {
                    var q = s[te];
                    te++;
                    while (te < s.Length && s[te] != q)
                        te++;
                }
                te++;
            }
            return te < s.Length ? te + 1 : -1;
        }
    }

    /// <summary>A reader of the channel's text, for the asynchronous XmlReader of the indentation.</summary>
    private sealed class ChannelTextReader(ChannelReader<string> source) : TextReader
    {
        private string _current = "";
        private int _pos;

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            while (_pos >= _current.Length)
            {
                if (!await source.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0;
                if (source.TryRead(out var next))
                {
                    _current = next;
                    _pos = 0;
                }
            }
            var n = Math.Min(buffer.Length, _current.Length - _pos);
            _current.AsSpan(_pos, n).CopyTo(buffer.Span);
            _pos += n;
            return n;
        }

        public override Task<int> ReadAsync(char[] buffer, int index, int count)
            => ReadAsync(buffer.AsMemory(index, count)).AsTask();

        public override int Read(char[] buffer, int index, int count)
            => throw new NotSupportedException("The streamed result is read asynchronously.");

        public override int Read() => throw new NotSupportedException("The streamed result is read asynchronously.");

        public override int Peek() => -1;
    }
}
