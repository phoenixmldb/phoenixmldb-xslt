using FluentAssertions;
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
