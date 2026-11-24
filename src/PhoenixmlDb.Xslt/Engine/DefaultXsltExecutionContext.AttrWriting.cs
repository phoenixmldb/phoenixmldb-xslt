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
    /// The attributes and namespace declarations collected for an element, read from its items (the
    /// typed attribute pipeline, S2) into <paramref name="target"/>: lexical name to escaped value, in
    /// write order, the last value of each name, as <see cref="ParseAttributeString"/> reads the text.
    /// </summary>
    /// <remarks>
    /// The element gets the last declaration of each prefix. An attribute written before a
    /// declaration that binds its prefix to another namespace (xsl:namespace, a copied namespace
    /// node) kept its prefix, and so moved into that namespace: <c>&lt;r&gt;&lt;xsl:attribute
    /// name="p:a" namespace="u1"/&gt;&lt;xsl:namespace name="p"&gt;u2&lt;/xsl:namespace&gt;&lt;/r&gt;</c>
    /// gave <c>&lt;r xmlns:p="u2" p:a="1"/&gt;</c>. Its item knows its namespace: it keeps it under a
    /// new prefix, as namespace fixup does (XSLT 3.0 §5.7.3), declared before it (BUGS.md #148).
    /// The element's name is the one it is written with, after its own fixup: an attribute in the
    /// element's namespace takes the element's prefix, and a new prefix is never the element's
    /// (it would be declared twice, or bound to two namespaces).
    /// </remarks>
    private void ReadCollectedAttributes(AttrBuffer buffer, Dictionary<string, string> target,
        string elementName, string? elementNamespace)
    {
        var elementColon = elementName.IndexOf(':', StringComparison.Ordinal);
        var elementPrefix = elementColon > 0 ? elementName[..elementColon] : "";
        Dictionary<string, string>? lastBinding = null;
        foreach (var item in buffer.Items)
        {
            if (item is NsItem { Prefix.Length: > 0 } ns)
                (lastBinding ??= new(StringComparer.Ordinal))[ns.Prefix] = ns.Uri;
        }
        Dictionary<(string Prefix, string Uri), string>? newPrefixes = null;
        foreach (var item in buffer.Items)
        {
            switch (item)
            {
                case AttrItem a:
                    target[NameKeepingNamespace(a)] = EscapeAttributeValue(a.Value);
                    break;
                case NsItem n:
                    target[n.Prefix.Length == 0 ? "xmlns" : "xmlns:" + n.Prefix] = EscapeAttributeValue(n.Uri);
                    break;
            }
        }

        string NameKeepingNamespace(AttrItem a)
        {
            var colon = a.Name.IndexOf(':', StringComparison.Ordinal);
            if (lastBinding is null || colon <= 0 || a.Namespace is not { Length: > 0 } uri)
                return a.Name;
            var prefix = a.Name[..colon];
            if (!lastBinding.TryGetValue(prefix, out var bound) || bound == uri)
                return a.Name;
            if (elementPrefix.Length > 0 && uri == elementNamespace
                && (!lastBinding.TryGetValue(elementPrefix, out var elementBound) || elementBound == uri))
                return elementPrefix + a.Name[colon..];
            newPrefixes ??= [];
            if (!newPrefixes.TryGetValue((prefix, uri), out var newPrefix))
            {
                for (var counter = 0; ; counter++)
                {
                    newPrefix = $"{prefix}_{counter}";
                    if (newPrefix != elementPrefix && !lastBinding.ContainsKey(newPrefix)
                        && !target.ContainsKey("xmlns:" + newPrefix) && !IsPrefixInUse(newPrefix))
                        break;
                }
                newPrefixes[(prefix, uri)] = newPrefix;
                target["xmlns:" + newPrefix] = EscapeAttributeValue(uri);
            }
            return newPrefix + a.Name[colon..];
        }
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
