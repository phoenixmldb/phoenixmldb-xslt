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
    /// Validates that a string value is a valid xs:decimal (no scientific notation).
    /// </summary>
    private static void ValidateDecimalValue(string value, string errorCode, string attrName, SourceLocation? location)
    {
        var trimmed = value.Trim();
        if (trimmed.Contains('e', StringComparison.OrdinalIgnoreCase))
            throw new XsltException($"{errorCode}: Invalid {attrName} value '{value}': must be a valid xs:decimal (scientific notation is not allowed)", location);
        if (!double.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
            throw new XsltException($"{errorCode}: Invalid {attrName} value '{value}': must be a valid xs:decimal", location);
    }


    private XsltNumber ParseNumber(XElement element, SourceLocation? location)
    {
        var valueAttr = element.Attribute("value");
        var selectAttr = element.Attribute("select");
        var levelAttr = element.Attribute("level");
        var countAttr = element.Attribute("count");
        var fromAttr = element.Attribute("from");
        var formatAttr = element.Attribute("format");
        var langAttr = element.Attribute("lang");
        var letterValueAttr = element.Attribute("letter-value");
        var ordinalAttr = element.Attribute("ordinal");
        var groupingSeparatorAttr = element.Attribute("grouping-separator");
        var groupingSizeAttr = element.Attribute("grouping-size");
        var startAtAttr = element.Attribute("start-at");

        // XTSE0975: value attribute of xsl:number must not be combined with select, level, count, or from
        if (valueAttr != null)
        {
            if (selectAttr != null)
                throw new XsltException("XTSE0975: The value and select attributes of xsl:number are mutually exclusive", location);
            if (levelAttr != null)
                throw new XsltException("XTSE0975: The value and level attributes of xsl:number are mutually exclusive", location);
            if (countAttr != null)
                throw new XsltException("XTSE0975: The value and count attributes of xsl:number are mutually exclusive", location);
            if (fromAttr != null)
                throw new XsltException("XTSE0975: The value and from attributes of xsl:number are mutually exclusive", location);
        }

        // Validate start-at: must be a list of space-separated integers (XTSE0020)
        if (startAtAttr != null && !startAtAttr.Value.Contains('{', StringComparison.Ordinal))
        {
            var parts = startAtAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (!long.TryParse(part, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out _))
                    throw new XsltException($"XTSE0020: Invalid start-at value '{startAtAttr.Value}': " +
                        "must be a list of space-separated integers");
            }
        }

        return new XsltNumber
        {
            Location = location,
            Value = valueAttr != null ? ParseExpr(valueAttr.Value, valueAttr) : null,
            Select = selectAttr != null ? ParseExpr(selectAttr.Value, selectAttr) : null,
            Level = levelAttr?.Value switch
            {
                "single" => NumberLevel.Single,
                "multiple" => NumberLevel.Multiple,
                "any" => NumberLevel.Any,
                _ => NumberLevel.Single
            },
            Count = countAttr != null ? ParsePattern(countAttr.Value, element) : null,
            From = fromAttr != null ? ParsePattern(fromAttr.Value, element) : null,
            Format = formatAttr != null ? ParseAvt(formatAttr.Value, element, formatAttr) : null,
            Lang = langAttr != null ? ParseAvt(langAttr.Value, element, langAttr) : null,
            LetterValue = letterValueAttr != null ? ParseAvt(letterValueAttr.Value, element, letterValueAttr) : null,
            OrdinalValue = ordinalAttr != null ? ParseAvt(ordinalAttr.Value, element, ordinalAttr) : null,
            GroupingSeparator = groupingSeparatorAttr != null ? ParseAvt(groupingSeparatorAttr.Value, element, groupingSeparatorAttr) : null,
            GroupingSize = groupingSizeAttr != null ? ParseAvt(groupingSizeAttr.Value, element, groupingSizeAttr) : null,
            StartAt = startAtAttr != null ? ParseAvt(startAtAttr.Value, element, startAtAttr) : null
        };
    }


    /// <summary>
    /// Formats a QName for display in error messages. Prefers <c>prefix:local</c>, falls back
    /// to <c>Q{uri}local</c> for prefix-less names that carry a non-default namespace, and uses
    /// the bare local name otherwise.
    /// </summary>
    private static string FormatVariableName(QName name)
    {
        if (!string.IsNullOrEmpty(name.Prefix))
            return name.PrefixedName;
        var ns = name.ResolvedNamespace;
        if (!string.IsNullOrEmpty(ns))
            return $"Q{{{ns}}}{name.LocalName}";
        return name.LocalName;
    }

}
