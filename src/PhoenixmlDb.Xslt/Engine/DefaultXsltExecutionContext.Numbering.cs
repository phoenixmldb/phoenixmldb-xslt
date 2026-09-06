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

internal sealed partial class DefaultXsltExecutionContext
{

    public override async ValueTask NumberAsync(XsltNumber instruction)
    {
        List<object> numbers;

        if (instruction.Value != null)
        {
            // Explicit value expression — may produce a sequence of numbers
            var result = await EvaluateAsync(instruction.Value).ConfigureAwait(false);
            numbers = ConvertToNumberSequence(result);
            // XTDE0980: value must be non-negative (and not NaN) — only in 2.0+ mode
            // In backwards-compatible (1.0) mode, NaN/negative produce "NaN" output
            if (!IsBackwardsCompatible)
            {
                foreach (var num in numbers)
                {
                    if (num is double d && (double.IsNaN(d) || d < 0))
                        throw Error("XTDE0980: Value of xsl:number must be a non-negative number");
                    if (num is BigInteger bi && bi < 0)
                        throw Error("XTDE0980: Value of xsl:number must be a non-negative number");
                }
            }
        }
        else if (instruction.Select != null)
        {
            // Count nodes based on select expression (XSLT 2.0+)
            var selectResult = await EvaluateAsync(instruction.Select).ConfigureAwait(false);
            XdmNode? selectedNode = null;
            int nodeCount = 0;
            if (selectResult is XdmNode n)
            {
                selectedNode = n;
                nodeCount = 1;
            }
            else if (selectResult is object?[] arr)
            {
                nodeCount = arr.Length;
                if (arr.Length > 0)
                    selectedNode = arr[0] as XdmNode;
            }
            else if (selectResult is System.Collections.IEnumerable enumerable && selectResult is not string)
            {
                foreach (var item in enumerable)
                {
                    nodeCount++;
                    if (selectedNode == null && item is XdmNode xn)
                        selectedNode = xn;
                }
            }
            // XTTE1000: select must return exactly one node
            if (nodeCount == 0 || selectedNode == null)
                throw Error("XTTE1000: select expression in xsl:number returned an empty sequence");
            if (nodeCount > 1)
                throw Error("XTTE1000: select expression in xsl:number returned more than one item");
            numbers = CountNodes(instruction, selectedNode);
        }
        else
        {
            // XTTE0990: context item must be a node when no value/select
            if (ContextItem is not XdmNode)
                throw Error("XTTE0990: Context item for xsl:number must be a node");
            // Count nodes based on level, count, from (uses context item)
            numbers = CountNodes(instruction, null);
        }

        // Apply start-at offset to all numbers (XSLT 3.0: applies to both counted and explicit value)
        if (instruction.StartAt != null)
        {
            var startAtStr = await EvaluateAvtAsync(instruction.StartAt).ConfigureAwait(false);
            // start-at can be a space-separated list of integers for level="multiple"
            var startAtParts = startAtStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (startAtParts.Length == 1)
            {
                if (long.TryParse(startAtStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var startAt) && startAt != 1)
                {
                    for (var i = 0; i < numbers.Count; i++)
                        if (numbers[i] is double d && !double.IsNaN(d))
                            numbers[i] = d + startAt - 1;
                        else if (numbers[i] is BigInteger bi)
                            numbers[i] = bi + startAt - 1;
                }
            }
            else
            {
                // Multiple start-at values for level="multiple" or a value sequence.
                // XSLT 3.0: when there are more numbers than start-at values, the LAST
                // start-at value is reused for every remaining number (not defaulted to 1).
                for (var i = 0; i < numbers.Count; i++)
                {
                    var partIdx = i < startAtParts.Length ? i : startAtParts.Length - 1;
                    if (long.TryParse(startAtParts[partIdx], NumberStyles.Integer, CultureInfo.InvariantCulture, out var startAt) && startAt != 1)
                        if (numbers[i] is double d && !double.IsNaN(d))
                            numbers[i] = d + startAt - 1;
                        else if (numbers[i] is BigInteger bi)
                            numbers[i] = bi + startAt - 1;
                }
            }
        }

        // Determine format string
        var format = "1";
        if (instruction.Format != null)
        {
            format = await EvaluateAvtAsync(instruction.Format).ConfigureAwait(false);
        }

        // Determine grouping
        string? groupSep = null;
        int groupSize = 0;
        if (instruction.GroupingSeparator != null)
        {
            groupSep = await EvaluateAvtAsync(instruction.GroupingSeparator).ConfigureAwait(false);
        }
        if (instruction.GroupingSize != null)
        {
            var sizeStr = await EvaluateAvtAsync(instruction.GroupingSize).ConfigureAwait(false);
            int.TryParse(sizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out groupSize);
        }

        // Determine ordinal
        string? ordinal = null;
        if (instruction.OrdinalValue != null)
        {
            ordinal = await EvaluateAvtAsync(instruction.OrdinalValue).ConfigureAwait(false);
        }

        // Determine language
        string? lang = null;
        if (instruction.Lang != null)
        {
            lang = await EvaluateAvtAsync(instruction.Lang).ConfigureAwait(false);
            // XTDE0030: lang must be a valid language tag (starts with letter, BCP 47-like)
            if (!string.IsNullOrEmpty(lang) && !IsValidLanguageTag(lang))
                throw Error($"XTDE0030: Invalid language tag '{lang}' in xsl:number");
        }

        // Format the numbers
        var formatted = FormatNumbers(numbers, format, groupSep, groupSize, ordinal, lang);
        _sink.RawText(formatted);
    }


