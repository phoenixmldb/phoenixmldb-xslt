using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A grouping key is ATOMIZED, and atomizing an element requires that element's descendants — so a
/// key expression that RETURNS elements is consuming even when it navigates nowhere.
///
/// Reported by Martin Honnen (#147): `group-adjacent="self::Line"` under `streamable="yes"` was
/// accepted and then failed at runtime with `XTDE1071: current-grouping-key() called when there is
/// no current grouping key`. That error names the wrong thing — the key was never established
/// because the expression is not one a streamed grouping can evaluate — and #152 fixed the runtime
/// half. This is the static half: the stylesheet must be rejected by streamability analysis.
///
/// `self::Line` is `Axis.Self`, and the existing motionless rule tests for downward navigation
/// (Child/Descendant/DescendantOrSelf), so it passed straight through.
/// </summary>
public class GroupKeyAtomizationStreamabilityTests
{
    private const string Input = """
        <Report>
          <Date id="d">2020-07-25</Date>
          <Number id="n">12</Number>
          <Line id="x"><LineNumber>1</LineNumber><Description>a</Description><Quantity>5</Quantity></Line>
          <Line id="x"><LineNumber>2</LineNumber><Description>b</Description><Quantity>7</Quantity></Line>
        </Report>
        """;

    private static string Stylesheet(string groupAttr, bool streamable = true) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                        xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
          <xsl:mode on-no-match="deep-skip" streamable="{(streamable ? "yes" : "no")}"/>
          <xsl:template match="/Report">
            <INV>
              <xsl:for-each-group select="*" {groupAttr}>
                <G><xsl:value-of select="count(current-group())"/></G>
              </xsl:for-each-group>
            </INV>
          </xsl:template>
        </xsl:stylesheet>
        """;

    private static async Task<Exception?> TryRun(string groupAttr, bool streamable = true)
    {
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(Stylesheet(groupAttr, streamable), null, null, null);
            await t.TransformAsync(Input);
            return null;
        }
        catch (Exception ex) { return ex; }
    }

    /// <summary>
    /// Martin's case: the stylesheet must be REJECTED statically with XTSE3430, not run.
    ///
    /// The assertion is on the ERROR CODE, and that is not fussiness. The first version asserted
    /// only "an exception whose message mentions group-adjacent", and it PASSED on unfixed source
    /// — because unfixed source runs the stylesheet and fails at runtime with
    /// `XTTE1100: The group-adjacent expression must return a single atomic value; it returned an
    /// empty sequence`, which mentions group-adjacent. A test that cannot tell a static rejection
    /// from a runtime failure is not testing this rule at all.
    /// </summary>
    [Fact]
    public async Task Element_returning_group_adjacent_is_rejected_under_streamable()
    {
        var ex = await TryRun("group-adjacent=\"self::Line\"");
        ex.Should().NotBeNull("a non-motionless group-adjacent must be rejected by streamability analysis");
        ex!.Message.Should().Contain("XTSE3430",
            "the rejection must be the STATIC streamability error, not a runtime symptom");
        ex.Message.Should().NotContain("XTDE1071",
            "XTDE1071 is the runtime symptom this replaces — it points away from the real cause");
        ex.Message.Should().Contain("group-adjacent");
    }

    /// <summary>
    /// The same shape on group-by, which has the identical atomization rule. On unfixed source
    /// this one produces NO ERROR AT ALL — silently wrong groups — which is why it is here
    /// separately rather than folded into the case above.
    /// </summary>
    [Fact]
    public async Task Element_returning_group_by_is_rejected_under_streamable()
    {
        var ex = await TryRun("group-by=\"self::Line\"");
        ex.Should().NotBeNull("unfixed source produces no error here at all — silently wrong groups");
        ex!.Message.Should().Contain("XTSE3430");
        ex.Message.Should().Contain("group-by");
    }

    // ---- controls: every one of these must KEEP working -----------------------------------

    /// <summary>
    /// What Martin meant to write. `fn:boolean` consumes the node without atomizing it, so the key
    /// is motionless. A rule phrased as "mentions a self step" would reject this.
    /// </summary>
    [Fact]
    public async Task Boolean_of_a_self_step_is_still_streamable()
        => (await TryRun("group-adjacent=\"boolean(self::Line)\"")).Should().BeNull();

    /// <summary>
    /// An ATTRIBUTE key is motionless — an attribute's value is available from the start tag. A
    /// rule phrased over "returns nodes" rather than "returns elements" would wrongly reject this,
    /// and attribute keys are ordinary in streamed grouping.
    ///
    /// Every element in the fixture carries @id deliberately: a missing attribute makes the key an
    /// empty sequence and the case fails with XTTE1100 at RUNTIME, which looks like a rejection
    /// and is not one. The first version of this test had no @id and failed for exactly that
    /// reason while the rule under test was behaving correctly.
    /// </summary>
    [Fact]
    public async Task An_attribute_key_is_still_streamable()
        => (await TryRun("group-adjacent=\"@id\"")).Should().BeNull();

    /// <summary>A key computed from the node's name, not its content.</summary>
    [Fact]
    public async Task A_name_based_key_is_still_streamable()
        => (await TryRun("group-adjacent=\"string(node-name())\"")).Should().BeNull();

    /// <summary>The positional key shape the W3C strm sets use.</summary>
    [Fact]
    public async Task A_positional_key_is_still_streamable()
        => (await TryRun("group-adjacent=\"(position()-1) idiv 2\"")).Should().BeNull();

    /// <summary>
    /// Not streamable, so the rule must not apply: the same expression is perfectly legal here and
    /// must still produce the non-streaming diagnostic rather than a streamability rejection.
    /// </summary>
    [Fact]
    public async Task The_rule_does_not_fire_when_the_mode_is_not_streamable()
    {
        var ex = await TryRun("group-adjacent=\"self::Line\"", streamable: false);
        if (ex != null) ex.Message.Should().NotContain("not streamable");
    }
}
