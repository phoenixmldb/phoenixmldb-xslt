using System.Text;
using System.Text.RegularExpressions;

namespace PhoenixmlDb.Xslt;

/// <summary>
/// Reads an XML file as text, honouring the encoding it declares.
/// </summary>
/// <remarks>
/// <para>
/// <c>File.ReadAllText</c> assumes UTF-8. An XML file that declares another encoding — and
/// the W3C XSLT corpus has many in ISO-8859-1 — decodes to U+FFFD replacement characters,
/// after which the parser reports something unrelated:
/// </para>
/// <code>
/// Error: Name cannot begin with the '�' character, hexadecimal value 0xFFFD.
/// </code>
/// <para>
/// That is what the <c>xslt</c> tool did with any non-UTF-8 stylesheet or source, so a
/// perfectly valid document failed to transform with a message pointing at its own mangling.
/// The conformance harness has always done this correctly, which is why the corpus ran and
/// the shipped tool did not.
/// </para>
/// <para>
/// This lives in the engine rather than the CLI so there is ONE implementation. A second copy
/// of an engine behaviour drifting from the first has been the most expensive class of bug in
/// this codebase.
/// </para>
/// </remarks>
public static class XmlSourceReader
{
    // Only the declaration matters and it is near the front; 512 bytes is far more than the
    // 1024-character limit XML places on the prolog.
    private const int HeaderBytes = 512;

    private static readonly Regex EncodingDecl = new(
        @"encoding\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads <paramref name="path"/>, decoding it per its BOM or XML declaration.</summary>
    public static async Task<string> ReadAsync(string path, CancellationToken ct = default)
        => Decode(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false));

    /// <summary>Decodes XML bytes per their BOM or declaration. Exposed for callers holding bytes.</summary>
    public static string Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        // A BOM is authoritative and outranks the declaration. The BOM itself is stripped:
        // a leading U+FEFF is not content, and XML parsers reject it as such.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            return Encoding.UTF32.GetString(bytes, 4, bytes.Length - 4);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // No BOM: read the declaration. It is ASCII-compatible in every encoding XML allows
        // without a BOM, so scanning the head as ASCII is safe.
        var header = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, HeaderBytes));
        var match = EncodingDecl.Match(header);
        if (match.Success)
        {
            try
            {
                // ISO-8859-1 and the other code pages are not registered by default in .NET Core.
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(match.Groups[1].Value).GetString(bytes);
            }
            catch (ArgumentException)
            {
                // An encoding name this runtime does not know. UTF-8 is the XML default and the
                // better guess than failing outright — the parser will report a real error if
                // the guess is wrong.
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