    private List<object> CountNodes(XsltNumber instruction, XdmNode? selectedNode)
    {
        var node = selectedNode ?? ContextItem;
        if (node is not XdmNode xdmNode || _nodeStore == null)
            return [1.0];

        var countPattern = instruction.Count;
        var fromPattern = instruction.From;

        // Ensure namespace IDs are resolved in count/from patterns
        // (template match patterns are resolved during setup, but xsl:number patterns are not)
        ResolvePatternNamespacesLocal(countPattern);
        ResolvePatternNamespacesLocal(fromPattern);

        // Push the target node as current() for count/from pattern predicates.
        // Per XSLT 3.0 §13.4, current() in count/from patterns returns the node being numbered.
        PushCurrentItem(xdmNode);
        try
        {
            return CountNodesCore(instruction, xdmNode, countPattern, fromPattern);
        }
        finally
        {
            PopCurrentItem();
        }
    }


#pragma warning disable CS8602 // _nodeStore guaranteed non-null by caller
    private List<object> CountNodesCore(XsltNumber instruction, XdmNode xdmNode,
        XsltPattern? countPattern, XsltPattern? fromPattern)
    {
        switch (instruction.Level)
        {
            case NumberLevel.Single:
            {
                var current = xdmNode;

                // If no count pattern, default to matching same node type/name
                if (countPattern == null)
                {
                    countPattern = CreateDefaultCountPattern(current);
                }

                // Walk up to find the first ancestor (or self) that matches count
                // but stop if we hit the from pattern
                // Note: For predicates with position(), we need to compute correct position among siblings
                while (current != null && !MatchesWithSiblingPosition(countPattern, current))
                {
                    if (fromPattern != null && MatchesWithSiblingPosition(fromPattern, current))
                        return []; // from found before count — produce no number
                    if (current.Parent is not { } pid || pid == NodeId.None)
                        return [];
                    current = _nodeStore.GetNode(pid);
                }

                if (current == null)
                    return [];

                // If from pattern specified, check if a from ancestor exists.
                // If it does, we count within that scope. If not, we still count
                // (just at the current level without the reset behavior).
                XdmNode? fromAncestor = null;
                if (fromPattern != null)
                {
                    var check = current;
                    while (check != null)
                    {
                        if (MatchesWithSiblingPosition(fromPattern, check))
                        {
                            fromAncestor = check;
                            break;
                        }
                        if (check.Parent is not { } cpid || cpid == NodeId.None)
                            break;
                        check = _nodeStore.GetNode(cpid);
                    }
                    // Note: we no longer return [] when fromAncestor is null.
                    // Per XSLT spec, nodes without a from ancestor are still numbered.
                }

                // Count preceding siblings (including self) that match
                long count = 0;
                if (current.Parent is { } parentId && parentId != NodeId.None)
                {
                    var parent = _nodeStore.GetNode(parentId);
                    if (parent != null)
                    {
                        var children = _nodeStore.GetChildren(parent).ToList();

                        // For pattern predicates using position(), we need to compute:
                        // 1. Total count of siblings matching the node test (for last())
                        // 2. Position of each sibling among those matching node test (for position())
                        int totalMatchingNodeTest = children.Count(c => countPattern.MatchesNodeTest(c));

                        int nodeTestPosition = 0;
                        foreach (var sibling in children)
                        {
                            // Track position among siblings matching node test
                            if (countPattern.MatchesNodeTest(sibling))
                                nodeTestPosition++;

                            // Evaluate full pattern with correct position/size context
                            if (countPattern.Matches(sibling, CreateMatchContext(nodeTestPosition, totalMatchingNodeTest)))
                                count++;

                            if (sibling.Id == current.Id)
                                break;
                        }
                    }
                }
                else
                {
                    // Root-level node (e.g., document node) has no parent/siblings
                    // If it matches the count pattern, its position is 1
                    count = 1;
                }

                return count == 0 ? [] : [(object)(double)count];
            }

            case NumberLevel.Multiple:
            {
                // Count at each level from the matched node up to the from node
                var numbers = new List<object>();
                var current = xdmNode;

                if (countPattern == null)
                    countPattern = CreateDefaultCountPattern(current);

                while (current != null)
                {
                    // First check if current matches the count pattern (with position context)
                    // We need position of current among its siblings for predicate evaluation
                    bool currentMatches = false;
                    if (current.Parent is { } pid2 && pid2 != NodeId.None)
                    {
                        var parent = _nodeStore.GetNode(pid2);
                        if (parent != null)
                        {
                            var children = _nodeStore.GetChildren(parent).ToList();
                            int totalMatchingNodeTest = children.Count(c => countPattern.MatchesNodeTest(c));
                            int currentPosition = 0;

                            foreach (var sibling in children)
                            {
                                if (countPattern.MatchesNodeTest(sibling))
                                    currentPosition++;
                                if (sibling.Id == current.Id)
                                    break;
                            }

                            using var mc = AcquireMatchContext(currentPosition, totalMatchingNodeTest);
                            currentMatches = countPattern.Matches(current, mc.Value);
                        }
                    }
                    else
                    {
                        using var mc = AcquireMatchContext();
                        currentMatches = countPattern.Matches(current, mc.Value);
                    }

                    if (currentMatches)
                    {
                        long count = 0;
                        if (current.Parent is { } pid3 && pid3 != NodeId.None)
                        {
                            var parent = _nodeStore.GetNode(pid3);
                            if (parent != null)
                            {
                                var children = _nodeStore.GetChildren(parent).ToList();
                                int totalMatchingNodeTest = children.Count(c => countPattern.MatchesNodeTest(c));
                                int nodeTestPosition = 0;

                                foreach (var sibling in children)
                                {
                                    if (countPattern.MatchesNodeTest(sibling))
                                        nodeTestPosition++;

                                    bool siblingMatches;
                                    using (var mc = AcquireMatchContext(nodeTestPosition, totalMatchingNodeTest))
                                        siblingMatches = countPattern.Matches(sibling, mc.Value);
                                    if (siblingMatches)
                                        count++;

                                    if (sibling.Id == current.Id)
                                        break;
                                }
                            }
                        }
                        numbers.Insert(0, (object)(double)(count == 0 ? 1 : count));
                    }

                    // Check from pattern AFTER count — a node matching both from and count
                    // should still be counted before the walk stops
                    if (fromPattern != null)
                    {
                        bool fromMatched;
                        using (var mc = AcquireMatchContext())
                            fromMatched = fromPattern.Matches(current, mc.Value);
                        if (fromMatched) break;
                    }

                    if (current.Parent is not { } pid || pid == NodeId.None)
                        break;
                    current = _nodeStore.GetNode(pid);
                }

                return numbers;
            }

            case NumberLevel.Any:
            {
                // Count all preceding nodes (in document order) matching count
                if (countPattern == null)
                    countPattern = CreateDefaultCountPattern(xdmNode);

                // Find the root of the tree containing the selected node
                // This is important when select points to a variable tree (not the main document)
                var treeRoot = FindTreeRoot(xdmNode);

                long count = 0;
                CountNodesInDocOrder(treeRoot, xdmNode, countPattern, fromPattern, ref count, out var done);
                return count == 0 ? [] : [(object)(double)count];
            }

            default:
                return [1.0];
        }
    }

#pragma warning restore CS8602

