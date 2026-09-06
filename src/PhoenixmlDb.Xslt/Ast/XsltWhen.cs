using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// xsl:when clause in xsl:choose.
/// </summary>
public sealed class XsltWhen
{
    public required XQueryExpression Test { get; init; }
    public required XsltSequenceConstructor Body { get; init; }
}
