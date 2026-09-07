using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Attribute value template (AVT).
/// </summary>
public class XsltAttributeValueTemplate
{
    public required IReadOnlyList<AvtPart> Parts { get; init; }

    public static XsltAttributeValueTemplate FromString(string value)
    {
        return new XsltAttributeValueTemplate
        {
            Parts = [new AvtLiteral { Value = value }]
        };
    }
}
