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
/// fn:parse-xml($arg as xs:string?) as document-node(element(*))?
/// Parses an XML document from a string and returns a document node.
/// </summary>
internal sealed class XsltParseXmlFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private readonly DefaultXsltExecutionContext _context;
    public XsltParseXmlFunction(DefaultXsltExecutionContext context) { _context = context; }

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "parse-xml");
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
        try
        {
            // Convert to XDM so XPath axis navigation works (e.g., $tree//e)
            if (_context._nodeStore != null)
            {
                var xmlDoc = new System.Xml.XmlDocument();
                xmlDoc.PreserveWhitespace = true;
                xmlDoc.LoadXml(xmlStr);
                var xdmDoc = XsltTransformEngine.ConvertToXdm(xmlDoc, _context._nodeStore, documentUri: null);
                // Temporary trees have no document URI per XSLT 3.0 §11.9.1
                xdmDoc.DocumentUri = null;
                return ValueTask.FromResult<object?>(xdmDoc);
            }
            // Fallback to LINQ XDocument when no node store available
            var doc = System.Xml.Linq.XDocument.Parse(xmlStr);
            return ValueTask.FromResult<object?>(doc);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new XsltException($"FODC0006: Error parsing XML: {ex.Message}");
        }
    }
}

// ─── fn:parse-xml-fragment ──────────────────────────────────────────────────
