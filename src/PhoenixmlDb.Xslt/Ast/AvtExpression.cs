using System.Globalization;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.XQuery.Ast;

namespace PhoenixmlDb.Xslt.Ast;

/// <summary>
/// Expression in AVT (between { and }).
/// </summary>
public sealed class AvtExpression : AvtPart
{
    public required XQueryExpression Expression { get; init; }

    public override async ValueTask<string> EvaluateAsync(XsltExecutionContext context)
    {
        var result = await context.EvaluateAsync(Expression).ConfigureAwait(false);
        return StringifyResult(result, context.IsBackwardsCompatibleMode);
    }

    private static string StringifyResult(object? result, bool backwardsCompatible = false)
    {
        return result switch
        {
            null => "",
            XdmNode node => node.StringValue,
            TextNodeItem tni => tni.Value,
            // Casting xs:QName to xs:string yields the LEXICAL form (XPath 3.1 §19.2), not
            // QName.ToString()'s EQName debugging rendering. This is the AVT/TVT path — the
            // THIRD place in this engine that converts a value to a string, alongside
            // DefaultXsltExecutionContext.StringValueOf and, in PhoenixmlDb.XQuery,
            // ConcatFunction.XQueryStringValue. XSpec's x:QName-expression interpolates a QName
            // with a text value template, so this is the one it goes through; fixing the other
            // two left it still printing Q{uri}local.
            QName qname => string.IsNullOrEmpty(qname.Prefix)
                ? qname.LocalName
                : qname.Prefix + ":" + qname.LocalName,
            bool b => b ? "true" : "false",
            decimal m => FormatDecimal(m),
            double d => FormatDouble(d),
            float f => FormatFloat(f),
            XsDateTime xdt => xdt.ToString(),
            XsDate xd => xd.ToString(),
            XsTime xt => xt.ToString(),
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeOffset dto => FormatDateTimeOffset(dto),
            TimeSpan ts => System.Xml.XmlConvert.ToString(ts),
            // XSLT 2.0 spec §7.6.2: in backwards-compat mode, only the first item is used
            object[] arr when backwardsCompatible => arr.Length > 0 ? StringifyResult(arr[0]) : "",
            object[] arr => StringifySequence(arr),
            IEnumerable<object?> seq when backwardsCompatible => StringifyResult(seq.FirstOrDefault()),
            IEnumerable<object?> seq => StringifySequence(seq.ToArray()),
            _ => result.ToString() ?? ""
        };
    }

    /// <summary>
    /// Implements XSLT 3.0 §5.7.2 simple content construction for sequences:
    /// 1. Merge adjacent text nodes (TextNodeItem)
    /// 2. Remove zero-length text nodes
    /// 3. Join remaining items with space separator
    /// </summary>
    private static string StringifySequence(object?[] items)
    {
        // Fast path: no TextNodeItems → simple space join
        bool hasTextNodeItems = false;
        foreach (var item in items)
        {
            if (item is TextNodeItem)
            {
                hasTextNodeItems = true;
                break;
            }
        }

        if (!hasTextNodeItems)
            return string.Join(" ", items.Select(x => StringifyResult(x)));

        // §5.7.2: merge adjacent TextNodeItems, remove zero-length ones, join with space
        var processed = new List<string>();
        System.Text.StringBuilder? textRun = null;

        foreach (var item in items)
        {
            if (item is TextNodeItem tni)
            {
                textRun ??= new System.Text.StringBuilder();
                textRun.Append(tni.Value);
            }
            else
            {
                if (textRun != null)
                {
                    // Flush merged text node, skip if zero-length
                    if (textRun.Length > 0)
                        processed.Add(textRun.ToString());
                    textRun = null;
                }
                processed.Add(StringifyResult(item));
            }
        }

        if (textRun != null && textRun.Length > 0)
            processed.Add(textRun.ToString());

        return string.Join(" ", processed);
    }

    private static string FormatDateTimeOffset(DateTimeOffset dto)
    {
        if (dto.Offset == TimeSpan.Zero)
            return dto.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "Z";
        return dto.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    private static string FormatDecimal(decimal m)
    {
        // XPath canonical decimal format: no trailing zeros
        var s = m.ToString("G", CultureInfo.InvariantCulture);
        if (s.Contains('.', StringComparison.Ordinal))
        {
            s = s.TrimEnd('0').TrimEnd('.');
        }
        return s == "-0" ? "0" : s;
    }

    private static string FormatDouble(double d) => XQuery.Functions.ConcatFunction.FormatDoubleXPath(d);

    private static string FormatFloat(float f) => XQuery.Functions.ConcatFunction.FormatFloatXPath(f);
}
