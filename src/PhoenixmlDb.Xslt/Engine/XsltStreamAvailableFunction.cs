using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xdm.Serialization;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
// XPath 4.0 ordered map: insertion-order iteration as a structural guarantee.
// xslt keeps its existing default key-equality (pass EqualityComparer<object>.Default
// at each construction site) — this change is about iteration order only.
using OrderedXdmMap = PhoenixmlDb.XQuery.Execution.OrderedXdmMap;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// XSLT stream-available() function — checks if a URI resolves to streamable XML.
/// </summary>
internal sealed class XsltStreamAvailableFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltStreamAvailableFunction(DefaultXsltExecutionContext context) => _context = context;

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "stream-available");
    public override XdmSequenceType ReturnType => XdmSequenceType.Boolean;
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
        [new() { Name = new QName(NamespaceId.None, "uri"), Type = XdmSequenceType.String }];

    public override async ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var uri = arguments[0]?.ToString();
        if (string.IsNullOrEmpty(uri))
            return false;

        try
        {
            // Resolve URI
            string? filePath = null;
            if (Uri.TryCreate(uri, UriKind.Absolute, out var absUri) && absUri.IsFile)
            {
                filePath = absUri.LocalPath;
            }
            else if (_context._stylesheet.BaseUri != null)
            {
                var resolved = new Uri(_context._stylesheet.BaseUri, uri);
                if (resolved.IsFile)
                    filePath = resolved.LocalPath;
            }
            else if (System.IO.File.Exists(uri))
            {
                filePath = uri;
            }

            if (filePath == null || !System.IO.File.Exists(filePath))
                return false;

            // Check if the file contains parseable XML
            var stream = System.IO.File.OpenRead(filePath);
            await using var _ = stream.ConfigureAwait(false);
            var settings = new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Ignore,
                XmlResolver = null,
                ConformanceLevel = System.Xml.ConformanceLevel.Fragment,
                Async = true
            };
            using var reader = System.Xml.XmlReader.Create(stream, settings);
            // Try to read the first element — if we can, the stream is available
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.NodeType == System.Xml.XmlNodeType.Element)
                    return true;
            }
            // No element found (e.g., DTD-only file)
            return false;
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
