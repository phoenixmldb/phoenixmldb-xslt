using System.Text;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Writes the finished markup of a result in the form its output method gives it, in one pass:
/// the XHTML/SVG/MathML prefixes are taken off the element names, empty and void elements get
/// the html or xhtml form, the Content-Type meta goes in each head, and the result is indented
/// (#400). Each step is a stage that reads the text of the one before it a piece at a time and
/// writes on to the next, so no step makes a whole copy of the result; they used to be five
/// passes, each over the whole text and each making a copy of it.
/// </summary>
/// <remarks>
/// The result tree is not indented while the transformation writes it: that buffer is also a
/// work area, cut back and read again for temporary trees and variable values, so text in it
/// is final only when the transformation ends. Each stage gives the bytes the pass it replaces
/// gave (they were compared on every result of the W3C suite and the unit tests, and on random
/// markup); the xml and xhtml indentation is still XmlWriter's.
/// </remarks>
internal static class ResultWriter
{
    /// <summary>What to do to the markup.</summary>
    internal readonly record struct Steps(
        OutputMethod Method,
        bool RedefaultNamespaces,
        bool HtmlForms,
        XsltOutput? ContentTypeMeta,
        bool Indent,
        HashSet<QName>? SuppressIndentation,
        IndentStyle IndentStyle = IndentStyle.Default,
        HashSet<QName>? CdataSectionElements = null);

    internal static string Write(string markup, in Steps steps) => Write(markup, steps, chunk: 0);

    /// <summary>With <paramref name="chunk"/> above 0, the markup goes to the stages that many characters at a time (tests).</summary>
    internal static string Write(string markup, in Steps steps, int chunk)
    {
        var xmlIndent = steps.Indent && steps.Method is OutputMethod.Xml or OutputMethod.Xhtml;
        var htmlIndent = steps.Indent && steps.Method == OutputMethod.Html;
        var redefault = steps.RedefaultNamespaces && markup.Contains("xmlns:", StringComparison.Ordinal);

        if (!redefault && !steps.HtmlForms && steps.ContentTypeMeta is null && !htmlIndent)
            return xmlIndent ? XsltTransformEngine.ApplyXmlIndentation(markup, steps.SuppressIndentation, steps.Method == OutputMethod.Xhtml, steps.IndentStyle, steps.CdataSectionElements) : markup;

        var sink = new Sink();
        Stage C(Stage stage) => chunk > 0 ? new Chunker(stage, chunk) : stage;
        var next = C(BuildStages(steps, sink, redefault, chunk));
        next.Write(markup);
        next.Finish();

        var text = sink.ToString();
        // XmlWriter's indentation reads the text the other steps wrote: an XmlReader needs it
        // whole, and when it is not well-formed the result is that text, unindented.
        return xmlIndent ? XsltTransformEngine.ApplyXmlIndentation(text, steps.SuppressIndentation, steps.Method == OutputMethod.Xhtml, steps.IndentStyle, steps.CdataSectionElements) : text;
    }

    /// <summary>
    /// The stages of <paramref name="steps"/> other than the xml and xhtml indentation, in front of
    /// <paramref name="sink"/>: the first one to write the markup to. With <paramref name="redefault"/>
    /// the XHTML, SVG and MathML prefixes are taken off first. A result written while the
    /// transformation streams goes through the same stages (StreamedResultFinalizer).
    /// </summary>
    internal static Stage BuildStages(in Steps steps, Stage sink, bool redefault, int chunk = 0)
    {
        var htmlIndent = steps.Indent && steps.Method == OutputMethod.Html;
        Stage C(Stage stage) => chunk > 0 ? new Chunker(stage, chunk) : stage;
        var next = sink;
        if (htmlIndent)
        {
            if (steps.SuppressIndentation is { Count: > 0 })
                next = C(new SuppressIndentationStage(next, steps.SuppressIndentation));
            next = C(new HtmlIndentStage(next));
        }
        if (steps.ContentTypeMeta is not null)
            next = C(new ContentTypeMetaStage(next, steps.ContentTypeMeta));
        if (steps.HtmlForms && steps.Method == OutputMethod.Html)
            next = C(new BooleanAttributeStage(next));
        if (steps.HtmlForms)
            next = C(new HtmlFormsStage(next, steps.Method == OutputMethod.Xhtml));
        if (redefault)
            next = C(new RedefaultNamespacesStage(next));
        return next;
    }

    // ── stages ──

    internal abstract class Stage
    {
        public abstract void Write(ReadOnlySpan<char> text);
        public void Write(char c) => Write(new ReadOnlySpan<char>(in c));
        public abstract void Finish();
    }

    private sealed class Chunker(Stage next, int size) : Stage
    {
        public override void Write(ReadOnlySpan<char> text)
        {
            for (var i = 0; i < text.Length; i += size)
                next.Write(text.Slice(i, Math.Min(size, text.Length - i)));
        }
        public override void Finish() => next.Finish();
    }

    private sealed class Sink : Stage
    {
        // Not sized for the result: one buffer that size is a large-object-heap array (#411).
        private readonly StringBuilder _sb = new();
        public override void Write(ReadOnlySpan<char> text) => _sb.Append(text);
        public override void Finish() { }
        public override string ToString() => _sb.ToString();
    }

