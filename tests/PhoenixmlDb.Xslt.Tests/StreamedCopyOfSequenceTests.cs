using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// In a streamed xsl:source-document, <c>copy-of((streamed-path, $other))</c> and
/// <c>snapshot((…))</c> copy each item: the streamed nodes come back as nodes, in order. The
/// streaming analysis knew a copy of one streamed path but not a copy of a sequence, so streamed
/// attributes came back as untypedAtomic values and streamed elements were lost (W3C
/// sf-copy-of-004, sf-snapshot-0304). Expected values are Saxon-HE 12.10's.
/// </summary>
/// <remarks>
/// The variable is used once, directly: used twice, it was buffered and the defect did not show.
/// </remarks>
public sealed class StreamedCopyOfSequenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "phx-cpyseq-" + Guid.NewGuid().ToString("N"));

    public StreamedCopyOfSequenceTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "t.xml"),
            "<account><t v='-1'/><t v='2'/><t v='-3'/></account>");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<string> Run(string variable, string use)
    {
        var t = new XsltTransformer();
        await t.LoadStylesheetAsync($$"""
            <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:fn="http://www.w3.org/2005/xpath-functions">
              <xsl:output method="text"/>
              <xsl:template name="xsl:initial-template">
                <xsl:variable name="extra" as="element()*"><x v="10"/><x v="11"/></xsl:variable>
                <xsl:source-document streamable="yes" href="t.xml">
                  {{variable}}
                  {{use}}
                </xsl:source-document>
              </xsl:template>
            </xsl:stylesheet>
            """, new Uri(Path.Combine(_dir, "main.xsl")));
        t.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
        return (await t.TransformAsync((string?)null)).Trim();
    }

    private const string Kinds =
        "<xsl:value-of select=\"$c ! (if (. instance of attribute()) then 'A' else if (. instance of element()) then 'E' else 'X')\"/>";

    [Theory]
    [InlineData("copy-of((account/t[@v &lt; 0]/@v, $extra/@v))", "A A A A")]
    [InlineData("snapshot((account/t[@v &lt; 0]/@v, $extra/@v))", "A A A A")]
    [InlineData("fn:copy-of((account/t[@v &lt; 0]/@v, $extra/@v))", "A A A A")]
    [InlineData("copy-of((account/t[@v &lt; 0], $extra))", "E E E E")]
    public async Task Each_item_is_copied_as_a_node(string select, string expected)
        => (await Run($"<xsl:variable name=\"c\" select=\"{select}\"/>", Kinds)).Should().Be(expected);

    [Fact]
    public async Task The_copies_keep_their_order_and_values()
        => (await Run("<xsl:variable name=\"c\" as=\"attribute()*\" select=\"copy-of((account/t[@v &lt; 0]/@v, $extra/@v))\"/>",
                "<xsl:value-of select=\"data($c)\"/>"))
            .Should().Be("-1 -3 10 11");
}
