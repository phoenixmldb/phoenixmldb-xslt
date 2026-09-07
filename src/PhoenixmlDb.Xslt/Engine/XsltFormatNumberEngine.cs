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
/// XSLT 3.0 spec-compliant format-number engine.
/// Implements the picture string parsing and number formatting per
/// https://www.w3.org/TR/xpath-functions-31/#func-format-number
/// </summary>
internal static class XsltFormatNumberEngine
{
    internal static double CoerceToDouble(object? arg)
    {
        var raw = PhoenixmlDb.XQuery.Execution.QueryExecutionContext.Atomize(arg);
        if (raw is null)
            return double.NaN;
        return raw switch
        {
            double d => d,
            decimal m => (double)m,
            long l => (double)l,
            int i => (double)i,
            float f => (double)f,
            System.Numerics.BigInteger bi => (double)bi,
            string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            string s => double.NaN, // Non-numeric string like 'foo' produces NaN
            _ => Convert.ToDouble(raw, System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    internal static XsltDecimalFormat GetDecimalFormat(DefaultXsltExecutionContext ctx, string? name)
    {
        // Check package-local decimal formats first (from the current function's originating package)
        var packageStylesheet = GetCurrentPackageStylesheet(ctx);
        if (packageStylesheet != null)
        {
            var pkgResult = FindDecimalFormatInStylesheet(packageStylesheet, name);
            if (pkgResult != null) return pkgResult;
        }

        if (string.IsNullOrEmpty(name))
        {
            // Default (unnamed) decimal format — key is QName("","")
            var defaultKey = new QName(NamespaceId.None, "");
            return ctx._stylesheet.DecimalFormats.TryGetValue(defaultKey, out var df) ? df : new XsltDecimalFormat();
        }

        // Resolve the name as a QName — may have a namespace prefix like "foo:decimal1"
        var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
        if (colonIdx > 0)
        {
            // Prefixed name — resolve prefix to namespace URI
            var prefix = name[..colonIdx];
            var localName = name[(colonIdx + 1)..];

            // Look up namespace URI for this prefix from the stylesheet
            if (ctx._stylesheet.Namespaces.TryGetValue(prefix, out var nsUri))
            {
                // Find the decimal format with matching namespace and local name
                var nsId = StylesheetParser.ResolveNamespaceUri(nsUri);
                foreach (var kvp in ctx._stylesheet.DecimalFormats)
                {
                    if (kvp.Key.LocalName == localName && kvp.Key.Namespace == nsId)
                        return kvp.Value;
                }
            }
        }
        else
        {
            // Unprefixed name — look in the no-namespace space
            foreach (var kvp in ctx._stylesheet.DecimalFormats)
            {
                if (kvp.Key.LocalName == name && kvp.Key.Namespace == NamespaceId.None)
                    return kvp.Value;
            }
        }

        // FODF1280/XTDE1280: Named decimal format not found
        throw new XsltException($"FODF1280 XTDE1280: No decimal format named '{name}' has been declared");
    }

    /// <summary>
    /// Gets the PackageStylesheet from the current executing function/template.
    /// Returns null if not inside a package function.
    /// </summary>
    internal static Ast.XsltStylesheet? GetCurrentPackageStylesheet(DefaultXsltExecutionContext ctx)
    {
        if (ctx._currentXsltFunctionStack.Count > 0)
            return ctx._currentXsltFunctionStack.Peek().PackageStylesheet;
        if (ctx._currentTemplateStack.Count > 0)
            return ctx._currentTemplateStack.Peek().PackageStylesheet;
        return ctx._currentGlobalPackage;
    }

    /// <summary>
    /// Searches for a decimal format in a specific stylesheet (package-local lookup).
    /// </summary>
    private static XsltDecimalFormat? FindDecimalFormatInStylesheet(Ast.XsltStylesheet stylesheet, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            var defaultKey = new QName(NamespaceId.None, "");
            return stylesheet.DecimalFormats.TryGetValue(defaultKey, out var df) ? df : null;
        }
        var colonIdx = name.IndexOf(':', StringComparison.Ordinal);
        if (colonIdx > 0)
        {
            var prefix = name[..colonIdx];
            var localName = name[(colonIdx + 1)..];
            if (stylesheet.Namespaces.TryGetValue(prefix, out var nsUri))
            {
                var nsId = StylesheetParser.ResolveNamespaceUri(nsUri);
                foreach (var kvp in stylesheet.DecimalFormats)
                {
                    if (kvp.Key.LocalName == localName && kvp.Key.Namespace == nsId)
                        return kvp.Value;
                }
            }
        }
        else
        {
            foreach (var kvp in stylesheet.DecimalFormats)
            {
                if (kvp.Key.LocalName == name && kvp.Key.Namespace == NamespaceId.None)
                    return kvp.Value;
            }
        }
        return null;
    }

    internal static string Format(double value, string picture, XsltDecimalFormat df)
    {
        // Handle special values
        if (double.IsNaN(value))
            return df.NaN;
        if (double.IsPositiveInfinity(value))
            return df.Infinity;
        if (double.IsNegativeInfinity(value))
            return df.MinusSign + df.Infinity;

        // Split picture into sub-pictures using pattern-separator
        var subPictures = SplitPicture(picture, df.PatternSeparator);

        // Select sub-picture: negative uses second (if present), otherwise first with minus prepended
        string subPic;
        bool prependMinus;
        if (value < 0 || (value == 0.0 && double.IsNegativeInfinity(1.0 / value)))
        {
            if (subPictures.Length > 1)
            {
                subPic = subPictures[1];
                prependMinus = false;
                value = Math.Abs(value);
            }
            else
            {
                subPic = subPictures[0];
                prependMinus = true;
                value = Math.Abs(value);
            }
        }
        else
        {
            subPic = subPictures[0];
            prependMinus = false;
        }

        return FormatSubPicture(value, subPic, df, prependMinus);
    }

    private static string[] SplitPicture(string picture, string separator)
    {
        // Split on the pattern-separator character, but only at the top level
        var idx = picture.IndexOf(separator, StringComparison.Ordinal);
        if (idx < 0)
            return [picture];
        var second = picture[(idx + separator.Length)..];
        // FODF1310: pattern separator must not appear more than once
        if (second.Contains(separator, StringComparison.Ordinal))
            throw new XsltException($"FODF1310 XTDE1310: The picture string contains more than one pattern separator character '{separator}'");
        return [picture[..idx], second];
    }

    private static string FormatSubPicture(double value, string subPic, XsltDecimalFormat df, bool prependMinus)
    {
        // Parse the sub-picture into prefix, body, suffix using text elements (for non-BMP support)
        var textElements = EnumerateTextElements(subPic);
        var activeSet = new HashSet<string>
        {
            df.Digit, df.DecimalSeparator, df.GroupingSeparator, df.ExponentSeparator
        };
        // Add zero-digit family: zero-digit through zero-digit+9
        AddZeroDigitFamily(activeSet, df.ZeroDigit);

        int bodyStart = -1, bodyEnd = -1;
        for (int i = 0; i < textElements.Count; i++)
        {
            if (activeSet.Contains(textElements[i]))
            {
                if (bodyStart < 0)
                    bodyStart = i;
                bodyEnd = i;
            }
        }

        if (bodyStart < 0)
            throw new XsltException("FODF1310 XTDE1310: The sub-picture does not contain any digit or zero-digit characters");

        var prefix = string.Concat(textElements.Take(bodyStart));
        var bodyElements = textElements.Skip(bodyStart).Take(bodyEnd - bodyStart + 1).ToList();
        var body = string.Concat(bodyElements);
        var suffix = string.Concat(textElements.Skip(bodyEnd + 1));

        // Validate body has at least one digit or zero-digit
        bool hasDigitOrZero = bodyElements.Any(te => te == df.Digit || IsZeroDigitFamily(te, df));
        if (!hasDigitOrZero)
            throw new XsltException("FODF1310 XTDE1310: The sub-picture does not contain any digit or zero-digit characters");

        // FODF1310: passive character between active characters
        for (int i = 1; i < bodyElements.Count - 1; i++)
        {
            var te = bodyElements[i];
            if (!activeSet.Contains(te) && te != df.Percent && te != df.PerMille)
                throw new XsltException($"FODF1310 XTDE1310: The picture string contains a passive character '{te}' between active characters");
        }

        // FODF1310: at most one decimal-separator
        if (bodyElements.Count(te => te == df.DecimalSeparator) > 1)
            throw new XsltException($"FODF1310 XTDE1310: The picture string contains more than one decimal separator '{df.DecimalSeparator}'");

        // FODF1310: percent and per-mille — at most one, not both
        int percentCount = textElements.Count(te => te == df.Percent);
        int perMilleCount = textElements.Count(te => te == df.PerMille);
        if (percentCount + perMilleCount > 1)
        {
            if (percentCount > 0 && perMilleCount > 0)
                throw new XsltException("FODF1310 XTDE1310: The picture string contains both percent and per-mille characters");
            throw new XsltException("FODF1310 XTDE1310: The picture string contains more than one percent or per-mille character");
        }

        // FODF1310: grouping-separator must not be adjacent to decimal-separator
        for (int i = 0; i < bodyElements.Count; i++)
        {
            if (bodyElements[i] == df.GroupingSeparator)
            {
                if (i + 1 < bodyElements.Count && bodyElements[i + 1] == df.DecimalSeparator)
                    throw new XsltException("FODF1310 XTDE1310: A grouping separator must not appear adjacent to a decimal separator");
                if (i > 0 && bodyElements[i - 1] == df.DecimalSeparator)
                    throw new XsltException("FODF1310 XTDE1310: A grouping separator must not appear adjacent to a decimal separator");
            }
        }

        // Check for percent/per-mille → multiply value
        bool hasPercent = prefix.Contains(df.Percent, StringComparison.Ordinal) || suffix.Contains(df.Percent, StringComparison.Ordinal)
                       || body.Contains(df.Percent, StringComparison.Ordinal);
        bool hasPerMille = prefix.Contains(df.PerMille, StringComparison.Ordinal) || suffix.Contains(df.PerMille, StringComparison.Ordinal)
                        || body.Contains(df.PerMille, StringComparison.Ordinal);

        if (hasPercent)
            value *= 100;
        if (hasPerMille)
            value *= 1000;

        // Parse the body into integer and fractional parts (using text elements)
        int decSepTeIdx = bodyElements.IndexOf(df.DecimalSeparator);
        List<string> intElements, fracElements;
        if (decSepTeIdx >= 0)
        {
            intElements = bodyElements.Take(decSepTeIdx).ToList();
            fracElements = bodyElements.Skip(decSepTeIdx + 1).ToList();
        }
        else
        {
            intElements = bodyElements;
            fracElements = new List<string>();
        }

        // Remove percent/per-mille from body parts
        intElements.RemoveAll(te => te == df.Percent || te == df.PerMille);
        fracElements.RemoveAll(te => te == df.Percent || te == df.PerMille);
        var intPart = string.Concat(intElements);
        var fracPart = string.Concat(fracElements);

        // Parse grouping patterns
        var intGrouping = ParseGroupingTE(intElements, df);
        var fracGrouping = ParseGroupingTE(fracElements, df);

        // FODF1310: In the integer part, mandatory digit must not be followed by optional digit
        if (intElements.Count > 0)
        {
            bool seenMandatory = false;
            foreach (var te in intElements)
            {
                if (IsZeroDigitFamily(te, df))
                    seenMandatory = true;
                else if (te == df.Digit && seenMandatory)
                    throw new XsltException("FODF1310 XTDE1310: In the integer part, a mandatory digit-sign must not be followed by an optional digit-sign");
            }
        }

        // FODF1310: In the fractional part, optional digits (#) must not appear before mandatory (0)
        if (fracElements.Count > 0)
        {
            bool seenDigit = false;
            foreach (var te in fracElements)
            {
                if (te == df.Digit)
                    seenDigit = true;
                else if (IsZeroDigitFamily(te, df) && seenDigit)
                    throw new XsltException("FODF1310 XTDE1310: In the fractional part, a mandatory digit cannot appear after an optional digit");
            }
        }

        // Count minimum and maximum digits
        int intMinDigits = CountTE(intElements, df.ZeroDigit, df);
        int intMaxDigits = intMinDigits + intElements.Count(te => te == df.Digit);
        int fracMinDigits = CountTE(fracElements, df.ZeroDigit, df);
        int fracMaxDigits = fracMinDigits + fracElements.Count(te => te == df.Digit);

        if (intMaxDigits == 0 && fracMaxDigits == 0)
            intMaxDigits = 1; // At least one integer digit

        // Round the value to fracMaxDigits decimal places
        if (fracMaxDigits >= 0 && fracMaxDigits <= 15)
        {
            value = Math.Round(value, fracMaxDigits, MidpointRounding.AwayFromZero);
        }

        // Format the number
        var absValue = Math.Abs(value);

        // Use decimal for integer part precision (handles large numbers correctly),
        // but use string-based formatting for fractional part to avoid double→decimal precision loss
        string intStr;
        string fracStr = "";
        try
        {
            var decValue = (decimal)absValue;
            var intPortion = Math.Truncate(decValue);
            intStr = intPortion.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);

            // For fractional part, format using double directly with desired precision
            if (fracMaxDigits > 0)
            {
                var rawFrac = absValue.ToString("F" + fracMaxDigits, System.Globalization.CultureInfo.InvariantCulture);
                var dotIdx = rawFrac.IndexOf('.', StringComparison.Ordinal);
                fracStr = dotIdx >= 0 ? rawFrac[(dotIdx + 1)..] : new string('0', fracMaxDigits);
            }
        }
        catch (OverflowException)
        {
            // Value too large for decimal — use string-based approach
            var formatted = absValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            var eIdx2 = formatted.IndexOf('E', StringComparison.OrdinalIgnoreCase);
            if (eIdx2 >= 0)
            {
                // Expand scientific notation to plain digits
                var mantissa = formatted[..eIdx2];
                var expPart = formatted[(eIdx2 + 1)..];
                if (expPart.StartsWith('+')) expPart = expPart[1..];
                var exp = int.Parse(expPart, System.Globalization.CultureInfo.InvariantCulture);
                var dotIdx2 = mantissa.IndexOf('.', StringComparison.Ordinal);
                string digits;
                int shift;
                if (dotIdx2 >= 0)
                {
                    digits = mantissa.Remove(dotIdx2, 1);
                    shift = exp - (mantissa.Length - dotIdx2 - 1);
                }
                else
                {
                    digits = mantissa;
                    shift = exp;
                }
                if (digits.StartsWith('-')) digits = digits[1..];
                if (shift > 0)
                    digits += new string('0', shift);
                else if (shift < 0 && -shift < digits.Length)
                    digits = digits[..^(-shift)];
                intStr = digits.Length > 0 ? digits : "0";
            }
            else
            {
                var eDotIdx = formatted.IndexOf('.', StringComparison.Ordinal);
                intStr = eDotIdx >= 0 ? formatted[..eDotIdx] : formatted;
            }
        }

        if (intStr.Length < intMinDigits)
            intStr = intStr.PadLeft(intMinDigits, '0');

        // Apply grouping separators to integer part
        if (intGrouping.Count > 0)
            intStr = ApplyGroupingSeparators(intStr, intGrouping, df.GroupingSeparator, df);

        // Process fractional part
        if (fracMaxDigits > 0 || decSepTeIdx >= 0)
        {
            // Ensure minimum fractional digits
            if (fracStr.Length < fracMinDigits)
                fracStr = fracStr.PadRight(fracMinDigits, '0');

            // Trim trailing zeros beyond minimum
            while (fracStr.Length > fracMinDigits && fracStr[^1] == '0')
                fracStr = fracStr[..^1];
        }
        else
        {
            fracStr = "";
        }

        // Replace digits with the appropriate zero-digit family
        if (df.ZeroDigit != "0")
        {
            intStr = ReplaceDigits(intStr, df.ZeroDigit, df.GroupingSeparator);
            fracStr = ReplaceDigits(fracStr, df.ZeroDigit, df.GroupingSeparator);
        }

        // Assemble result
        var sb = new System.Text.StringBuilder();
        if (prependMinus)
            sb.Append(df.MinusSign);
        sb.Append(prefix);
        sb.Append(intStr);
        if (fracStr.Length > 0 || decSepTeIdx >= 0)
        {
            // Only add decimal separator if there's fractional content or the pattern has it
            if (fracStr.Length > 0 || fracMinDigits > 0)
            {
                sb.Append(df.DecimalSeparator);
                sb.Append(fracStr);
            }
        }
        sb.Append(suffix);
        return sb.ToString();
    }

    /// <summary>
    /// Parse grouping separator positions from text elements.
    /// </summary>
    private static List<int> ParseGroupingTE(List<string> elements, XsltDecimalFormat df)
    {
        var positions = new List<int>();
        int digitPos = 0;
        for (int i = elements.Count - 1; i >= 0; i--)
        {
            if (elements[i] == df.GroupingSeparator)
            {
                if (digitPos > 0)
                    positions.Add(digitPos);
            }
            else if (IsDigitTE(elements[i], df))
            {
                digitPos++;
            }
        }
        return positions;
    }

    private static bool IsDigitTE(string te, XsltDecimalFormat df)
    {
        return te == df.Digit || IsZeroDigitFamily(te, df);
    }

    private static bool IsZeroDigitFamily(string te, XsltDecimalFormat df)
    {
        if (te.Length == 0) return false;
        var zdRune = GetFirstRune(df.ZeroDigit);
        var teRune = GetFirstRune(te);
        return teRune.Value >= zdRune.Value && teRune.Value <= zdRune.Value + 9;
    }

    private static int CountTE(List<string> elements, string target, XsltDecimalFormat df)
    {
        if (target == df.ZeroDigit)
        {
            // Count all mandatory digit positions (zero-digit through zero-digit+9)
            return elements.Count(te => IsZeroDigitFamily(te, df));
        }
        return elements.Count(te => te == target);
    }

    /// <summary>
    /// Apply grouping separators to a formatted integer string (right to left).
    /// </summary>
    private static string ApplyGroupingSeparators(string digits, List<int> groupPositions, string sep, XsltDecimalFormat df)
    {
        if (groupPositions.Count == 0 || digits.Length <= 0)
            return digits;

        bool isRegular = true;
        int groupSize = groupPositions[0];
        for (int g = 1; g < groupPositions.Count; g++)
        {
            if (groupPositions[g] - groupPositions[g - 1] != groupSize)
            {
                isRegular = false;
                break;
            }
        }

        if (groupPositions.Count == 1)
            isRegular = true;

        var sb = new System.Text.StringBuilder();
        int digitCount = 0;
        int posIdx = 0;
        int nextSepAt = posIdx < groupPositions.Count ? groupPositions[posIdx] : -1;

        for (int i = digits.Length - 1; i >= 0; i--)
        {
            if (digitCount > 0 && digitCount == nextSepAt)
            {
                sb.Insert(0, sep);
                posIdx++;
                if (posIdx < groupPositions.Count)
                    nextSepAt = groupPositions[posIdx];
                else if (isRegular && groupSize > 0)
                    nextSepAt += groupSize;
                else
                    nextSepAt = -1;
            }
            sb.Insert(0, digits[i]);
            digitCount++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Replace ASCII digits with the appropriate zero-digit family (supports non-BMP via Rune).
    /// </summary>
    private static string ReplaceDigits(string s, string zeroDigit, string groupSep)
    {
        if (s.Length == 0)
            return s;
        var zdRune = GetFirstRune(zeroDigit);
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (c >= '0' && c <= '9')
            {
                var newRune = new System.Text.Rune(zdRune.Value + (c - '0'));
                sb.Append(newRune.ToString());
            }
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Enumerate text elements (Unicode codepoints, including surrogate pairs) from a string.
    /// </summary>
    private static List<string> EnumerateTextElements(string s)
    {
        var result = new List<string>();
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(s);
        while (enumerator.MoveNext())
            result.Add(enumerator.GetTextElement());
        return result;
    }

    /// <summary>
    /// Add the zero-digit family (zero-digit through zero-digit+9) to a set.
    /// </summary>
    private static void AddZeroDigitFamily(HashSet<string> set, string zeroDigit)
    {
        var zdRune = GetFirstRune(zeroDigit);
        for (int i = 0; i <= 9; i++)
        {
            var rune = new System.Text.Rune(zdRune.Value + i);
            set.Add(rune.ToString());
        }
    }

    private static System.Text.Rune GetFirstRune(string s)
    {
        System.Text.Rune.DecodeFromUtf16(s, out var rune, out _);
        return rune;
    }

    /// <summary>
    /// Format a BigInteger value using the picture string and decimal format.
    /// BigInteger values are always exact integers — no fractional part precision loss.
    /// </summary>
    internal static string FormatBigInteger(System.Numerics.BigInteger value, string picture, XsltDecimalFormat df)
    {
        // Split picture into sub-pictures
        var subPictures = SplitPicture(picture, df.PatternSeparator);

        string subPic;
        bool prependMinus;
        if (value < 0)
        {
            if (subPictures.Length > 1)
            {
                subPic = subPictures[1];
                prependMinus = false;
            }
            else
            {
                subPic = subPictures[0];
                prependMinus = true;
            }
            value = System.Numerics.BigInteger.Abs(value);
        }
        else
        {
            subPic = subPictures[0];
            prependMinus = false;
        }

        // Parse the sub-picture structure (reuse the same parsing logic as FormatSubPicture)
        var textElements = EnumerateTextElements(subPic);
        var activeSet = new HashSet<string>
        {
            df.Digit, df.DecimalSeparator, df.GroupingSeparator, df.ExponentSeparator
        };
        AddZeroDigitFamily(activeSet, df.ZeroDigit);

        int bodyStart = -1, bodyEnd = -1;
        for (int i = 0; i < textElements.Count; i++)
        {
            if (activeSet.Contains(textElements[i]))
            {
                if (bodyStart < 0) bodyStart = i;
                bodyEnd = i;
            }
        }

        if (bodyStart < 0)
            throw new XsltException("FODF1310 XTDE1310: The sub-picture does not contain any digit or zero-digit characters");

        var prefix = string.Concat(textElements.Take(bodyStart));
        var bodyElements = textElements.Skip(bodyStart).Take(bodyEnd - bodyStart + 1).ToList();
        var suffix = string.Concat(textElements.Skip(bodyEnd + 1));

        // Check for percent/per-mille
        bool hasPercent = prefix.Contains(df.Percent, StringComparison.Ordinal) || suffix.Contains(df.Percent, StringComparison.Ordinal)
                       || string.Concat(bodyElements).Contains(df.Percent, StringComparison.Ordinal);
        bool hasPerMille = prefix.Contains(df.PerMille, StringComparison.Ordinal) || suffix.Contains(df.PerMille, StringComparison.Ordinal)
                        || string.Concat(bodyElements).Contains(df.PerMille, StringComparison.Ordinal);

        if (hasPercent) value *= 100;
        if (hasPerMille) value *= 1000;

        // Split body into integer and fractional parts
        int decSepTeIdx = bodyElements.IndexOf(df.DecimalSeparator);
        List<string> intElements, fracElements;
        if (decSepTeIdx >= 0)
        {
            intElements = bodyElements.Take(decSepTeIdx).ToList();
            fracElements = bodyElements.Skip(decSepTeIdx + 1).ToList();
        }
        else
        {
            intElements = bodyElements;
            fracElements = new List<string>();
        }

        intElements.RemoveAll(te => te == df.Percent || te == df.PerMille);
        fracElements.RemoveAll(te => te == df.Percent || te == df.PerMille);

        var intGrouping = ParseGroupingTE(intElements, df);
        int intMinDigits = CountTE(intElements, df.ZeroDigit, df);
        int fracMinDigits = CountTE(fracElements, df.ZeroDigit, df);

        // Format the integer part using BigInteger.ToString() for exact digits
        var intStr = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (intStr.Length < intMinDigits)
            intStr = intStr.PadLeft(intMinDigits, '0');

        if (intGrouping.Count > 0)
            intStr = ApplyGroupingSeparators(intStr, intGrouping, df.GroupingSeparator, df);

        // For BigInteger, fractional part is always zeros
        var fracStr = fracMinDigits > 0 ? new string('0', fracMinDigits) : "";

        // Replace digits with zero-digit family if needed
        if (df.ZeroDigit != "0")
        {
            intStr = ReplaceDigits(intStr, df.ZeroDigit, df.GroupingSeparator);
            fracStr = ReplaceDigits(fracStr, df.ZeroDigit, df.GroupingSeparator);
        }

        // Assemble result
        var sb = new System.Text.StringBuilder();
        if (prependMinus) sb.Append(df.MinusSign);
        sb.Append(prefix);
        sb.Append(intStr);
        if (fracStr.Length > 0 || (decSepTeIdx >= 0 && fracMinDigits > 0))
        {
            sb.Append(df.DecimalSeparator);
            sb.Append(fracStr);
        }
        sb.Append(suffix);
        return sb.ToString();
    }

    private static bool IsNegativeZero(double d)
    {
        return d == 0.0 && double.IsNegativeInfinity(1.0 / d);
    }
}

// ─── fn:path ────────────────────────────────────────────────────────────────
