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
/// The attributes and namespace declarations collected for an element under construction, as
/// items in write order (the typed attribute pipeline; since S4 without a text form).
/// </summary>
internal sealed class AttrBuffer
{
    public List<object> Items { get; } = new();

    /// <summary>The number of items: zero until something is written.</summary>
    public int Length => Items.Count;

    public void Clear() => Items.Clear();

    public object[] Snapshot() => Items.ToArray();

    public void Restore(object[] snapshot)
    {
        Items.Clear();
        Items.AddRange(snapshot);
    }

    /// <summary>Appends the items of <paramref name="other"/>.</summary>
    public void AppendFrom(AttrBuffer other) => Items.AddRange(other.Items);
}