    private void CountNodesInDocOrder(XdmNode? node, XdmNode target, XsltPattern countPattern,
        XsltPattern? fromPattern, ref long count, out bool done)
    {
        done = false;
        if (node == null)
            return;

        // Reset count if we hit the from pattern
        if (fromPattern != null)
        {
            bool fromMatched;
            using (var mc = AcquireMatchContext())
                fromMatched = fromPattern.Matches(node, mc.Value);
            if (fromMatched) count = 0;
        }

        bool countMatched;
        using (var mc2 = AcquireMatchContext())
            countMatched = countPattern.Matches(node, mc2.Value);
        if (countMatched) count++;

        if (node.Id == target.Id)
        {
            done = true;
            return;
        }

        // In XDM document order, attributes come after the element start but before children.
        // For level="any", the counting set is "descendants of AF plus the selected node".
        // Attributes are NOT descendants, so only check attributes when the target is an
        // attribute on THIS element — and only count the target itself (XSLT 3.0 §13.4).
        if (node is XdmElement elem && target is XdmAttribute && target.Parent == elem.Id)
        {
            foreach (var attr in _nodeStore!.GetAttributes(elem))
            {
                if (attr.Id == target.Id)
                {
                    if (fromPattern != null)
                    {
                        bool fromAttrMatched;
                        using (var mc = AcquireMatchContext())
                            fromAttrMatched = fromPattern.Matches(attr, mc.Value);
                        if (fromAttrMatched) count = 0;
                    }
                    bool countAttrMatched;
                    using (var mc2 = AcquireMatchContext())
                        countAttrMatched = countPattern.Matches(attr, mc2.Value);
                    if (countAttrMatched) count++;
                    done = true;
                    return;
                }
            }
        }

        // Process children in document order
        foreach (var child in _nodeStore!.GetChildren(node))
        {
            CountNodesInDocOrder(child, target, countPattern, fromPattern, ref count, out done);
            if (done)
                return;
        }
    }


