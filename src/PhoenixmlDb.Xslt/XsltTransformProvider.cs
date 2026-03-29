using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery;
using PhoenixmlDb.XQuery.Functions;
using XQueryException = PhoenixmlDb.XQuery.Functions.XQueryException;

namespace PhoenixmlDb.Xslt;

/// <summary>
/// Implements <see cref="ITransformProvider"/> using the public <see cref="XsltTransformer"/> API,
/// enabling fn:transform() from XQuery to run XSLT transformations.
/// </summary>
public sealed class XsltTransformProvider : ITransformProvider
{
    /// <inheritdoc/>
    public async ValueTask<object?> TransformAsync(
        IDictionary<object, object?> options,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);

        var stylesheetLocation = GetStringOption(options, "stylesheet-location");
        var stylesheetNode = GetOption(options, "stylesheet-node");
        var stylesheetText = GetStringOption(options, "stylesheet-text");
        var deliveryFormat = GetStringOption(options, "delivery-format") ?? "document";
        var initialTemplate = GetStringOption(options, "initial-template");
        var initialMode = GetStringOption(options, "initial-mode");
        var sourceNode = GetOption(options, "source-node");
        var staticParamsMap = GetOption(options, "static-params") as IDictionary<object, object?>;

        // Unwrap single-item arrays (from map constructor)
        if (sourceNode is object?[] srcArr && srcArr.Length == 1)
            sourceNode = srcArr[0];
        if (stylesheetNode is object?[] ssArr && ssArr.Length == 1)
            stylesheetNode = ssArr[0];

        var nodeStore = context.NodeStore;

        // Determine stylesheet XML
        string stylesheetXml;
        Uri? baseUri = null;

        if (stylesheetLocation != null)
        {
            // Resolve relative URI against static base URI
            var resolvedUri = stylesheetLocation;
            var staticBase = context.StaticBaseUri;
            if (staticBase != null && !Uri.TryCreate(resolvedUri, UriKind.Absolute, out _))
            {
                if (Uri.TryCreate(staticBase, UriKind.Absolute, out var sbu))
                {
                    var resolved = new Uri(sbu, resolvedUri);
                    resolvedUri = resolved.AbsoluteUri;
                    baseUri = resolved;
                }
            }
            else if (Uri.TryCreate(resolvedUri, UriKind.Absolute, out var absUri))
            {
                baseUri = absUri;
            }

            if (baseUri != null && baseUri.IsFile)
                stylesheetXml = await File.ReadAllTextAsync(baseUri.LocalPath).ConfigureAwait(false);
            else
            {
                var dir = staticBase != null
                    ? Path.GetDirectoryName(new Uri(staticBase).LocalPath) ?? "."
                    : ".";
                var fullPath = Path.Combine(dir, resolvedUri);
                stylesheetXml = await File.ReadAllTextAsync(fullPath).ConfigureAwait(false);
                baseUri = new Uri(fullPath);
            }
        }
        else if (stylesheetNode is XdmNode ssNode)
        {
            stylesheetXml = SerializeNode(ssNode, nodeStore);
            if (string.IsNullOrWhiteSpace(stylesheetXml))
                throw new XQueryException("FOXT0001", "Stylesheet node has no content");
        }
        else if (stylesheetText != null)
        {
            stylesheetXml = stylesheetText;
        }
        else
        {
            throw new XQueryException("FOXT0001",
                "No stylesheet specified (stylesheet-location, stylesheet-node, or stylesheet-text required)");
        }

        // Build static params dictionary
        Dictionary<string, string>? staticParams = null;
        if (staticParamsMap != null)
        {
            staticParams = new Dictionary<string, string>();
            foreach (var (key, value) in staticParamsMap)
            {
                var paramName = key is QName qn
                    ? (qn.Namespace != NamespaceId.None && !string.IsNullOrEmpty(qn.ExpandedNamespace)
                        ? $"Q{{{qn.ExpandedNamespace}}}{qn.LocalName}"
                        : qn.LocalName)
                    : key.ToString() ?? "";
                staticParams[paramName] = StringValueOf(value);
            }
        }

        // Create transformer and load stylesheet
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(stylesheetXml, baseUri, staticParams).ConfigureAwait(false);

        // Set initial template / mode
        if (initialTemplate != null)
            transformer.SetInitialTemplate(initialTemplate);
        if (initialMode != null)
            transformer.SetInitialMode(initialMode);

        // Set static-params as runtime parameters too
        if (staticParamsMap != null)
        {
            foreach (var (key, value) in staticParamsMap)
            {
                var paramName = key is QName qn ? qn.LocalName : key.ToString() ?? "";
                transformer.SetParameter(paramName, StringValueOf(value));
            }
        }

        // Serialize source node to XML string for the transformer
        string? inputXml = null;
        if (sourceNode is XdmNode srcNode)
            inputXml = SerializeNode(srcNode, nodeStore);

        // Run the transformation
        var result = await transformer.TransformAsync(inputXml).ConfigureAwait(false);

        // Build result map
        var resultMap = new Dictionary<object, object?>();

        if (string.Equals(deliveryFormat, "serialized", StringComparison.Ordinal))
        {
            resultMap["output"] = result;
        }
        else
        {
            // "document" (default) or "raw" — parse result XML back to XDM
            resultMap["output"] = !string.IsNullOrEmpty(result)
                ? await ParseResultToXdmAsync(result, nodeStore).ConfigureAwait(false)
                : null;
        }

