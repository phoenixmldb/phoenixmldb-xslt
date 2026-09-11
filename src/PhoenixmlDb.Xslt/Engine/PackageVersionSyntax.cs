using System.Text;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Lexical rules for package versions and version ranges (XSLT 3.0 §3.5.1). An invalid value
/// is a static error, XTSE0020. Neither attribute was validated: a malformed
/// <c>xsl:package/@package-version</c> was accepted as-is, and a malformed
/// <c>xsl:use-package/@package-version</c> simply matched nothing and surfaced as "package not
/// found" (XTDE3052) — an error about the catalog, for a mistake in the stylesheet.
/// </summary>
internal static class PackageVersionSyntax
{
    /// <summary>
    /// <c>PackageVersion ::= NumericPart ("-" NamePart)?</c>, where NumericPart is one or more
    /// integers separated by dots and NamePart is an NCName.
    /// </summary>
    public static bool IsValidVersion(string value)
    {
        var dash = value.IndexOf('-', StringComparison.Ordinal);
        if (!IsNumericPart(dash < 0 ? value : value[..dash]))
            return false;
        return dash < 0 || IsNCName(value[(dash + 1)..]);
    }

    /// <summary>
    /// A version range, in exactly the forms <c>StylesheetParser.VersionMatches</c> understands:
    /// <c>*</c>; a comma-separated list of <c>V</c>, <c>V+</c>, <c>N.*</c>, <c>to V</c>,
    /// <c>V to V</c> and <c>V to</c>. Kept in step with the matcher deliberately — a range this
    /// accepts but the matcher does not would silently match nothing.
    /// </summary>
    public static bool IsValidRange(string value)
    {
        var range = value.Trim();
        if (range == "*")
            return true;
        foreach (var raw in range.Split(','))
        {
            var part = raw.Trim();
            if (part.Length == 0)
                return false;
            if (part.StartsWith("to ", StringComparison.Ordinal))
            {
                if (!IsVersionOrPrefix(part[3..].Trim()))
                    return false;
                continue;
            }
            var to = part.IndexOf(" to ", StringComparison.Ordinal);
            if (to >= 0)
            {
                var high = part[(to + 4)..].Trim();
                if (!IsValidVersion(part[..to].Trim()) || (high.Length > 0 && !IsVersionOrPrefix(high)))
                    return false;
                continue;
            }
            if (part.EndsWith(" to", StringComparison.Ordinal))
            {
                if (!IsValidVersion(part[..^3].Trim()))
                    return false;
                continue;
            }
            if (!IsVersionOrPrefix(part.EndsWith('+') ? part[..^1] : part))
                return false;
        }
        return true;
    }

    private static bool IsVersionOrPrefix(string value)
        => value.EndsWith(".*", StringComparison.Ordinal) ? IsNumericPart(value[..^2]) : IsValidVersion(value);

    private static bool IsNumericPart(string value)
    {
        if (value.Length == 0)
            return false;
        foreach (var part in value.Split('.'))
        {
            if (part.Length == 0)
                return false;
            foreach (var c in part)
                if (!char.IsAsciiDigit(c))
                    return false;
        }
        return true;
    }

    /// <summary>
    /// NCName by XML 1.0 5th-edition code-point ranges. By code point, not by UTF-16 unit: a
    /// supplementary character is a valid NameStartChar in [#x10000-#xEFFFF] and invalid above
    /// it, and a char-based test cannot tell U+F00DC (private use, invalid) from U+10000.
    /// </summary>
    private static bool IsNCName(string value)
    {
        if (value.Length == 0)
            return false;
        var first = true;
        foreach (var rune in value.EnumerateRunes())
        {
            if (first ? !IsNameStartChar(rune.Value) : !IsNameChar(rune.Value))
                return false;
            first = false;
        }
        return true;
    }

    private static bool IsNameStartChar(int c) =>
        c is (>= 'A' and <= 'Z') or '_' or (>= 'a' and <= 'z')
            or (>= 0xC0 and <= 0xD6) or (>= 0xD8 and <= 0xF6) or (>= 0xF8 and <= 0x2FF)
            or (>= 0x370 and <= 0x37D) or (>= 0x37F and <= 0x1FFF) or (>= 0x200C and <= 0x200D)
            or (>= 0x2070 and <= 0x218F) or (>= 0x2C00 and <= 0x2FEF) or (>= 0x3001 and <= 0xD7FF)
            or (>= 0xF900 and <= 0xFDCF) or (>= 0xFDF0 and <= 0xFFFD) or (>= 0x10000 and <= 0xEFFFF);

    private static bool IsNameChar(int c) =>
        IsNameStartChar(c) || c is '-' or '.' or (>= '0' and <= '9') or 0xB7
            or (>= 0x300 and <= 0x36F) or (>= 0x203F and <= 0x2040);
}
