using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Under a streamable mode, accumulator-after() answers for the node being processed, whose subtree
/// the pass has just finished. Read off any OTHER node it cannot be answered — the parent's
/// post-descent value is not known until the parent ends, which is later than now — so XSLT 3.0
/// makes it a static error (W3C accumulator-060).
/// </summary>
public sealed class AccumulatorAfterForeignNodeTests
{
    private const string Source = """<doc><chap><fig alt="a"/></chap></doc>""";

    private static async Task<Exception?> RunAsync(string templateBody)
    {
        var ss = $$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"
              xmlns:xs="http://www.w3.org/2001/XMLSchema" exclude-result-prefixes="#all">
              <xsl:output method="text"/>
              <xsl:accumulator name="figNr" as="xs:integer" initial-value="0" streamable="yes">
                <xsl:accumulator-rule match="fig" select="$value + 1"/>
              </xsl:accumulator>
              <xsl:mode streamable="yes" on-no-match="shallow-skip" use-accumulators="figNr"/>
              <xsl:template match="fig">{{templateBody}}</xsl:template>
            </xsl:stylesheet>
            """;
        try
        {
            var t = new XsltTransformer();
            await t.LoadStylesheetAsync(ss);
            await t.TransformAsync(new StringReader(Source));
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact(Timeout = 30000)]
    public async Task AccumulatorAfterOnTheContextNode_IsStreamable()
        => (await RunAsync("""<xsl:value-of select="accumulator-after('figNr')"/>"""))
            .Should().BeNull("the pass has just finished this node's subtree");

    [Fact(Timeout = 30000)]
    public async Task AccumulatorAfterOnTheParent_IsRejected()
    {
        var ex = await RunAsync("""<xsl:value-of select="../accumulator-after('figNr')"/>""");
        ex.Should().NotBeNull();
        ex!.Message.Should().Contain("XTSE3430");
    }
}
