using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// A captured XML event for incremental subtree building.
/// </summary>
internal readonly record struct StreamXmlEvent(
    XmlNodeType Type,
    string LocalName,
    string? NamespaceUri,
    string? Value,
    IReadOnlyDictionary<string, string>? Attributes);
