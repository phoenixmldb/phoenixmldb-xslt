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
}
