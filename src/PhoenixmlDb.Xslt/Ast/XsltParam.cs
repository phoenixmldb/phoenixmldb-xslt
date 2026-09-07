using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Represents an xsl:param declaration.
/// </summary>
public sealed class XsltParam
{
    public required QName Name { get; init; }
    public XdmSequenceType? As { get; init; }
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public bool Required { get; init; }
    public bool Tunnel { get; init; }
    public bool Static { get; init; }
    public Uri? BaseUri { get; init; }
    public string? Version { get; init; }
}
