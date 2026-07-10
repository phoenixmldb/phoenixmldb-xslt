using System.Text;
using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Regression tests for <c>XsltTransformEngine.ScopedOutputBuffer</c> (bug-0305).
/// A scope records the output length when opened; the buffer can then be
/// <c>Clear()</c>ed out from under an open scope (an xsl:result-document or a
/// finalize/flush path resets <c>_output</c>). When that happens the buffer is
/// shorter than the scope's saved length. Previously:
/// <list type="bullet">
///   <item><c>WrittenLength</c> went negative, so <c>GetWritten()</c> passed a
///   negative count to <c>StringBuilder.ToString</c> → <c>ArgumentOutOfRangeException</c>
///   (observed intermittently only under parallel full-suite load).</item>
///   <item><c>Dispose()</c>'s <c>Length = _savedLength</c> would pad the buffer back
///   out with NUL characters.</item>
/// </list>
/// The scope is now robust to a shorter buffer: written length clamps to 0, and
/// Dispose only shrinks.
/// </summary>
public sealed class ScopedOutputBufferTests
{
    [Fact]
    public void GetWritten_ReturnsSliceWrittenSinceScopeOpened()
    {
        var sb = new StringBuilder("PREFIX");
        var scope = new XsltTransformEngine.ScopedOutputBuffer(sb);
        sb.Append("inner");

        scope.WrittenLength.Should().Be(5);
        scope.GetWritten().Should().Be("inner");
    }

    [Fact]
    public void Dispose_TruncatesBackToSavedLength()
    {
        var sb = new StringBuilder("PREFIX");
        var scope = new XsltTransformEngine.ScopedOutputBuffer(sb);
        sb.Append("inner");

        scope.Dispose();

        sb.ToString().Should().Be("PREFIX");
    }

    [Fact]
    public void GetWritten_BufferClearedUnderScope_ReturnsEmptyWithoutThrowing()
    {
        var sb = new StringBuilder("PREFIX");
        var scope = new XsltTransformEngine.ScopedOutputBuffer(sb);
        sb.Append("inner");

        // An xsl:result-document / finalize-flush path clears _output underneath the open scope.
        sb.Clear();

        scope.WrittenLength.Should().Be(0);
        scope.GetWritten().Should().BeEmpty();
    }

    [Fact]
    public void Dispose_BufferClearedUnderScope_DoesNotPadGrow()
    {
        var sb = new StringBuilder("PREFIX");
        var scope = new XsltTransformEngine.ScopedOutputBuffer(sb);
        sb.Append("inner");
        sb.Clear();

        scope.Dispose();

        // Must stay empty, not be padded back out to the saved length with NUL chars.
        sb.Length.Should().Be(0);
    }
}
