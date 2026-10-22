using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// A rule that fails inside a node makes the accumulator's value after that node, and after each
/// of its ancestors, the failure: accumulator-after on them raises the error. The after-value of
/// an ancestor kept the value from before its descendants, so the failure was lost and
/// accumulator-after gave a wrong number (W3C accumulator-031, where the failure is XTDE0640).
/// </summary>
public sealed class AccumulatorErrorAfterValueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pxaccerr-" + Guid.NewGuid().ToString("N"));

    public AccumulatorErrorAfterValueTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> RunAsync(string streamable, string read, string failOn)
    {
        var source = Path.Combine(_dir, "in.xml");
        await File.WriteAllTextAsync(source, "<doc><a/><b/></doc>");
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($"""
            <xsl:stylesheet xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" version="3.0">
              <xsl:accumulator name="n" as="xs:integer" initial-value="0" streamable="{streamable}">
                <xsl:accumulator-rule match="*" select="if (local-name() = '{failOn}') then error(xs:QName('FAILED')) else $value + 1"/>
              </xsl:accumulator>
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">
                <xsl:source-document streamable="{streamable}" href="{new Uri(source)}" use-accumulators="#all">
                  <xsl:for-each select="*"/>
                  <xsl:value-of select="{read}"/>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """);
        return await t.TransformAsync((string?)null);
    }

    [Theory]
    [InlineData("no", "accumulator-after('n')")]
    [InlineData("yes", "accumulator-after('n')")]
    [InlineData("no", "doc/accumulator-after('n')")]
    public async Task The_value_after_an_ancestor_of_a_failing_node_is_the_failure(string streamable, string read)
    {
        var act = () => RunAsync(streamable, read, failOn: "a");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("FAILED");
    }

    [Theory]
    [InlineData("no")]
    [InlineData("yes")]
    public async Task With_no_failure_the_value_counts_every_element(string streamable)
        => (await RunAsync(streamable, "accumulator-after('n')", failOn: "none")).Should().Be("3");
}