    private static string FormatNumbers(List<object> numbers, string format, string? groupSep, int groupSize, string? ordinal = null, string? lang = null)
    {
        // Parse format string into tokens
        // Format string like "1.1" means first number uses "1", separator ".", second uses "1"
        var tokens = ParseFormatTokens(format);

        // XSLT spec: when there are no numbers, still output prefix and suffix
        if (numbers.Count == 0)
        {
            if (tokens.prefix != null || tokens.suffix != null)
                return (tokens.prefix ?? "") + (tokens.suffix ?? "");
            return "";
        }
        var sb = new StringBuilder();

        for (var i = 0; i < numbers.Count; i++)
        {
            if (i > 0)
            {
                // Use separator from format tokens, default "."
                var sepIdx = Math.Min(i, tokens.separators.Count) - 1;
                sb.Append(sepIdx >= 0 && sepIdx < tokens.separators.Count
                    ? tokens.separators[sepIdx]
                    : ".");
            }

            var tokenIdx = Math.Min(i, tokens.formats.Count - 1);
            var token = tokenIdx >= 0 ? tokens.formats[tokenIdx] : "1";

            string formatted;
            if (numbers[i] is BigInteger bigInt)
            {
                // BigInteger: already exact, no rounding needed
                if (bigInt >= long.MinValue && bigInt <= long.MaxValue)
                    formatted = FormatNumber((long)bigInt, token, ordinal, lang);
                else
                    formatted = FormatBigNumber(bigInt, token);
            }
            else
            {
                var dval = (double)numbers[i];
                if (double.IsNaN(dval))
                {
                    sb.Append("NaN");
                    continue;
                }
                var rounded = Math.Round(dval, MidpointRounding.AwayFromZero);
                if (rounded >= long.MinValue && rounded <= long.MaxValue)
                    formatted = FormatNumber((long)rounded, token, ordinal, lang);
                else
                    formatted = FormatBigNumber(DoubleToBigInteger(rounded), token);
            }
            if (groupSep != null && groupSize > 0 && IsNumericFormat(token))
            {
                formatted = AddGroupingSeparators(formatted, groupSep, groupSize);
            }
            sb.Append(formatted);
        }

        // Add prefix and suffix from format
        if (tokens.prefix != null)
            sb.Insert(0, tokens.prefix);
        if (tokens.suffix != null)
            sb.Append(tokens.suffix);

        return sb.ToString();
    }


