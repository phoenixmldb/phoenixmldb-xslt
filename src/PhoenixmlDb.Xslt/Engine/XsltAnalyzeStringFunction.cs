using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using PhoenixmlDb.Core;
using PhoenixmlDb.Xdm;
using PhoenixmlDb.Xdm.Nodes;
using PhoenixmlDb.Xdm.Serialization;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;
using PhoenixmlDb.Xslt.Engine.Streamability;
// XPath 4.0 ordered map: insertion-order iteration as a structural guarantee.
// xslt keeps its existing default key-equality (pass EqualityComparer<object>.Default
// at each construction site) — this change is about iteration order only.
using OrderedXdmMap = PhoenixmlDb.XQuery.Execution.OrderedXdmMap;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// fn:analyze-string($input as xs:string?, $pattern as xs:string) as element(fn:analyze-string-result)
/// </summary>
internal sealed class XsltAnalyzeStringFunction : PhoenixmlDb.XQuery.Ast.XQueryFunction
{
    private static readonly System.Xml.Linq.XNamespace FnNs = "http://www.w3.org/2005/xpath-functions";

    public override QName Name => new(PhoenixmlDb.XQuery.Functions.FunctionNamespaces.Fn, "analyze-string");
    public override XdmSequenceType ReturnType => new() { ItemType = ItemType.Element, Occurrence = Occurrence.ExactlyOne };
    public override IReadOnlyList<FunctionParameterDef> Parameters =>
    [
        new() { Name = new QName(NamespaceId.None, "input"), Type = XdmSequenceType.OptionalString },
        new() { Name = new QName(NamespaceId.None, "pattern"), Type = XdmSequenceType.String }
    ];

    public override ValueTask<object?> InvokeAsync(
        IReadOnlyList<object?> arguments,
        PhoenixmlDb.XQuery.Ast.ExecutionContext context)
    {
        return AnalyzeStringCore(arguments[0], arguments[1]?.ToString() ?? "", "");
    }

