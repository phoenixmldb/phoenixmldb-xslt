using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// XSLT 3.0 §18.2.2: for the principal source tree, "the accumulators that are applicable are
/// those determined by the xsl:mode declaration of the initial mode. This means that in the
/// absence of an xsl:mode declaration, no accumulators are applicable." Reading one that is not
/// applicable is XTDE3362. Any mode without use-accumulators used to count as "all", so
/// accumulator-before worked where Saxon raises XTDE3362 (xslt#213, Martin Honnen).
/// </summary>
public sealed class AccumulatorPrincipalTreeApplicabilityTests
{
    private const string Input = "<doc><chapter/><chapter/></doc>";

    private static async Task<string> RunAsync(string modes, string initialMode = "", string body = "<xsl:apply-templates select='doc/chapter[2]'/>")
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="text"/>
              <xsl:accumulator name="n" as="xs:integer" initial-value="0">
                <xsl:accumulator-rule match="chapter" select="$value + 1"/>
              </xsl:accumulator>
              {modes}
              <xsl:template match="/" mode="#all">{body}</xsl:template>
              <xsl:template match="chapter" mode="#all">
                <xsl:value-of select="accumulator-before('n')"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        if (initialMode.Length > 0)
            t.SetInitialMode(initialMode);
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync(Input)).Trim();
    }

    [Theory]
    [InlineData("")]                                                        // no xsl:mode at all
    [InlineData("<xsl:mode on-no-match='shallow-copy'/>")]                  // Martin's: declared, no use-accumulators
    [InlineData("<xsl:mode use-accumulators=''/>")]
    public async Task Not_listed_for_the_initial_mode_is_XTDE3362(string modes)
    {
        var act = () => RunAsync(modes);
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE3362");
    }

    [Theory]
    [InlineData("<xsl:mode use-accumulators='n'/>")]
    [InlineData("<xsl:mode use-accumulators='#all'/>")]
    public async Task Listed_for_the_initial_mode_is_read(string modes) =>
        (await RunAsync(modes)).Should().Be("2");

    /// <summary>The initial mode decides for the whole tree; switching mode later changes nothing.</summary>
    [Fact]
    public async Task A_later_mode_without_the_attribute_keeps_the_initial_modes_answer() =>
        (await RunAsync("<xsl:mode use-accumulators='n'/><xsl:mode name='other'/>",
            body: "<xsl:apply-templates select='doc/chapter[2]' mode='other'/>")).Should().Be("2");

    [Fact]
    public async Task A_later_mode_listing_it_does_not_make_it_applicable()
    {
        var act = () => RunAsync("<xsl:mode/><xsl:mode name='other' use-accumulators='n'/>",
            body: "<xsl:apply-templates select='doc/chapter[2]' mode='other'/>");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE3362");
    }

    [Fact]
    public async Task A_named_initial_mode_uses_its_own_declaration() =>
        (await RunAsync("<xsl:mode name='start' use-accumulators='n'/>", initialMode: "start")).Should().Be("2");

    /// <summary>
    /// Started by a named template, the source is only the global context item: there is no
    /// initial match selection, so the initial-mode rule does not govern it (W3C mode-1511).
    /// </summary>
    [Fact]
    public async Task A_named_template_start_leaves_the_global_context_tree_to_the_general_rule()
    {
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="text"/>
              <xsl:accumulator name="n" as="xs:integer" initial-value="0">
                <xsl:accumulator-rule match="chapter" select="$value + 1"/>
              </xsl:accumulator>
              <xsl:template name="main"><xsl:value-of select="(//chapter)[2]/accumulator-before('n')"/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        t.SetInitialTemplate("main");
        await t.LoadStylesheetAsync(ss);
        (await t.TransformAsync(Input)).Trim().Should().Be("2");
    }
}
