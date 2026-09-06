using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:merge-key child of xsl:merge-source.
/// </summary>
public sealed class XsltMergeKey
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public XsltAttributeValueTemplate? Order { get; init; }
    public XsltAttributeValueTemplate? Collation { get; init; }
    public XsltAttributeValueTemplate? DataType { get; init; }
    public XsltAttributeValueTemplate? Lang { get; init; }
}
