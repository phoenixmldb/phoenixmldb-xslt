using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:sort specification.
/// </summary>
public sealed class XsltSort
{
    public XQueryExpression? Select { get; init; }
    public XsltSequenceConstructor? Content { get; init; }
    public XsltAttributeValueTemplate? Lang { get; init; }
    public XsltAttributeValueTemplate? Order { get; init; } // "ascending" or "descending"
    public XsltAttributeValueTemplate? Collation { get; init; }
    public XsltAttributeValueTemplate? Stable { get; init; }
    public XsltAttributeValueTemplate? CaseOrder { get; init; }
    public XsltAttributeValueTemplate? DataType { get; init; }
}
