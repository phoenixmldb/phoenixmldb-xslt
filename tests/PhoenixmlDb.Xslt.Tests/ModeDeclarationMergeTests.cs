using System.Threading.Tasks;
using FluentAssertions;
using PhoenixmlDb.Xslt;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A second <c>xsl:mode</c> for the same mode adds to the first; it does not replace it.
/// Assigning outright made every attribute the earlier declaration set and the later one omits
/// silently revert to its default:
/// <code>
///   &lt;xsl:mode name="X" streamable="yes"/&gt;
///   &lt;xsl:mode name="X" visibility="public"/&gt;   &lt;- X stops being streamable
/// </code>
/// Nothing complained, because the conflict checks compare only attributes that BOTH
/// declarations state, and these two state none in common. W3C mode-1903 exists for this and
/// says so in its own comment: "check that an xsl:mode with no @streamable attribute isn't
/// treated as streamable='no'".
///
/// The loss was never specific to <c>@streamable</c> — <c>on-no-match</c>,
/// <c>on-multiple-match</c>, <c>use-accumulators</c> and <c>visibility</c> all went the same
/// way, which is why the tests below cover more than the attribute that exposed it.
/// </summary>
public class ModeDeclarationMergeTests
{
    private static async Task<string> Run(string decls, string templates, string input)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
                            xmlns:xs="http://www.w3.org/2001/XMLSchema">
              <xsl:output method="text"/>
              {decls}
              {templates}
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        return await t.TransformAsync(input).ConfigureAwait(true);
    }

    [Fact]
    public async Task OnNoMatch_SurvivesALaterDeclarationThatOmitsIt()
    {
        // deep-skip means unmatched elements produce nothing. If the second declaration wiped
        // it, the built-in rule would copy "keep" through and the result would not be empty.
        var r = await Run(
            """<xsl:mode name="m" on-no-match="deep-skip"/><xsl:mode name="m" visibility="public"/>""",
            """<xsl:template match="/"><xsl:apply-templates select="r/*" mode="m"/></xsl:template>""",
            "<r><a>keep</a></r>").ConfigureAwait(true);
        r.Should().BeEmpty();
    }

    [Fact]
    public async Task OnNoMatchInTheLaterDeclaration_StillWins()
    {
        // The later declaration must win for what it DOES state — merging is not "first wins".
        var r = await Run(
            """<xsl:mode name="m" visibility="public"/><xsl:mode name="m" on-no-match="deep-skip"/>""",
            """<xsl:template match="/"><xsl:apply-templates select="r/*" mode="m"/></xsl:template>""",
            "<r><a>keep</a></r>").ConfigureAwait(true);
        r.Should().BeEmpty();
    }

    [Fact]
    public async Task DeclarationOrder_DoesNotChangeTheOutcome()
    {
        const string t = """<xsl:template match="/"><xsl:apply-templates select="r/*" mode="m"/></xsl:template>""";
        var first = await Run(
            """<xsl:mode name="m" on-no-match="deep-skip"/><xsl:mode name="m" visibility="public"/>""",
            t, "<r><a>keep</a></r>").ConfigureAwait(true);
        var second = await Run(
            """<xsl:mode name="m" visibility="public"/><xsl:mode name="m" on-no-match="deep-skip"/>""",
            t, "<r><a>keep</a></r>").ConfigureAwait(true);
        first.Should().Be(second);
    }

    [Fact]
    public async Task ASingleDeclaration_IsUnaffected()
    {
        var r = await Run(
            """<xsl:mode name="m" on-no-match="deep-skip"/>""",
            """<xsl:template match="/"><xsl:apply-templates select="r/*" mode="m"/></xsl:template>""",
            "<r><a>keep</a></r>").ConfigureAwait(true);
        r.Should().BeEmpty();
    }

    [Fact]
    public async Task TwoDeclarationsStatingTheSameAttributeDifferently_StillConflict()
    {
        // Merging must not swallow the conflict the checks above already catch: mode-1502 and
        // mode-1904 depend on this, and both pass today.
        var ss = """
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode name="m" on-no-match="shallow-copy"/>
              <xsl:mode name="m" on-no-match="text-only-copy"/>
              <xsl:template match="/"><out/></xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        var act = async () => await t.LoadStylesheetAsync(ss).ConfigureAwait(true);
        await act.Should().ThrowAsync<System.Exception>().ConfigureAwait(true);
    }
}
