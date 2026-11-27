using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using PhoenixmlDb.Core;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Ast;

namespace PhoenixmlDb.Xslt.Engine;

/// <summary>
/// Represents an error that occurred during XSLT stylesheet compilation or transformation.
/// </summary>
/// <remarks>
/// <para>
/// <c>XsltException</c> is thrown for both compile-time errors (malformed XSLT, invalid
/// attribute values, unresolved imports) and runtime errors (type mismatches, failed
/// assertions, dynamic evaluation errors). Check <see cref="Location"/> to determine
/// where in the stylesheet the error originated.
/// </para>
/// <para>
/// When an error occurs during transformation, the <see cref="Exception.Message"/> contains
/// a description of the XSLT error condition (e.g., <c>XTDE0540</c> for conflicting
/// <c>xsl:result-document</c> URIs), and <see cref="Location"/> pinpoints the stylesheet
/// instruction that caused the failure.
/// </para>
/// </remarks>
/// <seealso cref="XsltTransformer"/>
public class XsltException : Exception
{
    /// <summary>
    /// Gets the source location in the XSLT stylesheet where the error occurred,
    /// or <c>null</c> if the location is not available.
    /// </summary>
    /// <remarks>
    /// The location includes the line number and column number within the stylesheet,
    /// which is useful for diagnostic messages and IDE integration.
    /// </remarks>
    public SourceLocation? Location { get; }

    /// <summary>
    /// The W3C error code this exception reports — <c>XTDE0640</c>, <c>XPST0008</c>,
    /// <c>XTSE0010</c> — or a code of this processor with its prefix, <c>phx:XTSE9430</c>, or a
    /// code in another namespace, <c>Q{uri}local</c>, or <c>null</c> when the message carries none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A code that <c>xsl:message terminate="yes"</c> or <c>xsl:assert</c> gives in
    /// <c>error-code</c> has the same forms: a code in the error namespace
    /// (<c>http://www.w3.org/2005/xqt-errors</c>) is its local name, <c>FOER0000</c>; a code in
    /// the namespace of this processor (<c>https://schemas.phoenixml.dev/2026/functions</c>) is
    /// <c>phx:</c> and its local name, <c>phx:MYCODE</c>; a code in any other namespace is
    /// <c>Q{uri}local</c>, as the message writes it: <c>Q{urn:example}E1</c>, and
    /// <c>Q{}local</c> for no namespace.
    /// </para>
    /// <para>
    /// Derived from the message, because that is where 454 of this engine's 577 throw sites
    /// already put it: every one writes <c>"CODE: description"</c>. Without this property the
    /// only way to ask "which error is this?" was to search the message text, and the engine
    /// did exactly that in seven places — including a deferral guard reading
    /// <c>ex.Message.Contains("XTDE3400")</c>, which would also have matched an error that
    /// merely quoted the code in its prose.
    /// </para>
    /// <para>
    /// Prefer <c>ErrorCode == "XTDE0640"</c> to any test against <see cref="Exception.Message"/>:
    /// message text is for humans and changes freely; the code is the contract.
    /// </para>
    /// </remarks>
    public string? ErrorCode { get; }

    /// <summary>
    /// True when this error was raised while eagerly initializing a global variable and was
    /// deferred to the point of reference, as XSLT 3.0 §2.3.2 (Priming a Stylesheet) permits.
    /// A deferred error must not be converted into "variable not bound" by the XQuery
    /// variable fallback: the variable *is* bound, its initializer failed.
    /// </summary>
    public bool IsDeferredGlobalError { get; set; }

