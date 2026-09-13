using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A match pattern used by a streamable mode must be motionless, and <c>fn:current()</c> is
/// only sometimes so. The distinction is not whether the pattern mentions <c>current()</c> —
/// W3C sf-current-100 and sf-current-902 share three identical patterns, and one must be
/// accepted while the other is rejected. It is what the pattern does with it:
///
/// <list type="bullet">
/// <item>reading its NAME (<c>namespace-uri(current())</c>) — on the start tag, motionless</item>
/// <item>navigating to an ATTRIBUTE or an ANCESTOR (<c>current()/../@CAT</c>) — motionless</item>
/// <item>taking its VALUE (<c>current()='x'</c>) — atomizes an element, reads its text, NOT</item>
/// <item>navigating DOWN (<c>current()/text()</c>) — content not yet delivered, NOT</item>
/// </list>
///
/// The checker tested last(), position() and context-item access, but not current() at all, so
/// every one of these was accepted. The failure then surfaced far away and wearing someone
/// else's name: the engine ran on and died with XTDE0040 looking for an entry point the
/// erroring stylesheet never declared.
/// </summary>
public class StreamablePatternCurrentTests
{
    private static async Task<System.Exception?> Load(string match)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" default-mode="m"
                            exclude-result-prefixes="#all">
              <xsl:mode name="m" streamable="yes" on-no-match="shallow-copy"/>
              <xsl:template match="{match}"><xsl:next-match/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        try { await t.LoadStylesheetAsync(ss).ConfigureAwait(true); return null; }
        catch (System.Exception ex) { return ex; }
    }

    [Theory]
    // Name inspection and upward/attribute navigation: the start tag and the ancestor chain
    // are both available while streaming.
    [InlineData("ITEM[namespace-uri(current()) = '']")]
    [InlineData("DIMENSIONS[current()/@UNIT='in']")]
    [InlineData("AUTHOR[current()/../@CAT='H']")]
    [InlineData("AUTHOR[..[@CAT = current()/../@CAT]]")]
    [InlineData("*[ancestor::*[node-name(.) = node-name(current())]]")]
    public async Task MotionlessUseOfCurrent_IsAccepted(string match)
    {
        (await Load(match).ConfigureAwait(true)).Should().BeNull();
    }

    [Theory]
    // Taking the value of current(), or reading below it.
    [InlineData("AUTHOR[current()='Jasper Fforde']")]
    [InlineData("AUTHOR[current()/text()='Jasper Fforde']")]
    [InlineData("AUTHOR[string(current())='x']")]
    [InlineData("AUTHOR[current()/child::name='x']")]
    public async Task NonMotionlessUseOfCurrent_RaisesXTSE3430(string match)
    {
        var ex = await Load(match).ConfigureAwait(true);
        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("XTSE3430");
    }

    [Fact]
    public async Task APatternWithoutCurrent_IsUnaffected()
    {
        (await Load("ITEM[@CAT='H']").ConfigureAwait(true)).Should().BeNull();
    }

    [Fact]
    public async Task CurrentInANonStreamableMode_IsStillFine()
    {
        // The rule belongs to streamable modes only; an ordinary mode may do as it likes.
        var ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="AUTHOR[current()='x']"><xsl:next-match/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        var act = async () => await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        await act.Should().NotThrowAsync().ConfigureAwait(true);
    }
}
