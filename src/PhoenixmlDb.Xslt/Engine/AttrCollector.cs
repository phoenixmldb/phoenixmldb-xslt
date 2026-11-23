using System.Text;
using PhoenixmlDb.Xdm;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// An attribute of an element under construction, as written: its lexical name, its namespace
/// when the writer knows it, its value (not escaped) and its type annotation when it has one.
/// </summary>
internal readonly record struct AttrItem(string Name, string? Namespace, string Value, XdmTypeName? Type);

/// <summary>A namespace declaration of an element under construction (prefix "" is the default namespace).</summary>
internal readonly record struct NsItem(string Prefix, string Uri);

/// <summary>
/// What the instructions inside an element constructor add to the element before its first child:
/// attributes and namespace declarations (the typed attribute pipeline, step S1).
/// </summary>
/// <remarks>
/// <para>
/// The engine collects them as text, <c> name="escaped value"</c> pairs read back at the element's
/// end (<see cref="Text"/>). S1 adds the same content as items (<see cref="Items"/>), written by
/// the same calls, so that a later step can read the items and drop the text. Until then the text
/// decides the output; <see cref="AttrGuard"/> compares the two at each element's end.
/// </para>
/// </remarks>
internal sealed class AttrBuffer
{
    public StringBuilder Text { get; } = new();

    /// <summary><see cref="AttrItem"/> and <see cref="NsItem"/>, in the order written.</summary>
    public List<object> Items { get; } = new();

    public int Length => Text.Length;

    /// <summary>The text: what the element's end reads today.</summary>
    public override string ToString() => Text.ToString();

    public void Clear()
    {
        Text.Clear();
        Items.Clear();
    }

    /// <summary>A copy of the content, for a construct that may take it back (xsl:where-populated, a typed body).</summary>
    public (string Text, object[] Items) Snapshot() => (Text.ToString(), Items.ToArray());

    public void Restore((string Text, object[] Items) snapshot)
    {
        Text.Clear();
        Text.Append(snapshot.Text);
        Items.Clear();
        Items.AddRange(snapshot.Items);
    }

    /// <summary>
    /// Appends attributes that were collected as text apart from the element (attribute sets,
    /// which run with the output as their target): the text as it is, and an item for each entry
    /// read back from it. The items of such text come from the text, so the guard does not check
    /// them; a later step builds them directly.
    /// </summary>
    public void AppendText(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        Text.Append(text);
        var pos = 0;
        while (pos < text.Length)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                pos++;
            var nameStart = pos;
            while (pos < text.Length && text[pos] != '=')
                pos++;
            if (pos >= text.Length)
                break;
            var name = text[nameStart..pos].Trim();
            pos++;
            while (pos < text.Length && char.IsWhiteSpace(text[pos]))
                pos++;
            if (pos >= text.Length || (text[pos] != '"' && text[pos] != '\''))
                break;
            var quote = text[pos++];
            var valueStart = pos;
            while (pos < text.Length && text[pos] != quote)
                pos++;
            var value = System.Net.WebUtility.HtmlDecode(text[valueStart..pos]);
            if (pos < text.Length)
                pos++;
            if (name.Length == 0)
                continue;
            if (name == "xmlns")
                Items.Add(new NsItem("", value));
            else if (name.StartsWith("xmlns:", StringComparison.Ordinal))
                Items.Add(new NsItem(name[6..], value));
            else
                Items.Add(new AttrItem(name, null, value, null));
        }
    }
}

/// <summary>
/// The switch for the comparison of text and items at each element's end (S1): off unless
/// <c>PXDB_ATTR_GUARD=1</c>; on, a difference throws, so that a test or conformance run reports it.
/// </summary>
internal static class AttrGuard
{
    private static readonly bool s_enabled = Environment.GetEnvironmentVariable("PXDB_ATTR_GUARD") == "1";

    internal static bool Enabled => s_enabled;

    /// <summary>Elements compared, while enabled.</summary>
    internal static long Compared;
}