    internal static ValueTask<object?> AnalyzeStringCore(object? inputArg, string pattern, string flags)
    {
        var input = inputArg?.ToString() ?? "";

        // Validate flags
        foreach (var ch in flags)
        {
            if (ch is not ('s' or 'm' or 'i' or 'x' or 'q'))
                throw new XsltException($"FORX0001: Invalid flag '{ch}' in fn:analyze-string flags. Valid flags are: s, m, i, x, q");
        }

        var regexOptions = System.Text.RegularExpressions.RegexOptions.None;
        if (flags.Contains('i', StringComparison.Ordinal))
            regexOptions |= System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        if (flags.Contains('m', StringComparison.Ordinal))
            regexOptions |= System.Text.RegularExpressions.RegexOptions.Multiline;
        if (flags.Contains('s', StringComparison.Ordinal))
            regexOptions |= System.Text.RegularExpressions.RegexOptions.Singleline;
        if (flags.Contains('x', StringComparison.Ordinal))
            regexOptions |= System.Text.RegularExpressions.RegexOptions.IgnorePatternWhitespace;

        string netPattern;
        if (flags.Contains('q', StringComparison.Ordinal))
        {
            netPattern = System.Text.RegularExpressions.Regex.Escape(pattern);
        }
        else
        {
            PhoenixmlDb.XQuery.Functions.XQueryRegexHelper.ValidateXsdRegex(pattern);
            netPattern = PhoenixmlDb.XQuery.Functions.XQueryRegexHelper.ConvertXPathPatternToNet(pattern);
        }
        netPattern = PhoenixmlDb.XQuery.Functions.XQueryRegexHelper.ConvertXsdEscapesToNet(netPattern);
        if (!flags.Contains('m', StringComparison.Ordinal))
            netPattern = PhoenixmlDb.XQuery.Functions.XQueryRegexHelper.FixDollarAnchor(netPattern);
        // Pass the 's' flag through: this helper rewrites '.' into an explicit character
        // class, and its non-single-line form is [^\r\n]. Rewriting unconditionally bakes
        // "dot does not match a newline" into the PATTERN, where RegexOptions.Singleline —
        // set above — can no longer affect it. The five XQuery callers pass the flag; these
        // two XSLT ones did not (analyze-string-008/034/065).
        netPattern = PhoenixmlDb.XQuery.Functions.XQueryRegexHelper.FixDotForSurrogatePairs(netPattern,
            flags.Contains('s', StringComparison.Ordinal));

        System.Text.RegularExpressions.Regex regex;
        try
        {
            regex = new System.Text.RegularExpressions.Regex(netPattern, regexOptions);
        }
        catch (System.ArgumentException ex)
        {
            throw new XsltException($"FORX0002: Invalid regular expression '{pattern}': {ex.Message}");
        }

        // Check that the pattern doesn't match a zero-length string
        if (regex.IsMatch(""))
            throw new XsltException($"FORX0003: The regular expression '{pattern}' matches a zero-length string");

        var result = new System.Xml.Linq.XElement(FnNs + "analyze-string-result");
        var lastIndex = 0;

        for (var match = regex.Match(input); match.Success; match = match.NextMatch())
        {
            // Non-matching text before this match
            if (match.Index > lastIndex)
            {
                result.Add(new System.Xml.Linq.XElement(FnNs + "non-match", input[lastIndex..match.Index]));
            }

            // Build the match element
            var matchElem = new System.Xml.Linq.XElement(FnNs + "match");

            if (match.Groups.Count <= 1)
            {
                // No capturing groups — just the match text
                matchElem.Add(match.Value);
            }
            else
            {
                // Has capturing groups — build group elements
                BuildMatchContent(matchElem, match, input, match.Index, match.Index + match.Length);
            }

            result.Add(matchElem);
            lastIndex = match.Index + match.Length;
        }

        // Trailing non-matching text
        if (lastIndex < input.Length)
        {
            result.Add(new System.Xml.Linq.XElement(FnNs + "non-match", input[lastIndex..]));
        }

        return ValueTask.FromResult<object?>(result);
    }

    /// <summary>
    /// Build the content of a fn:match element, interleaving text and fn:group elements.
    /// Groups are identified by their capture index (Groups[1], Groups[2], etc.).
    /// </summary>
    private static void BuildMatchContent(System.Xml.Linq.XElement matchElem, System.Text.RegularExpressions.Match match, string input, int start, int end)
    {
        // Collect all group captures with their positions, sorted by position
        var segments = new List<(int Start, int End, int GroupNr, string Value)>();

        for (int g = 1; g < match.Groups.Count; g++)
        {
            var group = match.Groups[g];
            if (group.Success)
            {
                segments.Add((group.Index, group.Index + group.Length, g, group.Value));
            }
        }

        // Sort by start position, then by widest span (outermost first)
        segments.Sort((a, b) =>
        {
            var cmp = a.Start.CompareTo(b.Start);
            if (cmp != 0)
                return cmp;
            return b.End.CompareTo(a.End); // wider (outer) first
        });

        // Build a flat list of match text and group elements
        // For simplicity, handle non-nested groups
        var pos = start;
        foreach (var seg in segments)
        {
            // Skip groups that are contained within a previous group we already processed
            if (seg.Start < pos)
                continue;

            // Text before this group
            if (seg.Start > pos)
            {
                matchElem.Add(input[pos..seg.Start]);
            }

            var groupElem = new System.Xml.Linq.XElement(FnNs + "group",
                new System.Xml.Linq.XAttribute("nr", seg.GroupNr),
                seg.Value);
            matchElem.Add(groupElem);
            pos = seg.End;
        }

        // Trailing text after last group
        if (pos < end)
        {
            matchElem.Add(input[pos..end]);
        }
    }
}
