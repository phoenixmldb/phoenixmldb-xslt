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
    /// <c>XTSE0010</c> — or <c>null</c> when the message carries none.
    /// </summary>
    /// <remarks>
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
    /// Reads a leading W3C error code — four uppercase letters, four digits, then a delimiter.
    /// Deliberately anchored: a code mentioned mid-sentence is prose, not this error's identity.
    /// </summary>
    private static string? ExtractErrorCode(string? message)
    {
        if (message is null || message.Length < 8)
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
