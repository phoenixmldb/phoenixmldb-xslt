using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// The captured substrings of an enclosing <c>xsl:analyze-string</c> are not in scope inside a
/// PATTERN: <c>regex-group()</c> evaluated there returns the empty sequence, so a predicate using
/// it cannot see the outer match (W3C <c>analyze-string-076</c>, which puts <c>regex-group()</c>
/// in <c>@group-starting-with</c> and asserts it sees nothing).
///
/// The groups live in a context variable that a pattern predicate could read straight through, so
/// a pattern evaluated inside a matching substring saw the enclosing captures. The function-call
/// path already shadows these for the same reason.
/// </summary>
public sealed class RegexGroupInPatternTests
{
    /// <summary>
    /// The nodes are bound BEFORE the analyze-string: inside xsl:matching-substring the context
    /// item is the matched STRING, so an axis step there is an error rather than a selection.
    ///
    /// Three items, and a group-starting-with pattern whose predicate asks for regex-group(1).
    /// Inside the pattern that is the empty sequence, so no item starts a new group and all three
    /// stay in ONE group. When the outer captures leak in, the predicate is true for every item
    /// and three groups are produced — so the count discriminates the two behaviours.
    /// </summary>
    [Fact]
    public async Task RegexGroup_InsideGroupStartingWithPattern_IsEmpty()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="items" select="/items/i"/>
                <xsl:analyze-string select="'abc'" regex="(a)(b)(c)">
                  <xsl:matching-substring>
                    <xsl:for-each-group select="$items" group-starting-with="i[regex-group(1) = 'a']">
                      <xsl:text>[g]</xsl:text>
                    </xsl:for-each-group>
                  </xsl:matching-substring>
                </xsl:analyze-string>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        var result = (await t.TransformAsync("<items><i>1</i><i>2</i><i>3</i></items>")).Trim();

        result.Should().Be("[g]",
            "regex-group() is the empty sequence inside a pattern, so no item starts a new group");
    }

    /// <summary>
    /// Guard: regex-group() still works where it IS in scope — directly inside
    /// xsl:matching-substring — so the shadowing is confined to pattern evaluation.
    /// </summary>
    [Fact]
    public async Task RegexGroup_InsideMatchingSubstring_StillReturnsCaptures()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:analyze-string select="'abc'" regex="(a)(b)(c)">
                  <xsl:matching-substring>
                    <xsl:value-of select="regex-group(1), regex-group(2), regex-group(3)"/>
                  </xsl:matching-substring>
                </xsl:analyze-string>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        (await t.TransformAsync("<in/>")).Trim().Should().Be("a b c");
    }

    /// <summary>
    /// Guard: a pattern predicate that does not mention regex-group() is unaffected, so the
    /// shadowing cannot be credited for changing ordinary grouping.
    /// </summary>
    [Fact]
    public async Task OrdinaryGroupStartingWithPattern_IsUnaffected()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/">
                <xsl:variable name="items" select="/items/i"/>
                <xsl:analyze-string select="'abc'" regex="(a)(b)(c)">
                  <xsl:matching-substring>
                    <xsl:for-each-group select="$items" group-starting-with="i[@start='y']">
                      <xsl:text>[g]</xsl:text>
                    </xsl:for-each-group>
                  </xsl:matching-substring>
                </xsl:analyze-string>
              </xsl:template>
            </xsl:stylesheet>
            """;

        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        (await t.TransformAsync("<items><i>1</i><i start='y'>2</i><i start='y'>3</i></items>"))
            .Trim().Should().Be("[g][g][g]");
    }
}
