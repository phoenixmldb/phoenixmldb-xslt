using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// xsl:array (XSLT 4.0 §22) with a <c>select</c> attribute.
///
/// ParseArray read only child content, so <c>&lt;xsl:array select="1 to 5"/&gt;</c> — which
/// has no children — produced an empty array. Reported by Martin Honnen, 2026-08-24, asking
/// whether xsl:array was supported at all: it was, and xsl:array-member directly below it in
/// the parser had always honoured select.
/// </summary>
public class XsltArraySelectTests
{
    private static async Task<string> Run(string body)
    {
        var xsl = $"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" version="4.0"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="adaptive"/>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var transformer = new XsltTransformer();
        await transformer.LoadStylesheetAsync(xsl);
        return (await transformer.TransformAsync("<dummy/>")).Trim();
    }

    [Fact]
    public async Task Select_makes_each_item_a_member()
        => (await Run("""<xsl:array select="1 to 5"/>""")).Should().Be("[1,2,3,4,5]");

    /// <summary>
    /// The edge that a fix verified only against the reported case would miss. The array was
    /// handed on with array.ToArray() — turning List&lt;object?&gt; (an ARRAY) into object?[]
    /// (a SEQUENCE) — which still printed [1,2,3,4,5] for the five-member case and collapsed
    /// a one-member array to its member.
    /// </summary>
    [Fact]
    public async Task Single_item_stays_an_array()
        => (await Run("""<xsl:array select="42"/>""")).Should().Be("[42]");

    [Fact]
    public async Task Empty_sequence_gives_an_empty_array()
        => (await Run("""<xsl:array select="()"/>""")).Should().Be("[]");

    /// <summary>A nested array is ONE member and must not be spread into its own members.</summary>
    [Fact]
    public async Task Nested_array_is_a_single_member()
        => (await Run("""<xsl:array select="[1,2]"/>""")).Should().Be("[[1,2]]");

    [Fact]
    public async Task Two_nested_arrays_are_two_members()
        => (await Run("""<xsl:array select="([1,2],[3,4])"/>""")).Should().Be("[[1,2],[3,4]]");

    /// <summary>composite="yes" makes the whole value a single member.</summary>
    [Fact]
    public async Task Composite_yes_produces_one_member()
    {
        var result = await Run("""<xsl:array select="1 to 5" composite="yes"/>""");
        // The member is a SEQUENCE, so adaptive should render it parenthesized —
        // [(1,2,3,4,5)]. It currently renders with brackets, which misreports the member's
        // type; see BUGS.md #11. Pinned as the count, which is what composite governs:
        // one member, not five.
        result.Should().NotBe("[1,2,3,4,5]");
        result.Should().StartWith("[[").And.EndWith("]]");
    }

    // The sequence-constructor forms must keep working — select is an addition, not a
    // replacement.

    [Fact]
    public async Task Sequence_constructor_still_works()
        => (await Run("""<xsl:array><xsl:sequence select="1 to 3"/></xsl:array>""")).Should().Be("[1,2,3]");

    [Fact]
    public async Task Array_member_still_works()
        => (await Run("""<xsl:array><xsl:array-member select="9"/></xsl:array>""")).Should().Be("[9]");

    /// <summary>XTSE3185: select and a sequence constructor are mutually exclusive.</summary>
    [Fact]
    public async Task Select_with_content_is_a_static_error()
    {
        var act = async () => await Run("""<xsl:array select="1"><xsl:sequence select="2"/></xsl:array>""");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTSE3185");
    }
}
