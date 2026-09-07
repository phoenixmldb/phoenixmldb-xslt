using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// <c>xsl:switch</c> (XSLT 4.0) selects a branch by comparing the switch operand against each
/// <c>xsl:when/@test</c> sequence of CANDIDATE VALUES — it is not xsl:choose with a subject.
/// </summary>
/// <remarks>
/// <para>
/// The engine took the effective boolean value of @test. That raised FORG0006 for any when
/// listing alternatives, e.g. <c>test="('jpg','JPG','jpeg','JPEG')"</c> — "effective boolean
/// value not defined for a sequence of two or more items starting with a non-node value".
/// </para>
/// <para>
/// The single-value case was worse, because it did not error: the EBV of a non-empty string is
/// true, so the FIRST branch matched whatever the operand was. The suite's existing coverage
/// used <c>test="'active'"</c> against <c>&lt;status&gt;active&lt;/status&gt;</c> and passed for
/// that reason — it would have returned "Active" for an inactive status just as happily. The
/// SelectsByValue_NotByFirstBranch case below is the one that distinguishes them.
/// </para>
/// <para>
/// Separately, <c>@select</c> on a branch was parsed but ignored, so
/// <c>&lt;xsl:when test="..." select="'bitmap'"/&gt;</c> matched and then produced nothing —
/// indistinguishable downstream from "the branch did not match".
/// </para>
/// </remarks>
public class XsltSwitchSemanticsTests
{
    private static async Task<string> RunAsync(string body, string source = "<data/>")
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" "
            + "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" version=\"4.0\" "
            + "exclude-result-prefixes=\"#all\">"
            + "<xsl:template match=\"/\"><r>" + body + "</r></xsl:template></xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        return await t.TransformAsync(source);
    }

    private const string Kinds =
        "<xsl:when test=\"('svg','SVG')\" select=\"'vector'\"/>"
        + "<xsl:when test=\"('jpg','JPG','jpeg','JPEG')\" select=\"'bitmap'\"/>"
        + "<xsl:otherwise select=\"'not supported'\"/>";

    /// <summary>A when listing several candidates matches any of them (previously FORG0006).</summary>
    [Theory]
    [InlineData("jpg", "bitmap")]
    [InlineData("JPEG", "bitmap")]
    [InlineData("SVG", "vector")]
    [InlineData("txt", "not supported")]
    public async Task MultiValueWhen_MatchesAnyCandidate(string operand, string expected)
    {
        var result = await RunAsync(
            "<xsl:switch select=\"'" + operand + "'\">" + Kinds + "</xsl:switch>");
        result.Should().Be("<r>" + expected + "</r>");
    }

    /// <summary>
    /// The defect the old coverage could not see: with a single-value when, the first branch
    /// used to win regardless of the operand.
    /// </summary>
    [Fact]
    public async Task SelectsByValue_NotByFirstBranch()
    {
        var result = await RunAsync(
            "<xsl:switch select=\"/data/status\">"
            + "<xsl:when test=\"'active'\">Active</xsl:when>"
            + "<xsl:when test=\"'inactive'\">Inactive</xsl:when>"
            + "<xsl:otherwise>Unknown</xsl:otherwise></xsl:switch>",
            "<data><status>inactive</status></data>");
        result.Should().Be("<r>Inactive</r>");
    }

    /// <summary>No branch matches — xsl:otherwise runs.</summary>
    [Fact]
    public async Task NoMatchingBranch_FallsToOtherwise()
    {
        var result = await RunAsync(
            "<xsl:switch select=\"/data/status\">"
            + "<xsl:when test=\"'active'\">Active</xsl:when>"
            + "<xsl:otherwise>Unknown</xsl:otherwise></xsl:switch>",
            "<data><status>archived</status></data>");
        result.Should().Be("<r>Unknown</r>");
    }

    /// <summary>Comparison is by value, so numeric operands work.</summary>
    [Fact]
    public async Task NumericOperand_ComparesNumerically()
    {
        var result = await RunAsync(
            "<xsl:switch select=\"2\"><xsl:when test=\"(1,2,3)\">low</xsl:when>"
            + "<xsl:otherwise>high</xsl:otherwise></xsl:switch>");
        result.Should().Be("<r>low</r>");
    }

    /// <summary>@select on a branch supplies its body.</summary>
    [Fact]
    public async Task BranchSelectAttribute_SuppliesTheBody()
    {
        var result = await RunAsync(
            "<xsl:switch select=\"'svg'\">" + Kinds + "</xsl:switch>");
        result.Should().Be("<r>vector</r>");
    }

    /// <summary>A branch with both @select and content is ambiguous and must be rejected.</summary>
    [Fact]
    public async Task BranchWithBothSelectAndContent_IsRejected()
    {
        var ex = await Record.ExceptionAsync(async () => await RunAsync(
            "<xsl:switch select=\"'a'\">"
            + "<xsl:when test=\"'a'\" select=\"'x'\">also content</xsl:when>"
            + "<xsl:otherwise>o</xsl:otherwise></xsl:switch>"));
        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("XTSE0010");
    }
}
