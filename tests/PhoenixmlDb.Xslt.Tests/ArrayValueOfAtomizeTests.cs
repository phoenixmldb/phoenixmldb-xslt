using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XPath/XSLT 4.0: <c>xsl:value-of</c> atomizes its selected sequence with <c>fn:data</c>
/// before joining with the separator. <c>fn:data</c> of an array recursively flattens ALL
/// members into one atomic sequence (§ "Atomization" — the result is the concatenation of
/// <c>fn:data</c> applied to each member). So a two-member array whose members are themselves
/// sequences must contribute every atom individually, separated by the value-of separator —
/// NOT one space-joined string per member.
///
/// Regression coverage for the sx-square-array streaming cluster (032/033/034/035): the engine
/// expanded the array to its MEMBERS and string-valued each sequence-valued member into a single
/// token (<c>MHK MMP|A B</c>) instead of flattening to atoms (<c>MHK|MMP|A|B</c>). The defect is
/// in the core value-of merge path (non-streaming), surfaced by the streaming test stylesheets.
/// </summary>
public class ArrayValueOfAtomizeTests
{
    private static async Task<string> Transform(string stylesheet, string input = "<in/>")
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(input);
    }

    [Fact]
    public async Task ValueOf_ArrayOfSequenceMembers_ExplicitSeparator_FlattensAllAtoms()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
                <out><xsl:value-of select="[('MHK','MMP'),('A','B')]" separator="|"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // fn:data([('MHK','MMP'),('A','B')]) = ('MHK','MMP','A','B') → join with '|'.
        (await Transform(ss)).Should().Be("<out>MHK|MMP|A|B</out>");
    }

    [Fact]
    public async Task ValueOf_ArrayOfSequenceMembers_DefaultSeparator_FlattensAllAtoms()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
                <out><xsl:value-of select="[('a','b'),('c','d')]"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // Default separator is a single space.
        (await Transform(ss)).Should().Be("<out>a b c d</out>");
    }

    [Fact]
    public async Task ValueOf_ArrayWithSingletonAndEmptyMembers_FlattensAtomsOnly()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
                <out><xsl:value-of select="[('a','b'), (), 'c']" separator="|"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // fn:data([('a','b'), (), 'c']) = ('a','b','c') — an empty member contributes nothing.
        (await Transform(ss)).Should().Be("<out>a|b|c</out>");
    }

    [Fact]
    public async Task ValueOf_NestedArray_FlattensRecursively()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:template match="/" name="xsl:initial-template">
                <out><xsl:value-of select="[ [('a','b')], 'c']" separator="|"/></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        // fn:data of a nested array recurses: data([[('a','b')],'c']) = ('a','b','c').
        (await Transform(ss)).Should().Be("<out>a|b|c</out>");
    }
}
