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
/// fn:parse-xml-fragment($arg as xs:string?) as document-node()?
/// Parses an XML fragment from a string and returns a document node.
/// </summary>
internal sealed class XsltParseXmlFragmentFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltParseXmlFragmentFunction(DefaultXsltExecutionContext context) { _context = context; }

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "parse-xml-fragment");
    public override XdmSequenceType ReturnType => new() { ItemType = ItemType.Document, Occurrence = Occurrence.ZeroOrOne };
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
        [new() { Name = new QName(NamespaceId.None, "arg"), Type = XdmSequenceType.OptionalString }];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        var arg = arguments[0];
        if (arg == null)
            return ValueTask.FromResult<object?>(null);
        var xmlStr = arg.ToString() ?? "";
        if (string.IsNullOrEmpty(xmlStr))
        {
            if (_context._nodeStore != null)
            {
                // Create an empty XDM document node directly
                var docId = _context._nodeStore.NextId();
                var emptyDoc = new Xdm.Nodes.XdmDocument
                {
                    StringValueResolver = _context._nodeStore.StringValueResolver,
                    Id = docId,
                    Document = new DocumentId(1),
                    Parent = NodeId.None,
                    DocumentElement = NodeId.None,
                    DocumentUri = null,
                    Children = [],
                };
                emptyDoc._stringValue = "";
                _context._nodeStore.Register(emptyDoc);
                return ValueTask.FromResult<object?>(emptyDoc);
            }
            return ValueTask.FromResult<object?>(new System.Xml.Linq.XDocument());
        }
        try
        {
            if (_context._nodeStore != null)
            {
                // Wrap in a root element to handle fragments with multiple roots or text content
                var xmlDoc = new System.Xml.XmlDocument();
                xmlDoc.PreserveWhitespace = true;
                var wasWrapped = false;
                try
                {
                    xmlDoc.LoadXml(xmlStr);
                }
                catch (System.Xml.XmlException)
                {
                    xmlDoc.LoadXml($"<_rtf_root_>{xmlStr}</_rtf_root_>");
                    wasWrapped = true;
                }
                var xdmDoc = XsltTransformEngine.ConvertToXdm(xmlDoc, _context._nodeStore, documentUri: null);
                xdmDoc.DocumentUri = null;
                if (wasWrapped)
                    xdmDoc.BaseUri = null;
                return ValueTask.FromResult<object?>(xdmDoc);
            }
            // Fallback to LINQ XDocument when no node store available
            var wrapped = $"<_r>{xmlStr}</_r>";
            var tempDoc = System.Xml.Linq.XDocument.Parse(wrapped);
            var doc = new System.Xml.Linq.XDocument();
            foreach (var node in tempDoc.Root!.Nodes())
                doc.Add(node);
            return ValueTask.FromResult<object?>(doc);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new XsltException($"FODC0006: Error parsing XML fragment: {ex.Message}");
        }
    }
}

// ─── fn:xml-to-json ─────────────────────────────────────────────────────────
