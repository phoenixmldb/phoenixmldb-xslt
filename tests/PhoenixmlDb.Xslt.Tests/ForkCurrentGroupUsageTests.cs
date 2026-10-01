using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Inside xsl:fork, xsl:for-each-group may traverse current-group() once. Reading only the
/// group's attributes (current-group()/@x) is motionless and does not count (W3C si-fork-814);
/// two real traversals are still rejected (si-fork-951). A duplicate map key in XSLT is XTDE3365.
/// </summary>
public class ForkCurrentGroupUsageTests
{
    private static async Task<string> Run(string body)
    {
        var xsl = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:mode streamable="yes"/>
              <xsl:template match="r">
                <out><xsl:fork><xsl:for-each-group select="i" group-by="@k">{body}</xsl:for-each-group></xsl:fork></out>
              </xsl:template>
            </xsl:stylesheet>
            """;
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(xsl);
            return await t.TransformAsync("<r><i k='a' v='1'><p>x</p></i><i k='a' v='2'><p>y</p></i></r>");
        }
        catch (XsltException e)
        {
            return "ERR " + e.Message;
        }
    }

    [Fact]
    public async Task Attribute_only_uses_of_current_group_are_motionless() =>
        (await Run("<g sum=\"{sum(current-group()/@v)}\" vs=\"{current-group()/@v}\"/>"))
            .Should().Contain("<g sum=\"3\" vs=\"1 2\"/>");

    [Fact]
    public async Task Two_traversals_of_current_group_are_still_rejected() =>
        (await Run("<g n=\"{count(current-group())}\" p=\"{current-group()/p}\"/>"))
            .Should().StartWith("ERR XTSE3430");

    [Fact]
    public async Task A_duplicate_map_key_is_XTDE3365()
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync("""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:param name="k" select="'a'"/>
              <xsl:template match="/"><xsl:sequence select="map{'a': 1, $k: 2}"/></xsl:template>
            </xsl:stylesheet>
            """);
        var act = () => t.TransformAsync("<x/>");
        var ex = (await act.Should().ThrowAsync<Exception>()).Which;
        string? code = null;
        for (Exception? e = ex; e != null && code == null; e = e.InnerException)
            code = e.GetType().GetProperty("ErrorCode")?.GetValue(e) as string;
        code.Should().Be("XTDE3365");
    }
}