    /// <summary>
    /// A stage that keeps what it has read and not yet written. It processes what it can decide
    /// from the text it has, and keeps the rest (an unfinished tag, a head up to its end tag)
    /// for the next piece, or for the end, where it does what the whole-text pass did there.
    /// </summary>
    internal abstract class BufferedStage(Stage next) : Stage
    {
        protected readonly Stage Next = next;
        private char[] _buf = new char[256];
        private int _len;

        public sealed override void Write(ReadOnlySpan<char> text)
        {
            if (_len + text.Length > _buf.Length)
                Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + text.Length));
            text.CopyTo(_buf.AsSpan(_len));
            _len += text.Length;
            Consume(Process(_buf.AsSpan(0, _len), final: false));
        }

        public sealed override void Finish()
        {
            Consume(Process(_buf.AsSpan(0, _len), final: true));
            Next.Finish();
        }

        private void Consume(int used)
        {
            if (used <= 0) return;
            _buf.AsSpan(used, _len - used).CopyTo(_buf);
            _len -= used;
            // A head held for its end tag can be large; do not keep its buffer after it. A buffer
            // of up to 64K is kept: a streamed result comes in pieces of a few kilobytes, and its
            // buffer was made again, larger and then smaller, for every one of them (xslt#575).
            if (_buf.Length > 1 << 16 && _len < 1024)
                Array.Resize(ref _buf, Math.Max(256, _len));
        }

        /// <summary>Writes what it can of <paramref name="s"/>; returns how much it used.</summary>
        protected abstract int Process(ReadOnlySpan<char> s, bool final);
    }

    // ── 1. prefixes off XHTML, SVG and MathML element names ──

    /// <summary>
    /// An element bound to the XHTML, SVG or MathML namespace by a prefix is written with its
    /// local name and, where needed, a default namespace declaration; declarations of those three
    /// namespaces are dropped, and one is declared again for a prefixed attribute that needs it
    /// (Serialization 3.0, XHTML output method; W3C output-0211/0221/0225/0226). It reads the
    /// markup a token at a time, keeping an unfinished one for the next piece, so it also serves a
    /// result that is written while the transformation streams (xslt#575).
    /// </summary>
    private sealed class RedefaultNamespacesStage(Stage next) : BufferedStage(next)
    {
        private readonly List<Dictionary<string, string>> _inputScopes = new();
        private readonly List<string> _outDefaults = new();
        private readonly List<Dictionary<string, string>> _outPrefixDecls = new();
        private readonly List<string> _openNames = new();

        private static bool IsWs(char c) => c == ' ' || c == '\t' || c == '\n' || c == '\r';

        // Whether the rest could still become this token with more text.
        private static bool MayBecome(ReadOnlySpan<char> rest, string token)
            => rest.Length < token.Length && token.AsSpan().StartsWith(rest, StringComparison.Ordinal);

        protected override int Process(ReadOnlySpan<char> s, bool final)
        {
            int i = 0, n = s.Length;
            while (i < n)
            {
                char c = s[i];
                if (c != '<')
                {
                    var lt = s[i..].IndexOf('<');
                    var end = lt < 0 ? n : i + lt;
                    Next.Write(s[i..end]);
                    i = end;
                    continue;
                }

                if (!final && (i + 1 >= n || MayBecome(s[i..], "<!--") || MayBecome(s[i..], "<![CDATA[")))
                    return i;
                char c1 = i + 1 < n ? s[i + 1] : '\0';
                if (c1 == '!')
                {
                    if (s[i..].StartsWith("<!--", StringComparison.Ordinal))
                    {
                        int close = i + 4 <= n ? s[(i + 4)..].IndexOf("-->", StringComparison.Ordinal) : -1;
                        if (close < 0 && !final)
                            return i;
                        int end = close < 0 ? n : i + 4 + close + 3;
                        Next.Write(s[i..end]);
                        i = end;
                        continue;
                    }
                    if (s[i..].StartsWith("<![CDATA[", StringComparison.Ordinal))
                    {
                        int close = i + 9 <= n ? s[(i + 9)..].IndexOf("]]>", StringComparison.Ordinal) : -1;
                        if (close < 0 && !final)
                            return i;
                        int end = close < 0 ? n : i + 9 + close + 3;
                        Next.Write(s[i..end]);
                        i = end;
                        continue;
                    }
                    int gt0 = s[i..].IndexOf('>');
                    if (gt0 < 0 && !final)
                        return i;
                    gt0 = gt0 < 0 ? n : i + gt0 + 1;
                    Next.Write(s[i..gt0]);
                    i = gt0;
                    continue;
                }
                if (c1 == '?')
                {
                    int close = i + 2 <= n ? s[(i + 2)..].IndexOf("?>", StringComparison.Ordinal) : -1;
                    if (close < 0 && !final)
                        return i;
                    int end = close < 0 ? n : i + 2 + close + 2;
                    Next.Write(s[i..end]);
                    i = end;
                    continue;
                }
                if (c1 == '/')
                {
                    int gtAt = s[i..].IndexOf('>');
                    if (gtAt < 0)
                    {
                        if (!final)
                            return i;
                        Next.Write(s[i..n]);
                        i = n;
                        continue;
                    }
                    int gt = i + gtAt;
                    string emit;
                    if (_openNames.Count > 0)
                    {
                        emit = _openNames[^1];
                        _openNames.RemoveAt(_openNames.Count - 1);
                    }
                    else
                    {
                        emit = s.Slice(i + 2, gt - (i + 2)).ToString();
                    }
                    if (_inputScopes.Count > 0)
                        _inputScopes.RemoveAt(_inputScopes.Count - 1);
                    if (_outDefaults.Count > 0)
                        _outDefaults.RemoveAt(_outDefaults.Count - 1);
                    if (_outPrefixDecls.Count > 0)
                        _outPrefixDecls.RemoveAt(_outPrefixDecls.Count - 1);
                    Next.Write("</");
                    Next.Write(emit);
                    Next.Write('>');
                    i = gt + 1;
                    continue;
                }

                int p = i + 1;
                while (p < n && s[p] != '>')
                {
                    if (s[p] == '"' || s[p] == '\'')
                    {
                        char q = s[p];
                        p++;
                        while (p < n && s[p] != q)
                            p++;
                    }
                    p++;
                }
                if (p >= n)
                {
                    if (!final)
                        return i;
                    Next.Write(s[i..n]);
                    i = n;
                    continue;
                }
                bool selfClose = s[p - 1] == '/';
                int innerEnd = selfClose ? p - 1 : p;
                string inner = s.Slice(i + 1, innerEnd - (i + 1)).ToString();

                int k = 0;
                while (k < inner.Length && !IsWs(inner[k]))
                    k++;
                string qname = inner[..k];
                string attrsPart = inner[k..];

                var attrs = new List<(string ws, string name, string eqValue)>();
                string trailing = "";
                int a = 0;
                while (a < attrsPart.Length)
                {
                    int wsStart = a;
                    while (a < attrsPart.Length && IsWs(attrsPart[a]))
                        a++;
                    string ws = attrsPart[wsStart..a];
                    if (a >= attrsPart.Length)
                    {
                        trailing = ws;
                        break;
                    }
                    int nameStart = a;
                    while (a < attrsPart.Length && attrsPart[a] != '=' && !IsWs(attrsPart[a]))
                        a++;
                    string name = attrsPart[nameStart..a];
                    while (a < attrsPart.Length && IsWs(attrsPart[a]))
                        a++;
                    string eqValue = "";
                    if (a < attrsPart.Length && attrsPart[a] == '=')
                    {
                        int evStart = a;
                        a++;
                        while (a < attrsPart.Length && IsWs(attrsPart[a]))
                            a++;
                        if (a < attrsPart.Length && (attrsPart[a] == '"' || attrsPart[a] == '\''))
                        {
                            char q = attrsPart[a];
                            a++;
                            while (a < attrsPart.Length && attrsPart[a] != q)
                                a++;
                            if (a < attrsPart.Length)
                                a++;
                        }
                        eqValue = attrsPart[evStart..a];
                    }
                    attrs.Add((ws, name, eqValue));
                }

                var localDecls = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (_, name, eqValue) in attrs)
                {
                    if (name == "xmlns")
                        localDecls[""] = DefaultXsltExecutionContext.ExtractQuotedValue(eqValue);
                    else if (name.StartsWith("xmlns:", StringComparison.Ordinal))
                        localDecls[name[6..]] = DefaultXsltExecutionContext.ExtractQuotedValue(eqValue);
                }

                string prefix = "";
                string local = qname;
                int colon = qname.IndexOf(':', StringComparison.Ordinal);
                if (colon >= 0)
                {
                    prefix = qname[..colon];
                    local = qname[(colon + 1)..];
                }

                string InputLookup(string pfx)
                {
                    if (localDecls.TryGetValue(pfx, out var lv))
                        return lv;
                    for (int s = _inputScopes.Count - 1; s >= 0; s--)
                        if (_inputScopes[s].TryGetValue(pfx, out var v))
                            return v;
                    return "";
                }
                string OutLookupPrefix(string pfx)
                {
                    for (int s = _outPrefixDecls.Count - 1; s >= 0; s--)
                        if (_outPrefixDecls[s].TryGetValue(pfx, out var v))
                            return v;
                    return "";
                }
                string OutDefault() => _outDefaults.Count > 0 ? _outDefaults[^1] : "";

                string elemUri = InputLookup(prefix);
                bool strip = prefix.Length > 0 && DefaultXsltExecutionContext.Html5ContentNamespaces.Contains(elemUri);
                bool unprefixedOut = strip || prefix.Length == 0;
                string outName = strip ? local : qname;

                bool hasOwnDefaultDecl = localDecls.ContainsKey("");
                string curDefault = OutDefault();
                string newDefault = curDefault;
                string? addDefaultDecl = null;
                if (unprefixedOut)
                {
                    string wantUri = elemUri;
                    if (hasOwnDefaultDecl)
                        newDefault = localDecls[""];
                    else if (wantUri != curDefault)
                    {
                        addDefaultDecl = wantUri;
                        newDefault = wantUri;
                    }
                }

                var emittedPrefixDecls = new Dictionary<string, string>(StringComparer.Ordinal);
                var neededAttrDecls = new List<(string prefix, string uri)>();
                var attrsBuilder = new StringBuilder();
                foreach (var (ws, name, eqValue) in attrs)
                {
                    if (name == "xmlns")
                    {
                        attrsBuilder.Append(ws).Append(name).Append(eqValue);
                        continue;
                    }
                    if (name.StartsWith("xmlns:", StringComparison.Ordinal))
                    {
                        string dpfx = name[6..];
                        string duri = DefaultXsltExecutionContext.ExtractQuotedValue(eqValue);
                        if (DefaultXsltExecutionContext.Html5ContentNamespaces.Contains(duri))
                            continue;
                        attrsBuilder.Append(ws).Append(name).Append(eqValue);
                        emittedPrefixDecls[dpfx] = duri;
                        continue;
                    }
                    attrsBuilder.Append(ws).Append(name).Append(eqValue);
                    int ac = name.IndexOf(':', StringComparison.Ordinal);
                    if (ac > 0)
                    {
                        string apfx = name[..ac];
                        string auri = InputLookup(apfx);
                        if (DefaultXsltExecutionContext.Html5ContentNamespaces.Contains(auri))
                            neededAttrDecls.Add((apfx, auri));
                    }
                }

                var tag = new StringBuilder();
                tag.Append('<').Append(outName);
                if (addDefaultDecl != null)
                    tag.Append(" xmlns=\"").Append(addDefaultDecl).Append('"');
                tag.Append(attrsBuilder);
                foreach (var (apfx, auri) in neededAttrDecls)
                {
                    if (emittedPrefixDecls.TryGetValue(apfx, out var already) && already == auri)
                        continue;
                    if (OutLookupPrefix(apfx) == auri)
                        continue;
                    tag.Append(" xmlns:").Append(apfx).Append("=\"").Append(auri).Append('"');
                    emittedPrefixDecls[apfx] = auri;
                }
                tag.Append(trailing);
                if (selfClose)
                    tag.Append('/');
                tag.Append('>');
                foreach (var chunk in tag.GetChunks())
                    Next.Write(chunk.Span);

                if (!selfClose)
                {
                    _inputScopes.Add(new Dictionary<string, string>(localDecls, StringComparer.Ordinal));
                    _outDefaults.Add(newDefault);
                    _outPrefixDecls.Add(emittedPrefixDecls);
                    _openNames.Add(outName);
                }
                i = p + 1;
            }
            return n;
        }
    }

    // ── 2. the html and xhtml forms of empty and void elements ──

    /// <summary>
    /// &lt;br/&gt; becomes
    /// &lt;br&gt; (html) or &lt;br /&gt; (xhtml), and &lt;div/&gt; becomes &lt;div&gt;&lt;/div&gt;.
    /// </summary>
    private sealed class HtmlFormsStage(Stage next, bool xhtml) : BufferedStage(next)
    {
        protected override int Process(ReadOnlySpan<char> s, bool final)
        {
            var i = 0;
            while (i < s.Length)
            {
                if (s[i] != '<')
                {
                    var lt = s[i..].IndexOf('<');
                    var end = lt < 0 ? s.Length : i + lt;
                    Next.Write(s[i..end]);
                    i = end;
                    continue;
                }
                if (i + 1 >= s.Length)
                {
                    if (!final) return i;
                    Next.Write('<');
                    i++;
                    continue;
                }
                if (s[i + 1] == '/' || s[i + 1] == '!' || s[i + 1] == '?')
                {
                    Next.Write('<');
                    i++;
                    continue;
                }
                var gtRel = s[(i + 1)..].IndexOf('>');
                if (gtRel < 0 && !final)
                    return i;

                var tagStart = i;
                var j = i + 1;
                var nameStart = j;
                while (j < s.Length && s[j] != ' ' && s[j] != '>' && s[j] != '/' && s[j] != '\t' && s[j] != '\n' && s[j] != '\r')
                    j++;
                var tagName = s[nameStart..j];
                while (j < s.Length && s[j] != '>')
                    j++;

                if (s[j - 1] == '/')
                {
                    var upToSlash = s[tagStart..(j - 1)];
                    if (DefaultXsltExecutionContext.HtmlVoidElements.Contains(tagName.ToString()))
                    {
                        if (xhtml)
                        {
                            Next.Write(upToSlash.TrimEnd());
                            Next.Write(" />");
                        }
                        else
                        {
                            Next.Write(upToSlash);
                            Next.Write('>');
                        }
                    }
                    else
                    {
                        Next.Write(upToSlash);
                        Next.Write("></");
                        Next.Write(tagName);
                        Next.Write('>');
                    }
                }
                else
                {
                    Next.Write(s[tagStart..Math.Min(j + 1, s.Length)]);
                }
                i = j < s.Length ? j + 1 : j;
            }
            return i;
        }
    }

    // ── 2b. boolean attributes in minimized form (html) ──

    /// <summary>
    /// The html method writes a boolean attribute whose value is its own name in minimized form:
    /// <c>&lt;input checked&gt;</c> for <c>checked="checked"</c> (Serialization 3.1 §7.1; W3C
    /// attribute-0701). Which attribute is boolean depends on the element; the table is the one
    /// Saxon-HE 12.10 applies, the same for every html version. Names and the value are
    /// compared without case. The xhtml method never minimizes.
    /// </summary>
    private sealed class BooleanAttributeStage(Stage next) : BufferedStage(next)
    {
        private static readonly Dictionary<string, string[]> ByElement = new(StringComparer.OrdinalIgnoreCase)
        {
            ["area"] = ["nohref"],
            ["audio"] = ["autoplay", "controls", "loop", "muted"],
            ["button"] = ["autofocus", "disabled", "formnovalidate"],
            ["details"] = ["open"],
            ["dialog"] = ["open"],
            ["dir"] = ["compact"],
            ["dl"] = ["compact"],
            ["fieldset"] = ["disabled"],
            ["form"] = ["novalidate"],
            ["frame"] = ["noresize"],
            ["hr"] = ["noshade"],
            ["img"] = ["ismap"],
            ["input"] = ["autofocus", "checked", "disabled", "formnovalidate", "multiple", "readonly", "required"],
            ["keygen"] = ["autofocus", "disabled"],
            ["menu"] = ["compact"],
            ["object"] = ["declare", "typemustmatch"],
            ["ol"] = ["compact", "reversed"],
            ["optgroup"] = ["disabled"],
            ["option"] = ["disabled", "selected"],
            ["script"] = ["async", "defer"],
            ["select"] = ["autofocus", "disabled", "multiple", "required"],
            ["td"] = ["nowrap"],
            ["textarea"] = ["autofocus", "disabled", "readonly", "required"],
            ["th"] = ["nowrap"],
            ["track"] = ["default"],
            ["ul"] = ["compact"],
            ["video"] = ["autoplay", "controls", "loop", "muted"],
        };

        private static bool IsBoolean(string element, string attribute)
            => attribute.Equals("hidden", StringComparison.OrdinalIgnoreCase)
               || ByElement.TryGetValue(element, out var names)
                  && Array.Exists(names, n => n.Equals(attribute, StringComparison.OrdinalIgnoreCase));

        protected override int Process(ReadOnlySpan<char> s, bool final)
        {
            var i = 0;
            while (i < s.Length)
            {
                if (s[i] != '<')
                {
                    var lt = s[i..].IndexOf('<');
                    var end = lt < 0 ? s.Length : i + lt;
                    Next.Write(s[i..end]);
                    i = end;
                    continue;
                }
                if (i + 1 >= s.Length)
                {
                    if (!final) return i;
                    Next.Write(s[i..]);
                    return s.Length;
                }
                if (!char.IsAsciiLetter(s[i + 1]))
                {
                    // An end tag, comment, PI or declaration: no attributes to change. Written up
                    // to the next '<', whose own tag is looked at in turn.
                    Next.Write('<');
                    i++;
                    continue;
                }
                // A start tag: find its end, outside quoted values.
                var p = i + 1;
                char quote = '\0';
                while (p < s.Length && (quote != '\0' || s[p] != '>'))
                {
                    if (quote != '\0') { if (s[p] == quote) quote = '\0'; }
                    else if (s[p] is '"' or '\'') quote = s[p];
                    p++;
                }
                if (p >= s.Length)
                {
                    if (!final) return i;
                    Next.Write(s[i..]);
                    return s.Length;
                }
                WriteTag(s[i..(p + 1)]);
                i = p + 1;
            }
            return i;
        }

        private void WriteTag(ReadOnlySpan<char> tag)
        {
            var n = 1;
            while (n < tag.Length && tag[n] is not (' ' or '\t' or '\n' or '\r' or '/' or '>'))
                n++;
            var element = tag[1..n].ToString();
            if (element.Contains(':', StringComparison.Ordinal))
            {
                Next.Write(tag);
                return;
            }
            Next.Write(tag[..n]);
            var k = n;
            while (k < tag.Length)
            {
                // Whitespace, then name="value" (or a '/' or '>' that ends the tag).
                var wsStart = k;
                while (k < tag.Length && tag[k] is ' ' or '\t' or '\n' or '\r')
                    k++;
                var nameStart = k;
                while (k < tag.Length && tag[k] is not ('=' or ' ' or '\t' or '\n' or '\r' or '/' or '>'))
                    k++;
                if (k == nameStart || k >= tag.Length || tag[k] != '=' || k + 1 >= tag.Length || tag[k + 1] is not ('"' or '\''))
                {
                    // Not an attribute of the form name="value": the rest is written as it is.
                    Next.Write(tag[wsStart..]);
                    return;
                }
                var name = tag[nameStart..k];
                var q = tag[k + 1];
                var valueEnd = tag[(k + 2)..].IndexOf(q);
                if (valueEnd < 0)
                {
                    Next.Write(tag[wsStart..]);
                    return;
                }
                var value = tag.Slice(k + 2, valueEnd);
                var attrEnd = k + 2 + valueEnd + 1;
                if (value.Equals(name, StringComparison.OrdinalIgnoreCase) && IsBoolean(element, name.ToString()))
                {
                    Next.Write(tag[wsStart..k]);
                }
                else
                {
                    Next.Write(tag[wsStart..attrEnd]);
                }
                k = attrEnd;
            }
        }
    }

    // ── 3. the Content-Type meta ──

    /// <summary>
    /// The meta becomes the first child of each head, or replaces the http-equiv="Content-Type"
    /// meta the head has (XSLT 3.0 §27.6.4; W3C output-0143/0144/0157/0158).
    /// A head is held until its end tag, to find a meta it already has.
    /// </summary>
    private sealed class ContentTypeMetaStage : BufferedStage
    {
        private readonly string _meta;
        private int _scan; // where the search for "<head" goes on from, in the held text
        private bool _stopped;

        public ContentTypeMetaStage(Stage next, XsltOutput outputDecl) : base(next)
            => _meta = MetaElement(outputDecl);

        protected override int Process(ReadOnlySpan<char> s, bool final)
        {
            if (_stopped)
            {
                Next.Write(s);
                return s.Length;
            }
            var done = 0; // written so far; _scan is relative to it
            while (true)
            {
                var rest = s[done..];
                var idxRel = rest[_scan..].IndexOf("<head", StringComparison.OrdinalIgnoreCase);
                if (idxRel < 0)
                {
                    var keep = final ? 0 : Math.Min(4, rest.Length - _scan);
                    var upTo = rest.Length - keep;
                    Next.Write(rest[..upTo]);
                    _scan = Math.Max(0, _scan - upTo);
                    return done + upTo;
                }
                var idx = _scan + idxRel;
                var afterName = idx + 5;
                if (afterName >= rest.Length)
                {
                    if (final)
                    {
                        // The whole-text pass stopped here.
                        Next.Write(rest);
                        _stopped = true;
                        return s.Length;
                    }
                    return Hold(rest, idx, done);
                }
                var ch = rest[afterName];
                if (ch is '>' or ' ' or '\t' or '\n' or '\r')
                {
                    var closeRel = rest[afterName..].IndexOf('>');
                    if (closeRel < 0 && !final)
                        return Hold(rest, idx, done);
                    var closeTag = closeRel < 0 ? -1 : afterName + closeRel;
                    if (closeTag >= 0 && rest[closeTag - 1] != '/')
                    {
                        var endRel = rest[(closeTag + 1)..].IndexOf("</head", StringComparison.OrdinalIgnoreCase);
                        if (endRel < 0 && !final)
                            return Hold(rest, idx, done);
                        var headEnd = endRel < 0 ? rest.Length : closeTag + 1 + endRel;
                        var (start, length) = FindContentTypeMeta(rest, closeTag + 1, headEnd);
                        if (start < 0)
                        {
                            Next.Write(rest[..(closeTag + 1)]);
                            Next.Write(_meta);
                            done += closeTag + 1;
                        }
                        else
                        {
                            Next.Write(rest[..start]);
                            Next.Write(_meta);
                            done += start + length;
                        }
                        _scan = 0;
                        continue;
                    }
                }
                _scan = afterName;
            }
        }

        private static string MetaElement(XsltOutput outputDecl)
        {
            var content = $"{outputDecl.MediaType ?? "text/html"}; charset={outputDecl.Encoding ?? "UTF-8"}";
            return outputDecl.EffectiveMethod == OutputMethod.Xhtml
                ? $"<meta http-equiv=\"Content-Type\" content=\"{content}\" />"
                : $"<meta http-equiv=\"Content-Type\" content=\"{content}\">";
        }

        // The http-equiv="Content-Type" meta between from and end: its start and length, or -1.
        private static (int Start, int Length) FindContentTypeMeta(ReadOnlySpan<char> s, int from, int end)
        {
            var i = from;
            while (i < end)
            {
                var ltRel = s[i..].IndexOf('<');
                if (ltRel < 0 || i + ltRel >= end) break;
                i += ltRel;
                if (i + 5 <= end
                    && (s[i + 1] == 'm' || s[i + 1] == 'M')
                    && (s[i + 2] == 'e' || s[i + 2] == 'E')
                    && (s[i + 3] == 't' || s[i + 3] == 'T')
                    && (s[i + 4] == 'a' || s[i + 4] == 'A')
                    && (i + 5 == end || s[i + 5] == ' ' || s[i + 5] == '\t' || s[i + 5] == '\n'
                        || s[i + 5] == '\r' || s[i + 5] == '/' || s[i + 5] == '>'))
                {
                    var gtRel = s[i..].IndexOf('>');
                    if (gtRel < 0 || i + gtRel >= end) break;
                    var gt = i + gtRel;
                    var tag = s[i..(gt + 1)];
                    if (tag.IndexOf("http-equiv", StringComparison.OrdinalIgnoreCase) >= 0
                        && tag.IndexOf("Content-Type", StringComparison.OrdinalIgnoreCase) >= 0)
                        return (i, gt + 1 - i);
                    i = gt + 1;
                    continue;
                }
                i++;
            }
            return (-1, 0);
        }

        // Writes the text before a head whose start or content is not all here yet, and keeps it.
        private int Hold(ReadOnlySpan<char> rest, int idx, int done)
        {
            Next.Write(rest[..idx]);
            _scan = 0;
            return done + idx;
        }
    }

    // ── 4. html indentation ──

    /// <summary>
    /// A newline and two spaces for each level before the tags of block elements, except the end
    /// tag of an empty block element (&lt;title&gt;&lt;/title&gt;, result-document-0209) or of one
    /// whose last content is text (result-document-0701/1301). A void element opens no level.
    /// </summary>
    private sealed class HtmlIndentStage(Stage next) : BufferedStage(next)
    {
        private int _depth;
        private bool _wroteAny;
        private char _lastOut;
        private bool _stopped;
        // What the pass looked back at in its input: the last character, and the text from the
        // last '<' (its length, its first characters, its last two characters).
        private char _prev1, _prev2;
        private bool _readAny;
        // Whether the last thing written was the tag of an inline element (span, a, br, input):
        // no newline goes between it and a block tag, which would put a space into the inline
        // content around it (W3C include-0101, attribute-0701; Saxon does the same).
        private bool _afterInlineTag;
        private int _sinceLt = -1;
        private readonly StringBuilder _ltHead = new();

        private void Emit(ReadOnlySpan<char> t)
        {
            if (t.IsEmpty) return;
            Next.Write(t);
            _wroteAny = true;
            _lastOut = t[^1];
        }

        private void Read(ReadOnlySpan<char> t)
        {
            foreach (var c in t)
            {
                if (c == '<')
                {
                    _sinceLt = 0;
                    _ltHead.Clear();
                }
                if (_sinceLt >= 0)
                {
                    if (_ltHead.Length < 64)
                        _ltHead.Append(c);
                    _sinceLt++;
                }
                _prev2 = _prev1;
                _prev1 = c;
                _readAny = true;
            }
        }

        protected override int Process(ReadOnlySpan<char> s, bool final)
        {
            if (_stopped)
            {
                Emit(s);
                return s.Length;
            }
            var i = 0;
            while (i < s.Length)
            {
                if (s[i] != '<')
                {
                    var lt = s[i..].IndexOf('<');
                    var end = lt < 0 ? s.Length : i + lt;
                    Emit(s[i..end]);
                    Read(s[i..end]);
                    if (end > i)
                        _afterInlineTag = false;
                    i = end;
                    continue;
                }
                var gtRel = s[i..].IndexOf('>');
                if (gtRel < 0)
                {
                    if (!final) return i;
                    Emit(s[i..]);
                    _stopped = true;
                    return s.Length;
                }
                var tagEnd = i + gtRel;
                var tag = s[i..(tagEnd + 1)];
                bool isClosing = tag.Length > 1 && tag[1] == '/';
                bool isSelfClosing = tag[^2] == '/' || tag.EndsWith("/>");
                bool isComment = tag.StartsWith("<!--");
                bool isDoctype = tag.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
                bool isPi = tag.Length > 1 && tag[1] == '?';

                ReadOnlySpan<char> elemName = default;
                var hasName = false;
                if (!isComment && !isDoctype && !isPi)
                {
                    int nameStart = isClosing ? 2 : 1;
                    int nameEnd = nameStart;
                    while (nameEnd < tag.Length && tag[nameEnd] != ' ' && tag[nameEnd] != '>'
                           && tag[nameEnd] != '/' && tag[nameEnd] != '\t' && tag[nameEnd] != '\n')
                        nameEnd++;
                    if (nameEnd > nameStart)
                    {
                        elemName = tag[nameStart..nameEnd];
                        hasName = true;
                    }
                }

                bool isBlock = hasName && !XsltTransformEngine.HtmlInlineElements.Contains(elemName.ToString());

                bool emptyBlockClose = false;
                if (isBlock && isClosing && _prev1 == '>' && _sinceLt > 0)
                {
                    // The text from the last '<' to here: _sinceLt characters, ending in '>'.
                    bool prevClosing = _sinceLt > 1 && _ltHead[1] == '/';
                    bool prevSelfClosing = _sinceLt > 1 && _prev2 == '/';
                    if (!prevClosing && !prevSelfClosing)
                    {
                        int pn = 1, pe = 1;
                        while (pe < _sinceLt && pe < _ltHead.Length && _ltHead[pe] != ' ' && _ltHead[pe] != '>'
                               && _ltHead[pe] != '/' && _ltHead[pe] != '\t' && _ltHead[pe] != '\n')
                            pe++;
                        emptyBlockClose = pe > pn
                            && string.Equals(_ltHead.ToString(pn, pe - pn), elemName.ToString(), StringComparison.OrdinalIgnoreCase);
                    }
                }

                bool textContentClose = isBlock && isClosing && !emptyBlockClose && _readAny
                    && _prev1 != '>' && !char.IsWhiteSpace(_prev1);

                if (isBlock)
                {
                    if (isClosing)
                        _depth = Math.Max(0, _depth - 1);
                    if (!emptyBlockClose && !textContentClose && !_afterInlineTag)
                    {
                        if (_wroteAny && _lastOut != '\n')
                            Emit("\n");
                        if (_depth > 0)
                            Emit(new string(' ', _depth * 2));
                    }
                    if (!isClosing && !isSelfClosing
                        && !DefaultXsltExecutionContext.HtmlVoidElements.Contains(elemName.ToString()))
                        _depth++;
                }

                Emit(tag);
                Read(tag);
                _afterInlineTag = hasName && !isBlock;
                i = tagEnd + 1;
            }
            return i;
        }
    }

    // ── 5. no indentation inside suppress-indentation elements (html) ──

    /// <summary>
    /// Inside an element named in suppress-indentation, the newline and spaces put before a tag
    /// are taken out again; whitespace before the newline is text and stays. An html element is
    /// matched by its name without case, any other by its expanded QName.
    /// </summary>
    private sealed class SuppressIndentationStage : BufferedStage
    {
        private readonly HashSet<string> _htmlNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<(NamespaceId, string)> _otherNames = [];
        private readonly NamespaceId _xhtml = StylesheetParser.ResolveNamespaceUri("http://www.w3.org/1999/xhtml");
        private readonly Stack<(string Name, Dictionary<string, string> Scope, bool Suppressed)> _open = new();
        private readonly Dictionary<string, string> _rootScope = new(StringComparer.Ordinal);
        private int _suppressDepth;
        private bool _stopped;

        public SuppressIndentationStage(Stage next, HashSet<QName> suppressed) : base(next)
        {
            foreach (var qn in suppressed)
            {
                if (qn.Namespace == NamespaceId.None || qn.Namespace == _xhtml)
                    _htmlNames.Add(qn.LocalName);
                else
                    _otherNames.Add((qn.Namespace, qn.LocalName));
            }
        }

        protected override int Process(ReadOnlySpan<char> s, bool final)
        {
            if (_stopped)
            {
                Next.Write(s);
                return s.Length;
            }
            int pos = 0;
            while (pos < s.Length)
            {
                if (s[pos] == '<')
                {
                    var gtRel = s[pos..].IndexOf('>');
                    if (gtRel < 0)
                    {
                        if (!final) return pos;
                        Next.Write(s[pos..]);
                        _stopped = true;
                        return s.Length;
                    }
                    // The pass looked up to three characters past the '<' in the whole text.
                    if (!final && pos + 4 > s.Length)
                        return pos;
                    var tagEnd = pos + gtRel;

                    bool isClosing = pos + 1 < s.Length && s[pos + 1] == '/';
                    bool isSelfClosing = s[tagEnd - 1] == '/';
                    bool isComment = pos + 3 < s.Length && s[pos + 1] == '!' && s[pos + 2] == '-';
                    bool isPi = pos + 1 < s.Length && s[pos + 1] == '?';
                    bool isDoctype = pos + 1 < s.Length && s[pos + 1] == '!';

                    string? elemName = null;
                    if (!isComment && !isPi && !isDoctype)
                    {
                        int nameStart = isClosing ? pos + 2 : pos + 1;
                        int nameEnd = nameStart;
                        while (nameEnd <= tagEnd && nameEnd < s.Length
                               && s[nameEnd] != ' ' && s[nameEnd] != '>'
                               && s[nameEnd] != '/' && s[nameEnd] != '\t'
                               && s[nameEnd] != '\n' && s[nameEnd] != '\r')
                            nameEnd++;
                        if (nameEnd > nameStart)
                            elemName = s[nameStart..nameEnd].ToString();
                    }

                    if (isClosing && elemName != null
                        && _open.Any(o => string.Equals(o.Name, elemName, StringComparison.OrdinalIgnoreCase)))
                    {
                        while (_open.Count > 0)
                        {
                            var closed = _open.Pop();
                            if (closed.Suppressed)
                                _suppressDepth = Math.Max(0, _suppressDepth - 1);
                            if (string.Equals(closed.Name, elemName, StringComparison.OrdinalIgnoreCase))
                                break;
                        }
                    }

                    var tag = s[pos..(tagEnd + 1)];
                    Next.Write(tag);
                    pos = tagEnd + 1;

                    if (!isClosing && elemName != null)
                    {
                        var scope = _open.Count > 0 ? _open.Peek().Scope : _rootScope;
                        var declared = XsltTransformEngine.NamespaceDeclarationsOf(tag);
                        if (declared.Count > 0)
                        {
                            scope = new Dictionary<string, string>(scope, StringComparer.Ordinal);
                            foreach (var (prefix, uri) in declared)
                                scope[prefix] = uri;
                        }
                        var colon = elemName.IndexOf(':', StringComparison.Ordinal);
                        var prefixOf = colon > 0 ? elemName[..colon] : "";
                        var local = colon > 0 ? elemName[(colon + 1)..] : elemName;
                        var uriOf = scope.TryGetValue(prefixOf, out var u) ? u : "";
                        var ns = StylesheetParser.ResolveNamespaceUri(uriOf);
                        var suppressed = colon < 0 && (ns == NamespaceId.None || ns == _xhtml)
                            ? _htmlNames.Contains(local)
                            : _otherNames.Contains((ns, local)) || ((ns == NamespaceId.None || ns == _xhtml) && _htmlNames.Contains(local));
                        if (!isSelfClosing && !(colon < 0 && DefaultXsltExecutionContext.HtmlVoidElements.Contains(local)))
                        {
                            _open.Push((elemName, scope, suppressed));
                            if (suppressed)
                                _suppressDepth++;
                        }
                    }
                }
                else if (_suppressDepth > 0 && s[pos] is ' ' or '\t' or '\n' or '\r')
                {
                    int wsStart = pos;
                    int firstNewline = -1;
                    while (pos < s.Length && s[pos] is ' ' or '\t' or '\n' or '\r')
                    {
                        if (firstNewline < 0 && s[pos] is '\n' or '\r')
                            firstNewline = pos;
                        pos++;
                    }
                    if (pos == s.Length && !final)
                        return wsStart; // what follows the run decides it
                    bool precedesTag = pos < s.Length && s[pos] == '<';
                    if (firstNewline >= 0 && precedesTag)
                    {
                        if (firstNewline > wsStart)
                            Next.Write(s[wsStart..firstNewline]);
                    }
                    else
                    {
                        Next.Write(s[wsStart..pos]);
                    }
                }
                else
                {
                    int end = pos + 1;
                    while (end < s.Length && s[end] != '<'
                           && !(_suppressDepth > 0 && s[end] is ' ' or '\t' or '\n' or '\r'))
                        end++;
                    Next.Write(s[pos..end]);
                    pos = end;
                }
            }
            return pos;
        }
    }
}
