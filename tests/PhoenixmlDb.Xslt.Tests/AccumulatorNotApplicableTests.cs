using FluentAssertions;
using PhoenixmlDb.Xslt.Engine;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// Reading a declared accumulator on a tree it does not apply to — a document loaded with
/// use-accumulators that leaves it out — is XTDE3362. It was reported as XTDE3340, the code for a
/// name that is not an accumulator (W3C merge-067, non-stream-201).
/// </summary>
public sealed class AccumulatorNotApplicableTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"phoenixml-acc-{Guid.NewGuid():N}.xml");

    public AccumulatorNotApplicableTests() => File.WriteAllText(_file, "<r><x/><x/></r>");

    public void Dispose() => File.Delete(_file);

    private async Task<string> RunAsync(string function, string accumulator)
    {
        var ss = $"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
              <xsl:output method="text"/>
              <xsl:accumulator name="a" initial-value="0"><xsl:accumulator-rule match="x" select="$value + 1"/></xsl:accumulator>
              <xsl:accumulator name="b" initial-value="0"><xsl:accumulator-rule match="x" select="$value + 1"/></xsl:accumulator>
              <xsl:template match="/">
                <xsl:source-document href="{new Uri(_file).AbsoluteUri}" use-accumulators="a">
                  <xsl:value-of select="r/x[2]/{function}('{accumulator}')"/>
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """;
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync(ss);
        return (await t.TransformAsync("<doc/>")).Trim();
    }

    [Theory]
    [InlineData("accumulator-before", "2")]
    [InlineData("accumulator-after", "2")]
    public async Task AnApplicableAccumulator_IsRead(string function, string expected)
        => (await RunAsync(function, "a")).Should().Be(expected);

    [Theory]
    [InlineData("accumulator-before")]
    [InlineData("accumulator-after")]
    public async Task ADeclaredAccumulator_LeftOutOfUseAccumulators_IsXTDE3362(string function)
    {
        var act = () => RunAsync(function, "b");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE3362");
    }

    [Theory]
    [InlineData("accumulator-before")]
    [InlineData("accumulator-after")]
    public async Task AnUndeclaredName_IsStillXTDE3340(string function)
    {
        var act = () => RunAsync(function, "nope");
        (await act.Should().ThrowAsync<Exception>()).Which.Message.Should().Contain("XTDE3340");
    }
}
