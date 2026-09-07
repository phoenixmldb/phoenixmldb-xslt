using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:catch clause in xsl:try.
/// </summary>
public sealed class XsltCatch
{
    public List<QName> Errors { get; init; } = new();

    /// <summary>
    /// The select attribute (XPath expression) - mutually exclusive with Body.
    /// </summary>
    public XQueryExpression? SelectExpression { get; init; }

    /// <summary>
    /// The sequence constructor body - mutually exclusive with SelectExpression.
    /// </summary>
    public XsltSequenceConstructor? Body { get; init; }
}
