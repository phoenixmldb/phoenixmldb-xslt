using AwesomeAssertions;
using Xunit;

namespace PhoenixmlDb.Xslt.Tests;

/// <summary>
/// deep-equal and index-of in a streamable xsl:source-document, with an operand that reads the
/// streamed input. The scanner has no watcher for them, and only a first argument was checked, so
/// the operand was evaluated against the empty document: index-of($seq, /a/b) found nothing (W3C
/// sf-index-of-002) and deep-equal((/a/b, 31), (1, 31)) compared the literals alone (W3C
/// sf-deep-equal-012). Each result must equal the unstreamed one.
/// </summary>
public class StreamedComparisonOperandTests
{
    private const string InputXml = "<a><b>1</b><b>2</b><c>k</c></a>";

    private static async Task<string> RunAsync(string select, bool streamable)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"streamed-comparison-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        await File.WriteAllTextAsync(Path.Combine(tempDir, "in.xml"), InputXml);
        try
        {
            var transformer = new XsltTransformer();
            await transformer.LoadStylesheetAsync($$"""
                <xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:output method="text"/>
                  <xsl:template name="xsl:initial-template">
                    <xsl:variable name="keys" as="element()*"><k>x</k><k>k</k></xsl:variable>
                    <xsl:source-document streamable="{{(streamable ? "yes" : "no")}}" href="in.xml">
                      <xsl:value-of select="{{select}}"/>
                    </xsl:source-document>
                  </xsl:template>
                </xsl:stylesheet>
                """, new Uri(tempDir + "/"));
            transformer.SetInitialTemplate("initial-template", "http://www.w3.org/1999/XSL/Transform");
            return await transformer.TransformAsync((string?)null);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("index-of($keys, /a/c)", "2")]
    [InlineData("index-of(('x', '1', 'y', '1'), /a/b[1])", "2 4")]
    [InlineData("deep-equal((/a/b/number(), 3), (1, 2, 3))", "true")]
    [InlineData("deep-equal((1, 2), /a/b/number())", "true")]
    public async Task A_streamed_operand_gives_the_unstreamed_result(string select, string expected)
    {
        (await RunAsync(select, streamable: false)).Should().Be(expected);
        (await RunAsync(select, streamable: true)).Should().Be(expected);
    }
}
