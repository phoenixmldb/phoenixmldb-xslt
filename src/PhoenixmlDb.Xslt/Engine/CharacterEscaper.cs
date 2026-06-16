using System.Text;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Single source of truth for serialized-output character escaping.
/// XML text/attribute escaping and the shared JSON per-character escape tail
/// all live here so the rules are defined once rather than per delivery path.
/// </summary>
internal static class CharacterEscaper
{
    /// <summary>Appends XML text-node content, escaping &amp;, &lt;, &gt;.</summary>
    public static void AppendXmlText(StringBuilder sb, string value)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    /// <summary>Escapes XML text-node content and returns the result as a string.</summary>
    public static string EscapeXmlText(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        AppendXmlText(sb, value);
        return sb.ToString();
    }

    /// <summary>
    /// Appends XML attribute-value content, escaping &amp;, &lt;, &gt;, &quot;
    /// and the whitespace characters that XML attribute-value normalization would
    /// otherwise collapse (tab, newline, carriage return) as numeric character references.
    /// </summary>
    public static void AppendXmlAttribute(StringBuilder sb, string value)
    {
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\n': sb.Append("&#xA;"); break;
                case '\r': sb.Append("&#xD;"); break;
                case '\t': sb.Append("&#x9;"); break;
                default: sb.Append(c); break;
            }
        }
    }

    /// <summary>Escapes XML attribute-value content and returns the result as a string.</summary>
    public static string EscapeXmlAttribute(string value)
    {
        var sb = new StringBuilder(value.Length + 16);
        AppendXmlAttribute(sb, value);
        return sb.ToString();
    }

    /// <summary>
    /// Appends a single character using JSON string-escaping rules: the JSON short
    /// escapes for the control characters that have them, \uXXXX for any remaining
    /// C0 control, and the verbatim character otherwise. Backslash sequences in the
    /// source (raw escaping, pass-through, or validation) are handled by the caller.
    /// </summary>
    public static void AppendJsonEscapedChar(StringBuilder sb, char c)
    {
        switch (c)
        {
            case '"': sb.Append("\\\""); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            case '\b': sb.Append("\\b"); break;
            case '\f': sb.Append("\\f"); break;
            default:
                if (c < 0x20)
                    sb.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                else
                    sb.Append(c);
                break;
        }
    }
}
