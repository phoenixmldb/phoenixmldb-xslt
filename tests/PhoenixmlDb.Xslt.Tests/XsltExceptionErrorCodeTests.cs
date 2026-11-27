using AwesomeAssertions;
using PhoenixmlDb.XQuery.Ast;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <see cref="XsltException"/> carried its W3C error code only inside the message text, so
/// asking "which error is this?" meant searching prose. The engine did that in seven places,
/// including an accumulator deferral guard testing <c>Message.Contains("XTDE3400")</c> — which
/// would equally have matched an unrelated error that merely mentioned the code.
///
/// These pin the extraction, and especially its ANCHORING: a code is this error's identity only
/// when it leads the message.
/// </summary>
public class XsltExceptionErrorCodeTests
{
    [Theory]
    [InlineData("XTDE0640: Circular reference detected", "XTDE0640")]
    [InlineData("XPST0008: Variable $x is not in scope", "XPST0008")]
    [InlineData("XTSE0010: Invalid XML in stylesheet module", "XTSE0010")]
    [InlineData("SESU0007: unsupported encoding", "SESU0007")]
    [InlineData("phx:XTSE9430: The input cannot be streamed", "phx:XTSE9430")]
    // A code of this processor that is not W3C-shaped, and the forms xsl:message and xsl:assert
    // write for a code of error-code: the processor's namespace with its prefix, the error
    // namespace as the local name, another namespace (or none) as Q{uri}local.
    [InlineData("phx:MYCODE: Transformation terminated: m", "phx:MYCODE")]
    [InlineData("Q{https://schemas.phoenixml.dev/2026/functions}XTDE9430: Transformation terminated: m", "phx:XTDE9430")]
    [InlineData("Q{http://www.w3.org/2005/xqt-errors}FOER0000: Transformation terminated: m", "FOER0000")]
    [InlineData("Q{urn:q}E1: Assertion failed", "Q{urn:q}E1")]
    [InlineData("Q{}MYCODE: Transformation terminated: m", "Q{}MYCODE")]
    public void Leading_code_is_extracted(string message, string expected)
        => new XsltException(message).ErrorCode.Should().Be(expected);

    /// <summary>A code quoted mid-sentence is prose, not identity — this is the whole point.</summary>
    [Theory]
    [InlineData("Recovering from XTDE0640 in an imported module")]
    [InlineData("see XTDE3400 for details")]
    public void Code_mentioned_mid_message_is_not_the_error_code(string message)
        => new XsltException(message).ErrorCode.Should().BeNull();

    [Theory]
    [InlineData("No matching template found")]
    [InlineData("XTDE064: too short")]
    [InlineData("xtde0640: lowercase is not a code")]
    [InlineData("XTDEABCD: digits required")]
    [InlineData("XTDE0640X: not delimited")]
    [InlineData("")]
    [InlineData("Q{urn:q}E1 is not followed by a colon")]
    [InlineData("Q{urn:q}: no local name")]
    [InlineData("Q{urn:q}1E: not an NCName")]
    [InlineData("Q{urn:q: not closed")]
    [InlineData("phx:: no local name")]
    public void Non_codes_yield_null(string message)
        => new XsltException(message).ErrorCode.Should().BeNull();

    /// <summary>Every constructor that takes a message must populate it, not just the simple one.</summary>
    [Fact]
    public void All_message_constructors_populate_the_code()
    {
        const string m = "XTTE0505: required item type is element()";
        new XsltException(m).ErrorCode.Should().Be("XTTE0505");
        new XsltException(m, new InvalidOperationException()).ErrorCode.Should().Be("XTTE0505");
        new XsltException(m, (SourceLocation?)null).ErrorCode.Should().Be("XTTE0505");
        new XsltException(m, null, new InvalidOperationException()).ErrorCode.Should().Be("XTTE0505");
    }

    [Fact]
    public void Parameterless_constructor_has_no_code()
        => new XsltException().ErrorCode.Should().BeNull();
}
