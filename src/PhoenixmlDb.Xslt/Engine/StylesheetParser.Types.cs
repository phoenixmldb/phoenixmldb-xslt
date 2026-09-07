using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

public sealed partial class StylesheetParser
{

    /// <summary>
    /// Renders the "which expression, and where" tail appended to a parse failure.
    /// </summary>
    private static string DescribeParseOrigin(string expression, System.Xml.Linq.XObject? origin)
    {
        var snippet = expression.Length > 200 ? expression[..200] + "\u2026" : expression;
        string? where = null;
        System.Xml.Linq.XElement? owner = null;
        switch (origin)
        {
            case System.Xml.Linq.XAttribute attribute:
                owner = attribute.Parent;
                where = owner != null
                    ? $"{owner.Name.LocalName}/@{attribute.Name.LocalName}"
                    : $"@{attribute.Name.LocalName}";
                break;
            case System.Xml.Linq.XElement element:
                owner = element;
                where = element.Name.LocalName;
                break;
            default:
                break;
        }

        var position = "";
        if (origin is System.Xml.IXmlLineInfo lineInfo && lineInfo.HasLineInfo())
            position = $" at line {lineInfo.LineNumber}, column {lineInfo.LinePosition}";
        else if (owner is System.Xml.IXmlLineInfo ownerInfo && ownerInfo.HasLineInfo())
            position = $" at line {ownerInfo.LineNumber}, column {ownerInfo.LinePosition}";

        var module = owner?.Document?.BaseUri;
        if (!string.IsNullOrEmpty(module))
            position = $" in {module}{position}";

        var located = where != null ? $" in {where}" : "";
        return $"\n  \u21b3 parsing{located}{position}: {snippet}";
    }


    private static bool CoerceToBoolean(object? value) =>
        value switch
        {
            bool b => b,
            double d => d != 0.0 && !double.IsNaN(d),
            string s => s.Length > 0,
            null => false,
            _ => true
        };

}
