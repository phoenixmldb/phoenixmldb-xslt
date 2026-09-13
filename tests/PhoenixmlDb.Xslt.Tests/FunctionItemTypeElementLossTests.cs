using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An <c>xsl:function</c> declared <c>as="item()*"</c> silently lost element nodes.
///
/// A function body writes elements as markup into the output buffer and text into the sequence
/// accumulator. One branch of the result assembly parses that buffer back into nodes, and its
/// guard listed the node item types and <c>func.As == null</c> — but not <c>item()</c>. So with
/// <c>as="item()*"</c> the branch was skipped, and the branch below returned the accumulator's
/// text items and discarded the buffer the element was in.
///
/// The element did not raise an error and did not arrive as a string. It was gone:
/// <code>
///   &lt;xsl:function name="f:m"&gt;               A&lt;e/&gt;B  ->  3 items, T E T
///   &lt;xsl:function name="f:m" as="item()*"&gt;  A&lt;e/&gt;B  ->  2 items, T T
/// </code>
/// Declaring the widest type in the language was narrower than declaring nothing at all — and
/// <c>item()*</c> is also what a function gets by default, so the two spellings of the same
/// type disagreed.
/// </summary>
public class FunctionItemTypeElementLossTests
{
    private static async Task<string> Run(string functions, string body)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:f="urn:f"
                            exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              {functions}
              <xsl:template match="/" name="xsl:initial-template">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        return await t.TransformAsync("<in/>").ConfigureAwait(true);
    }

    // "TET" etc. — one letter per returned item, so a lost item shows up as a shorter string
    // rather than as a count that could be right for the wrong reason.
    private const string Kinds =
        """string-join(f:m() ! (if (. instance of element()) then 'E' else 'T'), '')""";

    private static string Fn(string asAttr) =>
        $"""<xsl:function name="f:m"{asAttr}><xsl:text>A</xsl:text><e/><xsl:text>B</xsl:text></xsl:function>""";

    [Theory]
    [InlineData("")]                        // no as — worked before
    [InlineData(""" as="item()*" """)]      // the defect
    [InlineData(""" as="item()+" """)]
    [InlineData(""" as="node()*" """)]      // worked before
    public async Task TextElementText_ReturnsAllThreeItems(string asAttr)
    {
        (await Run(Fn(asAttr.Trim() is "" ? "" : " " + asAttr.Trim()),
            $"""<xsl:value-of select="{Kinds}"/>""").ConfigureAwait(true))
            .Should().Be("TET");
    }

    [Fact]
    public async Task DeclaringItemStar_AgreesWithDeclaringNothing()
    {
        // The two spellings of the same type must not disagree; this is the invariant the
        // defect broke, stated directly rather than as two separate expectations.
        var declared = await Run(Fn(""" as="item()*" """), $"""<xsl:value-of select="{Kinds}"/>""")
            .ConfigureAwait(true);
        var undeclared = await Run(Fn(""), $"""<xsl:value-of select="{Kinds}"/>""")
            .ConfigureAwait(true);
        declared.Should().Be(undeclared);
    }

    [Fact]
    public async Task TwoElements_AreNotCollapsedIntoOneTextItem()
    {
        var fn = """<xsl:function name="f:m" as="item()*"><e/><g/></xsl:function>""";
        (await Run(fn, $"""<xsl:value-of select="{Kinds}"/>""").ConfigureAwait(true))
            .Should().Be("EE");
    }

    [Fact]
    public async Task ElementThenText_KeepsBoth()
    {
        var fn = """<xsl:function name="f:m" as="item()*"><e/><xsl:text>B</xsl:text></xsl:function>""";
        (await Run(fn, $"""<xsl:value-of select="{Kinds}"/>""").ConfigureAwait(true))
            .Should().Be("ET");
    }

    [Fact]
    public async Task ReturnedElementKeepsItsName()
    {
        // Guard against "fixing" the count by producing three items of the wrong kinds.
        var fn = """<xsl:function name="f:m" as="item()*"><xsl:text>A</xsl:text><e/><xsl:text>B</xsl:text></xsl:function>""";
        (await Run(fn, """<xsl:value-of select="f:m()[. instance of element()]/local-name()"/>""")
            .ConfigureAwait(true)).Should().Be("e");
    }

    [Fact]
    public async Task PlainTextBody_StillReturnsTextNotAnElement()
    {
        // A body with no markup must be unaffected by widening the parse branch.
        var fn = """<xsl:function name="f:m" as="item()*"><xsl:text>A</xsl:text></xsl:function>""";
        (await Run(fn, """<xsl:value-of select="string(f:m()) || ':' || count(f:m())"/>""")
            .ConfigureAwait(true)).Should().Be("A:1");
    }
}
