using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The reader makes one pass over the streamed input, so at most one operand of an operator may
/// consume it. <c>//a + //b</c> asks for two independent descents and is not streamable.
///
/// A detector for this already existed and was only ever run over an <c>xsl:for-each</c> body,
/// so the same expression written directly inside <c>xsl:source-document</c> was accepted and
/// produced a silent wrong answer — W3C error-3430a emitted <c>&lt;out/&gt;</c> where XTSE3430
/// is required.
///
/// The rule is deliberately narrow, and the accept cases below are why. Running the existing
/// per-expression counter over a whole source-document body treats the operands of a union, an
/// except, and a comma-separated sequence as independent consumers, when they are one traversal
/// or a per-item choice; that version failed five accept-side tests elsewhere in this suite.
/// Only arithmetic and comparison operators are covered here.
/// </summary>
public class StreamableTwoSidedOperandTests
{
    // The streamability error is DEFERRED: the parser records it on the instruction and it is
    // raised when xsl:source-document executes, so a stylesheet whose other templates are fine
    // still compiles. These tests therefore have to run the transform, not just load it.
    private static async Task<System.Exception?> Load(string body)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "twosided-" + System.IO.Path.GetRandomFileName());
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var src = System.IO.Path.Combine(dir, "in.xml");
            await System.IO.File.WriteAllTextAsync(src, "<r><a>1</a><b>2</b></r>").ConfigureAwait(true);
            var href = src.Replace("\\", "/", System.StringComparison.Ordinal);
            var ss = $"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                                xmlns:xs="http://www.w3.org/2001/XMLSchema">
                  <xsl:template name="main">
                    <out>
                      <xsl:source-document streamable="yes" href="{href}">{body}</xsl:source-document>
                    </out>
                  </xsl:template>
                </xsl:stylesheet>
                """;
            var t = new XsltTransformer();
            try
            {
                await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
                t.SetInitialTemplate("main");
                await t.TransformAsync("<in/>").ConfigureAwait(true);
                return null;
            }
            catch (System.Exception ex) { return ex; }
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); }
            catch (System.IO.IOException) { /* best effort */ }
        }
    }

    [Theory]
    // Both operands descend into the stream: two passes, one reader.
    [InlineData("""<xsl:value-of select="//a + //b"/>""")]
    [InlineData("""<xsl:value-of select="//a - //b"/>""")]
    [InlineData("""<xsl:value-of select="count(//a) = count(//b)"/>""")]
    [InlineData("""<xsl:value-of select="//a lt //b"/>""")]
    [InlineData("""<xsl:sequence select="//a + //b"/>""")]
    public async Task BothOperandsConsuming_RaisesXTSE3430(string body)
    {
        var ex = await Load(body).ConfigureAwait(true);
        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("XTSE3430");
    }

    [Theory]
    // One consuming operand is the whole point of streaming.
    [InlineData("""<xsl:value-of select="//a + 1"/>""")]
    [InlineData("""<xsl:value-of select="count(//a) + 1"/>""")]
    [InlineData("""<xsl:value-of select="1 + 2"/>""")]
    // A union is ONE traversal, not two — the operands are not independent descents.
    [InlineData("""<xsl:value-of select="//a | //b"/>""")]
    [InlineData("""<xsl:copy-of select="//a except //b"/>""")]
    // A sequence is a per-item choice, not a second pass.
    [InlineData("""<xsl:sequence select="(//a, //b)"/>""")]
    public async Task AtMostOneConsumingOperand_IsAccepted(string body)
    {
        (await Load(body).ConfigureAwait(true)).Should().BeNull();
    }

    [Fact]
    public async Task NonStreamableContext_IsUnaffected()
    {
        // The rule belongs to a streamable source-document; ordinary evaluation may do as it likes.
        var ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/"><out><xsl:value-of select="//a + //b"/></out></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        var act = async () => await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        await act.Should().NotThrowAsync().ConfigureAwait(true);
    }
}
