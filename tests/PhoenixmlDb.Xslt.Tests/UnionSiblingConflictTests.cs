using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Two branches of ONE union pattern matching the same node is not a multiple match.
/// </summary>
/// <remarks>
/// <para>
/// A template written <c>match="document-node() | node()"</c> is one rule, so a document node
/// satisfying both branches must not raise XTDE0540 under <c>on-multiple-match="fail"</c>
/// (XSLT erratum, spec bug 30402). The engine splits a union into one template per branch and
/// tags them with a shared <c>UnionGroupId</c> so the conflict check can skip siblings.
/// </para>
/// <para>
/// That id was <c>new object()</c>, minted per indexing pass rather than per source template. A
/// stylesheet reached through more than one include path is indexed more than once, so the two
/// branches of a single rule ended up in different groups, stopped recognising each other, and
/// the suite failed on a conflict that is not one. Found via XSpec's <c>local:report-node</c>
/// mode, whose module is included through several paths.
/// </para>
/// </remarks>
public class UnionSiblingConflictTests
{
    private static async Task<string> RunAsync(string stylesheet, string source)
    {
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(stylesheet);
        return await t.TransformAsync(source);
    }

    /// <summary>Both branches of one union match the document node; that is one rule.</summary>
    [Fact]
    public async Task UnionBranchesMatchingTheSameNode_AreNotAMultipleMatch()
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\">"
            + "<xsl:mode name=\"m\" on-multiple-match=\"fail\" on-no-match=\"fail\"/>"
            + "<xsl:template match=\"document-node() | node()\" mode=\"m\"><hit/></xsl:template>"
            + "<xsl:template match=\"/\"><out><xsl:apply-templates select=\".\" mode=\"m\"/></out></xsl:template>"
            + "</xsl:stylesheet>";

        (await RunAsync(xsl, "<r/>")).Should().Be("<out><hit/></out>");
    }

    /// <summary>
    /// The same shape where the module is included TWICE, which is what forked the group id.
    /// Two genuinely distinct rules at equal priority must still conflict, so this asserts the
    /// suppression is scoped to siblings rather than blanket.
    /// </summary>
    [Fact]
    public async Task TwoDistinctRulesAtEqualPriority_StillConflict()
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" version=\"3.0\">"
            + "<xsl:mode name=\"m\" on-multiple-match=\"fail\" on-no-match=\"fail\"/>"
            + "<xsl:template match=\"r\" mode=\"m\"><a/></xsl:template>"
            + "<xsl:template match=\"r\" mode=\"m\"><b/></xsl:template>"
            + "<xsl:template match=\"/\"><out><xsl:apply-templates select=\"r\" mode=\"m\"/></out></xsl:template>"
            + "</xsl:stylesheet>";

        var act = async () => await RunAsync(xsl, "<r/>");
        (await act.Should().ThrowAsync<PhoenixmlDb.Xslt.Engine.XsltException>())
            .WithMessage("*XTDE0540*");
    }
}
