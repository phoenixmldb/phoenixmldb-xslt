using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A named template that walks a string one character per call, passing
/// <c>substring($string, 2)</c> to itself, as the ISO Schematron skeleton's
/// <c>sch-check:strip-strings</c>, <c>test-paren</c> and <c>test-sqb</c> do (xquery#287). The
/// parameter holds a slice of the original string, not a copy of its rest. These tests check that
/// the walk gives what the same question asked of the whole string gives, surrogate pairs included;
/// that a parameter bound that way is an ordinary string to everything else; and that the walk no
/// longer costs the square of the length.
/// </summary>
[Collection(nameof(SubstringRecursionTests))]
public sealed class SubstringRecursionTests
{
    private const string Walk = """
        <xsl:template name="walk">
          <xsl:param name="string"/>
          <xsl:choose>
            <xsl:when test="string-length($string) = 0"/>
            <xsl:otherwise>
              <xsl:value-of select="substring($string, 1, 1)"/>
              <xsl:if test="starts-with($string, '(')">*</xsl:if>
              <xsl:value-of select="string-length($string)"/>
              <xsl:text>|</xsl:text>
              <xsl:call-template name="walk">
                <xsl:with-param name="string" select="substring($string, 2)"/>
              </xsl:call-template>
            </xsl:otherwise>
          </xsl:choose>
        </xsl:template>
        """;

    private static async Task<string> Run(string body, string templates = Walk)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="3.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:template name="main">{body}</xsl:template>
              {templates}
            </xsl:stylesheet>
            """);
        t.SetInitialTemplate("main");
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("a(b)c")]
    [InlineData("((x")]
    [InlineData("x\U0001F600(y\U0001D11E")]
    [InlineData("\U0001F600\U0001F600")]
    public async Task The_walk_gives_what_the_whole_string_gives(string text)
    {
        var walked = await Run($"""
            <xsl:call-template name="walk"><xsl:with-param name="string" select="'{text}'"/></xsl:call-template>
            """);
        var direct = await Run($"""
            <xsl:variable name="s" select="'{text}'"/>
            <xsl:for-each select="1 to string-length($s)">
              <xsl:variable name="rest" select="substring($s, .)"/>
              <xsl:value-of select="substring($rest, 1, 1)"/>
              <xsl:if test="starts-with($rest, '(')">*</xsl:if>
              <xsl:value-of select="string-length($rest)"/>
              <xsl:text>|</xsl:text>
            </xsl:for-each>
            """);
        walked.Should().Be(direct);
    }

    [Fact]
    public async Task A_parameter_bound_to_a_substring_is_an_ordinary_string()
        => (await Run("""
                <xsl:call-template name="use"><xsl:with-param name="s" select="substring('abcd', 2)"/></xsl:call-template>
                """, """
                <xsl:template name="use">
                  <xsl:param name="s"/>
                  <xsl:value-of select="$s instance of xs:string, $s eq 'bcd', concat($s, '!'), contains($s, 'cd'),
                    string-to-codepoints($s)[1], map{$s: 1}?bcd, (function() { $s })(), upper-case($s), $s"/>
                  <xsl:text> </xsl:text>
                  <xsl:value-of select="$s"/>
                  <xsl:call-template name="typed"><xsl:with-param name="t" select="substring($s, 2)"/></xsl:call-template>
                </xsl:template>
                <xsl:template name="typed">
                  <xsl:param name="t" as="xs:string"/>
                  <xsl:value-of select="' ', $t, $t instance of xs:string"/>
                </xsl:template>
                """)).Should().Be("true true bcd! true 98 1 bcd BCD bcd bcd  cd true");

    // A parameter declared as="xs:string" holds the slice as well (xquery#293); a type derived
    // from xs:string casts, and holds the string.
    [Theory]
    [InlineData("")]
    [InlineData(" as=\"xs:string\"")]
    public async Task The_walk_over_a_long_string_is_linear_with_or_without_xs_string(string asString)
        => await AssertLinear(asString);

    [Fact]
    public async Task A_parameter_of_a_type_derived_from_xs_string_casts_the_value()
        => (await Run("""
                <xsl:call-template name="tok"><xsl:with-param name="t" select="substring('  a  b ', 1)"/></xsl:call-template>
                """, """
                <xsl:template name="tok">
                  <xsl:param name="t" as="xs:token"/>
                  <xsl:value-of select="'[' || $t || ']', $t instance of xs:token"/>
                </xsl:template>
                """)).Should().Be("[a b] true");

    [Fact]
    public async Task The_walk_over_a_long_string_is_linear()
        => await AssertLinear("");

    private static async Task AssertLinear(string asString)
    {
        var text = new string('x', 20_000);
        GC.Collect();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var count = await Run($"""
            <xsl:call-template name="count"><xsl:with-param name="string" select="'{text}'"/></xsl:call-template>
            """, $"""
            <xsl:template name="count">
              <xsl:param name="string"{asString}/>
              <xsl:param name="n" select="0"/>
              <xsl:choose>
                <xsl:when test="string-length($string) = 0"><xsl:value-of select="$n"/></xsl:when>
                <xsl:otherwise>
                  <xsl:call-template name="count">
                    <xsl:with-param name="string" select="substring($string, 2)"/>
                    <xsl:with-param name="n" select="$n + 1"/>
                  </xsl:call-template>
                </xsl:otherwise>
              </xsl:choose>
            </xsl:template>
            """);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        count.Should().Be("20000");
        // A copy of the rest at each call is 20,000² / 2 chars = 400 MB; the walk without copies
        // allocates a few KB per call.
        allocated.Should().BeLessThan(200L * 1024 * 1024);
    }
}

[CollectionDefinition(nameof(SubstringRecursionTests), DisableParallelization = true)]
public sealed class SubstringRecursionSerial;
