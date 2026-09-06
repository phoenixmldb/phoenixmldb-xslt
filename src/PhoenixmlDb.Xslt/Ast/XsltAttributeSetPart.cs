using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

public sealed class XsltAttributeSetPart
{
    public required List<QName> UseAttributeSets { get; init; }
    public required List<XsltAttribute> Attributes { get; init; }
    public Uri? BaseUri { get; init; }
}