    private static string FormatNumber(long number, string token, string? ordinal = null, string? lang = null)
    {
        // Check for Unicode number sequences first
        if (token.Length > 0)
        {
            var unicodeResult = TryFormatUnicodeNumber(number, token);
            if (unicodeResult != null)
                return unicodeResult;
        }

        var result = token switch
        {
            "1" => number.ToString(CultureInfo.InvariantCulture),
            "01" => number.ToString("D2", CultureInfo.InvariantCulture),
            "001" => number.ToString("D3", CultureInfo.InvariantCulture),
            "a" => ToAlpha(number, lowercase: true, lang: lang),
            "A" => ToAlpha(number, lowercase: false, lang: lang),
            "i" => ToRoman(number, lowercase: true),
            "I" => ToRoman(number, lowercase: false),
            "w" => !string.IsNullOrEmpty(ordinal) ? ToOrdinalWords(number, lowercase: true, lang: lang, ordinalScheme: ordinal) : ToWords(number, lowercase: true, lang: lang),
            "W" => !string.IsNullOrEmpty(ordinal) ? ToOrdinalWords(number, lowercase: false, lang: lang, ordinalScheme: ordinal) : ToWords(number, lowercase: false, lang: lang),
            "Ww" => !string.IsNullOrEmpty(ordinal) ? ToOrdinalWords(number, titleCase: true, lang: lang, ordinalScheme: ordinal) : ToWords(number, titleCase: true, lang: lang),
            _ when token.Length > 0 && char.IsDigit(token[0]) =>
                number.ToString($"D{token.Length}", CultureInfo.InvariantCulture),
            _ => number.ToString(CultureInfo.InvariantCulture)
        };

        // Apply ordinal suffix for numeric formats
        if (!string.IsNullOrEmpty(ordinal) && (token == "1" || (token.Length > 0 && char.IsDigit(token[0]))))
        {
            result += GetOrdinalSuffix(number, lang);
        }

        return result;
    }


