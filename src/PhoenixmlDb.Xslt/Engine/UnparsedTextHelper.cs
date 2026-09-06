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
/// Shared URI resolution and file reading for unparsed-text functions.
/// </summary>
internal static class UnparsedTextHelper
{
    internal static string? ResolveFilePath(string href, Uri? baseUri)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var absUri) && absUri.IsFile)
            return absUri.LocalPath;

        if (baseUri != null)
        {
            var resolved = new Uri(baseUri, href);
            if (resolved.IsFile && System.IO.File.Exists(resolved.LocalPath))
                return resolved.LocalPath;
        }

        if (System.IO.File.Exists(href))
            return href;

        return null;
    }

    /// <summary>
    /// Checks that text does not contain characters forbidden in XML (NUL U+0000).
    /// Throws FOUT1190 if invalid characters are found.
    /// </summary>
    internal static void ValidateTextContent(string text)
    {
        if (text.Contains('\0', StringComparison.Ordinal))
            throw new XsltException("FOUT1190: The text resource contains a character that is not permitted in XML (NUL U+0000)");
    }

    /// <summary>
    /// Reads a file with automatic encoding detection:
    /// 1. BOM detection (UTF-8, UTF-16 LE/BE, UTF-32)
    /// 2. XML declaration encoding sniffing (for files starting with &lt;?xml)
    /// 3. UTF-8 fallback
    /// </summary>
    internal static async Task<string> ReadWithEncodingDetectionAsync(string filePath)
    {
        var bytes = await System.IO.File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
        var encoding = DetectEncoding(bytes);
        var text = encoding.GetString(bytes);
        // Strip BOM character (U+FEFF) if present at start of decoded text
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];
        return text;
    }

    private static System.Text.Encoding DetectEncoding(byte[] bytes)
    {
        if (bytes.Length == 0)
            return System.Text.Encoding.UTF8;

        // Check BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return System.Text.Encoding.UTF8;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return System.Text.Encoding.Unicode; // UTF-16 LE
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return System.Text.Encoding.BigEndianUnicode; // UTF-16 BE

        // Try to detect XML encoding declaration: <?xml ... encoding="..."?>
        // Only check ASCII-compatible bytes for the prolog
        if (bytes.Length >= 5 && bytes[0] == '<' && bytes[1] == '?' && bytes[2] == 'x' && bytes[3] == 'm' && bytes[4] == 'l')
        {
            // Find encoding="..." in the XML declaration (scan ASCII bytes)
            var prologEnd = System.Array.IndexOf(bytes, (byte)'>', 5);
            if (prologEnd > 0 && prologEnd < 200)
            {
                var prolog = System.Text.Encoding.ASCII.GetString(bytes, 0, prologEnd);
                var encIdx = prolog.IndexOf("encoding", StringComparison.OrdinalIgnoreCase);
                if (encIdx > 0)
                {
                    var eqIdx = prolog.IndexOf('=', encIdx + 8);
                    if (eqIdx > 0)
                    {
                        var quote = eqIdx + 1 < prolog.Length ? prolog[eqIdx + 1] : '\0';
                        if (quote is '"' or '\'')
                        {
                            var closeQuote = prolog.IndexOf(quote, eqIdx + 2);
                            if (closeQuote > 0)
                            {
                                var encName = prolog[(eqIdx + 2)..closeQuote].Trim();
                                try
                                {
                                    return System.Text.Encoding.GetEncoding(encName);
                                }
                                catch (ArgumentException)
                                {
                                    // Unknown encoding, fall through to UTF-8
                                }
                            }
                        }
                    }
                }
            }
        }

        return System.Text.Encoding.UTF8;
    }
}