        // Secondary result documents
        foreach (var (href, content) in transformer.SecondaryResultDocuments)
        {
            if (string.Equals(deliveryFormat, "serialized", StringComparison.Ordinal))
                resultMap[href] = content;
            else
                resultMap[href] = await ParseResultToXdmAsync(content, nodeStore).ConfigureAwait(false);
        }

        return resultMap;
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static string? GetStringOption(IDictionary<object, object?> options, string key)
    {
        if (options.TryGetValue(key, out var val) && val != null)
            return StringValueOf(val);
        return null;
    }

    private static object? GetOption(IDictionary<object, object?> options, string key)
    {
        options.TryGetValue(key, out var val);
        return val;
    }

    private static string StringValueOf(object? value)
    {
        return value switch
        {
            null => "",
            string s => s,
            bool b => b ? "true" : "false",
            XdmNode node => node.StringValue,
            _ => value.ToString() ?? ""
        };
    }

    /// <summary>
    /// Serializes an XDM node to XML markup using the node store from the execution context.
    /// </summary>
    private static string SerializeNode(XdmNode node, INodeStore? nodeStore)
    {
        var sb = new StringBuilder();
        SerializeNodeToXml(node, nodeStore, sb);
        return sb.ToString();
    }

    private static void SerializeNodeToXml(XdmNode node, INodeStore? store, StringBuilder sb)
    {
        switch (node)
        {
            case XdmDocument doc:
                foreach (var childId in doc.Children)
                    if (store?.GetNode(childId) is XdmNode child)
                        SerializeNodeToXml(child, store, sb);
                break;

            case XdmElement elem:
                var prefix = elem.Prefix;
                var localName = elem.LocalName;
                var qname = !string.IsNullOrEmpty(prefix) ? $"{prefix}:{localName}" : localName;
                sb.Append('<').Append(qname);

                // Namespace declarations
                foreach (var nsDecl in elem.NamespaceDeclarations)
                {
                    var nsUri = store?.GetNamespaceUri(nsDecl.Namespace) ?? "";
                    if (string.IsNullOrEmpty(nsDecl.Prefix))
                        sb.Append(" xmlns=\"").Append(nsUri).Append('"');
                    else
                        sb.Append(" xmlns:").Append(nsDecl.Prefix).Append("=\"").Append(nsUri).Append('"');
                }

                // Attributes
                foreach (var attrId in elem.Attributes)
                    if (store?.GetNode(attrId) is XdmAttribute attr)
                    {
                        var attrName = !string.IsNullOrEmpty(attr.Prefix)
                            ? $"{attr.Prefix}:{attr.LocalName}"
                            : attr.LocalName;
                        sb.Append(' ').Append(attrName).Append("=\"").Append(EscapeAttr(attr.Value)).Append('"');
                    }

                // Children
                var hasChildren = false;
                foreach (var childId in elem.Children)
                {
                    if (!hasChildren) { sb.Append('>'); hasChildren = true; }
                    if (store?.GetNode(childId) is XdmNode child)
                        SerializeNodeToXml(child, store, sb);
                }

                if (hasChildren)
                    sb.Append("</").Append(qname).Append('>');
                else
                    sb.Append("/>");
                break;

            case XdmText text:
                sb.Append(EscapeText(text.Value));
                break;

            case XdmComment comment:
                sb.Append("<!--").Append(comment.Value).Append("-->");
                break;

            case XdmProcessingInstruction pi:
                sb.Append("<?").Append(pi.Target);
                if (!string.IsNullOrEmpty(pi.Value))
                    sb.Append(' ').Append(pi.Value);
                sb.Append("?>");
                break;
        }
    }

    private static string EscapeAttr(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal);
    }

    private static string EscapeText(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses a result XML string back to an XDM document node using the context's node store,
    /// or returns the string if the node store doesn't support building or the XML is malformed.
    /// </summary>
    private static async ValueTask<object?> ParseResultToXdmAsync(string xml, INodeStore? nodeStore)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        if (nodeStore is INodeBuilder builder)
        {
            try
            {
                var xmlDoc = new XmlDocument { PreserveWhitespace = true };
                xmlDoc.LoadXml(xml);

                // Use the XQuery engine's parse-xml helper to build an XDM tree.
                // We instantiate ParseXmlFunction and invoke it — this is the simplest
                // way to reuse the existing ConvertToXdm logic without duplicating it.
                var parseXml = new ParseXmlFunction();
                var result = await parseXml.InvokeAsync(
                    [xml],
                    new SimpleExecutionContext(builder)).ConfigureAwait(false);
                if (result is XdmDocument xdmDoc)
                {
                    xdmDoc.DocumentUri = null;
                    return xdmDoc;
                }
                return result ?? xml;
            }
            catch (XmlException)
            {
                // Not well-formed XML — return as string
                return xml;
            }
        }

        // No node builder — return serialized string
        return xml;
    }

    /// <summary>
    /// Minimal execution context that exposes a node store for XDM tree construction.
    /// </summary>
    private sealed class SimpleExecutionContext(INodeBuilder nodeBuilder)
        : PhoenixmlDb.XQuery.Ast.ExecutionContext
    {
        public object? ContextItem => null;
        public INodeStore? NodeStore => nodeBuilder;
    }
}
