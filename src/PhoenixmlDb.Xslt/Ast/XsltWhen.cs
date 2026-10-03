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

    /// <summary>
    /// The default-collation declared on this xsl:when, governing its test and body (XSLT 3.0
    /// §3.7.2, standard attributes apply to the element and its descendants). Null to inherit.
    /// </summary>
    public string? DefaultCollation { get; init; }
}
