using System.Text;
using PhoenixmlDb.Xdm;

namespace PhoenixmlDb.Xslt.Engine;

internal sealed partial class DefaultXsltExecutionContext
{
    /// <summary>
    /// Writes a namespace declaration, <c> xmlns:prefix="uri"</c>, to <paramref name="target"/>: the
    /// attributes of the element under construction, or the output. Into the attributes it is also
    /// an item (the typed attribute pipeline, S1).
    /// </summary>
    private void AppendNamespaceDeclarationTo(StringBuilder target, string? prefix, string uri)
    {
        target.Append(" xmlns");
        if (!string.IsNullOrEmpty(prefix))
            target.Append(':').Append(prefix);
        target.Append("=\"").Append(EscapeAttributeValue(uri)).Append('"');
        if (_collectedAttrBuffer is { } buffer && ReferenceEquals(target, buffer.Text))
            buffer.Items.Add(new NsItem(prefix ?? "", uri));
    }

    /// <summary>
    /// Writes an attribute, <c> name="value"</c>, to <paramref name="target"/>: the attributes of the
    /// element under construction, or the output. Into the attributes it is also an item, with its
    /// namespace and type annotation when the writer knows them (S1).
    /// </summary>
    private void AppendAttributeTo(StringBuilder target, string name, string value, string? namespaceUri = null, XdmTypeName? type = null)
    {
        target.Append(' ').Append(name).Append("=\"").Append(EscapeAttributeValue(value)).Append('"');
        if (_collectedAttrBuffer is { } buffer && ReferenceEquals(target, buffer.Text))
            buffer.Items.Add(new AttrItem(name, namespaceUri, value, type));
    }

    /// <summary>
    /// At an element's end, with <c>PXDB_ATTR_GUARD=1</c>: the attributes and namespace declarations
    /// read from the text, as the element's end reads them (the last value of each name), must be
    /// those the items give (S1). A difference throws: an item the text has no counterpart for, or
    /// the reverse, means a writer that fills only one of the two.
    /// </summary>
    private static void CheckAttrGuard(AttrBuffer buffer, string site)
    {
        if (!AttrGuard.Enabled)
            return;
        Interlocked.Increment(ref AttrGuard.Compared);
        var fromText = new Dictionary<string, string>(StringComparer.Ordinal);
        ParseAttributeString(buffer.Text.ToString(), fromText);
        var fromItems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in buffer.Items)
        {
            switch (item)
            {
                case AttrItem a:
                    fromItems[a.Name] = EscapeAttributeValue(a.Value);
                    break;
                case NsItem n:
                    fromItems[n.Prefix.Length == 0 ? "xmlns" : "xmlns:" + n.Prefix] = EscapeAttributeValue(n.Uri);
                    break;
            }
        }
        if (fromItems.Count == fromText.Count && fromItems.All(kv => fromText.TryGetValue(kv.Key, out var v) && v == kv.Value))
            return;
        throw new InvalidOperationException(
            $"ATTR-GUARD ({site}): text [{Describe(fromText)}] items [{Describe(fromItems)}]");

        static string Describe(Dictionary<string, string> attributes)
            => string.Join(" ", attributes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}=\"{kv.Value}\""));
    }
}
