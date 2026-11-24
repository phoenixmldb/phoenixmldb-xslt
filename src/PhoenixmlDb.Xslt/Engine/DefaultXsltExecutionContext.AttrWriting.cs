using System.Text;
using PhoenixmlDb.Xdm;

namespace PhoenixmlDb.Xslt.Engine;

internal sealed partial class DefaultXsltExecutionContext
{
    /// <summary>Writes a namespace declaration, <c> xmlns:prefix="uri"</c>, as text to <paramref name="target"/>.</summary>
    private static void AppendNamespaceDeclarationTo(StringBuilder target, string? prefix, string uri)
    {
        target.Append(" xmlns");
        if (!string.IsNullOrEmpty(prefix))
            target.Append(':').Append(prefix);
        target.Append("=\"").Append(EscapeAttributeValue(uri)).Append('"');
    }

    /// <summary>
    /// A namespace declaration of the element under construction: an item of its attributes (the
    /// typed attribute pipeline); with no element under construction, text in the output.
    /// </summary>
    private void WriteNamespaceDeclaration(string? prefix, string uri)
    {
        if (_collectedAttrBuffer is { } buffer)
            buffer.Items.Add(new NsItem(prefix ?? "", uri));
        else
            AppendNamespaceDeclarationTo(_output, prefix, uri);
    }

    /// <summary>
    /// An attribute of the element under construction: an item of its attributes, with its
    /// namespace when the writer knows it; with no element under construction, an attribute
    /// written to the output.
    /// </summary>
    private void WriteAttribute(string name, string value, string? namespaceUri)
    {
        if (_collectedAttrBuffer is { } buffer)
            buffer.Items.Add(new AttrItem(name, namespaceUri, value, null));
        else
            _sink.Attribute(name, value);
    }

    /// <summary>
    /// The attributes and namespace declarations collected for an element, read from its items (the
    /// typed attribute pipeline, S2) into <paramref name="target"/>: lexical name to escaped value, in
    /// write order, the last value of each name.
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
        string elementName, string? elementNamespace, AttrBuffer? laterBindings = null)
    {
        var elementColon = elementName.IndexOf(':', StringComparison.Ordinal);
        var elementPrefix = elementColon > 0 ? elementName[..elementColon] : "";
        Dictionary<string, string>? lastBinding = null;
        foreach (var item in buffer.Items)
        {
            if (item is NsItem { Prefix.Length: > 0 } ns)
                (lastBinding ??= new(StringComparer.Ordinal))[ns.Prefix] = ns.Uri;
        }
        // The namespace nodes written after these attributes (the content of a literal result
        // element, after its attribute sets, S3): they rebind a prefix the same way.
        if (laterBindings is not null)
        {
            foreach (var item in laterBindings.Items)
            {
                if (item is NsItem { Prefix.Length: > 0 } ns)
                    (lastBinding ??= new(StringComparer.Ordinal))[ns.Prefix] = ns.Uri;
            }
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

}
