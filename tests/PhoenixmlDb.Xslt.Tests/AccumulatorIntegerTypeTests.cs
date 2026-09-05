using FluentAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An accumulator declared <c>as="xs:integer"</c> must accept every representation of an
/// xs:integer, including the BigInteger one.
/// </summary>
/// <remarks>
/// <para>
/// xs:integer is UNBOUNDED in XSD, so a value too wide for <c>long</c> is carried as a
/// BigInteger — and both <c>xs:integer()</c> and the overflow-safe arithmetic can produce one.
/// The accumulator's type matcher accepted only <c>int</c> or <c>long</c>, so a rule whose value
/// came through <c>xs:integer()</c> failed its type check on EVERY node.
/// </para>
/// <para>
/// The symptom was silence, not an error. An accumulator type failure becomes a deferred error
/// (correct per XSLT 3.0 §18.2 — accumulator errors are deferred), which freezes the accumulator,
/// so it reported its initial value forever. Two factors were needed and either alone looked
/// fine: <c>as="xs:integer"</c> with <c>number()</c> worked, and an untyped accumulator with
/// <c>xs:integer()</c> worked, which is what made it hard to see.
/// </para>
/// <para>
/// The XQuery engine's own MatchesItemType had accepted BigInteger for ItemType.Integer all
/// along; this separate matcher had not. Found by clustering the W3C decl group, where
/// accumulator was the largest set of failures.
/// </para>
/// </remarks>
public class AccumulatorIntegerTypeTests
{
    private const string Source = "<r><c>10</c><c>20</c><c>61</c></r>";

    private static async Task<string> RunAsync(string accumulators, string report)
    {
        var xsl =
            "<xsl:stylesheet xmlns:xsl=\"http://www.w3.org/1999/XSL/Transform\" "
            + "xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" version=\"3.0\" "
            + "exclude-result-prefixes=\"#all\">"
            + "<xsl:mode use-accumulators=\"#all\"/>"
            + accumulators
            + "<xsl:template match=\"/\"><out>" + report + "</out></xsl:template>"
            + "</xsl:stylesheet>";
        var t = new PhoenixmlDb.Xslt.XsltTransformer();
        await t.LoadStylesheetAsync(xsl);
        return await t.TransformAsync(Source);
    }

    /// <summary>
    /// The defect: as="xs:integer" plus xs:integer() in the rule reported the initial value.
    /// </summary>
    [Fact]
    public async Task TypedIntegerAccumulator_WithXsIntegerConstructor_Accumulates()
    {
        var result = await RunAsync(
            "<xsl:accumulator name=\"a\" as=\"xs:integer\" initial-value=\"0\">"
            + "<xsl:accumulator-rule match=\"c\" select=\"$value + xs:integer(.)\"/>"
            + "</xsl:accumulator>",
            "<xsl:value-of select=\"accumulator-after('a')\"/>");
        result.Should().Be("<out>91</out>");
    }

    /// <summary>The same rule expressed as a sequence-constructor body.</summary>
    [Fact]
    public async Task TypedIntegerAccumulator_WithRuleBody_Accumulates()
    {
        var result = await RunAsync(
            "<xsl:accumulator name=\"a\" as=\"xs:integer\" initial-value=\"0\">"
            + "<xsl:accumulator-rule match=\"c\">"
            + "<xsl:sequence select=\"$value + xs:integer(.)\"/>"
            + "</xsl:accumulator-rule></xsl:accumulator>",
            "<xsl:value-of select=\"accumulator-after('a')\"/>");
        result.Should().Be("<out>91</out>");
    }

    /// <summary>Matching the text node rather than the element works the same way.</summary>
    [Fact]
    public async Task TypedIntegerAccumulator_MatchingTextNodes_Accumulates()
    {
        var result = await RunAsync(
            "<xsl:accumulator name=\"a\" as=\"xs:integer\" initial-value=\"0\">"
            + "<xsl:accumulator-rule match=\"c/text()\" select=\"$value + xs:integer(.)\"/>"
            + "</xsl:accumulator>",
            "<xsl:value-of select=\"accumulator-after('a')\"/>");
        result.Should().Be("<out>91</out>");
    }

    /// <summary>The two forms that already worked must keep working.</summary>
    [Theory]
    [InlineData("as=\"xs:integer\" ", "$value + number(.)")]
    [InlineData("", "$value + xs:integer(.)")]
    [InlineData("as=\"xs:integer\" ", "$value + 1")]
    public async Task FormsThatAlreadyWorked_StillWork(string asAttr, string expr)
    {
        var expected = expr.Contains("+ 1", StringComparison.Ordinal) ? "3" : "91";
        var result = await RunAsync(
            "<xsl:accumulator name=\"a\" " + asAttr + "initial-value=\"0\">"
            + "<xsl:accumulator-rule match=\"c\" select=\"" + expr + "\"/>"
            + "</xsl:accumulator>",
            "<xsl:value-of select=\"accumulator-after('a')\"/>");
        result.Should().Be("<out>" + expected + "</out>");
    }

    /// <summary>
    /// A value genuinely wider than long must survive too — that is why BigInteger exists here.
    /// </summary>
    [Fact]
    public async Task TypedIntegerAccumulator_CarriesValuesWiderThanLong()
    {
        var result = await RunAsync(
            "<xsl:accumulator name=\"a\" as=\"xs:integer\" initial-value=\"0\">"
            + "<xsl:accumulator-rule match=\"c\" "
            + "select=\"$value + xs:integer('9223372036854775807')\"/>"
            + "</xsl:accumulator>",
            "<xsl:value-of select=\"accumulator-after('a')\"/>");
        // 3 × long.MaxValue — only representable as a BigInteger.
        result.Should().Be("<out>27670116110564327421</out>");
    }
}
