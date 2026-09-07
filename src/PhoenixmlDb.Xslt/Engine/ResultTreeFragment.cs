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
/// Wrapper for result tree fragments — XML content produced by variable/parameter body
/// that should be serialized as raw XML (not escaped text) when used in copy-of/sequence.
/// </summary>
internal sealed class ResultTreeFragment(string xmlContent, string? baseUri = null)
{
    public string XmlContent { get; } = xmlContent;
    public string? BaseUri { get; } = baseUri;

    /// <summary>
    /// Cached parsed document node for this RTF. Once parsed, the same XDM tree
    /// is reused to preserve node identity across pattern matching and navigation.
    /// </summary>
    internal XdmNode? CachedDocumentNode { get; set; }

    /// <summary>
    /// Returns the string value of this RTF (text content with XML markup stripped and entities decoded).
    /// This is the XPath string value of the document node.
    /// </summary>
    public override string ToString() => DefaultXsltExecutionContext.StripXmlMarkup(XmlContent);
}