    /// <summary>
    /// Reads a leading error code: a W3C error code — four uppercase letters, four digits, then a
    /// delimiter; <c>phx:</c> and an NCName, then a delimiter; or <c>Q{uri}local</c> and a colon,
    /// as xsl:message and xsl:assert write a code of their <c>error-code</c> attribute.
    /// Deliberately anchored: a code mentioned mid-sentence is prose, not this error's identity.
    /// </summary>
    private static string? ExtractErrorCode(string? message)
    {
        if (message is null)
            return null;
        // A code with a namespace URI, as xsl:message and xsl:assert write it (Q{} for no
        // namespace). The xqt-errors namespace is the local name and the processor's namespace
        // phx:local, as the engine raises them; another namespace keeps the form of the message.
        if (message.StartsWith("Q{", StringComparison.Ordinal)
            && message.IndexOf('}', StringComparison.Ordinal) is var close and > 1
            && NCNameLength(message, close + 1) is var localLength and > 0
            && close + 1 + localLength < message.Length
            && message[close + 1 + localLength] == ':')
        {
            var local = message.Substring(close + 1, localLength);
            return message[2..close] switch
            {
                StylesheetParser.PhxNamespace => "phx:" + local,
                "http://www.w3.org/2005/xqt-errors" => local,
                _ => message[..(close + 1 + localLength)],
            };
        }
        // A code of this processor: phx:XTSE9430 (xslt#582), or phx: and any NCName.
        if (message.StartsWith("phx:", StringComparison.Ordinal))
            return ReadPhxCode(message) is { } local ? "phx:" + local : null;
        if (message.Length < 8)
            return null;
        for (var i = 0; i < 4; i++)
            if (message[i] is < 'A' or > 'Z') return null;
        for (var i = 4; i < 8; i++)
            if (message[i] is < '0' or > '9') return null;
        if (message.Length > 8 && message[8] is not (':' or ' '))
            return null;
        return message[..8];
    }

    /// <summary>
    /// The local name of a code of this processor at the start of <paramref name="message"/>:
    /// <c>phx:</c>, an NCName, then a colon, a space or the end; <c>null</c> when the message does
    /// not start with one. The one reader of this form: <see cref="ErrorCode"/> and xsl:try both
    /// use it, so the two cannot read a message differently.
    /// </summary>
    internal static string? ReadPhxCode(string message)
    {
        if (!message.StartsWith("phx:", StringComparison.Ordinal))
            return null;
        var length = NCNameLength(message, 4);
        return length > 0 && (message.Length == 4 + length || message[4 + length] is ':' or ' ')
            ? message.Substring(4, length)
            : null;
    }

    /// <summary>
    /// The length of the NCName at <paramref name="start"/> of <paramref name="s"/>, 0 when none
    /// starts there; the rule of xsl:message's error-code check.
    /// </summary>
    private static int NCNameLength(string s, int start)
    {
        if (start >= s.Length || !(char.IsLetter(s[start]) || s[start] == '_'))
            return 0;
        var end = start + 1;
        while (end < s.Length && (char.IsLetterOrDigit(s[end]) || s[end] is '_' or '-' or '.'))
            end++;
        return end - start;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XsltException"/> class.
    /// </summary>
    public XsltException()
        : base()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XsltException"/> class with a
    /// specified error message.
    /// </summary>
    /// <param name="message">A description of the XSLT error.</param>
    public XsltException(string message)
        : base(message)
    {
        ErrorCode = ExtractErrorCode(message);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XsltException"/> class with a
    /// specified error message and inner exception.
    /// </summary>
    /// <param name="message">A description of the XSLT error.</param>
    /// <param name="innerException">The exception that caused this XSLT error.</param>
    public XsltException(string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = ExtractErrorCode(message);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XsltException"/> class with a
    /// specified error message and source location.
    /// </summary>
    /// <param name="message">A description of the XSLT error.</param>
    /// <param name="location">
    /// The location in the stylesheet where the error occurred, or <c>null</c>.
    /// </param>
    public XsltException(string message, SourceLocation? location)
        : base(message)
    {
        Location = location;
        ErrorCode = ExtractErrorCode(message);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="XsltException"/> class with a
    /// specified error message, source location, and inner exception.
    /// </summary>
    /// <param name="message">A description of the XSLT error.</param>
    /// <param name="location">
    /// The location in the stylesheet where the error occurred, or <c>null</c>.
    /// </param>
    /// <param name="innerException">The exception that caused this XSLT error.</param>
    public XsltException(string message, SourceLocation? location, Exception innerException)
        : base(message, innerException)
    {
        Location = location;
        ErrorCode = ExtractErrorCode(message);
    }
}