    private static string FormatBigNumber(BigInteger number, string token)
    {
        // For BigInteger, support numeric digit formats and non-ASCII zero digit replacement
        var result = BigInteger.Abs(number).ToString(CultureInfo.InvariantCulture);

        // Detect non-ASCII zero digit for replacement
        char zeroDigit = '0';
        if (token.Length > 0 && !char.IsAscii(token[0]) && char.IsDigit(token[0]))
            zeroDigit = (char)(token[0] - (int)char.GetNumericValue(token[0]));

        // Zero-pad if format token has leading zeros
        if (token.Length > 1 && char.IsDigit(token[0]))
        {
            var padDigits = token.Length;
            if (result.Length < padDigits)
                result = result.PadLeft(padDigits, '0');
        }

        // Replace ASCII digits with target digit family
        if (zeroDigit != '0')
        {
            var chars = result.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] >= '0' && chars[i] <= '9')
                    chars[i] = (char)(zeroDigit + (chars[i] - '0'));
            }
            result = new string(chars);
        }

        return result;
    }


    /// <summary>
    /// German ordinal word: the cardinal form with its final component turned into an ordinal
    /// stem and the requested inflection ending appended. Numbers 1–19 take a "-t" stem (with the
    /// irregular stems erst/dritt/siebt/acht), while 20+ and round hundreds/thousands take "-st".
    /// Only the trailing spoken component is ordinalized (e.g. 210 → "zweihundertzehnter").
    /// </summary>
    private static string NumberToOrdinalWordsGerman(long number, string ending)
    {
        // Standalone cardinal forms of 1–19 and their ordinal stems (without the inflection ending).
        string[] card19 = ["", "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun",
            "zehn", "elf", "zwölf", "dreizehn", "vierzehn", "fünfzehn", "sechzehn", "siebzehn", "achtzehn", "neunzehn"];
        string[] stem19 = ["", "erst", "zweit", "dritt", "viert", "fünft", "sechst", "siebt", "acht", "neunt",
            "zehnt", "elft", "zwölft", "dreizehnt", "vierzehnt", "fünfzehnt", "sechzehnt", "siebzehnt", "achtzehnt", "neunzehnt"];

        var cardinal = NumberToWordsGerman(number);
        var r100 = number % 100;

        if (r100 >= 1 && r100 <= 19)
        {
            // Trailing component is a ones/teens word: swap it for its ordinal stem.
            var trailing = card19[r100];
            if (cardinal.EndsWith(trailing, StringComparison.Ordinal))
                return string.Concat(cardinal.AsSpan(0, cardinal.Length - trailing.Length), stem19[r100], ending);
            return cardinal + stem19[r100] + ending;
        }

        // Round hundreds/thousands (r100 == 0) and the tens 20–99 take the "-st" ordinal stem.
        return cardinal + "st" + ending;
    }


    private static string NumberToOrdinalWordsEnglish(long number)
    {
        // For ordinals, only the last word changes to ordinal form
        // E.g., "twenty-one" → "twenty-first", "one hundred" → "one hundredth"
        string[] onesOrdinal = ["", "first", "second", "third", "fourth", "fifth",
            "sixth", "seventh", "eighth", "ninth", "tenth", "eleventh", "twelfth",
            "thirteenth", "fourteenth", "fifteenth", "sixteenth", "seventeenth",
            "eighteenth", "nineteenth"];
        string[] tensOrdinal = ["", "", "twentieth", "thirtieth", "fortieth", "fiftieth",
            "sixtieth", "seventieth", "eightieth", "ninetieth"];

        if (number <= 0) return "";
        if (number < 20) return onesOrdinal[number];
        if (number < 100)
        {
            var remainder = number % 10;
            if (remainder == 0) return tensOrdinal[number / 10];
            string[] tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
            return tens[number / 10] + " " + onesOrdinal[remainder];
        }

        // For larger numbers, build cardinal form for all but last component, then ordinal for last
        if (number < 1000)
        {
            string[] ones = ["", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
            var rest = number % 100;
            if (rest == 0) return ones[number / 100] + " hundredth";
            return ones[number / 100] + " hundred and " + NumberToOrdinalWordsEnglish(rest);
        }

        // Use standard groupings
        (long divisor, string name, string ordName)[] groups =
        [
            (1_000_000_000_000L, "trillion", "trillionth"),
            (1_000_000_000L, "billion", "billionth"),
            (1_000_000L, "million", "millionth"),
            (1_000L, "thousand", "thousandth")
        ];

        foreach (var (divisor, name, ordName) in groups)
        {
            if (number >= divisor)
            {
                var high = NumberToWordsEnglish(number / divisor);
                var rest = number % divisor;
                if (rest == 0) return $"{high} {ordName}";
                return $"{high} {name} {NumberToOrdinalWordsEnglish(rest)}";
            }
        }

        return number.ToString(CultureInfo.InvariantCulture);
    }


    private static string NumberToWordsEnglish(long number)
    {
        if (number == 0)
            return "";

        string[] ones = ["", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
        string[] tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

        if (number < 20)
            return ones[number];
        if (number < 100)
        {
            var t = tens[number / 10];
            var o = number % 10;
            return o > 0 ? $"{t} {ones[o]}" : t;
        }
        if (number < 1000)
        {
            var rest = number % 100;
            return rest > 0 ? $"{ones[number / 100]} hundred and {NumberToWordsEnglish(rest)}" : $"{ones[number / 100]} hundred";
        }

        (string name, long divisor)[] groups = [
            ("quintillion", 1_000_000_000_000_000_000L),
            ("quadrillion", 1_000_000_000_000_000L),
            ("trillion", 1_000_000_000_000L),
            ("billion", 1_000_000_000L),
            ("million", 1_000_000L),
            ("thousand", 1_000L)
        ];

        foreach (var (name, divisor) in groups)
        {
            if (number >= divisor)
            {
                var high = NumberToWordsEnglish(number / divisor);
                var rest = number % divisor;
                return rest > 0 ? $"{high} {name} {NumberToWordsEnglish(rest)}" : $"{high} {name}";
            }
        }

        return number.ToString(CultureInfo.InvariantCulture);
    }


    private static string NumberToWordsGerman(long number)
    {
        if (number == 0)
            return "";

        // German number words
        string[] ones = ["", "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun",
            "zehn", "elf", "zwölf", "dreizehn", "vierzehn", "fünfzehn", "sechzehn", "siebzehn", "achtzehn", "neunzehn"];
        // Note: "eins" becomes "ein" in compounds
        string[] onesCompound = ["", "ein", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun"];
        string[] tens = ["", "", "zwanzig", "dreißig", "vierzig", "fünfzig", "sechzig", "siebzig", "achtzig", "neunzig"];

        if (number < 20)
            return ones[number];
        if (number < 100)
        {
            var t = tens[number / 10];
            var o = number % 10;
            // German reverses: 21 = "einundzwanzig" (one-and-twenty)
            return o > 0 ? $"{onesCompound[o]}und{t}" : t;
        }
        if (number < 1000)
        {
            var rest = number % 100;
            var hundredPart = onesCompound[number / 100] + "hundert";
            return rest > 0 ? $"{hundredPart}{NumberToWordsGerman(rest)}" : hundredPart;
        }

        // German uses "Milliarde" for billion (10^9), "Billion" for trillion (10^12)
        (string singular, string plural, long divisor)[] groups = [
            ("Trillion", "Trillionen", 1_000_000_000_000_000_000L),
            ("Billiarde", "Billiarden", 1_000_000_000_000_000L),
            ("Billion", "Billionen", 1_000_000_000_000L),
            ("Milliarde", "Milliarden", 1_000_000_000L),
            ("Million", "Millionen", 1_000_000L),
            ("tausend", "tausend", 1_000L)
        ];

        foreach (var (singular, plural, divisor) in groups)
        {
            if (number >= divisor)
            {
                var count = number / divisor;
                var rest = number % divisor;
                string groupWord;
                if (divisor == 1000)
                {
                    // "tausend" is lowercase; 1000 is "eintausend" (the ordinal/cardinal
                    // W3C corpus, e.g. number-0812, expects the explicit "ein" prefix).
                    groupWord = count == 1 ? "eintausend" : $"{NumberToWordsGerman(count)}tausend";
                }
                else
                {
                    // Use "eine" for feminine nouns (Million, Milliarde, Billion, etc.)
                    var countWord = count == 1 ? "eine" : NumberToWordsGerman(count);
                    groupWord = count == 1 ? $"{countWord} {singular}" : $"{countWord} {plural}";
                }
                if (rest == 0)
                    return groupWord;
                // German writes values below a million as a single word (no space around
                // "tausend"): "eintausendfünf", "einhundertvierunddreißigtausendachthundertsechzehn".
                // At and above a million, the group noun ("Million" …) is a separate word.
                var restWord = NumberToWordsGerman(rest);
                return divisor == 1000
                    ? $"{groupWord}{restWord}"
                    : $"{groupWord} {restWord}".Trim();
            }
        }

        return number.ToString(CultureInfo.InvariantCulture);
    }


    private static string FormatDateTimeOffset(DateTimeOffset dto)
    {
        // Use Z for UTC offsets (XML Schema canonical form) instead of +00:00
        var formatted = dto.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        if (dto.Offset == TimeSpan.Zero)
            return formatted + "Z";
        return formatted + dto.ToString("zzz", System.Globalization.CultureInfo.InvariantCulture);
    }


    internal static string FormatDouble(double d) => XQuery.Functions.ConcatFunction.FormatDoubleXPath(d);


    internal static string FormatFloat(float f) => XQuery.Functions.ConcatFunction.FormatFloatXPath(f);


    private static string FormatDecimal(decimal m)
    {
        // XPath canonical decimal format: no trailing zeros, no leading zeros (except single 0 before decimal point)
        // E.g., 5.0 -> "5", 5.00 -> "5", 0.5 -> "0.5", -0.5 -> "-0.5"
        var s = m.ToString("G", CultureInfo.InvariantCulture);
        // Remove trailing zeros after decimal point
        if (s.Contains('.', StringComparison.Ordinal))
        {
            s = s.TrimEnd('0').TrimEnd('.');
        }
        return s == "-0" ? "0" : s;
    }

}
