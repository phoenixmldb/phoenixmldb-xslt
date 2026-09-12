using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// An accumulator read against the context node at the document level needs the value AFTER the
/// whole tree is consumed, so such a body runs over the whole input. That was detected for a
/// select but not for an attribute value template: an accumulator call navigates nothing, and the
/// AVT check only looked for input navigation. So the same call folded to the accumulator's
/// initial value in <c>&lt;result count="{accumulator-after('count')}"/&gt;</c> while working in
/// an xsl:value-of — silently, with a plausible number.
/// </summary>
public sealed class StreamingAvtAccumulatorTests
{
    private const string Source = """<doc><t amount="10"/><t amount="30"/><t amount="2"/></doc>""";

    private static async Task<string> RunAsync(string body, bool streamed)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="xml" omit-xml-declaration="yes"/>
              <xsl:accumulator name="count" as="xs:integer" initial-value="0" streamable="yes">
                <xsl:accumulator-rule match="t" select="$value + 1"/>
              </xsl:accumulator>
              <xsl:accumulator name="total" as="xs:double" initial-value="0" streamable="yes">
                <xsl:accumulator-rule match="t" select="$value + @amount"/>
              </xsl:accumulator>
              <xsl:mode streamable="yes" on-no-match="deep-skip" use-accumulators="count total"/>
              <xsl:template match="/">{body}</xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (streamed ? await t.TransformAsync(new StringReader(Source)) : await t.TransformAsync(Source)).Trim();
    }

    private const string AvtBody = """<result count="{accumulator-after('count')}" total="{accumulator-after('total')}"/>""";
    private const string ValueOfBody = """<result><xsl:value-of select="accumulator-after('count')"/>|<xsl:value-of select="accumulator-after('total')"/></result>""";

    [Fact]
    public async Task AnAccumulatorInAnAvt_SeesTheWholeStream()
        => (await RunAsync(AvtBody, streamed: true)).Should().Be("""<result count="3" total="42"/>""",
            "the streamed run reported the accumulators' initial values");

    /// <summary>
    /// Unstreamed, the same stylesheet answered the initial values too — the mode is streamable
    /// either way, so a test that only compared the two runs would have passed while both were
    /// wrong.
    /// </summary>
    [Fact]
    public async Task AnAccumulatorInAnAvt_SeesTheWholeTree_Unstreamed()
        => (await RunAsync(AvtBody, streamed: false)).Should().Be("""<result count="3" total="42"/>""");

    /// <summary>The same calls in an xsl:value-of were already routed correctly.</summary>
    [Fact]
    public async Task AnAccumulatorInAValueOf_IsUnchanged()
        => (await RunAsync(ValueOfBody, streamed: true)).Should().Be("<result>3|42</result>");
}
