using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A single-step <c>node()</c> kind-test pattern with a predicate
/// (e.g. <c>match="node()[self::keep]"</c>) must evaluate that predicate. The
/// engine previously short-circuited on node kind alone for
/// <c>KindTest{Kind:None}</c>, silently dropping the predicate — so
/// <c>node()[pred]</c> behaved like bare <c>node()</c> and matched nodes the
/// predicate should reject, then (at higher priority) pre-empted more specific
/// templates. Surfaced via XSpec gather-specs.xsl
/// (<c>match="node()[x:is-user-content(.)]"</c>). Reported by Martin Honnen.
/// </summary>
public sealed class NodeKindTestPredicateMatchTests
{
    private static async Task<string> RunAsync(string stylesheet, string input)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return (await t.TransformAsync(input)).Trim();
    }

    [Fact]
    public async Task NodeKindTest_WithSelfPredicate_MatchesOnlyThePredicatedElement()
    {
        // match="node()[self::keep]" priority=1 must NOT match <drop>; the built-in
        // (or the specific <drop> rule) handles it. Old behavior: node()[self::keep]
        // matched everything, so <drop> was intercepted too.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:template match="/root"><xsl:apply-templates/></xsl:template>
              <xsl:template match="node()[self::keep]" priority="1">[K]</xsl:template>
              <xsl:template match="drop">[D]</xsl:template>
            </xsl:stylesheet>
            """;
        // keep → [K] via the predicated node() rule; drop → [D] via its own rule
        // (would be [K] if the predicate were wrongly dropped).
        (await RunAsync(ss, "<root><keep/><drop/></root>")).Should().Be("[K][D]");
    }

    [Fact]
    public async Task NodeKindTest_PredicatedRule_DoesNotPreemptTunnelSettingTemplate()
    {
        // The XSpec shape: a priority-1 node()[self::leaf] dispatcher must not
        // intercept <desc> (predicate false), so <desc>'s own template runs and
        // sets the tunnel param that a mode-switched consumer requires.
        const string ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="text"/>
              <xsl:mode name="m" on-no-match="shallow-copy"/>
              <xsl:template match="/"><xsl:apply-templates select="desc" mode="m"/></xsl:template>
              <xsl:template match="desc" mode="m">
                <xsl:apply-templates mode="m"><xsl:with-param name="tp" tunnel="yes" select="'ok'"/></xsl:apply-templates>
              </xsl:template>
              <xsl:template match="node()[self::leaf]" as="node()?" mode="m" priority="1">
                <xsl:apply-templates select="." mode="m2"/>
              </xsl:template>
              <xsl:template match="leaf" mode="m2">
                <xsl:param name="tp" as="xs:string" required="yes" tunnel="yes"/>
                <xsl:value-of select="$tp"/>
              </xsl:template>
            </xsl:stylesheet>
            """;
        (await RunAsync(ss, "<desc><leaf/></desc>")).Should().Be("ok");
    }
}
