using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §5.5.3: a relative path pattern <c>A/B</c> (child axis) matches a node N iff N
/// matches <c>B</c> AND the PARENT of N matches <c>A</c>. A parentless element (a free-standing
/// constructed element / the root of a temporary tree) has no parent, so it must NOT match a
/// pattern whose leftmost step is a name test requiring a parent (<c>ITEM/*</c>, <c>chapter/para</c>).
///
/// Regression coverage for the sx-square-array / sx-union apply-templates cluster (018/019,
/// union-018): the grounded <c>$insertion</c> elements — passed through
/// <c>apply-templates select="[…, $insertion]?*"</c> under a mode with
/// <c>on-no-match="deep-skip"</c> — were wrongly matched by <c>match="ITEM/*"</c> (the generic
/// "child-or-top" escape returned true when the ancestor walk ran out of parents), so instead of
/// being deep-skipped they were processed and their content leaked into the output.
/// </summary>
public class ParentlessPatternMatchTests
{
    private static async Task<string> Transform(string stylesheet, string input)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(input);
    }

    [Fact]
    public async Task ParentlessElement_DoesNotMatch_NameStepChildPattern_DeepSkipped()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:variable name="insertion" as="element()*"><x>A</x><y>B</y></xsl:variable>
              <xsl:mode name="m" on-no-match="deep-skip"/>
              <xsl:template match="ITEM/*" mode="m">[<xsl:value-of select="."/>]</xsl:template>
              <xsl:template match="/">
                <p><xsl:apply-templates select="$insertion" mode="m"/></p>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // $insertion holds free-standing <x>/<y> with no ITEM parent → they do NOT match ITEM/*
        // → deep-skip → no output. (Was <p>[][]</p> — wrongly matched.)
        (await Transform(ss, "<r/>")).Should().Be("<p/>");
    }

    [Fact]
    public async Task ParentedElement_StillMatches_NameStepChildPattern()
    {
        // Guard against over-correction: a genuinely parented element must still match A/B.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode name="m" on-no-match="deep-skip"/>
              <xsl:template match="ITEM/*" mode="m">[<xsl:value-of select="."/>]</xsl:template>
              <xsl:template match="/">
                <p><xsl:apply-templates select="//ITEM/*" mode="m"/></p>
              </xsl:template>
            </xsl:stylesheet>
            """;
        (await Transform(ss, "<r><ITEM><T>Title1</T><P>9.99</P></ITEM></r>"))
            .Should().Be("<p>[Title1][9.99]</p>");
    }
}
